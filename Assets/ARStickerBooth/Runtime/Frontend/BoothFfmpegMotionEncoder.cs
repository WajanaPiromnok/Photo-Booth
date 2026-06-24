using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothFfmpegMotionEncoder
    {
        public Task<BoothMotionVideoResult> EncodeAsync(
            string ffmpegPath,
            string[] framePaths,
            string outputPath,
            float frameRate,
            int timeoutSeconds,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Encode(ffmpegPath, framePaths, outputPath, frameRate, timeoutSeconds, cancellationToken), cancellationToken);
        }

        private static BoothMotionVideoResult Encode(
            string ffmpegPath,
            string[] framePaths,
            string outputPath,
            float frameRate,
            int timeoutSeconds,
            CancellationToken cancellationToken)
        {
            if (framePaths == null || framePaths.Length == 0)
            {
                return BoothMotionVideoResult.Fail("No motion frames were available for MP4 encoding.");
            }

            var firstFrame = framePaths[0];
            if (string.IsNullOrWhiteSpace(firstFrame) || !File.Exists(firstFrame))
            {
                return BoothMotionVideoResult.Fail("The first motion frame was missing.");
            }

            var executable = string.IsNullOrWhiteSpace(ffmpegPath) ? "ffmpeg" : ffmpegPath.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetDirectoryName(firstFrame));

            var inputPattern = Path.Combine(Path.GetDirectoryName(firstFrame), "motion_%03d.png");
            var fps = Math.Max(1f, frameRate).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            var arguments = $"-y -framerate {fps} -i {Quote(inputPattern)} -vf \"scale=1280:-2:force_original_aspect_ratio=decrease,format=yuv420p\" -c:v libx264 -preset veryfast -crf 23 -movflags +faststart {Quote(outputPath)}";

            try
            {
                var outputBuilder = new StringBuilder();
                var errorBuilder = new StringBuilder();
                using var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                process.OutputDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        outputBuilder.AppendLine(args.Data);
                    }
                };
                process.ErrorDataReceived += (_, args) =>
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        errorBuilder.AppendLine(args.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                var timeoutMs = Math.Max(1, timeoutSeconds) * 1000;
                while (!process.WaitForExit(50))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    timeoutMs -= 50;
                    if (timeoutMs <= 0)
                    {
                        TryKill(process);
                        return BoothMotionVideoResult.Fail("FFmpeg timed out while encoding the motion clip.");
                    }
                }

                process.WaitForExit();
                var output = outputBuilder.ToString();
                var error = errorBuilder.ToString();
                if (process.ExitCode != 0 || !File.Exists(outputPath))
                {
                    return BoothMotionVideoResult.Fail(string.IsNullOrWhiteSpace(error) ? output : error);
                }

                return new BoothMotionVideoResult
                {
                    Success = true,
                    VideoPath = outputPath
                };
            }
            catch (Exception exception)
            {
                return BoothMotionVideoResult.Fail(exception.Message);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    public sealed class BoothMotionVideoResult
    {
        public bool Success;
        public string VideoPath;
        public string ErrorMessage;

        public static BoothMotionVideoResult Fail(string message)
        {
            return new BoothMotionVideoResult
            {
                Success = false,
                ErrorMessage = string.IsNullOrWhiteSpace(message) ? "Motion video encoding failed." : message.Trim()
            };
        }
    }
}
