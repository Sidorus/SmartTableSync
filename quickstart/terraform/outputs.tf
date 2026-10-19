# Values deploy.ps1 reads after apply; read them any time with: terraform output

output "resource_group_name" {
  value = azurerm_resource_group.quickstart.name
}

output "function_app_name" {
  description = "Name for \"func azure functionapp publish\""
  value       = azurerm_function_app_flex_consumption.sync.name
}

output "function_app_hostname" {
  value = azurerm_function_app_flex_consumption.sync.default_hostname
}

output "runtime_client_id" {
  description = "Client ID of the runtime identity; deploy.ps1 creates the smarttablesync-runtime database user from it"
  value       = one(azurerm_user_assigned_identity.runtime[*].client_id)
}

output "provisioning_client_id" {
  description = "Client ID of the provisioning identity (Auto mode); becomes the smarttablesync-child-provisioning database user"
  value       = one(azurerm_user_assigned_identity.provisioning[*].client_id)
}
