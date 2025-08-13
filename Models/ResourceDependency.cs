using System.ComponentModel.DataAnnotations;

namespace AzureDiscovery.Models
{
    public class ResourceDependency
    {
        [Key]
        public int Id { get; set; }
        public string SourceResourceId { get; set; } = string.Empty;
        public string TargetResourceId { get; set; } = string.Empty;
        public DependencyType Type { get; set; }
        public bool IsRequired { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public virtual AzureResource SourceResource { get; set; } = null!;
        public virtual AzureResource TargetResource { get; set; } = null!;
    }

    public enum DependencyType
    {
        NetworkDependency,
        StorageDependency,
        IdentityDependency,
        ConfigurationDependency,
        ParentChild,
        CrossResourceGroup
    }
}
