using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothFrontendAnalyticsClient
    {
        private readonly string baseUrl;
        private readonly string deviceId;
        private readonly string deviceToken;
        private readonly int timeoutSeconds;

        public BoothFrontendAnalyticsClient(string baseUrl, string deviceId, string deviceToken, int timeoutSeconds = 10)
        {
            this.baseUrl = NormalizeBaseUrl(baseUrl);
            this.deviceId = string.IsNullOrWhiteSpace(deviceId) ? "booth-local" : deviceId.Trim();
            this.deviceToken = deviceToken ?? string.Empty;
            this.timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public async Task TrackAsync(
            string eventName,
            string jobId = null,
            string themeId = null,
            BoothUiScreenId? screenId = null,
            int? durationSeconds = null,
            IReadOnlyDictionary<string, string> metadata = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(eventName))
            {
                return;
            }

            var payload = BuildPayload(eventName.Trim(), deviceId, jobId, themeId, screenId?.ToString(), durationSeconds, metadata);

            var request = new UnityWebRequest($"{baseUrl}/v1/analytics/events", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds
            };

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-Device-Id", deviceId);
            if (!string.IsNullOrWhiteSpace(deviceToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {deviceToken.Trim()}");
            }

            try
            {
                await SendAsync(request, cancellationToken);
                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"Backend analytics event failed: {request.responseCode} {request.error}");
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        private static string BuildPayload(
            string eventName,
            string deviceId,
            string jobId,
            string themeId,
            string screenId,
            int? durationSeconds,
            IReadOnlyDictionary<string, string> metadata)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            AppendString(builder, "event_name", eventName, false);
            AppendString(builder, "device_id", deviceId, true);
            AppendString(builder, "job_id", jobId, true);
            AppendString(builder, "theme_id", themeId, true);
            AppendString(builder, "screen_id", screenId, true);

            if (durationSeconds.HasValue)
            {
                builder.Append(",\"duration_seconds\":").Append(Math.Max(0, durationSeconds.Value));
            }

            builder.Append(",\"metadata\":{");
            if (metadata != null)
            {
                var firstMetadata = true;
                foreach (var pair in metadata)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                    {
                        continue;
                    }

                    if (!firstMetadata)
                    {
                        builder.Append(',');
                    }

                    builder.Append('"').Append(Escape(pair.Key)).Append("\":");
                    builder.Append('"').Append(Escape(pair.Value)).Append('"');
                    firstMetadata = false;
                }
            }

            builder.Append("}}");
            return builder.ToString();
        }

        private static void AppendString(StringBuilder builder, string name, string value, bool prefixComma)
        {
            if (prefixComma)
            {
                builder.Append(',');
            }

            builder.Append('"').Append(name).Append("\":");
            if (string.IsNullOrWhiteSpace(value))
            {
                builder.Append("null");
                return;
            }

            builder.Append('"').Append(Escape(value)).Append('"');
        }

        private static async Task SendAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(request.Abort);
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        private static string NormalizeBaseUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value.Trim().TrimEnd('/');
        }
    }
}
