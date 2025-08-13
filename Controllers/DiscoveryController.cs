using Microsoft.AspNetCore.Mvc;
using AzureDiscovery.Services;
using AzureDiscovery.Models;

namespace AzureDiscovery.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DiscoveryController : ControllerBase
    {
        private readonly IAzureResourceDiscoveryService _discoveryService;
        private readonly ILogger<DiscoveryController> _logger;

        public DiscoveryController(
            IAzureResourceDiscoveryService discoveryService,
            ILogger<DiscoveryController> logger)
        {
            _discoveryService = discoveryService;
            _logger = logger;
        }

        [HttpPost("start")]
        public async Task<ActionResult<DiscoverySession>> StartDiscovery([FromBody] DiscoveryRequest request)
        {
            try
            {
                var session = await _discoveryService.StartDiscoveryAsync(request);
                return Ok(session);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start discovery session");
                return StatusCode(500, "Failed to start discovery session");
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
                return StatusCode(500, "Failed to get discovery status");
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
                return StatusCode(500, "Failed to get discovered resources");
            }
        }
    }
}
