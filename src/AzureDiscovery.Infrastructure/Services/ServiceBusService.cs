using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Infrastructure.Configuration;
using System.Text.Json;

namespace AzureDiscovery.Infrastructure.Services
{
    public interface IServiceBusService
    {
        Task SendMessageAsync<T>(string queueName, T message);
        Task<T?> ReceiveMessageAsync<T>(string queueName, CancellationToken cancellationToken = default);
        Task StartProcessorAsync<T>(string queueName, Func<T, Task> messageHandler, CancellationToken cancellationToken = default);
    }

    public class ServiceBusService : IServiceBusService, IDisposable
    {
        private readonly ServiceBusClient _client;
        private readonly ILogger<ServiceBusService> _logger;
        private readonly Dictionary<string, ServiceBusSender> _senders = new();
        private readonly Dictionary<string, ServiceBusReceiver> _receivers = new();
        private readonly Dictionary<string, ServiceBusProcessor> _processors = new();

        public ServiceBusService(ILogger<ServiceBusService> logger, IOptions<AzureDiscoveryOptions> options)
        {
            var connectionString = options.Value.ServiceBusConnectionString;
            if (!string.IsNullOrEmpty(connectionString))
            {
                _client = new ServiceBusClient(connectionString);
            }
            else
            {
                _logger.LogWarning("Service Bus connection string not configured");
            }
            _logger = logger;
        }

        public async Task SendMessageAsync<T>(string queueName, T message)
        {
            if (_client == null)
            {
                _logger.LogWarning("Service Bus client not initialized. Message not sent to queue {QueueName}", queueName);
                return;
            }

            try
            {
                var sender = GetSender(queueName);
                var json = JsonSerializer.Serialize(message);
                var serviceBusMessage = new ServiceBusMessage(json)
                {
                    ContentType = "application/json",
                    MessageId = Guid.NewGuid().ToString(),
                    Subject = typeof(T).Name
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
            if (_client == null)
            {
                _logger.LogWarning("Service Bus client not initialized");
                return default;
            }

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

        public async Task StartProcessorAsync<T>(string queueName, Func<T, Task> messageHandler, CancellationToken cancellationToken = default)
        {
            if (_client == null)
            {
                _logger.LogWarning("Service Bus client not initialized");
                return;
            }

            try
            {
                var processor = GetProcessor(queueName);
                
                processor.ProcessMessageAsync += async args =>
                {
                    try
                    {
                        var json = args.Message.Body.ToString();
                        var message = JsonSerializer.Deserialize<T>(json);
                        
                        if (message != null)
                        {
                            await messageHandler(message);
                        }
                        
                        await args.CompleteMessageAsync(args.Message);
                        _logger.LogDebug("Processed message from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);
                        await args.AbandonMessageAsync(args.Message);
                    }
                };

                processor.ProcessErrorAsync += args =>
                {
                    _logger.LogError(args.Exception, "Error in Service Bus processor for queue {QueueName}", queueName);
                    return Task.CompletedTask;
                };

                await processor.StartProcessingAsync(cancellationToken);
                _logger.LogInformation("Started Service Bus processor for queue {QueueName}", queueName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start processor for queue {QueueName}", queueName);
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

        private ServiceBusProcessor GetProcessor(string queueName)
        {
            if (!_processors.TryGetValue(queueName, out var processor))
            {
                processor = _client.CreateProcessor(queueName);
                _processors[queueName] = processor;
            }
            return processor;
        }

        public void Dispose()
        {
            foreach (var sender in _senders.Values)
                sender?.DisposeAsync().AsTask().Wait();
            
            foreach (var receiver in _receivers.Values)
                receiver?.DisposeAsync().AsTask().Wait();
            
            foreach (var processor in _processors.Values)
            {
                processor?.StopProcessingAsync().Wait();
                processor?.DisposeAsync().AsTask().Wait();
            }
            
            _client?.DisposeAsync().AsTask().Wait();
        }
    }
}
