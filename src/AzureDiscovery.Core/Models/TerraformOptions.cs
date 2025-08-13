using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AzureDiscovery.Core.Models
{
    public class TerraformOptions
    {
        public const string SectionName = "Terraform";

        public string ExecutablePath { get; set; } = "terraform";
        public string WorkingDirectoryRoot { get; set; } = Path.GetTempPath();
    }
}