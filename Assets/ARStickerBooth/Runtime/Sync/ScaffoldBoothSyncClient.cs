using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Sync
{
    public sealed class ScaffoldBoothSyncClient : IBoothSyncClient
    {
        private readonly BoothBackendScaffoldConfig config;
        private readonly int latencyMilliseconds;

        public ScaffoldBoothSyncClient(BoothBackendScaffoldConfig config, int latencyMilliseconds = 200)
        {
            this.config = config ?? new BoothBackendScaffoldConfig();
            this.latencyMilliseconds = Math.Max(0, latencyMilliseconds);
        }

        public async Task<RawCaptureUploadResult> UploadRawCaptureAsync(RawCaptureUploadRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.RawCapturePath) || !File.Exists(request.RawCapturePath))
            {
                return new RawCaptureUploadResult
                {
                    Success = false,
                    Retryable = false,
                    Message = "Raw capture file is missing."
                };
            }

            if (latencyMilliseconds > 0)
            {
                await Task.Delay(latencyMilliseconds, cancellationToken);
            }

            return new RawCaptureUploadResult
            {
                Success = true,
                Retryable = false,
                Message = "Scaffold raw capture upload completed.",
                RemoteAssetKey = $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/raw/capture_{Math.Max(1, request.CaptureIndex):00}",
                FileUrl = $"{(string.IsNullOrWhiteSpace(config.BoothApiBaseUrl) ? "https://example.invalid" : config.BoothApiBaseUrl.Trim().TrimEnd('/'))}/files/{ResolveDeviceId(request.DeviceId)}/{request.JobId}/raw/capture_{Math.Max(1, request.CaptureIndex):00}",
                SessionFolder = $"{request.SessionStartedAtUtc ?? "session"}_{request.JobId}"
            };
        }

        public async Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ComposedImagePath) || !File.Exists(request.ComposedImagePath))
            {
                return new SyncJobResult
                {
                    Success = false,
                    Retryable = false,
                    Message = "Composed image file is missing."
                };
            }

            if (latencyMilliseconds > 0)
            {
                await Task.Delay(latencyMilliseconds, cancellationToken);
            }

            var remoteAssetKey = $"{ResolveDeviceId(request.DeviceId)}/{request.JobId}/final";
            return new SyncJobResult
            {
                Success = true,
                Retryable = false,
                Message = "Scaffold sync completed.",
                RemoteAssetKey = remoteAssetKey,
                DownloadUrl = BuildDownloadUrl(request.JobId),
                MotionClipUrl = ResolveMotionClipUrl(request)
            };
        }

        private string ResolveMotionClipUrl(SyncJobRequest request)
        {
            var downloadUrl = BuildDownloadUrl(request.JobId);
            if (!string.IsNullOrWhiteSpace(request.MotionVideoPath) && File.Exists(request.MotionVideoPath))
            {
                return $"{downloadUrl}/clip.mp4";
            }

            if (request.MotionClipFramePaths != null)
            {
                foreach (var framePath in request.MotionClipFramePaths)
                {
                    if (!string.IsNullOrWhiteSpace(framePath) && File.Exists(framePath))
                    {
                        return $"{downloadUrl}/clip";
                    }
                }
            }

            return null;
        }

        private string BuildDownloadUrl(string jobId)
        {
            var baseUrl = string.IsNullOrWhiteSpace(config.DownloadBaseUrl)
                ? "https://example.invalid/world-tour"
                : config.DownloadBaseUrl.Trim().TrimEnd('/');
            return $"{baseUrl}/{jobId}";
        }

        private string ResolveDeviceId(string requestedDeviceId)
        {
            if (!string.IsNullOrWhiteSpace(requestedDeviceId))
            {
                return requestedDeviceId.Trim();
            }

            return string.IsNullOrWhiteSpace(config.DeviceId) ? "booth-local" : config.DeviceId.Trim();
        }
    }
}
