locals {
  workload_resource_groups = {
    for location in [var.location] :
    location => data.terraform_remote_state.platform_workloads.outputs.workload_resource_groups[var.workload_name][var.environment].resource_groups[lower(location)]
  }

  workload_resource_group = local.workload_resource_groups[var.location]

  action_group_map = {
    critical      = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.critical
    high          = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.high
    moderate      = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.moderate
    low           = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.low
    informational = data.terraform_remote_state.platform_monitoring.outputs.monitor_action_groups.informational
  }

  app_configuration_endpoint = data.terraform_remote_state.portal_environments.outputs.app_configuration.endpoint

  managed_identities    = data.terraform_remote_state.portal_environments.outputs.managed_identities
  server_agent_identity = local.managed_identities["server_agent"]

  app_insights = data.terraform_remote_state.portal_core.outputs.app_insights
  servicebus   = data.terraform_remote_state.portal_core.outputs.servicebus_namespace

  acr = data.terraform_remote_state.platform_registry.outputs.acr

  container_app_environment_name = "cae-srvagent-${var.environment}-${random_id.environment_id.hex}"
  container_app_name             = "ca-srvagent-${var.environment}-${random_id.environment_id.hex}"
}
