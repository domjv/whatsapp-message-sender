# Windows Setup: Multiple WhatsApp Sender Instances

This guide explains how to run multiple WhatsApp sender instances on Windows,
with one Chrome profile and one WhatsApp number per ERP instance.

## Important Windows note

This application uses Selenium with normal Chrome/WhatsApp Web. Windows services
run in a non-interactive service session, so Chrome windows and QR codes may not
be visible the same way they are when you run the app from the desktop.

For that reason, use this order:

1. **Link the WhatsApp profile interactively first** by running the app from a
   normal desktop login for that Windows user.
2. Confirm WhatsApp Web loads without asking for a QR code.
3. Then install/start the Windows service.
4. If your environment cannot run visible Chrome reliably as a Windows service,
   use **Task Scheduler at user logon** instead. Task Scheduler runs in the
   signed-in user's desktop session and is often more reliable for browser
   automation.

## Recommended layout

Create one folder per ERP instance. Each folder contains the published app and
its own `appsettings.json`.

```text
C:\WhatsAppSender\ajk\
  WhatsappMessageSender.exe
  appsettings.json

C:\WhatsAppSender\ivyliving\
  WhatsappMessageSender.exe
  appsettings.json
```

Use one Chrome profile folder per ERP instance:

```text
C:\WhatsAppSenderData\Profiles\ajk
C:\WhatsAppSenderData\Profiles\ivyliving
```

## Example AJK `appsettings.json`

Keep only AJK topics/streams in the AJK folder's config.

```json
{
  "MessageBroker": "ServiceBus",
  "WhatsApp": {
    "ProfilePath": "C:\\WhatsAppSenderData\\Profiles\\ajk",
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

## First-time interactive profile linking

Open PowerShell as the same Windows user that will operate the sender and run:

```powershell
cd C:\WhatsAppSender\ajk
.\WhatsappMessageSender.exe
```

When Chrome opens WhatsApp Web:

1. Scan the QR code with the AJK WhatsApp phone.
2. Wait until WhatsApp Web is fully loaded.
3. Stop the console app with `Ctrl+C`.
4. Repeat for each ERP instance using that ERP's folder and phone.

The app creates a profile lock file while it runs. If startup says the profile is
already locked, another copy of that ERP sender is already running or did not
shut down cleanly.

## Option A: Install as a native Windows service

The application now supports Windows Service hosting. Run PowerShell as
Administrator.

Create AJK service:

```powershell
New-Service `
  -Name "whatsapp-sender-ajk" `
  -DisplayName "WhatsApp Sender - AJK" `
  -BinaryPathName "C:\WhatsAppSender\ajk\WhatsappMessageSender.exe" `
  -StartupType Automatic
```

Set recovery to restart on failure:

```powershell
sc.exe failure whatsapp-sender-ajk reset= 86400 actions= restart/15000/restart/30000/restart/60000
```

Start it:

```powershell
Start-Service whatsapp-sender-ajk
```

Check status:

```powershell
Get-Service whatsapp-sender-ajk
```

Stop it:

```powershell
Stop-Service whatsapp-sender-ajk
```

Remove it if needed:

```powershell
Stop-Service whatsapp-sender-ajk
sc.exe delete whatsapp-sender-ajk
```

Repeat with a different service name and folder for each ERP instance.

### Service account guidance

- Do not run multiple ERP services with the same `WhatsApp:ProfilePath`.
- Prefer a dedicated Windows user account for sender operation.
- Make sure that account has read/write access to `C:\WhatsAppSender` and
  `C:\WhatsAppSenderData`.
- Avoid `LocalSystem` unless you have tested Chrome/WhatsApp Web startup in your
  environment.

## Option B: Task Scheduler at user logon (often better for Chrome)

If Chrome does not behave correctly as a Windows service, create one scheduled
task per ERP instance.

PowerShell example for AJK:

```powershell
$Action = New-ScheduledTaskAction `
  -Execute "C:\WhatsAppSender\ajk\WhatsappMessageSender.exe" `
  -WorkingDirectory "C:\WhatsAppSender\ajk"

$Trigger = New-ScheduledTaskTrigger -AtLogOn
$Settings = New-ScheduledTaskSettingsSet -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask `
  -TaskName "WhatsApp Sender - AJK" `
  -Action $Action `
  -Trigger $Trigger `
  -Settings $Settings `
  -Description "Runs the WhatsApp sender for AJK when the sender user logs in."
```

This approach requires the sender user to be logged in after server restart, but
it is usually easier for visible Chrome and QR-code maintenance.

## Logs and troubleshooting

For native Windows services:

- Check service state with `Get-Service whatsapp-sender-ajk`.
- Check Windows Event Viewer under **Windows Logs > Application**.
- If the service starts and stops quickly, run the exe manually from PowerShell
  in that ERP folder to see the console error.

Common issues:

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Profile lock error | Another sender uses the same Chrome profile | Stop the duplicate service or fix `WhatsApp:ProfilePath` |
| QR code never visible | Windows service session cannot show Chrome | Run interactively first or use Task Scheduler |
| Wrong WhatsApp number sends messages | Topics/streams mixed between ERP configs | Keep each service config limited to one ERP |
| Service repeatedly restarts | WhatsApp Web not logged in before timeout | Run manually, scan QR, or increase `StartupWaitSeconds` |

## Windows resource planning for i7 + 8 GB RAM

Windows usually leaves less free memory than a minimal Linux server because the
OS, Defender/antivirus, desktop session, Chrome shared processes, and background
services consume more RAM.

Use these planning numbers per sender instance:

| Component per instance | Planning estimate on Windows |
| --- | ---: |
| .NET worker | 120-300 MB RAM |
| ChromeDriver | 20-80 MB RAM |
| Chrome + WhatsApp Web | 600-1,000 MB RAM |
| Attachments in flight | depends on file size |

Recommended budget: **about 1.2-1.5 GB RAM per Windows sender instance**, plus
at least **3 GB** reserved for Windows and safety headroom.

For an **i7 Windows machine with 8 GB RAM**:

- **recommended for production:** 2 to 3 instances;
- **possible after monitoring and low message volume:** 4 instances;
- **not recommended:** 5 or more instances unless RAM is upgraded.

If Task Manager shows high memory pressure or paging, reduce the number of
running sender instances or upgrade the machine to 16 GB RAM.
