using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Printing;
using PhotoBooth.Booth.Services;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothPrintServiceTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothPrintTests", Guid.NewGuid().ToString("N"));
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
        public async Task PrintAsync_WhenClientSucceeds_MarksJobPrinted()
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

            var printService = new BoothPrintService(sessions, new FakePrintClient(success: true, retryable: false));
            var printedJob = await printService.PrintAsync(job.JobId, "Printer A");

            Assert.That(printedJob.Status, Is.EqualTo(PhotoBooth.Booth.Domain.BoothJobStatus.Printed));
            Assert.That(printedJob.PrintStatus, Is.EqualTo(PhotoBooth.Booth.Domain.BoothPrintStatus.Printed));
            Assert.That(printedJob.PrinterName, Is.EqualTo("Printer A"));
            Assert.That(printedJob.PrintAttempts, Is.EqualTo(1));
        }

        private sealed class FakePrintClient : IPrintHelperClient
        {
            private readonly bool success;
            private readonly bool retryable;

            public FakePrintClient(bool success, bool retryable)
            {
                this.success = success;
                this.retryable = retryable;
            }

            public Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new PrintJobResult
                {
                    Success = success,
                    Retryable = retryable,
                    Message = success ? "Printed" : "Printer offline",
                    PrinterName = request.PrinterName
                });
            }
        }
    }
}
