using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Sync
{
    public interface IBoothSyncClient
    {
        Task<PreparedDownloadResult> PrepareDownloadAsync(PreparedDownloadRequest request, CancellationToken cancellationToken = default);
        Task<RawCaptureUploadResult> UploadRawCaptureAsync(RawCaptureUploadRequest request, CancellationToken cancellationToken = default);
        Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default);
    }
}
