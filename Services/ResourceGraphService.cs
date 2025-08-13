using Azure.Identity;
using Azure.ResourceManager.ResourceGraph;
using Azure.ResourceManager.ResourceGraph.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Configuration;
using System.Text.Json;

namespace AzureDiscovery.Services
{
    public interface IResourceGraphService
    {
        Task<List<ResourceGraphResult>> DiscoverResourcesAsync(
            string subscriptionId, 
            List<string> resourceGroupFilters, 
            List<string> resourceTypeFilters);
    }

    public class ResourceGraphService : IResourceGraphService
    {
        private readonly ResourceGraphQueryClient _client;
        private readonly ILogger<ResourceGraphService> _logger;
        private readonly AzureDiscoveryOptions _options;

        public ResourceGraphService(ILogger<ResourceGraphService> logger, IOptions<AzureDiscoveryOptions> options)
        {
            _client = new ResourceGraphQueryClient(new DefaultAzureCredential());
            _logger = logger;
            _options = options.Value;
        }

        public async Task<List<ResourceGraphResult>> DiscoverResourcesAsync(
            string subscriptionId, 
            List<string> resourceGroupFilters, 
            List<string> resourceTypeFilters)
        {
            var allResources = new List<ResourceGraphResult>();
            var skip = 0;
            const int take = 1000; // Resource Graph API limit

            do
            {
                var query = BuildResourceQuery(subscriptionId, resourceGroupFilters, resourceTypeFilters, skip, take);
                
                _logger.LogDebug("Executing Resource Graph query: {Query}", query);

                var request = new ResourceQueryContent(query)
                {
                    Subscriptions = { subscriptionId }
                };

                var response = await _client.ResourcesAsync(request);
                var resources = ParseResourceGraphResponse(response.Value);
                
                allResources.AddRange(resources);
                
                _logger.LogInformation("Retrieved {Count} resources (total: {Total})", 
                    resources.Count, allResources.Count);

                if (resources.Count < take)
                    break;

                skip += take;
                
                // Add delay to respect rate limits
                await Task.Delay(_options.ResourceGraphDelayMs);
                
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
                var rgFilter = string.Join("', '", resourceGroupFilters);
                whereConditions.Add($"resourceGroup in ('{rgFilter}')");
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

            query += " | project id, name, type, resourceGroup, subscriptionId, location, kind, properties, tags";
            query += $" | skip {skip} | limit {take}";

            return query;
        }

        private List<ResourceGraphResult> ParseResourceGraphResponse(ResourceQueryResult response)
        {
            var resources = new List<ResourceGraphResult>();

            if (response.Data is JsonElement dataElement && dataElement.ValueKind == JsonValueKind.Array)
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

    public class ResourceGraphResult
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string? Kind { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new();
        public Dictionary<string, string>? Tags { get; set; }
    }
}
