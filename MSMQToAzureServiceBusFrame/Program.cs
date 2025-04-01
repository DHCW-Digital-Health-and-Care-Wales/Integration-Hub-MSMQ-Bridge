using System;
using System.Messaging;
using Azure.Messaging.ServiceBus;
using System.Threading.Tasks;
using MSMQToAzureServiceBusFrame.Configuration;
using log4net;


namespace MSMQToAzureServiceBusFrame
{
    class Program
    {
        // Add a static flag to control the shutdown process
        private static bool _isShuttingDown = false;

        private static readonly ILog log = LogManager.GetLogger(typeof(Program));

        static async Task Main(string[] args)
        {
            AppConfig config = null;
            if (args.Length > 0)
            {
                config = AppConfig.ReadCommandLineArg(args);
            }
            else
            {
                config = AppConfig.ReadEnvConfig();
            }

            // Initialize the MSMQ queue
            using (MessageQueue msmqQueue = new MessageQueue(config.MsmqConnectionString))
            {
                msmqQueue.Formatter = new XmlMessageFormatter(new String[] { "System.String,mscorlib" });
                msmqQueue.MessageReadPropertyFilter.ArrivedTime = true;

                var conn = config.ServiceBusConnectionString;

                //// Initialize Service Bus client
                var client = new ServiceBusClient(config.ServiceBusConnectionString);
                var serviceBusSender = client.CreateSender(config.ServiceBusQueueName);  

                Console.WriteLine("Starting to consume messages from MSMQ...");
                log.Info("Starting to consume messages from MSMQ...");

                // Handle shutdown signals
                AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
                {
                    // Mark shutdown flag
                    _isShuttingDown = true;
                    Console.WriteLine("Shutting down gracefully...");
                    log.Error("Shutting down gracefully...");
                };

                // Handling Ctrl+C
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;  
                    _isShuttingDown = true;  
                    Console.WriteLine("Shutdown initiated...");
                    log.Error("Shutdown initiated...");
                };

                try
                {
                    while (!_isShuttingDown)
                    {
                        try
                        {
                            // Receive message from MSMQ
                            var msmqMessage = msmqQueue.Receive();

                            if (msmqMessage != null)
                            {
                                // Process message (convert to string)
                                string messageBody = msmqMessage.Body.ToString();

                                // Convert the message content into a byte array encoding
                                byte[] messageBytes = AppEncoding.DetectEncoding(messageBody);

                                // Create ReadOnlyMemory<byte> from the byte array
                                ReadOnlyMemory<byte> messageMemory = new ReadOnlyMemory<byte>(messageBytes);


                                DateTime arrivedTime = msmqMessage.ArrivedTime;

                                // Create a Service Bus message
                                var serviceBusMessage = new ServiceBusMessage(messageMemory);

                                serviceBusMessage.ApplicationProperties.Add("MSMQArrivedTime", arrivedTime);
                                // Send the message to Azure Service Bus
                                await serviceBusSender.SendMessageAsync(serviceBusMessage);  
                                Console.WriteLine("Message sent to Azure Service Bus.");
                                log.Info("Message sent to Azure Service Bus.");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Error: {ex.Message}");
                            log.Error($"Error: {ex.Message}");
                            _isShuttingDown = true;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Operation was canceled.");
                    log.Error("Operation was canceled.");
                    _isShuttingDown = true;
                }
                finally
                {
                    // Clean up
                    await serviceBusSender.CloseAsync();  
                    await client.DisposeAsync();
                    Console.WriteLine("Service Bus sender and client closed.");
                    log.Info("Service Bus sender and client closed.");
                }
            }
        }
    }
}
