# SQL server with Microsoft Entra authentication only (no SQL logins or passwords), and the
# two databases SmartTableSync syncs between. Both are serverless and pause when idle.

resource "azurerm_mssql_server" "demo" {
  name                = "sql-smarttablesync-${var.suffix}"
  resource_group_name = azurerm_resource_group.demo.name
  location            = azurerm_resource_group.demo.location
  version             = "12.0"
  minimum_tls_version = "1.2"

  azuread_administrator {
    login_username              = var.sql_admin_login
    object_id                   = data.azurerm_client_config.current.object_id
    azuread_authentication_only = true
  }
}

# Flex Consumption has no fixed outbound IPs without a virtual network, so the Function
# reaches SQL through the "Allow Azure services" rule. A production setup would use VNet
# integration and a private endpoint instead.
resource "azurerm_mssql_firewall_rule" "allow_azure_services" {
  name             = "AllowAzureServices"
  server_id        = azurerm_mssql_server.demo.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# Lets the machine running the demo seed the parent and apply the scaffolded SQL
resource "azurerm_mssql_firewall_rule" "allow_client" {
  name             = "AllowDemoClient"
  server_id        = azurerm_mssql_server.demo.id
  start_ip_address = var.client_ip
  end_ip_address   = var.client_ip
}

# Source of the data: the demo tables and seed rows live here
resource "azurerm_mssql_database" "parent" {
  name                        = "sqldb-parent"
  server_id                   = azurerm_mssql_server.demo.id
  sku_name                    = "GP_S_Gen5_1"
  min_capacity                = 0.5
  auto_pause_delay_in_minutes = 60
  max_size_gb                 = 5
  storage_account_type        = "Local"
  zone_redundant              = false
}

# Target of the sync: SmartTableSync keeps its copies and its tracking tables here
resource "azurerm_mssql_database" "child" {
  name                        = "sqldb-child"
  server_id                   = azurerm_mssql_server.demo.id
  sku_name                    = "GP_S_Gen5_1"
  min_capacity                = 0.5
  auto_pause_delay_in_minutes = 60
  max_size_gb                 = 5
  storage_account_type        = "Local"
  zone_redundant              = false
}
