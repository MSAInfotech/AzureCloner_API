using System.Text;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Infrastructure.Configuration;

namespace AzureDiscovery.Infrastructure.Services
{
    public interface ITerraformResourceManagerService
    {
        Task<ValidationResult> ValidateTemplateAsync(string workingDirectory, string templateContent, string variablesContent);
        Task<DeploymentResult> DeployTemplateAsync(string workingDirectory, string deploymentName, string templateContent, string variablesContent, DeploymentMode mode);
        Task<DeploymentResult> GetDeploymentStatusAsync(string workingDirectory, string deploymentName);
        Task<bool> CancelDeploymentAsync(string workingDirectory, string deploymentName);
        Task<bool> InitializeWorkspaceAsync(string workingDirectory);
        Task<string> PlanDeploymentAsync(string workingDirectory, string planFile);
        Task<bool> DestroyResourcesAsync(string workingDirectory);
    }

    public class TerraformResourceManagerService : ITerraformResourceManagerService
    {
        private readonly ILogger<TerraformResourceManagerService> _logger;
        private readonly AzureDiscoveryOptions _options;
        private readonly string _terraformExecutablePath;
        private readonly AzureAuthenticationOptions _authOptions;

        public TerraformResourceManagerService(
            ILogger<TerraformResourceManagerService> logger,
            IOptions<AzureDiscoveryOptions> options,
            IOptions<AzureAuthenticationOptions> authOptions)
        {
            _logger = logger;
            _options = options.Value;
            _authOptions = authOptions.Value;
            _terraformExecutablePath = GetTerraformExecutablePath();

            // Log the terraform path being used for debugging
            _logger.LogInformation("Using Terraform executable path: {TerraformPath}", _terraformExecutablePath);

            // Log authentication info for debugging (without secrets)
            _logger.LogInformation("Terraform will use ClientId: {ClientId}, TenantId: {TenantId}, SubscriptionId: {SubscriptionId}",
                _authOptions.ClientId, _authOptions.TenantId, _authOptions.subscriptionId);
        }

        private string GetTerraformExecutablePath()
        {
            // First, check if a specific path is configured
            if (!string.IsNullOrWhiteSpace(_options.Terraform?.ExecutablePath))
            {
                var configuredPath = _options.Terraform.ExecutablePath;

                // If it's a full path and the file exists, use it
                if (Path.IsPathFullyQualified(configuredPath) && File.Exists(configuredPath))
                {
                    _logger.LogInformation("Using configured Terraform path: {Path}", configuredPath);
                    return configuredPath;
                }

                // If it's just "terraform" or relative path, continue with PATH search
                if (configuredPath == "terraform" || !Path.IsPathFullyQualified(configuredPath))
                {
                    _logger.LogInformation("Configured path is relative, searching in PATH");
                }
                else
                {
                    _logger.LogWarning("Configured Terraform path does not exist: {Path}", configuredPath);
                }
            }

            // Try to find terraform in PATH
            var pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (pathVariable != null)
            {
                var paths = pathVariable.Split(Path.PathSeparator);
                foreach (var path in paths)
                {
                    try
                    {
                        var terraformPath = Path.Combine(path, "terraform");
                        var terraformExePath = Path.Combine(path, "terraform.exe");

                        if (File.Exists(terraformPath))
                        {
                            _logger.LogInformation("Found Terraform in PATH: {Path}", terraformPath);
                            return terraformPath;
                        }
                        if (File.Exists(terraformExePath))
                        {
                            _logger.LogInformation("Found Terraform in PATH: {Path}", terraformExePath);
                            return terraformExePath;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Skip invalid paths
                        _logger.LogDebug("Error checking path {Path}: {Error}", path, ex.Message);
                    }
                }
            }

            // If configured path exists but wasn't found above, try it anyway
            if (!string.IsNullOrWhiteSpace(_options.Terraform?.ExecutablePath))
            {
                _logger.LogWarning("Using configured path even though validation failed: {Path}",
                    _options.Terraform.ExecutablePath);
                return _options.Terraform.ExecutablePath;
            }

            // Default to just "terraform" and hope it's in PATH
            _logger.LogWarning("Terraform executable not found, using default 'terraform' command");
            return "terraform";
        }

        public async Task<ValidationResult> ValidateTemplateAsync(string workingDirectory, string templateContent, string variablesContent)
        {
            _logger.LogInformation("Validating Terraform template in directory {WorkingDirectory}", workingDirectory);

            var result = new ValidationResult { IsValid = true };

            try
            {
                // Check if Terraform is available before proceeding
                if (!await IsTerraformAvailableAsync())
                {
                    return new ValidationResult
                    {
                        IsValid = false,
                        Errors = new List<ValidationError>
                    {
                        new ValidationError
                        {
                            Code = "TerraformNotFound",
                            Message = $"Terraform executable not found at: {_terraformExecutablePath}. Please ensure Terraform is installed and the path is correct.",
                            Target = "terraform"
                        }
                    }
                    };
                }

                // Use configured working directory root if available
                var baseWorkingDir = _options.Terraform?.WorkingDirectoryRoot ?? Path.GetTempPath();
                var fullWorkingDirectory = Path.Combine(baseWorkingDir, "terraform", Path.GetFileName(workingDirectory));

                // Ensure working directory exists
                Directory.CreateDirectory(fullWorkingDirectory);

                // Write template files
                await WriteTemplateFilesAsync(fullWorkingDirectory, templateContent, variablesContent);

                // Pre-validate template syntax
                var syntaxValidation = await ValidateSyntaxAsync(fullWorkingDirectory);
                if (!syntaxValidation.IsValid)
                {
                    return syntaxValidation;
                }

                // Initialize terraform -- run that with init 
                var initResult = await InitializeWorkspaceAsync(fullWorkingDirectory);
                if (!initResult)
                {
                    result.IsValid = false;
                    result.Errors.Add(new ValidationError
                    {
                        Code = "InitializationFailed",
                        Message = "Failed to initialize Terraform workspace",
                        Target = fullWorkingDirectory
                    });
                    return result;
                }

                // Run terraform validate
                var validateResult = await RunTerraformCommandAsync(fullWorkingDirectory, "validate", "-json");
                if (validateResult.ExitCode != 0)
                {
                    result.IsValid = false;
                    result.Errors.AddRange(ParseTerraformValidationErrors(validateResult.Output));
                }

                _logger.LogInformation("Terraform template validation completed for directory {WorkingDirectory}", fullWorkingDirectory);
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating Terraform template in directory {WorkingDirectory}", workingDirectory);
                return new ValidationResult
                {
                    IsValid = false,
                    Errors = new List<ValidationError>
                {
                    new ValidationError
                    {
                        Code = "ValidationException",
                        Message = ex.Message,
                        Target = workingDirectory
                    }
                }
                };
            }
        }

        // Add this method to check if Terraform is available
        public async Task<bool> IsTerraformAvailableAsync()
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _terraformExecutablePath,
                    Arguments = "version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(processStartInfo);
                if (process == null)
                {
                    _logger.LogError("Failed to start Terraform process with path: {Path}", _terraformExecutablePath);
                    return false;
                }

                await process.WaitForExitAsync();

                if (process.ExitCode == 0)
                {
                    var output = await process.StandardOutput.ReadToEndAsync();
                    _logger.LogInformation("Terraform version check successful: {Output}", output.Trim());
                    return true;
                }
                else
                {
                    var error = await process.StandardError.ReadToEndAsync();
                    _logger.LogError("Terraform version check failed with exit code {ExitCode}: {Error}",
                        process.ExitCode, error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception checking Terraform availability with path: {Path}", _terraformExecutablePath);
                return false;
            }
        }

        // SINGLE, CONSOLIDATED RunTerraformCommandAsync method
        private async Task<TerraformCommandResult> RunTerraformCommandAsync(string workingDirectory, params string[] arguments)
        {
            try
            {
                var processStartInfo = new ProcessStartInfo
                {
                    FileName = _terraformExecutablePath,
                    Arguments = string.Join(" ", arguments),
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // IMPORTANT: Clear ALL existing Azure environment variables that might interfere
                var azureEnvVars = new[] {
                    "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AZURE_TENANT_ID", "AZURE_SUBSCRIPTION_ID",
                    "ARM_CLIENT_ID", "ARM_CLIENT_SECRET", "ARM_TENANT_ID", "ARM_SUBSCRIPTION_ID",
                    "ARM_USE_CLI", "ARM_USE_MSI", "ARM_USE_AZUREAD", "ARM_ENVIRONMENT"
                };

                foreach (var envVar in azureEnvVars)
                {
                    processStartInfo.EnvironmentVariables.Remove(envVar);
                }

                // Set the CORRECT ARM environment variables for Terraform using YOUR configured values
                processStartInfo.EnvironmentVariables["ARM_CLIENT_ID"] = _authOptions.ClientId;
                processStartInfo.EnvironmentVariables["ARM_CLIENT_SECRET"] = _authOptions.ClientSecret;
                processStartInfo.EnvironmentVariables["ARM_TENANT_ID"] = _authOptions.TenantId;
                processStartInfo.EnvironmentVariables["ARM_SUBSCRIPTION_ID"] = _authOptions.subscriptionId;

                // Disable ALL other authentication methods to force service principal auth
                processStartInfo.EnvironmentVariables["ARM_USE_CLI"] = "false";
                processStartInfo.EnvironmentVariables["ARM_USE_MSI"] = "false";
                processStartInfo.EnvironmentVariables["ARM_USE_AZUREAD"] = "false";
                processStartInfo.EnvironmentVariables["ARM_ENVIRONMENT"] = "public";

                // Add additional debugging
                //_logger.LogDebug("Running Terraform command: {FileName} {Arguments} in {WorkingDirectory}",
                //    _terraformExecutablePath, processStartInfo.Arguments, workingDirectory);
                //_logger.LogDebug("Using ARM_CLIENT_ID: {ClientId}", _authOptions.ClientId);
                //_logger.LogDebug("Using ARM_TENANT_ID: {TenantId}", _authOptions.TenantId);
                //_logger.LogDebug("Using ARM_SUBSCRIPTION_ID: {SubscriptionId}", _authOptions.subscriptionId);

                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();

                using var process = new Process { StartInfo = processStartInfo };

                process.OutputDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        outputBuilder.AppendLine(e.Data);
                        _logger.LogDebug("Terraform stdout: {Data}", e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (e.Data != null)
                    {
                        errorBuilder.AppendLine(e.Data);
                        _logger.LogDebug("Terraform stderr: {Data}", e.Data);
                    }
                };

                process.Start();

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                var output = outputBuilder.ToString();
                var error = errorBuilder.ToString();
                var combinedOutput = string.IsNullOrEmpty(error) ? output : $"{output}\n{error}";

                _logger.LogDebug("Terraform command completed with exit code {ExitCode}", process.ExitCode);

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning("Terraform command failed with exit code {ExitCode}: {Error}", 
                        process.ExitCode, combinedOutput);
                }

                return new TerraformCommandResult
                {
                    ExitCode = process.ExitCode,
                    Output = combinedOutput
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error running Terraform command '{Command}' in directory {WorkingDirectory}",
                    string.Join(" ", arguments), workingDirectory);
                throw;
            }
        }

        public async Task<DeploymentResult> DeployTemplateAsync(string workingDirectory, string deploymentName, string templateContent, string variablesContent, DeploymentMode mode)
        {
            _logger.LogInformation("Deploying Terraform template {DeploymentName} in directory {WorkingDirectory}", deploymentName, workingDirectory);

            try
            {
                // Ensure working directory exists
                Directory.CreateDirectory(workingDirectory);

                // Write template files
                await WriteTemplateFilesAsync(workingDirectory, templateContent, variablesContent);

                // Initialize terraform
                var initResult = await InitializeWorkspaceAsync(workingDirectory);
                if (!initResult)
                {
                    return new DeploymentResult
                    {
                        IsSuccessful = false,
                        State = DeploymentState.Failed,
                        Errors = new List<DeploymentError>
                        {
                            new DeploymentError
                            {
                                Code = "InitializationFailed",
                                Message = "Failed to initialize Terraform workspace",
                                Target = deploymentName
                            }
                        }
                    };
                }

                // Create execution plan
                var planFile = Path.Combine(workingDirectory, $"{deploymentName}.tfplan");

                var planResult = await RunTerraformCommandAsync(workingDirectory, "plan", "-out=" + planFile);

                if (planResult.ExitCode != 0)
                {
                    return new DeploymentResult
                    {
                        IsSuccessful = false,
                        State = DeploymentState.Failed,
                        Errors = ParseTerraformDeploymentErrors(planResult.Output)
                    };
                }

                // Apply the plan
                var applyResult = await RunTerraformCommandAsync(workingDirectory, "apply", "-auto-approve", planFile);

                if (applyResult.ExitCode == 0)
                {
                    return new DeploymentResult
                    {
                        IsSuccessful = true,
                        State = DeploymentState.Succeeded,
                        DeploymentId = deploymentName,
                        Outputs = await GetTerraformOutputsAsync(workingDirectory)
                    };
                }
                else
                {
                    return new DeploymentResult
                    {
                        IsSuccessful = false,
                        State = DeploymentState.Failed,
                        Errors = ParseTerraformDeploymentErrors(applyResult.Output)
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deploying Terraform template {DeploymentName}", deploymentName);
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

        public async Task<DeploymentResult> GetDeploymentStatusAsync(string workingDirectory, string deploymentName)
        {
            try
            {
                // Check if state file exists
                var stateFile = Path.Combine(workingDirectory, "terraform.tfstate");
                if (!File.Exists(stateFile))
                {
                    return new DeploymentResult
                    {
                        IsSuccessful = false,
                        State = DeploymentState.NotStarted,
                        DeploymentId = deploymentName
                    };
                }

                // Get terraform state
                var stateResult = await RunTerraformCommandAsync(workingDirectory, "show", "-json");

                if (stateResult.ExitCode == 0)
                {
                    var stateData = JsonSerializer.Deserialize<JsonElement>(stateResult.Output);
                    var hasResources = stateData.TryGetProperty("values", out var values) &&
                                     values.TryGetProperty("root_module", out var rootModule) &&
                                     rootModule.TryGetProperty("resources", out var resources) &&
                                     resources.GetArrayLength() > 0;

                    return new DeploymentResult
                    {
                        IsSuccessful = hasResources,
                        State = hasResources ? DeploymentState.Succeeded : DeploymentState.NotStarted,
                        DeploymentId = deploymentName,
                        Outputs = await GetTerraformOutputsAsync(workingDirectory)
                    };
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
                                Message = "Failed to get Terraform state",
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

        public async Task<bool> CancelDeploymentAsync(string workingDirectory, string deploymentName)
        {
            // Terraform doesn't have a direct cancel operation like ARM
            // We would need to implement this by terminating any running terraform processes
            try
            {
                var processes = Process.GetProcessesByName("terraform");
                foreach (var process in processes)
                {
                    try
                    {
                        if (IsProcessInDirectory(process, workingDirectory))
                        {
                            process.Kill();
                            _logger.LogInformation("Cancelled Terraform process for deployment {DeploymentName}", deploymentName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to kill Terraform process {ProcessId}", process.Id);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling deployment {DeploymentName}", deploymentName);
                return false;
            }
        }

        public async Task<bool> InitializeWorkspaceAsync(string workingDirectory)
        {
            try
            {
                var result = await RunTerraformCommandAsync(workingDirectory, "init");
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing Terraform workspace in {WorkingDirectory}", workingDirectory);
                return false;
            }
        }

        public async Task<string> PlanDeploymentAsync(string workingDirectory, string planFile)
        {
            try
            {
                var result = await RunTerraformCommandAsync(workingDirectory, "plan", "-out=" + planFile);
                return result.Output;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Terraform plan in {WorkingDirectory}", workingDirectory);
                throw;
            }
        }

        public async Task<bool> DestroyResourcesAsync(string workingDirectory)
        {
            try
            {
                var result = await RunTerraformCommandAsync(workingDirectory, "destroy", "-auto-approve");
                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error destroying Terraform resources in {WorkingDirectory}", workingDirectory);
                return false;
            }
        }

        private async Task WriteTemplateFilesAsync(string workingDirectory, string templateContent, string variablesContent)
        {
            // Ensure consistent line endings (LF only)
            var normalizedTemplate = templateContent.Replace("\r\n", "\n").Replace("\r", "\n");
            var normalizedVariables = variablesContent.Replace("\r\n", "\n").Replace("\r", "\n");

            var mainFile = Path.Combine(workingDirectory, "main.tf");
            await File.WriteAllTextAsync(mainFile, normalizedTemplate, new UTF8Encoding(false)); // UTF8 without BOM

            if (!string.IsNullOrEmpty(normalizedVariables))
            {
                var varsFile = Path.Combine(workingDirectory, "terraform.tfvars");
                await File.WriteAllTextAsync(varsFile, normalizedVariables, new UTF8Encoding(false)); // UTF8 without BOM
            }
        }

        private async Task<ValidationResult> ValidateSyntaxAsync(string workingDirectory)
        {
            var result = new ValidationResult { IsValid = true };

            try
            {
                // First, try to auto-format the files ---- The method prepares and runs terraform fmt
                var fmtResult = await RunTerraformCommandAsync(workingDirectory, "fmt");

                // If fmt succeeded, the files are now properly formatted
                if (fmtResult.ExitCode == 0)
                {
                    _logger.LogDebug("Terraform files successfully formatted");
                }
                else
                {
                    // Check what the formatting issues are
                    var fmtCheckResult = await RunTerraformCommandAsync(workingDirectory, "fmt", "-check", "-diff");
                    if (fmtCheckResult.ExitCode != 0)
                    {
                        _logger.LogWarning("Terraform formatting issues found: {Output}", fmtCheckResult.Output);
                        // Don't fail validation for formatting issues if auto-format worked
                        if (fmtResult.ExitCode != 0)
                        {
                            result.Errors.Add(new ValidationError
                            {
                                Code = "FormatError",
                                Message = $"Terraform configuration formatting issues: {fmtCheckResult.Output}",
                                Target = "template"
                            });
                            result.IsValid = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during Terraform format check");
                result.Errors.Add(new ValidationError
                {
                    Code = "SyntaxError",
                    Message = ex.Message,
                    Target = "template"
                });
                result.IsValid = false;
            }

            return result;
        }

        private async Task<Dictionary<string, object>> GetTerraformOutputsAsync(string workingDirectory)
        {
            try
            {
                var result = await RunTerraformCommandAsync(workingDirectory, "output", "-json");
                if (result.ExitCode == 0 && !string.IsNullOrEmpty(result.Output))
                {
                    return JsonSerializer.Deserialize<Dictionary<string, object>>(result.Output)
                           ?? new Dictionary<string, object>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to get Terraform outputs");
            }

            return new Dictionary<string, object>();
        }

        private List<ValidationError> ParseTerraformValidationErrors(string output)
        {
            var errors = new List<ValidationError>();

            try
            {
                if (string.IsNullOrEmpty(output))
                    return errors;

                var jsonOutput = JsonSerializer.Deserialize<JsonElement>(output);
                if (jsonOutput.TryGetProperty("diagnostics", out var diagnostics))
                {
                    foreach (var diagnostic in diagnostics.EnumerateArray())
                    {
                        errors.Add(new ValidationError
                        {
                            Code = diagnostic.TryGetProperty("severity", out var severity) ? severity.GetString() : "Error",
                            Message = diagnostic.TryGetProperty("summary", out var summary) ? summary.GetString() : "Unknown error",
                            Target = diagnostic.TryGetProperty("range", out var range) &&
                                   range.TryGetProperty("filename", out var filename) ? filename.GetString() : ""
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Terraform validation errors");
                errors.Add(new ValidationError
                {
                    Code = "ParseError",
                    Message = "Failed to parse validation output",
                    Target = ""
                });
            }

            return errors;
        }

        private List<DeploymentError> ParseTerraformDeploymentErrors(string output)
        {
            var errors = new List<DeploymentError>();

            try
            {
                // Parse terraform error output - this is more complex as it's not always JSON
                if (output.Contains("Error:"))
                {
                    var lines = output.Split('\n');
                    var errorMessage = "";
                    var inError = false;

                    foreach (var line in lines)
                    {
                        if (line.Trim().StartsWith("Error:"))
                        {
                            inError = true;
                            errorMessage = line.Trim();
                        }
                        else if (inError && !string.IsNullOrWhiteSpace(line) && !line.StartsWith("  "))
                        {
                            errors.Add(new DeploymentError
                            {
                                Code = "TerraformError",
                                Message = errorMessage,
                                Details = line.Trim()
                            });
                            inError = false;
                            errorMessage = "";
                        }
                        else if (inError)
                        {
                            errorMessage += " " + line.Trim();
                        }
                    }

                    if (inError && !string.IsNullOrEmpty(errorMessage))
                    {
                        errors.Add(new DeploymentError
                        {
                            Code = "TerraformError",
                            Message = errorMessage
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse Terraform deployment errors");
                errors.Add(new DeploymentError
                {
                    Code = "ParseError",
                    Message = "Failed to parse deployment output",
                    Details = output
                });
            }

            return errors;
        }

        private bool IsProcessInDirectory(Process process, string directory)
        {
            try
            {
                // This is a simplified check - in practice you might want to check
                // the process command line arguments to see if it's working in the specific directory
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public class TerraformCommandResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = string.Empty;
    }
}