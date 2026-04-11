using System;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Persistence;

namespace PhotoBooth.Booth.Services
{
    public sealed class BoothSessionService
    {
        private readonly ILocalRepository repository;
        private readonly BoothStateMachine stateMachine;
        private readonly IBoothTelemetrySink telemetry;

        public BoothSessionService(ILocalRepository repository, BoothStateMachine stateMachine, IBoothTelemetrySink telemetry = null)
        {
            this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
            this.stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));
            this.telemetry = telemetry;
        }

        public void Initialize()
        {
            repository.Initialize();
        }

        public BoothJob CreateJob(long amountMinorUnits, string currencyCode = "THB")
        {
            var jobId = BuildJobId();
            var paths = repository.CreateJobPaths(jobId);
            var job = stateMachine.CreateJob(jobId, paths, amountMinorUnits, currencyCode);
            var saved = repository.SaveNew(job);
            telemetry?.OnJobCreated(saved);
            return saved;
        }

        public BoothJob GetJob(string jobId)
        {
            return repository.Get(jobId);
        }

        public BoothJob SelectTheme(string jobId, string themeId)
        {
            var job = repository.Get(jobId);
            stateMachine.SelectTheme(job, themeId);
            return Save(job, saved => telemetry?.OnThemeSelected(saved));
        }

        public BoothJob SetPaymentPending(string jobId, string paymentReference = null)
        {
            var job = repository.Get(jobId);
            stateMachine.SetPaymentPending(job, paymentReference);
            return Save(job, saved => telemetry?.OnPaymentPending(saved));
        }

        public BoothJob ConfirmPayment(string jobId, string paymentReference = null)
        {
            var job = repository.Get(jobId);
            stateMachine.ConfirmPayment(job, paymentReference);
            return Save(job, saved => telemetry?.OnPaymentConfirmed(saved));
        }

        public BoothJob BypassPayment(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.BypassPayment(job);
            return repository.Save(job);
        }

        public BoothJob BeginCapture(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.BeginCapture(job);
            return repository.Save(job);
        }

        public BoothJob MarkCaptured(string jobId, int rawCaptureCount)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkCaptured(job, rawCaptureCount);
            return Save(job, saved => telemetry?.OnCaptureCompleted(saved));
        }

        public BoothJob BeginComposing(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.BeginComposing(job);
            return repository.Save(job);
        }

        public BoothJob MarkComposed(string jobId, string composedImagePath, string thumbnailPath)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkComposed(job, composedImagePath, thumbnailPath);
            return Save(job, saved => telemetry?.OnCompositionCompleted(saved));
        }

        public BoothJob BeginPrinting(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.BeginPrinting(job);
            return repository.Save(job);
        }

        public BoothJob MarkPrintRetryWait(string jobId, string reason, string printerName = null)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkPrintRetryWait(job, reason, printerName);
            return repository.Save(job);
        }

        public BoothJob MarkPrinted(string jobId, string printerName = null)
        {
            var job = repository.Get(jobId);
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                job.PrinterName = printerName;
            }

            stateMachine.MarkPrinted(job);
            return Save(job, saved => telemetry?.OnPrintCompleted(saved));
        }

        public BoothJob QueueUpload(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.QueueUpload(job);
            return repository.Save(job);
        }

        public BoothJob BeginUpload(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.BeginUpload(job);
            return repository.Save(job);
        }

        public BoothJob MarkUploadRetryWait(string jobId, string reason)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkUploadRetryWait(job, reason);
            return repository.Save(job);
        }

        public BoothJob MarkUploaded(string jobId, string remoteAssetKey = null)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkUploaded(job, remoteAssetKey);
            return repository.Save(job);
        }

        public BoothJob MarkLinkReady(string jobId, string downloadUrl)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkLinkReady(job, downloadUrl);
            return Save(job, saved => telemetry?.OnDownloadLinkReady(saved));
        }

        public BoothJob MarkDone(string jobId)
        {
            var job = repository.Get(jobId);
            stateMachine.MarkDone(job);
            return Save(job, saved => telemetry?.OnJobCompleted(saved));
        }

        public BoothJob Cancel(string jobId, string reason = null)
        {
            var job = repository.Get(jobId);
            stateMachine.Cancel(job, reason);
            return Save(job, saved => telemetry?.OnJobCancelled(saved, reason));
        }

        public BoothJob Fail(string jobId, string reason)
        {
            var job = repository.Get(jobId);
            stateMachine.Fail(job, reason);
            return Save(job, saved => telemetry?.OnJobFailed(saved, reason));
        }

        private static string BuildJobId()
        {
            return $"JOB-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
        }

        private BoothJob Save(BoothJob job, Action<BoothJob> afterSave = null)
        {
            var saved = repository.Save(job);
            afterSave?.Invoke(saved);
            return saved;
        }
    }
}
