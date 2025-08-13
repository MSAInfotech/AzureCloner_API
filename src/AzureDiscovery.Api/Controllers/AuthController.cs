using Microsoft.AspNetCore.Mvc;
using AzureDiscovery.Infrastructure.Services;
using AzureDiscovery.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using AzureDiscovery.Core.Interfaces;
using Azure.Core;

namespace AzureDiscovery.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IAzureAuthenticationService _authService;
        private readonly ILogger<AuthController> _logger;
        private readonly AzureAuthenticationOptions _authOptions;
        private readonly IUserAccountService _userAccountService;

        public AuthController(
            IAzureAuthenticationService authService,
            ILogger<AuthController> logger,
            IOptions<AzureAuthenticationOptions> authOptions,
            IUserAccountService userAccountService)
        {
            _authService = authService;
            _logger = logger;
            _authOptions = authOptions.Value;
            _userAccountService = userAccountService;
        }

        [HttpPost("validate")]
        public async Task<ActionResult> ValidateCredentials()
        {
            try
            {
                var isValid = await _authService.ValidateCredentialsAsync();
                
                if (isValid)
                {
                    return Ok(new 
                    { 
                        Status = "Success", 
                        Message = "Credentials validated successfully",
                        AuthenticationMethod = _authOptions.AuthenticationMethod.ToString()
                    });
                }
                else
                {
                    return Unauthorized(new 
                    { 
                        Status = "Failed", 
                        Message = "Credential validation failed" 
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating credentials");
                return StatusCode(500, new 
                { 
                    Status = "Error", 
                    Message = ex.Message 
                });
            }
        }
        
        [HttpGet("methods")]
        public ActionResult GetAuthenticationMethods()
        {
            var methods = new[]
            {
                new { 
                    Method = "DeviceCode", 
                    Description = "Best for MFA-enabled accounts. Requires device code authentication.",
                    SupportsMfa = true,
                    Recommended = true
                },
                new { 
                    Method = "InteractiveBrowser", 
                    Description = "Opens browser for authentication. Supports MFA.",
                    SupportsMfa = true,
                    Recommended = true
                },
                new { 
                    Method = "UsernamePassword", 
                    Description = "Direct username/password. Does NOT support MFA.",
                    SupportsMfa = false,
                    Recommended = false
                },
                new { 
                    Method = "ServicePrincipal", 
                    Description = "Uses service principal with client secret.",
                    SupportsMfa = false,
                    Recommended = false
                }
            };

            return Ok(new
            {
                CurrentMethod = _authOptions.AuthenticationMethod.ToString(),
                AvailableMethods = methods,
                Recommendation = "For MFA-enabled accounts, use DeviceCode or InteractiveBrowser authentication."
            });
        }

        [HttpPost("device-code")]
        public async Task<ActionResult> InitiateDeviceCodeFlow()
        {
            try
            {
                // This will trigger the device code flow
                var credential = await _authService.GetCredentialAsync();
                var token = await _authService.GetAccessTokenAsync(new[] { "https://management.azure.com/.default" });
                
                return Ok(new 
                { 
                    Status = "Success", 
                    Message = "Device code authentication completed successfully" 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during device code authentication");
                return StatusCode(500, new 
                { 
                    Status = "Error", 
                    Message = ex.Message 
                });
            }
        }

        //Fronted
        [HttpPost("signup")]
        public async Task<IActionResult> Signup([FromBody] UserRequest signupRequest)
        {
            try
            {
                await _userAccountService.RegisterAsync(signupRequest);
                return Ok(new { message = "Account created. Please check your email to activate it." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }

        }

        [HttpGet("activate")]
        public async Task<IActionResult> Activate([FromQuery] string token)
        {
            try
            {
                await _userAccountService.ActivateUserAsync(token);
                return Ok("Account activated successfully.");
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }

        }
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest loginRequest)
        {
            try
            {
                var token = await _userAccountService.LoginAsync(loginRequest);
                return Ok(new
                {
                    message = "User logged in successfully.",
                    token = token
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
        [HttpPost("forgotPassword")]
        public async Task<IActionResult> ForgotPassword([FromQuery] string email)
        {
            try
            {
                await _userAccountService.SendPasswordResetEmailAsync(email);
                return Ok("Reset link sent to your email.");
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
        [HttpPost("resetPassword")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            try
            {
                await _userAccountService.ResetPasswordAsync(request.Token, request.NewPassword);
                return Ok(new { message = "Password has been reset successfully." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }
    }
}
