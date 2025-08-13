//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Options;
//using AzureDiscovery.Core.Interfaces;
//using AzureDiscovery.Core.Models;
//using AzureDiscovery.Infrastructure.Data;
//using AzureDiscovery.Infrastructure.Configuration;
//using System.Text.Json;
//using System.Text;

//namespace AzureDiscovery.Infrastructure.Services
//{
//    public class TerraformAzureDeploymentService : IDeploymentService
//    {

//        private readonly DiscoveryDbContext _dbContext;
//        private readonly ITerraformResourceManagerService _terraformService;
//        private readonly IBlobStorageService _blobStorageService;
//        private readonly IServiceBusService _serviceBusService;
//        private readonly ILogger<TerraformAzureDeploymentService> _logger;
//        private readonly AzureDiscoveryOptions _options;

//        public TerraformAzureDeploymentService(
//            DiscoveryDbContext dbContext,
//            ITerraformResourceManagerService terraformService,
//            IBlobStorageService blobStorageService,
//            IServiceBusService serviceBusService,
//            ILogger<TerraformAzureDeploymentService> logger,
//            IOptions<AzureDiscoveryOptions> options)
//        {
//            _dbContext = dbContext;
//            _terraformService = terraformService;
//            _blobStorageService = blobStorageService;
//            _serviceBusService = serviceBusService;
//            _logger = logger;
//            _options = options.Value;
//        }

//        public async Task<DeploymentSession> CreateDeploymentSessionAsync(DeploymentRequest request)
//        {
//            _logger.LogInformation("Creating deployment session for discovery session {DiscoverySessionId}", request.DiscoverySessionId);

//            // Verify discovery session exists and is completed
//            var discoverySession = await _dbContext.DiscoverySessions.FindAsync(request.DiscoverySessionId);
//            if (discoverySession == null)
//                throw new ArgumentException($"Discovery session {request.DiscoverySessionId} not found");

//            if (discoverySession.Status != SessionStatus.Completed)
//                throw new InvalidOperationException($"Discovery session must be completed before deployment. Current status: {discoverySession.Status}");

//            var deploymentSession = new DeploymentSession
//            {
//                Name = request.Name,
//                DiscoverySessionId = request.DiscoverySessionId,
//                TargetSubscriptionId = request.TargetSubscriptionId,
//                TargetResourceGroup = request.TargetResourceGroup,
//                Mode = request.Mode,
//                Status = DeploymentStatus.Created
//            };

//            _dbContext.DeploymentSessions.Add(deploymentSession);
//            await _dbContext.SaveChangesAsync();

//            // Generate Terraform templates for deployment
//            await GenerateTerraformTemplateDeploymentsAsync(deploymentSession, request);

//            _logger.LogInformation("Created deployment session {SessionId} with {TemplateCount} templates",
//                deploymentSession.Id, deploymentSession.TotalTemplates);

//            return deploymentSession;
//        }

//        public async Task<DeploymentSession> GetDeploymentStatusAsync(Guid sessionId)
//        {
//            var session = await _dbContext.DeploymentSessions
//                .Include(d => d.DiscoverySession)
//                .Include(d => d.TemplateDeployments)
//                .FirstOrDefaultAsync(d => d.Id == sessionId);

//            if (session == null)
//                throw new ArgumentException($"Deployment session {sessionId} not found");

//            return session;
//        }

//        public async Task<List<TemplateDeployment>> GetTemplateDeploymentsAsync(Guid sessionId)
//        {
//            return await _dbContext.TemplateDeployments
//                .Where(t => t.DeploymentSessionId == sessionId)
//                .OrderBy(t => t.DependencyLevel)
//                .ThenBy(t => t.CreatedAt)
//                .ToListAsync();
//        }

//        public async Task<ValidationResult> ValidateTemplateAsync(Guid templateId)
//        {
//            var template = await _dbContext.TemplateDeployments.Include(t => t.DeploymentSession).FirstOrDefaultAsync(t => t.Id == templateId);
//            if (template == null)
//                throw new ArgumentException($"Template deployment {templateId} not found");

//            _logger.LogInformation("Validating Terraform template {TemplateId}: {TemplateName}", templateId, template.TemplateName);

//            var startTime = DateTime.UtcNow;
//            template.Status = TemplateStatus.Validating;
//            await _dbContext.SaveChangesAsync();

//            try
//            {
//                // Create a working directory for this template validation
//                var baseWorkingDir = _options.Terraform?.WorkingDirectoryRoot ?? Path.GetTempPath();
//                var workingDirectory = Path.Combine(baseWorkingDir, "terraform", template.Id.ToString());


//                var validationResult = await _terraformService.ValidateTemplateAsync(
//                    workingDirectory,
//                    template.TemplateContent,
//                    template.ParametersContent);

//                template.Status = validationResult.IsValid ? TemplateStatus.ValidationPassed : TemplateStatus.ValidationFailed;
//                template.ValidatedAt = DateTime.UtcNow;
//                template.ValidationResults = JsonSerializer.Serialize(validationResult);

//                await _dbContext.SaveChangesAsync();

//                // Clean up working directory
//                if (Directory.Exists(workingDirectory))
//                {
//                    Directory.Delete(workingDirectory, true);
//                }

//                validationResult.ValidationDuration = DateTime.UtcNow - startTime;
//                return validationResult;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Terraform template validation failed for {TemplateId}", templateId);

//                template.Status = TemplateStatus.ValidationFailed;
//                template.ErrorMessage = ex.Message;
//                await _dbContext.SaveChangesAsync();

//                return new ValidationResult
//                {
//                    IsValid = false,
//                    Errors = new List<ValidationError>
//                    {
//                        new ValidationError
//                        {
//                            Code = "ValidationException",
//                            Message = ex.Message,
//                            Target = template.TemplateName
//                        }
//                    },
//                    ValidationDuration = DateTime.UtcNow - startTime
//                };
//            }
//        }

//        public async Task<ValidationResult> ValidateAllTemplatesAsync(Guid sessionId)
//        {
//            var session = await GetDeploymentStatusAsync(sessionId);
//            var templates = await GetTemplateDeploymentsAsync(sessionId);

//            _logger.LogInformation("Validating all Terraform templates for deployment session {SessionId}", sessionId);

//            session.Status = DeploymentStatus.Validating;
//            await _dbContext.SaveChangesAsync();

//            var overallResult = new ValidationResult { IsValid = true };
//            var validationTasks = new List<Task<ValidationResult>>();

//            // Validate templates in parallel (with concurrency limit)
//            var semaphore = new SemaphoreSlim(_options.MaxConcurrentOperations);

//            foreach (var template in templates)
//            {
//                validationTasks.Add(ValidateTemplateWithSemaphore(template.Id, semaphore));
//            }

//            var results = await Task.WhenAll(validationTasks);

//            // Aggregate results
//            foreach (var result in results)
//            {
//                if (!result.IsValid)
//                    overallResult.IsValid = false;

//                overallResult.Errors.AddRange(result.Errors);
//                overallResult.Warnings.AddRange(result.Warnings);
//            }

//            // Update session status
//            session.Status = overallResult.IsValid ? DeploymentStatus.ValidationPassed : DeploymentStatus.ValidationFailed;
//            if (!overallResult.IsValid)
//            {
//                session.ErrorMessage = $"Validation failed with {overallResult.Errors.Count} errors";
//            }
//            await _dbContext.SaveChangesAsync();

//            _logger.LogInformation("Terraform template validation completed for session {SessionId}. Valid: {IsValid}, Errors: {ErrorCount}",
//                sessionId, overallResult.IsValid, overallResult.Errors.Count);

//            return overallResult;
//        }

//        public async Task<DeploymentResult> DeployTemplateAsync(Guid templateId)
//        {
//            var template = await _dbContext.TemplateDeployments
//                .Include(t => t.DeploymentSession)
//                .FirstOrDefaultAsync(t => t.Id == templateId);

//            if (template == null)
//                throw new ArgumentException($"Template deployment {templateId} not found");

//            _logger.LogInformation("Deploying Terraform template {TemplateId}: {TemplateName}", templateId, template.TemplateName);

//            var startTime = DateTime.UtcNow;
//            template.Status = TemplateStatus.Deploying;
//            await _dbContext.SaveChangesAsync();

//            try
//            {
//                // Create a working directory for this template deployment
//                var baseWorkingDir = _options.Terraform?.WorkingDirectoryRoot ?? Path.GetTempPath();
//                var workingDirectory = Path.Combine(baseWorkingDir, "terraform", template.Id.ToString());


//                var deploymentResult = await _terraformService.DeployTemplateAsync(
//                    workingDirectory,
//                    template.TemplateName,
//                    template.TemplateContent,
//                    template.ParametersContent,
//                    template.DeploymentSession.Mode);

//                template.Status = deploymentResult.IsSuccessful ? TemplateStatus.Deployed : TemplateStatus.Failed;
//                template.DeployedAt = DateTime.UtcNow;
//                template.DeploymentResults = JsonSerializer.Serialize(deploymentResult);

//                if (!deploymentResult.IsSuccessful)
//                {
//                    template.ErrorMessage = string.Join("; ", deploymentResult.Errors.Select(e => e.Message));
//                }

//                await _dbContext.SaveChangesAsync();

//                deploymentResult.DeploymentDuration = DateTime.UtcNow - startTime;
//                return deploymentResult;
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Terraform template deployment failed for {TemplateId}", templateId);

//                template.Status = TemplateStatus.Failed;
//                template.ErrorMessage = ex.Message;
//                await _dbContext.SaveChangesAsync();

//                return new DeploymentResult
//                {
//                    IsSuccessful = false,
//                    State = DeploymentState.Failed,
//                    Errors = new List<DeploymentError>
//                    {
//                        new DeploymentError
//                        {
//                            Code = "DeploymentException",
//                            Message = ex.Message,
//                            Target = template.TemplateName
//                        }
//                    },
//                    DeploymentDuration = DateTime.UtcNow - startTime
//                };
//            }
//        }

//        public async Task<DeploymentResult> DeployAllTemplatesAsync(Guid sessionId)
//        {
//            var session = await GetDeploymentStatusAsync(sessionId);
//            var templates = await GetTemplateDeploymentsAsync(sessionId);

//            _logger.LogInformation("Deploying all Terraform templates for deployment session {SessionId}", sessionId);

//            session.Status = DeploymentStatus.Deploying;
//            await _dbContext.SaveChangesAsync();

//            var overallResult = new DeploymentResult { IsSuccessful = true };
//            var deploymentResults = new List<DeploymentResult>();

//            // Deploy templates in dependency order (level by level)
//            var templatesByLevel = templates.GroupBy(t => t.DependencyLevel).OrderBy(g => g.Key);

//            foreach (var levelGroup in templatesByLevel)
//            {
//                _logger.LogInformation("Deploying dependency level {Level} with {Count} templates",
//                    levelGroup.Key, levelGroup.Count());

//                // For Terraform, we might want to deploy sequentially within a level to avoid state conflicts
//                // Unless each template has its own state backend configuration
//                foreach (var template in levelGroup.OrderBy(t => t.CreatedAt))
//                {
//                    var result = await DeployTemplateAsync(template.Id);
//                    deploymentResults.Add(result);

//                    if (!result.IsSuccessful)
//                    {
//                        _logger.LogError("Terraform deployment failed for template {TemplateId} at level {Level}. Stopping deployment.",
//                            template.Id, levelGroup.Key);
//                        overallResult.IsSuccessful = false;
//                        break;
//                    }

//                    // Add small delay between deployments to avoid conflicts
//                    await Task.Delay(1000);
//                }

//                if (!overallResult.IsSuccessful)
//                    break;

//                // Add delay between levels
//                if (levelGroup.Key < templatesByLevel.Max(g => g.Key))
//                {
//                    await Task.Delay(_options.RetryDelayMs);
//                }
//            }

//            // Aggregate results
//            foreach (var result in deploymentResults)
//            {
//                if (!result.IsSuccessful)
//                    overallResult.IsSuccessful = false;

//                overallResult.Errors.AddRange(result.Errors);

//                // Merge outputs
//                foreach (var output in result.Outputs)
//                {
//                    overallResult.Outputs[output.Key] = output.Value;
//                }
//            }

//            // Update session status
//            var successfulDeployments = deploymentResults.Count(r => r.IsSuccessful);
//            var failedDeployments = deploymentResults.Count(r => !r.IsSuccessful);

//            session.TemplatesDeployed = successfulDeployments;
//            session.TemplatesFailed = failedDeployments;
//            session.DeploymentResults = overallResult.Outputs;

//            if (overallResult.IsSuccessful)
//            {
//                session.Status = DeploymentStatus.Deployed;
//            }
//            else if (successfulDeployments > 0)
//            {
//                session.Status = DeploymentStatus.PartiallyDeployed;
//            }
//            else
//            {
//                session.Status = DeploymentStatus.Failed;
//            }

//            session.CompletedAt = DateTime.UtcNow;
//            await _dbContext.SaveChangesAsync();

//            _logger.LogInformation("Terraform deployment completed for session {SessionId}. Success: {SuccessCount}, Failed: {FailedCount}",
//                sessionId, successfulDeployments, failedDeployments);

//            return overallResult;
//        }

//        public async Task<DeploymentSession> CancelDeploymentAsync(Guid sessionId)
//        {
//            var session = await GetDeploymentStatusAsync(sessionId);

//            _logger.LogInformation("Cancelling Terraform deployment session {SessionId}", sessionId);

//            session.Status = DeploymentStatus.Cancelled;
//            session.CompletedAt = DateTime.UtcNow;

//            // Cancel any running template deployments using Terraform service
//            var runningTemplates = await _dbContext.TemplateDeployments
//                .Where(t => t.DeploymentSessionId == sessionId &&
//                           (t.Status == TemplateStatus.Deploying || t.Status == TemplateStatus.Queued))
//                .ToListAsync();

//            foreach (var template in runningTemplates)
//            {
//                try
//                {
//                    var workingDirectory = Path.Combine(Path.GetTempPath(), "terraform", template.Id.ToString());
//                    await _terraformService.CancelDeploymentAsync(workingDirectory, template.TemplateName);
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogWarning(ex, "Failed to cancel Terraform deployment for template {TemplateId}", template.Id);
//                }

//                template.Status = TemplateStatus.Skipped;
//                template.ErrorMessage = "Deployment cancelled by user";
//            }

//            await _dbContext.SaveChangesAsync();

//            return session;
//        }

//        private async Task<ValidationResult> ValidateTemplateWithSemaphore(Guid templateId, SemaphoreSlim semaphore)
//        {
//            await semaphore.WaitAsync();
//            try
//            {
//                return await ValidateTemplateAsync(templateId);
//            }
//            finally
//            {
//                semaphore.Release();
//            }
//        }

//        private async Task GenerateTerraformTemplateDeploymentsAsync(DeploymentSession session, DeploymentRequest request)
//        {
//            // Get resources from discovery session
//            var resources = await _dbContext.AzureResources
//                .Where(r => r.Id.StartsWith(session.DiscoverySessionId.ToString()))
//                .Include(r => r.Dependencies)
//                .OrderBy(r => r.DependencyLevel)
//                .ToListAsync();

//            // Group resources by resource group for template generation
//            var resourceGroups = resources.GroupBy(r => r.ResourceGroup);
//            var templateDeployments = new List<TemplateDeployment>();

//            foreach (var rgGroup in resourceGroups)
//            {
//                var templateName = $"{session.Name}_{rgGroup.Key}_{DateTime.UtcNow:yyyyMMddHHmmss}";
//                var resourceGroupResources = rgGroup.ToList();

//                // Generate Terraform template
//                var terraformTemplateGenerator = new TerraformTemplateGenerator(_logger);
//                var template = await terraformTemplateGenerator.GenerateTemplateAsync(resourceGroupResources);

//                // Generate Terraform variables (.tfvars format)
//                var variables = GenerateTerraformVariables(resourceGroupResources, request.Parameters);

//                // Calculate dependency level for this template (max level of resources in the group)
//                var dependencyLevel = resourceGroupResources.Max(r => r.DependencyLevel);

//                var templateDeployment = new TemplateDeployment
//                {
//                    DeploymentSessionId = session.Id,
//                    TemplateName = templateName,
//                    ResourceGroupName = string.IsNullOrEmpty(request.TargetResourceGroup) ? rgGroup.Key : request.TargetResourceGroup,
//                    TemplateContent = template, // Terraform HCL content as string
//                    ParametersContent = variables, // Terraform variables as string
//                    Status = TemplateStatus.Created,
//                    DependencyLevel = dependencyLevel
//                };

//                templateDeployments.Add(templateDeployment);

//                // Store Terraform files in blob storage for backup
//                await _blobStorageService.UploadTemplateAsync("deployment-templates", $"{templateName}.tf", template);
//                await _blobStorageService.UploadTemplateAsync("deployment-variables", $"{templateName}.tfvars", variables);
//            }

//            _dbContext.TemplateDeployments.AddRange(templateDeployments);
//            session.TotalTemplates = templateDeployments.Count;
//            await _dbContext.SaveChangesAsync();
//        }

//        private string GenerateTerraformVariables(List<AzureResource> resources, Dictionary<string, string> userParameters)
//        {
//            var variablesBuilder = new StringBuilder();
//            var addedVariables = new HashSet<string>();

//            // Add user-provided parameters first
//            foreach (var param in userParameters)
//            {
//                if (!addedVariables.Contains(param.Key.ToLower()))
//                {
//                    variablesBuilder.AppendLine($"{param.Key} = \"{param.Value}\"");
//                    addedVariables.Add(param.Key.ToLower());
//                }
//            }

//            // Add resource-specific variables (avoid duplicates)
//            foreach (var resource in resources)
//            {
//                var sanitizedName = SanitizeName(resource.Name);
//                var varName = $"{sanitizedName}_name";

//                if (!addedVariables.Contains(varName.ToLower()))
//                {
//                    variablesBuilder.AppendLine($"{varName} = \"{resource.Name}\"");
//                    addedVariables.Add(varName.ToLower());
//                }
//            }

//            // Add common variables if not already present
//            if (!addedVariables.Contains("location"))
//            {
//                var location = resources.FirstOrDefault()?.Location ?? "East US";
//                variablesBuilder.AppendLine($"location = \"{location}\"");
//                addedVariables.Add("location");
//            }

//            if (!addedVariables.Contains("resource_group_name"))
//            {
//                var resourceGroup = resources.FirstOrDefault()?.ResourceGroup ?? "default-rg";
//                variablesBuilder.AppendLine($"resource_group_name = \"{resourceGroup}\"");
//                addedVariables.Add("resource_group_name");
//            }

//            // Add tags if not already present
//            if (!addedVariables.Contains("tags"))
//            {
//                variablesBuilder.AppendLine("tags = {");
//                variablesBuilder.AppendLine("  Environment = \"Production\"");
//                variablesBuilder.AppendLine("  DeployedBy = \"AzureDiscovery\"");
//                variablesBuilder.AppendLine($"  DeploymentDate = \"{DateTime.UtcNow:yyyy-MM-dd}\"");
//                variablesBuilder.AppendLine("}");
//                addedVariables.Add("tags");
//            }

//            // Convert CRLF to LF for consistent line endings
//            return variablesBuilder.ToString().Replace("\r\n", "\n").Replace("\r", "\n");
//        }

//        private string SanitizeName(string name)
//        {
//            return System.Text.RegularExpressions.Regex.Replace(name, @"[^a-zA-Z0-9_]", "_").ToLower();
//        }

//        // Add method to destroy resources using Terraform
//    }
//}