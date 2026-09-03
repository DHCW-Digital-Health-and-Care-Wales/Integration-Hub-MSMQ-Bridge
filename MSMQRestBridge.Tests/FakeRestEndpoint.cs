using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace MsmqRestBridge.Tests
{
    /// <summary>
    /// Minimal local HTTP server that records requests and returns scripted status codes.
    /// Used as a stand-in for the real REST ingestion service.
    /// </summary>
    public sealed class FakeRestEndpoint : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly ConcurrentQueue<ReceivedRequest> _requests = new ConcurrentQueue<ReceivedRequest>();
        private readonly Func<int> _statusCodeSelector;
        private bool _disposed;

        public string Url { get; }

        public class ReceivedRequest
        {
            public string LabelHeader { get; }
            public string ArrivedTimeHeader { get; }
            public string ApiKeyHeader { get; }
            public byte[] Body { get; }

            public ReceivedRequest(string labelHeader, string arrivedTimeHeader, string apiKeyHeader, byte[] body)
            {
                LabelHeader = labelHeader;
                ArrivedTimeHeader = arrivedTimeHeader;
                ApiKeyHeader = apiKeyHeader;
                Body = body;
            }
        }

        /// <param name="statusCodeSelector">Called per request; return e.g. 200, 500. Default: always 200.</param>
        /// <param name="responseDelayMs">Optional delay before sending the response, to simulate slow servers.</param>
        public FakeRestEndpoint(Func<int> statusCodeSelector = null, int responseDelayMs = 0)
        {
            _statusCodeSelector = statusCodeSelector ?? (() => 200);
            Url = $"http://127.0.0.1:{GetFreePort()}/api/messages/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(() => AcceptLoopAsync(responseDelayMs));
        }

        public ReceivedRequest[] Requests => _requests.ToArray();

        private async Task AcceptLoopAsync(int responseDelayMs)
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (HttpListenerException) { return; } // disposed

                // Read body — use a read loop compatible with .NET Framework
                byte[] body = Array.Empty<byte>();
                long contentLength = ctx.Request.ContentLength64;
                if (contentLength > 0)
                {
body = new byte[checked((int)contentLength)];
                    int offset = 0;
                    while (offset < body.Length)
                    {
                        int read = await ctx.Request.InputStream.ReadAsync(body, offset, body.Length - offset);
                        if (read == 0) break;
                        offset += read;
                    }
                }

                _requests.Enqueue(new ReceivedRequest(
                    ctx.Request.Headers["X-MSMQ-Label"],
                    ctx.Request.Headers["X-MSMQ-ArrivedTime"],
                    ctx.Request.Headers["x-api-key"],
                    body));

                if (responseDelayMs > 0)
                    await Task.Delay(responseDelayMs);

                int status = _statusCodeSelector();
                ctx.Response.StatusCode = status;
                ctx.Response.Close();
            }
        }

        private static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                try { _listener.Stop(); } catch { /* intentionally swallowed */ }
            }
        }
    }
}
