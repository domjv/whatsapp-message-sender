using Microsoft.Extensions.Configuration;
using Moq;
using Newtonsoft.Json;
using System.Text;
using WhatsappMessageSender.Models;
using WhatsappMessageSender.Services;

namespace WhatsappMessageSender.Tests;

/// <summary>
/// Unit tests for <see cref="QueueProcessor"/>.
///
/// Tests call the internal <c>ProcessMessageCoreAsync</c> method directly,
/// which accepts primitive data extracted from the ServiceBus Message — no
/// real Azure Service Bus connection is required.
/// </summary>
public class QueueProcessorTests
{
    // -------------------------------------------------------------------------
    // Test fixtures
    // -------------------------------------------------------------------------

    private const string QueueName = "sbq-test";
    private const string SubscriptionName = "whatsapp-message-sender-tests";
    private const string ContainerName = "test-container";

    private readonly Mock<IWhatsAppService> _whatsAppMock = new();
    private readonly Mock<IBlobStorageService> _blobMock = new();
    private readonly Mock<IMessageTrackingService> _trackingMock = new();

    // Capture calls to the Func delegates
    private bool _completed;
    private string? _deadLetterReason;
    private IDictionary<string, object>? _abandonedProps;

    private QueueProcessor CreateProcessor()
    {
        var config = BuildConfiguration();
        return new QueueProcessor(
            config,
            _whatsAppMock.Object,
            _blobMock.Object,
            _trackingMock.Object,
            NullWhatsAppSendRateLimiter.Instance);
    }

    private IConfiguration BuildConfiguration()
    {
        var dict = new Dictionary<string, string?>
        {
            ["MessageBroker"]                              = "ServiceBus",
            ["ServiceBus:ConnectionString"]                = "Endpoint=sb://test.servicebus.windows.net/;SharedAccessKeyName=test;SharedAccessKey=dGVzdA==",
            ["ServiceBus:MaxConcurrentCalls"]              = "2",
            ["ServiceBus:Topics:0:TopicName"]              = QueueName,
            ["ServiceBus:Topics:0:SubscriptionName"]       = SubscriptionName,
            ["ServiceBus:Topics:0:ContainerName"]          = ContainerName,
            ["ServiceBus:Topics:0:Priority"]               = "100",
            ["BlobStorage:ConnectionString"]               = "UseDevelopmentStorage=true",
            ["WhatsApp:ProfilePath"]                       = "/tmp",
            ["WhatsApp:ChromeDriverPath"]                  = "/tmp",
            ["MessageTracking:ApiUrl"]                     = "http://localhost",
            ["MessageTracking:NotificationSecret"]         = "secret"
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(dict)
            .Build();
    }

    private Func<Task> CompleteFunc() => () =>
    {
        _completed = true;
        return Task.CompletedTask;
    };

    private Func<string, string, Task> DeadLetterFunc() => (reason, _) =>
    {
        _deadLetterReason = reason;
        return Task.CompletedTask;
    };

    private Func<IDictionary<string, object>, Task> AbandonFunc() => props =>
    {
        _abandonedProps = props;
        return Task.CompletedTask;
    };

    private static string Serialize(WhatsAppMessage msg) => JsonConvert.SerializeObject(msg);

    // -------------------------------------------------------------------------
    // Happy path
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_ValidMessage_SendsAndCompletes()
    {
        // Arrange
        var msg = new WhatsAppMessage { Name = "MSG-001", Phone = "919876543210", Message = "Hello", MessageName = "MSG-001" };
        _whatsAppMock
            .Setup(s => s.SendMessageAsync("919876543210", "Hello", null))
            .ReturnsAsync(new SendMessageResult { Success = true });

        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-1",
            messageBody: Serialize(msg),
            deliveryCount: 1,
            queueName: QueueName,
            messageType: "whatsapp",
            messageName: "MSG-001",
            completeAsync: CompleteFunc(),
            deadLetterAsync: DeadLetterFunc(),
            abandonAsync: AbandonFunc());

        // Assert
        Assert.True(_completed);
        Assert.Null(_deadLetterReason);
        Assert.Null(_abandonedProps);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync(
            QueueName, "MSG-001", "Sent", null, null, It.IsAny<DateTime?>()), Times.Once);
    }

    // -------------------------------------------------------------------------
    // With attachment
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_WithAttachment_DownloadsFileAndSends()
    {
        // Arrange
        const string blobUrl = "https://example.blob.core.windows.net/container/receipt.pdf";
        const string localPath = "/tmp/receipt.pdf";

        var msg = new WhatsAppMessage
        {
            Name = "MSG-ATTACH-001", Phone = "919000000001",
            Message = "See attached", MessageName = "MSG-ATTACH-001",
            AttachmentUrl = blobUrl
        };
        _blobMock
            .Setup(b => b.DownloadFileAsync(blobUrl, "MSG-ATTACH-001", ContainerName))
            .ReturnsAsync(localPath);
        _whatsAppMock
            .Setup(s => s.SendMessageAsync("919000000001", "See attached", localPath))
            .ReturnsAsync(new SendMessageResult { Success = true });

        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-2", messageBody: Serialize(msg), deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-ATTACH-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.True(_completed);
        _blobMock.Verify(b => b.DownloadFileAsync(blobUrl, "MSG-ATTACH-001", ContainerName), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Missing MessageName → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_MissingMessageName_DeadLetters()
    {
        // Arrange
        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-3", messageBody: "{}", deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: null,
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.Equal("Message Name Not Found", _deadLetterReason);
        Assert.False(_completed);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCore_EmptyMessageName_DeadLetters()
    {
        // Arrange
        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-x", messageBody: "{}", deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.Equal("Message Name Not Found", _deadLetterReason);
    }

    // -------------------------------------------------------------------------
    // Max retries exceeded → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_DeliveryCountExceedsMaxRetries_DeadLettersAndTracksFailed()
    {
        // Arrange
        var processor = CreateProcessor();
        var msg = new WhatsAppMessage { Name = "MSG-MAX-001", Phone = "919000000002", Message = "X", MessageName = "MSG-MAX-001" };

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-4", messageBody: Serialize(msg),
            deliveryCount: RetrySettings.MaxRetries + 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-MAX-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.Equal("MaxRetriesExceeded", _deadLetterReason);
        Assert.False(_completed);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync(
            QueueName, "MSG-MAX-001", "Failed", It.IsAny<string>(), null, null), Times.Once);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Invalid message body (deserialization failure) → dead-letter
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_InvalidMessageBody_DeadLetters()
    {
        // Arrange
        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-5", messageBody: "not-valid-json", deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-BAD-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.Equal("InvalidFormat", _deadLetterReason);
        Assert.False(_completed);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task ProcessCore_NullDeserialization_DeadLetters()
    {
        // Arrange — JSON "null" deserializes to null object
        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-6", messageBody: "null", deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-NULL-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.Equal("InvalidFormat", _deadLetterReason);
    }

    // -------------------------------------------------------------------------
    // Send failure → abandon (Service Bus native retry)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_SendFails_AbandonsForRetry()
    {
        // Arrange
        var msg = new WhatsAppMessage { Name = "MSG-FAIL-001", Phone = "919000000003", Message = "Retry me", MessageName = "MSG-FAIL-001" };
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ReturnsAsync(new SendMessageResult { Success = false, Error = "Browser timed out" });

        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-7", messageBody: Serialize(msg), deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-FAIL-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.False(_completed);
        Assert.Null(_deadLetterReason);
        Assert.NotNull(_abandonedProps);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync(
            QueueName, "MSG-FAIL-001", "Pending", It.IsAny<string>(), null, null), Times.Once);
    }

    [Fact]
    public async Task ProcessCore_UnsupportedMessageType_DeadLetters()
    {
        // Arrange
        var msg = new WhatsAppMessage
        {
            Name = "MSG-EMAIL-001",
            Phone = "919000000009",
            Message = "Should not send",
            MessageName = "MSG-EMAIL-001"
        };
        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-8",
            messageBody: Serialize(msg),
            deliveryCount: 1,
            queueName: QueueName,
            messageType: "email",
            messageName: "MSG-EMAIL-001",
            completeAsync: CompleteFunc(),
            deadLetterAsync: DeadLetterFunc(),
            abandonAsync: AbandonFunc());

        // Assert
        Assert.Equal("UnsupportedMessageType", _deadLetterReason);
        Assert.False(_completed);
        Assert.Null(_abandonedProps);
        _whatsAppMock.Verify(
            s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    // -------------------------------------------------------------------------
    // Send throws exception → abandon
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_SendThrowsException_AbandonsForRetry()
    {
        // Arrange
        var msg = new WhatsAppMessage { Name = "MSG-EX-001", Phone = "919000000004", Message = "Crash test", MessageName = "MSG-EX-001" };
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ThrowsAsync(new InvalidOperationException("Chrome crashed"));

        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-8", messageBody: Serialize(msg), deliveryCount: 2,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-EX-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.False(_completed);
        Assert.NotNull(_abandonedProps);
        _trackingMock.Verify(t => t.TrackMessageStatusAsync(
            QueueName, "MSG-EX-001", "Pending", It.Is<string>(s => s!.Contains("Chrome crashed")), null, null), Times.Once);
    }

    // -------------------------------------------------------------------------
    // Blob download throws → abandon (exception propagates up)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_BlobDownloadThrows_AbandonsForRetry()
    {
        // Arrange
        const string blobUrl = "https://example.blob.core.windows.net/container/file.pdf";
        var msg = new WhatsAppMessage
        {
            Name = "MSG-BLOBEX-001", Phone = "919000000005",
            Message = "With file", MessageName = "MSG-BLOBEX-001",
            AttachmentUrl = blobUrl
        };
        _blobMock
            .Setup(b => b.DownloadFileAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new IOException("Storage unavailable"));

        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-9", messageBody: Serialize(msg), deliveryCount: 1,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-BLOBEX-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.False(_completed);
        Assert.NotNull(_abandonedProps);
        _whatsAppMock.Verify(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // -------------------------------------------------------------------------
    // Retry count is included in the abandon properties
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ProcessCore_AbandonIncludesRetryCount()
    {
        // Arrange
        var msg = new WhatsAppMessage { Name = "MSG-RC-001", Phone = "919000000006", Message = "rc test", MessageName = "MSG-RC-001" };
        _whatsAppMock
            .Setup(s => s.SendMessageAsync(It.IsAny<string>(), It.IsAny<string>(), null))
            .ReturnsAsync(new SendMessageResult { Success = false, Error = "err" });

        var processor = CreateProcessor();

        // Act
        await processor.ProcessMessageCoreAsync(
            messageId: "msg-id-10", messageBody: Serialize(msg), deliveryCount: 3,
            queueName: QueueName, messageType: "whatsapp", messageName: "MSG-RC-001",
            completeAsync: CompleteFunc(), deadLetterAsync: DeadLetterFunc(), abandonAsync: AbandonFunc());

        // Assert
        Assert.NotNull(_abandonedProps);
        Assert.True(_abandonedProps!.ContainsKey("RetryCount"));
        Assert.Equal(3, _abandonedProps["RetryCount"]);
    }
}
