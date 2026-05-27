using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Services;

namespace PhotoBooth.Booth.Sync
{
    public sealed class BoothSyncService
    {
        private readonly BoothSessionService sessionService;
        private readonly IBoothSyncClient syncClient;
        private readonly BoothBackendScaffoldConfig config;

        public BoothSyncService(
            BoothSessionService sessionService,
            IBoothSyncClient syncClient,
            BoothBackendScaffoldConfig config)
        {
            this.sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            this.syncClient = syncClient ?? throw new ArgumentNullException(nameof(syncClient));
            this.config = config ?? new BoothBackendScaffoldConfig();
        }

        public async Task<BoothJob> SyncAsync(string jobId, CancellationToken cancellationToken = default)
        {
            var job = sessionService.GetJob(jobId);
            if (job.Status == BoothJobStatus.LinkReady || job.Status == BoothJobStatus.Done)
            {
                return job;
            }

            if (job.Status != BoothJobStatus.Composed
                && job.Status != BoothJobStatus.Printed
                && job.Status != BoothJobStatus.UploadPending
                && job.Status != BoothJobStatus.Uploading)
            {
                throw new InvalidOperationException($"Job {jobId} is not ready to sync from state {job.Status}.");
            }

            if (string.IsNullOrWhiteSpace(job.Paths?.ComposedImagePath) || !File.Exists(job.Paths.ComposedImagePath))
            {
                return sessionService.Fail(jobId, "Cannot sync without a composed image.");
            }

            if (job.Status == BoothJobStatus.Composed || job.Status == BoothJobStatus.Printed)
            {
                job = sessionService.QueueUpload(jobId);
            }

            if (job.Status == BoothJobStatus.UploadPending)
            {
                job = sessionService.BeginUpload(jobId);
            }

            var request = new SyncJobRequest
            {
                JobId = job.JobId,
                DeviceId = ResolveDeviceId(),
                ThemeId = job.ThemeId,
                ImagePreviewId = job.ThemeId,
                ComposedImagePath = job.Paths.ComposedImagePath,
                ThumbnailPath = job.Paths.ThumbnailPath,
                LiveImagePath = ResolveLiveImagePath(job),
                MotionVideoPath = job.MotionVideoPath,
                CurrencyCode = job.CurrencyCode,
                AmountMinorUnits = job.AmountMinorUnits,
                PaymentReference = job.PaymentReference,
                MotionClipFramePaths = job.MotionClipFramePaths,
                SessionStartedAtUtc = ResolveSessionStartedAtUtc(job)
            };

            var result = await syncClient.UploadAndPublishAsync(request, cancellationToken);
            if (result.Success)
            {
                sessionService.MarkUploaded(jobId, result.RemoteAssetKey);
                return sessionService.MarkLinkReady(jobId, result.DownloadUrl, string.IsNullOrWhiteSpace(result.MotionVideoUrl) ? result.MotionClipUrl : result.MotionVideoUrl);
            }

            if (result.Retryable)
            {
                return sessionService.MarkUploadRetryWait(jobId, result.Message);
            }

            return sessionService.Fail(jobId, result.Message);
        }

        public Task<RawCaptureUploadResult> UploadRawCaptureAsync(RawCaptureUploadRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.DeviceId))
            {
                request.DeviceId = ResolveDeviceId();
            }

            return syncClient.UploadRawCaptureAsync(request, cancellationToken);
        }

        private string ResolveDeviceId()
        {
            return string.IsNullOrWhiteSpace(config.DeviceId) ? "booth-local" : config.DeviceId.Trim();
        }

        private static string ResolveSessionStartedAtUtc(BoothJob job)
        {
            return string.IsNullOrWhiteSpace(job?.CreatedAtUtc)
                ? DateTime.UtcNow.ToString("O")
                : job.CreatedAtUtc;
        }

        private static string ResolveLiveImagePath(BoothJob job)
        {
            if (string.IsNullOrWhiteSpace(job?.Paths?.ComposedDirectory))
            {
                return null;
            }

            var liveImagePath = Path.Combine(job.Paths.ComposedDirectory, "live.png");
            return File.Exists(liveImagePath) ? liveImagePath : null;
        }
    }
}
