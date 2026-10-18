terraform {
  required_version = ">= 1.5"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.2"
    }
  }
}

provider "azurerm" {
  features {
    resource_group {
      # The demo owns everything in its resource group, so "terraform destroy" may remove it all.
      prevent_deletion_if_contains_resources = false
    }
  }
}
