using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AzureDiscovery.Core.Models
{
    public class DeploymentSession
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public Guid DiscoverySessionId { get; set; }
        public string TargetSubscriptionId { get; set; } = string.Empty;
        public string TargetResourceGroup { get; set; } = string.Empty;
        public DeploymentMode Mode { get; set; } = DeploymentMode.Incremental;
        public DeploymentStatus Status { get; set; } = DeploymentStatus.Created;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public int TotalTemplates { get; set; } = 0;
        public int TemplatesDeployed { get; set; } = 0;
        public int TemplatesFailed { get; set; } = 0;
        public string? ErrorMessage { get; set; }
        public Dictionary<string, object> DeploymentResults { get; set; } = new();

        // Navigation properties
        [JsonIgnore]
        public virtual DiscoverySession DiscoverySession { get; set; } = null!;
        [JsonIgnore]
        public virtual ICollection<TemplateDeployment> TemplateDeployments { get; set; } = new List<TemplateDeployment>();
    }

    public class TemplateDeployment
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid DeploymentSessionId { get; set; }
        public string TemplateName { get; set; } = string.Empty;
        public string ResourceGroupName { get; set; } = string.Empty;
        public string TemplateContent { get; set; } = string.Empty;
        public string ParametersContent { get; set; } = string.Empty;
        public TemplateStatus Status { get; set; } = TemplateStatus.Created;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ValidatedAt { get; set; }
        public DateTime? DeployedAt { get; set; }
        public string? ValidationResults { get; set; }
        public string? DeploymentResults { get; set; }
        public string? ErrorMessage { get; set; }
        public int DependencyLevel { get; set; } = 0;
        // Navigation properties
        [JsonIgnore]
        public virtual DeploymentSession DeploymentSession { get; set; } = null!;
    }

    public enum DeploymentMode
    {
        Incremental,
        Complete
    }

    public enum DeploymentStatus
    {
        Created,
        Validating,
        ValidationFailed,
        ValidationPassed,
        Deploying,
        PartiallyDeployed,
        Deployed,
        Failed,
        Cancelled
    }

    public enum TemplateStatus
    {
        Created,
        Validating,
        ValidationFailed,
        ValidationPassed,
        Queued,
        Deploying,
        Deployed,
        Failed,
        Skipped
    }
}
