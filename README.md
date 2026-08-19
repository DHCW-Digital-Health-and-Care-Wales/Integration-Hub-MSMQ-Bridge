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
$msmqConn = "10.57.106.225\private$\proms_queue_hdda_sit"
$sbConn = "Endpoint=sb://uks-dhcw-ih-dev-sbns.servicebus.windows.net/;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=*SHARED KEY*"
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

### 4. Manage the service

```powershell
Get-Service MsmqAzureServiceBusBridge
Stop-Service MsmqAzureServiceBusBridge
Start-Service MsmqAzureServiceBusBridge
sc.exe delete MsmqAzureServiceBusBridge   # uninstall (service must be stopped first)
```

Logs are written to `log.txt` next to the exe (see `App.config`'s `log4net` section) regardless of
whether the app is run interactively or as a service.

### Notes for remote MSMQ

If the MSMQ queue lives on a different machine than the service, `MSMQ_CONNECTION_STRING` must use the
`FormatName:DIRECT=OS:` / `FormatName:DIRECT=TCP:` syntax (handled automatically by `AppConfig`), and the
service account needs network access to that machine's MSMQ (firewall + MSMQ permissions on the remote queue).

