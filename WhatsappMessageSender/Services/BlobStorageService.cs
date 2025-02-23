namespace WhatsappMessageSender.Services;

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using WhatsappMessageSender.Models;

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
            var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException();
            var filePath = Path.Combine(path, $"{fileName}.pdf");

            var containerClient = _blobServiceClient.GetBlobContainerClient(containerName);
            var blobName = new Uri(blobUrl).Segments[^1];
            var blobClient = containerClient.GetBlobClient(blobName);

            Console.WriteLine($"Downloading {blobName} from Blob Storage container {containerName}...");
            await using var downloadFileStream = File.OpenWrite(filePath);
            await blobClient.DownloadToAsync(downloadFileStream);

            Console.WriteLine($"File downloaded: {filePath}");
            return filePath;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error downloading file: {ex.Message}");
            throw;
        }
    }
}