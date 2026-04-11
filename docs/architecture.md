# Architecture: WhatsApp Message Sender

## Overview

The **WhatsApp Message Sender** is a .NET 9 background worker that consumes
notification messages from Redis Streams, sends them via WhatsApp Web (Selenium),
tracks their delivery status, and optionally downloads file attachments from
Azure Blob Storage.

---

## System Context

```
┌───────────────────────┐          ┌──────────────────────┐
│  Frappe / ERPNext     │  XADD   │      Redis            │
│  (Python producer)    │────────▶│  Streams + Sorted Set │
│  + RQ Workers         │         │                       │
└───────────────────────┘         └──────────┬───────────┘
                                             │ XREADGROUP
                                   ┌─────────▼──────────────┐
                                   │  RedisStreamProcessor   │
                                   │  (this application)     │
                                   └──┬───────────┬──────────┘
                                      │           │
                              ┌───────▼──┐  ┌─────▼────────────┐
                              │  WhatsApp│  │  Azure Blob       │
                              │  Web     │  │  Storage          │
                              │ (Selenium│  │  (attachments)    │
                              └───────┬──┘  └─────────────────┘
                                      │
                              ┌───────▼──────────────┐
                              │  Frappe Message       │
                              │  Tracking API         │
                              └──────────────────────┘
```

---

## Delivery Model: RQ + Redis Streams Hybrid

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

## Components

### `RedisStreamProcessor`

Core service. Spawns two background tasks per configured stream:

1. **ProcessStreamLoopAsync** – polls `XREADGROUP GROUP … STREAMS … >` for new
   messages and dispatches them concurrently (up to `MaxConcurrentCalls`).
2. **RetrySchedulerLoopAsync** – every 10 s checks the `:retries` sorted set for
   due messages and promotes them back to the stream.

### `WhatsAppService`

Uses Selenium / ChromeDriver to open WhatsApp Web and send messages (with optional
file attachments). Stateful: the ChromeDriver instance is kept alive for the
process lifetime.

### `BlobStorageService`

Downloads file attachments from Azure Blob Storage to a temporary local directory
before passing the file path to `WhatsAppService`.

### `MessageTrackingService`

POSTs status updates (`Processing`, `Delivered`, `Retry Scheduled`, `Failed`) to
the Frappe Message Tracking API.

---

## Configuration (`appsettings.json`)

```json
{
  "Redis": {
    "ConnectionString": "localhost:6379",
    "ConsumerGroup": "whatsapp-sender",
    "ConsumerName": "whatsapp-sender-1",
    "MaxConcurrentCalls": 2,
    "PendingMessageTimeoutSeconds": 300,
    "Streams": [
      { "StreamName": "stream-pleasntbiz", "ContainerName": "pleasantbiz-attachments" },
      { "StreamName": "stream-ivyliving",  "ContainerName": "ivyliving-attachments"  }
    ]
  },
  "BlobStorage": { "ConnectionString": "…" },
  "WhatsApp":    { "ProfilePath": "…", "ChromeDriverPath": "…" },
  "MessageTracking": { "ApiUrl": "…", "AuthToken": "…" }
}
```

| Setting                      | Description                                                    |
|------------------------------|----------------------------------------------------------------|
| `ConnectionString`           | Redis connection string (e.g. `host:port,password=…`)         |
| `ConsumerGroup`              | Consumer group name; shared by all instances of this app       |
| `ConsumerName`               | Unique name for this instance (defaults to `MachineName`)      |
| `MaxConcurrentCalls`         | Max parallel message handlers per stream                       |
| `PendingMessageTimeoutSeconds` | Unused after hybrid redesign (kept for future XCLAIM use)    |
| `Streams[].StreamName`       | Redis Stream key to consume                                    |
| `Streams[].ContainerName`    | Azure Blob Storage container for attachments from that stream  |

---

## Message Format

The processor supports two wire formats:

### 1. JSON `data` field (preferred — produced by Frappe/Python)

```python
# Python producer (Frappe/RQ)
redis.xadd("stream-pleasntbiz", {
    "data": json.dumps({
        "name":           "MSG-00001",
        "phone":          "919876543210",
        "message":        "Hello, your booking is confirmed.",
        "attachment_url": "https://storage.example.com/…/receipt.pdf",
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
    "message_type":   "whatsapp",
    "message_name":   "MSG-00001",
    "phone":          "919876543210",
    "message":        "Hello, your booking is confirmed.",
    "name":           "MSG-00001",
    "attachment_url": "https://storage.example.com/…/receipt.pdf"
})
```

### Internal retry fields (added by the processor)

| Field           | Description                               |
|-----------------|-------------------------------------------|
| `retry_count`   | Number of times this message has been retried |

---

## Retry & Dead-Letter Policy

| Setting                         | Default | Description                              |
|---------------------------------|---------|------------------------------------------|
| `RetrySettings.MaxRetries`      | 10      | Maximum delivery attempts                |
| `RetrySettings.BaseDelaySeconds`| 30      | Base for exponential back-off            |
| Max delay cap                   | 3600 s  | Retry interval never exceeds 1 hour      |

Retry delay formula: `min(30 × 2^(retryCount-1), 3600)` seconds.

---

## Migration from Azure Service Bus

See [migration.md](migration.md) for the step-by-step migration guide.

---

## Further Improvements

See [agent-guide.md](agent-guide.md) for a prioritised list of recommended
enhancements and implementation guidance.
