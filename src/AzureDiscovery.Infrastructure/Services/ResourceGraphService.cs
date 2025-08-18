using System.Text;
using System.Text.Json;
using Azure.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Infrastructure.Configuration;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;

namespace AzureDiscovery.Infrastructure.Services
{
    public class ResourceGraphService : IResourceGraphService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<ResourceGraphService> _logger;
        private readonly IAzureAuthenticationService _authService;
        private readonly AzureDiscoveryOptions _options;
        private readonly Dictionary<string, JsonElement> _providerCache = new();


        public ResourceGraphService(
            HttpClient httpClient,
            ILogger<ResourceGraphService> logger,
            IAzureAuthenticationService authService,
            IOptions<AzureDiscoveryOptions> options)
        {
            _httpClient = httpClient;
            _logger = logger;
            _authService = authService;
            _options = options.Value;
        }

        public async Task<List<ResourceGraphResult>> DiscoverResourcesAsync(
            string subscriptionId, 
            List<string> resourceGroupFilters, 
            List<string> resourceTypeFilters,
            Guid sessionId)
        {
            var allResources = new List<ResourceGraphResult>();
            var skip = 0;
            const int take = 1000;

            do
            {
                var query = BuildResourceQuery(subscriptionId, resourceGroupFilters, resourceTypeFilters, skip, take);
                
                _logger.LogDebug("Executing Resource Graph query: {Query}", query);

                try
                {
                    var requestBody = new
                    {
                        subscriptions = new[] { subscriptionId },
                        query = query
                    };

                    // Get access token using the authentication service
                    var token = await _authService.GetAccessTokenWithClientSecretAsync(sessionId);

                    _httpClient.DefaultRequestHeaders.Clear();
                    _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                    var json = JsonSerializer.Serialize(requestBody);
                    var content = new StringContent(json, Encoding.UTF8, "application/json");

                    var response = await _httpClient.PostAsync(
                        "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2021-03-01", 
                        content);

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError("Resource Graph API error: {StatusCode} - {Content}", 
                            response.StatusCode, errorContent);
                        
                        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                        {
                            throw new UnauthorizedAccessException("Authentication failed. Please check your credentials and permissions.");
                        }
                        
                        break;
                    }

                    var responseContent = await response.Content.ReadAsStringAsync();
                    var resources = ParseResourceGraphResponse(responseContent);
                    foreach (var resource in resources)
                    {
                        try
                        {
                            if (string.IsNullOrWhiteSpace(resource.Type))
                                continue;

                            var typeParts = resource.Type.Split('/', 2);
                            if (typeParts.Length != 2)
                                continue;

                            var providerNamespace = typeParts[0];
                            var resourceType = typeParts[1];

                            var apiVersion = await GetApiVersion(
                                subscriptionId,
                                providerNamespace,
                                resourceType,
                                resource.Location,
                                token);

                            resource.ApiVersion = apiVersion;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to assign API version to resource: {ResourceId}", resource.Id);
                        }
                    }

                    allResources.AddRange(resources);
                    
                    _logger.LogInformation("Retrieved {Count} resources (total: {Total})", 
                        resources.Count, allResources.Count);

                    if (resources.Count < take)
                        break;

                    skip += take;
                    
                    await Task.Delay(_options.ResourceGraphDelayMs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error executing Resource Graph query");
                    throw;
                }
                
            } while (true);

            return allResources;
        }
        private async Task<string?> GetApiVersion(
            string subscriptionId,
            string providerNamespace,
            string resourceType,
            string location,
            string token)
        {
            try
            {
                if (!_providerCache.TryGetValue(providerNamespace, out JsonElement providerData))
                {
                    var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/{providerNamespace}?api-version=2021-04-01";

                    _httpClient.DefaultRequestHeaders.Clear();
                    _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                    var response = await _httpClient.GetAsync(url);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Failed to get provider metadata for {Provider}: {StatusCode}", providerNamespace, response.StatusCode);
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    providerData = doc.RootElement.Clone();
                    _providerCache[providerNamespace] = providerData;
                }

                if (providerData.TryGetProperty("resourceTypes", out var resourceTypesElement))
                {
                    foreach (var rt in resourceTypesElement.EnumerateArray())
                    {
                        var typeName = rt.GetProperty("resourceType").GetString();
                        if (!typeName.Equals(resourceType, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // Region check
                        var supportedLocations = rt.TryGetProperty("locations", out var locElement)
                            ? locElement.EnumerateArray().Select(l => NormalizeLocation(l.GetString())).ToHashSet()
                            : new HashSet<string>();

                        var normalizedRequestedLocation = NormalizeLocation(location);
                        bool isSupportedInRegion = supportedLocations.Contains(normalizedRequestedLocation);

                        if (!isSupportedInRegion)
                        {
                            _logger.LogWarning("Resource type {Provider}/{Type} is not supported in region {Location}", providerNamespace, resourceType, location);
                            return null;
                        }

                        var versions = rt.GetProperty("apiVersions")
                                         .EnumerateArray()
                                         .Select(x => x.GetString())
                                         .Where(x => !string.IsNullOrWhiteSpace(x))
                                         .ToList();

                        var stableVersion = versions.FirstOrDefault(v => !v.Contains("preview", StringComparison.OrdinalIgnoreCase));
                        return stableVersion ?? versions.FirstOrDefault();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error retrieving API version for {Provider}/{Type} in region {Location}", providerNamespace, resourceType, location);
                return null;
            }
        }
        private string NormalizeLocation(string? location)
        {
            return location?.Replace(" ", "").ToLowerInvariant() ?? string.Empty;
        }

        private string BuildResourceQuery(
            string subscriptionId, 
            List<string> resourceGroupFilters, 
            List<string> resourceTypeFilters, 
            int skip, 
            int take)
        {
            var query = "Resources";

            var whereConditions = new List<string>();

            if (resourceGroupFilters.Any())
            {
                var conditions = new List<string>();
                foreach (var filter in resourceGroupFilters)
                {
                    if (filter.Contains("*"))
                    {
                        var prefix = filter.Replace("*", "");
                        conditions.Add($"resourceGroup startswith '{prefix}'");
                    }
                    else
                    {
                        conditions.Add($"resourceGroup == '{filter}'");
                    }
                }
                whereConditions.Add($"({string.Join(" or ", conditions)})");
            }

            if (resourceTypeFilters.Any())
            {
                var typeFilter = string.Join("', '", resourceTypeFilters);
                whereConditions.Add($"type in ('{typeFilter}')");
            }

            if (whereConditions.Any())
            {
                query += $" | where {string.Join(" and ", whereConditions)}";
            }

            query += " | project id, name, type, resourceGroup, subscriptionId, location, kind, sku, identity, plan, properties, tags";
            query += $" | limit {take}";

            return query;
        }

        private List<ResourceGraphResult> ParseResourceGraphResponse(string responseContent)
        {
            var resources = new List<ResourceGraphResult>();

            try
            {
                using var document = JsonDocument.Parse(responseContent);

                var root = document.RootElement;
                if (root.TryGetProperty("data", out var dataElement) &&
                    dataElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in dataElement.EnumerateArray())
                    {
                        try
                        {
                            var resource = new ResourceGraphResult
                            {
                                Id = GetStringProperty(item, "id"),
                                Name = GetStringProperty(item, "name"),
                                Type = GetStringProperty(item, "type"),
                                ResourceGroup = GetStringProperty(item, "resourceGroup"),
                                SubscriptionId = GetStringProperty(item, "subscriptionId"),
                                Location = GetStringProperty(item, "location"),
                                Kind = GetStringProperty(item, "kind"),
                                Sku = item.TryGetProperty("sku", out var skuProp) && skuProp.ValueKind != JsonValueKind.Null
                                      ? JsonSerializer.Deserialize<Sku>(skuProp.GetRawText())
                                      : null,
                                Identity = item.TryGetProperty("identity", out var identityProp) && identityProp.ValueKind != JsonValueKind.Null
                                      ? JsonSerializer.Deserialize<Identity>(identityProp.GetRawText()): null,
                                Plan = item.TryGetProperty("plan", out var planProp) && planProp.ValueKind != JsonValueKind.Null
                                      ? JsonSerializer.Deserialize<Plan>(planProp.GetRawText()): null,
                                Properties = GetObjectProperty(item, "properties"),
                                Tags = GetDictionaryProperty(item, "tags")
                                //hear also have to add API version if needed
                            };

                            resources.Add(resource);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse resource from Resource Graph response");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse Resource Graph response: {Content}", responseContent);
            }

            return resources;
        }

        private string GetStringProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String 
                ? prop.GetString() ?? string.Empty 
                : string.Empty;
        }

        private Dictionary<string, object> GetObjectProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(prop.GetRawText()) 
                    ?? new Dictionary<string, object>();
            }
            return new Dictionary<string, object>();
        }

        private Dictionary<string, string> GetDictionaryProperty(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(prop.GetRawText()) 
                    ?? new Dictionary<string, string>();
            }
            return new Dictionary<string, string>();
        }
    }
}
