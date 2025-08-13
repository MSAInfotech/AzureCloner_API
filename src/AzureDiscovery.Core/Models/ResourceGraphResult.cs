using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models
{
    public class ResourceGraphResult
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string ResourceGroup { get; set; } = string.Empty;
        public string SubscriptionId { get; set; } = string.Empty;
        public string Location { get; set; } = string.Empty;
        public string? Kind { get; set; }
        // New top-level fields
        public Sku? Sku { get; set; }
        public Identity? Identity { get; set; }
        public Plan? Plan { get; set; }

        public object? Properties { get; set; }
        public Dictionary<string, string>? Tags { get; set; }
    }
    public class Sku
    {
        public string? Name { get; set; }
        public string? Tier { get; set; }
    }

    public class Identity
    {
        public string? Type { get; set; }
        public Dictionary<string, UserAssignedIdentity>? UserAssignedIdentities { get; set; }
    }

    public class UserAssignedIdentity
    {
        public string? ClientId { get; set; }
        public string? PrincipalId { get; set; }
    }

    public class Plan
    {
        public string? Name { get; set; }
        public string? Product { get; set; }
        public string? Publisher { get; set; }
        public string? PromotionCode { get; set; }
    }
}
