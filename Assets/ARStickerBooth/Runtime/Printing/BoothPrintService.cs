using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Services;

namespace PhotoBooth.Booth.Printing
{
    public sealed class BoothPrintService
    {
        private readonly BoothSessionService sessionService;
        private readonly IPrintHelperClient printHelperClient;

        public BoothPrintService(BoothSessionService sessionService, IPrintHelperClient printHelperClient)
        {
            this.sessionService = sessionService ?? throw new ArgumentNullException(nameof(sessionService));
            this.printHelperClient = printHelperClient ?? throw new ArgumentNullException(nameof(printHelperClient));
        }

        public async Task<BoothJob> PrintAsync(
            string jobId,
            string printerName,
            int copies = 1,
            CancellationToken cancellationToken = default)
        {
            var job = sessionService.GetJob(jobId);
            if (job.Status == BoothJobStatus.Printed || job.Status == BoothJobStatus.Done)
            {
                return job;
            }

            var preserveLifecycleStatus = job.Status == BoothJobStatus.UploadPending
                || job.Status == BoothJobStatus.Uploading
                || job.Status == BoothJobStatus.Uploaded
                || job.Status == BoothJobStatus.LinkReady;

            if (job.Status != BoothJobStatus.Composed
                && job.Status != BoothJobStatus.Printing
                && !preserveLifecycleStatus)
            {
                throw new InvalidOperationException($"Job {jobId} is not ready to print from state {job.Status}.");
            }

            if (string.IsNullOrWhiteSpace(job.Paths?.ComposedImagePath) || !File.Exists(job.Paths.ComposedImagePath))
            {
                return sessionService.Fail(jobId, "Cannot print without a composed image.");
            }

            if (job.Status == BoothJobStatus.Composed)
            {
                job = sessionService.BeginPrinting(jobId);
            }
            else if (preserveLifecycleStatus && job.PrintStatus != BoothPrintStatus.Printing)
            {
                job = sessionService.BeginSidecarPrint(jobId, printerName);
            }

            var request = new PrintJobRequest
            {
                JobId = job.JobId,
                PrinterName = string.IsNullOrWhiteSpace(printerName) ? job.PrinterName : printerName.Trim(),
                ImagePath = job.Paths.ComposedImagePath,
                ThumbnailPath = job.Paths.ThumbnailPath,
                Copies = Math.Max(1, copies)
            };

            var result = await printHelperClient.PrintAsync(request, cancellationToken);
            if (result.Success)
            {
                return preserveLifecycleStatus
                    ? sessionService.MarkPrintedWithoutStatusChange(jobId, result.PrinterName)
                    : sessionService.MarkPrinted(jobId, result.PrinterName);
            }

            if (result.Retryable)
            {
                return preserveLifecycleStatus
                    ? sessionService.MarkPrintRetryWaitWithoutStatusChange(jobId, result.Message, result.PrinterName)
                    : sessionService.MarkPrintRetryWait(jobId, result.Message, result.PrinterName);
            }

            return preserveLifecycleStatus
                ? sessionService.MarkPrintFailedWithoutStatusChange(jobId, result.Message, result.PrinterName)
                : sessionService.Fail(jobId, result.Message);
        }
    }
}
