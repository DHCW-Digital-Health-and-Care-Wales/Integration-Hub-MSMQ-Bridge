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
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PumpLoop(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break; // normal shutdown
                }
                catch (Exception ex)
                {
                    log.Fatal($"Message pump crashed, restarting in 10s: {ex.Message}", ex);
                    Console.WriteLine($"Pump crashed, restarting in 10s: {ex.Message}");
                    try { await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken); }
                    catch (OperationCanceledException) { break; }
                }
            }
            log.Info("MSMQ message pump stopped.");
            Console.WriteLine("MSMQ message pump stopped.");
        }

        private async Task PumpLoop(CancellationToken cancellationToken)
        {
            using (MessageQueue msmqQueue = new MessageQueue(_config.MsmqConnectionString))
            {
                msmqQueue.MessageReadPropertyFilter.ArrivedTime = true;
                msmqQueue.MessageReadPropertyFilter.Label = true;
                msmqQueue.MessageReadPropertyFilter.Extension = true;

                Console.WriteLine("Starting to consume messages from MSMQ...");
                log.Info("Starting to consume messages from MSMQ...");

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
byte[] messageBytes;
using (var memory = new MemoryStream())
{
    msmqMessage.BodyStream.CopyTo(memory);
    messageBytes = memory.ToArray();
}
                            DateTime arrivedTime = msmqMessage.ArrivedTime;

                            (string failureReason, bool isPermanent) = await PostToRestEndpointAsync(messageBytes, msmqMessage.Label, arrivedTime, cancellationToken);

                            if (failureReason == null)
                            {
                                Console.WriteLine("Message sent to REST endpoint.");
                                log.Info("Message sent to REST endpoint.");
                            }
                            else
                            {
                                await HandleDeliveryFailure(msmqQueue, msmqMessage, messageBytes, failureReason, isPermanent, cancellationToken);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw; // let RunAsync handle graceful stop
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
        }

        /// <summary>
        /// POSTs the message body to the configured REST endpoint.
        /// Returns (null, false) on success; otherwise (failureReason, isPermanent).
        /// isPermanent is true for 4xx responses that should not be retried.
        /// </summary>
        internal async Task<(string failureReason, bool isPermanent)> PostToRestEndpointAsync(byte[] messageBytes, string label, DateTime arrivedTime, CancellationToken cancellationToken)
        {
            // Per-request timeout via a linked token; avoids mutating the shared static HttpClient.
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.RestTimeoutSeconds)))
            using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token))
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

                    using (var response = await httpClient.SendAsync(request, linkedCts.Token))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            return (null, false);
                        }

                        string responseBody = response.Content == null
                            ? string.Empty
                            : await response.Content.ReadAsStringAsync();

                        int statusCode = (int)response.StatusCode;
                        string reason = $"HTTP {statusCode} {response.ReasonPhrase}: {Truncate(responseBody, 500)}";
                        // 4xx (except 429 Too Many Requests) are permanent - retrying will always fail.
                        bool isPermanent = statusCode >= 400 && statusCode < 500 && statusCode != 429;
                        return (reason, isPermanent);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // propagate external shutdown
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                return ($"Request timed out after {_config.RestTimeoutSeconds}s.", false);
            }
            catch (HttpRequestException ex)
            {
                return ($"Connection error: {ex.GetBaseException().Message}", false);
            }
        }

        /// <summary>
        /// The message has already been destructively received from MSMQ, so a failed POST must not be
        /// dropped. The attempt count is tracked in the message Extension behind a recognizable marker
        /// (so arbitrary application-defined Extension data on the source message is never misread as an
        /// attempt count, and is preserved across retries) and the message is re-sent to the back of the
        /// same queue until MaxRetryAttempts is exhausted, after which the body is written to the
        /// dead-letter folder for manual inspection.
        /// Permanent failures (4xx) are dead-lettered immediately without retrying.
        /// </summary>
        private async Task HandleDeliveryFailure(MessageQueue msmqQueue, Message msmqMessage, byte[] messageBytes, string failureReason, bool isPermanent, CancellationToken cancellationToken)
        {
            byte[] originalExtension = GetOriginalExtension(msmqMessage.Extension);
            int attempts = ReadAttemptCount(msmqMessage.Extension) + 1;

            Console.WriteLine($"Failed to send message to REST endpoint (attempt {attempts}/{_config.MaxRetryAttempts}): {failureReason}");
            log.Warn($"Failed to send message to REST endpoint (attempt {attempts}/{_config.MaxRetryAttempts}): {failureReason}");

            if (isPermanent)
            {
                log.Fatal($"Permanent failure (4xx) - dead-lettering immediately without retry. Label: {msmqMessage.Label}");
                WriteDeadLetter(messageBytes, msmqMessage.Label, failureReason, attempts);
                return;
            }

            if (attempts >= _config.MaxRetryAttempts)
            {
                WriteDeadLetter(messageBytes, msmqMessage.Label, failureReason, attempts);
                return;
            }

            log.Warn($"Delivery failed, pausing {_config.RetryCooldownSeconds}s before continuing...");
            Console.WriteLine($"Delivery failed, pausing {_config.RetryCooldownSeconds}s before continuing...");
            try { await Task.Delay(TimeSpan.FromSeconds(_config.RetryCooldownSeconds), cancellationToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                msmqMessage.BodyStream.Position = 0;
                msmqMessage.Extension = BuildRetryExtension(attempts, originalExtension);
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

        // Marks the Extension bytes we write as our retry metadata, so arbitrary application-defined
        // Extension data on a source message is never misread as an attempt count.
        private static readonly byte[] RetryMarker = { (byte)'M', (byte)'R', (byte)'B', 0x01 };

        internal static int ReadAttemptCount(byte[] extension)
        {
            if (!HasRetryMarker(extension))
            {
                return 0;
            }

            int attempts = BitConverter.ToInt32(extension, RetryMarker.Length);
            return attempts < 0 ? 0 : attempts;
        }

        /// <summary>
        /// Returns the application-defined Extension data that existed before we tagged the message
        /// with retry metadata, so it can be preserved across requeues instead of being overwritten.
        /// </summary>
        internal static byte[] GetOriginalExtension(byte[] extension)
        {
            if (!HasRetryMarker(extension))
            {
                return extension;
            }

            int originalLength = extension.Length - RetryMarker.Length - sizeof(int);
            var original = new byte[originalLength];
            Array.Copy(extension, RetryMarker.Length + sizeof(int), original, 0, originalLength);
            return original;
        }

        /// <summary>
        /// Builds the Extension bytes to write when requeuing: our marker, the new attempt count, then
        /// the preserved original Extension data (if any).
        /// </summary>
        internal static byte[] BuildRetryExtension(int attempts, byte[] originalExtension)
        {
            int originalLength = originalExtension?.Length ?? 0;
            var result = new byte[RetryMarker.Length + sizeof(int) + originalLength];
            Array.Copy(RetryMarker, 0, result, 0, RetryMarker.Length);
            Array.Copy(BitConverter.GetBytes(attempts), 0, result, RetryMarker.Length, sizeof(int));
            if (originalLength > 0)
            {
                Array.Copy(originalExtension, 0, result, RetryMarker.Length + sizeof(int), originalLength);
            }
            return result;
        }

        private static bool HasRetryMarker(byte[] extension)
        {
            if (extension == null || extension.Length < RetryMarker.Length + sizeof(int))
            {
                return false;
            }

            for (int i = 0; i < RetryMarker.Length; i++)
            {
                if (extension[i] != RetryMarker[i])
                {
                    return false;
                }
            }

            return true;
        }

        internal void WriteDeadLetter(byte[] messageBytes, string label, string failureReason, int attempts)
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

        internal static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }
            return value.Substring(0, maxLength) + "...";
        }
    }
}
