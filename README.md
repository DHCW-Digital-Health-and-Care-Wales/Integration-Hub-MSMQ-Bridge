# Integration-Hub-MSMQ-Bridge


## Prerequisites
Make sure you have the following installed and set up:
- [.NET Framework](https://dotnet.microsoft.com/download) version 8.0
- `az login --tenant <YOUR_TENNANT>`


## Running the Project
To run the project locally, follow these steps:
1. Clone the repository.
2. Don't forget `az login --tenant <YOUR_TENNANT>`
3. Setup local configuration according to `Required configuration for local development` section
2. Rebuild and run the project.

Pass following as command line arguments or Evironment variable
--MSMQ_CONNECTION_STRING your_connection_string 
--SERVICE_BUS_CONNECTION_STRING your_connection_string 
--SERVICE_BUS_QUEUENAME your_queue_name"

## Running as a Windows Service on a remote server

The app auto-detects how it was launched:
- Interactive console (double-click / `.exe` from a shell) -> runs the message loop directly, same as local dev.
- No interactive session (started by the Service Control Manager) -> runs as the `MsmqAzureServiceBusBridge` Windows Service.

### 1. Authentication - do NOT rely on `az login`

`az login` only caches a token for your interactive user session. A Windows Service typically runs
as `LocalSystem`/`NetworkService` or a dedicated service account with no interactive login, so
`AzureCliCredential`/`SharedTokenCacheCredential` (part of `DefaultAzureCredential`'s fallback chain)
won't find anything. For an unattended service, register an Entra ID app registration (service principal)
and provide its credentials via environment variables, which `DefaultAzureCredential` picks up automatically
through `EnvironmentCredential`:

```
AZURE_TENANT_ID=<tenant-id>
AZURE_CLIENT_ID=<app-registration-client-id>
AZURE_CLIENT_SECRET=<client-secret>          # or use a certificate instead of a secret
```

Grant that service principal the appropriate role (e.g. `Azure Service Bus Data Sender`) on the Service Bus namespace.
Set these as machine-level environment variables (`setx /M ...`) on the remote server before installing the service,
or as `SERVICE_START_NAME`/registry-based service environment variables.

### 2. Publish and copy to the server

```powershell
msbuild MSMQToAzureServiceBusFrame.csproj /p:Configuration=Release
# Copy bin\Release\* to e.g. C:\Services\MsmqBridge\ on the remote server
```

### 3. Test it interactively first, then install as a service

Before installing as a service, run it interactively from the output folder to confirm the
connection strings/topic name are correct - this is the exact same invocation as local dev
(see `Running the Project` above), just pointed at the remote server's queue:

```powershell
cd "C:\Services\MsmqBridge"
.\MSMQToAzureServiceBusFrame.exe `
  --MSMQ_CONNECTION_STRING "10.57.106.225\private$\proms_queue_hdda_sit" `
  --SERVICE_BUS_CONNECTION_STRING "Endpoint=sb://uks-dhcw-ih-dev-sbns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=*SHARED KEY*" `
  --SERVICE_BUS_TOPIC_NAME "uks-dhcw-ih-dev-sbns-sbt-msmq-receiver"
```

Confirm you see `Message sent to Azure Service Bus.` for a test message, then `Ctrl+C` to stop it.

Once confirmed, install it as a Windows Service (run as Administrator on the target server).
`New-Service -BinaryPathName` takes a single string, so the exe path and each `--arg value` pair
must be wrapped in escaped quotes:

```powershell
$exePath = "C:\Services\MsmqBridge\MSMQToAzureServiceBusFrame.exe"
$msmqConn = ".\private$\ALEX_TEST_QUEUE"
$sbConn = "Endpoint=sb://uks-dhcw-ih-dev-sbns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=*KEY HERE*"
$sbTopic = "uks-dhcw-ih-dev-sbns-sbt-msmq-receiver"

$binaryPathName = "`"$exePath`" --MSMQ_CONNECTION_STRING `"$msmqConn`" --SERVICE_BUS_CONNECTION_STRING `"$sbConn`" --SERVICE_BUS_TOPIC_NAME `"$sbTopic`""

New-Service -Name "MsmqAzureServiceBusBridge" `
    -BinaryPathName $binaryPathName `
    -DisplayName "MSMQ to Azure Service Bus Bridge" `
    -Description "Forwards MSMQ messages to Azure Service Bus." `
    -StartupType Automatic

# Auto-restart on crash: restart after 10s, 30s, 60s for the 1st/2nd/subsequent failures
sc.exe failure "MsmqAzureServiceBusBridge" reset= 86400 actions= restart/10000/restart/30000/restart/60000

Start-Service "MsmqAzureServiceBusBridge"
```

> Passing a shared access key on the command line means it ends up in the service's registry
> config (`HKLM:\SYSTEM\CurrentControlSet\Services\MsmqAzureServiceBusBridge\ImagePath`), readable
> by local admins. Prefer the environment-variable approach in step 1 (`AZURE_CLIENT_SECRET` etc. via
> Entra ID) for anything beyond a quick dev/test install, and set `MSMQ_CONNECTION_STRING`,
> `SERVICE_BUS_CONNECTION_STRING`, `SERVICE_BUS_TOPIC_NAME` as machine-level environment variables
> (`setx /M ...`) instead of command-line args.

> **"Access is denied" from `New-Service`**: creating a service requires an elevated (Run as
> Administrator) PowerShell session - being a local admin isn't enough on its own if the console
> itself wasn't launched elevated. Close the current session and reopen PowerShell/Windows
> Terminal via "Run as administrator", then re-run the `New-Service` command. You can confirm
> elevation first with `([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)`
> (should print `True`). If `New-Service` fails, the service was never created, so the subsequent
> `sc.exe failure` step will correctly report `OpenService FAILED 1060` ("service does not exist") -
> that error is expected until `New-Service` itself succeeds.

> **"Cannot start service" from `Start-Service`**: `Start-Service`'s error message is a generic
> wrapper - it doesn't show the real reason. Check the Windows **Event Viewer** ->
> `Windows Logs > System` (source `Service Control Manager`) and `Windows Logs > Application`
> (source matches the service name / log4net) for the actual failure. If the app was created
> without command-line arg support in the service host, or the arguments couldn't be read, `OnStart`
> throws an unhandled exception, which surfaces here as this generic error. Rebuild with the latest
> code (the service now correctly forwards the exe's own command-line args - the ones baked into
> `-BinaryPathName` - into the pump; earlier builds silently ignored them and fell back to reading
> environment variables instead, which are unset by default) before retrying.

### 4. Manage the service

```powershell
Get-Service MsmqAzureServiceBusBridge
Stop-Service MsmqAzureServiceBusBridge
Start-Service MsmqAzureServiceBusBridge
sc.exe delete MsmqAzureServiceBusBridge   # uninstall (service must be stopped first)
```

Logs are written to `log.txt` next to the exe (see `App.config`'s `log4net` section) regardless of
whether the app is run interactively or as a service.

### Viewing logs

Two sources, depending on what you need:

- **`log.txt` next to the exe** - the app's own activity log (message-by-message: sent/received,
  transient errors from the pump). Written via log4net regardless of console vs. service mode.
  ```powershell
  Get-Content "C:\Services\MsmqBridge\log.txt" -Tail 50 -Wait   # -Wait tails it live
  ```

- **Event Viewer -> Windows Logs -> Application** - service lifecycle events (start/stop, since
  `AutoLog = true` on `MsmqBridgeService`), and critically, any *unhandled* exception that crashes
  `OnStart` before log4net gets a chance to write anything to `log.txt` (e.g. a missing/invalid
  config value). This is the log to check first when `Start-Service` fails.
  ```powershell
  Get-WinEvent -LogName Application -MaxEvents 20 |
      Where-Object { $_.Message -like "*MsmqAzureServiceBusBridge*" } |
      Format-List TimeCreated, LevelDisplayName, Message
  ```



Run elevated (Run as Administrator) on the target server:

```powershell
# 1. Stop and delete the existing service
Stop-Service "MsmqAzureServiceBusBridge" -ErrorAction SilentlyContinue
sc.exe delete "MsmqAzureServiceBusBridge"

# 2. Replace the binaries with the newly built output - do this every time you change code,
#    even if the folder path is unchanged. Forgetting this step is the #1 cause of "the fix
#    didn't work" - the service will silently keep running the old exe.
New-Item -ItemType Directory -Path "C:\Services\MsmqBridge" -Force | Out-Null
Copy-Item -Path "C:\IntHub\Integration-Hub-MSMQ-Bridge\Integration-Hub-MSMQ-Bridge\MSMQToAzureServiceBusFrame\bin\Release\*" -Destination "C:\Services\MsmqBridge\" -Recurse -Force

# Sanity check: confirm the deployed exe's timestamp matches your latest build
Get-Item "C:\Services\MsmqBridge\MSMQToAzureServiceBusFrame.exe" | Select-Object LastWriteTime

# 3. Recreate the service (re-run the same New-Service block from step 3 above)
$exePath = "C:\Services\MsmqBridge\MSMQToAzureServiceBusFrame.exe"
$msmqConn = ".\private$\ALEX_TEST_QUEUE"
$sbConn = "Endpoint=sb://uks-dhcw-ih-dev-sbns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=*KEY HERE*"
$sbTopic = "uks-dhcw-ih-dev-sbns-sbt-msmq-receiver"

$binaryPathName = "`"$exePath`" --MSMQ_CONNECTION_STRING `"$msmqConn`" --SERVICE_BUS_CONNECTION_STRING `"$sbConn`" --SERVICE_BUS_TOPIC_NAME `"$sbTopic`""

New-Service -Name "MsmqAzureServiceBusBridge" `
    -BinaryPathName $binaryPathName `
    -DisplayName "MSMQ to Azure Service Bus Bridge" `
    -Description "Forwards MSMQ messages to Azure Service Bus." `
    -StartupType Automatic

sc.exe failure "MsmqAzureServiceBusBridge" reset= 86400 actions= restart/10000/restart/30000/restart/60000
Start-Service "MsmqAzureServiceBusBridge"
```

> `sc.exe delete` sometimes reports success but leaves the service marked "pending deletion" until
> every handle to it is closed (e.g. an open Services console/`services.msc` window, or a
> `Get-Service` result still referenced in the same PowerShell session). Close `services.msc` and
> any variables holding the old `Get-Service`/`New-Service` result, or open a fresh PowerShell
> session, if `New-Service` for the same name fails with "service already exists" right after
> deleting it.
>
> If only the **arguments** changed (not the exe itself), you don't need to delete/recreate the
> service at all - just update the registry directly and restart it:
> ```powershell
> Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\MsmqAzureServiceBusBridge" -Name ImagePath -Value $binaryPathName
> Restart-Service "MsmqAzureServiceBusBridge"
> ```

> **"Access to Message Queuing system is denied" (`MessageQueueException 0x80004005`) only when
> running as a service**: `New-Service` defaults to running the service as `LocalSystem`. Even
> though the exact same connection string worked when you ran the exe interactively as yourself
> (step 3), `LocalSystem` is a different Windows identity that has no ACL entry on the queue.
> Fix by granting that identity permission on the queue:
> 1. Open **Computer Management** -> **Services and Applications** -> **Message Queuing** ->
>    **Private Queues** -> find the queue (e.g. `ALEX_TEST_QUEUE`) -> right-click **Properties** ->
>    **Security** tab.
> 2. Add **SYSTEM** (or **NETWORK SERVICE** if you configure the service to run as that account
>    instead - see below) and grant **Receive Message** and **Peek Message** (Allow).
> 3. `Restart-Service "MsmqAzureServiceBusBridge"`.
>
> Alternatively, run the service under a dedicated service account that already has queue
> permissions (recommended for anything beyond local dev/test), instead of `LocalSystem`:
> ```powershell
> $cred = Get-Credential   # domain\svc-msmqbridge or .\svc-msmqbridge
> Set-Service "MsmqAzureServiceBusBridge" -Credential $cred
> Restart-Service "MsmqAzureServiceBusBridge"
> ```

### Notes for remote MSMQ

If the MSMQ queue lives on a different machine than the service, `MSMQ_CONNECTION_STRING` must use the
`FormatName:DIRECT=OS:` / `FormatName:DIRECT=TCP:` syntax (handled automatically by `AppConfig`), and the
service account needs network access to that machine's MSMQ (firewall + MSMQ permissions on the remote queue).

