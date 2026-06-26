using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Printing;
using PhotoBooth.Booth.Services;
using PhotoBooth.Booth.Sync;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothPrintAfterSyncTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothPrintAfterSyncTests", Guid.NewGuid().ToString("N"));
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
        public async Task PrintAsync_AfterLinkReady_PreservesLinkReadyStatus()
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
                new FakeSyncClient(),
                new BoothBackendScaffoldConfig { DeviceId = "booth-a01" });

            job = await syncService.SyncAsync(job.JobId);
            Assert.That(job.Status, Is.EqualTo(PhotoBooth.Booth.Domain.BoothJobStatus.LinkReady));

            var printService = new BoothPrintService(sessions, new FakePrintClient());
            var printedJob = await printService.PrintAsync(job.JobId, "Printer A");

            Assert.That(printedJob.Status, Is.EqualTo(PhotoBooth.Booth.Domain.BoothJobStatus.LinkReady));
            Assert.That(printedJob.UploadStatus, Is.EqualTo(PhotoBooth.Booth.Domain.BoothUploadStatus.LinkReady));
            Assert.That(printedJob.PrintStatus, Is.EqualTo(PhotoBooth.Booth.Domain.BoothPrintStatus.Printed));
            Assert.That(printedJob.PrinterName, Is.EqualTo("Printer A"));
            Assert.That(printedJob.PrintAttempts, Is.EqualTo(1));
        }

        private sealed class FakePrintClient : IPrintHelperClient
        {
            public Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new PrintJobResult
                {
                    Success = true,
                    Retryable = false,
                    Message = "Printed",
                    PrinterName = request.PrinterName
                });
            }
        }

        private sealed class FakeSyncClient : IBoothSyncClient
        {
            public Task<PreparedDownloadResult> PrepareDownloadAsync(PreparedDownloadRequest request, CancellationToken cancellationToken = default)
            {
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
                return Task.FromResult(new RawCaptureUploadResult
                {
                    Success = true,
                    Retryable = false,
                    RemoteAssetKey = $"raw/{request.JobId}/{request.CaptureIndex:00}",
                    FileUrl = $"https://example.invalid/files/raw/{request.JobId}/{request.CaptureIndex:00}"
                });
            }

            public Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new SyncJobResult
                {
                    Success = true,
                    Retryable = false,
                    Message = "Uploaded",
                    RemoteAssetKey = $"assets/{request.JobId}",
                    DownloadUrl = $"https://example.invalid/d/{request.JobId}"
                });
            }
        }
    }
}
