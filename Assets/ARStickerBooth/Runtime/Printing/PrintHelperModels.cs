using System;

namespace PhotoBooth.Booth.Printing
{
    [Serializable]
    public sealed class PrintJobRequest
    {
        public string JobId;
        public string PrinterName;
        public string ImagePath;
        public string ThumbnailPath;
        public int Copies;
    }

    [Serializable]
    public sealed class PrintJobResult
    {
        public bool Success;
        public bool Retryable;
        public string Message;
        public string PrinterName;
        public string OperationId;
    }
}
