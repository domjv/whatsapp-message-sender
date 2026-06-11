# Running Multiple WhatsApp Sender Service Instances

This document explains the supported production pattern for using different
WhatsApp numbers for different ERP instances without running several Chrome
profiles inside one .NET process.

## Design decision

Run **one application process per ERP instance**.

Each process/service gets its own:

- service name, for example `whatsapp-sender-ajk`;
- configuration file/environment, for example `DOTNET_ENVIRONMENT=ajk`;
- Chrome user-data directory, for example `/var/lib/whatsapp-sender/ajk/chrome-profile`;
- WhatsApp Web login/session, linked to that ERP instance's WhatsApp number;
- broker input list: only that ERP's Service Bus topics or Redis streams;
- local rate limiter and Selenium send semaphore.

This is intentionally simpler and safer than managing multiple Selenium browser
sessions inside one app process. The operating system service manager isolates
restart, logs, resource limits, and crashes per ERP instance.

## Code-level safeguards added for this model

### Unique Chrome profile lock

`WhatsAppService` creates and holds an exclusive lock file named
`.whatsapp-message-sender.lock` inside the configured `WhatsApp:ProfilePath`.
A second sender process using the same profile path fails fast with a clear
error instead of racing Chrome and risking profile corruption.

The lock is process-local and is released when the service shuts down and the
`WhatsAppService` is disposed.

### Startup waits for real WhatsApp Web login

The worker now navigates to WhatsApp Web and waits for a logged-in page before
connecting to the broker. Configure the timeout with:

```json
"WhatsApp": {
  "StartupWaitSeconds": 120
}
```

Increase this value during first-time QR linking. If login is not detected in
time, the service fails startup instead of consuming messages that it cannot send.

### Attachment temp file cleanup

Blob attachments are downloaded to `%TEMP%/BlobDownloads/<guid>/`. After the
send attempt finishes, both Service Bus and Redis processing paths delete the
downloaded file and remove the empty GUID directory. This prevents long-running
multi-service hosts from filling disk with attachment downloads.

### Rate-limit wait no longer blocks high-priority sends

The per-process send limiter is now awaited **before** entering the exclusive
Chrome/Selenium send semaphore, and the limiter reserves a throttled send slot
before returning. A throttled low-priority message can wait for a slot without
holding the browser lock, so a high-priority message in the same process is not
delayed by the low-priority wait.

### Redis retry backlog batching

Redis retry scheduler reads due retry entries in batches. Configure with:

```json
"Redis": {
  "RetrySchedulerBatchSize": 100
}
```

This keeps large retry backlogs from being loaded into memory in one scheduler
pass.

## Service Bus isolation rules

For multiple services, prefer **one subscription per service instance** and put
only that ERP's topics in that service's config.

Example for AJK:

```json
{
  "MessageBroker": "ServiceBus",
  "WhatsApp": {
    "ProfilePath": "/var/lib/whatsapp-sender/ajk/chrome-profile",
    "ChromeDriverPath": "auto",
    "StartupWaitSeconds": 180
  },
  "ServiceBus": {
    "ConnectionString": "Endpoint=sb://...",
    "UseWebSocketsTransport": true,
    "MaxConcurrentCalls": 2,
    "MaxAutoRenewDurationMinutes": 10,
    "Topics": [
      {
        "TopicName": "hm-ajk-attendance",
        "SubscriptionName": "whatsapp-message-sender-ajk",
        "ErpInstanceId": "ajk",
        "ContainerName": "ajk-attachments",
        "RequiresSession": true,
        "Priority": 20
      },
      {
        "TopicName": "hm-ajk-auth",
        "SubscriptionName": "whatsapp-message-sender-ajk",
        "ErpInstanceId": "ajk",
        "ContainerName": "ajk-attachments",
        "RequiresSession": true,
        "Priority": 0
      }
    ]
  }
}
```

Do not put `hm-ivyliving-*` topics in the AJK service config. That would send
Ivy Living messages from the AJK WhatsApp number.

## Redis isolation rules

Use one of these approaches:

1. **Preferred:** one stream per ERP instance and one service consuming only its
   own stream, for example `stream-ajk` for AJK and `stream-ivyliving` for Ivy
   Living.
2. If multiple services read the same stream with the same consumer group, they
   become competing consumers and either service may process a message.
3. If multiple services read the same stream with different consumer groups,
   each group can process the same message independently, which can duplicate
   WhatsApp sends.

## Per-instance configuration files

`Host.CreateDefaultBuilder` loads `appsettings.json` and then
`appsettings.{DOTNET_ENVIRONMENT}.json`. Use that to keep one binary and several
instance-specific configs:

- `appsettings.ajk.json`
- `appsettings.ivyliving.json`
- `appsettings.stthomas.json`

Set the service environment variable to select the right file:

```bash
DOTNET_ENVIRONMENT=ajk
```

## Example Linux systemd unit

Create one unit per ERP instance, changing only the name and environment:

```ini
[Unit]
Description=WhatsApp Sender - AJK
After=network-online.target graphical-session.target
Wants=network-online.target

[Service]
Type=simple
WorkingDirectory=/opt/whatsapp-message-sender
ExecStart=/usr/bin/dotnet /opt/whatsapp-message-sender/WhatsappMessageSender.dll
Environment=DOTNET_ENVIRONMENT=ajk
Environment=DOTNET_gcServer=1
Restart=always
RestartSec=15
KillSignal=SIGINT
TimeoutStopSec=60

# Optional guardrails; adjust after measuring real usage.
MemoryMax=1800M
CPUQuota=150%

[Install]
WantedBy=default.target
```

> Note: this app uses visible Chrome, not headless Chrome. For Linux servers,
> run it in a graphical user session or provide a display environment such as
> Xvfb and test QR linking before relying on unattended restarts.

## Resource planning

Actual usage depends on Chrome version, WhatsApp Web state, message volume,
attachment size, and how long the service has been running. Use these planning
numbers until you have measurements from your machine:

| Component per instance | Planning estimate |
| --- | ---: |
| .NET worker process | 100-250 MB RAM |
| ChromeDriver | 20-80 MB RAM |
| Chrome + WhatsApp Web tabs/process tree | 500-900 MB RAM |
| Temporary attachment working space | size of in-flight attachments |
| CPU while idle | low |
| CPU while sending / loading WhatsApp Web | short spikes |

Recommended budget: **about 1.0-1.3 GB RAM per service instance**, plus
operating-system headroom.

For an **i7 machine with 8 GB RAM**:

- reserve about 2 GB for OS, Chrome shared overhead, monitoring, antivirus, and
  safety headroom;
- budget the remaining 6 GB for sender instances;
- conservative production recommendation: **3 to 4 instances**;
- possible with careful monitoring and low traffic: **up to 5 instances**;
- not recommended without more RAM: **6+ instances**.

If the machine starts swapping, reduce instance count immediately. Chrome under
swap can become slow enough to cause Service Bus lock renewal pressure, retries,
or failed sends.

## Operational checklist

Before enabling a new service instance:

1. Create a unique Chrome profile directory.
2. Create a unique service name.
3. Create an instance-specific config file.
4. Verify the config contains only that ERP's topics/streams.
5. Verify `WhatsApp:ProfilePath` is unique and not used by any other service.
6. Start the service manually once and scan the WhatsApp Web QR code.
7. Wait until logs show `WhatsApp Web is logged in`.
8. Send a test message for that ERP instance.
9. Enable automatic service start/restart.
10. Monitor RAM, CPU, disk, and logs for at least one business day.
