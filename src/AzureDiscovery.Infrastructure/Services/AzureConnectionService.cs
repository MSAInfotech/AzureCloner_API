using Azure.Identity;
using Azure.ResourceManager;
using AzureDiscovery.Core.Interfaces;
using AzureDiscovery.Core.Models;
using AzureDiscovery.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;

namespace AzureDiscovery.Infrastructure.Services
{

    public class AzureConnectionService : IAzureConnectionService
    {
        private readonly DiscoveryDbContext _context;
        private readonly ILogger<AzureConnectionService> _logger;
        private readonly IConfiguration _configuration;

        public AzureConnectionService(
            DiscoveryDbContext context,
            ILogger<AzureConnectionService> logger,
            IConfiguration configuration)
        {
            _context = context;
            _logger = logger;
            _configuration = configuration;
        }

        public async Task<AzureConnectionValidationResult> ValidateAndSaveConnectionAsync(AzureConnectionRequest request)
        {
            // Step 1: Validate the connection
            var validationResult = await ValidateConnectionAsync(request);

            if (!validationResult.IsValid)
            {
                _logger.LogWarning("Validation failed for Azure connection: {Reason}", validationResult.ErrorMessage);
                throw new InvalidOperationException(validationResult.ErrorMessage);
            }

            await SaveConnectionAsync(request);
            return validationResult;
        }
        public async Task<bool> UpdateConnectionAsync(Guid id, AzureConnectionRequest request)
        {
            var connection = await _context.AzureConnections.FirstOrDefaultAsync(x => x.Id == id);

            if (connection == null)
                return false;

            connection.Name = request.Name;
            connection.SubscriptionId = request.SubscriptionId;
            connection.TenantId = request.TenantId;
            connection.ClientId = request.ClientId;  
            // Only update ClientSecret if it has changed
            var decryptedSecret = DecryptClientSecret(connection.ClientSecret);
            if (decryptedSecret != request.ClientSecret)
            {
                connection.ClientSecret = EncryptClientSecret(request.ClientSecret);
            }
            connection.Environment = request.Environment;
            connection.UpdatedAt = DateTime.UtcNow;
            _context.AzureConnections.Update(connection);
            await _context.SaveChangesAsync();
            return true;
        }
        public async Task<AzureConnectionValidationResult> ValidateConnectionAsync(AzureConnectionRequest request)
        {
            try
            {
                _logger.LogInformation("Starting Azure connection validation for tenant: {TenantId}", request.TenantId);

                // Create credential using the user-provided client ID, client secret, and tenant ID
                var credential = new ClientSecretCredential(
                    request.TenantId,
                    request.ClientId,
                    request.ClientSecret);

                // Create ARM client
                var armClient = new ArmClient(credential);

                // Test 1: Get subscription
                var subscriptionResource = armClient.GetSubscriptionResource(
                    new Azure.Core.ResourceIdentifier($"/subscriptions/{request.SubscriptionId}"));

                var subscription = await subscriptionResource.GetAsync();

                if (subscription?.Value == null)
                {
                    return new AzureConnectionValidationResult
                    {
                        IsValid = false,
                        ErrorMessage = "Subscription not found or access denied"
                    };
                }

                // Test 2: List resource groups (to verify permissions)
                var resourceGroups = subscriptionResource.GetResourceGroups();
                var rgList = new List<string>();

                await foreach (var rg in resourceGroups.GetAllAsync())
                {
                    rgList.Add(rg.Data.Name);
                    if (rgList.Count >= 5) break; // Just test first 5
                }

                _logger.LogInformation("Azure connection validation successful for: {ConnectionName}", request.Name);

                return new AzureConnectionValidationResult
                {
                    IsValid = true,
                    SubscriptionName = subscription.Value.Data.DisplayName,
                    SubscriptionState = subscription.Value.Data.State?.ToString(),
                    ResourceGroupCount = rgList.Count,
                    TestTimestamp = DateTime.UtcNow
                };
            }
            catch (Azure.RequestFailedException ex)
            {
                _logger.LogError(ex, "Azure API error during connection validation");

                string errorMessage = ex.Status switch
                {
                    401 => "Authentication failed. Please check your tenant ID, client ID, and client secret.",
                    403 => "Access denied. Please ensure the service principal has proper permissions.",
                    404 => "Subscription not found. Please check your subscription ID.",
                    _ => $"Azure API error: {ex.Message}"
                };

                return new AzureConnectionValidationResult
                {
                    IsValid = false,
                    ErrorMessage = errorMessage
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during Azure connection validation");

                return new AzureConnectionValidationResult
                {
                    IsValid = false,
                    ErrorMessage = $"Validation failed: {ex.Message}"
                };
            }
        }

        public async Task<AzureConnectionResponse> SaveConnectionAsync(AzureConnectionRequest request)
        {
            try
            {
                var encryptedSecret = EncryptClientSecret(request.ClientSecret);

                var connection = new AzureConnection
                {
                    Id = Guid.NewGuid(),
                    Name = request.Name,
                    SubscriptionId = request.SubscriptionId,
                    TenantId = request.TenantId,
                    ClientId = request.ClientId,
                    ClientSecret = encryptedSecret,
                    Environment = request.Environment,
                    Status = "connected",
                    LastValidated = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                _context.AzureConnections.Add(connection);
                await _context.SaveChangesAsync();

                _logger.LogInformation("Azure connection saved successfully: {ConnectionName} (ID: {ConnectionId})",
                    connection.Name, connection.Id);

                return new AzureConnectionResponse
                {
                    Id = connection.Id,
                    Name = connection.Name,
                    SubscriptionId = connection.SubscriptionId,
                    TenantId = connection.TenantId,
                    ClientId = connection.ClientId,
                    Environment = connection.Environment,
                    Status = connection.Status,
                    LastValidated = connection.LastValidated,
                    CreatedAt = connection.CreatedAt
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving Azure connection");
                throw new InvalidOperationException("Failed to save connection to database", ex);
            }
        }
        public async Task<AzureConnectionResponse> GetConnectionsById(Guid id)
        {
            var query = await _context.AzureConnections.FirstOrDefaultAsync(x => x.Id == id);

            if (query == null)
                return null;

            var decryptedSecret = DecryptClientSecret(query.ClientSecret);

            return new AzureConnectionResponse
            {
                Id = query.Id,
                Name = query.Name,
                SubscriptionId = query.SubscriptionId,
                TenantId = query.TenantId,
                ClientId = query.ClientId,
                Environment = query.Environment,
                Status = query.Status,
                LastValidated = query.LastValidated,
                CreatedAt = query.CreatedAt,
                ClientSecret = decryptedSecret
            };
        }

        public async Task<List<AzureConnectionResponse>> GetConnectionsAsync(string? environment = null)
        {
            var query = _context.AzureConnections.AsQueryable();

            if (!string.IsNullOrEmpty(environment))
            {
                query = query.Where(c => c.Environment == environment);
            }

            var connections = await query
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new AzureConnectionResponse
                {
                    Id = c.Id,
                    Name = c.Name,
                    SubscriptionId = c.SubscriptionId,
                    TenantId = c.TenantId,
                    ClientId = c.ClientId,
                    Environment = c.Environment,
                    Status = c.Status,
                    LastValidated = c.LastValidated,
                    CreatedAt = c.CreatedAt,
                    ClientSecret = c.ClientSecret
                })
                .ToListAsync();

            return connections;
        }
        public async Task<AzureConnectionResponse> GetConnectionsByIdAsync(Guid id)
        {
            var query = await _context.AzureConnections.Where(x => x.Id == id).FirstOrDefaultAsync();
            if (query == null)
                return null;

            var decryptClientSecret = DecryptClientSecret(query.ClientSecret);

            var AzureConnectionResponse = new AzureConnectionResponse
            {
                Id = query.Id,
                Name = query.Name,
                SubscriptionId = query.SubscriptionId,
                TenantId = query.TenantId,
                ClientId = query.ClientId,
                Environment = query.Environment,
                Status = query.Status,
                LastValidated = query.LastValidated,
                ClientSecret = decryptClientSecret
            };

            return AzureConnectionResponse;
        }
        public async Task<List<AzureConnectionResponse>> GetConnectionIfUsedInDiscovery()
        {
            var connections = await _context.AzureConnections
                .Where(conn =>
                    _context.DiscoverySessions
                        .Select(s => s.ConnectionId)
                        .Distinct()
                        .Contains(conn.Id))
                .Select(c => new AzureConnectionResponse
                {
                    Id = c.Id,
                    Name = c.Name,
                    SubscriptionId = c.SubscriptionId,
                    TenantId = c.TenantId,
                    ClientId = c.ClientId,
                    Environment = c.Environment,
                    Status = c.Status,
                    LastValidated = c.LastValidated,
                    CreatedAt = c.CreatedAt,

                    // Fetch latest DiscoverySession Id for this connection
                    LatestSessionId = _context.DiscoverySessions
                .Where(s => s.ConnectionId == c.Id)
                .OrderByDescending(s => s.StartedAt)
                .Select(s => s.Id)
                .FirstOrDefault()
                })
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            return connections;
        }

        public async Task<bool> DeleteConnectionAsync(Guid id)
        {
            var connection = await _context.AzureConnections.FindAsync(id);

            if (connection == null)
            {
                return false;
            }

            _context.AzureConnections.Remove(connection);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Azure connection deleted: {ConnectionId}", id);
            return true;
        }

        private string EncryptClientSecret(string clientSecret)
        {
            var key = _configuration["EncryptionKey"] ?? throw new InvalidOperationException("EncryptionKey not configured");

            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));
            aes.GenerateIV();

            using var encryptor = aes.CreateEncryptor();
            var plainBytes = Encoding.UTF8.GetBytes(clientSecret);
            var encryptedBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

            var result = new byte[aes.IV.Length + encryptedBytes.Length];
            Array.Copy(aes.IV, 0, result, 0, aes.IV.Length);
            Array.Copy(encryptedBytes, 0, result, aes.IV.Length, encryptedBytes.Length);

            return Convert.ToBase64String(result);
        }
        private string DecryptClientSecret(string encryptedSecret)
        {
            var key = _configuration["EncryptionKey"] ?? throw new InvalidOperationException("EncryptionKey not configured");

            var fullCipher = Convert.FromBase64String(encryptedSecret);

            using var aes = Aes.Create();
            aes.Key = Encoding.UTF8.GetBytes(key.PadRight(32).Substring(0, 32));

            // Extract the IV (first block)
            var iv = new byte[aes.BlockSize / 8];
            var cipherText = new byte[fullCipher.Length - iv.Length];
            Array.Copy(fullCipher, iv, iv.Length);
            Array.Copy(fullCipher, iv.Length, cipherText, 0, cipherText.Length);
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            var decryptedBytes = decryptor.TransformFinalBlock(cipherText, 0, cipherText.Length);

            return Encoding.UTF8.GetString(decryptedBytes);
        }
    }
}
