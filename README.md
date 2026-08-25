# Integration-Hub-MSMQ-Bridge

Forwards messages from an MSMQ queue to a REST endpoint (HTTP POST).

## Prerequisites
Make sure you have the following installed and set up:

- .NET Framework 4.7.2 developer pack / MSBuild (Visual Studio 2022 or Build Tools)
- A running REST endpoint (e.g. an Azure-hosted Python microservice) with HTTPS enabled

> **Note:** `az login` is no longer required - this bridge no longer uses Azure Service Bus.
> Authentication to the REST endpoint uses a shared API key header. If you later switch to
> Entra ID client-credentials auth, re-add `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` /
> `AZURE_CLIENT_SECRET` environment variables and use `Azure.Identity` in the Worker.

## Configuration
The app reads its settings from command-line arguments, falling back to machine-level environment variables:

| Setting | Arg / env var | Example |
| --- | --- | --- |
| MSMQ queue | `MSMQ_CONNECTION_STRING` | `.\private$\ALEX_TEST_QUEUE` or `FormatName:DIRECT=OS:server\private$\myqueue` |
| REST endpoint URL | `REST_ENDPOINT_URL` | `https://yourapp.azurewebsites.net/api/messages` |
| REST API key | `REST_API_KEY` | any shared secret your endpoint validates |
| Request timeout (s) | `REST_TIMEOUT_SECONDS` | default `30` |
| Max retry attempts | `MAX_RETRY_ATTEMPTS` | default `5` |
| Dead-letter folder | `DEAD_LETTER_FOLDER` | default `dead-letter` next to the exe |

> **Passing a shared API key on the command line means it ends up readable by local admins in the
> service's registry ImagePath (`HKLM:\SYSTEM\CurrentControlSet\Services\...\ImagePath`). Prefer
> machine-level env vars (`setx /M REST_API_KEY ...`) for anything beyond quick dev/test installs.**

## Running locally

1. Clone the repository.
2. Set config via args or environment variables:

   ```
   --MSMQ_CONNECTION_STRING ".\private$\ALEX_TEST_QUEUE" --REST_ENDPOINT_URL "https://yourapp.azurewebsites.net/api/messages" --REST_API_KEY "your-key"
   ```
3. Rebuild and run.
4. Send a test message to the queue; confirm you see **Message sent to REST endpoint.** in console / `log.txt`.

## Running as a Windows Service on a remote server
The app auto-detects how it was launched:

- Interactive console -> runs the message loop directly (same as local dev).
- No interactive session (Service Control Manager) -> runs as the **MsmqRestBridge** Windows Service.

### 1. Publish and copy to the server

```powershell
msbuild MSMQRestBridge.csproj /p:Configuration=Release
# Copy bin\Release\* to e.g. C:\Services\MsmqBridge\
```

### 2. Test interactively first, then install as a service

```powershell
cd "C:\Services\MsmqBridge"
.\MSMQRestBridge.exe `
  --MSMQ_CONNECTION_STRING "FormatName:DIRECT=OS:10.57.106.225\private$\proms_queue_hdda_sit" `
  --REST_ENDPOINT_URL "https://yourapp.azurewebsites.net/api/messages" `
  --REST_API_KEY "YOUR_KEY"
```

Confirm test messages are delivered, then `Ctrl+C` to stop.

### 3. Install as a service (elevated PowerShell)

```powershell
$exePath = "C:\Services\MsmqBridge\MSMQRestBridge.exe"
$msmqConn = ".\private$\ALEX_TEST_QUEUE"
$restUrl = "https://yourapp.azurewebsites.net/api/messages"

$binaryPathName = "`"$exePath`" --MSMQ_CONNECTION_STRING `"$msmqConn`" --REST_ENDPOINT_URL `"$restUrl`""

New-Service -Name "MsmqRestBridge" `
    -BinaryPathName $binaryPathName `
    -DisplayName "MSMQ to REST Bridge" `
    -Description "Forwards MSMQ messages to the Azure-hosted REST ingestion service." `
    -StartupType Automatic

# Auto-restart on crash: restart after 10s/30s/60s for 1st/2nd/subsequent failures
sc.exe failure "MsmqRestBridge" reset= 86400 actions= restart/10000/restart/30000/restart/60000

Start-Service "MsmqRestBridge"
```

### 4. Manage the service

```powershell
Get-Service MsmqRestBridge
Stop-Service MsmqRestBridge
Start-Service MsmqRestBridge
sc.exe delete MsmqRestBridge   # must be stopped first
```

Logs are written to **`log.txt` next to the exe** (log4net). Tail live with:

```powershell
Get-Content "C:\Services\MsmqBridge\log.txt" -Tail 50 -Wait
```

For crashes before logging initializes (e.g. bad config), check **Event Viewer -> Windows Logs -> Application**:

```powershell
Get-WinEvent -LogName Application -MaxEvents 20 |
  Where-Object { $_.Message -like "*MsmqRestBridge*" } |
  Format-List TimeCreated, LevelDisplayName, Message
```

### Redeploying after a code change
Elevated PowerShell:

```powershell
Stop-Service "MsmqRestBridge" -ErrorAction SilentlyContinue
Copy-Item -Path "<repo>\bin\Release\*" -Destination "C:\Services\MsmqBridge\" -Recurse -Force
Get-Item "C:\Services\MsmqBridge\MSMQRestBridge.exe" | Select-Object LastWriteTime   # verify fresh build!
Start-Service "MsmqRestBridge"
```

If only arguments changed (not the exe), just update the registry and restart:

```powershell
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\MsmqRestBridge" -Name ImagePath -Value $binaryPathName
Restart-Service "MsmqRestBridge"
```

(`sc.exe delete` can leave the service "pending deletion" if `services.msc` is open - close it and retry in a fresh session.)

### Known issues & fixes

- **"Access is denied" from `New-Service`** - the PowerShell session isn't elevated. Reopen via *Run as administrator*; verify with `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)` printing `True`.
- **"Cannot start service"** - generic wrapper error. Check Event Viewer (Application log) for the real exception; usually missing/invalid config args.
- **"Access to Message Queuing system is denied" (0x80004005) only as a service** - LocalSystem has no ACL entry on the queue. Fix: Computer Management -> Message Queuing -> queue Properties -> Security -> add **SYSTEM** (or NETWORK SERVICE / dedicated account) with *Receive Message* + *Peek Message*, then restart the service. Alternatively run the service under an account that already has permissions: `$cred = Get-Credential; Set-Service "MsmqRestBridge" -Credential $cred`.
- **Retry semantics:** unlike Service Bus peek-lock, a failed HTTP POST means the message was already destructively received from MSMQ. The bridge requeues it at the **back of the queue** (ordering not guaranteed) and tracks attempt counts; after `MAX_RETRY_ATTEMPTS` failures the message body is written to a local dead-letter folder for manual inspection.

### Notes for remote MSMQ

If the MSMQ queue lives on a different machine than the service, `MSMQ_CONNECTION_STRING` must use the
`FormatName:DIRECT=OS:` / `FormatName:DIRECT=TCP:` syntax (handled automatically by `AppConfig`), and the
service account needs network access to that machine's MSMQ (firewall + MSMQ permissions on the remote queue).

