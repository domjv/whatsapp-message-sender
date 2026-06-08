# WhatsApp Message Sender

A .NET 9 background worker that consumes notification messages from **Azure Service Bus** (or Redis Streams), sends them via **WhatsApp Web**, and reports delivery status back to **ERPNext / Frappe**.

## What it does

1. ERPNext publishes a notification to a Service Bus topic (e.g. `hm-ivyliving-attendance`)
2. This worker picks up the message from the `whatsapp-message-sender` subscription
3. It sends the WhatsApp message via Chrome / Selenium
4. It POSTs delivery status (`Sent`, `Failed`, `Pending`) to the correct ERPNext instance

## Documentation

| Guide | Description |
|-------|-------------|
| [Setup guide](docs/setup.md) | Prerequisites, configuration, Azure Service Bus, ERPNext, WhatsApp login |
| [Windows Service guide](docs/windows-service.md) | Install and run headless as a Windows Service (no UI) |
| [Usage guide](docs/usage.md) | Day-to-day operation, monitoring, updating, troubleshooting |
| [Architecture](docs/architecture.md) | Internal design and message flow |
| [Runtime flow](docs/runtime-flow.md) | Step-by-step processing walkthrough |

## Quick start (local development)

```bash
# Prerequisites: .NET 9 SDK, Google Chrome, chromedriver (or set ChromeDriverPath to "auto")

cd WhatsappMessageSender
dotnet run
```

Edit `WhatsappMessageSender/appsettings.json` before running — at minimum set:

- `ServiceBus:ConnectionString`
- `ErpInstances` — URL and secret per ERPNext site
- `WhatsApp:ProfilePath` — Chrome profile directory (must be logged into WhatsApp Web)

On first run a Chrome window opens. Scan the WhatsApp QR code when prompted.

## Quick start (Windows Server)

See [docs/windows-service.md](docs/windows-service.md) for the full guide. Summary:

1. Configure `appsettings.json` on the server
2. Log in to WhatsApp Web once (interactive run with `Headless: false`)
3. Publish the app and install as a Windows Service
4. Set `Headless: true` for production (no visible browser)

```powershell
# Run as Administrator
.\scripts\install-windows-service.ps1
```

## Project structure

```
WhatsappMessageSender/          Main worker project
WhatsappMessageSender.Tests/    Unit tests
docs/                           Guides and architecture
scripts/                        Windows Service install script
```

## Running tests

```bash
dotnet test
```
