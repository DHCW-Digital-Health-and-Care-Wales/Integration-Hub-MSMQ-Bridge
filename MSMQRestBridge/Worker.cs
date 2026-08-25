using System;
using System.Globalization;
using System.IO;
using System.Messaging;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using MsmqRestBridge.Configuration;

namespace MsmqRestBridge
{
    /// <summary>
    /// Core MSMQ -> REST endpoint message pump.
    /// Shared by console mode (Program.Main) and Windows Service mode (MsmqBridgeService).
    /// </summary>
    public class Worker
    {
        private static readonly ILog log = LogManager.GetLogger(typeof(Worker));

        // One HttpClient for the lifetime of the process - creating one per request exhausts sockets.
        private static readonly HttpClient httpClient = new HttpClient();

        private readonly AppConfig _config;

        public Worker(AppConfig config)
        {
            _config = config;

            // HttpClient.Timeout may only be changed before the first request is issued.
            httpClient.Timeout = TimeSpan.FromSeconds(_config.RestTimeoutSeconds);
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            using (MessageQueue msmqQueue = new MessageQueue(_config.MsmqConnectionString))
            {
                msmqQueue.MessageReadPropertyFilter.ArrivedTime = true;
                msmqQueue.MessageReadPropertyFilter.Label = true;
                msmqQueue.MessageReadPropertyFilter.Extension = true;

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
                                using (var reader = new StreamReader(msmqMessage.BodyStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true))
                                {
                                    messageBody = reader.ReadToEnd();
                                }

                                byte[] messageBytes = Encoding.UTF8.GetBytes(messageBody);
                                DateTime arrivedTime = msmqMessage.ArrivedTime;

                                string failureReason = await PostToRestEndpointAsync(messageBytes, msmqMessage.Label, arrivedTime, cancellationToken);

                                if (failureReason == null)
                                {
                                    Console.WriteLine("Message sent to REST endpoint.");
                                    log.Info("Message sent to REST endpoint.");
                                }
                                else
                                {
                                    HandleDeliveryFailure(msmqQueue, msmqMessage, messageBytes, failureReason);
                                }
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
                    Console.WriteLine("MSMQ message pump stopped.");
                    log.Info("MSMQ message pump stopped.");
                }
            }
        }

        /// <summary>
        /// POSTs the message body to the configured REST endpoint.
        /// Returns null on success, otherwise a human readable failure reason.
        /// </summary>
        private async Task<string> PostToRestEndpointAsync(byte[] messageBytes, string label, DateTime arrivedTime, CancellationToken cancellationToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, _config.RestEndpointUrl))
                {
                    var content = new ByteArrayContent(messageBytes);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content = content;

                    request.Headers.TryAddWithoutValidation("X-MSMQ-Label", label ?? string.Empty);
                    request.Headers.TryAddWithoutValidation("X-MSMQ-ArrivedTime", arrivedTime.ToString("o", CultureInfo.InvariantCulture));

                    if (!string.IsNullOrEmpty(_config.RestApiKey))
                    {
                        request.Headers.TryAddWithoutValidation("x-api-key", _config.RestApiKey);
                    }

                    using (var response = await httpClient.SendAsync(request, cancellationToken))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            return null;
                        }

                        string responseBody = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync();

                        return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(responseBody, 500)}";
                    }
                }
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return $"Request timed out after {_config.RestTimeoutSeconds}s.";
            }
            catch (HttpRequestException ex)
            {
                return $"Connection error: {ex.GetBaseException().Message}";
            }
        }

        /// <summary>
        /// The message has already been destructively received from MSMQ, so a failed POST must not be
        /// dropped. The attempt count is tracked in the message Extension and the message is re-sent to
        /// the back of the same queue until MaxRetryAttempts is exhausted, after which the body is
        /// written to the dead-letter folder for manual inspection.
        /// </summary>
        private void HandleDeliveryFailure(MessageQueue msmqQueue, Message msmqMessage, byte[] messageBytes, string failureReason)
        {
            int attempts = ReadAttemptCount(msmqMessage.Extension) + 1;

            Console.WriteLine($"Failed to send message to REST endpoint (attempt {attempts}/{_config.MaxRetryAttempts}): {failureReason}");
            log.Warn($"Failed to send message to REST endpoint (attempt {attempts}/{_config.MaxRetryAttempts}): {failureReason}");

            if (attempts >= _config.MaxRetryAttempts)
            {
                WriteDeadLetter(messageBytes, msmqMessage.Label, failureReason, attempts);
                return;
            }

            try
            {
                msmqMessage.BodyStream.Position = 0;
                msmqMessage.Extension = BitConverter.GetBytes(attempts);
                msmqQueue.Send(msmqMessage, msmqMessage.Label ?? string.Empty);

                Console.WriteLine("Message requeued for retry.");
                log.Info("Message requeued for retry.");
            }
            catch (Exception ex)
            {
                // Requeue itself failed - dead-letter rather than lose the message.
                log.Error("Failed to requeue message, writing to dead-letter folder.", ex);
                WriteDeadLetter(messageBytes, msmqMessage.Label, $"{failureReason} | Requeue failed: {ex.Message}", attempts);
            }
        }

        private static int ReadAttemptCount(byte[] extension)
        {
            if (extension == null || extension.Length < sizeof(int))
            {
                return 0;
            }

            int attempts = BitConverter.ToInt32(extension, 0);
            return attempts < 0 ? 0 : attempts;
        }

        private void WriteDeadLetter(byte[] messageBytes, string label, string failureReason, int attempts)
        {
            try
            {
                Directory.CreateDirectory(_config.DeadLetterFolder);

                string fileName = $"deadletter_{DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)}_{Guid.NewGuid():N}.txt";
                string path = Path.Combine(_config.DeadLetterFolder, fileName);

                var contents = new StringBuilder();
                contents.AppendLine($"# TimestampUtc: {DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)}");
                contents.AppendLine($"# Label: {label}");
                contents.AppendLine($"# Attempts: {attempts}");
                contents.AppendLine($"# Reason: {failureReason}");
                contents.AppendLine("# ---- body ----");
                contents.Append(Encoding.UTF8.GetString(messageBytes));

                File.WriteAllText(path, contents.ToString(), Encoding.UTF8);

                Console.WriteLine($"Message dead-lettered to {path}");
                log.Error($"Message dead-lettered to {path}. Reason: {failureReason}");
            }
            catch (Exception ex)
            {
                // Last resort - at least keep the payload in the log.
                log.Error($"Failed to write dead-letter file. Reason: {failureReason}. Body: {Encoding.UTF8.GetString(messageBytes)}", ex);
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }
            return value.Substring(0, maxLength) + "...";
        }
    }
}
