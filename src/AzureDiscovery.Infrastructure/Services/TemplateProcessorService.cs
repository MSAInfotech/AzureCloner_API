using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Core.Models.Messages;
using AzureDiscovery.Infrastructure.Data;
using AzureDiscovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

public class TemplateProcessorService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TemplateProcessorService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _apiBaseUrl;
    public TemplateProcessorService(
        IServiceProvider serviceProvider,
        ILogger<TemplateProcessorService> logger,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _apiBaseUrl = configuration["EmailUrl"]
                       ?? throw new ArgumentNullException("ApiBaseUrl configuration is missing");
        _logger.LogInformation("TemplateProcessorService constructor called");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TemplateProcessorService is starting...");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var serviceBusService = scope.ServiceProvider.GetRequiredService<IServiceBusService>();

            _logger.LogInformation("Starting message processors...");

            var processorTasks = new List<Task>{
            // *** DISCOVERY WORKFLOW ***
            RegisterMessageProcessor<StartDiscoveryMessage>(
                serviceBusService,
                QueueNames.ResourceDiscovery,
                ProcessStartDiscoveryMessage,
                stoppingToken),

            // *** TEMPLATE DEPLOYMENT WORKFLOW ***
            RegisterMessageProcessor<TemplateCreatedMessage>(
                serviceBusService,
                QueueNames.TemplateCreated,
                ProcessTemplateCreatedMessage,
                stoppingToken),

            RegisterMessageProcessor<TemplateValidationMessage>(
                serviceBusService,
                QueueNames.TemplateValidation,
                ProcessTemplateValidationMessage,
                stoppingToken),

            RegisterMessageProcessor<TemplateDeploymentMessage>(
                serviceBusService,
                QueueNames.TemplateDeployment,
                ProcessTemplateDeploymentMessage,
                stoppingToken),

            RegisterMessageProcessor<TemplateDeploymentResultMessage>(
                serviceBusService,
                QueueNames.TemplateDeploymentResult,
                ProcessTemplateDeploymentResultMessage,
                stoppingToken)
        };

            _logger.LogInformation("All message processors started successfully");

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TemplateProcessorService cancellation requested");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Critical error in TemplateProcessorService ExecuteAsync");
            throw;
        }
        finally
        {
            _logger.LogInformation("TemplateProcessorService is stopping");
        }
    }
    private Task RegisterMessageProcessor<TMessage>(
        IServiceBusService serviceBusService,
        string queueName,
        Func<TMessage, Task> handler,
        CancellationToken cancellationToken)
    {
        return serviceBusService.StartProcessorAsync(queueName, handler, cancellationToken);
    }
    public static HttpRequestMessage CreateRequestWithTimeout(HttpMethod method, string url, TimeSpan timeout)
    {
        var request = new HttpRequestMessage(method, url);

        var context = new Polly.Context
        {
            ["Timeout"] = timeout
        };

        request.Options.Set(new HttpRequestOptionsKey<Polly.Context>("PolicyExecutionContext"), context);
        return request;
    }
    private async Task ProcessStartDiscoveryMessage(StartDiscoveryMessage message)
    {
        _logger.LogInformation("Received start discovery message for session: {SessionId}", message.SessionId);

        // Create a new scope for this message processing (important!)
        using var scope = _serviceProvider.CreateScope();
        // Resolve the discovery service
        var discoveryService = scope.ServiceProvider.GetRequiredService<IDiscoveryService>();

        try
        {
            // Cast to the concrete type to access the ProcessDiscoveryAsync method.
            if (discoveryService is AzureResourceDiscoveryService concreteService)
            {
                await concreteService.ProcessDiscoveryAsync(message.SessionId);
                _logger.LogInformation("Successfully processed discovery for session {SessionId}", message.SessionId);
            }
            else
            {
                _logger.LogError("Discovery service instance is not of expected type AzureResourceDiscoveryService. Cannot call ProcessDiscoveryAsync.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing discovery message for session {SessionId}", message.SessionId);
            throw;
        }
    }
    private async Task ProcessTemplateCreatedMessage(TemplateCreatedMessage message)
    {
        using var scope = _serviceProvider.CreateScope();
        var serviceBusService = scope.ServiceProvider.GetRequiredService<IServiceBusService>();

        try
        {
            _logger.LogInformation("Processing template created message for template {TemplateId}", message.TemplateId);

            var httpClient = _httpClientFactory.CreateClient("ResilientClient");
            var validationUrl = $"{_apiBaseUrl}/api/deployment/templates/{message.TemplateId}/validate?discoverySessionId={message.DiscoverySessionId}";

            var request = CreateRequestWithTimeout(HttpMethod.Post, validationUrl, TimeSpan.FromSeconds(20));
            var response = await httpClient.SendAsync(request);


            TemplateValidationMessage validationMessage;

            if (response.IsSuccessStatusCode)
            {
                var validationResultJson = await response.Content.ReadAsStringAsync();
                try
                {
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    var validationResult = JsonSerializer.Deserialize<ValidationResult>(validationResultJson, options);


                    validationMessage = new TemplateValidationMessage
                    {
                        TemplateId = message.TemplateId,
                        DeploymentSessionId = message.DeploymentSessionId,
                        DiscoverySessionId = message.DiscoverySessionId,
                        IsValid = validationResult?.IsValid ?? false,
                        ValidationResult = JsonSerializer.Serialize(validationResult),
                        ValidatedAt = DateTime.UtcNow
                    };

                    _logger.LogInformation("Template {TemplateId} validation completed. IsValid: {IsValid}",
                        message.TemplateId, validationResult?.IsValid);
                }
                catch (JsonException ex)
                {
                    validationMessage = new TemplateValidationMessage
                    {
                        TemplateId = message.TemplateId,
                        DeploymentSessionId = message.DeploymentSessionId,
                        DiscoverySessionId = message.DiscoverySessionId,
                        IsValid = false,
                        ValidationResult = $"Deserialization failed on successful API response: {ex.Message}. Raw response: {validationResultJson}",
                        ValidatedAt = DateTime.UtcNow
                    };

                    _logger.LogError(ex, "Failed to deserialize validation result for template {TemplateId} despite successful API response. Raw JSON: {Json}",
                        message.TemplateId, validationResultJson);
                }
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                validationMessage = new TemplateValidationMessage
                {
                    TemplateId = message.TemplateId,
                    DeploymentSessionId = message.DeploymentSessionId,
                    DiscoverySessionId = message.DiscoverySessionId,
                    IsValid = false,
                    ValidationResult = $"API call failed with status: {response.StatusCode}. Response: {errorContent}",
                    ValidatedAt = DateTime.UtcNow
                };

                _logger.LogError("Failed to validate template {TemplateId}. Status: {StatusCode}, Response: {Response}",
                    message.TemplateId, response.StatusCode, errorContent);
            }

            await serviceBusService.SendMessageAsync("template-validation-queue", validationMessage);
            _logger.LogInformation("Sent validation result message for template {TemplateId}", message.TemplateId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing template created message for template {TemplateId}", message.TemplateId);
            throw;
        }
    }

    private async Task ProcessTemplateValidationMessage(TemplateValidationMessage message)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        try
        {
            _logger.LogInformation("Processing template validation result for template {TemplateId}", message.TemplateId);

            var template = await dbContext.TemplateDeployments.FindAsync(message.TemplateId);
            if (template != null)
            {
                template.Status = message.IsValid ? TemplateStatus.ValidationPassed : TemplateStatus.ValidationFailed;
                template.ValidationResults = message.ValidationResult;

                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Updated template {TemplateId} status to {Status}", message.TemplateId, template.Status);

                if (message.IsValid)
                {
                    await TriggerNextWorkflowStep(message);
                }
            }
            else
            {
                _logger.LogWarning("Template {TemplateId} not found in database", message.TemplateId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing template validation message for template {TemplateId}", message.TemplateId);
            throw;
        }
    }

    private async Task TriggerNextWorkflowStep(TemplateValidationMessage message)
    {
        using var scope = _serviceProvider.CreateScope();
        var serviceBusService = scope.ServiceProvider.GetRequiredService<IServiceBusService>();

        _logger.LogInformation("Template {TemplateId} validated successfully. Triggering deployment.", message.TemplateId);

        var deploymentMessage = new TemplateDeploymentMessage
        {
            TemplateId = message.TemplateId,
            DeploymentSessionId = message.DeploymentSessionId,
            DiscoverySessionId = message.DiscoverySessionId,
            TemplateName = "",
            DependencyLevel = 0,
            RequestedAt = DateTime.UtcNow
        };

        await serviceBusService.SendMessageAsync("template-deployment-queue", deploymentMessage);
        _logger.LogInformation("Sent deployment message for template {TemplateId}", message.TemplateId);
    }

    private async Task ProcessTemplateDeploymentMessage(TemplateDeploymentMessage message)
    {
        using var scope = _serviceProvider.CreateScope();
        var serviceBusService = scope.ServiceProvider.GetRequiredService<IServiceBusService>();

        try
        {
            _logger.LogInformation("Processing template deployment message for template {TemplateId}", message.TemplateId);

            var httpClient = _httpClientFactory.CreateClient("ResilientClient");
            var deploymentUrl = $"{_apiBaseUrl}/api/deployment/templates/{message.TemplateId}/deploy?discoverySessionId={message.DiscoverySessionId}";

            var request = CreateRequestWithTimeout(HttpMethod.Post, deploymentUrl, TimeSpan.FromSeconds(90));
            var response = await httpClient.SendAsync(request);


            TemplateDeploymentResultMessage resultMessage;

            if (response.IsSuccessStatusCode)
            {
                var resultJson = await response.Content.ReadAsStringAsync();

                resultMessage = new TemplateDeploymentResultMessage
                {
                    TemplateId = message.TemplateId,
                    DeploymentSessionId = message.DeploymentSessionId,
                    DiscoverySessionId = message.DiscoverySessionId,
                    IsSuccess = true,
                    DeploymentResult = resultJson,
                    CompletedAt = DateTime.UtcNow
                };

                _logger.LogInformation("Template {TemplateId} deployment completed successfully", message.TemplateId);
            }
            else
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                resultMessage = new TemplateDeploymentResultMessage
                {
                    TemplateId = message.TemplateId,
                    DeploymentSessionId = message.DeploymentSessionId,
                    DiscoverySessionId = message.DiscoverySessionId,
                    IsSuccess = false,
                    DeploymentResult = $"API call failed with status: {response.StatusCode}. Response: {errorContent}",
                    CompletedAt = DateTime.UtcNow
                };

                _logger.LogError("Failed to deploy template {TemplateId}. Status: {StatusCode}, Response: {Response}",
                    message.TemplateId, response.StatusCode, errorContent);
            }

            await serviceBusService.SendMessageAsync("template-deployment-result-queue", resultMessage);
            _logger.LogInformation("Sent deployment result message for template {TemplateId}", message.TemplateId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing template deployment message for template {TemplateId}", message.TemplateId);
            throw;
        }
    }

    private async Task ProcessTemplateDeploymentResultMessage(TemplateDeploymentResultMessage message)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        try
        {
            _logger.LogInformation("Processing template deployment result for template {TemplateId}", message.TemplateId);

            var template = await dbContext.TemplateDeployments.FindAsync(message.TemplateId);
            if (template != null)
            {
                template.Status = message.IsSuccess ? TemplateStatus.Deployed : TemplateStatus.Failed;
                template.DeploymentResults = message.DeploymentResult;

                await dbContext.SaveChangesAsync();

                _logger.LogInformation("Updated template {TemplateId} status to {Status}", message.TemplateId, template.Status);

                await CheckDeploymentSessionCompletion(message.DeploymentSessionId);
            }
            else
            {
                _logger.LogWarning("Template {TemplateId} not found in database", message.TemplateId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing template deployment result message for template {TemplateId}", message.TemplateId);
            throw;
        }
    }

    private async Task CheckDeploymentSessionCompletion(Guid deploymentSessionId)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();

        var deploymentSession = await dbContext.DeploymentSessions
            .Include(ds => ds.TemplateDeployments)
            .FirstOrDefaultAsync(ds => ds.Id == deploymentSessionId);

        if (deploymentSession != null)
        {
            var allCompleted = deploymentSession.TemplateDeployments.All(t =>
                t.Status == TemplateStatus.Deployed || t.Status == TemplateStatus.Failed);

            if (allCompleted)
            {
                var allSuccessful = deploymentSession.TemplateDeployments.All(t => t.Status == TemplateStatus.Deployed);
                deploymentSession.Status = allSuccessful ? DeploymentStatus.Deployed : DeploymentStatus.Failed;

                await dbContext.SaveChangesAsync();
                _logger.LogInformation("Deployment session {SessionId} completed with status {Status}",
                    deploymentSessionId, deploymentSession.Status);
            }
        }
    }
}