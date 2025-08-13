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
