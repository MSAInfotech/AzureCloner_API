using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Configuration;
using System.Text.Json;

namespace AzureDiscovery.Services
{
    public interface IServiceBusService
    {
        Task SendMessageAsync<T>(string queueName, T message);
        Task<T?> ReceiveMessageAsync<T>(string queueName, CancellationToken cancellationToken = default);
    }

    public class ServiceBusService : IServiceBusService, IDisposable
    {
        private readonly ServiceBusClient _client;
        private readonly ILogger<ServiceBusService> _logger;
        private readonly Dictionary<string, ServiceBusSender> _senders = new();
        private readonly Dictionary<string, ServiceBusReceiver> _receivers = new();

        public ServiceBusService(ILogger<ServiceBusService> logger, IOptions<AzureDiscoveryOptions> options)
        {
            _client = new ServiceBusClient(options.Value.ServiceBusConnectionString);
            _logger = logger;
        }

        public async Task SendMessageAsync<T>(string queueName, T message)
        {
            try
            {
                var sender = GetSender(queueName);
                var json = JsonSerializer.Serialize(message);
                var serviceBusMessage = new ServiceBusMessage(json)
                {
                    ContentType = "application/json",
                    MessageId = Guid.NewGuid().ToString()
                };

                await sender.SendMessageAsync(serviceBusMessage);
                _logger.LogDebug("Sent message to queue {QueueName}: {MessageId}", queueName, serviceBusMessage.MessageId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send message to queue {QueueName}", queueName);
                throw;
            }
        }

        public async Task<T?> ReceiveMessageAsync<T>(string queueName, CancellationToken cancellationToken = default)
        {
            try
            {
                var receiver = GetReceiver(queueName);
                var message = await receiver.ReceiveMessageAsync(TimeSpan.FromSeconds(30), cancellationToken);
                
                if (message == null)
                    return default;

                var json = message.Body.ToString();
                var result = JsonSerializer.Deserialize<T>(json);
                
                await receiver.CompleteMessageAsync(message, cancellationToken);
                _logger.LogDebug("Received and completed message from queue {QueueName}: {MessageId}", queueName, message.MessageId);
                
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to receive message from queue {QueueName}", queueName);
                throw;
            }
        }

        private ServiceBusSender GetSender(string queueName)
        {
            if (!_senders.TryGetValue(queueName, out var sender))
            {
                sender = _client.CreateSender(queueName);
                _senders[queueName] = sender;
            }
            return sender;
        }

        private ServiceBusReceiver GetReceiver(string queueName)
        {
            if (!_receivers.TryGetValue(queueName, out var receiver))
            {
                receiver = _client.CreateReceiver(queueName);
                _receivers[queueName] = receiver;
            }
            return receiver;
        }

        public void Dispose()
        {
            foreach (var sender in _senders.Values)
                sender?.DisposeAsync().AsTask().Wait();
            
            foreach (var receiver in _receivers.Values)
                receiver?.DisposeAsync().AsTask().Wait();
            
            _client?.DisposeAsync().AsTask().Wait();
        }
    }
}
