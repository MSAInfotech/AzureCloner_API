using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Infrastructure.Services;
using Microsoft.AspNetCore.Mvc;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TerraformDeploymentController : ControllerBase
    {
        private readonly IDeploymentService _deploymentService;
        private readonly ITerraformResourceManagerService _terraformService;
        private readonly ILogger<TerraformDeploymentController> _logger;

        public TerraformDeploymentController(
            IDeploymentService deploymentService,
            ITerraformResourceManagerService terraformService,
            ILogger<TerraformDeploymentController> logger)
        {
            _deploymentService = deploymentService;
            _terraformService = terraformService;
            _logger = logger;
        }

        [HttpPost("sessions")]
        public async Task<ActionResult<DeploymentSession>> CreateDeploymentSession([FromBody] DeploymentRequest request)
        {
            try
            {
                _logger.LogInformation("Creating Terraform deployment session for discovery {DiscoverySessionId}", request.DiscoverySessionId);
                var session = await _deploymentService.CreateDeploymentSessionAsync(request);
                return Ok(session);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message, Type = "InvalidArgument" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Error = ex.Message, Type = "InvalidOperation" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create Terraform deployment session");
                return StatusCode(500, new { Error = "Failed to create deployment session", Details = ex.Message });
            }
        }

        [HttpGet("sessions/{sessionId}")]
        public async Task<ActionResult<DeploymentSession>> GetDeploymentSession(Guid sessionId)
        {
            try
            {
                var session = await _deploymentService.GetDeploymentStatusAsync(sessionId);
                return Ok(session);
            }
            catch (ArgumentException)
            {
                return NotFound(new { Error = $"Deployment session {sessionId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get deployment session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get deployment session", Details = ex.Message });
            }
        }

        [HttpGet("sessions/{sessionId}/templates")]
        public async Task<ActionResult<List<TemplateDeployment>>> GetTemplateDeployments(Guid sessionId)
        {
            try
            {
                var templates = await _deploymentService.GetTemplateDeploymentsAsync(sessionId);
                return Ok(templates);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Terraform template deployments for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get template deployments", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/validate")]
        public async Task<ActionResult<ValidationResult>> ValidateAllTemplates(Guid sessionId, Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Validating all Terraform templates for deployment session {SessionId}", sessionId);
                var result = await _deploymentService.ValidateAllTemplatesAsync(sessionId, discoverySessionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate Terraform templates for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to validate templates", Details = ex.Message });
            }
        }

        [HttpPost("templates/{templateId}/validate")]
        public async Task<ActionResult<ValidationResult>> ValidateTemplate(Guid templateId, Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Validating Terraform template {TemplateId}", templateId);
                var result = await _deploymentService.ValidateTemplateAsync(templateId, discoverySessionId);
                return Ok(result);
            }
            catch (ArgumentException)
            {
                return NotFound(new { Error = $"Template {templateId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate Terraform template {TemplateId}", templateId);
                return StatusCode(500, new { Error = "Failed to validate template", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/deploy")]
        public async Task<ActionResult<DeploymentResult>> DeployAllTemplates(Guid sessionId, Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Deploying all Terraform templates for session {SessionId}", sessionId);
                var result = await _deploymentService.DeployAllTemplatesAsync(sessionId, discoverySessionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy Terraform templates for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to deploy templates", Details = ex.Message });
            }
        }

        [HttpPost("templates/{templateId}/deploy")]
        public async Task<ActionResult<DeploymentResult>> DeployTemplate(Guid templateId, Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Deploying Terraform template {TemplateId}", templateId);
                var result = await _deploymentService.DeployTemplateAsync(templateId, discoverySessionId);
                return Ok(result);
            }
            catch (ArgumentException)
            {
                return NotFound(new { Error = $"Template {templateId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy Terraform template {TemplateId}", templateId);
                return StatusCode(500, new { Error = "Failed to deploy template", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/cancel")]
        public async Task<ActionResult<DeploymentSession>> CancelDeployment(Guid sessionId)
        {
            try
            {
                _logger.LogInformation("Cancelling Terraform deployment session {SessionId}", sessionId);
                var session = await _deploymentService.CancelDeploymentAsync(sessionId);
                return Ok(session);
            }
            catch (ArgumentException)
            {
                return NotFound(new { Error = $"Deployment session {sessionId} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel Terraform deployment session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to cancel deployment", Details = ex.Message });
            }
        }

        [HttpGet("sessions/{sessionId}/summary")]
        public async Task<ActionResult> GetDeploymentSummary(Guid sessionId)
        {
            try
            {
                var session = await _deploymentService.GetDeploymentStatusAsync(sessionId);
                var templates = await _deploymentService.GetTemplateDeploymentsAsync(sessionId);

                var summary = new
                {
                    Session = session,
                    Progress = new
                    {
                        TotalTemplates = session.TotalTemplates,
                        TemplatesDeployed = session.TemplatesDeployed,
                        TemplatesFailed = session.TemplatesFailed,
                        PercentComplete = session.TotalTemplates > 0
                            ? (double)(session.TemplatesDeployed + session.TemplatesFailed) / session.TotalTemplates * 100
                            : 0
                    },
                    TemplatesByStatus = templates.GroupBy(t => t.Status)
                        .Select(g => new { Status = g.Key.ToString(), Count = g.Count() }),
                    TemplatesByLevel = templates.GroupBy(t => t.DependencyLevel)
                        .Select(g => new { Level = g.Key, Count = g.Count() })
                        .OrderBy(x => x.Level),
                    RecentActivity = templates
                        .Where(t => t.DeployedAt.HasValue || t.ValidatedAt.HasValue)
                        .OrderByDescending(t => t.DeployedAt ?? t.ValidatedAt)
                        .Take(10)
                        .Select(t => new
                        {
                            t.TemplateName,
                            t.Status,
                            LastActivity = t.DeployedAt ?? t.ValidatedAt,
                            t.ErrorMessage
                        })
                };

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Terraform deployment summary for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get deployment summary", Details = ex.Message });
            }
        }

        // Additional Terraform-specific endpoints

        [HttpPost("sessions/{sessionId}/plan")]
        public async Task<ActionResult> GenerateDeploymentPlan(Guid sessionId)
        {
            try
            {
                _logger.LogInformation("Generating Terraform plan for deployment session {SessionId}", sessionId);

                var session = await _deploymentService.GetDeploymentStatusAsync(sessionId);
                var templates = await _deploymentService.GetTemplateDeploymentsAsync(sessionId);

                var planResults = new List<object>();

                foreach (var template in templates)
                {
                    try
                    {
                        var workingDirectory = Path.Combine(Path.GetTempPath(), "terraform", template.Id.ToString());
                        Directory.CreateDirectory(workingDirectory);

                        // Write template files
                        await System.IO.File.WriteAllTextAsync(Path.Combine(workingDirectory, "main.tf"), template.TemplateContent);
                        if (!string.IsNullOrEmpty(template.ParametersContent))
                        {
                            await System.IO.File.WriteAllTextAsync(Path.Combine(workingDirectory, "terraform.tfvars"), template.ParametersContent);
                        }

                        // Initialize and plan
                        await _terraformService.InitializeWorkspaceAsync(workingDirectory);
                        var planFile = Path.Combine(workingDirectory, $"{template.TemplateName}.tfplan");
                        var planOutput = await _terraformService.PlanDeploymentAsync(workingDirectory, planFile);

                        planResults.Add(new
                        {
                            TemplateId = template.Id,
                            TemplateName = template.TemplateName,
                            PlanOutput = planOutput,
                            Status = "Success"
                        });

                        // Clean up
                        if (Directory.Exists(workingDirectory))
                        {
                            Directory.Delete(workingDirectory, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        planResults.Add(new
                        {
                            TemplateId = template.Id,
                            TemplateName = template.TemplateName,
                            Error = ex.Message,
                            Status = "Failed"
                        });
                    }
                }

                return Ok(new { SessionId = sessionId, Plans = planResults });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate Terraform plan for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to generate deployment plan", Details = ex.Message });
            }
        }

        [HttpPost("templates/{templateId}/plan")]
        public async Task<ActionResult> GenerateTemplatePlan(Guid templateId)
        {
            try
            {
                _logger.LogInformation("Generating Terraform plan for template {TemplateId}", templateId);

                var templates = await _deploymentService.GetTemplateDeploymentsAsync(Guid.Empty);
                var template = templates.FirstOrDefault(t => t.Id == templateId);

                if (template == null)
                    return NotFound(new { Error = $"Template {templateId} not found" });

                var workingDirectory = Path.Combine(Path.GetTempPath(), "terraform", template.Id.ToString());
                Directory.CreateDirectory(workingDirectory);

                try
                {
                    // Write template files
                    await System.IO.File.WriteAllTextAsync(Path.Combine(workingDirectory, "main.tf"), template.TemplateContent);
                    if (!string.IsNullOrEmpty(template.ParametersContent))
                    {
                        await System.IO.File.WriteAllTextAsync(Path.Combine(workingDirectory, "terraform.tfvars"), template.ParametersContent);
                    }

                    // Initialize and plan
                    await _terraformService.InitializeWorkspaceAsync(workingDirectory);
                    var planFile = Path.Combine(workingDirectory, $"{template.TemplateName}.tfplan");
                    var planOutput = await _terraformService.PlanDeploymentAsync(workingDirectory, planFile);

                    return Ok(new
                    {
                        TemplateId = templateId,
                        TemplateName = template.TemplateName,
                        PlanOutput = planOutput
                    });
                }
                finally
                {
                    // Clean up
                    if (Directory.Exists(workingDirectory))
                    {
                        Directory.Delete(workingDirectory, true);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate Terraform plan for template {TemplateId}", templateId);
                return StatusCode(500, new { Error = "Failed to generate template plan", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/destroy")]
        public async Task<ActionResult> DestroyDeployment(Guid sessionId)
        {
            try
            {
                _logger.LogInformation("Destroying Terraform deployment for session {SessionId}", sessionId);

                var session = await _deploymentService.GetDeploymentStatusAsync(sessionId);
                var templates = await _deploymentService.GetTemplateDeploymentsAsync(sessionId);

                var destroyResults = new List<object>();

                // Destroy in reverse dependency order
                var templatesInReverseOrder = templates
                    .OrderByDescending(t => t.DependencyLevel)
                    .ThenByDescending(t => t.CreatedAt);

                foreach (var template in templatesInReverseOrder)
                {
                    try
                    {
                        var workingDirectory = Path.Combine(Path.GetTempPath(), "terraform", template.Id.ToString());
                        Directory.CreateDirectory(workingDirectory);

                        // Write template files
                        await System.IO.File.WriteAllTextAsync(Path.Combine(workingDirectory, "main.tf"), template.TemplateContent);
                        if (!string.IsNullOrEmpty(template.ParametersContent))
                        {
                            await System.IO.File.WriteAllTextAsync(Path.Combine(workingDirectory, "terraform.tfvars"), template.ParametersContent);
                        }

                        // Initialize and destroy
                        await _terraformService.InitializeWorkspaceAsync(workingDirectory);
                        var destroyResult = await _terraformService.DestroyResourcesAsync(workingDirectory);

                        destroyResults.Add(new
                        {
                            TemplateId = template.Id,
                            TemplateName = template.TemplateName,
                            Success = destroyResult,
                            Status = destroyResult ? "Destroyed" : "Failed"
                        });

                        // Clean up
                        if (Directory.Exists(workingDirectory))
                        {
                            Directory.Delete(workingDirectory, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        destroyResults.Add(new
                        {
                            TemplateId = template.Id,
                            TemplateName = template.TemplateName,
                            Error = ex.Message,
                            Status = "Failed"
                        });
                    }
                }

                return Ok(new { SessionId = sessionId, DestroyResults = destroyResults });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to destroy Terraform deployment for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to destroy deployment", Details = ex.Message });
            }
        }

        [HttpGet("sessions/{sessionId}/state")]
        public async Task<ActionResult> GetTerraformState(Guid sessionId)
        {
            try
            {
                var session = await _deploymentService.GetDeploymentStatusAsync(sessionId);
                var templates = await _deploymentService.GetTemplateDeploymentsAsync(sessionId);

                var stateResults = new List<object>();

                foreach (var template in templates)
                {
                    try
                    {
                        var workingDirectory = Path.Combine(Path.GetTempPath(), "terraform", template.Id.ToString());
                        var deploymentStatus = await _terraformService.GetDeploymentStatusAsync(workingDirectory, template.TemplateName);

                        stateResults.Add(new
                        {
                            TemplateId = template.Id,
                            TemplateName = template.TemplateName,
                            State = deploymentStatus.State.ToString(),
                            IsSuccessful = deploymentStatus.IsSuccessful,
                            Outputs = deploymentStatus.Outputs
                        });
                    }
                    catch (Exception ex)
                    {
                        stateResults.Add(new
                        {
                            TemplateId = template.Id,
                            TemplateName = template.TemplateName,
                            Error = ex.Message,
                            State = "Unknown"
                        });
                    }
                }

                return Ok(new { SessionId = sessionId, States = stateResults });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get Terraform state for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get terraform state", Details = ex.Message });
            }
        }
    }
}