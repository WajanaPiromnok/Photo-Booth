using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class GPhoto2CameraCaptureService
    {
        private const int AutoDetectAttemptCount = 3;
        private const int AutoDetectRetryDelayMs = 1000;

        private readonly SemaphoreSlim commandLock = new(1, 1);
        private readonly string executablePath;
        private readonly int timeoutMs;
        private readonly bool killPtpcameraBeforeCommand;

        public GPhoto2CameraCaptureService(string executablePath, int timeoutMs, bool killPtpcameraBeforeCommand)
        {
            this.executablePath = ResolveExecutablePath(executablePath);
            this.timeoutMs = Math.Max(1000, timeoutMs);
            this.killPtpcameraBeforeCommand = killPtpcameraBeforeCommand;
            Debug.Log($"gPhoto2 capture service configured: executable={this.executablePath}, timeoutMs={this.timeoutMs}, releaseMacCameraOwners={this.killPtpcameraBeforeCommand}");
        }

        public async Task<string> CaptureImageAndDownloadAsync(string outputDirectory, string fileName, CancellationToken cancellationToken = default)
        {
            await commandLock.WaitAsync(cancellationToken);
            try
            {
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    throw new ArgumentException("Output directory is required.", nameof(outputDirectory));
                }

                var resolvedFileName = string.IsNullOrWhiteSpace(fileName) ? "capture.jpg" : fileName.Trim();
                Directory.CreateDirectory(outputDirectory);
                var outputPath = Path.Combine(outputDirectory, resolvedFileName);

                var arguments = string.Join(
                    " ",
                    "--capture-image-and-download",
                    "--filename",
                    QuoteProcessArgument(outputPath),
                    "--force-overwrite");

                ProcessResult result = default;
                var maxAttempts = 3;
                var attemptDelayMs = 1200;

                for (var attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }

                    await ReleaseMacCameraOwnerProcessesAsync(cancellationToken);

                    result = await RunProcessAsync(executablePath, arguments, timeoutMs, $"gPhoto2 capture attempt {attempt}/{maxAttempts}", cancellationToken);
                    var errorText = result.Error?.Trim() ?? string.Empty;
                    var hasError = result.ExitCode != 0 
                                   || errorText.Contains("ERROR") 
                                   || errorText.Contains("Error") 
                                   || errorText.Contains("failed");

                    if (!hasError && File.Exists(outputPath) && new FileInfo(outputPath).Length > 0)
                    {
                        Debug.Log($"gPhoto2 capture saved: {outputPath}");
                        return outputPath;
                    }

                    if (attempt < maxAttempts)
                    {
                        Debug.LogWarning($"gPhoto2 capture attempt {attempt}/{maxAttempts} failed (exit={result.ExitCode}, error={errorText}). Retrying in {attemptDelayMs}ms...");
                        await Task.Delay(attemptDelayMs, cancellationToken);
                    }
                }

                var finalErrorText = result.Error?.Trim() ?? string.Empty;
                throw new InvalidOperationException(
                    $"gPhoto2 capture failed after {maxAttempts} attempts.\n" +
                    $"Error details: {(string.IsNullOrWhiteSpace(finalErrorText) ? "No error output" : finalErrorText)}\n" +
                    $"Output details: {(string.IsNullOrWhiteSpace(result.Output) ? "No standard output" : result.Output.Trim())}");
            }
            finally
            {
                commandLock.Release();
            }
        }

        public async Task<string> DetectCameraNameAsync(CancellationToken cancellationToken = default)
        {
            await commandLock.WaitAsync(cancellationToken);
            try
            {
                await ReleaseMacCameraOwnerProcessesAsync(cancellationToken);

                ProcessResult lastResult = default;
                for (var attempt = 1; attempt <= AutoDetectAttemptCount; attempt++)
                {
                    lastResult = await RunProcessAsync(
                        executablePath,
                        "--auto-detect",
                        Math.Min(timeoutMs, 8000),
                        $"gPhoto2 auto-detect attempt {attempt}/{AutoDetectAttemptCount}",
                        cancellationToken,
                        logSuccess: false);

                    if (lastResult.ExitCode == 0 && TryExtractDetectedCameraLine(lastResult.Output, out var detectedCameraLine))
                    {
                        Debug.Log($"gPhoto2 auto-detect detected camera: {detectedCameraLine}");
                        return detectedCameraLine;
                    }

                    var reason = lastResult.ExitCode == 0
                        ? "found no camera"
                        : $"failed with exit={lastResult.ExitCode}";
                    Debug.LogWarning($"gPhoto2 auto-detect attempt {attempt}/{AutoDetectAttemptCount} {reason}: output={CompactLogText(lastResult.Output)}, error={CompactLogText(lastResult.Error)}");

                    if (attempt < AutoDetectAttemptCount)
                    {
                        await Task.Delay(AutoDetectRetryDelayMs, cancellationToken);
                    }
                }

                Debug.LogWarning($"gPhoto2 auto-detect gave up after {AutoDetectAttemptCount} attempts: output={CompactLogText(lastResult.Output)}, error={CompactLogText(lastResult.Error)}");
                return null;
            }
            finally
            {
                commandLock.Release();
            }
        }

        public async Task<bool> CheckCameraAsync(CancellationToken cancellationToken = default)
        {
            return !string.IsNullOrWhiteSpace(await DetectCameraNameAsync(cancellationToken));
        }

        public async Task<byte[]> CapturePreviewBytesAsync(CancellationToken cancellationToken = default)
        {
            await commandLock.WaitAsync(cancellationToken);
            try
            {
                var result = await RunBinaryProcessAsync(
                    executablePath,
                    "--capture-preview --stdout",
                    Math.Min(timeoutMs, 8000),
                    "gPhoto2 capture preview",
                    cancellationToken);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException($"gPhoto2 capture preview failed: exit={result.ExitCode}, error={CompactLogText(result.Error)}");
                }

                if (result.Output == null || result.Output.Length == 0)
                {
                    throw new InvalidOperationException("gPhoto2 capture preview returned no data.");
                }

                return result.Output;
            }
            finally
            {
                commandLock.Release();
            }
        }

        public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        {
            await commandLock.WaitAsync(cancellationToken);
            commandLock.Release();
        }

        private static string ResolveExecutablePath(string configuredPath)
        {
            var trimmed = string.IsNullOrWhiteSpace(configuredPath) ? "/opt/homebrew/bin/gphoto2" : configuredPath.Trim();
            if (Path.IsPathRooted(trimmed) && File.Exists(trimmed))
            {
                return trimmed;
            }

            if (string.Equals(trimmed, "/opt/homebrew/bin/gphoto2", StringComparison.Ordinal)
                && File.Exists("/usr/local/bin/gphoto2"))
            {
                return "/usr/local/bin/gphoto2";
            }

            return string.Equals(trimmed, "/opt/homebrew/bin/gphoto2", StringComparison.Ordinal)
                ? "gphoto2"
                : trimmed;
        }

        private static bool IsMacOs()
        {
            return Application.platform == RuntimePlatform.OSXEditor
                || Application.platform == RuntimePlatform.OSXPlayer;
        }

        private async Task ReleaseMacCameraOwnerProcessesAsync(CancellationToken cancellationToken)
        {
            if (!killPtpcameraBeforeCommand || !IsMacOs())
            {
                return;
            }

            var killedAny = false;
            killedAny |= await RunProcessAllowingFailureAsync("/usr/bin/killall", QuoteProcessArgument("EOS Utility"), 3000, "Canon EOS Utility release", cancellationToken);
            killedAny |= await RunProcessAllowingFailureAsync("/usr/bin/killall", QuoteProcessArgument("EOS Utility 3"), 3000, "Canon EOS Utility 3 release", cancellationToken);
            killedAny |= await RunProcessAllowingFailureAsync("/usr/bin/killall", "PTPCamera", 2000, "macOS PTPCamera release", cancellationToken);

            if (killedAny)
            {
                Debug.Log("Waiting 1.5 seconds for macOS to release camera USB interfaces...");
                await Task.Delay(1500, cancellationToken);
            }
        }

        private static async Task<bool> RunProcessAllowingFailureAsync(string executablePath, string arguments, int timeoutMs, string label, CancellationToken cancellationToken)
        {
            try
            {
                var result = await Task.Run(() => RunProcess(executablePath, arguments, timeoutMs), cancellationToken);
                if (result.ExitCode != 0)
                {
                    var errorMsg = result.Error?.Trim() ?? string.Empty;
                    // Exit code 1 for killall on macOS indicates no matching processes were found.
                    // This is the expected/normal state, so we do not log it as a warning or noisy error.
                    if (result.ExitCode == 1 && (errorMsg.Contains("No matching processes") || string.IsNullOrEmpty(errorMsg)))
                    {
                        return false;
                    }

                    Debug.Log($"{label} skipped or not needed: exit={result.ExitCode}, error={errorMsg}");
                    return false;
                }

                Debug.Log($"{label} succeeded: {result.Output.Trim()}");
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.Log($"{label} skipped: {exception.Message}");
                return false;
            }
        }

        private static async Task<ProcessResult> RunProcessAsync(
            string executablePath,
            string arguments,
            int timeoutMs,
            string label,
            CancellationToken cancellationToken,
            bool logSuccess = true)
        {
            try
            {
                var result = await Task.Run(() => RunProcess(executablePath, arguments, timeoutMs), cancellationToken);
                if (result.ExitCode == 0)
                {
                    if (logSuccess)
                    {
                        Debug.Log($"{label} succeeded: {CompactLogText(result.Output)}");
                    }
                }
                else
                {
                    Debug.LogWarning($"{label} failed: exit={result.ExitCode}, output={CompactLogText(result.Output)}, error={CompactLogText(result.Error)}");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"{label} failed: {exception.Message}", exception);
            }
        }

        private static async Task<BinaryProcessResult> RunBinaryProcessAsync(
            string executablePath,
            string arguments,
            int timeoutMs,
            string label,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await Task.Run(() => RunBinaryProcess(executablePath, arguments, timeoutMs), cancellationToken);
                if (result.ExitCode == 0)
                {
                    // Debug.Log($"{label} succeeded: bytes={result.Output?.Length ?? 0}");
                }
                else
                {
                    // Debug.LogWarning($"{label} failed: exit={result.ExitCode}, error={CompactLogText(result.Error)}");
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException($"{label} failed: {exception.Message}", exception);
            }
        }

        private static ProcessResult RunProcess(string executablePath, string arguments, int timeoutMs)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var exited = process.WaitForExit(Math.Max(250, timeoutMs));
            if (!exited)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best effort: process may have exited between WaitForExit and Kill.
                }

                return new ProcessResult(124, string.Empty, "Timed out waiting for gPhoto2 command.");
            }

            return new ProcessResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd());
        }

        private static BinaryProcessResult RunBinaryProcess(string executablePath, string arguments, int timeoutMs)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = arguments ?? string.Empty,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            using var outputStream = new MemoryStream();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(outputStream);
            var stderrTask = process.StandardError.ReadToEndAsync();
            var exited = process.WaitForExit(Math.Max(250, timeoutMs));
            if (!exited)
            {
                try
                {
                    process.Kill();
                }
                catch
                {
                    // Best effort.
                }

                return new BinaryProcessResult(124, Array.Empty<byte>(), "Timed out waiting for gPhoto2 command.");
            }

            process.WaitForExit();
            try
            {
                Task.WaitAll(stdoutTask, stderrTask);
            }
            catch
            {
                // Preserve whatever output we already have.
            }

            return new BinaryProcessResult(
                process.ExitCode,
                outputStream.ToArray(),
                stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty);
        }

        private static string QuoteProcessArgument(string value)
        {
            return $"\"{(value ?? string.Empty).Replace("\"", "\\\"")}\"";
        }

        private static bool TryExtractDetectedCameraLine(string output, out string cameraLine)
        {
            cameraLine = string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                return false;
            }

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0
                    || trimmed.StartsWith("Model", StringComparison.OrdinalIgnoreCase)
                    || trimmed.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                if (trimmed.IndexOf("usb:", StringComparison.OrdinalIgnoreCase) >= 0
                    || trimmed.IndexOf("Canon", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var usbIndex = trimmed.IndexOf("usb:", StringComparison.OrdinalIgnoreCase);
                    cameraLine = usbIndex > 0
                        ? trimmed.Substring(0, usbIndex).Trim()
                        : trimmed;
                    return !string.IsNullOrWhiteSpace(cameraLine);
                }
            }

            return false;
        }

        private static string CompactLogText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            return value.Trim().Replace("\r", string.Empty).Replace("\n", " | ");
        }

        private readonly struct ProcessResult
        {
            public readonly int ExitCode;
            public readonly string Output;
            public readonly string Error;

            public ProcessResult(int exitCode, string output, string error)
            {
                ExitCode = exitCode;
                Output = output ?? string.Empty;
                Error = error ?? string.Empty;
            }
        }

        private readonly struct BinaryProcessResult
        {
            public readonly int ExitCode;
            public readonly byte[] Output;
            public readonly string Error;

            public BinaryProcessResult(int exitCode, byte[] output, string error)
            {
                ExitCode = exitCode;
                Output = output ?? Array.Empty<byte>();
                Error = error ?? string.Empty;
            }
        }
    }
}
