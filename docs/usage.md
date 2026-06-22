# Usage Guide

How to run, monitor, and operate the WhatsApp Message Sender day to day.

---

## Running modes

### Local development (macOS / Linux / Windows)

```bash
cd WhatsappMessageSender
dotnet run
```

- Chrome opens visibly (unless `Headless: true`)
- Output goes to the terminal
- Stop with `Ctrl+C`

Use this mode for:

- First-time WhatsApp QR login
- Testing config changes
- Debugging send failures

### Windows Service (production)

See [windows-service.md](windows-service.md). The service:

- Starts automatically on boot
- Runs Chrome headless (no UI)
- Logs to Windows Event Viewer

```powershell
Start-Service  WhatsappMessageSender
Stop-Service   WhatsappMessageSender
Restart-Service WhatsappMessageSender
```

---

## Message flow

```
ERPNext publishes JSON → Azure Service Bus topic
                              ↓
              whatsapp-message-sender subscription
                              ↓
              Worker picks up message (by priority)
                              ↓
              WhatsApp send via Selenium
                              ↓
              POST delivery status → ERPNext instance API
```

### Message payload (from ERPNext)

The worker expects JSON like:

```json
{
  "message_id": "550e8400-e29b-41d4-a716-446655440000",
  "recipient_address": "919876543210",
  "body": "Your leave request has been approved.",
  "attachment_url": "https://storage.example.com/receipt.pdf",
  "message_name": "MSG-00001"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `message_id` | Recommended | UUID used for delivery-status callbacks |
| `recipient_address` | Yes | Phone number with country code, no `+` |
| `body` | Yes | Message text |
| `attachment_url` | No | Blob URL — downloaded when `ContainerName` is configured |
| `message_name` | Fallback id | Used if `message_id` is missing |

### Delivery status callbacks

After each send attempt the worker POSTs to the ERPNext instance that owns the topic:

| Status | When |
|--------|------|
| `Sent` | Message delivered successfully |
| `Failed` | Invalid format, unsupported type, or max retries exceeded |
| `Pending` | Send failed temporarily — will retry via Service Bus |

---

## Monitoring

### Windows Service

**Log files** (primary — one file per day):

```
C:\ProgramData\WhatsappMessageSender\logs\whatsapp-sender-2026-06-06.log
```

Configure in `appsettings.Production.json`:

```json
"FileLogging": {
  "LogDirectory": "C:\\ProgramData\\WhatsappMessageSender\\logs",
  "FileNamePrefix": "whatsapp-sender",
  "WriteToConsole": false,
  "RetainedFileCountLimit": 31
}
```

`WriteToConsole: false` means all output goes to log files only (recommended for services). Set `WriteToConsole: true` during interactive debugging.

**Windows Event Viewer** (host/framework errors only):

```
eventvwr.msc → Windows Logs → Application → Source: WhatsappMessageSender
```

| Message | Meaning |
|---------|---------|
| `Logging to ...` | Daily log file location on startup |
| `Started processing topic/subscription` | Connected to a Service Bus topic |
| `WhatsApp Web session is ready` | Chrome logged in successfully |
| `Processing message: ...` | Handling a notification |
| `Message ... sent via topic: ...` | Send succeeded |
| `Failed to update message status` | ERPNext callback failed — check URL/secret |

### Local development

Output goes to daily log files under `logs/` (see `FileLogging` in `appsettings.json`). With `WriteToConsole: true`, the same lines also appear in the terminal.

```bash
tail -f logs/whatsapp-sender-$(date +%Y-%m-%d).log
```

### Azure Service Bus

In the Azure Portal, check each topic's `whatsapp-message-sender` subscription:

- **Active message count** — backlog waiting to be processed
- **Dead-letter message count** — messages that failed permanently (invalid format, max retries)

---

## Priority and rate limiting

Messages are processed in priority order across all topics:

| Priority range | Behaviour |
|----------------|-----------|
| 0–9 (auth, payment, support) | Sent immediately, no rate cap |
| 10+ (leave, room, attendance, general) | Capped at 20 successful sends/minute |

If the worker is under heavy load, high-priority auth/payment messages jump ahead of attendance notifications.

---

## Adding a new ERPNext instance

1. Create Service Bus topics in Azure: `hm-{instance}-{feature}` for each feature
2. Create a `whatsapp-message-sender` subscription on each topic (sessions enabled)
3. Add to `ErpInstances` in `appsettings.json`:

```json
{
  "Id": "newinstance",
  "MessageTracking": {
    "ApiUrl": "https://newinstance.example.com/api/method/hostel_management.api.v1.endpoints.notification_delivery.report_delivery_status",
    "NotificationSecret": "secret-from-erpnext"
  }
}
```

4. Add topic entries to `ServiceBus:Topics` (copy an existing block and change names)
5. Restart the worker / service

No code changes required.

---

## Adding a new feature topic

If ERPNext adds a new notification type (e.g. `hm-ivyliving-fees`):

1. Create the topic and subscription in Azure
2. Add one entry to `ServiceBus:Topics`:

```json
{
  "TopicName": "hm-ivyliving-fees",
  "SubscriptionName": "whatsapp-message-sender",
  "ErpInstanceId": "ivyliving",
  "ContainerName": "ivyliving-attachments",
  "RequiresSession": true,
  "Priority": 15
}
```

3. Restart the worker

`ErpInstanceId` is optional when the topic name follows `hm-{instance}-{feature}`.

---

## Re-authenticating WhatsApp

WhatsApp Web sessions expire periodically. When sends start failing with login errors:

1. Stop the service: `Stop-Service WhatsappMessageSender`
2. Set `"Headless": false` temporarily
3. Run interactively as the service account user and scan QR
4. Set `"Headless": true` again
5. Start the service: `Start-Service WhatsappMessageSender`

The existing `ChromeProfile` folder is reused — do not delete it unless you want a fresh login.

---

## Common troubleshooting

| Symptom | Likely cause | Fix |
|---------|-------------|-----|
| Service stops on startup | Bad config | Run exe manually, read error; check Event Viewer |
| Messages stuck in subscription | Worker not running or crashed | Restart service; check Event Viewer |
| `message_id not found` in logs | ERPNext callback id mismatch | Ensure ERPNext publishes the same UUID in `message_id` |
| Attachments not sent | Missing `ContainerName` or `BlobStorage` | Add container name to topic config and blob connection string |
| Chrome version mismatch | Outdated chromedriver | Set `ChromeDriverPath` to `"auto"` |
| All sends fail immediately | WhatsApp session expired | Re-authenticate (see above) |
| Only one instance's callbacks fail | Wrong secret or URL | Check that instance's `ErpInstances` entry |

---

## Running tests

```bash
dotnet test
```

Tests cover message processors, tracking routing, and rate limiting. They do not require Chrome, Redis, or Azure.

---

## Related docs

- [Setup guide](setup.md) — initial installation and configuration
- [Windows Service guide](windows-service.md) — headless production deployment
- [Architecture](architecture.md) — internal design

## WhatsApp Cloud API template delivery

A stream or Service Bus topic can bypass the Selenium/WhatsApp Web sender and use the official WhatsApp Cloud API instead. Configure the shared Cloud API credentials in `WhatsAppCloudApi`, then set `WhatsAppApi:UseWhatsAppApi` on only the streams/topics that should use the official API.

When `UseWhatsAppApi` is true, the processor does not download attachments and does not call the existing WhatsApp Web flow. It sends an approved template message to Meta's `/messages` endpoint and stores the returned provider message id when Meta returns one.

Example topic configuration:

```json
{
  "WhatsAppCloudApi": {
    "ApiBaseUrl": "https://graph.facebook.com",
    "ApiVersion": "v20.0",
    "PhoneNumberId": "REPLACE_WITH_META_PHONE_NUMBER_ID",
    "AccessToken": "REPLACE_WITH_META_ACCESS_TOKEN"
  },
  "ServiceBus": {
    "Topics": [
      {
        "TopicName": "hm-ajk-attendance",
        "SubscriptionName": "whatsapp-message-sender",
        "ContainerName": "ajk-attachments",
        "WhatsAppApi": {
          "UseWhatsAppApi": true,
          "TemplateName": "attendance_status",
          "LanguageCode": "en",
          "TemplateParameters": [
            "student_name",
            "receiver_name",
            "student_name",
            "attendance_status",
            "attendance_date",
            "portal_sub_domain",
            "hostel_name"
          ]
        }
      }
    ]
  }
}
```

Template values can be supplied in the message payload as `TemplateParameters`, `template_parameters`, `templateParameters`, or `parameters`. If explicit values are absent, the optional configured `TemplateBody` is used to extract parameter values from the rendered legacy message text.
