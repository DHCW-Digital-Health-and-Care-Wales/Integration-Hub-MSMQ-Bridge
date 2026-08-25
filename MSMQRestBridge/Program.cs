using System;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using MSMQToAzureServiceBusFrame.Configuration;
using log4net;


namespace MSMQToAzureServiceBusFrame
{
    class Program
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Program));

        static async Task Main(string[] args)
        {
            // Must be set before the first log4net logger is created (see AssemblyInfo.cs).
            log4net.GlobalContext.Properties["LogDir"] = AppDomain.CurrentDomain.BaseDirectory;

            if (!Environment.UserInteractive)
            {
                // No interactive session (SessionId 0) means we were launched by the
                // Service Control Manager - hand control over to it.
                // NOTE: args here are the real process command-line args (from the service's
                // ImagePath). ServiceBase's own OnStart(args) parameter is a *different*,
                // separate mechanism (SCM "start parameters") that is empty unless passed via
                // `sc start Name arg1 arg2`, so we must forward these explicitly.
                ServiceBase.Run(new MsmqBridgeService(args));
                return;
            }

            AppConfig config = args.Length > 0
                ? AppConfig.ReadCommandLineArg(args)
                : AppConfig.ReadEnvConfig();

            using (var cts = new CancellationTokenSource())
            {
                // Handle shutdown signals
                AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
                {
                    Console.WriteLine("Shutting down gracefully...");
                    log.Info("Shutting down gracefully...");
                    cts.Cancel();
                };

                // Handling Ctrl+C
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    Console.WriteLine("Shutdown initiated...");
                    log.Info("Shutdown initiated...");
                    cts.Cancel();
                };

                var worker = new Worker(config);
                await worker.RunAsync(cts.Token);
            }
        }
    }
}
