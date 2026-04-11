using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Printing
{
    public sealed class SimulatedPrintHelperClient : IPrintHelperClient
    {
        private readonly int latencyMilliseconds;

        public SimulatedPrintHelperClient(int latencyMilliseconds = 150)
        {
            this.latencyMilliseconds = Math.Max(0, latencyMilliseconds);
        }

        public async Task<PrintJobResult> PrintAsync(PrintJobRequest request, CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ImagePath) || !File.Exists(request.ImagePath))
            {
                return new PrintJobResult
                {
                    Success = false,
                    Retryable = false,
                    Message = "Composed image file is missing.",
                    PrinterName = request.PrinterName
                };
            }

            if (latencyMilliseconds > 0)
            {
                await Task.Delay(latencyMilliseconds, cancellationToken);
            }

            return new PrintJobResult
            {
                Success = true,
                Retryable = false,
                Message = "Simulated print completed.",
                PrinterName = request.PrinterName,
                OperationId = Guid.NewGuid().ToString("N")
            };
        }
    }
}
