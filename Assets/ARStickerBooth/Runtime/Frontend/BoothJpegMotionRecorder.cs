using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class CanonPreviewFrame
    {
        public byte[] JpegBytes;
        public double TimestampSeconds;
    }

    public sealed class BoothMotionSegmentResult
    {
        public string[] FramePaths = Array.Empty<string>();
        public Task WriteTask = Task.CompletedTask;
    }

    /// <summary>
    /// Captures timestamped Canon EVF JPEG frames and writes a fixed-rate sequence
    /// away from Unity's main thread.
    /// </summary>
    public sealed class BoothJpegMotionRecorder
    {
        private readonly object sync = new();
        private readonly List<CanonPreviewFrame> samples = new();
        private readonly List<Task> pendingWrites = new();
        private CancellationTokenSource writeCancellation = new();

        private CanonPreviewFrame latestFrame;
        private bool recording;
        private double segmentStartedAt;
        private int targetFramesPerSecond;
        private int durationSeconds;

        public static double NowSeconds => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void BeginSegment(int framesPerSecond, int duration)
        {
            lock (sync)
            {
                if (recording)
                {
                    throw new InvalidOperationException("A motion segment is already recording.");
                }

                samples.Clear();
                targetFramesPerSecond = Math.Max(1, framesPerSecond);
                durationSeconds = Math.Max(1, duration);
                segmentStartedAt = NowSeconds;
                recording = true;
                if (latestFrame?.JpegBytes?.Length > 0)
                {
                    samples.Add(new CanonPreviewFrame
                    {
                        JpegBytes = latestFrame.JpegBytes,
                        TimestampSeconds = segmentStartedAt
                    });
                }
            }
        }

        public void AcceptFrame(CanonPreviewFrame frame)
        {
            if (frame?.JpegBytes == null || frame.JpegBytes.Length == 0)
            {
                return;
            }

            lock (sync)
            {
                latestFrame = frame;
                if (!recording || frame.TimestampSeconds < segmentStartedAt)
                {
                    return;
                }

                var maximumSamples = (targetFramesPerSecond * durationSeconds * 2) + 4;
                if (samples.Count < maximumSamples)
                {
                    samples.Add(frame);
                }
            }
        }

        public BoothMotionSegmentResult CompleteSegment(
            string outputDirectory,
            int startingFrameIndex,
            CancellationToken cancellationToken = default)
        {
            CanonPreviewFrame[] capturedSamples;
            int fps;
            int duration;
            double startedAt;
            lock (sync)
            {
                if (!recording)
                {
                    throw new InvalidOperationException("No motion segment is recording.");
                }

                recording = false;
                capturedSamples = samples
                    .Where(sample => sample?.JpegBytes?.Length > 0)
                    .OrderBy(sample => sample.TimestampSeconds)
                    .ToArray();
                fps = targetFramesPerSecond;
                duration = durationSeconds;
                startedAt = segmentStartedAt;
                samples.Clear();
            }

            if (capturedSamples.Length == 0)
            {
                throw new InvalidOperationException("Canon Live View produced no JPEG frames during countdown.");
            }

            var targetCount = Math.Max(1, fps * duration);
            var outputPaths = new string[targetCount];
            var outputFrames = new byte[targetCount][];
            var sampleIndex = 0;
            var selected = capturedSamples[0];
            for (var index = 0; index < targetCount; index += 1)
            {
                var targetTimestamp = startedAt + (index / (double)fps);
                while (sampleIndex + 1 < capturedSamples.Length
                       && capturedSamples[sampleIndex + 1].TimestampSeconds <= targetTimestamp)
                {
                    sampleIndex++;
                    selected = capturedSamples[sampleIndex];
                }

                outputFrames[index] = selected.JpegBytes;
                outputPaths[index] = Path.Combine(
                    outputDirectory,
                    $"motion_{Math.Max(0, startingFrameIndex + index):000}.jpg");
            }

            var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                writeCancellation.Token);
            var writeTask = Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(outputDirectory);
                    for (var index = 0; index < outputPaths.Length; index += 1)
                    {
                        linkedCancellation.Token.ThrowIfCancellationRequested();
                        File.WriteAllBytes(outputPaths[index], outputFrames[index]);
                    }
                }
                finally
                {
                    linkedCancellation.Dispose();
                }
            }, CancellationToken.None);

            lock (sync)
            {
                pendingWrites.Add(writeTask);
            }

            return new BoothMotionSegmentResult
            {
                FramePaths = outputPaths,
                WriteTask = writeTask
            };
        }

        public async Task WaitForWritesAsync(CancellationToken cancellationToken = default)
        {
            Task[] tasks;
            lock (sync)
            {
                tasks = pendingWrites.ToArray();
            }

            if (tasks.Length > 0)
            {
                await Task.WhenAll(tasks);
            }

            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                pendingWrites.RemoveAll(task => task.IsCompleted);
            }
        }

        public void Reset()
        {
            lock (sync)
            {
                writeCancellation.Cancel();
                writeCancellation.Dispose();
                writeCancellation = new CancellationTokenSource();
                recording = false;
                samples.Clear();
                latestFrame = null;
                pendingWrites.Clear();
            }
        }
    }
}
