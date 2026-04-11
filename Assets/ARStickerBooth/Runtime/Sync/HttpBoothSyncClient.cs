using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace PhotoBooth.Booth.Sync
{
    public sealed class HttpBoothSyncClient : IBoothSyncClient
    {
        private readonly BoothBackendScaffoldConfig config;

        public HttpBoothSyncClient(BoothBackendScaffoldConfig config)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public async Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ComposedImagePath) || !File.Exists(request.ComposedImagePath))
            {
                return Failure("Composed image file is missing.", false);
            }

            if (string.IsNullOrWhiteSpace(config.BoothApiBaseUrl))
            {
                return Failure("Booth API base URL is not configured.", false);
            }

            if (string.IsNullOrWhiteSpace(config.PublishApiBaseUrl))
            {
                config.PublishApiBaseUrl = config.BoothApiBaseUrl;
            }

            var uploadResponse = await UploadAssetsAsync(request, cancellationToken);
            if (!uploadResponse.Success)
            {
                return uploadResponse;
            }

            if (config.SeparateAssetRegistration)
            {
                var registrationResponse = await RegisterAssetsAsync(request, uploadResponse.RemoteAssetKey, cancellationToken);
                if (!registrationResponse.Success)
                {
                    return registrationResponse;
                }

                if (string.IsNullOrWhiteSpace(uploadResponse.RemoteAssetKey))
                {
                    uploadResponse.RemoteAssetKey = registrationResponse.RemoteAssetKey;
                }
            }

            var publishResponse = await PublishAsync(request, cancellationToken);
            if (!publishResponse.Success)
            {
                return publishResponse;
            }

            if (string.IsNullOrWhiteSpace(publishResponse.RemoteAssetKey))
            {
                publishResponse.RemoteAssetKey = uploadResponse.RemoteAssetKey;
            }

            if (string.IsNullOrWhiteSpace(publishResponse.DownloadUrl))
            {
                publishResponse.DownloadUrl = uploadResponse.DownloadUrl;
            }

            if (string.IsNullOrWhiteSpace(publishResponse.DownloadUrl))
            {
                publishResponse.DownloadUrl = BuildDownloadUrl(request.JobId);
            }

            if (string.IsNullOrWhiteSpace(publishResponse.Message))
            {
                publishResponse.Message = "Upload and publish completed.";
            }

            return publishResponse;
        }

        private async Task<SyncJobResult> UploadAssetsAsync(SyncJobRequest request, CancellationToken cancellationToken)
        {
            var uploadUrl = BoothBackendPathResolver.Resolve(
                config.BoothApiBaseUrl,
                string.IsNullOrWhiteSpace(config.AssetUploadPathTemplate) ? "/v1/jobs/{jobId}/assets/upload" : config.AssetUploadPathTemplate,
                request.JobId);

            var composedBytes = File.ReadAllBytes(request.ComposedImagePath);
            var sections = new List<IMultipartFormSection>
            {
                new MultipartFormDataSection("job_id", request.JobId ?? string.Empty),
                new MultipartFormDataSection("device_id", ResolveDeviceId(request.DeviceId)),
                new MultipartFormDataSection("theme_id", request.ThemeId ?? string.Empty),
                new MultipartFormDataSection("currency", request.CurrencyCode ?? string.Empty),
                new MultipartFormDataSection("amount_minor_units", request.AmountMinorUnits.ToString()),
                new MultipartFormDataSection("payment_reference", request.PaymentReference ?? string.Empty),
                new MultipartFormDataSection("composed_checksum", BoothChecksumUtility.ComputeSha256Tag(request.ComposedImagePath)),
                new MultipartFormFileSection("composed_file", composedBytes, Path.GetFileName(request.ComposedImagePath), ResolveContentType(request.ComposedImagePath))
            };

            if (config.UploadThumbnail && !string.IsNullOrWhiteSpace(request.ThumbnailPath) && File.Exists(request.ThumbnailPath))
            {
                var thumbnailChecksum = BoothChecksumUtility.ComputeSha256Tag(request.ThumbnailPath);
                sections.Add(new MultipartFormDataSection("thumbnail_checksum", thumbnailChecksum));
                sections.Add(new MultipartFormFileSection("thumbnail_file", File.ReadAllBytes(request.ThumbnailPath), Path.GetFileName(request.ThumbnailPath), ResolveContentType(request.ThumbnailPath)));
            }

            using var requestMessage = UnityWebRequest.Post(uploadUrl, sections);
            requestMessage.timeout = ResolveTimeoutSeconds();
            requestMessage.downloadHandler = new DownloadHandlerBuffer();
            ApplyHeaders(requestMessage, ResolveDeviceId(request.DeviceId));

            await SendAsync(requestMessage, cancellationToken);
            return ParseUploadResponse(request.JobId, requestMessage);
        }

        private async Task<SyncJobResult> RegisterAssetsAsync(SyncJobRequest request, string remoteAssetKey, CancellationToken cancellationToken)
        {
            var registerUrl = BoothBackendPathResolver.Resolve(
                config.BoothApiBaseUrl,
                string.IsNullOrWhiteSpace(config.AssetRegistrationPathTemplate) ? "/v1/jobs/{jobId}/assets" : config.AssetRegistrationPathTemplate,
                request.JobId);

            var assets = new List<BoothAssetRegistrationItem>
            {
                new BoothAssetRegistrationItem
                {
                    asset_type = "composed",
                    remote_key = string.IsNullOrWhiteSpace(remoteAssetKey) ? $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/composed" : remoteAssetKey,
                    content_type = ResolveContentType(request.ComposedImagePath),
                    checksum = BoothChecksumUtility.ComputeSha256Tag(request.ComposedImagePath)
                }
            };

            if (config.UploadThumbnail && !string.IsNullOrWhiteSpace(request.ThumbnailPath) && File.Exists(request.ThumbnailPath))
            {
                assets.Add(new BoothAssetRegistrationItem
                {
                    asset_type = "thumbnail",
                    remote_key = $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/thumbnail",
                    content_type = ResolveContentType(request.ThumbnailPath),
                    checksum = BoothChecksumUtility.ComputeSha256Tag(request.ThumbnailPath)
                });
            }

            var payload = JsonUtility.ToJson(new BoothAssetRegistrationRequest { assets = assets.ToArray() });
            using var requestMessage = CreateJsonRequest(registerUrl, UnityWebRequest.kHttpVerbPOST, payload, ResolveDeviceId(request.DeviceId));
            await SendAsync(requestMessage, cancellationToken);
            return ParseRegistrationResponse(request.JobId, requestMessage);
        }

        private async Task<SyncJobResult> PublishAsync(SyncJobRequest request, CancellationToken cancellationToken)
        {
            var publishUrl = BoothBackendPathResolver.Resolve(
                config.PublishApiBaseUrl,
                string.IsNullOrWhiteSpace(config.PublishPathTemplate) ? "/v1/jobs/{jobId}/publish" : config.PublishPathTemplate,
                request.JobId);

            var payload = JsonUtility.ToJson(new BoothPublishRequest { primary_asset_type = "composed" });
            using var requestMessage = CreateJsonRequest(publishUrl, UnityWebRequest.kHttpVerbPOST, payload, ResolveDeviceId(request.DeviceId));
            await SendAsync(requestMessage, cancellationToken);
            return ParsePublishResponse(request.JobId, requestMessage);
        }

        private UnityWebRequest CreateJsonRequest(string url, string method, string payload, string deviceId)
        {
            var request = new UnityWebRequest(url, method)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload ?? "{}")),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = ResolveTimeoutSeconds()
            };
            request.SetRequestHeader("Content-Type", "application/json");
            ApplyHeaders(request, deviceId);
            return request;
        }

        private void ApplyHeaders(UnityWebRequest request, string deviceId)
        {
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("X-Device-Id", deviceId);
            request.SetRequestHeader("Idempotency-Key", Guid.NewGuid().ToString("N"));

            if (!string.IsNullOrWhiteSpace(config.DeviceToken))
            {
                request.SetRequestHeader("Authorization", $"Bearer {config.DeviceToken.Trim()}");
            }
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

        private SyncJobResult ParseUploadResponse(string jobId, UnityWebRequest request)
        {
            if (!IsSuccess(request))
            {
                return ParseFailure(request, $"Asset upload failed for {jobId}.");
            }

            var envelope = TryParseJson<BoothAssetUploadResponseEnvelope>(request.downloadHandler.text);
            if (envelope != null && !envelope.success)
            {
                return Failure(BuildEnvelopeErrorMessage(envelope.error, "Asset upload was rejected."), IsRetryable(request));
            }

            var remoteAssetKey = envelope?.data?.remote_asset_key;
            if (string.IsNullOrWhiteSpace(remoteAssetKey))
            {
                remoteAssetKey = envelope?.data?.remote_key;
            }

            if (string.IsNullOrWhiteSpace(remoteAssetKey) && envelope?.data?.assets != null)
            {
                foreach (var asset in envelope.data.assets)
                {
                    if (asset == null || string.IsNullOrWhiteSpace(asset.remote_key))
                    {
                        continue;
                    }

                    if (string.Equals(asset.asset_type, "composed", StringComparison.OrdinalIgnoreCase))
                    {
                        remoteAssetKey = asset.remote_key;
                        break;
                    }
                }
            }

            return new SyncJobResult
            {
                Success = true,
                Retryable = false,
                Message = "Asset upload completed.",
                RemoteAssetKey = remoteAssetKey,
                DownloadUrl = envelope?.data?.download_url
            };
        }

        private SyncJobResult ParseRegistrationResponse(string jobId, UnityWebRequest request)
        {
            if (!IsSuccess(request))
            {
                return ParseFailure(request, $"Asset registration failed for {jobId}.");
            }

            var envelope = TryParseJson<BoothRegistrationResponseEnvelope>(request.downloadHandler.text);
            if (envelope != null && !envelope.success)
            {
                return Failure(BuildEnvelopeErrorMessage(envelope.error, "Asset registration was rejected."), IsRetryable(request));
            }

            var remoteAssetKey = envelope?.data?.remote_asset_key;
            if (string.IsNullOrWhiteSpace(remoteAssetKey))
            {
                remoteAssetKey = envelope?.data?.remote_key;
            }

            if (string.IsNullOrWhiteSpace(remoteAssetKey) && envelope?.data?.assets != null)
            {
                foreach (var asset in envelope.data.assets)
                {
                    if (asset == null || string.IsNullOrWhiteSpace(asset.remote_key))
                    {
                        continue;
                    }

                    if (string.Equals(asset.asset_type, "composed", StringComparison.OrdinalIgnoreCase))
                    {
                        remoteAssetKey = asset.remote_key;
                        break;
                    }
                }
            }

            if (envelope?.data != null && !envelope.data.accepted)
            {
                return Failure("Asset registration was not accepted.", false);
            }

            return new SyncJobResult
            {
                Success = true,
                Retryable = false,
                Message = "Asset registration completed.",
                RemoteAssetKey = remoteAssetKey
            };
        }

        private SyncJobResult ParsePublishResponse(string jobId, UnityWebRequest request)
        {
            if (!IsSuccess(request))
            {
                return ParseFailure(request, $"Publish failed for {jobId}.");
            }

            var envelope = TryParseJson<BoothPublishResponseEnvelope>(request.downloadHandler.text);
            if (envelope != null && !envelope.success)
            {
                return Failure(BuildEnvelopeErrorMessage(envelope.error, "Publish was rejected."), IsRetryable(request));
            }

            var remoteAssetKey = envelope?.data?.remote_asset_key;
            if (string.IsNullOrWhiteSpace(remoteAssetKey))
            {
                remoteAssetKey = envelope?.data?.remote_key;
            }

            if (string.IsNullOrWhiteSpace(remoteAssetKey) && envelope?.data?.assets != null)
            {
                foreach (var asset in envelope.data.assets)
                {
                    if (asset == null || string.IsNullOrWhiteSpace(asset.remote_key))
                    {
                        continue;
                    }

                    if (string.Equals(asset.asset_type, "composed", StringComparison.OrdinalIgnoreCase))
                    {
                        remoteAssetKey = asset.remote_key;
                        break;
                    }
                }
            }

            return new SyncJobResult
            {
                Success = true,
                Retryable = false,
                Message = "Publish completed.",
                RemoteAssetKey = remoteAssetKey,
                DownloadUrl = envelope?.data?.download_url
            };
        }

        private static T TryParseJson<T>(string payload) where T : class
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            try
            {
                return JsonUtility.FromJson<T>(payload);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static SyncJobResult ParseFailure(UnityWebRequest request, string fallbackMessage)
        {
            var body = request.downloadHandler?.text;
            var uploadEnvelope = TryParseJson<BoothAssetUploadResponseEnvelope>(body);
            if (uploadEnvelope?.error != null)
            {
                return Failure(BuildEnvelopeErrorMessage(uploadEnvelope.error, fallbackMessage), IsRetryable(request));
            }

            var publishEnvelope = TryParseJson<BoothPublishResponseEnvelope>(body);
            if (publishEnvelope?.error != null)
            {
                return Failure(BuildEnvelopeErrorMessage(publishEnvelope.error, fallbackMessage), IsRetryable(request));
            }

            var registrationEnvelope = TryParseJson<BoothRegistrationResponseEnvelope>(body);
            if (registrationEnvelope?.error != null)
            {
                return Failure(BuildEnvelopeErrorMessage(registrationEnvelope.error, fallbackMessage), IsRetryable(request));
            }

            return Failure($"{fallbackMessage} HTTP {(long)request.responseCode}: {request.error}", IsRetryable(request));
        }

        private static string BuildEnvelopeErrorMessage(BoothApiError error, string fallbackMessage)
        {
            if (error == null)
            {
                return fallbackMessage;
            }

            if (!string.IsNullOrWhiteSpace(error.code) && !string.IsNullOrWhiteSpace(error.message))
            {
                return $"{error.code}: {error.message}";
            }

            if (!string.IsNullOrWhiteSpace(error.message))
            {
                return error.message;
            }

            if (!string.IsNullOrWhiteSpace(error.code))
            {
                return error.code;
            }

            return fallbackMessage;
        }

        private static SyncJobResult Failure(string message, bool retryable)
        {
            return new SyncJobResult
            {
                Success = false,
                Retryable = retryable,
                Message = message
            };
        }

        private static bool IsSuccess(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.Success && request.responseCode is >= 200 and < 300;
        }

        private static bool IsRetryable(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.ConnectionError
                || request.result == UnityWebRequest.Result.DataProcessingError
                || request.responseCode == 408
                || request.responseCode == 429
                || request.responseCode >= 500;
        }

        private int ResolveTimeoutSeconds()
        {
            return config.RequestTimeoutSeconds <= 0 ? 30 : config.RequestTimeoutSeconds;
        }

        private string BuildDownloadUrl(string jobId)
        {
            var baseUrl = string.IsNullOrWhiteSpace(config.DownloadBaseUrl)
                ? "https://example.invalid/d"
                : config.DownloadBaseUrl.Trim().TrimEnd('/');
            return $"{baseUrl}/{Uri.EscapeDataString(jobId ?? string.Empty)}";
        }

        private string ResolveDeviceId(string requestedDeviceId)
        {
            if (!string.IsNullOrWhiteSpace(requestedDeviceId))
            {
                return requestedDeviceId.Trim();
            }

            return string.IsNullOrWhiteSpace(config.DeviceId) ? "booth-local" : config.DeviceId.Trim();
        }

        private static string ResolveContentType(string filePath)
        {
            var extension = Path.GetExtension(filePath)?.ToLowerInvariant();
            return extension switch
            {
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "application/octet-stream"
            };
        }
    }
}
