# Architecture: WhatsApp Message Sender

## Overview

The **WhatsApp Message Sender** is a .NET 9 background worker that consumes
notification messages from a configurable message broker (Redis Streams **or**
Azure Service Bus), sends them via WhatsApp Web (Selenium), tracks delivery
status, and optionally downloads file attachments from Azure Blob Storage.

The active broker is selected by a single `MessageBroker` setting in
`appsettings.json`; no code changes are needed to switch between brokers.

---

## System Context

```
┌────────────────────────┐   XADD / Send   ┌────────────────────────┐
│  Frappe / ERPNext      │────────────────▶│  Redis Streams  (OR)   │
│  (Python producer      │                 │  Azure Service Bus      │
│   + RQ Workers)        │                 └───────────┬────────────┘
└────────────────────────┘                             │ XREADGROUP / RegisterMessageHandler
                                           ┌───────────▼───────────────────────────┐
                                           │          IMessageProcessor             │
                                           │  RedisStreamProcessor (Redis)  OR      │
                                           │  QueueProcessor       (ServiceBus)     │
                                           └──┬──────────────────┬─────────────────┘
                                              │                  │
                                    ┌─────────▼──┐    ┌──────────▼───────────────┐
                                    │  WhatsApp  │    │  Azure Blob Storage      │
                                    │  Web       │    │  (optional attachments)  │
                                    │ (Selenium) │    └──────────────────────────┘
                                    └─────────┬──┘
                                              │
                                    ┌─────────▼──────────────┐
                                    │  Frappe Message        │
                                    │  Tracking API          │
                                    └────────────────────────┘
```

---

## Broker Selection

Set `MessageBroker` in `appsettings.json` (or an environment variable) to
choose the active transport layer. Only the matching configuration block needs
to be populated.

```json
{
  "MessageBroker": "Redis",   // ← or "ServiceBus"
  "Redis": { ... },
  "ServiceBus": { ... }
}
```

| Value          | Processor class        | Transport          |
|----------------|------------------------|--------------------|
| `"Redis"`      | `RedisStreamProcessor` | Redis Streams      |
| `"ServiceBus"` | `QueueProcessor`       | Azure Service Bus  |

---

## Redis Streams delivery model (RQ + Redis Streams hybrid)

### Why this combination?

| Concern                        | Component                                |
|--------------------------------|------------------------------------------|
| Ordered, durable message store | Redis Streams (`XADD` / `XREADGROUP`)    |
| At-least-once delivery         | Consumer groups + Pending-Entry-List     |
| Delayed / scheduled retries    | Redis Sorted Set (RQ-style scheduler)    |
| Dead-lettering                 | Dedicated dead-letter stream             |

### Message lifecycle

```
Producer
  │
  │ XADD stream-<tenant> *  message_type whatsapp  message_name <name>  …
  ▼
Redis Stream  ──── XREADGROUP ──▶  RedisStreamProcessor
                                        │
                                   ┌────┴──────────┐
                              Success           Failure / Exception
                                │                    │
                             XACK             XACK + ZADD stream:retries
                                              (score = retry_after_unix_ts)
                                                     │
                                          (10 s ticker checks ZRANGEBYSCORE)
                                                     │
                                              XADD stream back (retry_count++)
                                                     │
                                         retry_count > MaxRetries?
                                              │             │
                                             No            Yes
                                              │             │
                                         (loop)    XADD stream:dead + XACK
```

### Redis keys used per stream

| Key                         | Type          | Purpose                                      |
|-----------------------------|---------------|----------------------------------------------|
| `stream-<tenant>`           | Stream        | Primary message pipeline                     |
| `stream-<tenant>:retries`   | Sorted Set    | Scheduled retries (score = Unix retry time)  |
| `stream-<tenant>:dead`      | Stream        | Dead-lettered / exhausted messages           |

---

## Azure Service Bus delivery model

The `QueueProcessor` uses Service Bus native features:

- **Lock-based at-least-once delivery** — messages remain locked until
  `CompleteAsync` (success) or `AbandonAsync` (retry).
- **Native retry** — `AbandonAsync` returns the message to the queue; the
  Service Bus visibility timeout governs the retry interval.
- **Dead-letter** — after `MaxDeliveryCount` (configurable on the queue) or
  when the app explicitly calls `DeadLetterAsync`, the message moves to the
  associated dead-letter queue.

---

## Key abstractions

| Interface                | Implementations                                     | Purpose                                  |
|--------------------------|-----------------------------------------------------|------------------------------------------|
| `IMessageProcessor`      | `RedisStreamProcessor`, `QueueProcessor`            | Broker-agnostic consumer lifecycle       |
| `IWhatsAppService`       | `WhatsAppService` (Selenium)                        | Mockable WhatsApp sender                 |
| `IBlobStorageService`    | `BlobStorageService` (Azure SDK)                    | Mockable file downloader                 |
| `IMessageTrackingService`| `MessageTrackingService` (HTTP/Frappe API)          | Mockable status tracker                  |

---

## Configuration reference

### Common settings

```json
{
  "MessageBroker": "Redis",
  "BlobStorage":   { "ConnectionString": "…" },
  "WhatsApp":      { "ProfilePath": "…", "ChromeDriverPath": "…" },
  "MessageTracking": { "ApiUrl": "…", "AuthToken": "…" }
}
```

### Redis-specific settings

```json
"Redis": {
  "ConnectionString":           "localhost:6379",
  "ConsumerGroup":              "whatsapp-sender",
  "ConsumerName":               "whatsapp-sender-1",
  "MaxConcurrentCalls":         2,
  "PendingMessageTimeoutSeconds": 300,
  "Streams": [
    { "StreamName": "stream-pleasntbiz", "ContainerName": "pleasantbiz-attachments" }
  ]
}
```

### Service Bus–specific settings

```json
"ServiceBus": {
  "ConnectionString": "Endpoint=sb://…",
  "MaxConcurrentCalls": 2,
  "Queues": [
    { "QueueName": "sbq-pleasntbiz", "ContainerName": "pleasantbiz-attachments" }
  ]
}
```

---

## Message format (Redis Streams)

The processor supports two wire formats:

### 1. JSON `data` field (preferred — produced by Frappe/Python)

```python
redis.xadd("stream-pleasntbiz", {
    "data": json.dumps({
        "name":           "MSG-00001",
        "phone":          "919876543210",
        "message":        "Hello, your booking is confirmed.",
        "attachment_url": "https://…/receipt.pdf",
        "message_name":   "MSG-00001"
    }),
    "message_type":  "whatsapp",
    "message_name":  "MSG-00001",
    "stream_name":   "stream-pleasntbiz"
})
```

### 2. Individual fields (fallback)

```python
redis.xadd("stream-pleasntbiz", {
    "message_type": "whatsapp",
    "message_name": "MSG-00001",
    "phone":        "919876543210",
    "message":      "Hello, your booking is confirmed.",
    "name":         "MSG-00001"
})
```

**Note:** `message_name` is required. Messages without it are immediately dead-lettered.

---

## Retry policy

| Setting                         | Default | Description                              |
|---------------------------------|---------|------------------------------------------|
| `RetrySettings.MaxRetries`      | 10      | Maximum delivery attempts                |
| `RetrySettings.BaseDelaySeconds`| 30      | Base for exponential back-off            |
| Max delay cap                   | 3600 s  | Retry interval never exceeds 1 hour      |

Retry delay formula: `min(30 × 2^(retryCount-1), 3600)` seconds.

---

## Testing without a live broker

### Redis Streams — `RedisStreamTestPublisher`

Use the `WhatsappMessageSender.Tools.RedisStreamTestPublisher` class to publish
test messages directly to a running Redis instance before the Frappe producer is ready.

```csharp
// Publish a single message
await RedisStreamTestPublisher.PublishAsync(
    connectionString: "localhost:6379",
    streamName: "stream-pleasntbiz",
    message: new WhatsAppMessage
    {
        Name = "MSG-TEST-001", Phone = "919876543210",
        Message = "Test message.", MessageName = "MSG-TEST-001"
    });

// Publish all scenario types at once
await RedisStreamTestPublisher.PublishScenarioSuiteAsync(
    "localhost:6379", "stream-pleasntbiz");
```

### Unit tests

Run without any external dependencies:

```bash
dotnet test WhatsappMessageSender.Tests/WhatsappMessageSender.Tests.csproj
```

The test suite covers 38 scenarios across both processors using Moq — no real
Redis instance or Service Bus connection is required.

---

## Further improvements

See [agent-guide.md](agent-guide.md) for a prioritised list of recommended
enhancements.
