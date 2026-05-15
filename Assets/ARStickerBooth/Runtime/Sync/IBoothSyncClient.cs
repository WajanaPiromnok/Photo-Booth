using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Sync
{
    public interface IBoothSyncClient
    {
        Task<RawCaptureUploadResult> UploadRawCaptureAsync(RawCaptureUploadRequest request, CancellationToken cancellationToken = default);
        Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default);
    }
}
