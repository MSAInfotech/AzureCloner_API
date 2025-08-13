using Microsoft.AspNetCore.Mvc;
using AzureDiscovery.Infrastructure.Services;
using AzureDiscovery.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthTestController : ControllerBase
    {
        private readonly IAzureAuthenticationService _authService;
        private readonly ILogger<AuthTestController> _logger;
        private readonly AzureAuthenticationOptions _authOptions;

        public AuthTestController(
            IAzureAuthenticationService authService,
            ILogger<AuthTestController> logger,
            IOptions<AzureAuthenticationOptions> authOptions)
        {
            _authService = authService;
            _logger = logger;
            _authOptions = authOptions.Value;
        }

        [HttpGet("status")]
        public ActionResult GetAuthStatus()
        {
            return Ok(new
            {
                AuthenticationMethod = _authOptions.AuthenticationMethod.ToString(),
                TenantId = _authOptions.TenantId,
                ClientId = _authOptions.ClientId,
                HasUsername = !string.IsNullOrEmpty(_authOptions.Username),
                HasPassword = !string.IsNullOrEmpty(_authOptions.Password),
                MfaSupport = _authOptions.EnableMfaSupport,
                Timestamp = DateTime.UtcNow
            });
        }

        [HttpPost("test-token")]
        public async Task<ActionResult> TestTokenAcquisition()
        {
            try
            {
                _logger.LogInformation("Testing token acquisition with method: {Method}", _authOptions.AuthenticationMethod);
                
                var token = await _authService.GetAccessTokenAsync(new[] { "https://management.azure.com/.default" });
                
                // Don't return the actual token for security reasons
                var tokenInfo = new
                {
                    HasToken = !string.IsNullOrEmpty(token),
                    TokenLength = token?.Length ?? 0,
                    TokenPrefix = token?.Substring(0, Math.Min(10, token?.Length ?? 0)) + "...",
                    AcquiredAt = DateTime.UtcNow
                };

                return Ok(new
                {
                    Status = "Success",
                    Message = "Token acquired successfully",
                    TokenInfo = tokenInfo,
                    AuthenticationMethod = _authOptions.AuthenticationMethod.ToString()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to acquire token");
                return StatusCode(500, new
                {
                    Status = "Error",
                    Message = ex.Message,
                    AuthenticationMethod = _authOptions.AuthenticationMethod.ToString()
                });
            }
        }

        [HttpPost("validate")]
        public async Task<ActionResult> ValidateCredentials()
        {
            try
            {
                _logger.LogInformation("Validating credentials with method: {Method}", _authOptions.AuthenticationMethod);
                
                var isValid = await _authService.ValidateCredentialsAsync();
                
                if (isValid)
                {
                    return Ok(new 
                    { 
                        Status = "Success", 
                        Message = "Credentials validated successfully",
                        AuthenticationMethod = _authOptions.AuthenticationMethod.ToString(),
                        ValidatedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    return Unauthorized(new 
                    { 
                        Status = "Failed", 
                        Message = "Credential validation failed",
                        AuthenticationMethod = _authOptions.AuthenticationMethod.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating credentials");
                return StatusCode(500, new 
                { 
                    Status = "Error", 
                    Message = ex.Message,
                    AuthenticationMethod = _authOptions.AuthenticationMethod.ToString()
                });
            }
        }

        [HttpGet("help")]
        public ActionResult GetAuthenticationHelp()
        {
            //var help = new
            //{
            //    CurrentMethod = _authOptions.AuthenticationMethod.ToString(),
            //    SupportsMfa = _authOptions.AuthenticationMethod == AuthenticationMethod.DeviceCode || 
            //                 _authOptions.AuthenticationMethod == AuthenticationMethod.InteractiveBrowser,
            //    Instructions = _authOptions.AuthenticationMethod switch
            //    {
            //        AuthenticationMethod.DeviceCode => new
            //        {
            //            Step1 = "Call POST /api/authtest/test-token",
            //            Step2 = "Check the console/logs for device code instructions",
            //            Step3 = "Visit the URL and enter the code",
            //            Step4 = "Complete MFA authentication in browser",
            //            Note = "Best option for MFA-enabled accounts"
            //        },
            //        AuthenticationMethod.InteractiveBrowser => new
            //        {
            //            Step1 = "Call POST /api/authtest/test-token",
            //            Step2 = "Browser window will open automatically",
            //            Step3 = "Complete authentication and MFA",
            //            Note = "Good for development environments"
            //        },
            //        AuthenticationMethod.UsernamePassword => new
            //        {
            //            Step1 = "Ensure username and password are set in config",
            //            Step2 = "Call POST /api/authtest/test-token",
            //            Warning = "Does NOT work with MFA-enabled accounts"
            //        },
            //        _ => new
            //        {
            //            Step1 = "Check your authentication configuration",
            //            Step2 = "Call POST /api/authtest/test-token"
            //        }
            //    },
            //    RequiredPermissions = new[]
            //    {
            //        "Reader role on subscription",
            //        "Resource Graph Reader (recommended)"
            //    },
            //    ConfigurationExample = new
            //    {
            //        AzureAuthentication = new
            //        {
            //            AuthenticationMethod = "DeviceCode",
            //            TenantId = "your-tenant-id-here",
            //            ClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46",
            //            EnableMfaSupport = true
            //        }
            //    }
            //};

            return Ok(true);
        }
    }
}
