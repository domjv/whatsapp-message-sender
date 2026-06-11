namespace WhatsappMessageSender.Services;

using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Models;

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient? _blobServiceClient;
    private bool _loggedBlobDisabled;

    public BlobStorageService(IConfiguration configuration)
    {
        var appSettings = configuration.Get<AppSettings>()
            ?? throw new InvalidOperationException("Invalid configuration");

        var connectionString = appSettings.BlobStorage?.ConnectionString?.Trim();
        if (string.IsNullOrEmpty(connectionString))
        {
            Console.WriteLine(
                "Blob storage is not configured (missing or empty BlobStorage:ConnectionString). Attachment downloads are disabled.");
            return;
        }

        if (LooksLikeRedactedOrPlaceholderConnectionString(connectionString))
        {
            Console.WriteLine(
                "Blob storage is disabled: connection string still contains a redacted placeholder (e.g. AccountKey=******). " +
                "Set a real connection string or remove the BlobStorage section.");
            return;
        }

        try
        {
            _blobServiceClient = new BlobServiceClient(connectionString);
            Console.WriteLine("Blob storage is enabled.");
        }
        catch (FormatException ex)
        {
            Console.WriteLine(
                "Blob storage is disabled: connection string is not valid for Azure Storage. " +
                "Attachment downloads will be skipped. " + ex.Message);
        }
    }

    private static bool LooksLikeRedactedOrPlaceholderConnectionString(string connectionString) =>
        connectionString.Contains("******", StringComparison.Ordinal)
        && connectionString.Contains("AccountKey", StringComparison.OrdinalIgnoreCase);

    public async Task<string?> DownloadFileAsync(string blobUrl, string fileName, string containerName)
    {
        if (_blobServiceClient == null)
        {
            if (!_loggedBlobDisabled)
            {
                _loggedBlobDisabled = true;
                Console.WriteLine(
                    "Blob storage unavailable — skipping attachment download (further skips will be silent).");
            }

            return null;
        }

        try
        {
            var extension = Path.GetExtension(blobUrl);
            if (string.IsNullOrEmpty(extension))
            {
                return null;
            }

            var tempDirPath = Path.Combine(Path.GetTempPath(), "BlobDownloads", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDirPath);

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
