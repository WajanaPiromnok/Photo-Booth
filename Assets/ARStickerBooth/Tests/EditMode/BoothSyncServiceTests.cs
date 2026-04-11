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
            job = sessions.MarkCaptured(job.JobId, 1);
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
            Assert.That(syncedJob.UploadAttempts, Is.EqualTo(1));
        }

        private sealed class FakeSyncClient : IBoothSyncClient
        {
            private readonly bool success;
            private readonly bool retryable;

            public FakeSyncClient(bool success, bool retryable)
            {
                this.success = success;
                this.retryable = retryable;
            }

            public Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncJobResult
                {
                    Success = success,
                    Retryable = retryable,
                    Message = success ? "Uploaded" : "Network timeout",
                    RemoteAssetKey = $"assets/{request.JobId}",
                    DownloadUrl = $"https://example.invalid/d/{request.JobId}"
                });
            }
        }
    }
}
