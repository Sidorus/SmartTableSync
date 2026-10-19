# Everything SmartTableSync needs in Azure to sync two databases that already exist:
# two managed identities, the Function App with its storage, and monitoring.
# Nothing here touches the databases; deploy.ps1 creates their users and grants.

resource "azurerm_resource_group" "quickstart" {
  name     = "rg-smarttablesync-qs-${var.suffix}"
  location = var.location
}

# --- Identities ---------------------------------------------------------------------------
# User-assigned, so they keep their client IDs when the Function is redeployed; deploy.ps1
# creates the database users from those IDs. Database user names are fixed
# (smarttablesync-runtime, smarttablesync-child-provisioning), whatever these are called.

# Reads the parent and writes data into the child. Never has DDL rights.
resource "azurerm_user_assigned_identity" "runtime" {
  name                = "id-smarttablesync-runtime-${var.suffix}"
  location            = azurerm_resource_group.quickstart.location
  resource_group_name = azurerm_resource_group.quickstart.name
}

# Auto mode only: creates and changes the child's tables, table types and procedures.
resource "azurerm_user_assigned_identity" "provisioning" {
  count               = var.mode == "Auto" ? 1 : 0
  name                = "id-smarttablesync-provisioning-${var.suffix}"
  location            = azurerm_resource_group.quickstart.location
  resource_group_name = azurerm_resource_group.quickstart.name
}

locals {
  # Managed Identity sign-in for SqlClient; User Id picks which of the Function's identities.
  runtime_parent = join(";", [
    "Server=tcp:${var.parent_server},1433",
    "Database=${var.parent_database}",
    "Authentication=Active Directory Managed Identity",
    "User Id=${azurerm_user_assigned_identity.runtime.client_id}",
    "Encrypt=True",
  ])
  runtime_child = join(";", [
    "Server=tcp:${var.child_server},1433",
    "Database=${var.child_database}",
    "Authentication=Active Directory Managed Identity",
    "User Id=${azurerm_user_assigned_identity.runtime.client_id}",
    "Encrypt=True",
  ])
  provisioning_child = var.mode == "Auto" ? join(";", [
    "Server=tcp:${var.child_server},1433",
    "Database=${var.child_database}",
    "Authentication=Active Directory Managed Identity",
    "User Id=${azurerm_user_assigned_identity.provisioning[0].client_id}",
    "Encrypt=True",
  ]) : null

  # Linux app settings can't contain ":", so "SmartTableSync:TableList" is written with "__".
  sync_settings = {
    ParentDbConnectionString  = local.runtime_parent
    ChildDbConnectionString   = local.runtime_child
    SmartTableSync__TableList = var.table_list
    SmartTableSync__Mode      = var.mode
  }
  provisioning_settings = var.mode == "Auto" ? {
    ChildProvisioningConnectionString = local.provisioning_child
  } : {}
}

# --- Monitoring ---------------------------------------------------------------------------

resource "azurerm_log_analytics_workspace" "quickstart" {
  name                = "log-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.quickstart.location
  resource_group_name = azurerm_resource_group.quickstart.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "quickstart" {
  name                = "appi-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.quickstart.location
  resource_group_name = azurerm_resource_group.quickstart.name
  workspace_id        = azurerm_log_analytics_workspace.quickstart.id
  application_type    = "web"
}

# --- Function App -------------------------------------------------------------------------

# Storage the Functions runtime needs (timer leases, keys, the deployment package)
resource "azurerm_storage_account" "function" {
  # Different from the demo's names, so both can run with the same suffix
  name                     = "stsmtsync${var.suffix}"
  resource_group_name      = azurerm_resource_group.quickstart.name
  location                 = azurerm_resource_group.quickstart.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  min_tls_version          = "TLS1_2"
}

resource "azurerm_storage_container" "deployment" {
  name                  = "deploymentpackage"
  storage_account_id    = azurerm_storage_account.function.id
  container_access_type = "private"
}

resource "azurerm_service_plan" "function" {
  name                = "plan-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.quickstart.location
  resource_group_name = azurerm_resource_group.quickstart.name
  os_type             = "Linux"
  sku_name            = "FC1"
}

resource "azurerm_function_app_flex_consumption" "sync" {
  name                = "func-smarttablesync-qs-${var.suffix}"
  location            = azurerm_resource_group.quickstart.location
  resource_group_name = azurerm_resource_group.quickstart.name
  service_plan_id     = azurerm_service_plan.function.id

  # The runtime's own storage uses the account key: with "SystemAssignedIdentity", azurerm 4.81
  # writes an AzureWebJobsStorage connection string with an empty AccountKey and the app starts
  # unhealthy. The SQL connections are not affected: they use managed identities, no password.
  storage_container_type      = "blobContainer"
  storage_container_endpoint  = "${azurerm_storage_account.function.primary_blob_endpoint}${azurerm_storage_container.deployment.name}"
  storage_authentication_type = "StorageAccountConnectionString"
  storage_access_key          = azurerm_storage_account.function.primary_access_key

  runtime_name           = "dotnet-isolated"
  runtime_version        = "10.0"
  maximum_instance_count = 40
  instance_memory_in_mb  = 2048

  identity {
    type = "UserAssigned"
    identity_ids = concat(
      [azurerm_user_assigned_identity.runtime.id],
      azurerm_user_assigned_identity.provisioning[*].id,
    )
  }

  app_settings = merge(local.sync_settings, local.provisioning_settings)

  site_config {
    application_insights_connection_string = azurerm_application_insights.quickstart.connection_string
  }
}
