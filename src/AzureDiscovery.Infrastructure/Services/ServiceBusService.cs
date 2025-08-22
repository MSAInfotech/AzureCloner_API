using Azure.Messaging.ServiceBus;
using AzureDiscovery.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
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
        private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new();
        private readonly ConcurrentDictionary<string, ServiceBusReceiver> _receivers = new();
        private readonly ConcurrentDictionary<string, ServiceBusProcessor> _processors = new();
        private readonly SemaphoreSlim _processorSemaphore = new(1, 1);

        public ServiceBusService(ILogger<ServiceBusService> logger, IOptions<AzureDiscoveryOptions> options)
        {
            _logger = logger;
            var connectionString = options.Value.ServiceBusConnectionString;

            if (string.IsNullOrEmpty(connectionString))
            {
                _logger.LogError("Service Bus connection string is null or empty");
                throw new InvalidOperationException("Service Bus connection string is required");
            }

            try
            {
                _client = new ServiceBusClient(connectionString);
                _logger.LogInformation("Service Bus client initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Service Bus client");
                throw;
            }
        }

        public async Task SendMessageAsync<T>(string queueName, T message)
        {
            if (_client == null)
            {
                _logger.LogError("Service Bus client is null. Cannot send message to queue {QueueName}", queueName);
                throw new InvalidOperationException("Service Bus client is not initialized");
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
                _logger.LogInformation("Sent message to queue {QueueName}: {MessageId}", queueName, serviceBusMessage.MessageId);
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
                _logger.LogError("Service Bus client is null");
                throw new InvalidOperationException("Service Bus client is not initialized");
            }

            try
            {
                var receiver = GetReceiver(queueName);
                //amount of time the method will wait to receive a message before giving up.
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
                _logger.LogError("Service Bus client is null");
                throw new InvalidOperationException("Service Bus client is not initialized");
            }

            await _processorSemaphore.WaitAsync(cancellationToken);
            try
            {
                _logger.LogInformation("Starting processor for queue {QueueName}", queueName);

                var processor = GetProcessor(queueName);

                // Check if processor is already running
                if (processor.IsProcessing)
                {
                    _logger.LogWarning("Processor for queue {QueueName} is already running", queueName);
                    return;
                }

                processor.ProcessMessageAsync += async args =>
                {
                    try
                    {
                        _logger.LogDebug("Processing message from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);

                        var json = args.Message.Body.ToString();
                        var message = JsonSerializer.Deserialize<T>(json);

                        if (message != null)
                        {
                            await messageHandler(message);
                            _logger.LogInformation("Successfully processed message from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);
                        }
                        else
                        {
                            _logger.LogWarning("Deserialized message is null for queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);
                        }

                        await args.CompleteMessageAsync(args.Message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);

                        // Dead letter the message after max delivery count attempts
                        try
                        {
                            await args.DeadLetterMessageAsync(args.Message, "ProcessingError", ex.Message);
                            _logger.LogWarning("Message dead-lettered from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);
                        }
                        catch (Exception deadLetterEx)
                        {
                            _logger.LogError(deadLetterEx, "Failed to dead letter message from queue {QueueName}: {MessageId}", queueName, args.Message.MessageId);
                            await args.AbandonMessageAsync(args.Message);
                        }
                    }
                };

                processor.ProcessErrorAsync += args =>
                {
                    _logger.LogError(args.Exception, "Service Bus processor error for queue {QueueName}. Source: {ErrorSource}", queueName, args.ErrorSource);
                    return Task.CompletedTask;
                };

                // Register for cancellation to stop processing gracefully
                cancellationToken.Register(async () =>
                {
                    _ = Task.Run(async () =>
                    {
                        _logger.LogInformation("Cancellation requested, stopping processor for queue {QueueName}", queueName);
                        try
                        {
                            if (processor.IsProcessing)
                            {
                                await processor.StopProcessingAsync();
                                _logger.LogInformation("Processor stopped for queue {QueueName}", queueName);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error stopping processor for queue {QueueName}", queueName);
                        }
                    });
                });

                await processor.StartProcessingAsync(cancellationToken);
                _logger.LogInformation("Successfully started Service Bus processor for queue {QueueName}", queueName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start processor for queue {QueueName}", queueName);
                throw;
            }
            finally
            {
                _processorSemaphore.Release();
            }
        }

        private ServiceBusSender GetSender(string queueName)
        {
            if (!_senders.TryGetValue(queueName, out var sender))
            {
                sender = _client.CreateSender(queueName);
                _senders[queueName] = sender;
                _logger.LogDebug("Created new sender for queue {QueueName}", queueName);
            }
            return sender;
        }

        private ServiceBusReceiver GetReceiver(string queueName)
        {
            if (!_receivers.TryGetValue(queueName, out var receiver))
            {
                receiver = _client.CreateReceiver(queueName);
                _receivers[queueName] = receiver;
                _logger.LogDebug("Created new receiver for queue {QueueName}", queueName);
            }
            return receiver;
        }

        private ServiceBusProcessor GetProcessor(string queueName)
        {
            if (!_processors.TryGetValue(queueName, out var processor))
            {
                // Configure processor options for better reliability
                var processorOptions = new ServiceBusProcessorOptions
                {
                    AutoCompleteMessages = false,
                    MaxConcurrentCalls = 10, // or from config
                    PrefetchCount = 20,     // optional, improves performance
                    ReceiveMode = ServiceBusReceiveMode.PeekLock
                };
                processor = _client.CreateProcessor(queueName, processorOptions);
                _processors[queueName] = processor;
                _logger.LogDebug("Created new processor for queue {QueueName}", queueName);
            }
            return processor;
        }

        public void Dispose()
        {
            _logger.LogInformation("Disposing ServiceBusService");

            try
            {
                // Stop all processors first
                var stopTasks = _processors.Values
                    .Where(p => p.IsProcessing)
                    .Select(async p =>
                    {
                        try
                        {
                            await p.StopProcessingAsync();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error stopping processor during dispose");
                        }
                    });

                Task.WaitAll(stopTasks.ToArray(), TimeSpan.FromSeconds(30));

                // Dispose all resources
                foreach (var sender in _senders.Values)
                    sender?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));

                foreach (var receiver in _receivers.Values)
                    receiver?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));

                foreach (var processor in _processors.Values)
                    processor?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));

                _client?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
                _processorSemaphore?.Dispose();

                _logger.LogInformation("ServiceBusService disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during ServiceBusService disposal");
            }
        }
    }
}