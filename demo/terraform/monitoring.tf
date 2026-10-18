# Logs and the SmartTableSync metrics (rows synced, errors, duration, lag)

resource "azurerm_log_analytics_workspace" "demo" {
  name                = "log-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
}

resource "azurerm_application_insights" "demo" {
  name                = "appi-smarttablesync-${var.suffix}"
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
  workspace_id        = azurerm_log_analytics_workspace.demo.id
  application_type    = "web"
}
