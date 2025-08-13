using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Infrastructure.Configuration;

namespace AzureDiscovery.Infrastructure.Services
{
    public interface IAzureResourceManagerService
    {
        Task<ValidationResult> ValidateTemplateAsync(string subscriptionId, string resourceGroupName, string templateContent, string parametersContent, Guid discoverySessionId);
        Task<DeploymentResult> DeployTemplateAsync(string subscriptionId, string resourceGroupName, string deploymentName, string templateContent, string parametersContent, DeploymentMode mode, Guid discoverySessionId);
        Task<DeploymentResult> GetDeploymentStatusAsync(string subscriptionId, string resourceGroupName, string deploymentName, Guid discoverySessionId);
        Task<bool> CancelDeploymentAsync(string subscriptionId, string resourceGroupName, string deploymentName);
        Task<bool> EnsureResourceGroupExistsAsync(string subscriptionId, string resourceGroupName, string location, Guid discoverySessionId);
    }

    public class AzureResourceManagerService : IAzureResourceManagerService
    {
        private readonly HttpClient _httpClient;
        private readonly IAzureAuthenticationService _authService;
        private readonly ILogger<AzureResourceManagerService> _logger;
        private readonly AzureDiscoveryOptions _options;

        public AzureResourceManagerService(
            HttpClient httpClient,
            IAzureAuthenticationService authService,
            ILogger<AzureResourceManagerService> logger,
            IOptions<AzureDiscoveryOptions> options)
        {
            _httpClient = httpClient;
            _authService = authService;
            _logger = logger;
            _options = options.Value;
        }
        private ValidationResult PreValidateTemplate(string templateContent, string parametersContent)
        {
            var result = new ValidationResult { IsValid = true };

            try
            {
                var template = JsonSerializer.Deserialize<JsonElement>(templateContent);
                var parameters = JsonSerializer.Deserialize<JsonElement>(parametersContent);

                // Validate template structure
                if (!template.TryGetProperty("$schema", out _))
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "MissingSchema",
                        Message = "Template is missing required '$schema' property",
                        Target = "template"
                    });
                    result.IsValid = false;
                }

                if (!template.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "MissingResources",
                        Message = "Template is missing required 'resources' array",
                        Target = "template"
                    });
                    result.IsValid = false;
                }
                else
                {
                    // Validate storage account resources
                    foreach (var resource in resources.EnumerateArray())
                    {
                        if (resource.TryGetProperty("type", out var type) &&
                            type.GetString() == "Microsoft.Storage/storageAccounts")
                        {
                            ValidateStorageAccountResource(resource, result);
                        }
                    }
                }

                // Validate parameters structure
                if (parameters.ValueKind != JsonValueKind.Object)
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "InvalidParameters",
                        Message = "Parameters must be a JSON object",
                        Target = "parameters"
                    });
                    result.IsValid = false;
                }
            }
            catch (JsonException ex)
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "InvalidJson",
                    Message = $"Invalid JSON format: {ex.Message}",
                    Target = "template"
                });
                result.IsValid = false;
            }

            return result;
        }

        private void ValidateStorageAccountResource(JsonElement resource, ValidationResult result)
        {
            var resourceName = resource.TryGetProperty("name", out var name) ? name.GetString() : "unknown";

            if (resource.TryGetProperty("properties", out var properties))
            {
                // Check for invalid properties
                if (properties.TryGetProperty("primaryLocation", out _))
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "InvalidProperty",
                        Message = "The property 'primaryLocation' cannot be set in storage account template",
                        Target = resourceName
                    });
                    result.IsValid = false;
                }

                if (properties.TryGetProperty("provisioningState", out _))
                {
                    result.Errors.Add(new ValidationError
                    {
                        Code = "InvalidProperty",
                        Message = "The property 'provisioningState' is read-only and cannot be set",
                        Target = resourceName
                    });
                    result.IsValid = false;
                }

                // Check accessTier compatibility
                if (properties.TryGetProperty("accessTier", out var accessTier))
                {
                    var accessTierValue = accessTier.GetString();
                    if (resource.TryGetProperty("kind", out var kind))
                    {
                        var kindValue = kind.GetString();
                        if (kindValue != "StorageV2" && kindValue != "BlobStorage" && accessTierValue != null)
                        {
                            result.Errors.Add(new ValidationError
                            {
                                Code = "InvalidAccessTier",
                                Message = $"AccessTier '{accessTierValue}' is only supported for StorageV2 and BlobStorage accounts",
                                Target = resourceName
                            });
                            result.IsValid = false;
                        }
                    }
                }
            }

            // Check for missing sku
            if (!resource.TryGetProperty("sku", out var sku) || !sku.TryGetProperty("name", out _))
            {
                result.Errors.Add(new ValidationError
                {
                    Code = "MissingSku",
                    Message = "Storage account is missing required 'sku.name' property",
                    Target = resourceName
                });
                result.IsValid = false;
            }
        }
        public async Task<ValidationResult> ValidateTemplateAsync(string subscriptionId, string resourceGroupName, string templateContent, string parametersContent, Guid discoverySessionId)
        {
            _logger.LogInformation("Validating ARM template for resource group {ResourceGroup}", resourceGroupName);

            // Pre-validate template structure
            var preValidationResult = PreValidateTemplate(templateContent, parametersContent);
            if (!preValidationResult.IsValid)
            {
                _logger.LogWarning("Template pre-validation failed for resource group {ResourceGroup}", resourceGroupName);
                return preValidationResult;
            }

            try
            {
                var token = await _authService.GetAccessTokenWithClientSecretAsync(discoverySessionId);
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var requestBody = new
                {
                    properties = new
                    {
                        template = JsonSerializer.Deserialize<object>(templateContent),
                        parameters = JsonSerializer.Deserialize<object>(parametersContent),
                        mode = "Incremental"
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/validation-{Guid.NewGuid()}/validate?api-version=2021-04-01";

                var response = await _httpClient.PostAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Template validation successful for resource group {ResourceGroup}", resourceGroupName);
                    return new ValidationResult { IsValid = true };
                }
                else
                {
                    _logger.LogWarning("Template validation failed for resource group {ResourceGroup}: {StatusCode} - {Response}",
                        resourceGroupName, response.StatusCode, responseContent);
                    return ParseValidationErrors(responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating template for resource group {ResourceGroup}", resourceGroupName);
                return new ValidationResult
                {
                    IsValid = false,
                    Errors = new List<ValidationError>
            {
                new ValidationError
                {
                    Code = "ValidationException",
                    Message = ex.Message,
                    Target = resourceGroupName
                }
            }
                };
            }
        }

        public async Task<DeploymentResult> DeployTemplateAsync(string subscriptionId, string resourceGroupName, string deploymentName, string templateContent, string parametersContent, DeploymentMode mode, Guid discoverySessionId)
        {
            _logger.LogInformation("Deploying ARM template {DeploymentName} to resource group {ResourceGroup}", deploymentName, resourceGroupName);

            try
            {
                // Ensure resource group exists
                await EnsureResourceGroupExistsAsync(subscriptionId, resourceGroupName, "East US", discoverySessionId);

                var token = await _authService.GetAccessTokenWithClientSecretAsync(discoverySessionId);
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var requestBody = new
                {
                    properties = new
                    {
                        template = JsonSerializer.Deserialize<object>(templateContent),
                        parameters = JsonSerializer.Deserialize<object>(parametersContent),
                        mode = mode.ToString()
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/{deploymentName}?api-version=2021-04-01";

                var response = await _httpClient.PutAsync(url, content);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    // Monitor deployment progress
                    return await MonitorDeploymentAsync(subscriptionId, resourceGroupName, deploymentName, discoverySessionId);
                }
                else
                {
                    _logger.LogError("Deployment failed for {DeploymentName}: {StatusCode} - {Content}", deploymentName, response.StatusCode, responseContent);
                    return ParseDeploymentErrors(responseContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deploying template {DeploymentName} to resource group {ResourceGroup}", deploymentName, resourceGroupName);
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
                            Target = deploymentName
                        }
                    }
                };
            }
        }

        public async Task<DeploymentResult> GetDeploymentStatusAsync(string subscriptionId, string resourceGroupName, string deploymentName, Guid discoverySessionId)
        {
            try
            {
                var token = await _authService.GetAccessTokenWithClientSecretAsync(discoverySessionId);
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/{deploymentName}?api-version=2021-04-01";

                var response = await _httpClient.GetAsync(url);
                var responseContent = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    return ParseDeploymentStatus(responseContent);
                }
                else
                {
                    return new DeploymentResult
                    {
                        IsSuccessful = false,
                        State = DeploymentState.Failed,
                        Errors = new List<DeploymentError>
                        {
                            new DeploymentError
                            {
                                Code = "StatusCheckFailed",
                                Message = $"Failed to get deployment status: {response.StatusCode}",
                                Target = deploymentName
                            }
                        }
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deployment status for {DeploymentName}", deploymentName);
                return new DeploymentResult
                {
                    IsSuccessful = false,
                    State = DeploymentState.Failed,
                    Errors = new List<DeploymentError>
                    {
                        new DeploymentError
                        {
                            Code = "StatusException",
                            Message = ex.Message,
                            Target = deploymentName
                        }
                    }
                };
            }
        }

        public async Task<bool> CancelDeploymentAsync(string subscriptionId, string resourceGroupName, string deploymentName)
        {
            try
            {
                var token = await _authService.GetAccessTokenAsync(new[] { "https://management.azure.com/.default" });
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                var url = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}/providers/Microsoft.Resources/deployments/{deploymentName}/cancel?api-version=2021-04-01";

                var response = await _httpClient.PostAsync(url, null);
                
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully cancelled deployment {DeploymentName}", deploymentName);
                    return true;
                }
                else
                {
                    _logger.LogWarning("Failed to cancel deployment {DeploymentName}: {StatusCode}", deploymentName, response.StatusCode);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling deployment {DeploymentName}", deploymentName);
                return false;
            }
        }

        public async Task<bool> EnsureResourceGroupExistsAsync(string subscriptionId, string resourceGroupName, string location, Guid discoverySessionId)
        {
            try
            {
                var token = await _authService.GetAccessTokenWithClientSecretAsync(discoverySessionId);
                _httpClient.DefaultRequestHeaders.Clear();
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

                // Check if resource group exists
                var checkUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}?api-version=2021-04-01";
                var checkResponse = await _httpClient.GetAsync(checkUrl);

                if (checkResponse.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Resource group {ResourceGroup} already exists", resourceGroupName);
                    return true;
                }

                // Create resource group if it doesn't exist
                _logger.LogInformation("Creating resource group {ResourceGroup} in {Location}", resourceGroupName, location);

                var requestBody = new
                {
                    location = location,
                    tags = new Dictionary<string, string>
                    {
                        { "CreatedBy", "AzureDiscoveryEngine" },
                        { "CreatedAt", DateTime.UtcNow.ToString("yyyy-MM-dd") }
                    }
                };

                var json = JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var createUrl = $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroupName}?api-version=2021-04-01";
                var createResponse = await _httpClient.PutAsync(createUrl, content);

                if (createResponse.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Successfully created resource group {ResourceGroup}", resourceGroupName);
                    return true;
                }
                else
                {
                    var errorContent = await createResponse.Content.ReadAsStringAsync();
                    _logger.LogError("Failed to create resource group {ResourceGroup}: {StatusCode} - {Content}", 
                        resourceGroupName, createResponse.StatusCode, errorContent);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ensuring resource group {ResourceGroup} exists", resourceGroupName);
                return false;
            }
        }

        private async Task<DeploymentResult> MonitorDeploymentAsync(string subscriptionId, string resourceGroupName, string deploymentName, Guid discoverySessionId)
        {
            var maxAttempts = 60; // 30 minutes with 30-second intervals
            var attempt = 0;

            while (attempt < maxAttempts)
            {
                await Task.Delay(30000); // Wait 30 seconds between checks
                attempt++;

                var status = await GetDeploymentStatusAsync(subscriptionId, resourceGroupName, deploymentName,discoverySessionId);
                
                if (status.State == DeploymentState.Succeeded || status.State == DeploymentState.Failed || status.State == DeploymentState.Canceled)
                {
                    return status;
                }

                _logger.LogDebug("Deployment {DeploymentName} still running. Attempt {Attempt}/{MaxAttempts}", 
                    deploymentName, attempt, maxAttempts);
            }

            // Timeout
            return new DeploymentResult
            {
                IsSuccessful = false,
                State = DeploymentState.Failed,
                Errors = new List<DeploymentError>
                {
                    new DeploymentError
                    {
                        Code = "DeploymentTimeout",
                        Message = "Deployment monitoring timed out after 30 minutes",
                        Target = deploymentName
                    }
                }
            };
        }

        private ValidationResult ParseValidationErrors(string responseContent)
        {
            var result = new ValidationResult { IsValid = false };

            try
            {
                var errorResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

                if (errorResponse.TryGetProperty("error", out var error))
                {
                    // Parse main error
                    var mainError = new ValidationError
                    {
                        Code = error.GetProperty("code").GetString(),
                        Message = error.GetProperty("message").GetString(),
                        Target = ""
                    };

                    // Check for nested details
                    if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var detail in details.EnumerateArray())
                        {
                            ProcessErrorDetail(detail, result.Errors);
                        }
                    }
                    else
                    {
                        result.Errors.Add(mainError);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse validation errors: {ResponseContent}", responseContent);
                result.Errors.Add(new ValidationError
                {
                    Code = "ParseError",
                    Message = "Failed to parse validation response",
                    Target = ""
                });
            }

            return result;
        }
        private void ProcessErrorDetail(JsonElement detail, List<ValidationError> errors)
        {
            var error = new ValidationError
            {
                Code = detail.GetProperty("code").GetString(),
                Message = detail.GetProperty("message").GetString(),
                Target = detail.TryGetProperty("target", out var target) ? target.GetString() : ""
            };

            // Check for nested details (like in PreflightValidationCheckFailed)
            if (detail.TryGetProperty("details", out var nestedDetails) && nestedDetails.ValueKind == JsonValueKind.Array)
            {
                foreach (var nestedDetail in nestedDetails.EnumerateArray())
                {
                    ProcessErrorDetail(nestedDetail, errors);
                }
            }
            else
            {
                errors.Add(error);
            }
        }

        private DeploymentResult ParseDeploymentErrors(string responseContent)
        {
            var result = new DeploymentResult 
            { 
                IsSuccessful = false,
                State = DeploymentState.Failed
            };

            try
            {
                using var document = JsonDocument.Parse(responseContent);
                
                if (document.RootElement.TryGetProperty("error", out var errorElement))
                {
                    var error = new DeploymentError
                    {
                        Code = GetStringProperty(errorElement, "code"),
                        Message = GetStringProperty(errorElement, "message"),
                        Target = GetStringProperty(errorElement, "target")
                    };
                    result.Errors.Add(error);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse deployment errors from response");
                result.Errors.Add(new DeploymentError
                {
                    Code = "ParseError",
                    Message = "Failed to parse deployment response",
                    Details = responseContent
                });
            }

            return result;
        }

        private DeploymentResult ParseDeploymentStatus(string responseContent)
        {
            try
            {
                using var document = JsonDocument.Parse(responseContent);
                
                if (document.RootElement.TryGetProperty("properties", out var propertiesElement))
                {
                    var provisioningState = GetStringProperty(propertiesElement, "provisioningState");
                    var deploymentId = GetStringProperty(document.RootElement, "id");

                    var result = new DeploymentResult
                    {
                        DeploymentId = deploymentId,
                        State = ParseProvisioningState(provisioningState),
                        IsSuccessful = provisioningState.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
                    };

                    // Parse outputs if available
                    if (propertiesElement.TryGetProperty("outputs", out var outputsElement))
                    {
                        result.Outputs = JsonSerializer.Deserialize<Dictionary<string, object>>(outputsElement.GetRawText()) 
                            ?? new Dictionary<string, object>();
                    }

                    // Parse errors if deployment failed
                    if (propertiesElement.TryGetProperty("error", out var errorElement))
                    {
                        var error = new DeploymentError
                        {
                            Code = GetStringProperty(errorElement, "code"),
                            Message = GetStringProperty(errorElement, "message"),
                            Target = GetStringProperty(errorElement, "target")
                        };
                        result.Errors.Add(error);
                    }

                    return result;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse deployment status from response");
            }

            return new DeploymentResult
            {
                IsSuccessful = false,
                State = DeploymentState.Failed,
                Errors = new List<DeploymentError>
                {
                    new DeploymentError
                    {
                        Code = "ParseError",
                        Message = "Failed to parse deployment status",
                        Details = responseContent
                    }
                }
            };
        }

        private DeploymentState ParseProvisioningState(string provisioningState)
        {
            return provisioningState.ToLowerInvariant() switch
            {
                "succeeded" => DeploymentState.Succeeded,
                "failed" => DeploymentState.Failed,
                "canceled" => DeploymentState.Canceled,
                "running" => DeploymentState.Running,
                _ => DeploymentState.NotStarted
            };
        }

        private string GetStringProperty(JsonElement element, string propertyName)
        {
            return element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String 
                ? prop.GetString() ?? string.Empty 
                : string.Empty;
        }
    }
}
