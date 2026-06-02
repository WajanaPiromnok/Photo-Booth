using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PhotoBooth.Booth.Sync
{
    public sealed class BoothRawCaptureUploadQueue
    {
        private sealed class QueueItem
        {
            public RawCaptureUploadRequest Request;
            public int Attempts;
        }

        private readonly BoothSyncService syncService;
        private readonly List<QueueItem> pending = new();
        private readonly SemaphoreSlim processingLock = new(1, 1);
        private readonly int maxAttempts;
        private readonly bool autoProcess;

        public BoothRawCaptureUploadQueue(BoothSyncService syncService, int maxAttempts = 3, bool autoProcess = true)
        {
            this.syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
            this.maxAttempts = Math.Max(1, maxAttempts);
            this.autoProcess = autoProcess;
        }

        public int PendingCount
        {
            get
            {
                lock (pending)
                {
                    return pending.Count;
                }
            }
        }

        public void Enqueue(RawCaptureUploadRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            lock (pending)
            {
                pending.Add(new QueueItem { Request = request });
            }

            if (autoProcess)
            {
                _ = ProcessAsync(CancellationToken.None);
            }
        }

        public Task FlushAsync(CancellationToken cancellationToken = default, string passengerName = null)
        {
            ApplyPassengerName(passengerName);
            return ProcessAsync(cancellationToken);
        }

        private void ApplyPassengerName(string passengerName)
        {
            if (string.IsNullOrWhiteSpace(passengerName))
            {
                return;
            }

            var normalizedName = passengerName.Trim().ToUpperInvariant();
            lock (pending)
            {
                foreach (var item in pending)
                {
                    if (item?.Request != null)
                    {
                        item.Request.PassengerName = normalizedName;
                    }
                }
            }
        }

        private async Task ProcessAsync(CancellationToken cancellationToken)
        {
            await processingLock.WaitAsync(cancellationToken);
            try
            {
                while (true)
                {
                    QueueItem item;
                    lock (pending)
                    {
                        if (pending.Count == 0)
                        {
                            return;
                        }

                        item = pending[0];
                    }

                    item.Attempts += 1;
                    RawCaptureUploadResult result;
                    try
                    {
                        result = await syncService.UploadRawCaptureAsync(item.Request, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        result = new RawCaptureUploadResult
                        {
                            Success = false,
                            Retryable = true,
                            Message = exception.Message
                        };
                    }

                    if (result.Success)
                    {
                        Debug.Log(BuildSuccessLog(item.Request, result));
                        Remove(item);
                        continue;
                    }

                    if (!result.Retryable || item.Attempts >= maxAttempts)
                    {
                        Debug.LogWarning($"Raw capture upload failed for {item.Request.JobId} capture {item.Request.CaptureIndex}: {result.Message}");
                        Remove(item);
                        continue;
                    }

                    Debug.LogWarning($"Raw capture upload will retry: job={item.Request.JobId}, capture={item.Request.CaptureIndex}, attempt={item.Attempts}, reason={result.Message}");
                    return;
                }
            }
            finally
            {
                processingLock.Release();
            }
        }

        private void Remove(QueueItem item)
        {
            lock (pending)
            {
                pending.Remove(item);
            }
        }

        private static string BuildSuccessLog(RawCaptureUploadRequest request, RawCaptureUploadResult result)
        {
            var link = !string.IsNullOrWhiteSpace(result.FileUrl)
                ? result.FileUrl
                : result.RemoteAssetKey;
            return $"Raw capture uploaded: job={request.JobId}, capture={request.CaptureIndex}/{request.CaptureTotal}, link={link}";
        }
    }
}
