using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Azure;
using AzureDiscovery.Data;
using AzureDiscovery.Services;
using AzureDiscovery.Configuration;
using Azure.Identity;

var builder = WebApplication.CreateBuilder(args);

// Add configuration
builder.Configuration.AddAzureKeyVault(
    new Uri(builder.Configuration["AzureDiscovery:KeyVaultUrl"]!),
    new DefaultAzureCredential());

// Add services
builder.Services.Configure<AzureDiscoveryOptions>(
    builder.Configuration.GetSection(AzureDiscoveryOptions.SectionName));

// Add Entity Framework
builder.Services.AddDbContext<DiscoveryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// Add Azure services
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.UseCredential(new DefaultAzureCredential());
});

// Add application services
builder.Services.AddScoped<IAzureResourceDiscoveryService, AzureResourceDiscoveryService>();
builder.Services.AddScoped<IResourceGraphService, ResourceGraphService>();
builder.Services.AddScoped<IServiceBusService, ServiceBusService>();
builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();

// Add Application Insights
builder.Services.AddApplicationInsightsTelemetry(options =>
{
    options.ConnectionString = builder.Configuration["AzureDiscovery:ApplicationInsightsConnectionString"];
});

// Add controllers
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks
builder.Services.AddHealthChecks()
    .AddDbContext<DiscoveryDbContext>()
    .AddAzureServiceBusQueue(
        builder.Configuration["AzureDiscovery:ServiceBusConnectionString"]!,
        "discovery-queue")
    .AddAzureBlobStorage(
        builder.Configuration["AzureDiscovery:StorageConnectionString"]!);

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapHealthChecks("/health");

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
    await context.Database.EnsureCreatedAsync();
}

app.Run();
