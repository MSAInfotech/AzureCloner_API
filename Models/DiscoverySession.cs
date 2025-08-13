using System.ComponentModel.DataAnnotations;

namespace AzureDiscovery.Models
{
    public class DiscoverySession
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string SourceSubscriptionId { get; set; } = string.Empty;
        public string TargetSubscriptionId { get; set; } = string.Empty;
        public List<string> ResourceGroupFilters { get; set; } = new();
        public List<string> ResourceTypeFilters { get; set; } = new();
        public SessionStatus Status { get; set; } = SessionStatus.Created;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public int TotalResourcesDiscovered { get; set; } = 0;
        public int ResourcesProcessed { get; set; } = 0;
        public string? ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    public enum SessionStatus
    {
        Created,
        InProgress,
        Completed,
        Failed,
        Cancelled
    }
}
