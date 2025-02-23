namespace WhatsappMessageSender.Services;

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Models;

public class BlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;

    public BlobStorageService(IConfiguration configuration)
    {
        var appSettings = configuration.Get<AppSettings>() 
            ?? throw new InvalidOperationException("Invalid configuration");
        _blobServiceClient = new BlobServiceClient(appSettings.BlobStorage.ConnectionString);
    }

    public async Task<string> DownloadFileAsync(string blobUrl, string fileName, string containerName)
    {
        try
        {
            var tempDirPath = Path.Combine(Path.GetTempPath(), "BlobDownloads", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDirPath);

            var extension = Path.GetExtension(blobUrl);
            if (string.IsNullOrEmpty(extension))
            {
                extension = ".pdf";
            }

            var filePath = Path.Combine(tempDirPath, $"{fileName}{extension}");

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);

            var containerUri = containerClient.Uri;
            var blobPath = blobUrl
                .Replace(containerUri.ToString(), "")
                .TrimStart('/');

            var blobClient = containerClient.GetBlobClient(blobPath);

            Console.WriteLine($"Downloading {blobPath} from Blob Storage container {containerName}...");
            await using var downloadFileStream = File.OpenWrite(filePath);
            await blobClient.DownloadToAsync(downloadFileStream);

            Console.WriteLine($"File downloaded to temp location: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
            throw;
        }
    }
}