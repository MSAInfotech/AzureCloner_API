namespace AzureDiscovery.Core.Model
{
    public class EmailTemplate
    {
        public int Id { get; set; }
        public string TemplateKey { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Body { get; set; } = string.Empty;
    }
}
