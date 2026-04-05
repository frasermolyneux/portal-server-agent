resource "azurerm_container_app_environment" "env" {
  name = local.container_app_environment_name

  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  log_analytics_workspace_id = data.azurerm_application_insights.app_insights.workspace_id

  tags = var.tags
}
