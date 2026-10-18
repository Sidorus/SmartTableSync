# User-assigned identity the Function uses for SQL (the "runtime" identity in
# sql/07_permissions.sql). A user-assigned identity keeps a fixed name, so the database
# users created from it survive a redeployed Function, and the repo's grant scripts can refer
# to it as smarttablesync-runtime.

resource "azurerm_user_assigned_identity" "runtime" {
  name                = var.runtime_identity_name
  location            = azurerm_resource_group.demo.location
  resource_group_name = azurerm_resource_group.demo.name
}
