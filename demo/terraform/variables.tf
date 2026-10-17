# Input values. run-demo.ps1 passes them on the command line; to run Terraform by hand, copy
# terraform.tfvars.example to terraform.tfvars (git-ignored) and fill it in.

variable "suffix" {
  type        = string
  description = "Short unique suffix for globally unique names (storage account, SQL server, function), e.g. your initials"

  validation {
    condition     = can(regex("^[a-z0-9]{3,8}$", var.suffix))
    error_message = "Use 3-8 lowercase letters or digits."
  }
}

variable "location" {
  type        = string
  description = "Azure region for all resources"
  default     = "swedencentral"
}

variable "sql_admin_login" {
  type        = string
  description = "Your Microsoft Entra sign-in name (e.g. you@contoso.com). Becomes the SQL server's Entra admin; Terraform takes the object ID from the signed-in identity."
}

variable "client_ip" {
  type        = string
  description = "Public IP of the machine running the demo, allowed through the SQL firewall"
}

variable "runtime_identity_name" {
  type        = string
  description = "Name of the managed identity the Function uses for SQL. The repo's grant scripts use smarttablesync-runtime; Azure SQL resolves the database user by this name, so it must be unique in the tenant."
  default     = "smarttablesync-runtime"
}

variable "table_list" {
  type        = string
  description = "Comma-separated tables the Function syncs (SmartTableSync:TableList)"
  default     = "Product,Customer,Document"
}
