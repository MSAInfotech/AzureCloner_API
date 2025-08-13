using AzureDiscovery.Core.Models;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AzureDiscovery.Infrastructure.Services
{
    public class TerraformTemplateGenerator
    {
        private readonly ILogger _logger;

        public TerraformTemplateGenerator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> GenerateTemplateAsync(List<AzureResource> resources)
        {
            _logger.LogInformation("Generating Terraform template for {ResourceCount} resources", resources.Count);

            var sb = new StringBuilder();
            var usedNames = new HashSet<string>();

            // Generate variable declarations
            sb.AppendLine(GenerateVariableDeclarations(resources));
            sb.AppendLine();

            // Terraform provider block with authentication options
            sb.AppendLine("terraform {");
            sb.AppendLine("  required_providers {");
            sb.AppendLine("    azurerm = {");
            sb.AppendLine("      source  = \"hashicorp/azurerm\"");
            sb.AppendLine("      version = \">= 3.0.0\"");
            sb.AppendLine("    }");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();

            // Provider with flexible authentication
            sb.AppendLine("provider \"azurerm\" {");
            sb.AppendLine("  features {}");
            sb.AppendLine("  # Authentication will be handled by:");
            sb.AppendLine("  # 1. Environment variables (ARM_CLIENT_ID, ARM_CLIENT_SECRET, etc.)");
            sb.AppendLine("  # 2. Azure CLI (az login)");
            sb.AppendLine("  # 3. Managed Identity (when running on Azure)");
            sb.AppendLine("}");
            sb.AppendLine();

            // Add data sources that might be needed
            sb.AppendLine("data \"azurerm_client_config\" \"current\" {}");
            sb.AppendLine();

            // Group resources by type for ordering
            var sortedResources = resources.OrderBy(r => r.DependencyLevel).ToList();

            foreach (var resource in sortedResources)
            {
                var terraformResource = GenerateTerraformResource(resource, usedNames);
                sb.AppendLine(terraformResource);
                sb.AppendLine();
            }

            // Convert CRLF to LF for consistent line endings
            return sb.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
        }

        private string GenerateVariableDeclarations(List<AzureResource> resources)
        {
            var sb = new StringBuilder();
            var addedVariables = new HashSet<string>();

            // Common variables
            sb.AppendLine("variable \"location\" {");
            sb.AppendLine("  description = \"The Azure region where resources will be created\"");
            sb.AppendLine("  type        = string");
            sb.AppendLine($"  default     = \"{resources.FirstOrDefault()?.Location ?? "East US"}\"");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("variable \"resource_group_name\" {");
            sb.AppendLine("  description = \"The name of the resource group\"");
            sb.AppendLine("  type        = string");
            sb.AppendLine($"  default     = \"{resources.FirstOrDefault()?.ResourceGroup ?? "default-rg"}\"");
            sb.AppendLine("}");
            sb.AppendLine();

            sb.AppendLine("variable \"tags\" {");
            sb.AppendLine("  description = \"A map of tags to assign to resources\"");
            sb.AppendLine("  type        = map(string)");
            sb.AppendLine("  default = {");
            sb.AppendLine("    Environment    = \"Production\"");
            sb.AppendLine("    DeployedBy     = \"AzureDiscovery\"");
            sb.AppendLine($"    DeploymentDate = \"{DateTime.UtcNow:yyyy-MM-dd}\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            sb.AppendLine();

            // Resource-specific variables
            foreach (var resource in resources)
            {
                var sanitizedName = SanitizeName(resource.Name);
                var varName = $"{sanitizedName}_name";

                if (!addedVariables.Contains(varName))
                {
                    sb.AppendLine($"variable \"{varName}\" {{");
                    sb.AppendLine($"  description = \"Name for {resource.Type} resource\"");
                    sb.AppendLine("  type        = string");
                    sb.AppendLine($"  default     = \"{resource.Name}\"");
                    sb.AppendLine("}");
                    sb.AppendLine();
                    addedVariables.Add(varName);
                }
            }

            return sb.ToString();
        }

        private string GenerateTerraformResource(AzureResource resource, HashSet<string> usedNames)
        {
            var resourceName = GetUniqueResourceName(resource, usedNames);

            return resource.Type.ToLower() switch
            {
                "microsoft.storage/storageaccounts" => GenerateStorageAccount(resource, resourceName),
                "microsoft.network/virtualnetworks" => GenerateVirtualNetwork(resource, resourceName),
                "microsoft.network/networksecuritygroups" => GenerateNetworkSecurityGroup(resource, resourceName),
                "microsoft.network/publicipaddresses" => GeneratePublicIP(resource, resourceName),
                "microsoft.network/networkinterfaces" => GenerateNetworkInterface(resource, resourceName),
                "microsoft.compute/virtualmachines" => GenerateVirtualMachine(resource, resourceName),
                "microsoft.compute/disks" => GenerateManagedDisk(resource, resourceName),
                "microsoft.sql/servers" => GenerateSqlServer(resource, resourceName),
                "microsoft.sql/servers/databases" => GenerateSqlDatabase(resource, resourceName),
                "microsoft.keyvault/vaults" => GenerateKeyVault(resource, resourceName),
                "microsoft.web/sites" => GenerateAppService(resource, resourceName),
                "microsoft.web/serverfarms" => GenerateAppServicePlan(resource, resourceName),
                "microsoft.servicebus/namespaces" => GenerateServiceBusNamespace(resource, resourceName),
                "microsoft.documentdb/databaseaccounts" => GenerateCosmosDB(resource, resourceName),
                "microsoft.containerregistry/registries" => GenerateContainerRegistry(resource, resourceName),
                "microsoft.operationalinsights/workspaces" => GenerateLogAnalyticsWorkspace(resource, resourceName),
                "microsoft.insights/components" => GenerateApplicationInsights(resource, resourceName),
                _ => GenerateGenericResource(resource, resourceName)
            };
        }

        private string GetUniqueResourceName(AzureResource resource, HashSet<string> usedNames)
        {
            var baseName = SanitizeName(resource.Name);
            var uniqueName = baseName;
            var counter = 1;

            while (usedNames.Contains(uniqueName))
            {
                uniqueName = $"{baseName}_{counter}";
                counter++;
            }

            usedNames.Add(uniqueName);
            return uniqueName;
        }

        private string GenerateStorageAccount(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var sku = ExtractStorageAccountSku(properties);
            var kind = resource.Kind ?? "StorageV2";
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_storage_account"" ""{resourceName}"" {{
  name                     = var.{sanitizedName}_name
  resource_group_name      = var.resource_group_name
  location                 = var.location
  account_tier             = ""{sku.tier}""
  account_replication_type = ""{sku.replication}""
  account_kind             = ""{kind}""

  tags = var.tags
}}";
        }

        private string GenerateServiceBusNamespace(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var sku = GetPropertyValue(properties, "sku", "Standard");
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_servicebus_namespace"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = ""{sku}""

  tags = var.tags
}}";
        }

        private string GenerateCosmosDB(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var offerType = GetPropertyValue(properties, "databaseAccountOfferType", "Standard");
            var kind = resource.Kind ?? "GlobalDocumentDB";
            var consistencyLevel = GetPropertyValue(properties, "consistencyPolicy.defaultConsistencyLevel", "Session");
            var geoLocations = ExtractGeoLocations(properties, "var.location");
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_cosmosdb_account"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  offer_type          = ""{offerType}""
  kind                = ""{kind}""

  consistency_policy {{
    consistency_level = ""{consistencyLevel}""
  }}

{geoLocations}

  tags = var.tags
}}";
        }

        private string GenerateVirtualNetwork(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var addressSpace = GetAddressSpace(properties);
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_virtual_network"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  address_space       = {addressSpace}

  tags = var.tags
}}";
        }

        private string GenerateNetworkSecurityGroup(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_network_security_group"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location

  tags = var.tags
}}";
        }

        private string GeneratePublicIP(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var allocationMethod = GetPropertyValue(properties, "publicIPAllocationMethod", "Dynamic");
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_public_ip"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  allocation_method   = ""{allocationMethod}""

  tags = var.tags
}}";
        }

        private string GenerateNetworkInterface(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_network_interface"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location

  ip_configuration {{
    name                          = ""internal""
    subnet_id                     = ""# Reference to subnet""
    private_ip_address_allocation = ""Dynamic""
  }}

  tags = var.tags
}}";
        }

        private string GenerateVirtualMachine(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var vmSize = GetPropertyValue(properties, "hardwareProfile.vmSize", "Standard_B2s");
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_linux_virtual_machine"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  size                = ""{vmSize}""
  admin_username      = ""azureuser""

  network_interface_ids = [
    # Reference to network interface
  ]

  os_disk {{
    caching              = ""ReadWrite""
    storage_account_type = ""Standard_LRS""
  }}

  source_image_reference {{
    publisher = ""Canonical""
    offer     = ""0001-com-ubuntu-server-focal""
    sku       = ""20_04-lts-gen2""
    version   = ""latest""
  }}

  admin_ssh_key {{
    username   = ""azureuser""
    public_key = ""# Your SSH public key""
  }}

  tags = var.tags
}}";
        }

        private string GenerateManagedDisk(AzureResource resource, string resourceName)
        {
            var properties = resource.Properties.RootElement;
            var diskSizeGb = GetPropertyValue(properties, "diskSizeGB", "128");
            var storageType = GetPropertyValue(properties, "accountType", "Standard_LRS");
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_managed_disk"" ""{resourceName}"" {{
  name                 = var.{sanitizedName}_name
  resource_group_name  = var.resource_group_name
  location             = var.location
  storage_account_type = ""{storageType}""
  create_option        = ""Empty""
  disk_size_gb         = {diskSizeGb}

  tags = var.tags
}}";
        }

        private string GenerateSqlServer(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_mssql_server"" ""{resourceName}"" {{
  name                         = var.{sanitizedName}_name
  resource_group_name          = var.resource_group_name
  location                     = var.location
  version                      = ""12.0""
  administrator_login          = ""sqladmin""
  administrator_login_password = ""# Set your password""

  tags = var.tags
}}";
        }

        private string GenerateSqlDatabase(AzureResource resource, string resourceName)
        {
            var serverName = ExtractServerName(resource.Id);
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_mssql_database"" ""{resourceName}"" {{
  name      = var.{sanitizedName}_name
  server_id = azurerm_mssql_server.{SanitizeName(serverName)}.id

  tags = var.tags
}}";
        }

        private string GenerateKeyVault(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_key_vault"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = ""standard""

  tags = var.tags
}}";
        }

        private string GenerateAppService(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_linux_web_app"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  service_plan_id     = ""# Reference to app service plan""

  site_config {{}}

  tags = var.tags
}}";
        }

        private string GenerateAppServicePlan(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_service_plan"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  os_type             = ""Linux""
  sku_name            = ""B1""

  tags = var.tags
}}";
        }

        private string GenerateContainerRegistry(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_container_registry"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = ""Basic""
  admin_enabled       = false

  tags = var.tags
}}";
        }

        private string GenerateLogAnalyticsWorkspace(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_log_analytics_workspace"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  sku                 = ""PerGB2018""
  retention_in_days   = 30

  tags = var.tags
}}";
        }

        private string GenerateApplicationInsights(AzureResource resource, string resourceName)
        {
            var sanitizedName = SanitizeName(resource.Name);

            return $@"resource ""azurerm_application_insights"" ""{resourceName}"" {{
  name                = var.{sanitizedName}_name
  resource_group_name = var.resource_group_name
  location            = var.location
  application_type    = ""web""

  tags = var.tags
}}";
        }

        private string GenerateGenericResource(AzureResource resource, string resourceName)
        {
            return $@"# Resource type '{resource.Type}' is not directly supported by this generator.
# Resource name: {resource.Name}
# Resource ID: {resource.Id}
# Consider implementing a custom resource block or using azurerm_resource_group_template_deployment";
        }

        private string FormatTags(AzureResource resource)
        {
            try
            {
                if (resource.Tags.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(resource.Tags.RootElement.GetRawText());
                    if (dict != null && dict.Count > 0)
                    {
                        var tagPairs = dict.Select(kv => $"    \"{kv.Key}\" = \"{kv.Value}\"");
                        return "{\n" + string.Join(",\n", tagPairs) + "\n  }";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse tags for resource {ResourceName}: {Error}", resource.Name, ex.Message);
            }
            return "var.tags";
        }

        private string GetPropertyValue(JsonElement properties, string propertyPath, string defaultValue)
        {
            try
            {
                var pathParts = propertyPath.Split('.');
                var current = properties;

                foreach (var part in pathParts)
                {
                    if (current.TryGetProperty(part, out var prop))
                    {
                        current = prop;
                    }
                    else
                    {
                        return defaultValue;
                    }
                }

                return current.ValueKind switch
                {
                    JsonValueKind.String => current.GetString() ?? defaultValue,
                    JsonValueKind.Number => current.GetInt32().ToString(),
                    JsonValueKind.Object when current.TryGetProperty("name", out var nameProp) => nameProp.GetString() ?? defaultValue,
                    _ => defaultValue
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to get property {PropertyPath}: {Error}", propertyPath, ex.Message);
                return defaultValue;
            }
        }

        private string GetAddressSpace(JsonElement properties)
        {
            try
            {
                if (properties.TryGetProperty("addressSpace", out var addressSpaceProp) &&
                    addressSpaceProp.TryGetProperty("addressPrefixes", out var prefixesProp) &&
                    prefixesProp.ValueKind == JsonValueKind.Array)
                {
                    var prefixes = prefixesProp.EnumerateArray()
                        .Select(p => $"\"{p.GetString()}\"")
                        .ToArray();
                    return $"[{string.Join(", ", prefixes)}]";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to parse address space: {Error}", ex.Message);
            }
            return "[\"10.0.0.0/16\"]";
        }

        private string ExtractServerName(string resourceId)
        {
            var parts = resourceId.Split('/');
            var serverIndex = Array.IndexOf(parts, "servers");
            return serverIndex >= 0 && serverIndex + 1 < parts.Length ? parts[serverIndex + 1] : "unknown_server";
        }

        private string ExtractGeoLocations(JsonElement properties, string defaultLocation)
        {
            try
            {
                if (properties.TryGetProperty("locations", out var locationsProp) &&
                    locationsProp.ValueKind == JsonValueKind.Array)
                {
                    var locations = new List<string>();
                    foreach (var location in locationsProp.EnumerateArray())
                    {
                        var locationName = location.TryGetProperty("locationName", out var locProp) ?
                            locProp.GetString() : defaultLocation.Replace("var.", "");
                        var priority = location.TryGetProperty("failoverPriority", out var priProp) ?
                            priProp.GetInt32() : 0;

                        locations.Add($@"  geo_location {{
    location          = ""{locationName}""
    failover_priority = {priority}
  }}");
                    }
                    return string.Join("\n", locations);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to extract geo-locations: {Error}", ex.Message);
            }

            return $@"  geo_location {{
    location          = {defaultLocation}
    failover_priority = 0
  }}";
        }

        private (string tier, string replication) ExtractStorageAccountSku(JsonElement properties)
        {
            try
            {
                if (properties.TryGetProperty("sku", out var skuProp))
                {
                    var skuName = skuProp.TryGetProperty("name", out var nameProp) ?
                        nameProp.GetString() : "Standard_LRS";

                    return skuName switch
                    {
                        "Standard_LRS" => ("Standard", "LRS"),
                        "Standard_GRS" => ("Standard", "GRS"),
                        "Standard_RAGRS" => ("Standard", "RAGRS"),
                        "Standard_ZRS" => ("Standard", "ZRS"),
                        "Premium_LRS" => ("Premium", "LRS"),
                        "Premium_ZRS" => ("Premium", "ZRS"),
                        _ => ("Standard", "LRS")
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to extract storage account SKU: {Error}", ex.Message);
            }

            return ("Standard", "LRS");
        }

        private string SanitizeName(string name)
        {
            var sanitized = Regex.Replace(name, @"[^a-zA-Z0-9_]", "_");
            if (char.IsDigit(sanitized[0]))
            {
                sanitized = "r_" + sanitized;
            }
            return sanitized;
        }
    }
}