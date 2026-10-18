# Values run-demo.ps1 reads after apply; read them any time with: terraform output

output "resource_group_name" {
  value = azurerm_resource_group.demo.name
}

output "function_app_name" {
  description = "Name for \"func azure functionapp publish\""
  value       = azurerm_function_app_flex_consumption.sync.name
}

output "function_app_hostname" {
  value = azurerm_function_app_flex_consumption.sync.default_hostname
}

output "sql_server_fqdn" {
  description = "Server name for SSMS, sqlcmd and connection strings"
  value       = azurerm_mssql_server.demo.fully_qualified_domain_name
}

output "parent_database_name" {
  value = azurerm_mssql_database.parent.name
}

output "child_database_name" {
  value = azurerm_mssql_database.child.name
}

output "runtime_identity_name" {
  description = "Database user name to create with CREATE USER ... FROM EXTERNAL PROVIDER"
  value       = azurerm_user_assigned_identity.runtime.name
}
