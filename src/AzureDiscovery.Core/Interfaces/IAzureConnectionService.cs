using System.ComponentModel.DataAnnotations;

namespace AzureDiscovery.Core.Interfaces
{
    public interface IAzureConnectionService
    {
        Task<AzureConnectionValidationResult> ValidateConnectionAsync(AzureConnectionRequest request);
        Task<AzureConnectionResponse> SaveConnectionAsync(AzureConnectionRequest request);
        Task<AzureConnectionValidationResult> ValidateAndSaveConnectionAsync(AzureConnectionRequest request);
        Task<List<AzureConnectionResponse>> GetConnectionsAsync(string? environment = null);
        Task<AzureConnectionResponse> GetConnectionsById(Guid id);
        Task<List<AzureConnectionResponse>> GetConnectionIfUsedInDiscovery();
        Task<bool> DeleteConnectionAsync(Guid id);
        Task<bool> UpdateConnectionAsync(Guid id, AzureConnectionRequest request);
        Task<AzureConnectionResponse> GetConnectionsByIdAsync(Guid id);
    }
    public class AzureConnectionRequest
    {
        [Required(ErrorMessage = "Connection name is required")]
        [StringLength(100, ErrorMessage = "Connection name cannot exceed 100 characters")]
        public string Name { get; set; } = string.Empty;

        [Required(ErrorMessage = "Subscription ID is required")]
        [RegularExpression(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            ErrorMessage = "Invalid subscription ID format")]
        public string SubscriptionId { get; set; } = string.Empty;

        [Required(ErrorMessage = "Tenant ID is required")]
        [RegularExpression(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            ErrorMessage = "Invalid tenant ID format")]
        public string TenantId { get; set; } = string.Empty;

        [Required(ErrorMessage = "Client ID is required")]
        [RegularExpression(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
            ErrorMessage = "Invalid client ID format")]
        public string ClientId { get; set; } = string.Empty;

        [Required(ErrorMessage = "Client secret is required")]
        public string ClientSecret { get; set; } = string.Empty;

        [Required(ErrorMessage = "Environment is required")]
        [RegularExpression("^(Development|Staging|Production|Testing)$",
            ErrorMessage = "Environment must be Development, Staging, Production, or Testing")]
        public string Environment { get; set; } = "Development";
    }

    public class AzureConnectionResponse
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? LastValidated { get; set; }
        public DateTime CreatedAt { get; set; }
        public Guid LatestSessionId { get; set; }
        public string ClientSecret { get; set; } = string.Empty;
    }

    public class AzureConnectionValidationResult
    {
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
        public string? SubscriptionName { get; set; }
        public string? SubscriptionState { get; set; }
        public int ResourceGroupCount { get; set; }
        public DateTime TestTimestamp { get; set; }
    }

    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public T? Data { get; set; }
        public List<string>? Errors { get; set; }
    }
}
