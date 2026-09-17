variable "workload_name" {
  description = "Name of the workload as defined in platform-workloads state"
  type        = string
  default     = "portal-server-agent"
}

variable "environment" {
  description = "Deployment environment."
  type        = string
  default     = "dev"

  validation {
    condition     = contains(["dev", "prd"], var.environment)
    error_message = "environment must be either dev or prd."
  }
}

variable "location" {
  default = "uksouth"
}

variable "subscription_id" {}

variable "image_repository" {
  description = "Environment-specific ACR repository used for portal-server-agent images."
  type        = string

  validation {
    condition = (
      (var.environment == "dev" && var.image_repository == "portal-server-agent-dev") ||
      (var.environment == "prd" && var.image_repository == "portal-server-agent")
    )
    error_message = "image_repository must be portal-server-agent-dev for dev and portal-server-agent for prd."
  }
}

variable "platform_workloads_state" {
  description = "Backend config for platform-workloads remote state (used to read workload resource groups/backends)"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "platform_monitoring_state" {
  description = "Backend config for platform-monitoring remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "portal_environments_state" {
  description = "Backend config for portal-environments remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "portal_core_state" {
  description = "Backend config for portal-core remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "platform_registry_state" {
  description = "Backend config for platform-registry remote state"
  type = object({
    resource_group_name  = string
    storage_account_name = string
    container_name       = string
    key                  = string
    subscription_id      = string
    tenant_id            = string
  })
}

variable "tags" {
  default = {}
}
