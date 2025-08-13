using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations.Schema;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models
{
    public class AzureConnection
    {
        [Key]
        public Guid Id { get; set; }

        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;

        [Required]
        [MaxLength(36)]
        public string SubscriptionId { get; set; } = string.Empty;

        [Required]
        [MaxLength(36)]
        public string TenantId { get; set; } = string.Empty;

        [Required]
        [MaxLength(36)]
        public string ClientId { get; set; } = string.Empty;

        [Required]
        public string ClientSecret { get; set; } = string.Empty; // Encrypted

        [Required]
        [MaxLength(20)]
        public string Environment { get; set; } = string.Empty;

        [Required]
        [MaxLength(20)]
        public string Status { get; set; } = "Active";

        public DateTime? LastValidated { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
