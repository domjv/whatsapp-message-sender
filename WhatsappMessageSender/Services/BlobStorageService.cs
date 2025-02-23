namespace WhatsappMessageSender.Services;

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.Reflection;

public class BlobStorageService(IConfiguration configuration)
{
    private readonly BlobServiceClient _blobServiceClient = new(configuration["BlobStorage:ConnectionString"]);
    private readonly string? _containerName = configuration["BlobStorage:ContainerName"];

    public async Task<string> DownloadFileAsync(string blobUrl, string fileName)
    {
        try
        {
            var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException();
            var filePath = Path.Combine(path, $"{fileName}.pdf");

            var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
            var blobName = new Uri(blobUrl).Segments[^1];
            var blobClient = containerClient.GetBlobClient(blobName);

            Console.WriteLine($"Downloading {blobName} from Blob Storage...");
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