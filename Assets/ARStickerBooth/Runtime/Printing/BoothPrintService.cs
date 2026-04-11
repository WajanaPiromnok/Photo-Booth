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

            if (job.Status != BoothJobStatus.Composed && job.Status != BoothJobStatus.Printing)
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
                return sessionService.MarkPrinted(jobId, result.PrinterName);
            }

            if (result.Retryable)
            {
                return sessionService.MarkPrintRetryWait(jobId, result.Message, result.PrinterName);
            }

            return sessionService.Fail(jobId, result.Message);
        }
    }
}
