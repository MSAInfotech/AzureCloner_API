using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models.Messages
{
    public class TemplateValidationMessage
    {
        public Guid TemplateId { get; set; }
        public Guid DeploymentSessionId { get; set; }
        public Guid DiscoverySessionId { get; set; }
        public bool IsValid { get; set; }
        public string ValidationResult { get; set; }
        public DateTime ValidatedAt { get; set; }
    }
}
