using System;
using System.Collections.Generic;
using PhotoBooth.Booth.Domain;

namespace PhotoBooth.Booth.Services
{
    public sealed class BoothStateMachine
    {
        private static readonly Dictionary<BoothJobStatus, HashSet<BoothJobStatus>> AllowedTransitions = new()
        {
            [BoothJobStatus.Idle] = new HashSet<BoothJobStatus> { BoothJobStatus.Created },
            [BoothJobStatus.Created] = new HashSet<BoothJobStatus> { BoothJobStatus.ThemeSelected, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.ThemeSelected] = new HashSet<BoothJobStatus> { BoothJobStatus.PaymentPending, BoothJobStatus.PaymentBypassed, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.PaymentPending] = new HashSet<BoothJobStatus> { BoothJobStatus.PaymentConfirmed, BoothJobStatus.PaymentBypassed, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.PaymentConfirmed] = new HashSet<BoothJobStatus> { BoothJobStatus.Capturing, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.PaymentBypassed] = new HashSet<BoothJobStatus> { BoothJobStatus.Capturing, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.Capturing] = new HashSet<BoothJobStatus> { BoothJobStatus.Captured, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.Captured] = new HashSet<BoothJobStatus> { BoothJobStatus.Capturing, BoothJobStatus.Composing, BoothJobStatus.Cancelled, BoothJobStatus.Failed },
            [BoothJobStatus.Composing] = new HashSet<BoothJobStatus> { BoothJobStatus.Capturing, BoothJobStatus.Composed, BoothJobStatus.Failed },
            [BoothJobStatus.Composed] = new HashSet<BoothJobStatus> { BoothJobStatus.Capturing, BoothJobStatus.Printing, BoothJobStatus.UploadPending, BoothJobStatus.Failed },
            [BoothJobStatus.Printing] = new HashSet<BoothJobStatus> { BoothJobStatus.Composed, BoothJobStatus.Printed, BoothJobStatus.Failed },
            [BoothJobStatus.Printed] = new HashSet<BoothJobStatus> { BoothJobStatus.UploadPending, BoothJobStatus.Done, BoothJobStatus.Failed },
            [BoothJobStatus.UploadPending] = new HashSet<BoothJobStatus> { BoothJobStatus.Uploading, BoothJobStatus.Failed },
            [BoothJobStatus.Uploading] = new HashSet<BoothJobStatus> { BoothJobStatus.Uploaded, BoothJobStatus.UploadPending, BoothJobStatus.Failed },
            [BoothJobStatus.Uploaded] = new HashSet<BoothJobStatus> { BoothJobStatus.LinkReady, BoothJobStatus.Done, BoothJobStatus.Failed },
            [BoothJobStatus.LinkReady] = new HashSet<BoothJobStatus> { BoothJobStatus.Done, BoothJobStatus.Failed },
            [BoothJobStatus.Done] = new HashSet<BoothJobStatus>(),
            [BoothJobStatus.Cancelled] = new HashSet<BoothJobStatus>(),
            [BoothJobStatus.Failed] = new HashSet<BoothJobStatus>()
        };

        public BoothJob CreateJob(string jobId, BoothJobPaths paths, long amountMinorUnits, string currencyCode)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            if (paths == null)
            {
                throw new ArgumentNullException(nameof(paths));
            }

            var now = UtcNow();
            return new BoothJob
            {
                JobId = jobId,
                Status = BoothJobStatus.Created,
                PaymentStatus = BoothPaymentStatus.Unknown,
                PrintStatus = BoothPrintStatus.Pending,
                UploadStatus = BoothUploadStatus.Pending,
                AmountMinorUnits = amountMinorUnits,
                CurrencyCode = string.IsNullOrWhiteSpace(currencyCode) ? "THB" : currencyCode.ToUpperInvariant(),
                Paths = paths,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
        }

        public BoothJob SelectTheme(BoothJob job, string themeId)
        {
            Transition(job, BoothJobStatus.ThemeSelected);
            job.ThemeId = themeId;
            return job;
        }

        public BoothJob SetPaymentPending(BoothJob job, string paymentReference = null)
        {
            Transition(job, BoothJobStatus.PaymentPending);
            job.PaymentStatus = BoothPaymentStatus.Pending;
            job.PaymentReference = paymentReference;
            return job;
        }

        public BoothJob ConfirmPayment(BoothJob job, string paymentReference = null)
        {
            Transition(job, BoothJobStatus.PaymentConfirmed);
            job.PaymentStatus = BoothPaymentStatus.Confirmed;
            if (!string.IsNullOrWhiteSpace(paymentReference))
            {
                job.PaymentReference = paymentReference;
            }

            return job;
        }

        public BoothJob BypassPayment(BoothJob job)
        {
            Transition(job, BoothJobStatus.PaymentBypassed);
            job.PaymentStatus = BoothPaymentStatus.Bypassed;
            return job;
        }

        public BoothJob BeginCapture(BoothJob job)
        {
            Transition(job, BoothJobStatus.Capturing);
            return job;
        }

        public BoothJob BeginRetake(BoothJob job)
        {
            Transition(job, BoothJobStatus.Capturing);
            job.RawCaptureCount = 0;
            job.MotionClipFramePaths = null;
            job.MotionVideoPath = null;
            job.MotionVideoUrl = null;
            job.MotionClipUrl = null;
            job.Paths.RawImagePath = null;
            job.Paths.ComposedImagePath = null;
            job.Paths.PrintImagePath = null;
            job.Paths.ThumbnailPath = null;
            job.LastError = null;
            return job;
        }

        public BoothJob MarkCaptured(BoothJob job, int rawCaptureCount)
        {
            Transition(job, BoothJobStatus.Captured);
            job.RawCaptureCount = rawCaptureCount;
            return job;
        }

        public BoothJob BeginComposing(BoothJob job)
        {
            Transition(job, BoothJobStatus.Composing);
            return job;
        }

        public BoothJob MarkComposed(BoothJob job, string composedImagePath, string thumbnailPath)
        {
            Transition(job, BoothJobStatus.Composed);
            job.Paths.ComposedImagePath = composedImagePath;
            job.Paths.ThumbnailPath = thumbnailPath;
            return job;
        }

        public BoothJob MarkComposed(BoothJob job, string composedImagePath, string printImagePath, string thumbnailPath)
        {
            MarkComposed(job, composedImagePath, thumbnailPath);
            job.Paths.PrintImagePath = printImagePath;
            return job;
        }

        public BoothJob BeginPrinting(BoothJob job)
        {
            Transition(job, BoothJobStatus.Printing);
            job.PrintStatus = BoothPrintStatus.Printing;
            job.PrintAttempts += 1;
            job.LastPrintError = null;
            return job;
        }

        public BoothJob MarkPrintRetryWait(BoothJob job, string reason, string printerName = null)
        {
            Transition(job, BoothJobStatus.Composed);
            job.PrintStatus = BoothPrintStatus.RetryWait;
            job.LastPrintError = reason;
            job.RetryCount += 1;
            if (!string.IsNullOrWhiteSpace(printerName))
            {
                job.PrinterName = printerName;
            }

            return job;
        }

        public BoothJob MarkPrinted(BoothJob job)
        {
            Transition(job, BoothJobStatus.Printed);
            job.PrintStatus = BoothPrintStatus.Printed;
            job.LastPrintError = null;
            return job;
        }

        public BoothJob QueueUpload(BoothJob job)
        {
            Transition(job, BoothJobStatus.UploadPending);
            job.UploadStatus = BoothUploadStatus.Pending;
            job.LastUploadError = null;
            return job;
        }

        public BoothJob BeginUpload(BoothJob job)
        {
            Transition(job, BoothJobStatus.Uploading);
            job.UploadStatus = BoothUploadStatus.Uploading;
            job.UploadAttempts += 1;
            job.LastUploadError = null;
            return job;
        }

        public BoothJob MarkUploadRetryWait(BoothJob job, string reason)
        {
            Transition(job, BoothJobStatus.UploadPending);
            job.UploadStatus = BoothUploadStatus.RetryWait;
            job.LastUploadError = reason;
            job.RetryCount += 1;
            return job;
        }

        public BoothJob MarkUploaded(BoothJob job, string remoteAssetKey = null)
        {
            Transition(job, BoothJobStatus.Uploaded);
            job.UploadStatus = BoothUploadStatus.Uploaded;
            job.LastUploadError = null;
            if (!string.IsNullOrWhiteSpace(remoteAssetKey))
            {
                job.RemoteAssetKey = remoteAssetKey;
            }

            return job;
        }

        public BoothJob MarkLinkReady(BoothJob job, string downloadUrl)
        {
            Transition(job, BoothJobStatus.LinkReady);
            job.UploadStatus = BoothUploadStatus.LinkReady;
            job.DownloadUrl = downloadUrl;
            job.PublishedAtUtc = UtcNow();
            return job;
        }

        public BoothJob MarkDone(BoothJob job)
        {
            Transition(job, BoothJobStatus.Done);
            return job;
        }

        public BoothJob Cancel(BoothJob job, string reason = null)
        {
            Transition(job, BoothJobStatus.Cancelled);
            job.LastError = reason;
            return job;
        }

        public BoothJob Fail(BoothJob job, string reason)
        {
            Transition(job, BoothJobStatus.Failed);
            job.LastError = reason;

            if (job.UploadStatus == BoothUploadStatus.Uploading)
            {
                job.UploadStatus = BoothUploadStatus.FailedHard;
            }

            if (job.PrintStatus == BoothPrintStatus.Printing)
            {
                job.PrintStatus = BoothPrintStatus.FailedHard;
            }

            if (job.PaymentStatus == BoothPaymentStatus.Pending)
            {
                job.PaymentStatus = BoothPaymentStatus.Failed;
            }

            return job;
        }

        private static void Transition(BoothJob job, BoothJobStatus target)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (!AllowedTransitions.TryGetValue(job.Status, out var allowedTargets) || !allowedTargets.Contains(target))
            {
                throw new InvalidOperationException($"Invalid booth job transition: {job.Status} -> {target}");
            }

            job.Status = target;
            Touch(job);
        }

        private static void Touch(BoothJob job)
        {
            job.UpdatedAtUtc = UtcNow();
        }

        private static string UtcNow()
        {
            return DateTime.UtcNow.ToString("O");
        }
    }
}
