using Microsoft.Extensions.Configuration;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace AzureDiscovery.Infrastructure.Helpers
{
    public class EmailSender
    {
        private readonly string _sendGridApiKey;
        private readonly string _fromEmail;

        public EmailSender(IConfiguration config)
        {
            _sendGridApiKey = config["SendGrid:ApiKey"];
            _fromEmail = config["SendGrid:FromEmail"];
        }

        public async Task<Response> SendEmailAsync(string to, string subject, string html)
        {
            var client = new SendGridClient(_sendGridApiKey);
            var msg = MailHelper.CreateSingleEmail(
                new EmailAddress(_fromEmail, "Azure Clonner"),
                new EmailAddress(to),
                subject,
                plainTextContent: "",
                htmlContent: html
            );
            var response = await client.SendEmailAsync(msg);
            return response;
        }
    }
}
