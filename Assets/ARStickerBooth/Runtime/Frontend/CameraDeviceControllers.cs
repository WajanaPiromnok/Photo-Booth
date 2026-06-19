using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace PhotoBooth.Booth.Frontend
{
    public enum CameraDeviceControllerKind
    {
        None,
        ObsbotCli,
        ExternalCommand
    }

    public interface ICameraDeviceController
    {
        Task WakeAsync(CancellationToken cancellationToken = default);
        Task SleepAsync(CancellationToken cancellationToken = default);
        Task StatusAsync(CancellationToken cancellationToken = default);
    }

    public sealed class NoopCameraDeviceController : ICameraDeviceController
    {
        public static readonly NoopCameraDeviceController Instance = new();

        private NoopCameraDeviceController()
        {
        }

        public Task WakeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SleepAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StatusAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class ObsbotCliCameraDeviceController : ICameraDeviceController
    {
        private readonly string executablePath;
        private readonly string deviceName;
        private readonly int timeoutMs;
        private bool loggedMissingExecutable;

        public ObsbotCliCameraDeviceController(string executablePath, string deviceName, int timeoutMs)
        {
            this.executablePath = executablePath ?? string.Empty;
            this.deviceName = deviceName ?? string.Empty;
            this.timeoutMs = Math.Max(250, timeoutMs);
        }

        public Task WakeAsync(CancellationToken cancellationToken = default)
        {
            return RunAsync("wake", cancellationToken);
        }

        public Task SleepAsync(CancellationToken cancellationToken = default)
        {
            return RunAsync("sleep", cancellationToken);
        }

        public Task StatusAsync(CancellationToken cancellationToken = default)
        {
            return RunAsync("status", cancellationToken);
        }

        private async Task RunAsync(string action, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                if (!loggedMissingExecutable)
                {
                    loggedMissingExecutable = true;
                    Debug.LogWarning($"Camera device control skipped; OBSBOT executable not found: {executablePath ?? "(empty)"}. Run scripts/build-obsbot-control.sh first.");
                }

                return;
            }

            var arguments = $"{QuoteProcessArgument(action)} --timeout-ms {timeoutMs}";
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                arguments += $" --device-name {QuoteProcessArgument(deviceName.Trim())}";
            }

            await CameraDeviceCommandRunner.RunProcessAsync(
                executablePath,
                arguments,
                timeoutMs + 2000,
                $"OBSBOT power control {action}",
                cancellationToken);
        }

        private static string QuoteProcessArgument(string value)
        {
            return $"\"{(value ?? string.Empty).Replace("\"", "\\\"")}\"";
        }
    }

    public sealed class ExternalCommandCameraDeviceController : ICameraDeviceController
    {
        private readonly string wakeCommand;
        private readonly string sleepCommand;
        private readonly string statusCommand;
        private readonly int timeoutMs;

        public ExternalCommandCameraDeviceController(string wakeCommand, string sleepCommand, string statusCommand, int timeoutMs)
        {
            this.wakeCommand = wakeCommand ?? string.Empty;
            this.sleepCommand = sleepCommand ?? string.Empty;
            this.statusCommand = statusCommand ?? string.Empty;
            this.timeoutMs = Math.Max(250, timeoutMs);
        }

        public Task WakeAsync(CancellationToken cancellationToken = default)
        {
            return RunShellCommandAsync("wake", wakeCommand, cancellationToken);
        }

        public Task SleepAsync(CancellationToken cancellationToken = default)
        {
            return RunShellCommandAsync("sleep", sleepCommand, cancellationToken);
        }

        public Task StatusAsync(CancellationToken cancellationToken = default)
        {
            return RunShellCommandAsync("status", statusCommand, cancellationToken);
        }

        private Task RunShellCommandAsync(string action, string command, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return Task.CompletedTask;
            }

            return CameraDeviceCommandRunner.RunShellCommandAsync(
                command,
                timeoutMs,
                $"Camera external {action}",
                cancellationToken);
        }
    }

    internal static class CameraDeviceCommandRunner
    {
        public static async Task RunShellCommandAsync(string command, int timeoutMs, string label, CancellationToken cancellationToken)
        {
            var shell = Application.platform == RuntimePlatform.WindowsPlayer
                || Application.platform == RuntimePlatform.WindowsEditor
                ? "cmd.exe"
                : "/bin/sh";
            var arguments = Application.platform == RuntimePlatform.WindowsPlayer
                || Application.platform == RuntimePlatform.WindowsEditor
                ? $"/C {command}"
                : $"-lc {QuoteShellArgument(command)}";
            await RunProcessAsync(shell, arguments, timeoutMs, label, cancellationToken);
        }

        public static async Task RunProcessAsync(string executablePath, string arguments, int timeoutMs, string label, CancellationToken cancellationToken)
        {
            try
            {
                var result = await Task.Run(() => RunProcess(executablePath, arguments, timeoutMs), cancellationToken);
                if (result.ExitCode == 0)
                {
                    Debug.Log($"{label} succeeded: {result.Output.Trim()}");
                    return;
                }

                Debug.LogWarning($"{label} failed: exit={result.ExitCode}, output={result.Output.Trim()}, error={result.Error.Trim()}");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"{label} failed: {exception.Message}");
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

                return new ProcessResult(124, string.Empty, "Timed out waiting for camera device command.");
            }

            return new ProcessResult(
                process.ExitCode,
                process.StandardOutput.ReadToEnd(),
                process.StandardError.ReadToEnd());
        }

        private static string QuoteShellArgument(string value)
        {
            return $"\"{(value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`")}\"";
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
    }
}
