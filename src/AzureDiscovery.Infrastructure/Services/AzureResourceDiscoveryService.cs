using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Infrastructure.Data;
using AzureDiscovery.Infrastructure.Configuration;
using System.Text.Json;
using AzureDiscovery.Core.Models.Messages;

namespace AzureDiscovery.Infrastructure.Services
{
    public class AzureResourceDiscoveryService : IDiscoveryService
    {
        private readonly DiscoveryDbContext _dbContext;
        private readonly IResourceGraphService _resourceGraphService;
        private readonly IServiceBusService _serviceBusService;
        private readonly IBlobStorageService _blobStorageService;
        private readonly IResourceDependencyAnalyzer _dependencyAnalyzer;
        private readonly ILogger<AzureResourceDiscoveryService> _logger;
        private readonly AzureDiscoveryOptions _options;

        public AzureResourceDiscoveryService(
            DiscoveryDbContext dbContext,
            IResourceGraphService resourceGraphService,
            IServiceBusService serviceBusService,
            IBlobStorageService blobStorageService,
            IResourceDependencyAnalyzer dependencyAnalyzer,
            ILogger<AzureResourceDiscoveryService> logger,
            IOptions<AzureDiscoveryOptions> options)
        {
            _dbContext = dbContext;
            _resourceGraphService = resourceGraphService;
            _serviceBusService = serviceBusService;
            _blobStorageService = blobStorageService;
            _dependencyAnalyzer = dependencyAnalyzer;
            _logger = logger;
            _options = options.Value;
        }

        public async Task<DiscoveryResult> StartDiscoveryAsync(DiscoveryRequest request)
        {
            _logger.LogInformation("Starting discovery session for subscription {SubscriptionId}", request.SourceSubscriptionId);

            var session = new DiscoverySession
            {
                Name = request.Name,
                ConnectionId = request.ConnectionId,
                SourceSubscriptionId = request.SourceSubscriptionId,
                TargetSubscriptionId = request.TargetSubscriptionId,
                ResourceGroupFilters = request.ResourceGroupFilters,
                ResourceTypeFilters = request.ResourceTypeFilters,
                Status = SessionStatus.InProgress
            };

            _dbContext.DiscoverySessions.Add(session);
            await _dbContext.SaveChangesAsync();

            try
            {
                // Start the discovery process
                var message = new StartDiscoveryMessage { SessionId = session.Id };
                await _serviceBusService.SendMessageAsync("resource-discovery-queue", message);

                _logger.LogInformation("Discovery session {SessionId} queued for background processing.", session.Id);

                //await ProcessDiscoveryAsync(session.Id);
                //var discoveredResources = await _dbContext.AzureResources.Where(r => r.Id.StartsWith(session.Id.ToString()))
                //                         .OrderBy(r => r.DependencyLevel).ToListAsync();
                return new DiscoveryResult
                {
                    Session = session,
                    DiscoveredResources = new List<AzureResource>() // Empty now, will be populated later
                };

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during discovery session {SessionId}", session.Id);
                session.Status = SessionStatus.Failed;
                session.ErrorMessage = ex.Message;
                session.CompletedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
                throw;
            }
        }

        public async Task<DiscoveryResult> GetExistingDiscovery(DiscoveryRequest request)
        {
            _logger.LogInformation("Start checking discovery session for subscription {SubscriptionId}", request.SourceSubscriptionId);

            var session = new DiscoverySession
            {
                Name = request.Name,
                ConnectionId = request.ConnectionId,
                SourceSubscriptionId = request.SourceSubscriptionId,
                TargetSubscriptionId = request.TargetSubscriptionId,
                ResourceGroupFilters = request.ResourceGroupFilters,
                ResourceTypeFilters = request.ResourceTypeFilters,
                Status = SessionStatus.InProgress
            };
            try
            {
                // Get latest discoveredId
                var discoveredId = _dbContext.AzureResources
                    .Where(r => r.ConnectionId == request.ConnectionId)
                    .OrderByDescending(x => x.DiscoveredAt)
                    .Select(g => g.Id)
                    .FirstOrDefault();

                if (!string.IsNullOrEmpty(discoveredId))
                {
                    var prefix = discoveredId;
                    var slashIndex = discoveredId.IndexOf('/');
                    if (slashIndex > 0)
                    {
                        prefix = discoveredId.Substring(0, slashIndex);
                    }

                    var discoveredResources = _dbContext.AzureResources.Where(x => x.Id.StartsWith(prefix)).ToList();
                    return new DiscoveryResult
                    {
                        Session = session,
                        DiscoveredResources = discoveredResources
                    };
                }
                return new DiscoveryResult
                {
                    Session = session,
                    DiscoveredResources = new List<AzureResource>()
                };

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during checking existing discovery session {SessionId}", session.Id);
                session.Status = SessionStatus.Failed;
                session.ErrorMessage = ex.Message;
                session.CompletedAt = DateTime.UtcNow;
                await _dbContext.SaveChangesAsync();
                throw;
            }
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
                .AsNoTracking()
                .Where(r => r.Id.StartsWith(sessionId.ToString()))
                .Include(r => r.Dependencies)
                .ThenInclude(d => d.TargetResource)
                .OrderBy(r => r.DependencyLevel)
                .ThenBy(r => r.Name)
                .ToListAsync();
            return resources;
        }

        public async Task ProcessDiscoveryAsync(Guid sessionId)
        {
            // Fetch the session from the database
            var session = await _dbContext.DiscoverySessions.FindAsync(sessionId);
            if (session == null)
            {
                _logger.LogError("ProcessDiscoveryAsync failed: Session {SessionId} not found.", sessionId);
                return;
            }
            _logger.LogInformation("Processing discovery for session {SessionId}", session.Id);

            // Phase 1: Discover resources using Resource Graph API
            var discoveredResources = await DiscoverResourcesAsync(session);

            session.TotalResourcesDiscovered = discoveredResources.Count;
            await _dbContext.SaveChangesAsync();

            // Phase 2: Process resources in batches
            await ProcessResourceBatchesAsync(session, discoveredResources);

            // Phase 3: Analyze dependencies
            await AnalyzeDependenciesAsync(session);

            // Phase 4: Calculate deployment order
            await CalculateDependencyLevelsAsync(session);

            // Phase 5: Generate ARM templates
            // await GenerateArmTemplatesAsync(session);

            // Complete the session
            session.Status = SessionStatus.Completed;
            session.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Completed discovery session {SessionId}. Discovered {ResourceCount} resources",
                session.Id, session.TotalResourcesDiscovered);
        }

        private async Task<List<ResourceGraphResult>> DiscoverResourcesAsync(DiscoverySession session)
        {
            _logger.LogInformation("Discovering resources for session {SessionId}", session.Id);

            var resources = await _resourceGraphService.DiscoverResourcesAsync(
                session.SourceSubscriptionId,
                session.ResourceGroupFilters,
                session.ResourceTypeFilters,
                session.Id);

            _logger.LogInformation("Discovered {Count} resources for session {SessionId}", resources.Count, session.Id);
            return resources;
        }

        private async Task ProcessResourceBatchesAsync(DiscoverySession session, List<ResourceGraphResult> discoveredResources)
        {
            _logger.LogInformation("Processing {Count} resources in batches for session {SessionId}",
                discoveredResources.Count, session.Id);

            var batchSize = _options.ProcessingBatchSize;
            var processedCount = 0;

            for (int i = 0; i < discoveredResources.Count; i += batchSize)
            {
                var batch = discoveredResources.Skip(i).Take(batchSize).ToList();
                await ProcessResourceBatchAsync(session, batch);

                processedCount += batch.Count;
                session.ResourcesProcessed = processedCount;
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("Processed {ProcessedCount}/{TotalCount} resources for session {SessionId}",
                    processedCount, discoveredResources.Count, session.Id);

                // Add delay to respect rate limits
                if (i + batchSize < discoveredResources.Count)
                {
                    await Task.Delay(_options.RetryDelayMs);
                }
            }
        }

        private async Task ProcessResourceBatchAsync(DiscoverySession session, List<ResourceGraphResult> resources)
        {
            var azureResources = new List<AzureResource>();

            foreach (var resource in resources)
            {
                try
                {
                    var azureResource = new AzureResource
                    {
                        //Id = $"{session.Id}_{ExtractResourceId(resource.Id)}",
                        Id = $"{session.Id}_{resource.Id}", // Use the full resource ID
                        Name = resource.Name,
                        Type = resource.Type,
                        ResourceGroup = resource.ResourceGroup,
                        Subscription = resource.SubscriptionId,
                        Location = resource.Location,
                        Kind = resource.Kind ?? string.Empty,
                        Properties = JsonDocument.Parse(JsonSerializer.Serialize(resource.Properties)),
                        Tags = JsonDocument.Parse(JsonSerializer.Serialize(resource.Tags ?? new Dictionary<string, string>())),
                        Status = ResourceStatus.Discovered,
                        ApiVersion = resource.ApiVersion,
                        ConnectionId = session.ConnectionId
                    };

                    azureResources.Add(azureResource);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process resource {ResourceId} in session {SessionId}",
                        resource.Id, session.Id);
                }
            }

            if (azureResources.Any())
            {
                _dbContext.AzureResources.AddRange(azureResources);
                await _dbContext.SaveChangesAsync();

                // comment this because No background service reading from the queue
                // Queue resources for detailed analysis
                //foreach (var resource in azureResources)
                //{
                //    await QueueResourceForAnalysisAsync(session.Id, resource.Id);
                //}
            }
        }

        private async Task AnalyzeDependenciesAsync(DiscoverySession session)
        {
            _logger.LogInformation("Analyzing dependencies for session {SessionId}", session.Id);

            var resources = await _dbContext.AzureResources
                .AsNoTracking()
                .Where(r => r.Id.StartsWith(session.Id.ToString()))
                .ToListAsync();

            var dependencies = await _dependencyAnalyzer.AnalyzeDependenciesAsync(resources);


            var distinctDependencies = dependencies
                .GroupBy(d => new
                {
                    Source = d.SourceResourceId.ToLowerInvariant(),
                    Target = d.TargetResourceId.ToLowerInvariant()
                })
                .Select(g => g.First())
                .ToList();

            if (distinctDependencies.Any())
            {
                _dbContext.ResourceDependencies.AddRange(distinctDependencies);
                await _dbContext.SaveChangesAsync();
            }

            // Update resource status
            foreach (var resource in resources)
            {
                resource.Status = ResourceStatus.Analyzed;
            }
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Analyzed {DependencyCount} dependencies for session {SessionId}",
                dependencies.Count, session.Id);
        }

        private async Task CalculateDependencyLevelsAsync(DiscoverySession session)
        {
            _logger.LogInformation("Calculating dependency levels for session {SessionId}", session.Id);

            var resources = await _dbContext.AzureResources
                .AsNoTracking()
                .Where(r => r.Id.StartsWith(session.Id.ToString()))
                .Include(r => r.Dependencies)
                .ToListAsync();
            //var resources = await _dbContext.AzureResources
            //    .Where(r => r.Id.StartsWith(session.Id.ToString()))
            //    .OrderBy(r => r.DependencyLevel)
            //    .ToListAsync();

            //var dependencies = await _dbContext.ResourceDependencies
            //    .Where(d => resources.Select(r => r.Id).Contains(d.SourceResourceId))
            //    .ToListAsync();

            //foreach (var resource in resources)
            //{
            //    resource.Dependencies = dependencies.Where(d => d.SourceResourceId == resource.Id).ToList();
            //}

            var levelCalculator = new DependencyLevelCalculator();
            var levels = levelCalculator.CalculateLevels(resources);

            foreach (var (resourceId, level) in levels)
            {
                var resource = resources.FirstOrDefault(r => r.Id == resourceId);
                if (resource != null)
                {
                    resource.DependencyLevel = level;
                }
            }

            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Calculated dependency levels for {ResourceCount} resources in session {SessionId}",
                resources.Count, session.Id);
        }

        private async Task GenerateArmTemplatesAsync(DiscoverySession session)
        {
            _logger.LogInformation("Generating ARM templates for session {SessionId}", session.Id);

            var resources = await _dbContext.AzureResources
                .AsNoTracking()
                .Where(r => r.Id.StartsWith(session.Id.ToString()))
                .Include(r => r.Dependencies)
                .OrderBy(r => r.DependencyLevel)
                .ToListAsync();


            var templateGenerator = new ArmTemplateGenerator(_logger);

            // Group resources by resource group for template generation
            var resourceGroups = resources.GroupBy(r => r.ResourceGroup);

            foreach (var rgGroup in resourceGroups)
            {
                try
                {
                    var template = await templateGenerator.GenerateTemplateAsync(rgGroup.ToList());
                    var templateName = $"{session.Id}_{rgGroup.Key}_template.json";

                    //await _blobStorageService.UploadTemplateAsync("arm-templates", templateName, template);

                    _logger.LogInformation("Generated ARM template for resource group {ResourceGroup} in session {SessionId}",
                        rgGroup.Key, session.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate ARM template for resource group {ResourceGroup} in session {SessionId}",
                        rgGroup.Key, session.Id);
                }
            }

            // Update resource status
            foreach (var resource in resources)
            {
                resource.Status = ResourceStatus.TemplateGenerated;
            }
            await _dbContext.SaveChangesAsync();
        }

        private string ExtractResourceId(string fullResourceId)
        {
            // Extract the resource name from the full Azure resource ID
            var parts = fullResourceId.Split('/');
            return parts.Length > 0 ? parts[^1] : fullResourceId;
        }
    }
}
