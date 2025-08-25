using Azure.Identity;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Infrastructure.Configuration;
using AzureDiscovery.Infrastructure.Data;
using AzureDiscovery.Infrastructure.Helpers;
using AzureDiscovery.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Azure;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Polly.Timeout;
using System.Text;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// Add configuration
if (!string.IsNullOrEmpty(builder.Configuration["AzureDiscovery:KeyVaultUrl"]))
{
    builder.Configuration.AddAzureKeyVault(
        new Uri(builder.Configuration["AzureDiscovery:KeyVaultUrl"]!),
        new DefaultAzureCredential());
}

// Add services
builder.Services.Configure<AzureDiscoveryOptions>(
    builder.Configuration.GetSection(AzureDiscoveryOptions.SectionName));

builder.Services.Configure<AzureAuthenticationOptions>(
    builder.Configuration.GetSection(AzureAuthenticationOptions.SectionName));

// Add Entity Framework
builder.Services.AddDbContext<DiscoveryDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"),
    x => x.MigrationsAssembly("AzureDiscovery.Infrastructure")));
//builder.Services.AddDbContext<DiscoveryDbContext>(options =>
//    options.UseCosmos(
//        builder.Configuration["AzureDiscovery:CosmosDbConnectionString"],
//            builder.Configuration["AzureDiscovery:CosmosDbDatabaseName"]
//        ));

// Add authentication service
builder.Services.AddScoped<IAzureAuthenticationService, AzureAuthenticationService>();
// Add HttpClient for Resource Graph API
builder.Services.AddHttpClient<IResourceGraphService, ResourceGraphService>();

// Add Azure services
builder.Services.AddAzureClients(clientBuilder =>
{
    clientBuilder.UseCredential(new DefaultAzureCredential());
});

// Add resilient HTTP client
builder.Services.AddHttpClient("ResilientClient")
    .AddTransientHttpErrorPolicy(policyBuilder =>
        policyBuilder.WaitAndRetryAsync(3, retryAttempt =>
            TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))))
    .AddTransientHttpErrorPolicy(policyBuilder =>
        policyBuilder.CircuitBreakerAsync(5, TimeSpan.FromSeconds(30)))
    .AddPolicyHandler((request, cancellationToken) =>
    {
        return Policy.TimeoutAsync<HttpResponseMessage>(context =>{
                if (context.TryGetValue("Timeout", out var timeoutObj) && timeoutObj is TimeSpan timeout){
                    return timeout;
                }return TimeSpan.FromSeconds(10);
            },
    TimeoutStrategy.Optimistic);});

// Add application services
builder.Services.AddScoped<IResourceGraphService, ResourceGraphService>();
builder.Services.AddScoped<IServiceBusService, ServiceBusService>();
builder.Services.AddHostedService<TemplateProcessorService>();
builder.Services.AddScoped<IBlobStorageService, BlobStorageService>();
builder.Services.AddScoped<IResourceDependencyAnalyzer, ResourceDependencyAnalyzer>();
builder.Services.AddScoped<IDiscoveryService, AzureResourceDiscoveryService>();
builder.Services.AddScoped<IAzureConnectionService, AzureConnectionService>();

// Add deployment services
builder.Services.AddHttpClient<IAzureResourceManagerService, AzureResourceManagerService>();
builder.Services.AddScoped<IAzureResourceManagerService, AzureResourceManagerService>();
builder.Services.AddScoped<IDeploymentService, AzureDeploymentService>();

// Terraform deployment service
builder.Services.AddHttpClient<ITerraformResourceManagerService, TerraformResourceManagerService>();
builder.Services.AddScoped<ITerraformResourceManagerService, TerraformResourceManagerService>();
//builder.Services.AddScoped<IDeploymentService, TerraformAzureDeploymentService>();

// Add userAccount service
builder.Services.AddScoped<IUserAccountService, UserAccountService>();
builder.Services.AddSingleton<EmailSender>();

// Add Application Insights
if (!string.IsNullOrEmpty(builder.Configuration["AzureDiscovery:ApplicationInsightsConnectionString"]))
{
    builder.Services.AddApplicationInsightsTelemetry(options =>
    {
        options.ConnectionString = builder.Configuration["AzureDiscovery:ApplicationInsightsConnectionString"];
    });
}

// Add controllers
builder.Services.AddControllers()
.AddJsonOptions(options =>
 {
     options.JsonSerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles;
 });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add health checks
var healthChecksBuilder = builder.Services.AddHealthChecks()
    .AddDbContextCheck<DiscoveryDbContext>();

if (!string.IsNullOrEmpty(builder.Configuration["AzureDiscovery:ServiceBusConnectionString"]))
{
    healthChecksBuilder.AddAzureServiceBusQueue(
        builder.Configuration["AzureDiscovery:ServiceBusConnectionString"]!,
        "discovery-queue");
}

if (!string.IsNullOrEmpty(builder.Configuration["AzureDiscovery:StorageConnectionString"]))
{
    healthChecksBuilder.AddAzureBlobStorage(
        builder.Configuration["AzureDiscovery:StorageConnectionString"]!);
}

builder.Services.AddAuthentication("Bearer")
    .AddJwtBearer("Bearer", options =>
    {
        var config = builder.Configuration;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config["JwtSettings:Issuer"],
            ValidAudience = config["JwtSettings:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["JwtSettings:SecretKey"]))
        };
    });
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReact",
        policy => policy.AllowAnyOrigin()
        .AllowAnyMethod()
        .AllowAnyHeader()
        .SetIsOriginAllowed(origin => true));
});

var app = builder.Build();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseCors("AllowReact");
app.MapControllers();
app.MapHealthChecks("/health");

// Ensure database is created
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<DiscoveryDbContext>();
    await context.Database.EnsureCreatedAsync();
}

app.Run();
