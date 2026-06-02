using System;

namespace PhotoBooth.Booth.Domain
{
    [Serializable]
    public sealed class BoothJob
    {
        public string JobId;
        public BoothJobStatus Status;
        public BoothPaymentStatus PaymentStatus;
        public BoothPrintStatus PrintStatus;
        public BoothUploadStatus UploadStatus;
        public string ThemeId;
        public string PassengerName;
        public string AiStyleId;
        public string AiStylePrompt;
        public string PaymentReference;
        public long AmountMinorUnits;
        public string CurrencyCode;
        public int RawCaptureCount;
        public int PrintAttempts;
        public int UploadAttempts;
        public BoothJobPaths Paths;
        public string DownloadUrl;
        public string MotionClipUrl;
        public string MotionVideoUrl;
        public string PrinterName;
        public string RemoteAssetKey;
        public string PublishedAtUtc;
        public int RetryCount;
        public string LastError;
        public string LastPrintError;
        public string LastUploadError;
        public string[] MotionClipFramePaths;
        public string MotionVideoPath;
        public string CreatedAtUtc;
        public string UpdatedAtUtc;
    }

    [Serializable]
    public sealed class BoothJobPaths
    {
        public string RootDirectory;
        public string RawDirectory;
        public string ComposedDirectory;
        public string ThumbsDirectory;
        public string LogsDirectory;
        public string SnapshotPath;
        public string RawImagePath;
        public string ComposedImagePath;
        public string PrintImagePath;
        public string ThumbnailPath;
    }
}
