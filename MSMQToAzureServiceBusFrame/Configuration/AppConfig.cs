using System;
using System.Collections.Generic;
using System.Linq;


namespace MSMQToAzureServiceBusFrame.Configuration
{
    public class AppConfig
    {
        public string MsmqConnectionString { get; set; }
        public string ServiceBusConnectionString { get; set; }
        public string ServiceBusQueueName { get; set; }

        private static List<string> _args = new List<string> { "MSMQ_CONNECTION_STRING", "SERVICE_BUS_CONNECTION_STRING", "SERVICE_BUS_QUEUENAME" };
        private static List<string> argsvalue;
        private static Dictionary<string, string> argMap = new Dictionary<string, string>();

        public AppConfig(string msmqConnectionString, string serviceBusConnectionString, string serviceBusQueueName)
        {
            MsmqConnectionString = msmqConnectionString;
            ServiceBusConnectionString = serviceBusConnectionString;
            ServiceBusQueueName = serviceBusQueueName;
        }
            
        public static AppConfig ReadEnvConfig()
        {
            return new AppConfig(
                ReadEnv("MSMQ_CONNECTION_STRING", true),
                ReadEnv("SERVICE_BUS_CONNECTION_STRING", false),                         
                ReadEnv("SERVICE_BUS_QUEUENAME", false)
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
                argMap["SERVICE_BUS_QUEUENAME"]);

        }

    }
}
