using AzureDiscovery.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace AzureDiscovery.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly AzureDiscoveryOptions _options;

    public Worker(ILogger<Worker> logger, IOptions<AzureDiscoveryOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
            }
            
            // TODO: Implement background processing logic
            // - Process discovery queue messages
            // - Handle resource analysis
            // - Generate ARM templates
            
            await Task.Delay(1000, stoppingToken);
        }
    }
}
