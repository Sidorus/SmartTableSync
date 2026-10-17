# Who Terraform is running as (tenant, subscription, user/app object ID)
data "azurerm_client_config" "current" {}
