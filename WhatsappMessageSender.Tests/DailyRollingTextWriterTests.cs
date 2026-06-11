using WhatsappMessageSender.Logging;

namespace WhatsappMessageSender.Tests;

public class DailyRollingTextWriterTests
{
    [Fact]
    public void WriteLine_CreatesDailyLogFileWithTimestamp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "whatsapp-sender-tests", Guid.NewGuid().ToString("N"));
        var dateKey = DateTime.Now.ToString("yyyy-MM-dd");
        var expectedPath = Path.Combine(tempDir, $"whatsapp-sender-{dateKey}.log");

        using (var writer = new DailyRollingTextWriter(tempDir))
        {
            writer.WriteLine("test message");
            writer.Flush();
        }

        Assert.True(File.Exists(expectedPath));
        var content = File.ReadAllText(expectedPath);
        Assert.Contains("test message", content);

        Directory.Delete(tempDir, recursive: true);
    }
}
