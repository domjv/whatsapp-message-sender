# Install WhatsappMessageSender as a Windows Service
#
# Prerequisites — see docs/windows-service.md and docs/setup.md:
#   - .NET 9 Runtime installed
#   - Google Chrome installed for the service account user
#   - appsettings.json configured in the publish folder
#   - WhatsApp Web logged in once (interactive run with Headless: false)
#
# Run this script as Administrator.

$ErrorActionPreference = "Stop"

$serviceName  = "WhatsappMessageSender"
$publishDir   = "C:\Services\WhatsappMessageSender"
$exePath      = Join-Path $publishDir "WhatsappMessageSender.exe"
$repoRoot     = Split-Path -Parent $PSScriptRoot

Write-Host "Publishing to $publishDir ..."
dotnet publish (Join-Path $repoRoot "WhatsappMessageSender\WhatsappMessageSender.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o $publishDir

if (-not (Test-Path (Join-Path $publishDir "appsettings.json"))) {
  Write-Warning "appsettings.json not found in $publishDir."
  Write-Warning "Copy your configured appsettings.json before starting the service."
}

# Set production environment (loads appsettings.Production.json with Headless: true)
[System.Environment]::SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production", "Machine")

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
  Write-Host "Service '$serviceName' already exists. Stopping before re-registering ..."
  Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
  sc.exe delete $serviceName | Out-Null
  Start-Sleep -Seconds 2
}

Write-Host "Creating service '$serviceName' ..."
New-Service -Name $serviceName `
  -BinaryPathName "`"$exePath`"" `
  -DisplayName "WhatsApp Message Sender" `
  -Description "Consumes Azure Service Bus notifications and sends via WhatsApp Web." `
  -StartupType Automatic

Write-Host ""
Write-Host "IMPORTANT: Configure the service to run as a user account (not LocalSystem)."
Write-Host "Chrome requires a real user profile. Example:"
Write-Host '  sc.exe config WhatsappMessageSender obj= "DOMAIN\svc-whatsapp" password= "YourPassword"'
Write-Host ""
Write-Host "Then start the service:"
Write-Host "  Start-Service -Name $serviceName"
Write-Host ""
Write-Host "View logs: Event Viewer -> Windows Logs -> Application (source: $serviceName)"
Write-Host "Full guide: docs\windows-service.md"

# Uncomment after configuring the service account:
# Start-Service -Name $serviceName
# Get-Service -Name $serviceName

# Uninstall:
# Stop-Service -Name $serviceName -Force
# sc.exe delete $serviceName
