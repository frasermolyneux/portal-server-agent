resource "azurerm_storage_account" "agent_storage" {
  name = "sa${random_id.environment_id.hex}"

  resource_group_name = data.azurerm_resource_group.rg.name
  location            = data.azurerm_resource_group.rg.location

  account_tier             = "Standard"
  account_replication_type = "LRS"
  account_kind             = "StorageV2"
  access_tier              = "Hot"

  https_traffic_only_enabled = true
  min_tls_version            = "TLS1_2"

  local_user_enabled        = false
  shared_access_key_enabled = false

  tags = var.tags
}

resource "azurerm_storage_container" "tailer_offsets" {
  name = "tailer-offsets"

  storage_account_id    = azurerm_storage_account.agent_storage.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "server_locks" {
  name = "server-locks"

  storage_account_id    = azurerm_storage_account.agent_storage.id
  container_access_type = "private"
}
