# Resource group that holds every resource of the SmartTableSync demo.
# The other resources are split by area:
#   identity.tf    managed identity the Function uses for SQL
#   databases.tf   SQL server (Entra-only) with the parent and child databases
#   functions.tf   Flex Consumption Function App and its storage
#   monitoring.tf  Log Analytics, Application Insights

resource "azurerm_resource_group" "demo" {
  name     = "rg-smarttablesync-demo-${var.suffix}"
  location = var.location
}
