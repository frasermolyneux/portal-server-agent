resource "azurerm_container_app" "app" {
  name = local.container_app_name

  resource_group_name          = data.azurerm_resource_group.rg.name
  container_app_environment_id = azurerm_container_app_environment.env.id
  revision_mode                = "Single"

  identity {
    type         = "UserAssigned"
    identity_ids = [local.server_agent_identity.id]
  }

  registry {
    server   = local.acr.login_server
    identity = local.server_agent_identity.id
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "server-agent"
      image  = "mcr.microsoft.com/dotnet/runtime:9.0"
      cpu    = 0.25
      memory = "0.5Gi"

      env {
        name  = "AzureAppConfiguration__Endpoint"
        value = local.app_configuration_endpoint
      }

      env {
        name  = "AzureAppConfiguration__ManagedIdentityClientId"
        value = local.server_agent_identity.client_id
      }

      env {
        name  = "AzureAppConfiguration__Environment"
        value = var.environment
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = local.server_agent_identity.client_id
      }

      env {
        name  = "ServiceBusConnection__fullyQualifiedNamespace"
        value = local.servicebus.fqdn
      }

      env {
        name  = "ServiceBusConnection__ManagedIdentityClientId"
        value = local.server_agent_identity.client_id
      }

      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = data.azurerm_application_insights.app_insights.connection_string
      }

      env {
        name  = "AgentStorage__BlobEndpoint"
        value = azurerm_storage_account.agent_storage.primary_blob_endpoint
      }

      env {
        name  = "BanFileStorage__BlobEndpoint"
        value = local.ban_files_storage.blob_endpoint
      }

      env {
        name  = "BanFileStorage__ContainerName"
        value = local.ban_files_storage.container_name
      }

      env {
        name  = "ASPNETCORE_HTTP_PORTS"
        value = "8080"
      }

      liveness_probe {
        transport = "HTTP"
        path      = "/healthz"
        port      = 8080
      }

      startup_probe {
        transport = "HTTP"
        path      = "/healthz"
        port      = 8080
      }
    }
  }

  ingress {
    target_port      = 8080
    external_enabled = false
    transport        = "http"

    traffic_weight {
      percentage      = 100
      latest_revision = true
    }
  }

  tags = var.tags

  lifecycle {
    ignore_changes = [
      template[0].container[0].image
    ]
  }
}
