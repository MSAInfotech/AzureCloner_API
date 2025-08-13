using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace AzureDiscovery.Models
{
    public class AzureResource
    {
        [Key]
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string Subscription { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public JsonDocument Properties { get; set; } = JsonDocument.Parse("{}");
        public JsonDocument Tags { get; set; } = JsonDocument.Parse("{}");
        public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
        public ResourceStatus Status { get; set; } = ResourceStatus.Discovered;
        public string? ParentResourceId { get; set; }
        public int DependencyLevel { get; set; } = 0;
        
        // Navigation properties
        public virtual ICollection<ResourceDependency> Dependencies { get; set; } = new List<ResourceDependency>();
        public virtual ICollection<ResourceDependency> DependentResources { get; set; } = new List<ResourceDependency>();
    }

    public enum ResourceStatus
    {
        Discovered,
        Analyzed,
        TemplateGenerated,
        ReadyForCloning,
        Cloning,
        Cloned,
        Failed
    }
}
