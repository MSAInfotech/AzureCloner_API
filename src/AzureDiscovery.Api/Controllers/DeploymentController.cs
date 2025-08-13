using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DeploymentController : ControllerBase
    {
        private readonly IDeploymentService _deploymentService;
        private readonly ILogger<DeploymentController> _logger;
        private readonly IAzureConnectionService _azureConnectionService;

        public DeploymentController(
            IDeploymentService deploymentService,
            ILogger<DeploymentController> logger,
            IAzureConnectionService azureConnectionService)
        {
            _deploymentService = deploymentService;
            _logger = logger;
            _azureConnectionService = azureConnectionService;
        }

        [HttpPost("start-deployment")]
        public async Task<ActionResult<DeploymentSession>> StartDeployment([FromBody] DeploymentRequest request)
        {
            try
            {
                Guid targetSubscriptionGuid = Guid.Parse(request.TargetSubscriptionId);
                AzureConnectionResponse? GetTargetSubscriptionId = await _azureConnectionService.GetConnectionsById(targetSubscriptionGuid);
                request.TargetSubscriptionId = GetTargetSubscriptionId.SubscriptionId;

                _logger.LogInformation("Creating deployment session for discovery {DiscoverySessionId}", request.DiscoverySessionId);
                var session = await _deploymentService.CreateDeploymentSessionAsync(request);

                // Step 2: Validate Templates
                _logger.LogInformation("Validating all templates for deployment session {SessionId}", session.Id);
                var validationResult = await _deploymentService.ValidateAllTemplatesAsync(session.Id, request.DiscoverySessionId);

                if (!validationResult.IsValid)
                {
                    return BadRequest(new
                    {
                        Error = "Validation failed",
                        Details = validationResult.Errors
                    });
                }

                // Step 3: Deploy Templates
                _logger.LogInformation("Deploying all templates for session {SessionId}", session.Id);
                var deploymentResult = await _deploymentService.DeployAllTemplatesAsync(session.Id, request.DiscoverySessionId);

                return Ok(new
                {
                    Message = "Deployment completed successfully",
                    SessionId = session.Id,
                    DeploymentResult = deploymentResult
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create deployment session");
                return StatusCode(500, new { Error = "Failed to create deployment session", Details = ex.Message });
            }
        }

        [HttpPost("sessions")]
        public async Task<ActionResult<DeploymentSession>> CreateDeploymentSession([FromBody] DeploymentRequest request)
        {
            try
            {
                _logger.LogInformation("Creating deployment session for discovery {DiscoverySessionId}", request.DiscoverySessionId);
                var session = await _deploymentService.CreateDeploymentSessionAsync(request);
                return Ok(session);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { Error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create deployment session");
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
                return NotFound($"Deployment session {sessionId} not found");
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
                _logger.LogError(ex, "Failed to get template deployments for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get template deployments", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/validate")]
        public async Task<ActionResult<Core.Interfaces.ValidationResult>> ValidateAllTemplates(Guid sessionId,Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Validating all templates for deployment session {SessionId}", sessionId);
                var result = await _deploymentService.ValidateAllTemplatesAsync(sessionId, discoverySessionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate templates for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to validate templates", Details = ex.Message });
            }
        }

        [HttpPost("templates/{templateId}/validate")]
        public async Task<ActionResult<Core.Interfaces.ValidationResult>> ValidateTemplate(Guid templateId,Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Validating template {TemplateId}", templateId);
                var result = await _deploymentService.ValidateTemplateAsync(templateId, discoverySessionId);
                return Ok(result);
            }
            catch (ArgumentException)
            {
                return NotFound($"Template {templateId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate template {TemplateId}", templateId);
                return StatusCode(500, new { Error = "Failed to validate template", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/deploy")]
        public async Task<ActionResult<DeploymentResult>> DeployAllTemplates(Guid sessionId, Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Deploying all templates for session {SessionId}", sessionId);
                var result = await _deploymentService.DeployAllTemplatesAsync(sessionId, discoverySessionId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy templates for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to deploy templates", Details = ex.Message });
            }
        }

        [HttpPost("templates/{templateId}/deploy")]
        public async Task<ActionResult<DeploymentResult>> DeployTemplate(Guid templateId,Guid discoverySessionId)
        {
            try
            {
                _logger.LogInformation("Deploying template {TemplateId}", templateId);
                var result = await _deploymentService.DeployTemplateAsync(templateId, discoverySessionId);
                return Ok(result);
            }
            catch (ArgumentException)
            {
                return NotFound($"Template {templateId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deploy template {TemplateId}", templateId);
                return StatusCode(500, new { Error = "Failed to deploy template", Details = ex.Message });
            }
        }

        [HttpPost("sessions/{sessionId}/cancel")]
        public async Task<ActionResult<DeploymentSession>> CancelDeployment(Guid sessionId)
        {
            try
            {
                _logger.LogInformation("Cancelling deployment session {SessionId}", sessionId);
                var session = await _deploymentService.CancelDeploymentAsync(sessionId);
                return Ok(session);
            }
            catch (ArgumentException)
            {
                return NotFound($"Deployment session {sessionId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cancel deployment session {SessionId}", sessionId);
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
                _logger.LogError(ex, "Failed to get deployment summary for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get deployment summary", Details = ex.Message });
            }
        }

       
    }
}
