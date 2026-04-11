using System;

namespace PhotoBooth.Booth.Sync
{
    public static class BoothBackendPathResolver
    {
        public static string Resolve(string baseUrl, string pathTemplate, string jobId)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new InvalidOperationException("Backend base URL is required.");
            }

            var normalizedBaseUrl = baseUrl.Trim().TrimEnd('/');
            var normalizedTemplate = string.IsNullOrWhiteSpace(pathTemplate) ? string.Empty : pathTemplate.Trim();
            if (!normalizedTemplate.StartsWith("/", StringComparison.Ordinal))
            {
                normalizedTemplate = "/" + normalizedTemplate;
            }

            normalizedTemplate = normalizedTemplate.Replace("{jobId}", Uri.EscapeDataString(jobId ?? string.Empty));
            return normalizedBaseUrl + normalizedTemplate;
        }
    }
}
