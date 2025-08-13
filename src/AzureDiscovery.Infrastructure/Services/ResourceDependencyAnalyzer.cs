using Microsoft.Extensions.Logging;
using AzureDiscovery.Core.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzureDiscovery.Infrastructure.Services
{
    public interface IResourceDependencyAnalyzer
    {
        Task<List<ResourceDependency>> AnalyzeDependenciesAsync(List<AzureResource> resources);
    }

    public class ResourceDependencyAnalyzer : IResourceDependencyAnalyzer
    {
        private readonly ILogger<ResourceDependencyAnalyzer> _logger;
        private readonly Dictionary<string, Func<AzureResource, List<AzureResource>, List<ResourceDependency>>> _analyzers;

        public ResourceDependencyAnalyzer(ILogger<ResourceDependencyAnalyzer> logger)
        {
            _logger = logger;
            _analyzers = new Dictionary<string, Func<AzureResource, List<AzureResource>, List<ResourceDependency>>>
            {
                { "Microsoft.Compute/virtualMachines", AnalyzeVirtualMachineDependencies },
                { "Microsoft.Network/networkInterfaces", AnalyzeNetworkInterfaceDependencies },
                { "Microsoft.Storage/storageAccounts", AnalyzeStorageAccountDependencies },
                { "Microsoft.Web/sites", AnalyzeWebAppDependencies },
                { "Microsoft.Sql/servers", AnalyzeSqlServerDependencies },
                { "Microsoft.KeyVault/vaults", AnalyzeKeyVaultDependencies },
                { "Microsoft.Network/virtualNetworks", AnalyzeVirtualNetworkDependencies },
                { "Microsoft.Network/publicIPAddresses", AnalyzePublicIPDependencies },
                { "Microsoft.Network/networkSecurityGroups", AnalyzeNetworkSecurityGroupDependencies }
            };
        }

        public async Task<List<ResourceDependency>> AnalyzeDependenciesAsync(List<AzureResource> resources)
        {
            var dependencies = new List<ResourceDependency>();

            _logger.LogInformation("Analyzing dependencies for {ResourceCount} resources", resources.Count);

            await Task.Run(() =>
            {
                foreach (var resource in resources)
                {
                    try
                    {
                        if (_analyzers.TryGetValue(resource.Type, out var analyzer))
                        {
                            var resourceDependencies = analyzer(resource, resources);
                            dependencies.AddRange(resourceDependencies);
                        }
                        else
                        {
                            // Generic dependency analysis
                            var genericDependencies = AnalyzeGenericDependencies(resource, resources);
                            dependencies.AddRange(genericDependencies);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to analyze dependencies for resource {ResourceId}", resource.Id);
                    }
                }
            });

            _logger.LogInformation("Found {DependencyCount} dependencies", dependencies.Count);
            return dependencies;
        }

        private List<ResourceDependency> AnalyzeVirtualMachineDependencies(AzureResource vm, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = vm.Properties.RootElement;
                
                // Network Interface dependencies
                if (properties.TryGetProperty("networkProfile", out var networkProfile) &&
                    networkProfile.TryGetProperty("networkInterfaces", out var networkInterfaces))
                {
                    foreach (var nic in networkInterfaces.EnumerateArray())
                    {
                        if (nic.TryGetProperty("id", out var nicId))
                        {
                            var nicResource = FindResourceByAzureId(allResources, nicId.GetString());
                            if (nicResource != null)
                            {
                                dependencies.Add(CreateDependency(vm.Id, nicResource.Id, DependencyType.NetworkDependency));
                            }
                        }
                    }
                }

                // Storage dependencies (OS disk, data disks)
                if (properties.TryGetProperty("storageProfile", out var storageProfile))
                {
                    // OS Disk
                    if (storageProfile.TryGetProperty("osDisk", out var osDisk))
                    {
                        AnalyzeDiskDependency(osDisk, vm, allResources, dependencies);
                    }

                    // Data Disks
                    if (storageProfile.TryGetProperty("dataDisks", out var dataDisks))
                    {
                        foreach (var dataDisk in dataDisks.EnumerateArray())
                        {
                            AnalyzeDiskDependency(dataDisk, vm, allResources, dependencies);
                        }
                    }
                }

                // Availability Set dependency
                if (properties.TryGetProperty("availabilitySet", out var availabilitySet) &&
                    availabilitySet.TryGetProperty("id", out var availabilitySetId))
                {
                    var availabilitySetResource = FindResourceByAzureId(allResources, availabilitySetId.GetString());
                    if (availabilitySetResource != null)
                    {
                        dependencies.Add(CreateDependency(vm.Id, availabilitySetResource.Id, DependencyType.ConfigurationDependency));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing VM dependencies for {ResourceId}", vm.Id);
            }

            return dependencies;
        }

        private void AnalyzeDiskDependency(JsonElement disk, AzureResource vm, List<AzureResource> allResources, List<ResourceDependency> dependencies)
        {
            if (disk.TryGetProperty("managedDisk", out var managedDisk) &&
                managedDisk.TryGetProperty("id", out var diskId))
            {
                var diskResource = FindResourceByAzureId(allResources, diskId.GetString());
                if (diskResource != null)
                {
                    dependencies.Add(CreateDependency(vm.Id, diskResource.Id, DependencyType.StorageDependency));
                }
            }
            else if (disk.TryGetProperty("vhd", out var vhd) &&
                     vhd.TryGetProperty("uri", out var vhdUri))
            {
                // VHD-based disk - find storage account
                var storageAccount = FindStorageAccountByVhdUri(allResources, vhdUri.GetString());
                if (storageAccount != null)
                {
                    dependencies.Add(CreateDependency(vm.Id, storageAccount.Id, DependencyType.StorageDependency));
                }
            }
        }

        private List<ResourceDependency> AnalyzeNetworkInterfaceDependencies(AzureResource nic, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = nic.Properties.RootElement;

                // Subnet dependencies
                if (properties.TryGetProperty("ipConfigurations", out var ipConfigurations))
                {
                    foreach (var ipConfig in ipConfigurations.EnumerateArray())
                    {
                        if (ipConfig.TryGetProperty("properties", out var ipConfigProps))
                        {
                            // Subnet dependency
                            if (ipConfigProps.TryGetProperty("subnet", out var subnet) &&
                                subnet.TryGetProperty("id", out var subnetId))
                            {
                                var vnetId = ExtractVNetIdFromSubnetId(subnetId.GetString());
                                var vnetResource = FindResourceByAzureId(allResources, vnetId);
                                if (vnetResource != null)
                                {
                                    dependencies.Add(CreateDependency(nic.Id, vnetResource.Id, DependencyType.NetworkDependency));
                                }
                            }

                            // Public IP dependency
                            if (ipConfigProps.TryGetProperty("publicIPAddress", out var publicIp) &&
                                publicIp.TryGetProperty("id", out var publicIpId))
                            {
                                var publicIpResource = FindResourceByAzureId(allResources, publicIpId.GetString());
                                if (publicIpResource != null)
                                {
                                    dependencies.Add(CreateDependency(nic.Id, publicIpResource.Id, DependencyType.NetworkDependency));
                                }
                            }

                            // Load Balancer Backend Pool dependency
                            if (ipConfigProps.TryGetProperty("loadBalancerBackendAddressPools", out var backendPools))
                            {
                                foreach (var pool in backendPools.EnumerateArray())
                                {
                                    if (pool.TryGetProperty("id", out var poolId))
                                    {
                                        var loadBalancerId = ExtractLoadBalancerIdFromBackendPoolId(poolId.GetString());
                                        var loadBalancerResource = FindResourceByAzureId(allResources, loadBalancerId);
                                        if (loadBalancerResource != null)
                                        {
                                            dependencies.Add(CreateDependency(nic.Id, loadBalancerResource.Id, DependencyType.NetworkDependency));
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Network Security Group dependency
                if (properties.TryGetProperty("networkSecurityGroup", out var nsg) &&
                    nsg.TryGetProperty("id", out var nsgId))
                {
                    var nsgResource = FindResourceByAzureId(allResources, nsgId.GetString());
                    if (nsgResource != null)
                    {
                        dependencies.Add(CreateDependency(nic.Id, nsgResource.Id, DependencyType.NetworkDependency));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing NIC dependencies for {ResourceId}", nic.Id);
            }

            return dependencies;
        }

        private List<ResourceDependency> AnalyzeStorageAccountDependencies(AzureResource storage, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = storage.Properties.RootElement;

                // Key Vault dependencies for encryption
                if (properties.TryGetProperty("encryption", out var encryption))
                {
                    if (encryption.TryGetProperty("keySource", out var keySource) &&
                        keySource.GetString() == "Microsoft.Keyvault")
                    {
                        if (encryption.TryGetProperty("keyvaultproperties", out var kvProps) &&
                            kvProps.TryGetProperty("keyvaulturi", out var kvUri))
                        {
                            var keyVaultResource = FindKeyVaultByUri(allResources, kvUri.GetString());
                            if (keyVaultResource != null)
                            {
                                dependencies.Add(CreateDependency(storage.Id, keyVaultResource.Id, DependencyType.IdentityDependency));
                            }
                        }
                    }
                }

                // Virtual Network Rules
                if (properties.TryGetProperty("networkAcls", out var networkAcls) &&
                    networkAcls.TryGetProperty("virtualNetworkRules", out var vnetRules))
                {
                    foreach (var rule in vnetRules.EnumerateArray())
                    {
                        if (rule.TryGetProperty("id", out var subnetId))
                        {
                            var vnetId = ExtractVNetIdFromSubnetId(subnetId.GetString());
                            var vnetResource = FindResourceByAzureId(allResources, vnetId);
                            if (vnetResource != null)
                            {
                                dependencies.Add(CreateDependency(storage.Id, vnetResource.Id, DependencyType.NetworkDependency));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing Storage Account dependencies for {ResourceId}", storage.Id);
            }

            return dependencies;
        }

        private List<ResourceDependency> AnalyzeWebAppDependencies(AzureResource webApp, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = webApp.Properties.RootElement;

                // App Service Plan dependency
                if (properties.TryGetProperty("serverFarmId", out var serverFarmId))
                {
                    var appServicePlan = FindResourceByAzureId(allResources, serverFarmId.GetString());
                    if (appServicePlan != null)
                    {
                        dependencies.Add(CreateDependency(webApp.Id, appServicePlan.Id, DependencyType.ParentChild));
                    }
                }

                // Virtual Network Integration
                if (properties.TryGetProperty("virtualNetworkSubnetId", out var subnetId))
                {
                    var vnetId = ExtractVNetIdFromSubnetId(subnetId.GetString());
                    var vnetResource = FindResourceByAzureId(allResources, vnetId);
                    if (vnetResource != null)
                    {
                        dependencies.Add(CreateDependency(webApp.Id, vnetResource.Id, DependencyType.NetworkDependency));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing Web App dependencies for {ResourceId}", webApp.Id);
            }

            return dependencies;
        }

        private List<ResourceDependency> AnalyzeSqlServerDependencies(AzureResource sqlServer, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = sqlServer.Properties.RootElement;

                // Key Vault dependencies for TDE
                if (properties.TryGetProperty("keyId", out var keyId))
                {
                    var keyVaultResource = FindKeyVaultByKeyId(allResources, keyId.GetString());
                    if (keyVaultResource != null)
                    {
                        dependencies.Add(CreateDependency(sqlServer.Id, keyVaultResource.Id, DependencyType.IdentityDependency));
                    }
                }

                // Virtual Network Rules
                if (properties.TryGetProperty("virtualNetworkRules", out var vnetRules))
                {
                    foreach (var rule in vnetRules.EnumerateArray())
                    {
                        if (rule.TryGetProperty("virtualNetworkSubnetId", out var subnetId))
                        {
                            var vnetId = ExtractVNetIdFromSubnetId(subnetId.GetString());
                            var vnetResource = FindResourceByAzureId(allResources, vnetId);
                            if (vnetResource != null)
                            {
                                dependencies.Add(CreateDependency(sqlServer.Id, vnetResource.Id, DependencyType.NetworkDependency));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing SQL Server dependencies for {ResourceId}", sqlServer.Id);
            }

            return dependencies;
        }

        private List<ResourceDependency> AnalyzeKeyVaultDependencies(AzureResource keyVault, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = keyVault.Properties.RootElement;

                // Network dependencies (if using VNet integration)
                if (properties.TryGetProperty("networkAcls", out var networkAcls) &&
                    networkAcls.TryGetProperty("virtualNetworkRules", out var vnetRules))
                {
                    foreach (var rule in vnetRules.EnumerateArray())
                    {
                        if (rule.TryGetProperty("id", out var subnetId))
                        {
                            var vnetId = ExtractVNetIdFromSubnetId(subnetId.GetString());
                            var vnetResource = FindResourceByAzureId(allResources, vnetId);
                            if (vnetResource != null)
                            {
                                dependencies.Add(CreateDependency(keyVault.Id, vnetResource.Id, DependencyType.NetworkDependency));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing Key Vault dependencies for {ResourceId}", keyVault.Id);
            }

            return dependencies;
        }

        private List<ResourceDependency> AnalyzeVirtualNetworkDependencies(AzureResource vnet, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                var properties = vnet.Properties.RootElement;

                // VNet Peering dependencies
                if (properties.TryGetProperty("virtualNetworkPeerings", out var peerings))
                {
                    foreach (var peering in peerings.EnumerateArray())
                    {
                        if (peering.TryGetProperty("properties", out var peeringProps) &&
                            peeringProps.TryGetProperty("remoteVirtualNetwork", out var remoteVnet) &&
                            remoteVnet.TryGetProperty("id", out var remoteVnetId))
                        {
                            var remoteVnetResource = FindResourceByAzureId(allResources, remoteVnetId.GetString());
                            if (remoteVnetResource != null)
                            {
                                dependencies.Add(CreateDependency(vnet.Id, remoteVnetResource.Id, DependencyType.NetworkDependency));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing Virtual Network dependencies for {ResourceId}", vnet.Id);
            }

            return dependencies;
        }

        private List<ResourceDependency> AnalyzePublicIPDependencies(AzureResource publicIp, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();
            // Public IPs typically don't have dependencies on other resources
            return dependencies;
        }

        private List<ResourceDependency> AnalyzeNetworkSecurityGroupDependencies(AzureResource nsg, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();
            // NSGs typically don't have dependencies on other resources
            return dependencies;
        }

        private List<ResourceDependency> AnalyzeGenericDependencies(AzureResource resource, List<AzureResource> allResources)
        {
            var dependencies = new List<ResourceDependency>();

            try
            {
                // Look for resource ID references in properties
                var propertiesJson = resource.Properties.RootElement.GetRawText();
                var resourceIdPattern = @"/subscriptions/[^/]+/resourceGroups/[^/]+/providers/[^/]+/[^/]+/[^""'\s,}]+";
                var matches = Regex.Matches(propertiesJson, resourceIdPattern, RegexOptions.IgnoreCase);

                foreach (Match match in matches)
                {
                    var referencedResourceId = match.Value;
                    var referencedResource = FindResourceByAzureId(allResources, referencedResourceId);
                    
                    if (referencedResource != null && referencedResource.Id != resource.Id)
                    {
                        dependencies.Add(CreateDependency(resource.Id, referencedResource.Id, DependencyType.ConfigurationDependency));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error analyzing generic dependencies for {ResourceId}", resource.Id);
            }

            return dependencies;
        }

        // Helper methods
        private AzureResource? FindResourceByAzureId(List<AzureResource> resources, string? azureResourceId)
        {
            if (string.IsNullOrEmpty(azureResourceId)) return null;
            
            return resources.FirstOrDefault(r => 
                r.Id.EndsWith(ExtractResourceNameFromAzureId(azureResourceId)) ||
                azureResourceId.Contains(ExtractResourceNameFromAzureId(r.Id)));
        }

        private AzureResource? FindKeyVaultByUri(List<AzureResource> resources, string? keyVaultUri)
        {
            if (string.IsNullOrEmpty(keyVaultUri)) return null;
            
            var kvName = ExtractKeyVaultNameFromUri(keyVaultUri);
            return resources.FirstOrDefault(r => r.Type == "Microsoft.KeyVault/vaults" && r.Name == kvName);
        }

        private AzureResource? FindKeyVaultByKeyId(List<AzureResource> resources, string? keyId)
        {
            if (string.IsNullOrEmpty(keyId)) return null;
            
            var kvName = ExtractKeyVaultNameFromKeyId(keyId);
            return resources.FirstOrDefault(r => r.Type == "Microsoft.KeyVault/vaults" && r.Name == kvName);
        }

        private AzureResource? FindStorageAccountByVhdUri(List<AzureResource> resources, string? vhdUri)
        {
            if (string.IsNullOrEmpty(vhdUri)) return null;
            
            var accountName = ExtractStorageAccountNameFromVhdUri(vhdUri);
            return resources.FirstOrDefault(r => r.Type == "Microsoft.Storage/storageAccounts" && r.Name == accountName);
        }

        private ResourceDependency CreateDependency(string sourceId, string targetId, DependencyType type)
        {
            return new ResourceDependency
            {
                SourceResourceId = sourceId,
                TargetResourceId = targetId,
                Type = type,
                IsRequired = true
            };
        }

        // String manipulation helper methods
        private string ExtractVNetIdFromSubnetId(string? subnetId)
        {
            if (string.IsNullOrEmpty(subnetId)) return string.Empty;
            
            var parts = subnetId.Split('/');
            if (parts.Length >= 9)
            {
                return string.Join('/', parts.Take(9));
            }
            return string.Empty;
        }

        private string ExtractLoadBalancerIdFromBackendPoolId(string? backendPoolId)
        {
            if (string.IsNullOrEmpty(backendPoolId)) return string.Empty;
            
            var parts = backendPoolId.Split('/');
            if (parts.Length >= 9)
            {
                return string.Join('/', parts.Take(9));
            }
            return string.Empty;
        }

        private string ExtractResourceNameFromAzureId(string? azureId)
        {
            if (string.IsNullOrEmpty(azureId)) return string.Empty;
            
            var parts = azureId.Split('/');
            return parts.Length > 0 ? parts[^1] : azureId;
        }

        private string ExtractKeyVaultNameFromUri(string? keyVaultUri)
        {
            if (string.IsNullOrEmpty(keyVaultUri)) return string.Empty;
            
            try
            {
                var uri = new Uri(keyVaultUri);
                return uri.Host.Split('.').FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string ExtractKeyVaultNameFromKeyId(string? keyId)
        {
            if (string.IsNullOrEmpty(keyId)) return string.Empty;
            
            try
            {
                var uri = new Uri(keyId);
                return uri.Host.Split('.').FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string ExtractStorageAccountNameFromVhdUri(string? vhdUri)
        {
            if (string.IsNullOrEmpty(vhdUri)) return string.Empty;
            
            try
            {
                var uri = new Uri(vhdUri);
                return uri.Host.Split('.').FirstOrDefault() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public class DependencyLevelCalculator
    {
        public Dictionary<string, int> CalculateLevels(List<AzureResource> resources)
        {
            var levels = new Dictionary<string, int>();
            var visited = new HashSet<string>();
            var visiting = new HashSet<string>();

            foreach (var resource in resources)
            {
                if (!visited.Contains(resource.Id))
                {
                    CalculateLevel(resource, resources, levels, visited, visiting, 0);
                }
            }

            return levels;
        }

        private int CalculateLevel(AzureResource resource, List<AzureResource> allResources, 
            Dictionary<string, int> levels, HashSet<string> visited, HashSet<string> visiting, int currentDepth)
        {
            if (visiting.Contains(resource.Id))
            {
                // Circular dependency detected, assign current depth
                return currentDepth;
            }

            if (levels.ContainsKey(resource.Id))
            {
                return levels[resource.Id];
            }

            visiting.Add(resource.Id);
            int maxDependencyLevel = 0;

            foreach (var dependency in resource.Dependencies)
            {
                var targetResource = allResources.FirstOrDefault(r => r.Id == dependency.TargetResourceId);
                if (targetResource != null)
                {
                    var dependencyLevel = CalculateLevel(targetResource, allResources, levels, visited, visiting, currentDepth + 1);
                    maxDependencyLevel = Math.Max(maxDependencyLevel, dependencyLevel + 1);
                }
            }

            visiting.Remove(resource.Id);
            visited.Add(resource.Id);
            levels[resource.Id] = maxDependencyLevel;

            return maxDependencyLevel;
        }
    }
}
