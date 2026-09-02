using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MsmqRestBridge.Configuration;
using Xunit;

namespace MsmqRestBridge.Tests
{
    public class WorkerIntegrationTests : IDisposable
    {
        private readonly FakeRestEndpoint _endpoint;
        private readonly string _deadLetterDir;

        public WorkerIntegrationTests()
        {
            _endpoint = new FakeRestEndpoint();
            _deadLetterDir = Path.Combine(Path.GetTempPath(), "msmqbridge-tests-" + Guid.NewGuid().ToString("N"));
        }

        private AppConfig MakeConfig(string endpointUrl = null, int timeoutSeconds = 5) =>
            new AppConfig(
                msmqConnectionString: ".\\private$__DOES_NOT_EXIST__",
                restEndpointUrl: endpointUrl ?? _endpoint.Url,
                restApiKey: "test-key-123",
                restTimeoutSeconds: timeoutSeconds.ToString(),
                maxRetryAttempts: "3",
                retryCooldownSeconds: "1",
                deadLetterFolder: _deadLetterDir);

        // ------------------------------------------------------------------ POST success

        [Fact]
        public async Task PostToRest_Success_ReturnsNull()
        {
            var worker = new Worker(MakeConfig());
            byte[] body = Encoding.UTF8.GetBytes("MSH|^~\\&|TEST");

            var (failureReason, isPermanent) = await worker.PostToRestEndpointAsync(
                body, "test-label",
                new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                CancellationToken.None);

            Assert.Null(failureReason);
            Assert.False(isPermanent);
        }

        [Fact]
        public async Task PostToRest_Success_SendsHeadersAndBody()
        {
            var worker = new Worker(MakeConfig());
            byte[] body = Encoding.UTF8.GetBytes("MSH|^~\\&|TEST");

            await worker.PostToRestEndpointAsync(
                body, "test-label",
                new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc),
                CancellationToken.None);

            var req = Assert.Single(_endpoint.Requests);
            Assert.Equal("test-label", req.LabelHeader);
            Assert.Equal("2024-01-15T10:30:00.0000000Z", req.ArrivedTimeHeader);
            Assert.Equal("test-key-123", req.ApiKeyHeader);
            Assert.Equal(body, req.Body);
        }

        // ------------------------------------------------------------------ POST failures

        [Fact]
        public async Task PostToRest_ServerError500_ReturnsFailureReason_NotPermanent()
        {
            using var failing = new FakeRestEndpoint(() => 500);
            var worker = new Worker(MakeConfig(endpointUrl: failing.Url));

            var (failureReason, isPermanent) = await worker.PostToRestEndpointAsync(
                Array.Empty<byte>(), null, DateTime.UtcNow, CancellationToken.None);

            Assert.NotNull(failureReason);
            Assert.Contains("500", failureReason);
            Assert.False(isPermanent); // 5xx is transient
        }

        [Fact]
        public async Task PostToRest_ClientError400_ReturnsFailureReason_IsPermanent()
        {
            using var badRequest = new FakeRestEndpoint(() => 400);
            var worker = new Worker(MakeConfig(endpointUrl: badRequest.Url));

            var (failureReason, isPermanent) = await worker.PostToRestEndpointAsync(
                Array.Empty<byte>(), null, DateTime.UtcNow, CancellationToken.None);

            Assert.NotNull(failureReason);
            Assert.Contains("400", failureReason);
            Assert.True(isPermanent); // 4xx (non-429) is permanent
        }

        [Fact]
        public async Task PostToRest_TooManyRequests429_IsNotPermanent()
        {
            using var rateLimit = new FakeRestEndpoint(() => 429);
            var worker = new Worker(MakeConfig(endpointUrl: rateLimit.Url));

            var (failureReason, isPermanent) = await worker.PostToRestEndpointAsync(
                Array.Empty<byte>(), null, DateTime.UtcNow, CancellationToken.None);

            Assert.NotNull(failureReason);
            Assert.False(isPermanent); // 429 is transient - should be retried
        }

        [Fact]
        public async Task PostToRest_UnreachableServer_ReturnsConnectionError()
        {
            var worker = new Worker(MakeConfig(endpointUrl: "http://127.0.0.1:1/api/messages/"));

            var (failureReason, isPermanent) = await worker.PostToRestEndpointAsync(
                Array.Empty<byte>(), null, DateTime.UtcNow, CancellationToken.None);

            Assert.NotNull(failureReason);
            Assert.Contains("Connection error", failureReason);
        }

        [Fact]
        public async Task PostToRest_Timeout_ReturnsTimeoutMessage()
        {
            // Server receives the request but waits 3s before responding; client timeout is 1s
            using var slow = new FakeRestEndpoint(responseDelayMs: 3000);
            var worker = new Worker(MakeConfig(endpointUrl: slow.Url, timeoutSeconds: 1));

            var (failureReason, isPermanent) = await worker.PostToRestEndpointAsync(
                Array.Empty<byte>(), null, DateTime.UtcNow, CancellationToken.None);

            Assert.NotNull(failureReason);
            Assert.Contains("timed out", failureReason);
        }

        [Fact]
        public async Task PostToRest_CancellationRequested_PropagatesOperationCanceled()
        {
            // Use the slow endpoint so the request is actually in-flight when we cancel
            using var slow = new FakeRestEndpoint(responseDelayMs: 5000);
            var worker = new Worker(MakeConfig(endpointUrl: slow.Url, timeoutSeconds: 10));

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                worker.PostToRestEndpointAsync(Array.Empty<byte>(), null, DateTime.UtcNow, cts.Token));
        }

        // ------------------------------------------------------------------ Dead-letter

        [Fact]
        public void WriteDeadLetter_CreatesMetadataFileAndBinaryPayload()
        {
            var worker = new Worker(MakeConfig());
            byte[] body = new byte[] { 0x00, 0xFF, 0x10, 0x7F, 0x41, 0x42 };

            worker.WriteDeadLetter(body, "bad-label", "HTTP 400 Bad Request", 3);

            var files = Directory.GetFiles(_deadLetterDir);
            Assert.Equal(2, files.Length);

            string metadataPath = Array.Find(files, path => path.EndsWith(".txt"));
            Assert.NotNull(metadataPath);
            string metadata = File.ReadAllText(metadataPath, Encoding.UTF8);
            Assert.Contains("HTTP 400 Bad Request", metadata);
            Assert.Contains("Attempts: 3", metadata);
            Assert.Contains("bad-label", metadata);
            Assert.Contains("BodyFile:", metadata);
            Assert.Contains("sibling .bin file", metadata);

            string payloadPath = metadataPath.Substring(0, metadataPath.Length - 4) + ".bin";
            Assert.True(File.Exists(payloadPath));
            Assert.Equal(body, File.ReadAllBytes(payloadPath));
        }

        [Fact]
        public void WriteDeadLetter_CreatesDeadLetterDir_IfMissing()
        {
            string missingDir = Path.Combine(Path.GetTempPath(), "msmq-dl-" + Guid.NewGuid().ToString("N"));
            var config = new AppConfig(".\\private$\\q", "http://x", null,
                deadLetterFolder: missingDir);
            var worker = new Worker(config);

            worker.WriteDeadLetter(new byte[0], "label", "reason", 1);

            Assert.True(Directory.Exists(missingDir));
            Directory.Delete(missingDir, recursive: true);
        }

        [Fact]
        public void WriteDeadLetter_WithEfsEnabled_StillWritesFiles()
        {
            var config = new AppConfig(
                msmqConnectionString: ".\\private$\\q",
                restEndpointUrl: "http://x",
                restApiKey: null,
                deadLetterFolder: _deadLetterDir,
                deadLetterEncryptWithEfs: "true",
                deadLetterRequireEfsSuccess: "false");

            var worker = new Worker(config);
            worker.WriteDeadLetter(new byte[] { 1, 2, 3 }, "label", "reason", 1);

            var files = Directory.GetFiles(_deadLetterDir);
            Assert.Equal(2, files.Length);
        }

        [Fact]
        public void WriteDeadLetter_Throws_WhenDeadLetterDirectoryIsUnavailable()
        {
            string blockedDir = Path.Combine(Path.GetTempPath(), "msmq-dl-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(blockedDir, "not a directory");
            try
            {
                var config = new AppConfig(".\\private$\\q", "http://x", null,
                    deadLetterFolder: blockedDir);
                var worker = new Worker(config);

                var ex = Assert.Throws<IOException>(() =>
                    worker.WriteDeadLetter(new byte[] { 0x01, 0x02, 0x03 }, "label", "reason", 1));

                Assert.Contains("Failed to write dead-letter file", ex.Message);
            }
            finally
            {
                if (File.Exists(blockedDir)) File.Delete(blockedDir);
            }
        }

        // ------------------------------------------------------------------ Cleanup

        public void Dispose()
        {
            _endpoint.Dispose();
            try { if (Directory.Exists(_deadLetterDir)) Directory.Delete(_deadLetterDir, recursive: true); }
            catch { /* best-effort */ }
        }
    }
}
