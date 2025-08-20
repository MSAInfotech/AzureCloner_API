using Microsoft.EntityFrameworkCore;
using AzureDiscovery.Core.Models;
using System.Text.Json;
using AzureDiscovery.Core.Model;

namespace AzureDiscovery.Infrastructure.Data
{
    public class DiscoveryDbContext : DbContext
    {
        public DiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : base(options) { }

        public DbSet<AzureResource> AzureResources { get; set; }
        public DbSet<ResourceDependency> ResourceDependencies { get; set; }
        public DbSet<DiscoverySession> DiscoverySessions { get; set; }
        public DbSet<DeploymentSession> DeploymentSessions { get; set; }
        public DbSet<TemplateDeployment> TemplateDeployments { get; set; }
        public DbSet<User> Users { get; set; }
        public DbSet<EmailTemplate> EmailTemplates { get; set; }
        public DbSet<AzureConnection> AzureConnections { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            // Configure AzureResource
            modelBuilder.Entity<AzureResource>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => new { e.Subscription, e.ResourceGroup });
                entity.HasIndex(e => e.Type);
                entity.HasIndex(e => e.Status);
                
                entity.Property(e => e.Properties)
                    .HasConversion(
                        v => v.RootElement.GetRawText(),
                        v => JsonDocument.Parse(v,new JsonDocumentOptions()));
                entity.Property(e => e.Sku)
                    .HasConversion(
                        v => v.RootElement.GetRawText(),
                        v => JsonDocument.Parse(v, new JsonDocumentOptions()));

                entity.Property(e => e.Identity)
                    .HasConversion(
                        v => v.RootElement.GetRawText(),
                        v => JsonDocument.Parse(v, new JsonDocumentOptions()));

                entity.Property(e => e.Plan)
                    .HasConversion(
                        v => v.RootElement.GetRawText(),
                        v => JsonDocument.Parse(v, new JsonDocumentOptions()));

                entity.Property(e => e.Tags)
                    .HasConversion(
                        v => v.RootElement.GetRawText(),
                        v => JsonDocument.Parse(v, new JsonDocumentOptions()));
                entity.Property(e => e.ApiVersion)
                    .HasColumnName("ApiVersion");

            });

            // Configure ResourceDependency relationships
            modelBuilder.Entity<ResourceDependency>(entity =>
            {
                entity.HasKey(e => e.Id);
                
                entity.HasOne(d => d.SourceResource)
                    .WithMany(p => p.Dependencies)
                    .HasForeignKey(d => d.SourceResourceId)
                    .OnDelete(DeleteBehavior.Cascade);
                
                entity.HasOne(d => d.TargetResource)
                    .WithMany(p => p.DependentResources)
                    .HasForeignKey(d => d.TargetResourceId)
                    .OnDelete(DeleteBehavior.Restrict);
                
                entity.HasIndex(e => new { e.SourceResourceId, e.TargetResourceId }).IsUnique();
            });

            // Configure DiscoverySession
            modelBuilder.Entity<DiscoverySession>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.StartedAt);

                // Foreign key configuration
                entity.HasOne(d => d.AzureConnection)
                      .WithMany()
                      .HasForeignKey(d => d.ConnectionId)
                      .OnDelete(DeleteBehavior.Restrict);

                entity.Property(e => e.ResourceGroupFilters)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
                
                entity.Property(e => e.ResourceTypeFilters)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());
                
                entity.Property(e => e.Metadata)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, object>());
            });

            // Configure DeploymentSession
            modelBuilder.Entity<DeploymentSession>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.StartedAt);
                
                entity.HasOne(d => d.DiscoverySession)
                    .WithMany()
                    .HasForeignKey(d => d.DiscoverySessionId)
                    .OnDelete(DeleteBehavior.Cascade);
                
                entity.Property(e => e.DeploymentResults)
                    .HasConversion(
                        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                        v => JsonSerializer.Deserialize<Dictionary<string, object>>(v, (JsonSerializerOptions?)null) ?? new Dictionary<string, object>());
            });

            // Configure TemplateDeployment
            modelBuilder.Entity<TemplateDeployment>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Status);
                entity.HasIndex(e => e.DependencyLevel);
                
                entity.HasOne(d => d.DeploymentSession)
                    .WithMany(p => p.TemplateDeployments)
                    .HasForeignKey(d => d.DeploymentSessionId)
                    .OnDelete(DeleteBehavior.Cascade);
            });
            // Configure User
            modelBuilder.Entity<User>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Email);
            });

            // Configure EmailTemplate
            modelBuilder.Entity<EmailTemplate>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.TemplateKey);
            });

            // Configure AzureConnection
            modelBuilder.Entity<AzureConnection>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.ClientId);
            });

        }
    }
}
