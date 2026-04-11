using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Printing
{
    public interface IPrintHelperClient
    {
        Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default);
    }
}
