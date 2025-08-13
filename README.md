# Azure Discovery Engine

A comprehensive .NET solution for discovering, analyzing, and cloning Azure resources across subscriptions and environments.

## Architecture

The solution is organized into the following projects:

- **AzureDiscovery.Core** - Domain models and interfaces
- **AzureDiscovery.Infrastructure** - Data access, Azure services, and external integrations
- **AzureDiscovery.Api** - REST API for discovery operations
- **AzureDiscovery.Worker** - Background services for processing discovery jobs
- **AzureDiscovery.Tests** - Unit and integration tests

## Features

- 🔍 **Resource Discovery** - Uses Azure Resource Graph API for efficient resource enumeration
- 🔗 **Dependency Analysis** - Automatically detects and maps resource dependencies
- 📊 **Progress Tracking** - Real-time monitoring of discovery sessions
- ⚡ **High Performance** - Parallel processing with configurable rate limiting
- 🛡️ **Security** - Azure Key Vault integration and Managed Identity support

## Prerequisites

- .NET 8.0 SDK
- SQL Server (LocalDB for development)
- Azure subscription with appropriate permissions
- Visual Studio 2022 or VS Code

## Getting Started

1. **Clone the repository**
   \`\`\`bash
   git clone <repository-url>
   cd AzureDiscovery
   \`\`\`

2. **Configure settings**
   Update `appsettings.json` in the API project with your Azure connection strings:
   \`\`\`json
   {
     "ConnectionStrings": {
       "DefaultConnection": "your-sql-connection-string"
     },
     "AzureDiscovery": {
       "ServiceBusConnectionString": "your-service-bus-connection",
       "StorageConnectionString": "your-storage-connection",
       "ApplicationInsightsConnectionString": "your-app-insights-connection",
       "KeyVaultUrl": "https://your-keyvault.vault.azure.net/"
     }
   }
   \`\`\`

3. **Build the solution**
   \`\`\`bash
   dotnet build
   \`\`\`

4. **Run database migrations**
   \`\`\`bash
   dotnet ef database update --project src/AzureDiscovery.Api
   \`\`\`

5. **Start the API**
   \`\`\`bash
   dotnet run --project src/AzureDiscovery.Api
   \`\`\`

6. **Start the Worker (optional)**
   \`\`\`bash
   dotnet run --project src/AzureDiscovery.Worker
   \`\`\`

## Usage

### Start a Discovery Session

\`\`\`bash
curl -X POST https://localhost:7000/api/discovery/start \
  -H "Content-Type: application/json" \
  -d '{
    "name": "Production Environment Discovery",
    "sourceSubscriptionId": "your-subscription-id",
    "targetSubscriptionId": "target-subscription-id",
    "resourceGroupFilters": ["rg-prod-*"],
    "resourceTypeFilters": ["Microsoft.Compute/virtualMachines"]
  }'
\`\`\`

### Check Discovery Status

\`\`\`bash
curl https://localhost:7000/api/discovery/{sessionId}/status
\`\`\`

### Get Discovered Resources

\`\`\`bash
curl https://localhost:7000/api/discovery/{sessionId}/resources
\`\`\`

## Configuration

Key configuration options in `appsettings.json`:

\`\`\`json
{
  "AzureDiscovery": {
    "ProcessingBatchSize": 50,
    "ResourceGraphDelayMs": 100,
    "MaxConcurrentOperations": 10,
    "RetryAttempts": 3,
    "ServiceRateLimits": {
      "ResourceGraph": 100,
      "ARM": 200,
      "Storage": 500
    }
  }
}
\`\`\`

## Testing

Run the test suite:

\`\`\`bash
dotnet test
\`\`\`

## Deployment

The solution includes Docker support for containerized deployment:

\`\`\`bash
# Build API container
docker build -f src/AzureDiscovery.Api/Dockerfile -t azure-discovery-api .

# Build Worker container
docker build -f src/AzureDiscovery.Worker/Dockerfile -t azure-discovery-worker .
\`\`\`

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Submit a pull request

## License

This project is licensed under the MIT License - see the LICENSE file for details.
