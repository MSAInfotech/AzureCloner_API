using Microsoft.AspNetCore.Mvc;
using AzureDiscovery.Infrastructure.Services;
using AzureDiscovery.Core.Interfaces;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestController : ControllerBase
    {
        private readonly IResourceGraphService _resourceGraphService;
        private readonly ILogger<TestController> _logger;

        public TestController(
            IResourceGraphService resourceGraphService,
            ILogger<TestController> logger)
        {
            _resourceGraphService = resourceGraphService;
            _logger = logger;
        }

        [HttpGet("resources/{subscriptionId}")]
        public async Task<ActionResult> TestResourceDiscovery(string subscriptionId,Guid sessionId)
        {
            try
            {
                _logger.LogInformation("Testing resource discovery for subscription {SubscriptionId}", subscriptionId);

                var resources = await _resourceGraphService.DiscoverResourcesAsync(
                    subscriptionId, 
                    new List<string>(), // No resource group filters
                    new List<string>(),  // No resource type filters
                    sessionId
                );

                return Ok(new
                {
                    SubscriptionId = subscriptionId,
                    ResourceCount = resources.Count,
                    Resources = resources.Take(10).Select(r => new
                    {
                        r.Id,
                        r.Name,
                        r.Type,
                        r.ResourceGroup,
                        r.Location
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error testing resource discovery for subscription {SubscriptionId}", subscriptionId);
                return StatusCode(500, new { Error = ex.Message });
            }
        }

        [HttpGet("health")]
        public ActionResult Health()
        {
            return Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow });
        }
    }
}
