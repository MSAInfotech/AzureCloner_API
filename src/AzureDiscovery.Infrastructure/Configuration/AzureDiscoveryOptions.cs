using AzureDiscovery.Core.Models;

namespace AzureDiscovery.Infrastructure.Configuration
{
    public class AzureDiscoveryOptions
    {
        public const string SectionName = "AzureDiscovery";

        public string ServiceBusConnectionString { get; set; } = string.Empty;
        public string StorageConnectionString { get; set; } = string.Empty;
        public string CosmosDbConnectionString { get; set; } = string.Empty;
        public string CosmosDbDatabaseName { get; set; } = "AzureDiscovery";
        public string ApplicationInsightsConnectionString { get; set; } = string.Empty;
        public string KeyVaultUrl { get; set; } = string.Empty;
        
        public int ProcessingBatchSize { get; set; } = 50;
        public int ResourceGraphDelayMs { get; set; } = 100;
        public int MaxConcurrentOperations { get; set; } = 10;
        public int RetryAttempts { get; set; } = 3;
        public int RetryDelayMs { get; set; } = 1000;
        public TerraformOptions Terraform { get; set; } = new();
        public Dictionary<string, int> ServiceRateLimits { get; set; } = new()
        {
            { "ResourceGraph", 100 },
            { "ARM", 200 },
            { "Storage", 500 }
        };
    }
}
