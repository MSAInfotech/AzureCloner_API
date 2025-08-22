using AzureDiscovery.Core.Models;

namespace AzureDiscovery.Core.Interfaces
{
    public interface IDiscoveryService
    {
        Task<DiscoveryResult> StartDiscoveryAsync(DiscoveryRequest request);
        Task<DiscoveryResult> GetExistingDiscovery(DiscoveryRequest request);
        Task<DiscoverySession> GetDiscoveryStatusAsync(Guid sessionId);
        Task<List<AzureResource>> GetDiscoveredResourcesAsync(Guid sessionId);
    }

    public class DiscoveryRequest
    {
        public string Name { get; set; } = string.Empty;
        public Guid ConnectionId { get; set; }
        public string SourceSubscriptionId { get; set; } = string.Empty;
        public string TargetSubscriptionId { get; set; } = string.Empty;
        public List<string> ResourceGroupFilters { get; set; } = new();
        public List<string> ResourceTypeFilters { get; set; } = new();
    }
    public class DiscoveryResult
    {
        public DiscoverySession Session { get; set; }
        public List<AzureResource> DiscoveredResources { get; set; }
    }
}
