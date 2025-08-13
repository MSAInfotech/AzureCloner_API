using Microsoft.AspNetCore.Mvc;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DiscoveryController : ControllerBase
    {
        private readonly IDiscoveryService _discoveryService;
        private readonly ILogger<DiscoveryController> _logger;

        public DiscoveryController(
            IDiscoveryService discoveryService,
            ILogger<DiscoveryController> logger)
        {
            _discoveryService = discoveryService;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<ActionResult<DiscoveryResult>> StartDiscovery([FromBody] DiscoveryRequest request)
        {
            try
            {
                _logger.LogInformation("Starting discovery for subscription {SubscriptionId}", request.SourceSubscriptionId);
                var session = await _discoveryService.StartDiscoveryAsync(request);
                return Ok(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start discovery session");
                return StatusCode(500, new { Error = "Failed to start discovery session", Details = ex.Message });
            }
        }

        [HttpGet("{sessionId}/status")]
        public async Task<ActionResult<DiscoverySession>> GetDiscoveryStatus(Guid sessionId)
        {
            try
            {
                var session = await _discoveryService.GetDiscoveryStatusAsync(sessionId);
                return Ok(session);
            }
            catch (ArgumentException)
            {
                return NotFound($"Discovery session {sessionId} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get discovery status for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get discovery status", Details = ex.Message });
            }
        }

        [HttpGet("{sessionId}/resources")]
        public async Task<ActionResult<List<AzureResource>>> GetDiscoveredResources(Guid sessionId)
        {
            try
            {
                var resources = await _discoveryService.GetDiscoveredResourcesAsync(sessionId);
                return Ok(resources);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get discovered resources for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get discovered resources", Details = ex.Message });
            }
        }

        [HttpGet("{sessionId}/summary")]
        public async Task<ActionResult> GetDiscoverySummary(Guid sessionId)
        {
            try
            {
                var session = await _discoveryService.GetDiscoveryStatusAsync(sessionId);
                var resources = await _discoveryService.GetDiscoveredResourcesAsync(sessionId);

                var summary = new
                {
                    Session = session,
                    ResourceCount = resources.Count,
                    ResourcesByType = resources.GroupBy(r => r.Type)
                        .Select(g => new { Type = g.Key, Count = g.Count() })
                        .OrderByDescending(x => x.Count),
                    ResourcesByStatus = resources.GroupBy(r => r.Status)
                        .Select(g => new { Status = g.Key.ToString(), Count = g.Count() }),
                    DependencyLevels = resources.GroupBy(r => r.DependencyLevel)
                        .Select(g => new { Level = g.Key, Count = g.Count() })
                        .OrderBy(x => x.Level)
                };

                return Ok(summary);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get discovery summary for session {SessionId}", sessionId);
                return StatusCode(500, new { Error = "Failed to get discovery summary", Details = ex.Message });
            }
        }
    }
}
