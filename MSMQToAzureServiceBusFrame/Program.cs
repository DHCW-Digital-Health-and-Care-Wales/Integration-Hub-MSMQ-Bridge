using System;
using System.Messaging;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using System.Threading.Tasks;
using MSMQToAzureServiceBusFrame.Configuration;
using log4net;
using Microsoft.Azure.Amqp.Encoding;


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
                msmqQueue.MessageReadPropertyFilter.ArrivedTime = true;

                // Use Entra ID (DefaultAzureCredential) since SAS auth is disabled on the namespace
                // Extract hostname from connection string e.g. "Endpoint=sb://mynamespace.servicebus.windows.net/;..."
                var endpoint = config.ServiceBusConnectionString;
                var host = endpoint.Split(';')[0].Replace("Endpoint=sb://", "").TrimEnd('/');
                var clientOptions = new ServiceBusClientOptions
                {
                    TransportType = ServiceBusTransportType.AmqpWebSockets
                };
                var client = new ServiceBusClient(host, new DefaultAzureCredential(), clientOptions);
                var serviceBusSender = client.CreateSender(config.ServiceBusTopicName);

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
                            // Receive message from MSMQ with timeout so shutdown can be detected
                            Message msmqMessage = null;
                            try
                            {
                                msmqMessage = msmqQueue.Receive(TimeSpan.FromSeconds(2));
                            }
                            catch (MessageQueueException mqEx) when (mqEx.MessageQueueErrorCode == MessageQueueErrorCode.IOTimeout)
                            {
                                // No message within timeout - loop back and check shutdown flag
                                continue;
                            }

                            if (msmqMessage != null)
                            {
                                // Read raw body stream to handle any message format (XML, HL7, plain text)
                                msmqMessage.BodyStream.Position = 0;
                                string messageBody;
                                using (var reader = new System.IO.StreamReader(msmqMessage.BodyStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                                {
                                    messageBody = reader.ReadToEnd();
                                }

                                // Convert the message content into a byte array encoding
                                byte[] messageBytes = AppEncoding.DetectEncoding(messageBody);

                                // Create ReadOnlyMemory<byte> from the byte array
                                ReadOnlyMemory<byte> messageMemory = new ReadOnlyMemory<byte>(messageBytes);


                                DateTime arrivedTime = msmqMessage.ArrivedTime;

                                // Create a Service Bus message
                                var serviceBusMessage = new ServiceBusMessage(messageMemory);

                                // Queue has sessions enabled - use MSMQ label as SessionId, fallback to "default"
                                serviceBusMessage.SessionId = !string.IsNullOrEmpty(msmqMessage.Label) ? msmqMessage.Label : "default";
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
