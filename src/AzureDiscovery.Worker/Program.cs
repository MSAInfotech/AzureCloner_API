using AzureDiscovery.Infrastructure.Configuration;
using AzureDiscovery.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration
builder.Services.Configure<AzureDiscoveryOptions>(
    builder.Configuration.GetSection(AzureDiscoveryOptions.SectionName));

// Add Application Insights
if (!string.IsNullOrEmpty(builder.Configuration["AzureDiscovery:ApplicationInsightsConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetryWorkerService(options =>
    {
        options.ConnectionString = builder.Configuration["AzureDiscovery:ApplicationInsightsConnectionString"];
    });
}

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
