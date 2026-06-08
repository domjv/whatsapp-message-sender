# Setup Guide

This guide walks through everything needed before the WhatsApp Message Sender can run in production.

---

## Prerequisites

### Software

| Component | Version / notes |
|-----------|-----------------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 9.0 or later |
| Google Chrome | Latest stable — must be installed on the machine that sends messages |
| ChromeDriver | Bundled in the repo (`chromedriver-win64/`) or set `ChromeDriverPath` to `"auto"` to use Selenium Manager |
| Azure Service Bus namespace | Topics and subscriptions must already exist |
| Azure Blob Storage | Optional — only needed if messages include attachments |

### Azure Service Bus

Each ERPNext instance publishes to topics named:

```
hm-{instance}-{feature}
```

Examples: `hm-ivyliving-attendance`, `hm-ajk-auth`, `hm-stthomas-leave`

Every topic needs a subscription named **`whatsapp-message-sender`** with **sessions enabled** (matching `RequiresSession: true` in config).

Current instances: `ajk`, `ivyliving`, `macollege`, `staging`, `stthomas`

Current features per instance: `attendance`, `auth`, `general`, `leave`, `payment`, `room`, `support`

### ERPNext / Frappe

Each ERPNext site must expose the delivery-status callback endpoint:

```
POST /api/method/hostel_management.api.v1.endpoints.notification_delivery.report_delivery_status
Header: X-Notification-Secret: <secret>
Body:   { "message_id": "...", "status": "Sent|Failed|Pending", ... }
```

The worker routes callbacks to the correct site using the `ErpInstances` section in config (see below).

---

## Configuration

All settings live in `WhatsappMessageSender/appsettings.json`. Environment-specific overrides go in `appsettings.Production.json` (loaded automatically when `DOTNET_ENVIRONMENT=Production`).

### Message broker

```json
{
  "MessageBroker": "ServiceBus"
}
```

Set to `"Redis"` only if you use Redis Streams instead of Service Bus.

### Service Bus connection

```json
{
  "ServiceBus": {
    "ConnectionString": "Endpoint=sb://your-namespace.servicebus.windows.net/;SharedAccessKeyName=...;SharedAccessKey=...",
    "UseWebSocketsTransport": true,
    "MaxConcurrentCalls": 4,
    "MaxAutoRenewDurationMinutes": 10,
    "Topics": [ ... ]
  }
}
```

| Setting | Description |
|---------|-------------|
| `ConnectionString` | Shared access key for the namespace |
| `UseWebSocketsTransport` | Set `true` if AMQP TCP is blocked by firewall |
| `MaxConcurrentCalls` | Parallel message handlers (WhatsApp sends are still serialized) |
| `Topics` | One entry per topic the worker should consume |

Each topic entry:

```json
{
  "TopicName": "hm-ivyliving-attendance",
  "SubscriptionName": "whatsapp-message-sender",
  "ErpInstanceId": "ivyliving",
  "ContainerName": "ivyliving-attachments",
  "RequiresSession": true,
  "Priority": 20
}
```

| Field | Description |
|-------|-------------|
| `TopicName` | Azure Service Bus topic name |
| `SubscriptionName` | Always `whatsapp-message-sender` |
| `ErpInstanceId` | Links to `ErpInstances` for delivery-status callbacks |
| `ContainerName` | Azure Blob container for attachment downloads |
| `RequiresSession` | Must match the subscription setting in Azure |
| `Priority` | Lower = processed first. Priorities below 10 bypass the send rate cap |

**Priority reference:**

| Feature | Priority |
|---------|----------|
| auth | 0 |
| payment | 1 |
| support | 2 |
| leave | 10 |
| room | 15 |
| attendance | 20 |
| general | 30 |

`ErpInstanceId` can be omitted when the topic follows `hm-{instanceId}-{feature}` — the worker auto-detects the instance (longest id match wins, so `ivylivingbudget` beats `ivyliving`).

### ERPNext instances (delivery tracking)

```json
{
  "ErpInstances": [
    {
      "Id": "ivyliving",
      "MessageTracking": {
        "ApiUrl": "https://ivy-living.example.com/api/method/hostel_management.api.v1.endpoints.notification_delivery.report_delivery_status",
        "NotificationSecret": "your-secret-here"
      }
    }
  ]
}
```

Add one entry per ERPNext site. The `NotificationSecret` must match what ERPNext expects in the `X-Notification-Secret` header.

### WhatsApp / Chrome

```json
{
  "WhatsApp": {
    "ProfilePath": "C:\\ProgramData\\WhatsappMessageSender\\ChromeProfile",
    "ChromeDriverPath": "auto",
    "Headless": false,
    "HideDriverWindow": true
  }
}
```

| Field | Description |
|-------|-------------|
| `ProfilePath` | Directory Chrome uses to store the WhatsApp Web session. **Must be a dedicated folder**, not your personal Chrome profile |
| `ChromeDriverPath` | Path to `chromedriver.exe`, or `"auto"` to let Selenium Manager download a matching driver |
| `Headless` | `true` = no visible browser window. Auto-enabled when running as a Windows Service |
| `HideDriverWindow` | Hides the chromedriver console on Windows |

### Send rate limiting

```json
{
  "WhatsAppSendRateLimit": {
    "Enabled": true,
    "HighPriorityLessThan": 10,
    "MaxSendsPerMinute": 20
  }
}
```

Topics with priority 0–9 send immediately. Priorities 10+ share a cap of 20 successful sends per minute.

### Blob storage (optional)

Only needed for messages with attachments:

```json
{
  "BlobStorage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=..."
  }
}
```

---

## WhatsApp Web login (first time)

WhatsApp Web must be logged in before the worker can send messages. The session is stored in `WhatsApp:ProfilePath`.

### Step 1 — Create a dedicated Chrome profile folder

```powershell
# Windows
mkdir C:\ProgramData\WhatsappMessageSender\ChromeProfile

# macOS / Linux
mkdir -p ~/WhatsAppProfile
```

### Step 2 — Run interactively with Headless off

Set in `appsettings.json`:

```json
"WhatsApp": {
  "ProfilePath": "C:\\ProgramData\\WhatsappMessageSender\\ChromeProfile",
  "Headless": false
}
```

Run the app:

```bash
dotnet run --project WhatsappMessageSender
```

A Chrome window opens to WhatsApp Web. Scan the QR code with your phone within ~20 seconds.

### Step 3 — Verify the session persists

Stop the app (`Ctrl+C`) and run again. WhatsApp Web should load without asking for a QR code.

### Step 4 — Switch to headless for production

Once logged in, set `"Headless": true` (or use `appsettings.Production.json`) before installing as a Windows Service.

---

## Secrets management

Do not commit real secrets to git. Options:

**Environment variables** (recommended for production):

```powershell
[System.Environment]::SetEnvironmentVariable(
  "ServiceBus__ConnectionString", "Endpoint=sb://...", "Machine")
```

Double underscore `__` maps to nested JSON keys.

**User secrets** (local development only):

```bash
cd WhatsappMessageSender
dotnet user-secrets init
dotnet user-secrets set "ServiceBus:ConnectionString" "Endpoint=sb://..."
dotnet user-secrets set "ErpInstances:0:MessageTracking:NotificationSecret" "secret"
```

---

## Validate configuration

The app validates config at startup. If something is wrong it exits immediately with an error message. Common issues:

| Error | Fix |
|-------|-----|
| `Channel 'hm-...' has no resolvable MessageTracking` | Add the instance to `ErpInstances` or set `ErpInstanceId` |
| `MessageTracking configuration is missing` | At least one topic must resolve to valid tracking settings |
| `WhatsApp Web is not logged in` | Run interactively once with `Headless: false` and scan QR |

---

## Build and publish

```bash
# Development build
dotnet build

# Windows server publish
dotnet publish WhatsappMessageSender/WhatsappMessageSender.csproj \
  -c Release -r win-x64 --self-contained false \
  -o C:\Services\WhatsappMessageSender
```

Copy your configured `appsettings.json` into the publish folder before starting the service.

---

## Next steps

- **Windows Server production:** [windows-service.md](windows-service.md)
- **Day-to-day operation:** [usage.md](usage.md)
