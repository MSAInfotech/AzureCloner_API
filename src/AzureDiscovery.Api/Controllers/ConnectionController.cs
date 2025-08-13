using AzureDiscovery.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System;
using static Microsoft.ApplicationInsights.MetricDimensionNames.TelemetryContext;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class ConnectionController : Controller
    {
        private readonly ILogger<AuthController> _logger;
        private readonly IAzureConnectionService _azureConnectionService;

        public ConnectionController(
            ILogger<AuthController> logger,
            IAzureConnectionService azureConnectionService)
        {
            _logger = logger;
            _azureConnectionService = azureConnectionService;
        }

        [HttpPost("validate-azure-connection")]
        public async Task<ActionResult> ValidateConnection([FromBody] AzureConnectionRequest request)
        {
            _logger.LogInformation("Received request to validate Azure connection for: {ConnectionName}", request.Name);
            try
            {
                if (!ModelState.IsValid)
                {
                    _logger.LogWarning("Validation failed for Azure connection request. Errors: {Errors}",
                        string.Join("; ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));

                    return BadRequest(new ApiResponse<object>
                    {
                        Success = false,
                        Message = "Input validation failed",
                        Errors = ModelState.Values
                            .SelectMany(v => v.Errors)
                            .Select(e => e.ErrorMessage)
                            .ToList()
                    });
                }

                var validationResult = await _azureConnectionService.ValidateAndSaveConnectionAsync(request);

                if (validationResult.IsValid)
                {
                    _logger.LogInformation("Azure connection validation successful for: {ConnectionName}", request.Name);

                    return Ok(new ApiResponse<AzureConnectionValidationResult>
                    {
                        Success = true,
                        Message = "Azure connection validated successfully",
                        Data = validationResult
                    });
                }
                else
                {
                    _logger.LogWarning("Azure connection validation failed for: {ConnectionName}. Error: {Error}",
                        request.Name, validationResult.ErrorMessage);

                    return BadRequest(new ApiResponse<object>
                    {
                        Success = false,
                        Message = "Azure connection validation failed",
                        Errors = new List<string> { validationResult.ErrorMessage ?? "Unknown validation error" }
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during Azure connection validation for: {ConnectionName}", request.Name);

                return StatusCode(500, new ApiResponse<object>
                {
                    Success = false,
                    Message = "Internal server error occurred during validation",
                    Errors = new List<string> { ex.Message }
                });
            }
        }
        [HttpGet("connections")]
        public async Task<ActionResult> GetAllConnections([FromQuery] string? environment = null)
        {
            _logger.LogInformation("Fetching all Azure connections. Environment filter: {Environment}", environment);

            try
            {
                var connections = await _azureConnectionService.GetConnectionsAsync(environment);

                return Ok(new ApiResponse<List<AzureConnectionResponse>>
                {
                    Success = true,
                    Message = "Connections retrieved successfully",
                    Data = connections
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching connections");

                return StatusCode(500, new ApiResponse<object>
                {
                    Success = false,
                    Message = "Internal server error while fetching connections",
                    Errors = new List<string> { ex.Message }
                });
            }
        }
        [HttpDelete("{id:guid}")]
        public async Task<IActionResult> DeleteConnection(Guid id)
        {
            var deleted = await _azureConnectionService.DeleteConnectionAsync(id);

            if (deleted)
            {
                return Ok(new ApiResponse<object>
                {
                    Success = true,
                    Message = "Connection deleted successfully"
                });
            }

            return NotFound(new ApiResponse<object>
            {
                Success = false,
                Message = "Connection not found"
            });
        }

        [HttpGet("connectionsWithDiscovery")]
        public async Task<ActionResult> GetConnectionsWithDiscoverySessions()
        {
            _logger.LogInformation("Fetching azure connections user in discovery table");

            try
            {
                var connections = await _azureConnectionService.GetConnectionIfUsedInDiscovery();

                return Ok(new ApiResponse<List<AzureConnectionResponse>>
                {
                    Success = true,
                    Message = "Connections retrieved successfully",
                    Data = connections
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching connections");

                return StatusCode(500, new ApiResponse<object>
                {
                    Success = false,
                    Message = "Internal server error while fetching connections",
                    Errors = new List<string> { ex.Message }
                });
            }
        }
    }
}
