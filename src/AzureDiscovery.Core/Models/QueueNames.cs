using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models
{
    public static class QueueNames
    {
        public const string TemplateCreated = "template-created-queue";
        public const string TemplateValidation = "template-validation-queue";
        public const string TemplateDeployment = "template-deployment-queue";
        public const string TemplateDeploymentResult = "template-deployment-result-queue";
        public const string ResourceDiscovery = "resource-discovery";
    }

}
