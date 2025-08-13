using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Infrastructure.Data;
using AzureDiscovery.Infrastructure.Configuration;
using System.Text.Json;
using System.Text;

namespace AzureDiscovery.Infrastructure.Services
{
    public class AzureDeploymentService : IDeploymentService
    {
        private readonly DiscoveryDbContext _dbContext;
        private readonly IAzureResourceManagerService _armService;
        private readonly IBlobStorageService _blobStorageService;
        private readonly IServiceBusService _serviceBusService;
        private readonly ILogger<AzureDeploymentService> _logger;
        private readonly AzureDiscoveryOptions _options;

        public AzureDeploymentService(
            DiscoveryDbContext dbContext,
            IAzureResourceManagerService armService,
            IBlobStorageService blobStorageService,
            IServiceBusService serviceBusService,
            ILogger<AzureDeploymentService> logger,
            IOptions<AzureDiscoveryOptions> options)
        {
            _dbContext = dbContext;
            _armService = armService;
            _blobStorageService = blobStorageService;
            _serviceBusService = serviceBusService;
            _logger = logger;
            _options = options.Value;
        }

        public async Task<DeploymentSession> CreateDeploymentSessionAsync(DeploymentRequest request)
        {
            _logger.LogInformation("Creating deployment session for discovery session {DiscoverySessionId}", request.DiscoverySessionId);

            // Verify discovery session exists and is completed
            var discoverySession = await _dbContext.DiscoverySessions.FindAsync(request.DiscoverySessionId);
            if (discoverySession == null)
                throw new ArgumentException($"Discovery session {request.DiscoverySessionId} not found");

            if (discoverySession.Status != SessionStatus.Completed)
                throw new InvalidOperationException($"Discovery session must be completed before deployment. Current status: {discoverySession.Status}");

            var deploymentSession = new DeploymentSession
            {
                Name = request.Name,
                DiscoverySessionId = request.DiscoverySessionId,
                TargetSubscriptionId = request.TargetSubscriptionId,
                TargetResourceGroup = request.TargetResourceGroup,
                Mode = request.Mode,
                Status = DeploymentStatus.Created
            };

            _dbContext.DeploymentSessions.Add(deploymentSession);
            await _dbContext.SaveChangesAsync();

            // Generate templates for deployment
            await GenerateTemplateDeploymentsAsync(deploymentSession, request);

            _logger.LogInformation("Created deployment session {SessionId} with {TemplateCount} templates",
                deploymentSession.Id, deploymentSession.TotalTemplates);

            return deploymentSession;
        }

        public async Task<DeploymentSession> GetDeploymentStatusAsync(Guid sessionId)
        {
            var session = await _dbContext.DeploymentSessions
                .Include(d => d.DiscoverySession)
                .Include(d => d.TemplateDeployments)
                .FirstOrDefaultAsync(d => d.Id == sessionId);

            if (session == null)
                throw new ArgumentException($"Deployment session {sessionId} not found");

            return session;
        }

        public async Task<List<TemplateDeployment>> GetTemplateDeploymentsAsync(Guid sessionId)
        {
            return await _dbContext.TemplateDeployments
                .Where(t => t.DeploymentSessionId == sessionId)
                .OrderBy(t => t.DependencyLevel)
                .ThenBy(t => t.CreatedAt)
                .ToListAsync();
        }

        public async Task<ValidationResult> ValidateTemplateAsync(Guid templateId,Guid discoverySessionId)
        {
            var template = await _dbContext.TemplateDeployments.Include(t => t.DeploymentSession).FirstOrDefaultAsync(t => t.Id == templateId);
            if (template == null)
                throw new ArgumentException($"Template deployment {templateId} not found");

            _logger.LogInformation("Validating template {TemplateId}: {TemplateName}", templateId, template.TemplateName);

            var startTime = DateTime.UtcNow;
            template.Status = TemplateStatus.Validating;
            await _dbContext.SaveChangesAsync();

            try
            {
                var validationResult = await _armService.ValidateTemplateAsync(
                    template.DeploymentSession.TargetSubscriptionId,
                    template.ResourceGroupName,
                    template.TemplateContent,
                    template.ParametersContent,
                    discoverySessionId);

                template.Status = validationResult.IsValid ? TemplateStatus.ValidationPassed : TemplateStatus.ValidationFailed;
                template.ValidatedAt = DateTime.UtcNow;
                template.ValidationResults = JsonSerializer.Serialize(validationResult);

                await _dbContext.SaveChangesAsync();

                validationResult.ValidationDuration = DateTime.UtcNow - startTime;
                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template validation failed for {TemplateId}", templateId);

                template.Status = TemplateStatus.ValidationFailed;
                template.ErrorMessage = ex.Message;
                await _dbContext.SaveChangesAsync();

                return new ValidationResult
                {
                    IsValid = false,
                    Errors = new List<ValidationError>
                    {
                        new ValidationError
                        {
                            Code = "ValidationException",
                            Message = ex.Message,
                            Target = template.TemplateName
                        }
                    },
                    ValidationDuration = DateTime.UtcNow - startTime
                };
            }
        }

        public async Task<ValidationResult> ValidateAllTemplatesAsync(Guid sessionId, Guid discoverySessionId)
        {
            var session = await GetDeploymentStatusAsync(sessionId);
            var templates = await GetTemplateDeploymentsAsync(sessionId);

            _logger.LogInformation("Validating all templates for deployment session {SessionId}", sessionId);

            session.Status = DeploymentStatus.Validating;
            await _dbContext.SaveChangesAsync();

            var overallResult = new ValidationResult { IsValid = true };

            foreach (var template in templates)
            {
                // Ensure this method does not internally use parallel DbContext calls either
                var result = await ValidateTemplateAsync(template.Id, discoverySessionId);

                if (!result.IsValid)
                    overallResult.IsValid = false;

                overallResult.Errors.AddRange(result.Errors);
                overallResult.Warnings.AddRange(result.Warnings);
            }

            session.Status = overallResult.IsValid ? DeploymentStatus.ValidationPassed : DeploymentStatus.ValidationFailed;

            if (!overallResult.IsValid)
            {
                session.ErrorMessage = $"Validation failed with {overallResult.Errors.Count} errors";
            }

            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Template validation completed for session {SessionId}. Valid: {IsValid}, Errors: {ErrorCount}",
                sessionId, overallResult.IsValid, overallResult.Errors.Count);

            return overallResult;
        }


        public async Task<DeploymentResult> DeployTemplateAsync(Guid templateId,Guid discoverySessionId)
        {
            var template = await _dbContext.TemplateDeployments
                .Include(t => t.DeploymentSession)
                .FirstOrDefaultAsync(t => t.Id == templateId);

            if (template == null)
                throw new ArgumentException($"Template deployment {templateId} not found");

            _logger.LogInformation("Deploying template {TemplateId}: {TemplateName}", templateId, template.TemplateName);

            var startTime = DateTime.UtcNow;
            template.Status = TemplateStatus.Deploying;
            await _dbContext.SaveChangesAsync();

            try
            {
                var deploymentResult = await _armService.DeployTemplateAsync(
                    template.DeploymentSession.TargetSubscriptionId,
                    template.ResourceGroupName,
                    template.TemplateName,
                    template.TemplateContent,
                    template.ParametersContent,
                    template.DeploymentSession.Mode,
                    discoverySessionId);

                template.Status = deploymentResult.IsSuccessful ? TemplateStatus.Deployed : TemplateStatus.Failed;
                template.DeployedAt = DateTime.UtcNow;
                template.DeploymentResults = JsonSerializer.Serialize(deploymentResult);

                if (!deploymentResult.IsSuccessful)
                {
                    template.ErrorMessage = string.Join("; ", deploymentResult.Errors.Select(e => e.Message));
                }

                await _dbContext.SaveChangesAsync();

                deploymentResult.DeploymentDuration = DateTime.UtcNow - startTime;
                return deploymentResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Template deployment failed for {TemplateId}", templateId);

                template.Status = TemplateStatus.Failed;
                template.ErrorMessage = ex.Message;
                await _dbContext.SaveChangesAsync();

                return new DeploymentResult
                {
                    IsSuccessful = false,
                    State = DeploymentState.Failed,
                    Errors = new List<DeploymentError>
                    {
                        new DeploymentError
                        {
                            Code = "DeploymentException",
                            Message = ex.Message,
                            Target = template.TemplateName
                        }
                    },
                    DeploymentDuration = DateTime.UtcNow - startTime
                };
            }
        }

        public async Task<DeploymentResult> DeployAllTemplatesAsync(Guid sessionId,Guid discoverySessionId)
        {
            var session = await GetDeploymentStatusAsync(sessionId);
            var templates = await GetTemplateDeploymentsAsync(sessionId);

            _logger.LogInformation("Deploying all templates for deployment session {SessionId}", sessionId);

            session.Status = DeploymentStatus.Deploying;
            await _dbContext.SaveChangesAsync();

            var overallResult = new DeploymentResult { IsSuccessful = true };
            var deploymentResults = new List<DeploymentResult>();

            // Deploy templates in dependency order (level by level)
            var templatesByLevel = templates.GroupBy(t => t.DependencyLevel).OrderBy(g => g.Key);

            foreach (var levelGroup in templatesByLevel)
            {
                _logger.LogInformation("Deploying dependency level {Level} with {Count} templates",
                    levelGroup.Key, levelGroup.Count());

                foreach (var template in levelGroup)
                {
                    var result = await DeployTemplateAsync(template.Id, discoverySessionId);
                    deploymentResults.Add(result);

                    if (!result.IsSuccessful)
                    {
                        _logger.LogError("Deployment failed for template {TemplateId} at level {Level}. Stopping deployment.",
                            template.Id, levelGroup.Key);
                        overallResult.IsSuccessful = false;
                        break;
                    }
                }

                if (!overallResult.IsSuccessful)
                {
                    break;
                }

                // Add delay between levels to avoid throttling
                if (levelGroup.Key < templatesByLevel.Max(g => g.Key))
                {
                    await Task.Delay(_options.RetryDelayMs);
                }
            }

            // Aggregate results
            foreach (var result in deploymentResults)
            {
                if (!result.IsSuccessful)
                    overallResult.IsSuccessful = false;

                overallResult.Errors.AddRange(result.Errors);

                foreach (var output in result.Outputs)
                {
                    overallResult.Outputs[output.Key] = output.Value;
                }
            }

            // Update session status
            var successfulDeployments = deploymentResults.Count(r => r.IsSuccessful);
            var failedDeployments = deploymentResults.Count(r => !r.IsSuccessful);

            session.TemplatesDeployed = successfulDeployments;
            session.TemplatesFailed = failedDeployments;
            session.DeploymentResults = overallResult.Outputs;

            if (overallResult.IsSuccessful)
            {
                session.Status = DeploymentStatus.Deployed;
            }
            else if (successfulDeployments > 0)
            {
                session.Status = DeploymentStatus.PartiallyDeployed;
            }
            else
            {
                session.Status = DeploymentStatus.Failed;
            }

            session.CompletedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Deployment completed for session {SessionId}. Success: {SuccessCount}, Failed: {FailedCount}",
                sessionId, successfulDeployments, failedDeployments);

            return overallResult;
        }

        public async Task<DeploymentSession> CancelDeploymentAsync(Guid sessionId)
        {
            var session = await GetDeploymentStatusAsync(sessionId);

            _logger.LogInformation("Cancelling deployment session {SessionId}", sessionId);

            session.Status = DeploymentStatus.Cancelled;
            session.CompletedAt = DateTime.UtcNow;

            // Cancel any running template deployments
            var runningTemplates = await _dbContext.TemplateDeployments
                .Where(t => t.DeploymentSessionId == sessionId &&
                           (t.Status == TemplateStatus.Deploying || t.Status == TemplateStatus.Queued))
                .ToListAsync();

            foreach (var template in runningTemplates)
            {
                template.Status = TemplateStatus.Skipped;
                template.ErrorMessage = "Deployment cancelled by user";
            }

            await _dbContext.SaveChangesAsync();

            return session;
        }

        //private async Task<ValidationResult> ValidateTemplateWithSemaphore(Guid templateId, SemaphoreSlim semaphore)
        //{
        //    await semaphore.WaitAsync();
        //    try
        //    {
        //        return await ValidateTemplateAsync(templateId);
        //    }
        //    finally
        //    {
        //        semaphore.Release();
        //    }
        //}

        private async Task GenerateTemplateDeploymentsAsync(DeploymentSession session, DeploymentRequest request)
        {
            // Get resources from discovery session
            var resources = await _dbContext.AzureResources
                .Where(r => r.Id.StartsWith(session.DiscoverySessionId.ToString()))
                .Include(r => r.Dependencies)
                .OrderBy(r => r.DependencyLevel)
                .ToListAsync();

            // Group resources by resource group for template generation
            var resourceGroups = resources.GroupBy(r => r.ResourceGroup);
            var templateDeployments = new List<TemplateDeployment>();

            foreach (var rgGroup in resourceGroups)
            {
                var templateName = $"{session.Name}_{rgGroup.Key}_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var resourceGroupResources = rgGroup.ToList();

                // Generate ARM template
                var templateGenerator = new ArmTemplateGenerator(_logger);

                var template = await templateGenerator.GenerateTemplateAsync(resourceGroupResources);
                //var parameters = GenerateParameters(resourceGroupResources, request.Parameters);

                // Calculate dependency level for this template (max level of resources in the group)
                var dependencyLevel = resourceGroupResources.Max(r => r.DependencyLevel);

                var templateDeployment = new TemplateDeployment
                {
                    DeploymentSessionId = session.Id,
                    TemplateName = templateName,
                    ResourceGroupName = string.IsNullOrEmpty(request.TargetResourceGroup) ? rgGroup.Key : request.TargetResourceGroup,
                    TemplateContent = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true }),
                    //ParametersContent = JsonSerializer.Serialize(parameters, new JsonSerializerOptions { WriteIndented = true }),
                    ParametersContent = JsonSerializer.Serialize(new { }),
                    Status = TemplateStatus.Created,
                    DependencyLevel = dependencyLevel
                };

                templateDeployments.Add(templateDeployment);

                // Store template in blob storage for backup
                //await _blobStorageService.UploadTemplateAsync("deployment-templates", $"{templateName}.json", template);
                //await _blobStorageService.UploadJsonAsync("deployment-parameters", $"{templateName}.parameters.json", parameters);
                //await _blobStorageService.UploadJsonAsync("deployment-parameters",$"{templateName}.parameters.json",new { });

            }

            _dbContext.TemplateDeployments.AddRange(templateDeployments);
            session.TotalTemplates = templateDeployments.Count;
            await _dbContext.SaveChangesAsync();
        }

        //private object GenerateParameters(List<AzureResource> resources, Dictionary<string, string> userParameters)
        //{
        //    var parameters = new Dictionary<string, object>();

        //    // Add user-provided parameters
        //    foreach (var param in userParameters)
        //    {
        //        parameters[param.Key] = new { value = param.Value };
        //    }

        //    // Add resource-specific parameters
        //    foreach (var resource in resources)
        //    {
        //        var paramName = $"{SanitizeName(resource.Name)}Name";
        //        if (!parameters.ContainsKey(paramName))
        //        {
        //            parameters[paramName] = new { value = resource.Name };
        //        }
        //    }

        //    // Add common parameters
        //    if (!parameters.ContainsKey("location"))
        //    {
        //        parameters["location"] = new { value = resources.FirstOrDefault()?.Location ?? "East US" };
        //    }

        //    return parameters;
        //}

        private string SanitizeName(string name)
        {
            return System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9]", "");
        }
    }
}
