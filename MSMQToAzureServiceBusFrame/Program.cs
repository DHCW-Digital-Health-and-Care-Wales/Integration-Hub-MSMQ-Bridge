using System;
using System.Messaging;
using Azure.Messaging.ServiceBus;
using System.Threading.Tasks;

namespace MSMQToAzureServiceBusFrame
{
    class Program
    {
        // Define MSMQ Queue and Service Bus connection strings
        static string msmqQueuePath = @".\private$\WASPQueue1";  // Adjust MSMQ queue name
        static string serviceBusConnectionString = "<Your_Azure_Service_Bus_Connection_String>";
        static string serviceBusQueueName = "your-servicebus-queue-name";

        static async Task Main(string[] args)
        {
            // Initialize the MSMQ queue
            using (MessageQueue msmqQueue = new MessageQueue(msmqQueuePath))
            {
                msmqQueue.Formatter = new XmlMessageFormatter(new String[] { "System.String,mscorlib" });

                //// Initialize Service Bus client
                //var client = new ServiceBusClient(serviceBusConnectionString);
                //var sender = client.CreateSender(serviceBusQueueName);

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
                            Console.WriteLine($"Received message from MSMQ: {messageBody}");

                            // Create a Service Bus message
                            var serviceBusMessage = new ServiceBusMessage(messageBody);

                            // Send the message to Azure Service Bus
                            //await sender.SendMessageAsync(serviceBusMessage);
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
