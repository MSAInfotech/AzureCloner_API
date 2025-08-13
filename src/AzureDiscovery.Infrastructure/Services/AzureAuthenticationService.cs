using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Infrastructure.Configuration;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace AzureDiscovery.Infrastructure.Services
{
    public interface IAzureAuthenticationService
    {
        Task<TokenCredential> GetCredentialAsync();
        Task<string> GetAccessTokenAsync(string[] scopes);
        Task<bool> ValidateCredentialsAsync();
        Task<string?> GetAccessTokenWithClientSecretAsync(Guid discoverySessionId);
    }

    public class AzureAuthenticationService : IAzureAuthenticationService
    {
        private readonly ILogger<AzureAuthenticationService> _logger;
        private readonly AzureAuthenticationOptions _options;
        private TokenCredential? _credential;
        private readonly IAzureConnectionService _azureConnectionService;
        private readonly IServiceProvider _serviceProvider;

        public AzureAuthenticationService(
            ILogger<AzureAuthenticationService> logger,
            IOptions<AzureAuthenticationOptions> options,
            IAzureConnectionService azureConnectionService,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _options = options.Value;
            _azureConnectionService = azureConnectionService;
            _serviceProvider = serviceProvider;
        }

        public async Task<TokenCredential> GetCredentialAsync()
        {
            if (_credential != null)
                return _credential;

            _credential = _options.AuthenticationMethod switch
            {
                AuthenticationMethod.UsernamePassword => CreateUsernamePasswordCredential(),
                AuthenticationMethod.DeviceCode => CreateDeviceCodeCredential(),
                AuthenticationMethod.InteractiveBrowser => CreateInteractiveBrowserCredential(),
                AuthenticationMethod.ServicePrincipal => CreateServicePrincipalCredential(),
                AuthenticationMethod.ManagedIdentity => CreateManagedIdentityCredential(),
                _ => new DefaultAzureCredential()
            };

            // Validate the credential
            await ValidateCredentialAsync(_credential);
            return _credential;
        }

        public async Task<string> GetAccessTokenAsync(string[] scopes)
        {
            var credential = await GetCredentialAsync();
            var tokenRequest = new TokenRequestContext(scopes);
            var token = await credential.GetTokenAsync(tokenRequest, CancellationToken.None);
            return token.Token;
        }
        public async Task<string?> GetAccessTokenWithClientSecretAsync(Guid discoverySessionId)
        {
            try
            {
                var _azureDiscoveryService = _serviceProvider.GetRequiredService<IDiscoveryService>();
                DiscoverySession? discovery = await _azureDiscoveryService.GetDiscoveryStatusAsync(discoverySessionId);
                if (discovery == null)
                {
                    _logger.LogWarning("Discovery session {DiscoverySessionId} not found", discoverySessionId);
                    return null;
                }

                AzureConnectionResponse? connection = await _azureConnectionService.GetConnectionsById(discovery.ConnectionId);
                if (connection == null)
                {
                    _logger.LogWarning("Azure connection {ConnectionId} not found", discovery.ConnectionId);
                    return null;
                }

                if (string.IsNullOrWhiteSpace(connection.TenantId) ||
                    string.IsNullOrWhiteSpace(connection.ClientId) ||
                    string.IsNullOrWhiteSpace(connection.ClientSecret))
                {
                    _logger.LogWarning("Missing Service Principal credentials for connection {ConnectionId}", connection.Id);
                    return null;
                }

                // Create credential using Service Principal details
                var credential = new ClientSecretCredential(
                    connection.TenantId!,
                    connection.ClientId!,
                    connection.ClientSecret!
                );

                // Request access token for given scopes
                var tokenRequest = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
                var token = await credential.GetTokenAsync(tokenRequest, CancellationToken.None);

                return token.Token;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating access token with ClientSecretCredential");
                throw new InvalidOperationException("Failed to get access token. " + ex.Message, ex);
            }
        }

        public async Task<bool> ValidateCredentialsAsync()
        {
            try
            {
                var credential = await GetCredentialAsync();
                await ValidateCredentialAsync(credential);
                return true;

                //var request = new AzureConnectionRequest
                //{
                //    TenantId = config.TenantId,
                //    ClientId = config.ClientId,
                //    ClientSecret = config.ClientSecret,
                //    SubscriptionId = config.SubscriptionId,
                //    Name = "Global Validation"
                //};

                //var result = await _azureConnectionService.ValidateConnectionAsync(request);
                //if (!result.IsValid)
                //{
                //    throw new InvalidOperationException(result.ErrorMessage);
                //}
                //return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Credential validation failed");
                return false;
            }
        }

        private TokenCredential CreateUsernamePasswordCredential()
        {
            _logger.LogInformation("Creating username/password credential");

            if (string.IsNullOrEmpty(_options.Username) || string.IsNullOrEmpty(_options.Password))
            {
                throw new InvalidOperationException("Username and password are required for username/password authentication");
            }

            // For MFA-enabled accounts, we need to use device code flow or interactive browser
            // Username/password flow doesn't work with MFA
            _logger.LogWarning("Username/password authentication doesn't support MFA. Consider using device code or interactive browser flow.");

            var options = new UsernamePasswordCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            };

            return new UsernamePasswordCredential(
                _options.Username,
                _options.Password,
                _options.TenantId,
                _options.ClientId,
                options);
        }

        private TokenCredential CreateDeviceCodeCredential()
        {
            _logger.LogInformation("Creating device code credential (recommended for MFA accounts)");

            var options = new DeviceCodeCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                ClientId = _options.ClientId,
                TenantId = _options.TenantId,
                DeviceCodeCallback = (code, cancellation) =>
                {
                    _logger.LogInformation("=== DEVICE CODE AUTHENTICATION REQUIRED ===");
                    _logger.LogInformation("Please visit: {Url}", code.VerificationUri);
                    _logger.LogInformation("And enter code: {Code}", code.UserCode);
                    _logger.LogInformation("Or visit direct URL: {DirectUrl}", code.VerificationUri + "?user_code=" + code.UserCode);
                    _logger.LogInformation("===========================================");

                    Console.WriteLine("=== DEVICE CODE AUTHENTICATION REQUIRED ===");
                    Console.WriteLine($"Please visit: {code.VerificationUri}");
                    Console.WriteLine($"And enter code: {code.UserCode}");
                    Console.WriteLine($"Or visit: {code.VerificationUri}?user_code={code.UserCode}");
                    Console.WriteLine("===========================================");

                    return Task.CompletedTask;
                }
            };

            return new DeviceCodeCredential(options);
        }

        private TokenCredential CreateInteractiveBrowserCredential()
        {
            _logger.LogInformation("Creating interactive browser credential (supports MFA)");

            var options = new InteractiveBrowserCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
                ClientId = _options.ClientId,
                TenantId = _options.TenantId,
                RedirectUri = new Uri(_options.RedirectUri)
            };

            return new InteractiveBrowserCredential(options);
        }

        private TokenCredential CreateServicePrincipalCredential()
        {
            _logger.LogInformation("Creating service principal credential");

            if (string.IsNullOrEmpty(_options.ClientId) || string.IsNullOrEmpty(_options.ClientSecret))
            {
                throw new InvalidOperationException("ClientId and ClientSecret are required for service principal authentication");
            }

            var options = new ClientSecretCredentialOptions
            {
                AuthorityHost = AzureAuthorityHosts.AzurePublicCloud,
            };

            return new ClientSecretCredential(
                _options.TenantId,
                _options.ClientId,
                _options.ClientSecret,
                options);
        }

        private TokenCredential CreateManagedIdentityCredential()
        {
            _logger.LogInformation("Creating managed identity credential");

            // Use the constructor that accepts clientId as string parameter
            if (!string.IsNullOrEmpty(_options.ClientId))
            {
                return new ManagedIdentityCredential(_options.ClientId);
            }
            else
            {
                return new ManagedIdentityCredential();
            }
        }

        private async Task ValidateCredentialAsync(TokenCredential credential)
        {
            try
            {
                var tokenRequest = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
                var token = await credential.GetTokenAsync(tokenRequest, CancellationToken.None);

                if (string.IsNullOrEmpty(token.Token))
                {
                    throw new InvalidOperationException("Failed to acquire access token");
                }

                _logger.LogInformation("Successfully validated Azure credentials");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate Azure credentials");
                throw;
            }
        }
    }

    public enum AuthenticationMethod
    {
        UsernamePassword,
        DeviceCode,
        InteractiveBrowser,
        ServicePrincipal,
        ManagedIdentity,
        Default
    }
}
