namespace WhatsappMessageSender.Services;

internal static class AttachmentFileCleanup
{
    private static readonly string BlobDownloadsRoot = Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "BlobDownloads"));

    public static void DeleteDownloadedAttachment(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        try
        {
            var fullFilePath = Path.GetFullPath(filePath);
            if (File.Exists(fullFilePath))
            {
                File.Delete(fullFilePath);
            }

            DeleteParentDirectoryIfSafeAndEmpty(fullFilePath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: failed to delete temporary attachment '{filePath}': {ex.Message}");
        }
    }

    private static void DeleteParentDirectoryIfSafeAndEmpty(string fullFilePath)
    {
        var parent = Path.GetDirectoryName(fullFilePath);
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
        {
            return;
        }

        var fullParent = Path.GetFullPath(parent);
        if (!IsUnderBlobDownloadsRoot(fullParent))
        {
            return;
        }

        if (!Directory.EnumerateFileSystemEntries(fullParent).Any())
        {
            Directory.Delete(fullParent);
        }
    }

    private static bool IsUnderBlobDownloadsRoot(string fullPath)
    {
        var relativePath = Path.GetRelativePath(BlobDownloadsRoot, fullPath);
        return !relativePath.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, ".", StringComparison.Ordinal);
    }
}
