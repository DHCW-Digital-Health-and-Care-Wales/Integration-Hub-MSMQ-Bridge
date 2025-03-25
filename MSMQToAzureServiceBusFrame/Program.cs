using System;
using System.Messaging;
using Azure.Messaging.ServiceBus;
using System.Threading.Tasks;
using MSMQToAzureServiceBusFrame.Configuration;

namespace MSMQToAzureServiceBusFrame
{
    class Program
    {
       static async Task Main(string[] args)
        {
            AppConfig config = null;
            Console.WriteLine("Number of arguments: " + args.Length);
            if (args.Length > 0)
            {
                 config =  AppConfig.ReadCommandLineArg(args);
               
            }
            else
            {
                 config = AppConfig.ReadEnvConfig();
                Console.WriteLine("Connection String: " + config.ServiceBusConnectionString);
            }
                
            // Initialize the MSMQ queue
            using (MessageQueue msmqQueue = new MessageQueue(config.MsmqConnectionString))
            {
                msmqQueue.Formatter = new XmlMessageFormatter(new String[] { "System.String,mscorlib" });

                var conn = config.ServiceBusConnectionString;

                //// Initialize Service Bus client
                var client = new ServiceBusClient(config.ServiceBusConnectionString);
                var sender = client.CreateSender(config.ServiceBusQueueName);

                Console.WriteLine("Starting to consume messages from MSMQ...");

                while (true)
                {
                    try
                    {
                        // Receive message from MSMQ
                        var msmqMessage = msmqQueue.Receive();

                        if (msmqMessage != null)
                        {
                            // Process message (convert to string)
                            string messageBody = msmqMessage.Body.ToString();
                            
                            // Create a Service Bus message
                            var serviceBusMessage = new ServiceBusMessage(messageBody);

                            // Send the message to Azure Service Bus
                            await sender.SendMessageAsync(serviceBusMessage);
                            Console.WriteLine("Message sent to Azure Service Bus.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                }
            }
        }
    }
}
