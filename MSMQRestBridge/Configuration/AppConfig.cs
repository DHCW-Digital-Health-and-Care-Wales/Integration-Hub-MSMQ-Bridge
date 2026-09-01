using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;


namespace MsmqRestBridge.Configuration
{
    public class AppConfig
    {
        public const int DefaultRestTimeoutSeconds = 30;
        public const int DefaultMaxRetryAttempts = 5;
        public const int DefaultRetryCooldownSeconds = 15;

        public string MsmqConnectionString { get; set; }
        public string RestEndpointUrl { get; set; }
        public string RestApiKey { get; set; }
        public int RestTimeoutSeconds { get; set; }
        public int MaxRetryAttempts { get; set; }
        public int RetryCooldownSeconds { get; set; }
        public string DeadLetterFolder { get; set; }

        private static readonly List<string> RequiredArgs = new List<string> { "MSMQ_CONNECTION_STRING", "REST_ENDPOINT_URL" };
        private static readonly List<string> OptionalArgs = new List<string> { "REST_API_KEY", "REST_TIMEOUT_SECONDS", "MAX_RETRY_ATTEMPTS", "RETRY_COOLDOWN_SECONDS", "DEAD_LETTER_FOLDER" };

        private static readonly Regex IpAddressPattern = new Regex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

        public AppConfig(string msmqConnectionString, string restEndpointUrl, string restApiKey,
            string restTimeoutSeconds = null, string maxRetryAttempts = null, string retryCooldownSeconds = null, string deadLetterFolder = null)
        {
            MsmqConnectionString = NormalizeMsmqPath(msmqConnectionString);
            RestEndpointUrl = restEndpointUrl;
            RestApiKey = restApiKey;
            RestTimeoutSeconds = ParsePositiveInt(restTimeoutSeconds, DefaultRestTimeoutSeconds);
            MaxRetryAttempts = ParsePositiveInt(maxRetryAttempts, DefaultMaxRetryAttempts);
            RetryCooldownSeconds = ParsePositiveInt(retryCooldownSeconds, DefaultRetryCooldownSeconds);
            DeadLetterFolder = string.IsNullOrWhiteSpace(deadLetterFolder)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dead-letter")
                : deadLetterFolder;
        }

        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(MsmqConnectionString))
                throw new InvalidOperationException("MSMQ_CONNECTION_STRING is required.");
if (!Uri.TryCreate(RestEndpointUrl, UriKind.Absolute, out var endpoint) ||
    (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("REST_ENDPOINT_URL must be an absolute HTTP(S) URL.");
        }

        private static int ParsePositiveInt(string value, int defaultValue)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) &&
                parsed > 0)
            {
                return parsed;
            }
            return defaultValue;
        }

        // Path syntax (machine\private$\queue) only works for local queues.
        // Remote private queues require FormatName direct syntax, e.g.
        // FormatName:DIRECT=TCP:10.57.106.225\private$\queue (IP address)
        // FormatName:DIRECT=OS:MYSERVER\private$\queue (host name)
        private static string NormalizeMsmqPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                path.StartsWith("FormatName:", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(".\\", StringComparison.Ordinal))
            {
                return path;
            }

            var separatorIndex = path.IndexOf('\\');
            if (separatorIndex <= 0)
            {
                return path;
            }

            var machine = path.Substring(0, separatorIndex);
            var protocol = IpAddressPattern.IsMatch(machine) ? "TCP" : "OS";
            return $"FormatName:DIRECT={protocol}:{path}";
        }
            
        public static AppConfig ReadEnvConfig()
        {
            return new AppConfig(
                ReadEnv("MSMQ_CONNECTION_STRING", true),
                ReadEnv("REST_ENDPOINT_URL", true),
                ReadEnv("REST_API_KEY", false),
                ReadEnv("REST_TIMEOUT_SECONDS", false),
                ReadEnv("MAX_RETRY_ATTEMPTS", false),
                ReadEnv("RETRY_COOLDOWN_SECONDS", false),
                ReadEnv("DEAD_LETTER_FOLDER", false)
            );
        }

        private  static string ReadEnv(string name, bool required)
        {
            string value = Environment.GetEnvironmentVariable(name);
            if (required && string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException($"Missing required configuration: {name}");
            }
            return value;
        }

        public static AppConfig ReadCommandLineArg(string[] args)
        {
            var argsvalue = args.ToList();
            var argMap = new Dictionary<string, string>();

            foreach (var name in RequiredArgs.Concat(OptionalArgs))
            {
                var index = argsvalue.IndexOf("--" + name);
                if (index >= 0 && argsvalue.Count > index + 1)
                {
                    argMap[name] = argsvalue[index + 1];
                }
                else if (RequiredArgs.Contains(name))
                {
                    // Fall back to the environment before giving up on a required setting.
                    var fromEnv = Environment.GetEnvironmentVariable(name);
                    if (string.IsNullOrEmpty(fromEnv))
                    {
                        throw new InvalidOperationException($"Missing required configuration: {name}");
                    }
                    argMap[name] = fromEnv;
                }
                else
                {
                    argMap[name] = Environment.GetEnvironmentVariable(name);
                }
            }

            return new AppConfig(
                argMap["MSMQ_CONNECTION_STRING"],
                argMap["REST_ENDPOINT_URL"],
                argMap["REST_API_KEY"],
                argMap["REST_TIMEOUT_SECONDS"],
                argMap["MAX_RETRY_ATTEMPTS"],
                argMap["RETRY_COOLDOWN_SECONDS"],
                argMap["DEAD_LETTER_FOLDER"]);

        }

    }
}
