using System;
using MsmqRestBridge.Configuration;
using Xunit;

namespace MsmqRestBridge.Tests
{
    public class ConfigTests
    {
        [Fact]
        public void ReadCommandLineArg_ParsesAllArguments()
        {
            var args = new[]
            {
                "--MSMQ_CONNECTION_STRING", ".\\private$\\TEST",
                "--REST_ENDPOINT_URL", "http://localhost:8000/api/messages",
                "--REST_API_KEY", "secret-key"
            };

            var config = AppConfig.ReadCommandLineArg(args);

            Assert.Equal(".\\private$\\TEST", config.MsmqConnectionString);
            Assert.Equal("http://localhost:8000/api/messages", config.RestEndpointUrl);
            Assert.Equal("secret-key", config.RestApiKey);
        }

        [Fact]
        public void ReadCommandLineArg_OptionalArgsMissingUsesDefaults()
        {
            var args = new[]
            {
                "--MSMQ_CONNECTION_STRING", ".\\private$\\TEST",
                "--REST_ENDPOINT_URL", "http://localhost/api"
            };

            var config = AppConfig.ReadCommandLineArg(args);

            Assert.Equal(AppConfig.DefaultRestTimeoutSeconds, config.RestTimeoutSeconds);
            Assert.Equal(AppConfig.DefaultMaxRetryAttempts, config.MaxRetryAttempts);
            Assert.Equal(AppConfig.DefaultRetryCooldownSeconds, config.RetryCooldownSeconds);
            Assert.Equal(AppConfig.DefaultDeadLetterRetentionDays, config.DeadLetterRetentionDays);
            Assert.Equal(AppConfig.DefaultDeadLetterEncryptWithEfs, config.DeadLetterEncryptWithEfs);
            Assert.Equal(AppConfig.DefaultDeadLetterRequireEfsSuccess, config.DeadLetterRequireEfsSuccess);
            Assert.False(string.IsNullOrWhiteSpace(config.DeadLetterFolder));
        }

        [Fact]
        public void ReadCommandLineArg_CustomNumericArgs_AreParsed()
        {
            var args = new[]
            {
                "--MSMQ_CONNECTION_STRING", ".\\private$\\TEST",
                "--REST_ENDPOINT_URL", "http://localhost/api",
                "--REST_TIMEOUT_SECONDS", "60",
                "--MAX_RETRY_ATTEMPTS", "10",
                "--RETRY_COOLDOWN_SECONDS", "20"
            };

            var config = AppConfig.ReadCommandLineArg(args);

            Assert.Equal(60, config.RestTimeoutSeconds);
            Assert.Equal(10, config.MaxRetryAttempts);
            Assert.Equal(20, config.RetryCooldownSeconds);
        }

        [Fact]
        public void ReadCommandLineArg_MissingRequired_Throws()
        {
            Assert.Throws<InvalidOperationException>(() =>
                AppConfig.ReadCommandLineArg(Array.Empty<string>()));
        }

        [Fact]
        public void Validate_MissingMsmqConnectionString_Throws()
        {
            var config = new AppConfig(string.Empty, "http://localhost/api", null);
            Assert.Throws<InvalidOperationException>(() => config.Validate());
        }

        [Fact]
        public void Validate_MissingRestEndpointUrl_Throws()
        {
            var config = new AppConfig(".\\private$\\TEST", string.Empty, null);
            Assert.Throws<InvalidOperationException>(() => config.Validate());
        }

        [Fact]
        public void Validate_ValidConfig_DoesNotThrow()
        {
            var config = new AppConfig(".\\private$\\TEST", "http://localhost/api", null);
            config.Validate(); // should not throw
        }

        [Fact]
        public void Constructor_InvalidRestTimeoutSeconds_FallsBackToDefault()
        {
            var config = new AppConfig(".\\private$\\q", "http://x", null, restTimeoutSeconds: "not-a-number");
            Assert.Equal(AppConfig.DefaultRestTimeoutSeconds, config.RestTimeoutSeconds);
        }

        [Fact]
        public void Constructor_ZeroRestTimeoutSeconds_FallsBackToDefault()
        {
            var config = new AppConfig(".\\private$\\q", "http://x", null, restTimeoutSeconds: "0");
            Assert.Equal(AppConfig.DefaultRestTimeoutSeconds, config.RestTimeoutSeconds);
        }

        [Fact]
        public void NormalizeMsmqPath_NumericLikeHostOutsideIpv4Range_UsesOSProtocol()
        {
            var config = new AppConfig("999.999.999.999\\private$\\q", "http://x", null);
            Assert.StartsWith("FormatName:DIRECT=OS:", config.MsmqConnectionString);
        }

        [Fact]
        public void ReadCommandLineArg_EfsFlags_AreParsed()
        {
            var args = new[]
            {
                "--MSMQ_CONNECTION_STRING", ".\\private$\\TEST",
                "--REST_ENDPOINT_URL", "http://localhost/api",
                "--DEAD_LETTER_ENCRYPT_WITH_EFS", "true",
                "--DEAD_LETTER_REQUIRE_EFS_SUCCESS", "1"
            };

            var config = AppConfig.ReadCommandLineArg(args);

            Assert.True(config.DeadLetterEncryptWithEfs);
            Assert.True(config.DeadLetterRequireEfsSuccess);
        }
    }
}
