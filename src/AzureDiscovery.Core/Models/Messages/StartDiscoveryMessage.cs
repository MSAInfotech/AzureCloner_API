using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models.Messages
{
    public class StartDiscoveryMessage
    {
        public Guid SessionId { get; set; }
    }
}
