using AzureDiscovery.Core.Models;

namespace AzureDiscovery.Core.Interfaces
{
    public interface IDeploymentService
    {
        Task<DeploymentSession> CreateDeploymentSessionAsync(DeploymentRequest request);
        Task<DeploymentSession> GetDeploymentStatusAsync(Guid sessionId);
        Task<List<TemplateDeployment>> GetTemplateDeploymentsAsync(Guid sessionId);
        Task<ValidationResult> ValidateTemplateAsync(Guid templateId, Guid discoverySessionId);
        Task<ValidationResult> ValidateAllTemplatesAsync(Guid sessionId,Guid discoverySessionId);
        Task<DeploymentResult> DeployTemplateAsync(Guid templateId,Guid discoverySessionId);
        Task<DeploymentResult> DeployAllTemplatesAsync(Guid sessionId, Guid discoverySessionId);
        Task<DeploymentSession> CancelDeploymentAsync(Guid sessionId);
    }

    public class DeploymentRequest
    {
        public string Name { get; set; } = string.Empty;
        public Guid DiscoverySessionId { get; set; }
        public string TargetSubscriptionId { get; set; } = string.Empty;
        public string TargetResourceGroup { get; set; } = string.Empty;
        public DeploymentMode Mode { get; set; } = DeploymentMode.Incremental;
        public bool ValidateOnly { get; set; } = false;
        public Dictionary<string, string> Parameters { get; set; } = new();
    }

    public class ValidationResult
    {
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new();
        public List<ValidationWarning> Warnings { get; set; } = new();
        public TimeSpan ValidationDuration { get; set; }
        public DateTime ValidatedAt { get; set; } = DateTime.UtcNow;
    }

    public class ValidationError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public class ValidationWarning
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
    }

    public class DeploymentResult
    {
        public bool IsSuccessful { get; set; }
        public string DeploymentId { get; set; } = string.Empty;
        public DeploymentState State { get; set; }
        public List<DeploymentError> Errors { get; set; } = new();
        public Dictionary<string, object> Outputs { get; set; } = new();
        public TimeSpan DeploymentDuration { get; set; }
        public DateTime DeployedAt { get; set; } = DateTime.UtcNow;
    }

    public class DeploymentError
    {
        public string Code { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
    }

    public enum DeploymentState
    {
        NotStarted,
        Running,
        Succeeded,
        Failed,
        Canceled
    }
}
