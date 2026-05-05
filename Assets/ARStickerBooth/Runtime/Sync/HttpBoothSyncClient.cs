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
                var registrationResponse = await RegisterAssetsAsync(request, uploadResponse, cancellationToken);
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

            if (HasMotionVideo(request))
            {
                publishResponse.MotionClipUrl = $"{publishResponse.DownloadUrl.TrimEnd('/')}/clip.mp4";
            }
            else if (HasMotionClipFrames(request))
            {
                publishResponse.MotionClipUrl = $"{publishResponse.DownloadUrl.TrimEnd('/')}/clip";
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
            var sections = new List<IMultipartFormSection>();
            AddMultipartField(sections, "job_id", request.JobId, true);
            AddMultipartField(sections, "device_id", ResolveDeviceId(request.DeviceId), true);
            AddMultipartField(sections, "theme_id", request.ThemeId, false);
            AddMultipartField(sections, "currency", request.CurrencyCode, false);
            AddMultipartField(sections, "amount_minor_units", request.AmountMinorUnits.ToString(), true);
            AddMultipartField(sections, "payment_reference", request.PaymentReference, false);
            AddMultipartField(sections, "composed_checksum", BoothChecksumUtility.ComputeSha256Tag(request.ComposedImagePath), true);
            sections.Add(new MultipartFormFileSection("composed_file", composedBytes, Path.GetFileName(request.ComposedImagePath), ResolveContentType(request.ComposedImagePath)));

            if (config.UploadThumbnail && !string.IsNullOrWhiteSpace(request.ThumbnailPath) && File.Exists(request.ThumbnailPath))
            {
                var thumbnailChecksum = BoothChecksumUtility.ComputeSha256Tag(request.ThumbnailPath);
                AddMultipartField(sections, "thumbnail_checksum", thumbnailChecksum, true);
                sections.Add(new MultipartFormFileSection("thumbnail_file", File.ReadAllBytes(request.ThumbnailPath), Path.GetFileName(request.ThumbnailPath), ResolveContentType(request.ThumbnailPath)));
            }

            if (HasMotionVideo(request))
            {
                AddMultipartField(sections, "motion_video_checksum", BoothChecksumUtility.ComputeSha256Tag(request.MotionVideoPath), true);
                sections.Add(new MultipartFormFileSection("motion_video_file", File.ReadAllBytes(request.MotionVideoPath), Path.GetFileName(request.MotionVideoPath), ResolveContentType(request.MotionVideoPath)));
            }

            var motionFrameIndex = 0;
            foreach (var framePath in EnumerateExistingMotionFrames(request))
            {
                AddMultipartField(sections, $"motion_frame_checksum_{motionFrameIndex}", BoothChecksumUtility.ComputeSha256Tag(framePath), true);
                sections.Add(new MultipartFormFileSection("motion_frame_files", File.ReadAllBytes(framePath), Path.GetFileName(framePath), ResolveContentType(framePath)));
                motionFrameIndex += 1;
            }

            using var requestMessage = UnityWebRequest.Post(uploadUrl, sections);
            requestMessage.timeout = ResolveTimeoutSeconds();
            requestMessage.downloadHandler = new DownloadHandlerBuffer();
            ApplyHeaders(requestMessage, ResolveDeviceId(request.DeviceId));

            await SendAsync(requestMessage, cancellationToken);
            return ParseUploadResponse(request.JobId, requestMessage);
        }

        private async Task<SyncJobResult> RegisterAssetsAsync(SyncJobRequest request, SyncJobResult uploadResponse, CancellationToken cancellationToken)
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
                    remote_key = ResolveUploadedAssetKey(uploadResponse, "composed") ?? uploadResponse?.RemoteAssetKey ?? $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/composed",
                    content_type = ResolveContentType(request.ComposedImagePath),
                    checksum = BoothChecksumUtility.ComputeSha256Tag(request.ComposedImagePath)
                }
            };

            if (config.UploadThumbnail && !string.IsNullOrWhiteSpace(request.ThumbnailPath) && File.Exists(request.ThumbnailPath))
            {
                assets.Add(new BoothAssetRegistrationItem
                {
                    asset_type = "thumbnail",
                    remote_key = ResolveUploadedAssetKey(uploadResponse, "thumbnail") ?? $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/thumbnail",
                    content_type = ResolveContentType(request.ThumbnailPath),
                    checksum = BoothChecksumUtility.ComputeSha256Tag(request.ThumbnailPath)
                });
            }

            if (HasMotionVideo(request))
            {
                assets.Add(new BoothAssetRegistrationItem
                {
                    asset_type = "motion_video",
                    remote_key = ResolveUploadedAssetKey(uploadResponse, "motion_video") ?? $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/motion.mp4",
                    content_type = ResolveContentType(request.MotionVideoPath),
                    checksum = BoothChecksumUtility.ComputeSha256Tag(request.MotionVideoPath)
                });
            }

            var motionFrameIndex = 0;
            foreach (var framePath in EnumerateExistingMotionFrames(request))
            {
                var uploadedKey = ResolveUploadedAssetKey(uploadResponse, "motion_frame", motionFrameIndex);
                assets.Add(new BoothAssetRegistrationItem
                {
                    asset_type = "motion_frame",
                    remote_key = uploadedKey ?? $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/motion/{motionFrameIndex:000}",
                    content_type = ResolveContentType(framePath),
                    checksum = BoothChecksumUtility.ComputeSha256Tag(framePath)
                });
                motionFrameIndex += 1;
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
                DownloadUrl = envelope?.data?.download_url,
                Assets = envelope?.data?.assets
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
                DownloadUrl = envelope?.data?.download_url,
                MotionClipUrl = envelope?.data?.motion_clip_url,
                MotionVideoUrl = envelope?.data?.motion_video_url
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

        private static bool HasMotionClipFrames(SyncJobRequest request)
        {
            foreach (var _ in EnumerateExistingMotionFrames(request))
            {
                return true;
            }

            return false;
        }

        private static bool HasMotionVideo(SyncJobRequest request)
        {
            return !string.IsNullOrWhiteSpace(request?.MotionVideoPath) && File.Exists(request.MotionVideoPath);
        }

        private static IEnumerable<string> EnumerateExistingMotionFrames(SyncJobRequest request)
        {
            if (request?.MotionClipFramePaths == null)
            {
                yield break;
            }

            foreach (var framePath in request.MotionClipFramePaths)
            {
                if (!string.IsNullOrWhiteSpace(framePath) && File.Exists(framePath))
                {
                    yield return framePath;
                }
            }
        }

        private static string ResolveUploadedAssetKey(SyncJobResult uploadResponse, string assetType, int occurrenceIndex = 0)
        {
            if (uploadResponse?.Assets == null || string.IsNullOrWhiteSpace(assetType))
            {
                return null;
            }

            var matchIndex = 0;
            foreach (var asset in uploadResponse.Assets)
            {
                if (asset == null || !string.Equals(asset.asset_type, assetType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (matchIndex == occurrenceIndex)
                {
                    return asset.remote_key;
                }

                matchIndex += 1;
            }

            return null;
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
                ".mp4" => "video/mp4",
                ".mov" => "video/quicktime",
                _ => "application/octet-stream"
            };
        }

        private static void AddMultipartField(List<IMultipartFormSection> sections, string fieldName, string value, bool required)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                if (required)
                {
                    throw new ArgumentException($"Multipart field '{fieldName}' is required.");
                }

                return;
            }

            sections.Add(new MultipartFormDataSection(fieldName, Encoding.UTF8.GetBytes(normalized), "text/plain; charset=utf-8"));
        }
    }
}
