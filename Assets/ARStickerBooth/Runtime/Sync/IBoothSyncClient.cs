using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Sync
{
    public interface IBoothSyncClient
    {
        Task<SyncJobResult> UploadAndPublishAsync(SyncJobRequest request, CancellationToken cancellationToken = default);
    }
}
