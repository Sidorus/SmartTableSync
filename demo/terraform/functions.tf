# The SmartTableSync Function (Flex Consumption) and the storage the Functions runtime needs

locals {
  # Managed Identity sign-in for SqlClient. User Id selects the user-assigned identity, since
  # the Function also has a system-assigned one (used only for its own storage).
  sql_connection_template = join(";", [
    "Server=tcp:${azurerm_mssql_server.demo.fully_qualified_domain_name},1433",
    "Database=%s",
    "Authentication=Active Directory Managed Identity",
    "User Id=${azurerm_user_assigned_identity.runtime.client_id}",
    "Encrypt=True",
  ])
}

# Storage account required by the Functions runtime (host state, timer leases, deployment)
resource "azurerm_storage_account" "function" {
  name                     = "stsmarttablesync${var.suffix}"
  resource_group_name      = azurerm_resource_group.demo.name
  location                 = azurerm_resource_group.demo.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

# Blob container where Flex Consumption keeps the function's deployment package
resource "azurerm_storage_container" "deployment" {
  name                  = "deploymentpackage"
  storage_account_id    = azurerm_storage_account.function.id
  container_access_type = "private"
}

resource "azurerm_service_plan" "function" {
  name                = "plan-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  os_type             = "Linux"
  sku_name            = "FC1"
}

resource "azurerm_function_app_flex_consumption" "sync" {
  name                = "func-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  service_plan_id     = azurerm_service_plan.function.id

  # The Functions runtime's own storage (timer leases, keys, the deployment package) uses the
  # account key, the setup this provider fully manages: with "SystemAssignedIdentity", azurerm
  # 4.81 writes an AzureWebJobsStorage connection string with an empty AccountKey, and the app
  # starts unhealthy ("Unable to access AzureWebJobsStorage"). The SQL connections are not
  # affected: they still use the runtime managed identity, with no password.
  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.function.primary_blob_endpoint}${azurerm_storage_container.deployment.name}"
  storage_authentication_type = "StorageAccountConnectionString"
  storage_access_key          = azurerm_storage_account.function.primary_access_key

  runtime_name           = "dotnet-isolated"
  runtime_version        = "10.0"
  maximum_instance_count = 40
  instance_memory_in_mb  = 2048

  identity {
    type         = "SystemAssigned, UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.runtime.id]
  }

  # Linux app settings can't contain ":", so "SmartTableSync:TableList" is written with "__",
  # which .NET configuration reads back as ":".
  app_settings = {
    ParentDbConnectionString  = format(local.sql_connection_template, azurerm_mssql_database.parent.name)
    ChildDbConnectionString   = format(local.sql_connection_template, azurerm_mssql_database.child.name)
    SmartTableSync__TableList = var.table_list
    SmartTableSync__Mode      = "Manual"
  }

  site_config {
    application_insights_connection_string = azurerm_application_insights.demo.connection_string
  }
}

# Lets the Function read and write its storage with its system-assigned identity (no access key)
resource "azurerm_role_assignment" "function_storage" {
  scope                = azurerm_storage_account.function.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = azurerm_function_app_flex_consumption.sync.identity[0].principal_id
}
