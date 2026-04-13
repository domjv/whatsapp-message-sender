using Moq;
using Newtonsoft.Json;
using StackExchange.Redis;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;
using WhatsappMessageSender.Tests.Helpers;

namespace WhatsappMessageSender.Tests;

/// <summary>
/// Unit tests for <see cref="RedisStreamProcessor"/>.
///
/// All tests use the internal test constructor which accepts a mock
/// <see cref="IDatabase"/> — no real Redis instance is required.
/// </summary>
public class RedisStreamProcessorTests
{
    // -------------------------------------------------------------------------
    // Test fixtures / shared helpers
    // -------------------------------------------------------------------------

    private const string StreamName = "stream-test";
    private const string Group = "test-group";
    private const string ContainerName = "test-container";

    private readonly Mock<IWhatsAppService> _whatsAppMock = new();
    private readonly Mock<IBlobStorageService> _blobMock = new();
    private readonly Mock<IMessageTrackingService> _trackingMock = new();
    private readonly Mock<IDatabase> _dbMock = new();

    private RedisStreamProcessor CreateProcessor(string streamName = StreamName)
    {
        var settings = new AppSettings
        {
            MessageBroker = "Redis",
            Redis = new RedisSettings
            {
                ConnectionString = "localhost:6379",
                ConsumerGroup = Group,
                ConsumerName = "test-consumer",
                MaxConcurrentCalls = 2,
                Streams =
                [
                    new StreamConfig { StreamName = streamName, ContainerName = ContainerName }
                ]
            },
            BlobStorage = new BlobStorageSettings { ConnectionString = "dummy" },
            WhatsApp = new WhatsAppSettings { ProfilePath = "/tmp", ChromeDriverPath = "/tmp" },
            MessageTracking = new MessageTrackingSettings { ApiUrl = "http://localhost", AuthToken = "token" }
        };

        return new RedisStreamProcessor(
            settings,
            _whatsAppMock.Object,
            _blobMock.Object,
            _trackingMock.Object,
            _dbMock.Object);
    }

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_ValidWhatsAppMessage_SendsAndAcknowledges()
    {
        // Arrange
        var entry = StreamEntryBuilder.ValidWhatsAppMessage(messageName: "MSG-001");
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ReturnsAsync(new SendMessageResult { Success = true });
        _dbMock
            .Setup(d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert
        _whatsAppMock.Verify(s => s.SendMessageAsync("919876543210", "Test message", null), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-001", "Processing", null), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-001", "Delivered", null), Times.Once);
        _dbMock.Verify(d => d.StreamAcknowledgeAsync(StreamName, Group, It.IsAny<RedisValue>(), CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task HandleMessage_ValidRawFieldsMessage_SendsAndAcknowledges()
    {
        // Arrange
        var entry = StreamEntryBuilder.ValidWhatsAppMessageRawFields(messageName: "MSG-RAW-001");
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ReturnsAsync(new SendMessageResult { Success = true });
        _dbMock
            .Setup(d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert
        _whatsAppMock.Verify(s => s.SendMessageAsync("919876543210", "Test message raw", null), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-RAW-001", "Delivered", null), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Attachment download
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_WithAttachment_DownloadsFileAndSends()
    {
        // Arrange
        const string attachmentUrl = "https://example.blob.core.windows.net/container/doc.pdf";
        const string localFilePath = "/tmp/doc.pdf";

        var entry = StreamEntryBuilder.ValidWhatsAppMessage(
            messageName: "MSG-ATTACH-001",
            attachmentUrl: attachmentUrl,
            streamName: StreamName);

        _blobMock
            .Setup(b => b.DownloadFileAsync(attachmentUrl, "MSG-ATTACH-001", ContainerName))
            .ReturnsAsync(localFilePath);
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), localFilePath))
            .ReturnsAsync(new SendMessageResult { Success = true });
        _dbMock
            .Setup(d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert
        _blobMock.Verify(b => b.DownloadFileAsync(attachmentUrl, "MSG-ATTACH-001", ContainerName), Times.Once);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), localFilePath), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-ATTACH-001", "Delivered", null), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Missing MessageName → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_MissingMessageName_DeadLetters()
    {
        // Arrange
        var entry = StreamEntryBuilder.MissingMessageNameEntry(streamName: StreamName);
        SetupDeadLetterDb();

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert — message goes to the dead-letter stream, WhatsApp is never called
        _dbMock.Verify(d => d.StreamAddAsync(
            StreamName + ":dead",
            It.IsAny<NameValueEntry[]>(),
            It.IsAny<RedisValue?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Unsupported message type → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_UnsupportedType_DeadLetters()
    {
        // Arrange
        var entry = StreamEntryBuilder.UnsupportedTypeEntry(messageName: "MSG-SMS-001");
        SetupDeadLetterDb();
        _trackingMock.Setup(t => t.TrackMessageStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert
        _dbMock.Verify(d => d.StreamAddAsync(
            StreamName + ":dead",
            It.IsAny<NameValueEntry[]>(),
            It.IsAny<RedisValue?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Invalid JSON data field → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_InvalidJsonAndNoFallbackFields_DeadLetters()
    {
        // Arrange — bad JSON in data field, no phone/message fallback
        var entry = StreamEntryBuilder.InvalidJsonDataEntry(messageName: "MSG-BAD-001");
        SetupDeadLetterDb();
        _trackingMock.Setup(t => t.TrackMessageStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns(Task.CompletedTask);

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert — deserialization fails → dead letter
        _dbMock.Verify(d => d.StreamAddAsync(
            StreamName + ":dead",
            It.IsAny<NameValueEntry[]>(),
            It.IsAny<RedisValue?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Max retries exceeded → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_MaxRetriesExceeded_DeadLettersAndTracksFailed()
    {
        // Arrange
        var entry = StreamEntryBuilder.ExhaustedRetriesEntry(messageName: "MSG-EX-001");
        SetupDeadLetterDb();

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert
        _dbMock.Verify(d => d.StreamAddAsync(
            StreamName + ":dead",
            It.IsAny<NameValueEntry[]>(),
            It.IsAny<RedisValue?>(),
            It.IsAny<int?>(),
            It.IsAny<bool>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-EX-001", "Failed", It.IsAny<string>()), Times.Once);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Send failure → schedules retry
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_SendFails_SchedulesRetry()
    {
        // Arrange
        var entry = StreamEntryBuilder.ValidWhatsAppMessage(messageName: "MSG-FAIL-001");
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ReturnsAsync(new SendMessageResult { Success = false, Error = "Browser timed out" });
        SetupRetryDb();

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert — added to retry sorted set
        _dbMock.Verify(d => d.SortedSetAddAsync(
            StreamName + ":retries",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _dbMock.Verify(d => d.StreamAcknowledgeAsync(
            StreamName, Group, It.IsAny<RedisValue>(), CommandFlags.None), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-FAIL-001", "Retry Scheduled", It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task HandleMessage_SendThrowsException_SchedulesRetry()
    {
        // Arrange
        var entry = StreamEntryBuilder.ValidWhatsAppMessage(messageName: "MSG-EX2-001");
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ThrowsAsync(new InvalidOperationException("Chrome crashed"));
        SetupRetryDb();

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert
        _dbMock.Verify(d => d.SortedSetAddAsync(
            StreamName + ":retries",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-EX2-001", "Retry Scheduled", It.IsAny<string>()), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Blob download failure → schedules retry (exception bubbles up)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task HandleMessage_BlobDownloadThrows_SchedulesRetry()
    {
        // Arrange
        const string attachmentUrl = "https://example.blob.core.windows.net/container/doc.pdf";
        var entry = StreamEntryBuilder.ValidWhatsAppMessage(
            messageName: "MSG-BLOB-001",
            attachmentUrl: attachmentUrl,
            streamName: StreamName);

        _blobMock
            .Setup(b => b.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("Storage unavailable"));
        SetupRetryDb();

        var processor = CreateProcessor();

        // Act
        await processor.HandleMessageAsync(StreamName, Group, entry);

        // Assert — no send attempted, retry scheduled
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        _dbMock.Verify(d => d.SortedSetAddAsync(
            StreamName + ":retries",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync("MSG-BLOB-001", "Retry Scheduled", It.IsAny<string>()), Times.Once);
    }

    // -------------------------------------------------------------------------
    // ExtractMessageData unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ExtractMessageData_JsonDataField_ParsedCorrectly()
    {
        var msg = new WhatsAppMessage
        {
            Name = "MSG-001", Phone = "911234567890",
            Message = "Hello", MessageName = "MSG-001"
        };
        var entry = new StreamEntry("123-0", new NameValueEntry[]
        {
            new("data",         JsonConvert.SerializeObject(msg)),
            new("message_type", "whatsapp"),
            new("message_name", "MSG-001"),
            new("stream_name",  "stream-test")
        });

        var (messageType, messageName, channelName, parsed, retryCount) =
            RedisStreamProcessor.ExtractMessageData(entry, "stream-test");

        Assert.Equal("whatsapp", messageType);
        Assert.Equal("MSG-001", messageName);
        Assert.Equal("stream-test", channelName);
        Assert.NotNull(parsed);
        Assert.Equal("911234567890", parsed!.Phone);
        Assert.Equal(0, retryCount);
    }

    [Fact]
    public void ExtractMessageData_IndividualFields_ParsedCorrectly()
    {
        var entry = new StreamEntry("124-0", new NameValueEntry[]
        {
            new("message_type", "whatsapp"),
            new("message_name", "MSG-002"),
            new("phone",        "911111111111"),
            new("message",      "Hi there"),
            new("name",         "MSG-002"),
            new("stream_name",  "stream-abc")
        });

        var (messageType, messageName, channelName, parsed, retryCount) =
            RedisStreamProcessor.ExtractMessageData(entry, "stream-default");

        Assert.Equal("whatsapp", messageType);
        Assert.Equal("MSG-002", messageName);
        Assert.Equal("stream-abc", channelName);
        Assert.NotNull(parsed);
        Assert.Equal("911111111111", parsed!.Phone);
        Assert.Equal(0, retryCount);
    }

    [Fact]
    public void ExtractMessageData_WithRetryCount_ReturnsParsedRetryCount()
    {
        var entry = StreamEntryBuilder.ValidWhatsAppMessage(retryCount: 3);
        var (_, _, _, _, retryCount) = RedisStreamProcessor.ExtractMessageData(entry, StreamName);
        Assert.Equal(3, retryCount);
    }

    [Fact]
    public void ExtractMessageData_NoStreamNameField_UsesDefault()
    {
        var entry = new StreamEntry("125-0", new NameValueEntry[]
        {
            new("message_type", "whatsapp"),
            new("message_name", "MSG-003"),
            new("phone",        "910000000000"),
            new("message",      "No stream_name field")
        });

        var (_, _, channelName, _, _) = RedisStreamProcessor.ExtractMessageData(entry, "my-default-stream");

        Assert.Equal("my-default-stream", channelName);
    }

    [Fact]
    public void ExtractMessageData_MissingPhoneAndMessage_ReturnsNullMessage()
    {
        var entry = new StreamEntry("126-0", new NameValueEntry[]
        {
            new("message_type", "whatsapp"),
            new("message_name", "MSG-NOPHONE")
        });

        var (_, _, _, parsed, _) = RedisStreamProcessor.ExtractMessageData(entry, StreamName);
        Assert.Null(parsed);
    }

    [Fact]
    public void TryParseClaimedStreamEntry_ValidMessage_ReturnsStreamEntry()
    {
        var raw = (RedisResult)new RedisResult[]
        {
            (RedisResult)"1670000000000-1",
            (RedisResult)new RedisResult[]
            {
                (RedisResult)"message_name",
                (RedisResult)"MSG-CLAIM-001",
                (RedisResult)"message_type",
                (RedisResult)"whatsapp"
            }
        };

        var success = RedisStreamProcessor.TryParseClaimedStreamEntry(raw, out var parsed);

        Assert.True(success);
        Assert.NotNull(parsed);
        Assert.Equal("1670000000000-1", parsed!.Value.Id.ToString());
        Assert.Equal("MSG-CLAIM-001", parsed.Value["message_name"].ToString());
    }

    [Fact]
    public void TryParseClaimedStreamEntry_MalformedMessage_ReturnsFalse()
    {
        var raw = (RedisResult)new RedisResult[]
        {
            (RedisResult)"1670000000000-2"
        };

        var success = RedisStreamProcessor.TryParseClaimedStreamEntry(raw, out var parsed);

        Assert.False(success);
        Assert.Null(parsed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void SetupDeadLetterDb()
    {
        _dbMock
            .Setup(d => d.StreamAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<NameValueEntry[]>(),
                It.IsAny<RedisValue?>(),
                It.IsAny<int?>(),
                It.IsAny<bool>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisValue.EmptyString);
        _dbMock
            .Setup(d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);
    }

    private void SetupRetryDb()
    {
        _dbMock
            .Setup(d => d.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);
        _dbMock
            .Setup(d => d.StreamAcknowledgeAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(1L);
    }
}
