using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Services;
using PhotoBooth.Booth.Sync;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothSyncServiceTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothSyncTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tempRootDirectory))
            {
                Directory.Delete(tempRootDirectory, true);
            }
        }

        [Test]
        public async Task SyncAsync_WhenClientSucceeds_MarksLinkReady()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());
            sessions.Initialize();

            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "theme-a");
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);
            var motionFramePath = Path.Combine(job.Paths.RawDirectory, "motion_000.png");
            File.WriteAllText(motionFramePath, "fake-motion-frame");
            var motionVideoPath = Path.Combine(job.Paths.RawDirectory, "motion.mp4");
            File.WriteAllText(motionVideoPath, "fake-motion-video");
            job = sessions.MarkCaptured(job.JobId, 1, motionFramePath, new[] { motionFramePath }, motionVideoPath);
            job = sessions.BeginComposing(job.JobId);

            var composedPath = Path.Combine(job.Paths.ComposedDirectory, "final.png");
            File.WriteAllText(composedPath, "fake-image");
            job = sessions.MarkComposed(job.JobId, composedPath, null);

            var syncService = new BoothSyncService(
                sessions,
                new FakeSyncClient(success: true, retryable: false),
                new BoothBackendScaffoldConfig { DeviceId = "booth-a01" });

            var syncedJob = await syncService.SyncAsync(job.JobId);

            Assert.That(syncedJob.Status, Is.EqualTo(PhotoBooth.Booth.Domain.BoothJobStatus.LinkReady));
            Assert.That(syncedJob.UploadStatus, Is.EqualTo(PhotoBooth.Booth.Domain.BoothUploadStatus.LinkReady));
            Assert.That(syncedJob.DownloadUrl, Is.EqualTo("https://example.invalid/d/" + job.JobId));
            Assert.That(syncedJob.MotionClipUrl, Is.EqualTo("https://example.invalid/d/" + job.JobId + "/clip.mp4"));
            Assert.That(syncedJob.MotionVideoUrl, Is.EqualTo("https://example.invalid/d/" + job.JobId + "/clip.mp4"));
            Assert.That(syncedJob.MotionVideoPath, Is.EqualTo(motionVideoPath));
            Assert.That(syncedJob.UploadAttempts, Is.EqualTo(1));
        }

        [Test]
        public async Task UploadRawCaptureAsync_FillsConfiguredDeviceId()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());
            sessions.Initialize();

            var fakeClient = new FakeSyncClient(success: true, retryable: false);
            var syncService = new BoothSyncService(
                sessions,
                fakeClient,
                new BoothBackendScaffoldConfig { DeviceId = "booth-a01" });

            var result = await syncService.UploadRawCaptureAsync(new RawCaptureUploadRequest
            {
                JobId = "JOB-001",
                RawCapturePath = Path.Combine(tempRootDirectory, "capture.png"),
                CaptureIndex = 1,
                CaptureTotal = 4,
                SessionStartedAtUtc = DateTime.UtcNow.ToString("O"),
                CaptureTakenAtUtc = DateTime.UtcNow.ToString("O")
            });

            Assert.That(result.Success, Is.True);
            Assert.That(fakeClient.LastRawCaptureRequest.DeviceId, Is.EqualTo("booth-a01"));
            Assert.That(fakeClient.LastRawCaptureRequest.CaptureIndex, Is.EqualTo(1));
            Assert.That(fakeClient.LastRawCaptureRequest.CaptureTotal, Is.EqualTo(4));
        }

        [Test]
        public async Task PrepareDownloadAsync_FillsConfiguredDeviceIdAndReturnsQrUrl()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());
            sessions.Initialize();

            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "theme-a");
            var fakeClient = new FakeSyncClient(success: true, retryable: false);
            var syncService = new BoothSyncService(
                sessions,
                fakeClient,
                new BoothBackendScaffoldConfig { DeviceId = "booth-a01" });

            var result = await syncService.PrepareDownloadAsync(job.JobId);

            Assert.That(result.Success, Is.True);
            Assert.That(result.DownloadUrl, Is.EqualTo("https://example.invalid/d/" + job.JobId));
            Assert.That(result.QrPngUrl, Is.EqualTo("https://example.invalid/d/" + job.JobId + "/qr"));
            Assert.That(fakeClient.LastPreparedDownloadRequest.DeviceId, Is.EqualTo("booth-a01"));
            Assert.That(fakeClient.LastPreparedDownloadRequest.ThemeId, Is.EqualTo("theme-a"));
        }

        [Test]
        public async Task RawCaptureUploadQueue_WhenRetryableFailure_DoesNotThrow()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());
            sessions.Initialize();

            var syncService = new BoothSyncService(
                sessions,
                new FakeSyncClient(success: false, retryable: true, rawSuccess: false),
                new BoothBackendScaffoldConfig { DeviceId = "booth-a01" });

            var queue = new BoothRawCaptureUploadQueue(syncService, maxAttempts: 1);
            queue.Enqueue(new RawCaptureUploadRequest
            {
                JobId = "JOB-001",
                RawCapturePath = Path.Combine(tempRootDirectory, "capture.png"),
                CaptureIndex = 1,
                CaptureTotal = 4,
                SessionStartedAtUtc = DateTime.UtcNow.ToString("O"),
                CaptureTakenAtUtc = DateTime.UtcNow.ToString("O")
            });

            await queue.FlushAsync();

            Assert.That(queue.PendingCount, Is.EqualTo(0));
        }

        private sealed class FakeSyncClient : IBoothSyncClient
        {
            private readonly bool success;
            private readonly bool retryable;
            private readonly bool rawSuccess;

            public FakeSyncClient(bool success, bool retryable, bool rawSuccess = true)
            {
                this.success = success;
                this.retryable = retryable;
                this.rawSuccess = rawSuccess;
            }

            public RawCaptureUploadRequest LastRawCaptureRequest { get; private set; }
            public PreparedDownloadRequest LastPreparedDownloadRequest { get; private set; }

            public Task<PreparedDownloadResult> PrepareDownloadAsync(PreparedDownloadRequest request, CancellationToken cancellationToken = default)
            {
                LastPreparedDownloadRequest = request;
                return Task.FromResult(new PreparedDownloadResult
                {
                    Success = true,
                    Retryable = false,
                    Message = "Prepared",
                    JobId = request.JobId,
                    DownloadUrl = $"https://example.invalid/d/{request.JobId}",
                    QrPngUrl = $"https://example.invalid/d/{request.JobId}/qr"
                });
            }

            public Task<RawCaptureUploadResult> UploadRawCaptureAsync(RawCaptureUploadRequest request, CancellationToken cancellationToken = default)
            {
                LastRawCaptureRequest = request;
                return Task.FromResult(new RawCaptureUploadResult
                {
                    Success = rawSuccess,
                    Retryable = !rawSuccess && retryable,
                    Message = rawSuccess ? "Raw uploaded" : "Raw timeout",
                    RemoteAssetKey = $"raw/{request.JobId}/{request.CaptureIndex:00}",
                    FileUrl = $"https://example.invalid/files/raw/{request.JobId}/{request.CaptureIndex:00}"
                });
            }

            public Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncJobResult
                {
                    Success = success,
                    Retryable = retryable,
                    Message = success ? "Uploaded" : "Network timeout",
                    RemoteAssetKey = $"assets/{request.JobId}",
                    DownloadUrl = $"https://example.invalid/d/{request.JobId}",
                    MotionClipUrl = !string.IsNullOrWhiteSpace(request.MotionVideoPath) && File.Exists(request.MotionVideoPath)
                        ? $"https://example.invalid/d/{request.JobId}/clip.mp4"
                        : request.MotionClipFramePaths == null || request.MotionClipFramePaths.Length == 0
                            ? null
                            : $"https://example.invalid/d/{request.JobId}/clip"
                });
            }
        }
    }
}
