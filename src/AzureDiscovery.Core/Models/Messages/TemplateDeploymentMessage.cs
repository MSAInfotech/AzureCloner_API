using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models.Messages
{
    public class TemplateDeploymentMessage
    {
        public Guid TemplateId { get; set; }
        public Guid DeploymentSessionId { get; set; }
        public Guid DiscoverySessionId { get; set; }
        public string TemplateName { get; set; }
        public int DependencyLevel { get; set; }
        public DateTime RequestedAt { get; set; }
    }
}
