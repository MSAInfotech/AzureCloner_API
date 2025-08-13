using AzureDiscovery.Infrastructure.Services;

namespace AzureDiscovery.Infrastructure.Configuration
{
    public class AzureAuthenticationOptions
    {
        public const string SectionName = "AzureAuthentication";

        public AuthenticationMethod AuthenticationMethod { get; set; } = AuthenticationMethod.Default;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string ClientSecret { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string subscriptionId { get; set; } = string.Empty;
        public string RedirectUri { get; set; } = "http://localhost:8400";
        public bool EnableMfaSupport { get; set; } = true;
        public int TokenCacheExpirationMinutes { get; set; } = 60;
    }
}
