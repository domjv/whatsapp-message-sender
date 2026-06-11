using WhatsappMessageSender.Services;

namespace WhatsappMessageSender.Tests;

public class AttachmentFileCleanupTests
{
    [Fact]
    public void DeleteDownloadedAttachment_RemovesFileAndEmptyBlobDownloadDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BlobDownloads", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "attachment.pdf");
        File.WriteAllText(filePath, "test");

        AttachmentFileCleanup.DeleteDownloadedAttachment(filePath);

        Assert.False(File.Exists(filePath));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void DeleteDownloadedAttachment_DoesNotDeleteNonBlobDownloadsParentDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "whatsapp-cleanup-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(directory, "attachment.pdf");
        File.WriteAllText(filePath, "test");

        try
        {
            AttachmentFileCleanup.DeleteDownloadedAttachment(filePath);

            Assert.False(File.Exists(filePath));
            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
