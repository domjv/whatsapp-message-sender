namespace WhatsappMessageSender.Services;

/// <summary>
/// Abstraction over Azure Blob Storage file downloads so that the
/// concrete implementation can be replaced by a mock in unit tests.
/// </summary>
public interface IBlobStorageService
{
    Task<string?> DownloadFileAsync(string blobUrl, string fileName, string containerName);
}
