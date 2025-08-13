using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Models;
using AzureDiscovery.Data;
using AzureDiscovery.Configuration;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace AzureDiscovery.Services
{
    public interface IAzureResourceDiscoveryService
    {
        Task<DiscoverySession> StartDiscoveryAsync(DiscoveryRequest request);
        Task<DiscoverySession> GetDiscoveryStatusAsync(Guid sessionId);
        Task<List<AzureResource>> GetDiscoveredResourcesAsync(Guid sessionId);
    }

    public class AzureResourceDiscoveryService : IAzureResourceDiscoveryService
    {
        private readonly DiscoveryDbContext _dbContext;
        private readonly IResourceGraphService _resourceGraphService;
        private readonly IServiceBusService _serviceBusService;
        private readonly IBlobStorageService _blobStorageService;
        private readonly ILogger<AzureResourceDiscoveryService> _logger;
        private readonly AzureDiscoveryOptions _options;
        private readonly ArmClient _armClient;

        public AzureResourceDiscoveryService(
            DiscoveryDbContext dbContext,
            IResourceGraphService resourceGraphService,
            IServiceBusService serviceBusService,
            IBlobStorageService blobStorageService,
            ILogger<AzureResourceDiscoveryService> logger,
            IOptions<AzureDiscoveryOptions> options)
        {
            _dbContext = dbContext;
            _resourceGraphService = resourceGraphService;
            _serviceBusService = serviceBusService;
            _blobStorageService = blobStorageService;
            _logger = logger;
            _options = options.Value;
            _armClient = new ArmClient(new DefaultAzureCredential());
        }

        public async Task<DiscoverySession> StartDiscoveryAsync(DiscoveryRequest request)
        {
            var session = new DiscoverySession
            {
                Name = request.Name,
                SourceSubscriptionId = request.SourceSubscriptionId,
                TargetSubscriptionId = request.TargetSubscriptionId,
                ResourceGroupFilters = request.ResourceGroupFilters,
                ResourceTypeFilters = request.ResourceTypeFilters,
                Status = SessionStatus.InProgress
            };

            _dbContext.DiscoverySessions.Add(session);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Started discovery session {SessionId} for subscription {SubscriptionId}", 
                session.Id, request.SourceSubscriptionId);

            // Queue the discovery job
            await _serviceBusService.SendMessageAsync("discovery-queue", new DiscoveryJobMessage
            {
                SessionId = session.Id,
                SubscriptionId = request.SourceSubscriptionId,
                ResourceGroupFilters = request.ResourceGroupFilters,
                ResourceTypeFilters = request.ResourceTypeFilters
            });

            return session;
        }

        public async Task<DiscoverySession> GetDiscoveryStatusAsync(Guid sessionId)
        {
            var session = await _dbContext.DiscoverySessions.FindAsync(sessionId);
            if (session == null)
                throw new ArgumentException($"Discovery session {sessionId} not found");

            return session;
        }

        public async Task<List<AzureResource>> GetDiscoveredResourcesAsync(Guid sessionId)
        {
            var resources = await _dbContext.AzureResources
                .Where(r => r.Id.StartsWith(sessionId.ToString()))
                .Include(r => r.Dependencies)
                .ThenInclude(d => d.TargetResource)
                .OrderBy(r => r.DependencyLevel)
                .ThenBy(r => r.Name)
                .ToListAsync();

            return resources;
        }

        public async Task ProcessDiscoveryJobAsync(DiscoveryJobMessage message)
        {
            var session = await _dbContext.DiscoverySessions.FindAsync(message.SessionId);
            if (session == null) return;

            try
            {
                _logger.LogInformation("Processing discovery job for session {SessionId}", message.SessionId);

                // Discover resources using Resource Graph API
                var resources = await _resourceGraphService.DiscoverResourcesAsync(
                    message.SubscriptionId, 
                    message.ResourceGroupFilters, 
                    message.ResourceTypeFilters);

                session.TotalResourcesDiscovered = resources.Count;
                await _dbContext.SaveChangesAsync();

                // Process resources in batches to avoid overwhelming the system
                var batchSize = _options.ProcessingBatchSize;
                for (int i = 0; i < resources.Count; i += batchSize)
                {
                    var batch = resources.Skip(i).Take(batchSize).ToList();
                    await ProcessResourceBatchAsync(session.Id, batch);
                    
                    session.ResourcesProcessed += batch.Count;
                    await _dbContext.SaveChangesAsync();
                }

                // Analyze dependencies
                await AnalyzeDependenciesAsync(session.Id);

                session.Status = SessionStatus.Completed;
                session.CompletedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("Completed discovery session {SessionId}. Discovered {ResourceCount} resources", 
                    session.Id, session.TotalResourcesDiscovered);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing discovery job for session {SessionId}", message.SessionId);
                session.Status = SessionStatus.Failed;
                session.ErrorMessage = ex.Message;
                await _dbContext.SaveChangesAsync();
            }
        }

        private async Task ProcessResourceBatchAsync(Guid sessionId, List<ResourceGraphResult> resources)
        {
            var azureResources = new List<AzureResource>();

            foreach (var resource in resources)
            {
                var azureResource = new AzureResource
                {
                    Id = $"{sessionId}_{resource.Id}",
                    Name = resource.Name,
                    Type = resource.Type,
                    ResourceGroup = resource.ResourceGroup,
                    Subscription = resource.SubscriptionId,
                    Location = resource.Location,
                    Kind = resource.Kind ?? string.Empty,
                    Properties = JsonDocument.Parse(JsonSerializer.Serialize(resource.Properties)),
                    Tags = JsonDocument.Parse(JsonSerializer.Serialize(resource.Tags ?? new Dictionary<string, string>())),
                    Status = ResourceStatus.Discovered
                };

                azureResources.Add(azureResource);
            }

            _dbContext.AzureResources.AddRange(azureResources);
            await _dbContext.SaveChangesAsync();

            // Queue each resource for detailed analysis
            foreach (var resource in azureResources)
            {
                await _serviceBusService.SendMessageAsync("resource-analysis-queue", new ResourceAnalysisMessage
                {
                    SessionId = sessionId,
                    ResourceId = resource.Id
                });
            }
        }

        private async Task AnalyzeDependenciesAsync(Guid sessionId)
        {
            var resources = await _dbContext.AzureResources
                .Where(r => r.Id.StartsWith(sessionId.ToString()))
                .ToListAsync();

            var dependencyAnalyzer = new ResourceDependencyAnalyzer(_logger);
            var dependencies = await dependencyAnalyzer.AnalyzeDependenciesAsync(resources);

            _dbContext.ResourceDependencies.AddRange(dependencies);
            await _dbContext.SaveChangesAsync();

            // Calculate dependency levels for deployment ordering
            await CalculateDependencyLevelsAsync(sessionId);
        }

        private async Task CalculateDependencyLevelsAsync(Guid sessionId)
        {
            var resources = await _dbContext.AzureResources
                .Where(r => r.Id.StartsWith(sessionId.ToString()))
                .Include(r => r.Dependencies)
                .ToListAsync();

            var levelCalculator = new DependencyLevelCalculator();
            var levels = levelCalculator.CalculateLevels(resources);

            foreach (var (resourceId, level) in levels)
            {
                var resource = resources.First(r => r.Id == resourceId);
                resource.DependencyLevel = level;
            }

            await _dbContext.SaveChangesAsync();
        }
    }

    public class DiscoveryRequest
    {
        public string Name { get; set; } = string.Empty;
        public string SourceSubscriptionId { get; set; } = string.Empty;
        public string TargetSubscriptionId { get; set; } = string.Empty;
        public List<string> ResourceGroupFilters { get; set; } = new();
        public List<string> ResourceTypeFilters { get; set; } = new();
    }

    public class DiscoveryJobMessage
    {
        public Guid SessionId { get; set; }
        public string SubscriptionId { get; set; } = string.Empty;
        public List<string> ResourceGroupFilters { get; set; } = new();
        public List<string> ResourceTypeFilters { get; set; } = new();
    }

    public class ResourceAnalysisMessage
    {
        public Guid SessionId { get; set; }
        public string ResourceId { get; set; } = string.Empty;
    }
}
