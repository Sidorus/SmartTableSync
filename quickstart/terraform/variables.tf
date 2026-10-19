# Input values. deploy.ps1 passes them on the command line.

variable "suffix" {
  type        = string
  description = "Short unique suffix for globally unique names (storage account, function), e.g. your initials"

  validation {
    condition     = can(regex("^[a-z0-9]{3,8}$", var.suffix))
    error_message = "Use 3-8 lowercase letters or digits."
  }
}

variable "location" {
  type        = string
  description = "Azure region for the Function and its resources. Best the same region as the databases."
  default     = "swedencentral"
}

variable "parent_server" {
  type        = string
  description = "Parent SQL server host name, e.g. myserver.database.windows.net"
}

variable "parent_database" {
  type        = string
  description = "Parent database name"
}

variable "child_server" {
  type        = string
  description = "Child SQL server host name (may be the same server as the parent)"
}

variable "child_database" {
  type        = string
  description = "Child database name"
}

variable "table_list" {
  type        = string
  description = "Comma-separated tables to sync (SmartTableSync:TableList)"
}

variable "mode" {
  type        = string
  description = "SmartTableSync:Mode: Auto (the Function creates the child's objects) or Manual (deploy.ps1 applies the scaffold tool's scripts)"
  default     = "Auto"

  validation {
    condition     = contains(["Auto", "Manual"], var.mode)
    error_message = "Use Auto or Manual."
  }
}
