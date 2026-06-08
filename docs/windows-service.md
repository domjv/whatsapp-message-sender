# Windows Service Guide

Run the WhatsApp Message Sender as a background Windows Service with **no visible browser or console window**.

---

## Overview

| Mode | Chrome window | Console | Auto-start on boot |
|------|--------------|---------|-------------------|
| `dotnet run` (dev) | Visible (unless Headless) | Visible | No |
| Windows Service | Hidden (headless) | Hidden | Yes |

The service name is **`WhatsappMessageSender`**. Logs are written to **Windows Event Viewer → Application** (source: `WhatsappMessageSender`).

---

## Requirements

- Windows Server 2019+ or Windows 10/11
- .NET 9 Runtime ([download](https://dotnet.microsoft.com/download/dotnet/9.0))
- Google Chrome (installed for the service account user)
- A dedicated Windows user account to run the service (see below)

### Why not LocalSystem?

Chrome and WhatsApp Web need a real user profile directory. Running as `LocalSystem` often fails because:

- Chrome cannot access a normal user profile path
- Session 0 isolation prevents desktop interaction
- The WhatsApp session in `ProfilePath` belongs to a specific user

**Recommended:** create a service account (e.g. `DOMAIN\svc-whatsapp`) and run the service under that account.

---

## Installation steps

### 1. Prepare the service account

1. Create a Windows user (local or domain) — e.g. `svc-whatsapp`
2. Grant **Log on as a service** right:
   - `secpol.msc` → Local Policies → User Rights Assignment → Log on as a service
3. Install Google Chrome while logged in as that user (or ensure Chrome is available machine-wide)
4. Create the profile directory and grant the service account full control:

```powershell
mkdir C:\ProgramData\WhatsappMessageSender\ChromeProfile
icacls C:\ProgramData\WhatsappMessageSender /grant "DOMAIN\svc-whatsapp:(OI)(CI)F" /T
```

### 2. Configure the app

Edit `WhatsappMessageSender/appsettings.json` on your build machine (or directly on the server):

- Set `ServiceBus:ConnectionString`
- Fill in all `ErpInstances` secrets and URLs
- Set WhatsApp profile to the server path:

```json
"WhatsApp": {
  "ProfilePath": "C:\\ProgramData\\WhatsappMessageSender\\ChromeProfile",
  "ChromeDriverPath": "auto",
  "Headless": true,
  "HideDriverWindow": true
}
```

`appsettings.Production.json` already sets headless defaults for Windows — it merges with `appsettings.json` when `DOTNET_ENVIRONMENT=Production`.

### 3. Log in to WhatsApp Web (one-time, interactive)

Before installing the service, log in to WhatsApp while logged in as the **service account user**:

**Option A — Run as the service user**

```powershell
# Open a session as the service account (or RDP/login as that user)
cd C:\Services\WhatsappMessageSender
$env:DOTNET_ENVIRONMENT = "Development"   # uses Headless: false from appsettings.json
.\WhatsappMessageSender.exe
```

Scan the QR code in the Chrome window that opens. Confirm WhatsApp loads on a second run without QR.

**Option B — Copy an existing profile**

If you already logged in on another machine, copy the entire `ChromeProfile` folder to `C:\ProgramData\WhatsappMessageSender\ChromeProfile` on the server.

### 4. Publish the app

On the server (or build machine, then copy):

```powershell
dotnet publish WhatsappMessageSender\WhatsappMessageSender.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o C:\Services\WhatsappMessageSender
```

Ensure `appsettings.json` (with real secrets) is in the publish folder.

### 5. Install the Windows Service

Run PowerShell **as Administrator**:

```powershell
cd C:\path\to\whatsapp-message-sender
.\scripts\install-windows-service.ps1
```

Or manually:

```powershell
$serviceName = "WhatsappMessageSender"
$exePath = "C:\Services\WhatsappMessageSender\WhatsappMessageSender.exe"

New-Service -Name $serviceName `
  -BinaryPathName "`"$exePath`"" `
  -DisplayName "WhatsApp Message Sender" `
  -Description "Consumes Azure Service Bus notifications and sends via WhatsApp Web." `
  -StartupType Automatic

# Run as service account (required for Chrome)
sc.exe config $serviceName obj= "DOMAIN\svc-whatsapp" password= "YourPassword"

# Set production environment
[System.Environment]::SetEnvironmentVariable(
  "DOTNET_ENVIRONMENT", "Production", "Machine")

Start-Service -Name $serviceName
```

### 6. Verify it is running

```powershell
Get-Service WhatsappMessageSender
# Status should be Running

# Check Event Viewer
eventvwr.msc
# Windows Logs → Application → filter by Source: WhatsappMessageSender
```

Look for log lines like:

```
Started processing topic/subscription (sessions): hm-ivyliving-auth/whatsapp-message-sender
WhatsApp Web session is ready.
```

---

## Service management

```powershell
# Start / stop / restart
Start-Service  WhatsappMessageSender
Stop-Service   WhatsappMessageSender
Restart-Service WhatsappMessageSender

# Check status
Get-Service WhatsappMessageSender | Format-List *

# View recent event log entries
Get-EventLog -LogName Application -Source WhatsappMessageSender -Newest 50
```

---

## Updating the service

1. Stop the service: `Stop-Service WhatsappMessageSender`
2. Publish new binaries to `C:\Services\WhatsappMessageSender` (keep `appsettings.json`)
3. Start the service: `Start-Service WhatsappMessageSender`

The WhatsApp session in `ChromeProfile` is preserved across updates.

---

## Uninstalling

```powershell
Stop-Service -Name WhatsappMessageSender -Force
sc.exe delete WhatsappMessageSender

# Optional: remove files
Remove-Item -Recurse -Force C:\Services\WhatsappMessageSender
```

---

## Troubleshooting

### Service starts then stops immediately

1. Check Event Viewer for the exception message
2. Run manually from a console to see errors:

```powershell
cd C:\Services\WhatsappMessageSender
$env:DOTNET_ENVIRONMENT = "Production"
.\WhatsappMessageSender.exe
```

Common causes: invalid `appsettings.json`, missing .NET 9 runtime, Service Bus connection failure.

### WhatsApp Web is not logged in

```
InvalidOperationException: WhatsApp Web is not logged in. Run the app once interactively...
```

Log in as the service account user with `Headless: false`, scan QR, then restart the service with `Headless: true`.

### Chrome / chromedriver version mismatch

Set `"ChromeDriverPath": "auto"` in config. Selenium Manager downloads a driver matching the installed Chrome version.

### No messages being processed

1. Confirm subscriptions exist in Azure for every configured topic
2. Confirm `RequiresSession` matches the Azure subscription setting
3. Check ERPNext is publishing to the correct topic names
4. Look for `Processing message:` lines in Event Viewer

### Service account cannot access Chrome profile

Ensure the service account owns `C:\ProgramData\WhatsappMessageSender\ChromeProfile`:

```powershell
icacls C:\ProgramData\WhatsappMessageSender /grant "DOMAIN\svc-whatsapp:(OI)(CI)F" /T
```

### Firewall / Service Bus connectivity

If AMQP TCP is blocked, ensure `"UseWebSocketsTransport": true` in `ServiceBus` config.

---

## Security notes

- Store secrets in environment variables or Azure Key Vault, not in plain text in `appsettings.json` on shared machines
- Restrict file permissions on the publish folder and Chrome profile to the service account only
- The service account needs only "Log on as a service" — no admin rights
