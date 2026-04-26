resource "azurerm_role_assignment" "app_to_storage" {
  scope                = azurerm_storage_account.agent_storage.id
  role_definition_name = "Storage Blob Data Owner"
  principal_id         = local.server_agent_identity.principal_id
}

resource "azurerm_role_assignment" "app_to_ban_files_storage" {
  scope                = local.ban_files_storage.id
  role_definition_name = "Storage Blob Data Reader"
  principal_id         = local.server_agent_identity.principal_id
  description          = "Allows the server agent runtime identity to read the central regenerated ban files (produced by portal-sync) and push them to game servers via FTP."
}
