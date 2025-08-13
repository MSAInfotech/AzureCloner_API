using Microsoft.Extensions.Logging;
using AzureDiscovery.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AzureDiscovery.Infrastructure.Services
{
    public class ArmTemplateGenerator
    {
        private readonly ILogger _logger;
        private readonly List<object> _defaultGeneratedResources = new();
        public ArmTemplateGenerator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<Dictionary<string, object>> GenerateTemplateAsync(List<AzureResource> resources)
        {
            _logger.LogInformation("Generating ARM template for {ResourceCount} resources", resources.Count);

            return new Dictionary<string, object>
            {
                ["$schema"] = "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
                ["contentVersion"] = "1.0.0.0",
                ["parameters"] = GenerateParameters(resources),
                ["variables"] = GenerateVariables(resources),
                ["resources"] = await GenerateResourcesAsync(resources),
                ["outputs"] = GenerateOutputs(resources)
            };
        }

        private Dictionary<string, object> GenerateParameters(List<AzureResource> resources)
        {
            var parameters = new Dictionary<string, object>();

            // Resource-specific parameters
            foreach (var resource in resources)
            {
                var safeName = SanitizeName(resource.Name);
                var paramName = $"{safeName}Name";
                var locationParamName = $"{safeName}Location";

                // Name parameter
                if (!parameters.ContainsKey(paramName))
                {
                    var parameterDefinition = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["metadata"] = new { description = $"Name for {resource.Type}" }
                    };

                    if (resource.Type == "Microsoft.Storage/storageAccounts")
                    {
                        var storageAccountName = GetStorageAccountName(resource);
                        parameterDefinition["defaultValue"] = storageAccountName;
                        parameterDefinition["minLength"] = 3;
                        parameterDefinition["maxLength"] = 24;
                        parameterDefinition["metadata"] = new { description = "Storage account name (3-24 characters, lowercase letters and numbers only)" };
                    }
                    else
                    {
                        parameterDefinition["defaultValue"] = resource.Name;
                    }

                    parameters[paramName] = parameterDefinition;
                }

                // Location parameter per resource
                if (!parameters.ContainsKey(locationParamName))
                {
                    parameters[locationParamName] = new Dictionary<string, object>
                    {
                        ["type"] = "string",
                        ["defaultValue"] = resource.Location ?? "[resourceGroup().location]",
                        ["metadata"] = new { description = $"Location for {resource.Type}" }
                    };
                }
            }

            // Add SQL Server admin password parameter if needed
            var hasSqlServer = resources.Any(r => r.Type == "Microsoft.Sql/servers");
            if (hasSqlServer)
            {
                parameters["sqlAdminPassword"] = new Dictionary<string, object>
                {
                    ["type"] = "securestring",
                    ["metadata"] = new { description = "SQL Server administrator password" }
                };
            }

            // Add default App Service Plan parameter if any Web App has no linked plan
            var webAppsWithoutPlan = resources
                .Where(r => r.Type.Equals("Microsoft.Web/sites", StringComparison.OrdinalIgnoreCase))
                .Where(webApp => !resources.Any(p =>
                    p.Type.Equals("Microsoft.Web/serverfarms", StringComparison.OrdinalIgnoreCase) &&
                    webApp.Dependencies.Any(d => d.TargetResourceId == p.Id)))
                .ToList();

            if (webAppsWithoutPlan.Any() && !parameters.ContainsKey("defaultAppServicePlan"))
            {
                parameters["defaultAppServicePlan"] = new Dictionary<string, object>
                {
                    ["type"] = "string",
                    ["defaultValue"] = "Default-AppServicePlan",
                    ["metadata"] = new { description = "Fallback App Service Plan name for Web Apps without an associated plan" }
                };
            }

            return parameters;
        }


        private object GenerateVariables(List<AzureResource> resources)
        {
            return new Dictionary<string, object>
            {
                ["resourcePrefix"] = "[concat(resourceGroup().name, '-')]"
            };
        }

        private async Task<List<object>> GenerateResourcesAsync(List<AzureResource> resources)
        {
            var armResources = new List<object>();
            armResources.AddRange(_defaultGeneratedResources);
            var sortedResources = resources.OrderBy(r => r.DependencyLevel).ToList();

            foreach (var resource in sortedResources)
            {
                try
                {
                    var armResource = await GenerateArmResourceAsync(resource, resources);
                    if (armResource != null)
                    {
                        armResources.Add(armResource);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to generate ARM resource for {ResourceId}", resource.Id);
                }
            }

            return armResources;
        }

        private async Task<object?> GenerateArmResourceAsync(AzureResource resource, List<AzureResource> allResources)
        {
            return resource.Type switch
            {
                "Microsoft.Storage/storageAccounts" => GenerateStorageAccountResource(resource, allResources),
                "Microsoft.Network/virtualNetworks" => GenerateVirtualNetworkResource(resource, allResources),
                "Microsoft.Network/networkSecurityGroups" => GenerateNetworkSecurityGroupResource(resource, allResources),
                "Microsoft.Network/publicIPAddresses" => GeneratePublicIPResource(resource, allResources),
                "Microsoft.Network/networkInterfaces" => GenerateNetworkInterfaceResource(resource, allResources),
                "Microsoft.Compute/virtualMachines" => GenerateVirtualMachineResource(resource, allResources),
                "Microsoft.Web/serverfarms" => GenerateAppServicePlanResource(resource, allResources),
                "Microsoft.Web/sites" => GenerateWebAppResource(resource, allResources),
                "Microsoft.Sql/servers" => GenerateSqlServerResource(resource, allResources),
                "microsoft.documentdb/databaseaccounts" => GenerateCosmosDbResource(resource, allResources),
                "microsoft.servicebus/namespaces" => GenerateServiceBusResource(resource, allResources),
                "Microsoft.KeyVault/vaults" => GenerateKeyVaultResource(resource, allResources),
                _ => GenerateGenericResource(resource, allResources)
            };
        }

        private string GetApiVersion(string resourceType)
        {
            return resourceType.ToLowerInvariant() switch
            {
                "microsoft.storage/storageaccounts" => "2023-01-01",
                "microsoft.documentdb/databaseaccounts" => "2024-05-15",
                "microsoft.servicebus/namespaces" => "2024-01-01",
                "microsoft.sql/servers" => "2021-11-01",
                "microsoft.web/serverfarms" => "2023-01-01",
                "microsoft.web/sites" => "2023-01-01",
                "microsoft.keyvault/vaults" => "2023-07-01",
                "microsoft.network/virtualnetworks" => "2023-09-01",
                "microsoft.network/networksecuritygroups" => "2023-09-01",
                "microsoft.network/publicipaddresses" => "2023-09-01",
                "microsoft.network/networkinterfaces" => "2023-09-01",
                "microsoft.compute/virtualmachines" => "2023-09-01",
                _ => "2023-05-01"
            };
        }

        private object GenerateStorageAccountResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            object? skuObj = resource.Sku?.RootElement.ValueKind == JsonValueKind.Object && resource.Sku.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Sku.RootElement.GetRawText())
                : new { name = "Standard_LRS" };

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            var kind = string.IsNullOrWhiteSpace(resource.Kind) ? "StorageV2" : resource.Kind;

            var writableProps = new Dictionary<string, object>
            {
                ["supportsHttpsTrafficOnly"] = ExtractProperty(props, "supportsHttpsTrafficOnly", true),
                ["minimumTlsVersion"] = ExtractProperty(props, "minimumTlsVersion", "TLS1_2"),
                ["allowBlobPublicAccess"] = ExtractProperty(props, "allowBlobPublicAccess", false),
                ["allowSharedKeyAccess"] = ExtractProperty(props, "allowSharedKeyAccess", true)
            };

            if (props.TryGetProperty("encryption", out var encProp))
            {
                var cleanedEncryption = CleanEncryptionProperty(encProp);
                if (cleanedEncryption != null)
                    writableProps["encryption"] = cleanedEncryption;
            }

            if (props.TryGetProperty("networkAcls", out var networkAclsProp))
            {
                writableProps["networkAcls"] = JsonSerializer.Deserialize<object>(networkAclsProp.GetRawText());
            }

            if (kind == "StorageV2" && skuObj is JsonElement skuEl && skuEl.TryGetProperty("name", out var skuNameEl) &&
                skuNameEl.GetString()?.StartsWith("Standard_", StringComparison.OrdinalIgnoreCase) == true &&
                !writableProps.ContainsKey("accessTier"))
            {
                writableProps["accessTier"] = "Hot";
            }

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["sku"] = skuObj,
                ["kind"] = kind,
                ["tags"] = ExtractTags(resource),
                ["properties"] = writableProps,
                ["dependsOn"] = dependsOn
            };

            if (identityObj != null)
                armResource["identity"] = identityObj;

            return armResource;
        }

        private object? CleanEncryptionProperty(JsonElement encProp)
        {
            try
            {
                var encryption = new Dictionary<string, object>
                {
                    ["keySource"] = encProp.TryGetProperty("keySource", out var keySource) ? keySource.GetString() ?? "Microsoft.Storage" : "Microsoft.Storage"
                };

                if (encProp.TryGetProperty("services", out var services))
                {
                    var cleanedServices = new Dictionary<string, object>();
                    foreach (var service in services.EnumerateObject())
                    {
                        if (service.Name == "blob" || service.Name == "file")
                        {
                            cleanedServices[service.Name] = new { enabled = true, keyType = "Account" };
                        }
                    }
                    if (cleanedServices.Count > 0)
                        encryption["services"] = cleanedServices;
                }

                if (encProp.TryGetProperty("requireInfrastructureEncryption", out var reqInfraEnc))
                {
                    encryption["requireInfrastructureEncryption"] = reqInfraEnc.GetBoolean();
                }

                return encryption.Count > 0 ? encryption : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean encryption property");
                return null;
            }
        }

        private object GenerateAppServicePlanResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            object? skuObj = resource.Sku?.RootElement.ValueKind == JsonValueKind.Object && resource.Sku.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Sku.RootElement.GetRawText())
                : new { name = "F1", tier = "Free" };

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            var writableProps = new Dictionary<string, object>
            {
                ["perSiteScaling"] = ExtractProperty(props, "perSiteScaling", false)
            };

            if (props.TryGetProperty("maximumElasticWorkerCount", out var maxElasticProp))
            {
                writableProps["maximumElasticWorkerCount"] = maxElasticProp.GetInt32();
            }

            if (props.TryGetProperty("reserved", out var reservedProp))
            {
                writableProps["reserved"] = reservedProp.GetBoolean();
            }

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["sku"] = skuObj,
                ["tags"] = ExtractTags(resource),
                ["properties"] = writableProps,
                ["dependsOn"] = dependsOn
            };

            if (identityObj != null)
                armResource["identity"] = identityObj;

            return armResource;
        }

        private object GenerateWebAppResource(AzureResource resource, List<AzureResource> allResources)
        {
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            // Check if Web App already has a linked App Service Plan
            var appServicePlan = allResources.FirstOrDefault(r =>
                r.Type.Equals("Microsoft.Web/serverfarms", StringComparison.OrdinalIgnoreCase) &&
                resource.Dependencies.Any(d => d.TargetResourceId == r.Id));

            string appServicePlanName;
            if (appServicePlan != null)
            {
                appServicePlanName = SanitizeName(appServicePlan.Name);
            }
            else
            {
                // No App Service Plan found ? create a default one
                appServicePlanName = "defaultAppServicePlan";

                var defaultPlanResource = new Dictionary<string, object>
                {
                    ["type"] = "Microsoft.Web/serverfarms",
                    ["apiVersion"] = GetApiVersion("Microsoft.Web/serverfarms"),
                    ["name"] = $"[parameters('{appServicePlanName}Name')]",
                    ["location"] = $"[parameters('{safeName}Location')]", // Same location as Web App
                    ["sku"] = new { name = "F1", tier = "Free" },
                    ["properties"] = new { perSiteScaling = false },
                    ["dependsOn"] = new List<string>()
                };

                // Inject into template so Web App can depend on it
                allResources.Add(new AzureResource
                {
                    Name = appServicePlanName,
                    Type = "Microsoft.Web/serverfarms",
                    Location = resource.Location,
                    DependencyLevel = resource.DependencyLevel - 1
                });

                // Add default plan to generated ARM resources list
                _defaultGeneratedResources.Add(defaultPlanResource);
            }

            string serverFarmId = $"[resourceId('Microsoft.Web/serverfarms', parameters('{appServicePlanName}Name'))]";

            // Minimal valid properties for deployment
            var writableProps = new Dictionary<string, object>
            {
                ["serverFarmId"] = serverFarmId,
                ["httpsOnly"] = true,
                ["siteConfig"] = new Dictionary<string, object>
                {
                    ["alwaysOn"] = false,
                    ["http20Enabled"] = false,
                    ["minTlsVersion"] = "1.2",
                    ["ftpsState"] = "Disabled"
                }
            };

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["kind"] = resource.Kind ?? "app",
                ["tags"] = ExtractTags(resource),
                ["dependsOn"] = dependsOn.Append(serverFarmId).ToList(),
                ["properties"] = writableProps
            };

            return armResource;
        }




        private object GenerateCosmosDbResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var dependsOn = GenerateDependsOn(resource, allResources);

            var locations = ExtractAndCleanLocations(props);

            // Clean capabilities to only keep 'name' property
            var cleanedCapabilities = new List<object>();
            if (props.TryGetProperty("capabilities", out var capArray) && capArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var cap in capArray.EnumerateArray())
                {
                    if (cap.TryGetProperty("name", out var nameProp) && !string.IsNullOrEmpty(nameProp.GetString()))
                    {
                        cleanedCapabilities.Add(new { name = nameProp.GetString() });
                    }
                }
            }

            // Clean consistencyPolicy to only allowed keys
            var cleanedConsistencyPolicy = new Dictionary<string, object>();
            if (props.TryGetProperty("consistencyPolicy", out var policyProp) && policyProp.ValueKind == JsonValueKind.Object)
            {
                if (policyProp.TryGetProperty("defaultConsistencyLevel", out var levelProp))
                    cleanedConsistencyPolicy["defaultConsistencyLevel"] = levelProp.GetString() ?? "Session";

                if (policyProp.TryGetProperty("maxIntervalInSeconds", out var intervalProp) && intervalProp.ValueKind == JsonValueKind.Number)
                    cleanedConsistencyPolicy["maxIntervalInSeconds"] = intervalProp.GetInt32();

                if (policyProp.TryGetProperty("maxStalenessPrefix", out var prefixProp) && prefixProp.ValueKind == JsonValueKind.Number)
                    cleanedConsistencyPolicy["maxStalenessPrefix"] = prefixProp.GetInt32();
            }
            else
            {
                cleanedConsistencyPolicy["defaultConsistencyLevel"] = "Session";
            }

            var backupPolicy = GenerateBackupPolicy(props);

            return new
            {
                type = resource.Type,
                apiVersion = GetApiVersion(resource.Type),
                name = $"[parameters('{SanitizeName(resource.Name)}Name')]",
                location = $"[parameters('{SanitizeName(resource.Name)}Location')]",
                tags = ExtractTags(resource),
                kind = resource.Kind ?? "GlobalDocumentDB",
                properties = new
                {
                    databaseAccountOfferType = "Standard",
                    consistencyPolicy = cleanedConsistencyPolicy,
                    locations = locations,
                    capabilities = cleanedCapabilities,
                    backupPolicy = backupPolicy,
                    publicNetworkAccess = ExtractProperty(props, "publicNetworkAccess", "Enabled"),
                    enableAutomaticFailover = ExtractProperty(props, "enableAutomaticFailover", true),
                    minimalTlsVersion = ExtractProperty(props, "minimalTlsVersion", "Tls12")
                },
                dependsOn = dependsOn
            };
        }


        private object GenerateBackupPolicy(JsonElement props)
        {
            try
            {
                if (props.TryGetProperty("backupPolicy", out var backupPolicyProp) &&
                    backupPolicyProp.ValueKind == JsonValueKind.Object)
                {
                    if (backupPolicyProp.TryGetProperty("type", out var typeProp) &&
                        typeProp.GetString() == "Periodic")
                    {
                        var backupPolicy = new Dictionary<string, object> { ["type"] = "Periodic" };
                        var periodicModeProperties = new Dictionary<string, object>
                        {
                            ["backupIntervalInMinutes"] = backupPolicyProp.TryGetProperty("periodicModeProperties", out var periodicProps) &&
                                periodicProps.TryGetProperty("backupIntervalInMinutes", out var intervalProp)
                                ? intervalProp.GetInt32() : 240,
                            ["backupRetentionIntervalInHours"] = periodicProps.TryGetProperty("backupRetentionIntervalInHours", out var retentionProp)
                                ? retentionProp.GetInt32() : 8,
                            ["backupStorageRedundancy"] = periodicProps.TryGetProperty("backupStorageRedundancy", out JsonElement redundancyProp)
                                ? redundancyProp.GetString() ?? "Geo": "Geo"

                        };
                        backupPolicy["periodicModeProperties"] = periodicModeProperties;
                        return backupPolicy;
                    }
                    else if (backupPolicyProp.TryGetProperty("type", out var continuousTypeProp) &&
                             continuousTypeProp.GetString() == "Continuous")
                    {
                        var continuousPolicy = new Dictionary<string, object> { ["type"] = "Continuous" };
                        if (backupPolicyProp.TryGetProperty("continuousModeProperties", out var continuousProps))
                        {
                            continuousPolicy["continuousModeProperties"] = JsonSerializer.Deserialize<object>(continuousProps.GetRawText());
                        }
                        return continuousPolicy;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse backup policy, using default Periodic policy");
            }

            return new
            {
                type = "Periodic",
                periodicModeProperties = new
                {
                    backupIntervalInMinutes = 240,
                    backupRetentionIntervalInHours = 8,
                    backupStorageRedundancy = "Geo"
                }
            };
        }

        private object ExtractAndCleanLocations(JsonElement props)
        {
            try
            {
                if (props.TryGetProperty("locations", out var locationsProp) &&
                    locationsProp.ValueKind == JsonValueKind.Array)
                {
                    var cleanedLocations = new List<object>();
                    foreach (var location in locationsProp.EnumerateArray())
                    {
                        var cleanLocation = new Dictionary<string, object>();
                        if (location.TryGetProperty("locationName", out var locationName))
                            cleanLocation["locationName"] = locationName.GetString();
                        if (location.TryGetProperty("failoverPriority", out var priority))
                            cleanLocation["failoverPriority"] = priority.GetInt32();
                        if (location.TryGetProperty("isZoneRedundant", out var zoneRedundant))
                            cleanLocation["isZoneRedundant"] = zoneRedundant.GetBoolean();
                        if (cleanLocation.Count > 0)
                            cleanedLocations.Add(cleanLocation);
                    }
                    return cleanedLocations.Any() ? cleanedLocations : new[] { new { locationName = "[parameters('location')]", failoverPriority = 0, isZoneRedundant = false } };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract locations for Cosmos DB resource");
            }
            return new[] { new { locationName = "[parameters('location')]", failoverPriority = 0, isZoneRedundant = false } };
        }

        private object GenerateServiceBusResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var dependsOn = GenerateDependsOn(resource, allResources);

            return new
            {
                type = resource.Type,
                apiVersion = GetApiVersion(resource.Type),
                name = $"[parameters('{SanitizeName(resource.Name)}Name')]",
                location = $"[parameters('{SanitizeName(resource.Name)}Location')]",
                tags = ExtractTags(resource),
                sku = new { name = "Standard", tier = "Standard" },
                properties = new
                {
                    minimumTlsVersion = ExtractProperty(props, "minimumTlsVersion", "1.2"),
                    zoneRedundant = ExtractProperty(props, "zoneRedundant", false),
                    disableLocalAuth = ExtractProperty(props, "disableLocalAuth", false),
                    publicNetworkAccess = ExtractProperty(props, "publicNetworkAccess", "Enabled")
                },
                dependsOn = dependsOn
            };
        }

        private object GenerateVirtualNetworkResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var dependsOn = GenerateDependsOn(resource, allResources);

            return new
            {
                type = resource.Type,
                apiVersion = GetApiVersion(resource.Type),
                name = $"[parameters('{SanitizeName(resource.Name)}Name')]",
                location = $"[parameters('{SanitizeName(resource.Name)}Location')]",
                tags = ExtractTags(resource),
                properties = new
                {
                    addressSpace = ExtractProperty(props, "addressSpace", new { addressPrefixes = new[] { "10.0.0.0/16" } }),
                    subnets = ExtractProperty(props, "subnets", new object[0])
                },
                dependsOn = dependsOn
            };
        }

        private object GenerateNetworkSecurityGroupResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var dependsOn = GenerateDependsOn(resource, allResources);

            return new
            {
                type = resource.Type,
                apiVersion = GetApiVersion(resource.Type),
                name = $"[parameters('{SanitizeName(resource.Name)}Name')]",
                location = $"[parameters('{SanitizeName(resource.Name)}Location')]",
                tags = ExtractTags(resource),
                properties = new
                {
                    securityRules = ExtractProperty(props, "securityRules", new object[0])
                },
                dependsOn = dependsOn
            };
        }

        private object GeneratePublicIPResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var dependsOn = GenerateDependsOn(resource, allResources);
            var safeName = SanitizeName(resource.Name);

            object? skuObj = resource.Sku?.RootElement.ValueKind == JsonValueKind.Object && resource.Sku.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Sku.RootElement.GetRawText())
                : new { name = "Standard" };

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["tags"] = ExtractTags(resource),
                ["sku"] = skuObj,
                ["properties"] = new
                {
                    publicIPAllocationMethod = ExtractProperty(props, "publicIPAllocationMethod", "Static"),
                    publicIPAddressVersion = ExtractProperty(props, "publicIPAddressVersion", "IPv4")
                },
                ["dependsOn"] = dependsOn
            };

            if (identityObj != null)
                armResource["identity"] = identityObj;

            return armResource;
        }

        private object GenerateNetworkInterfaceResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var dependsOn = GenerateDependsOn(resource, allResources);

            return new
            {
                type = resource.Type,
                apiVersion = GetApiVersion(resource.Type),
                name = $"[parameters('{SanitizeName(resource.Name)}Name')]",
                location = $"[parameters('{SanitizeName(resource.Name)}Location')]",
                tags = ExtractTags(resource),
                dependsOn = dependsOn,
                properties = new
                {
                    ipConfigurations = ExtractProperty(props, "ipConfigurations", new object[0])
                }
            };
        }

        private object GenerateVirtualMachineResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            object? skuObj = resource.Sku?.RootElement.ValueKind == JsonValueKind.Object && resource.Sku.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Sku.RootElement.GetRawText())
                : null;

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            object? planObj = resource.Plan?.RootElement.ValueKind == JsonValueKind.Object && resource.Plan.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Plan.RootElement.GetRawText())
                : null;

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["tags"] = ExtractTags(resource),
                ["dependsOn"] = dependsOn,
                ["properties"] = new
                {
                    hardwareProfile = ExtractProperty(props, "hardwareProfile", new { vmSize = "Standard_B2s" }),
                    storageProfile = ExtractProperty(props, "storageProfile", new object()),
                    osProfile = ExtractProperty(props, "osProfile", new object()),
                    networkProfile = ExtractProperty(props, "networkProfile", new object())
                }
            };

            if (skuObj != null) armResource["sku"] = skuObj;
            if (identityObj != null) armResource["identity"] = identityObj;
            if (planObj != null) armResource["plan"] = planObj;

            return armResource;
        }

        private object GenerateSqlServerResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["tags"] = ExtractTags(resource),
                ["properties"] = new
                {
                    administratorLogin = ExtractProperty(props, "administratorLogin", "sqladmin"),
                    administratorLoginPassword = "[parameters('sqlAdminPassword')]",
                    version = ExtractProperty(props, "version", "12.0"),
                    minimalTlsVersion = ExtractProperty(props, "minimalTlsVersion", "1.2"),
                    publicNetworkAccess = ExtractProperty(props, "publicNetworkAccess", "Enabled")
                },
                ["dependsOn"] = dependsOn
            };

            if (identityObj != null)
                armResource["identity"] = identityObj;

            return armResource;
        }

        private object GenerateKeyVaultResource(AzureResource resource, List<AzureResource> allResources)
        {
            var props = resource.Properties.RootElement;
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            object? skuObj = resource.Sku?.RootElement.ValueKind == JsonValueKind.Object && resource.Sku.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Sku.RootElement.GetRawText())
                : new { family = "A", name = "standard" };

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["tags"] = ExtractTags(resource),
                ["properties"] = new
                {
                    sku = skuObj,
                    tenantId = "[subscription().tenantId]",
                    accessPolicies = ExtractProperty(props, "accessPolicies", new object[0]),
                    enabledForDeployment = ExtractProperty(props, "enabledForDeployment", false),
                    enabledForTemplateDeployment = ExtractProperty(props, "enabledForTemplateDeployment", false),
                    enabledForDiskEncryption = ExtractProperty(props, "enabledForDiskEncryption", false),
                    enableSoftDelete = ExtractProperty(props, "enableSoftDelete", true),
                    softDeleteRetentionInDays = ExtractProperty(props, "softDeleteRetentionInDays", 90)
                },
                ["dependsOn"] = dependsOn
            };

            if (identityObj != null)
                armResource["identity"] = identityObj;

            return armResource;
        }

        private object GenerateGenericResource(AzureResource resource, List<AzureResource> allResources)
        {
            var safeName = SanitizeName(resource.Name);
            var dependsOn = GenerateDependsOn(resource, allResources);

            object? skuObj = resource.Sku?.RootElement.ValueKind == JsonValueKind.Object && resource.Sku.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Sku.RootElement.GetRawText())
                : null;

            object? identityObj = resource.Identity?.RootElement.ValueKind == JsonValueKind.Object && resource.Identity.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Identity.RootElement.GetRawText())
                : null;

            object? planObj = resource.Plan?.RootElement.ValueKind == JsonValueKind.Object && resource.Plan.RootElement.EnumerateObject().Any()
                ? JsonSerializer.Deserialize<object>(resource.Plan.RootElement.GetRawText())
                : null;

            var armResource = new Dictionary<string, object>
            {
                ["type"] = resource.Type,
                ["apiVersion"] = GetApiVersion(resource.Type),
                ["name"] = $"[parameters('{safeName}Name')]",
                ["location"] = $"[parameters('{safeName}Location')]",
                ["tags"] = ExtractTags(resource),
                ["dependsOn"] = dependsOn
            };

            if (skuObj != null) armResource["sku"] = skuObj;
            if (identityObj != null) armResource["identity"] = identityObj;
            if (planObj != null) armResource["plan"] = planObj;

            return armResource;
        }

        private object GenerateOutputs(List<AzureResource> resources)
        {
            var outputs = new Dictionary<string, object>();
            foreach (var resource in resources)
            {
                var outputName = $"{SanitizeName(resource.Name)}Id";
                outputs[outputName] = new
                {
                    type = "string",
                    value = $"[resourceId('{resource.Type}', parameters('{SanitizeName(resource.Name)}Name'))]"
                };
            }
            return outputs;
        }

        private List<string> GenerateDependsOn(AzureResource resource, List<AzureResource> allResources)
        {
            var dependsOn = new List<string>();
            foreach (var dependency in resource.Dependencies)
            {
                var dependentResource = allResources.FirstOrDefault(r => r.Id == dependency.TargetResourceId);
                if (dependentResource != null)
                {
                    dependsOn.Add($"[resourceId('{dependentResource.Type}', parameters('{SanitizeName(dependentResource.Name)}Name'))]");
                }
            }
            return dependsOn;
        }

        private object ExtractTags(AzureResource resource)
        {
            try
            {
                if (resource.Tags.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(resource.Tags.RootElement.GetRawText()) ?? new Dictionary<string, string>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract tags for resource {ResourceId}", resource.Id);
            }
            return new Dictionary<string, string>();
        }

        private string GetStorageAccountName(AzureResource resource)
        {
            return SanitizeStorageAccountName(resource.Name);
        }

        private T ExtractProperty<T>(JsonElement properties, string propertyName, T defaultValue)
        {
            try
            {
                if (properties.ValueKind != JsonValueKind.Object)
                    return defaultValue;

                if (properties.TryGetProperty(propertyName, out var property))
                {
                    if (property.ValueKind == JsonValueKind.Null || property.ValueKind == JsonValueKind.Undefined)
                        return defaultValue;

                    if ((property.ValueKind == JsonValueKind.Object && !property.EnumerateObject().Any()) ||
                        (property.ValueKind == JsonValueKind.Array && !property.EnumerateArray().Any()))
                        return defaultValue;

                    var result = JsonSerializer.Deserialize<T>(property.GetRawText());
                    return result ?? defaultValue;
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON deserialization failed for property {PropertyName}, using default value", propertyName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to extract property {PropertyName}, using default value", propertyName);
            }
            return defaultValue;
        }

        private string SanitizeName(string name)
        {
            var sanitized = Regex.Replace(name, @"[^a-zA-Z0-9]", "");
            if (!string.IsNullOrEmpty(sanitized) && char.IsDigit(sanitized[0]))
                sanitized = "p" + sanitized;
            return sanitized;
        }

        private string SanitizeStorageAccountName(string name)
        {
            var sanitized = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", "");
            if (sanitized.Length < 3)
                sanitized = sanitized.PadRight(3, '0');
            if (sanitized.Length > 24)
                sanitized = sanitized.Substring(0, 24);
            return sanitized;
        }
    }
}