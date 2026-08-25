using System;
using System.Messaging;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using log4net;
using MSMQToAzureServiceBusFrame.Configuration;

namespace MSMQToAzureServiceBusFrame
{
    /// <summary>
    /// Core MSMQ -> Azure Service Bus message pump.
    /// Shared by console mode (Program.Main) and Windows Service mode (MsmqBridgeService).
    /// </summary>
    public class Worker
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Worker));
        private readonly AppConfig _config;

        public Worker(AppConfig config)
        {
            _config = config;
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (MessageQueue msmqQueue = new MessageQueue(_config.MsmqConnectionString))
            {
                msmqQueue.MessageReadPropertyFilter.ArrivedTime = true;

                // Use Entra ID (DefaultAzureCredential) since SAS auth is disabled on the namespace
                // Extract hostname from connection string e.g. "Endpoint=sb://mynamespace.servicebus.windows.net/;..."
                var endpoint = _config.ServiceBusConnectionString;
                var host = endpoint.Split(';')[0].Replace("Endpoint=sb://", "").TrimEnd('/');
                var clientOptions = new ServiceBusClientOptions
                {
                    TransportType = ServiceBusTransportType.AmqpWebSockets
                };
                var client = new ServiceBusClient(host, new DefaultAzureCredential(), clientOptions);
                var serviceBusSender = client.CreateSender(_config.ServiceBusTopicName);

                Console.WriteLine("Starting to consume messages from MSMQ...");
                log.Info("Starting to consume messages from MSMQ...");

                try
                {
                    while (!cancellationToken.IsCancellationRequested)
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
                                // No message within timeout - loop back and check cancellation
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
                            // Log and keep running - a transient error (e.g. network blip) should not
                            // permanently stop an unattended service. Cancellation is the only stop signal.
                            Console.WriteLine($"Error: {ex.Message}");
                            log.Error($"Error: {ex.Message}", ex);
                        }
                    }
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
