using PhotoBooth.Booth.Domain;

namespace PhotoBooth.Booth.Services
{
    public interface IBoothTelemetrySink
    {
        void OnJobCreated(BoothJob job);
        void OnThemeSelected(BoothJob job);
        void OnPaymentPending(BoothJob job);
        void OnPaymentConfirmed(BoothJob job);
        void OnCaptureCompleted(BoothJob job);
        void OnCompositionCompleted(BoothJob job);
        void OnPrintCompleted(BoothJob job);
        void OnDownloadLinkReady(BoothJob job);
        void OnJobCompleted(BoothJob job);
        void OnJobCancelled(BoothJob job, string reason);
        void OnJobFailed(BoothJob job, string reason);
    }
}
