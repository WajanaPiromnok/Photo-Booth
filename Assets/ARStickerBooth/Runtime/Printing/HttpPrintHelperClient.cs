using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PhotoBooth.Booth.Printing
{
    public sealed class HttpPrintHelperClient : IPrintHelperClient
    {
        private readonly string baseUrl;
        private readonly int timeoutSeconds;

        public HttpPrintHelperClient(string baseUrl, int timeoutSeconds = 10)
        {
            this.baseUrl = NormalizeBaseUrl(baseUrl);
            this.timeoutSeconds = Math.Max(1, timeoutSeconds);
        }

        public async Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var payload = JsonUtility.ToJson(new PrintBridgeRequest
            {
                job_id = request.JobId,
                image_path = request.ImagePath,
                thumbnail_path = request.ThumbnailPath,
                printer_name = null,
                copies = Math.Max(1, request.Copies)
            });

            using var webRequest = new UnityWebRequest($"{baseUrl}/api/print/jobs", UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = timeoutSeconds
            };
            webRequest.SetRequestHeader("Content-Type", "application/json");
            webRequest.SetRequestHeader("Accept", "application/json");

            var operation = webRequest.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            if (webRequest.result == UnityWebRequest.Result.ConnectionError
                || webRequest.result == UnityWebRequest.Result.DataProcessingError)
            {
                return new PrintJobResult
                {
                    Success = false,
                    Retryable = true,
                    Message = webRequest.error ?? "Print bridge is unavailable.",
                    PrinterName = request.PrinterName
                };
            }

            var responseText = webRequest.downloadHandler?.text;
            var response = ParseResponse(responseText);
            if (response != null)
            {
                return new PrintJobResult
                {
                    Success = response.success,
                    Retryable = response.retryable,
                    Message = response.message,
                    PrinterName = response.printer_name,
                    OperationId = response.operation_id
                };
            }

            return new PrintJobResult
            {
                Success = false,
                Retryable = webRequest.responseCode >= 500 || webRequest.responseCode == 0,
                Message = string.IsNullOrWhiteSpace(webRequest.error)
                    ? $"Print bridge returned HTTP {webRequest.responseCode}."
                    : webRequest.error,
                PrinterName = request.PrinterName
            };
        }

        private static PrintBridgeResponse ParseResponse(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<PrintBridgeResponse>(responseText);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Print bridge response could not be parsed: {exception.Message}");
                return null;
            }
        }

        private static string NormalizeBaseUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? "http://127.0.0.1:18080"
                : value.Trim().TrimEnd('/');
        }

        [Serializable]
        private sealed class PrintBridgeRequest
        {
            public string job_id;
            public string image_path;
            public string thumbnail_path;
            public string printer_name;
            public int copies;
        }

        [Serializable]
        private sealed class PrintBridgeResponse
        {
            public bool success;
            public bool retryable;
            public string message;
            public string printer_name;
            public string operation_id;
        }
    }
}
