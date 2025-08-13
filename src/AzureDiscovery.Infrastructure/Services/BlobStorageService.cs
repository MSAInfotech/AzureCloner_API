using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureDiscovery.Infrastructure.Configuration;
using System.Text;
using System.Text.Json;

namespace AzureDiscovery.Infrastructure.Services
{
    public interface IBlobStorageService
    {
        Task<string> UploadTemplateAsync(string containerName, string fileName, object template);
        Task<T?> DownloadTemplateAsync<T>(string containerName, string fileName);
        Task<bool> DeleteTemplateAsync(string containerName, string fileName);
        Task<List<string>> ListTemplatesAsync(string containerName);
        Task<string> UploadJsonAsync(string containerName, string fileName, object data);
    }

    public class BlobStorageService : IBlobStorageService
    {
        private readonly BlobServiceClient? _blobServiceClient;
        private readonly ILogger<BlobStorageService> _logger;

        public BlobStorageService(ILogger<BlobStorageService> logger, IOptions<AzureDiscoveryOptions> options)
        {
            var connectionString = options.Value.StorageConnectionString;
            if (!string.IsNullOrEmpty(connectionString))
            {
                _blobServiceClient = new BlobServiceClient(connectionString);
            }
            else
            {
                _logger.LogWarning("Storage connection string not configured");
            }
            _logger = logger;
        }

        public async Task<string> UploadTemplateAsync(string containerName, string fileName, object template)
        {
            if (_blobServiceClient == null)
            {
                _logger.LogWarning("Blob service client not initialized");
                return string.Empty;
            }

            try
            {
                var containerClient = await GetContainerClientAsync(containerName);
                var blobClient = containerClient.GetBlobClient(fileName);

                var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true });
                var content = Encoding.UTF8.GetBytes(json);

                using var stream = new MemoryStream(content);
                await blobClient.UploadAsync(stream, overwrite: true);

                _logger.LogInformation("Uploaded template {FileName} to container {ContainerName}", fileName, containerName);
                return blobClient.Uri.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload template {FileName} to container {ContainerName}", fileName, containerName);
                throw;
            }
        }

        public async Task<string> UploadJsonAsync(string containerName, string fileName, object data)
        {
            if (_blobServiceClient == null)
            {
                _logger.LogWarning("Blob service client not initialized");
                return string.Empty;
            }

            try
            {
                var containerClient = await GetContainerClientAsync(containerName);
                var blobClient = containerClient.GetBlobClient(fileName);

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                var content = Encoding.UTF8.GetBytes(json);

                using var stream = new MemoryStream(content);
                await blobClient.UploadAsync(stream, overwrite: true);

                _logger.LogInformation("Uploaded JSON {FileName} to container {ContainerName}", fileName, containerName);
                return blobClient.Uri.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to upload JSON {FileName} to container {ContainerName}", fileName, containerName);
                throw;
            }
        }

        public async Task<T?> DownloadTemplateAsync<T>(string containerName, string fileName)
        {
            if (_blobServiceClient == null)
            {
                _logger.LogWarning("Blob service client not initialized");
                return default;
            }

            try
            {
                var containerClient = await GetContainerClientAsync(containerName);
                var blobClient = containerClient.GetBlobClient(fileName);

                if (!await blobClient.ExistsAsync())
                    return default;

                var response = await blobClient.DownloadContentAsync();
                var json = response.Value.Content.ToString();
                
                return JsonSerializer.Deserialize<T>(json);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download template {FileName} from container {ContainerName}", fileName, containerName);
                throw;
            }
        }

        public async Task<bool> DeleteTemplateAsync(string containerName, string fileName)
        {
            if (_blobServiceClient == null)
            {
                _logger.LogWarning("Blob service client not initialized");
                return false;
            }

            try
            {
                var containerClient = await GetContainerClientAsync(containerName);
                var blobClient = containerClient.GetBlobClient(fileName);

                var response = await blobClient.DeleteIfExistsAsync();
                
                if (response.Value)
                    _logger.LogInformation("Deleted template {FileName} from container {ContainerName}", fileName, containerName);
                
                return response.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete template {FileName} from container {ContainerName}", fileName, containerName);
                throw;
            }
        }

        public async Task<List<string>> ListTemplatesAsync(string containerName)
        {
            if (_blobServiceClient == null)
            {
                _logger.LogWarning("Blob service client not initialized");
                return new List<string>();
            }

            try
            {
                var containerClient = await GetContainerClientAsync(containerName);
                var blobs = new List<string>();

                await foreach (var blobItem in containerClient.GetBlobsAsync())
                {
                    blobs.Add(blobItem.Name);
                }

                return blobs;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list templates in container {ContainerName}", containerName);
                throw;
            }
        }

        private async Task<BlobContainerClient> GetContainerClientAsync(string containerName)
        {
            var containerClient = _blobServiceClient!.GetBlobContainerClient(containerName);
            await containerClient.CreateIfNotExistsAsync(PublicAccessType.None);
            return containerClient;
        }
    }
}
