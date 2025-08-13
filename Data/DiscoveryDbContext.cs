using Microsoft.EntityFrameworkCore;
using AzureDiscovery.Models;
using System.Text.Json;

namespace AzureDiscovery.Data
{
    public class DiscoveryDbContext : DbContext
    {
        public DiscoveryDbContext(DbContextOptions<DiscoveryDbContext> options) : base(options) { }

        public DbSet<AzureResource> AzureResources { get; set; }
        public DbSet<ResourceDependency> ResourceDependencies { get; set; }
        public DbSet<DiscoverySession> DiscoverySessions { get; set; }

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
                        v => JsonDocument.Parse(v));
                
                entity.Property(e => e.Tags)
                    .HasConversion(
                        v => v.RootElement.GetRawText(),
                        v => JsonDocument.Parse(v));
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
        }
    }
}
