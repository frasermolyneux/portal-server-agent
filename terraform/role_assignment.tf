resource "azurerm_role_assignment" "app_to_storage" {
  scope                = azurerm_storage_account.agent_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = local.server_agent_identity.principal_id
}
