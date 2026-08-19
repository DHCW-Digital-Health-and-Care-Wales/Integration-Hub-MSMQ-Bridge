using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MSMQToAzureServiceBusFrame.Configuration;

namespace MSMQToAzureServiceBusFrame
{
    /// <summary>
    /// Hosts the Worker message pump under the Windows Service Control Manager.
    /// Install/uninstall via sc.exe or New-Service - see README for commands.
    /// </summary>
    public class MsmqBridgeService : ServiceBase
    {
        public const string ServiceIdentifier = "MsmqAzureServiceBusBridge";

        private static readonly ILog log = LogManager.GetLogger(typeof(MsmqBridgeService));
        private readonly string[] _processArgs;
        private CancellationTokenSource _cts;
        private Task _runTask;

        /// <param name="processArgs">
        /// The real command-line args the process was launched with (embedded in the service's
        /// ImagePath). These are NOT the same as OnStart's own args parameter, which only carries
        /// SCM "start parameters" and is empty unless the service is started via
        /// `sc start Name arg1 arg2`.
        /// </param>
        public MsmqBridgeService(string[] processArgs)
        {
            _processArgs = processArgs;
            ServiceName = ServiceIdentifier;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        protected override void OnStart(string[] args)
        {
            log.Info("Service starting...");

            AppConfig config = _processArgs != null && _processArgs.Length > 0
                ? AppConfig.ReadCommandLineArg(_processArgs)
                : AppConfig.ReadEnvConfig();

            _cts = new CancellationTokenSource();
            var worker = new Worker(config);

            // OnStart must return quickly, so the message pump runs on a background task.
            _runTask = Task.Run(() => worker.RunAsync(_cts.Token));

            log.Info("Service started.");
        }

        protected override void OnStop()
        {
            log.Info("Service stopping...");
            Shutdown();
            log.Info("Service stopped.");
        }

        protected override void OnShutdown()
        {
            log.Info("Machine shutdown detected, stopping service...");
            Shutdown();
        }

        private void Shutdown()
        {
            _cts?.Cancel();
            try
            {
                // Give the pump a chance to close the Service Bus client gracefully.
                _runTask?.Wait(TimeSpan.FromSeconds(15));
            }
            catch (AggregateException ex)
            {
                log.Error("Error while stopping the message pump.", ex.Flatten());
            }
        }
    }
}
