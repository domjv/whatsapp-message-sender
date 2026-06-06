# Migration Guide: Azure Service Bus → Redis Streams

## Why we migrated

| Concern                    | Azure Service Bus                        | Redis Streams (new)                          |
|----------------------------|------------------------------------------|----------------------------------------------|
| Infrastructure dependency  | Azure subscription required              | Self-hosted Redis (or any managed Redis)     |
| Cost                       | Per-operation billing                    | Included in existing Redis infra             |
| Multi-channel support      | Separate namespace per use-case          | Multiple streams on one Redis instance       |
| Retry scheduling           | Built-in lock-based retry                | RQ-style sorted-set scheduler (explicit)     |
| Dead-letter                | Built-in DLQ                             | Dedicated `:dead` stream                     |
| Tooling alignment          | Azure-only ecosystem                     | Frappe/Python RQ already uses Redis          |

---

## Breaking changes

| Area                     | Before (Service Bus)                         | After (Redis Streams)                           |
|--------------------------|----------------------------------------------|-------------------------------------------------|
| `AppSettings.ServiceBus` | `ConnectionString`, `Queues[]`               | `AppSettings.Redis`: `ConnectionString`, `Streams[]` |
| Queue / stream naming    | `sbq-<tenant>` (Azure queue names)           | `stream-<tenant>` (Redis key names)             |
| `QueueConfig.QueueName`  | Azure queue name                             | `StreamConfig.StreamName` (Redis key)           |
| `MessageProperties.QueueName` | Azure queue reference                   | `MessageProperties.StreamName`                  |
| NuGet package            | `Microsoft.Azure.ServiceBus` 5.2.0           | `StackExchange.Redis` 2.8.16                    |
| `QueueProcessor.cs`      | Removed                                      | Replaced by `RedisStreamProcessor.cs`           |

---

## Step-by-step migration checklist

### 1. Redis infrastructure

- [ ] Stand up a Redis instance (version **6.2+** required for `XAUTOCLAIM`).
- [ ] Note the connection string (e.g. `redis-host:6379,password=secret`).
- [ ] Ensure the Redis instance is reachable from the machine running this app.

### 2. Update `appsettings.json`

Replace the `ServiceBus` block with a `Redis` block:

```json
{
  "Redis": {
    "ConnectionString": "<your-redis-connection-string>",
    "ConsumerGroup": "whatsapp-sender",
    "ConsumerName": "whatsapp-sender-1",
    "MaxConcurrentCalls": 2,
    "PendingMessageTimeoutSeconds": 300,
    "Streams": [
      { "StreamName": "stream-pleasntbiz", "ContainerName": "pleasantbiz-attachments" },
      { "StreamName": "stream-ivyliving",  "ContainerName": "ivyliving-attachments"  }
    ]
  }
}
```

> **Note:** `ContainerName` values remain unchanged — they still refer to Azure
> Blob Storage containers for file attachments.

### 3. Update the Python / Frappe producer

Remove any Azure Service Bus SDK usage and replace with Redis `XADD`:

```python
# requirements: redis-py>=4.0
import redis, json

r = redis.Redis.from_url("redis://localhost:6379")

def send_whatsapp_notification(stream_name, message_doc):
    r.xadd(stream_name, {
        "data": json.dumps({
            "name":           message_doc.name,
            "phone":          message_doc.mobile_no,
            "message":        message_doc.message,
            "attachment_url": message_doc.attachment_url or "",
            "message_name":   message_doc.name,
        }),
        "message_type": "whatsapp",
        "message_name": message_doc.name,
        "stream_name":  stream_name,
    })
```

For RQ-based enqueuing, wrap this call in an RQ job:

```python
from rq import Queue
from redis import Redis

q = Queue("default", connection=Redis.from_url("redis://localhost:6379"))
q.enqueue(send_whatsapp_notification, "stream-pleasntbiz", message_doc)
```

### 4. Verify consumer group creation

On first run the app auto-creates the consumer group for each configured stream.
You can verify with:

```bash
redis-cli XINFO GROUPS stream-pleasntbiz
```

### 5. Remove Azure Service Bus resources (when ready)

- [ ] Delete or disable Azure Service Bus queues.
- [ ] Remove `Microsoft.Azure.ServiceBus` NuGet package references from any other projects.
- [ ] Revoke or delete the Azure Service Bus shared-access key.

---

## Rollback plan

If you need to revert:

1. Restore `appsettings.json` from git (`git checkout HEAD~1 -- WhatsappMessageSender/appsettings.json`).
2. Restore `QueueProcessor.cs` from git (`git show HEAD~1:WhatsappMessageSender/Services/QueueProcessor.cs > …`).
3. Restore `AppSettings.cs`, `MessageProperties.cs`, `Program.cs` similarly.
4. Restore `WhatsappMessageSender.csproj` and run `dotnet restore`.
5. Any messages that arrived in Redis Streams during the window can be re-published
   to Azure Service Bus manually or left in Redis for a future migration attempt.
