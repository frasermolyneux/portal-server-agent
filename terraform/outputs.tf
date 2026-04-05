output "container_app_name" {
  value = azurerm_container_app.app.name
}

output "resource_group_name" {
  value = data.azurerm_resource_group.rg.name
}

output "container_app_environment_name" {
  value = azurerm_container_app_environment.env.name
}

output "acr_login_server" {
  value = local.acr.login_server
}

output "storage_blob_endpoint" {
  value = azurerm_storage_account.agent_storage.primary_blob_endpoint
}
