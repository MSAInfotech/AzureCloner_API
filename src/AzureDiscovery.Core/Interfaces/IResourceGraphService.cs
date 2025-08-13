using System.Collections.Generic;
using System.Threading.Tasks;
using AzureDiscovery.Core.Models;

namespace AzureDiscovery.Core.Interfaces
{
    public interface IResourceGraphService
    {
        Task<List<ResourceGraphResult>> DiscoverResourcesAsync(
            string subscriptionId,
            List<string> resourceGroupFilters,
            List<string> resourceTypeFilters,
            Guid discoverySessionId);
    }
}
