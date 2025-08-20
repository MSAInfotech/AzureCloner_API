using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models.Messages
{
    public class TemplateDeploymentResultMessage
    {
        public Guid TemplateId { get; set; }
        public Guid DeploymentSessionId { get; set; }
        public Guid DiscoverySessionId { get; set; }
        public bool IsSuccess { get; set; }
        public string DeploymentResult { get; set; }
        public DateTime CompletedAt { get; set; }
    }
}
