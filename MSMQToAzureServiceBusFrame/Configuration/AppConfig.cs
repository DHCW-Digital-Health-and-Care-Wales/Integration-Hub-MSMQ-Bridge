using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;


namespace MSMQToAzureServiceBusFrame.Configuration
{
    public class AppConfig
    {
        public string MsmqConnectionString { get; set; }
        public string ServiceBusConnectionString { get; set; }
        public string ServiceBusTopicName { get; set; }

        private static List<string> _args = new List<string> { "MSMQ_CONNECTION_STRING", "SERVICE_BUS_CONNECTION_STRING", "SERVICE_BUS_TOPIC_NAME" };
        private static List<string> argsvalue;
        private static Dictionary<string, string> argMap = new Dictionary<string, string>();

        private static readonly Regex IpAddressPattern = new Regex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

        public AppConfig(string msmqConnectionString, string serviceBusConnectionString, string serviceBusTopicName)
        {
            MsmqConnectionString = NormalizeMsmqPath(msmqConnectionString);
            ServiceBusConnectionString = serviceBusConnectionString;
            ServiceBusTopicName = serviceBusTopicName;
        }

        // Path syntax (machine\private$\queue) only works for local queues.
        // Remote private queues require FormatName direct syntax, e.g.
        // FormatName:DIRECT=TCP:10.57.106.225\private$\queue (IP address)
        // FormatName:DIRECT=OS:MYSERVER\private$\queue (host name)
        private static string NormalizeMsmqPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("FormatName:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(".\\", StringComparison.Ordinal))
            {
                return path;
            }

            var separatorIndex = path.IndexOf('\\');
            if (separatorIndex <= 0)
            {
                return path;
            }

            var machine = path.Substring(0, separatorIndex);
            var protocol = IpAddressPattern.IsMatch(machine) ? "TCP" : "OS";
            return $"FormatName:DIRECT={protocol}:{path}";
        }
            
        public static AppConfig ReadEnvConfig()
        {
            return new AppConfig(
                ReadEnv("MSMQ_CONNECTION_STRING", true),
                ReadEnv("SERVICE_BUS_CONNECTION_STRING", false),                         
                ReadEnv("SERVICE_BUS_TOPIC_NAME", false)
            );
        }

        private  static string ReadEnv(string name, bool required)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (required && string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"Missing required configuration: {name}");
            }
            return value;
        }

        public static AppConfig ReadCommandLineArg(string[] args)
        {
            argsvalue = args.ToList();
            for (int i = 0; i < _args.Count; i++)
            {
                var index = argsvalue.IndexOf("--" + _args[i]);
                if (index >= 0 && argsvalue.Count > index)
                {
                    argMap.Add(_args[i], argsvalue[index + 1]);

                }
                else
                {
                    throw new InvalidOperationException($"Missing required configuration: {_args[i]}");

                }
            }
            return new AppConfig(argMap["MSMQ_CONNECTION_STRING"],
                argMap["SERVICE_BUS_CONNECTION_STRING"],
                argMap["SERVICE_BUS_TOPIC_NAME"]);

        }

    }
}
