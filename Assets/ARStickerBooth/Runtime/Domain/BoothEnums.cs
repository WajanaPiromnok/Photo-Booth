namespace PhotoBooth.Booth.Domain
{
    public enum BoothJobStatus
    {
        Idle = 0,
        Created = 1,
        ThemeSelected = 2,
        PaymentPending = 3,
        PaymentConfirmed = 4,
        PaymentBypassed = 5,
        Capturing = 6,
        Captured = 7,
        Composing = 8,
        Composed = 9,
        Printing = 10,
        Printed = 11,
        UploadPending = 12,
        Uploading = 13,
        Uploaded = 14,
        LinkReady = 15,
        Done = 16,
        Cancelled = 17,
        Failed = 18
    }

    public enum BoothPaymentStatus
    {
        Unknown = 0,
        Pending = 1,
        Confirmed = 2,
        Failed = 3,
        Expired = 4,
        Bypassed = 5
    }

    public enum BoothPrintStatus
    {
        Pending = 0,
        Printing = 1,
        RetryWait = 2,
        Printed = 3,
        FailedHard = 4
    }

    public enum BoothUploadStatus
    {
        Pending = 0,
        Uploading = 1,
        RetryWait = 2,
        Uploaded = 3,
        LinkReady = 4,
        FailedHard = 5
    }
}
