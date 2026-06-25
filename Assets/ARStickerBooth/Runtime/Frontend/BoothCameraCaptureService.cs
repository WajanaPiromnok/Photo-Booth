using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothCameraCaptureService : IDisposable
    {
        private const int GPhoto2PreviewFailureThreshold = 3;
        private const int MotionFrameJpegTargetBytes = 100 * 1024;
        private const int MotionFrameJpegMaxWidth = 960;
        private const int MotionFrameJpegMaxQuality = 72;
        private const int MotionFrameJpegMinQuality = 45;
        private const string ObsbotDeviceName = "OBSBOT";
        private static readonly string[] CaptureCardNameParts =
        {
            "Acasis",
            "HD33",
            "HDMI",
            "UVC",
            "USB Video",
            "Video Capture",
            "Capture"
        };
        private static readonly string[] CanonCameraNameParts =
        {
            "EOS",
            "Canon",
            "EOS Webcam",
            "Cam Link"
        };

        private static readonly string[] VirtualCameraNameParts =
        {
            "OBS Virtual",
            "OBS Camera"
        };

        private static readonly string[] BuiltInCameraNameParts =
        {
            "FaceTime",
            "Built-in",
            "iSight",
            "MacBook"
        };

        private enum PreviewBackendKind
        {
            None = 0,
            GPhoto2Preview = 1,
            WebCamTexture = 2,
            Simulated = 3,
            CanonEdsdk = 4
        }

        private readonly RawImage previewTarget;
        private readonly int requestedWidth;
        private readonly int requestedHeight;
        private readonly int requestedFps;
        private readonly string[] preferredDeviceNames;
        private readonly int preferredDeviceDiscoveryTimeoutSeconds;
        private readonly bool useGPhoto2Preview;
        private readonly int gPhoto2PreviewFramesPerSecond;
        private readonly bool gPhoto2PreviewFailureFallbackEnabled;
        private readonly GPhoto2CameraCaptureService gPhoto2CaptureService;
        private readonly ICanonCameraBackend canonCameraBackend;
        private readonly bool useCanonEdsdk;
        private readonly bool allowCameraFallback;
        private readonly int canonPreviewUiFramesPerSecond;
        private readonly SemaphoreSlim previewLifecycleLock = new(1, 1);

        private WebCamTexture cameraTexture;
        private Texture2D simulatedCameraTexture;
        private Texture2D gPhoto2PreviewTexture;
        private CancellationTokenSource gPhoto2PreviewLoopCancellation;
        private Task gPhoto2PreviewLoopTask;
        private CancellationTokenSource canonPreviewLoopCancellation;
        private Task canonPreviewLoopTask;
        private PreviewBackendKind currentPreviewBackend = PreviewBackendKind.None;
        private bool gPhoto2PreviewPaused;
        private double lastCanonPreviewUiUpdateAt;
        private byte[] latestCanonPreviewBytes;
        private int latestCanonPreviewVersion;
        private int appliedCanonPreviewVersion;
        private Exception pendingCanonPreviewFailure;
        private string currentDeviceSource = "none";

        public BoothCameraCaptureService(
            RawImage previewTarget,
            int requestedWidth = 1280,
            int requestedHeight = 720,
            int requestedFps = 30,
            IEnumerable<string> preferredDeviceNames = null,
            int preferredDeviceDiscoveryTimeoutSeconds = 3,
            GPhoto2CameraCaptureService gPhoto2CaptureService = null,
            bool useGPhoto2Preview = false,
            int gPhoto2PreviewFramesPerSecond = 3,
            bool gPhoto2PreviewFailureFallbackEnabled = true,
            ICanonCameraBackend canonCameraBackend = null,
            bool useCanonEdsdk = false,
            bool allowCameraFallback = true,
            int canonPreviewUiFramesPerSecond = 10)
        {
            this.previewTarget = previewTarget;
            this.requestedWidth = Math.Max(320, requestedWidth);
            this.requestedHeight = Math.Max(240, requestedHeight);
            this.requestedFps = Math.Max(15, requestedFps);
            this.preferredDeviceNames = NormalizePreferredDeviceNames(preferredDeviceNames);
            this.preferredDeviceDiscoveryTimeoutSeconds = Math.Max(0, preferredDeviceDiscoveryTimeoutSeconds);
            this.gPhoto2CaptureService = gPhoto2CaptureService;
            this.useGPhoto2Preview = useGPhoto2Preview;
            this.gPhoto2PreviewFramesPerSecond = Mathf.Clamp(gPhoto2PreviewFramesPerSecond, 1, 30);
            this.gPhoto2PreviewFailureFallbackEnabled = gPhoto2PreviewFailureFallbackEnabled;
            this.canonCameraBackend = canonCameraBackend;
            this.useCanonEdsdk = useCanonEdsdk;
            this.allowCameraFallback = allowCameraFallback;
            this.canonPreviewUiFramesPerSecond = Mathf.Clamp(canonPreviewUiFramesPerSecond, 1, 15);
        }

        public bool IsPreviewing => (cameraTexture != null && cameraTexture.isPlaying) || gPhoto2PreviewTexture != null || simulatedCameraTexture != null;

        public bool IsUsingGPhoto2Preview => currentPreviewBackend == PreviewBackendKind.GPhoto2Preview;

        public bool IsUsingCanonEdsdk => currentPreviewBackend == PreviewBackendKind.CanonEdsdk;

        public int CurrentWidth => cameraTexture != null && cameraTexture.width > 16
            ? cameraTexture.width
            : gPhoto2PreviewTexture != null && gPhoto2PreviewTexture.width > 16
                ? gPhoto2PreviewTexture.width
                : simulatedCameraTexture != null
                    ? simulatedCameraTexture.width
                    : requestedWidth;

        public int CurrentHeight => cameraTexture != null && cameraTexture.height > 16
            ? cameraTexture.height
            : gPhoto2PreviewTexture != null && gPhoto2PreviewTexture.height > 16
                ? gPhoto2PreviewTexture.height
                : simulatedCameraTexture != null
                    ? simulatedCameraTexture.height
                    : requestedHeight;

        public Texture CurrentPreviewTexture => cameraTexture != null
            ? cameraTexture
            : gPhoto2PreviewTexture != null
                ? gPhoto2PreviewTexture
                : simulatedCameraTexture;

        public string CurrentDeviceName { get; private set; }

        public string CurrentDeviceSource => currentDeviceSource;

        public event Action<Exception> PreviewFailed;
        public event Action<CanonPreviewFrame> PreviewFrameReceived;

        public async Task StartPreviewAsync(CancellationToken cancellationToken = default)
        {
            await previewLifecycleLock.WaitAsync(cancellationToken);
            try
            {
                if (IsPreviewing)
                {
                    return;
                }

                if (useCanonEdsdk)
                {
                    try
                    {
                        await StartCanonPreviewAsync(cancellationToken);
                        return;
                    }
                    catch when (allowCameraFallback)
                    {
                        await StopCanonPreviewAsync(CancellationToken.None);
                        Debug.LogWarning("Canon EDSDK preview unavailable; using configured fallback camera.");
                    }
                }

                if (useGPhoto2Preview)
                {
                    try
                    {
                        if (await TryStartGPhoto2PreviewAsync(cancellationToken))
                        {
                            return;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning($"gPhoto2 preview unavailable; falling back to webcam preview. {exception.Message}");
                    }
                }

                await StartWebCamOrSimulatedPreviewAsync(cancellationToken);
            }
            finally
            {
                previewLifecycleLock.Release();
            }
        }

        public async Task PausePreviewAsync(CancellationToken cancellationToken = default)
        {
            if (!IsUsingGPhoto2Preview || gPhoto2CaptureService == null)
            {
                return;
            }

            gPhoto2PreviewPaused = true;
            await gPhoto2CaptureService.WaitForIdleAsync(cancellationToken);
        }

        public void ResumePreview()
        {
            gPhoto2PreviewPaused = false;
        }

        public Task<string> CaptureCanonStillAsync(
            string outputDirectory,
            string fileName,
            CancellationToken cancellationToken = default)
        {
            if (!IsUsingCanonEdsdk || canonCameraBackend == null)
            {
                throw new InvalidOperationException("Canon EDSDK camera is not active.");
            }

            return canonCameraBackend.CaptureStillAsync(outputDirectory, fileName, cancellationToken);
        }

        public Task AutoFocusCanonAsync(CancellationToken cancellationToken = default)
        {
            if (!IsUsingCanonEdsdk || canonCameraBackend == null)
            {
                return Task.CompletedTask;
            }

            return canonCameraBackend.AutoFocusAsync(cancellationToken);
        }


        public Texture2D CaptureCurrentFrameTexture()
        {
            EnsureReady();
            var sourceTexture = CurrentPreviewTexture;
            var texture = CreateReadableCopy(sourceTexture);
            if (texture == null)
            {
                throw new InvalidOperationException("Camera preview is not ready.");
            }

            return texture;
        }

        public void CaptureCurrentFrameTexture(ref Texture2D destination)
        {
            EnsureReady();
            var sourceTexture = CurrentPreviewTexture;
            if (sourceTexture == null)
            {
                throw new InvalidOperationException("Camera preview is not ready.");
            }

            var sourceWidth = sourceTexture.width;
            var sourceHeight = sourceTexture.height;
            if (sourceWidth <= 16 || sourceHeight <= 16)
            {
                throw new InvalidOperationException("Camera preview dimensions are invalid.");
            }

            if (destination == null || destination.width != sourceWidth || destination.height != sourceHeight)
            {
                if (destination != null)
                {
                    UnityEngine.Object.Destroy(destination);
                }
                destination = new Texture2D(sourceWidth, sourceHeight, TextureFormat.RGBA32, false);
                destination.name = "CachedCameraFrame";
            }

            if (sourceTexture is WebCamTexture webcamTexture)
            {
                destination.SetPixels32(webcamTexture.GetPixels32());
            }
            else if (sourceTexture is Texture2D texture2D)
            {
                destination.SetPixels32(texture2D.GetPixels32());
            }
            else
            {
                throw new InvalidOperationException("Unsupported camera preview texture type.");
            }

            destination.Apply(false, false);
        }

        public string CapturePng(string outputDirectory, string fileName = "capture.png")
        {
            return CaptureCurrentFramePng(outputDirectory, fileName, null);
        }

        public string CaptureMotionFramePng(string outputDirectory, int frameIndex)
        {
            return CaptureCurrentFramePng(outputDirectory, $"motion_{Math.Max(0, frameIndex):000}.png", null);
        }

        public string CapturePng(string outputDirectory, string fileName, Action<Texture2D> beforeEncode)
        {
            return CaptureCurrentFramePng(outputDirectory, fileName, beforeEncode);
        }

        public string CaptureMotionFramePng(string outputDirectory, int frameIndex, Action<Texture2D> beforeEncode)
        {
            return CaptureCurrentFramePng(outputDirectory, $"motion_{Math.Max(0, frameIndex):000}.png", beforeEncode);
        }

        public string CaptureMotionFrameJpeg(string outputDirectory, int frameIndex, Action<Texture2D> beforeEncode)
        {
            return CaptureCurrentFrameJpeg(outputDirectory, $"motion_{Math.Max(0, frameIndex):000}.jpg", beforeEncode);
        }

        private string CaptureCurrentFramePng(string outputDirectory, string fileName, Action<Texture2D> beforeEncode)
        {
            EnsureReady();

            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, fileName);
            var texture = CaptureCurrentFrameTexture();
            try
            {
                beforeEncode?.Invoke(texture);
                File.WriteAllBytes(outputPath, ImageConversion.EncodeToPNG(texture));
                return outputPath;
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        private string CaptureCurrentFrameJpeg(string outputDirectory, string fileName, Action<Texture2D> beforeEncode)
        {
            EnsureReady();

            Directory.CreateDirectory(outputDirectory);
            var outputPath = Path.Combine(outputDirectory, fileName);
            var texture = CaptureCurrentFrameTexture();
            Texture2D encodedTexture = null;
            try
            {
                beforeEncode?.Invoke(texture);
                encodedTexture = ResizeMotionFrameForUpload(texture);
                File.WriteAllBytes(outputPath, EncodeMotionFrameJpeg(encodedTexture));
                return outputPath;
            }
            finally
            {
                if (encodedTexture != null && encodedTexture != texture)
                {
                    UnityEngine.Object.Destroy(encodedTexture);
                }

                UnityEngine.Object.Destroy(texture);
            }
        }

        private static Texture2D ResizeMotionFrameForUpload(Texture2D source)
        {
            if (source == null || source.width <= MotionFrameJpegMaxWidth)
            {
                return source;
            }

            var targetWidth = MotionFrameJpegMaxWidth;
            var targetHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * (targetWidth / (float)source.width)));
            var previousActive = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                var resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
                resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                resized.Apply(false, false);
                return resized;
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static byte[] EncodeMotionFrameJpeg(Texture2D texture)
        {
            byte[] smallest = null;
            for (var quality = MotionFrameJpegMaxQuality; quality >= MotionFrameJpegMinQuality; quality -= 6)
            {
                var bytes = ImageConversion.EncodeToJPG(texture, quality);
                if (smallest == null || bytes.Length < smallest.Length)
                {
                    smallest = bytes;
                }

                if (bytes.Length <= MotionFrameJpegTargetBytes)
                {
                    return bytes;
                }
            }

            return smallest ?? ImageConversion.EncodeToJPG(texture, MotionFrameJpegMinQuality);
        }

        private void EnsureReady()
        {
            if (!IsPreviewing)
            {
                throw new InvalidOperationException("Camera preview is not ready.");
            }
        }

        public void StopPreview()
        {
            StopPreviewTextures();
            _ = CloseCanonSessionSafelyAsync();
        }

        public async Task StopPreviewAsync(CancellationToken cancellationToken = default)
        {
            StopPreviewTextures();
            await StopCanonPreviewAsync(cancellationToken);
        }

        private void StopPreviewTextures()
        {
            var stoppedDeviceName = CurrentDeviceName;
            var wasPreviewing = IsPreviewing;
            StopGPhoto2Preview();
            StopCanonPreviewLoop();
            if (previewTarget != null)
            {
                previewTarget.texture = null;
            }

            StopCameraTextureOnly();
            if (simulatedCameraTexture != null)
            {
                UnityEngine.Object.Destroy(simulatedCameraTexture);
                simulatedCameraTexture = null;
            }

            CurrentDeviceName = null;
            currentDeviceSource = "none";
            currentPreviewBackend = PreviewBackendKind.None;
            if (wasPreviewing)
            {
                Debug.Log($"PhotoBooth camera stopped: device={stoppedDeviceName ?? "(unknown)"}");
            }
        }

        private async Task CloseCanonSessionSafelyAsync()
        {
            try
            {
                await StopCanonPreviewAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Canon camera cleanup failed: {exception.Message}");
            }
        }

        private void StopCameraTextureOnly()
        {
            if (cameraTexture == null)
            {
                return;
            }

            if (cameraTexture.isPlaying)
            {
                cameraTexture.Stop();
            }

            UnityEngine.Object.Destroy(cameraTexture);
            cameraTexture = null;
        }

        private void StartSimulatedPreview()
        {
            StopCameraTextureOnly();
            StopGPhoto2Preview();
            CurrentDeviceName = "Simulated Camera";
            currentDeviceSource = "simulated";
            currentPreviewBackend = PreviewBackendKind.Simulated;
            simulatedCameraTexture = CreateSimulatedCameraTexture(requestedWidth, requestedHeight);
            if (previewTarget != null)
            {
                previewTarget.texture = simulatedCameraTexture;
                previewTarget.color = Color.white;
                previewTarget.uvRect = new Rect(0f, 0f, 1f, 1f);
            }
        }

        private async Task StartWebCamOrSimulatedPreviewAsync(CancellationToken cancellationToken)
        {
            if (currentPreviewBackend == PreviewBackendKind.GPhoto2Preview || gPhoto2PreviewTexture != null)
            {
                StopGPhoto2Preview();
            }

            if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
            {
                var authorization = Application.RequestUserAuthorization(UserAuthorization.WebCam);
                while (!authorization.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }

                if (!Application.HasUserAuthorization(UserAuthorization.WebCam))
                {
                    Debug.LogWarning("Webcam permission was not granted. Using simulated camera preview.");
                    StartSimulatedPreview();
                    return;
                }
            }

            var selectedDeviceName = await WaitForCameraDeviceNameAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(selectedDeviceName))
            {
                Debug.LogWarning("No camera device was found. Using simulated camera preview.");
                StartSimulatedPreview();
                return;
            }

            CurrentDeviceName = selectedDeviceName;
            currentDeviceSource = "camera";
            currentPreviewBackend = PreviewBackendKind.WebCamTexture;
            Debug.Log($"PhotoBooth camera selected: device={selectedDeviceName}, source={currentDeviceSource}");
            cameraTexture = new WebCamTexture(selectedDeviceName, requestedWidth, requestedHeight, requestedFps);
            if (previewTarget != null)
            {
                previewTarget.texture = cameraTexture;
                previewTarget.color = Color.white;
                previewTarget.uvRect = IsHd33DeviceName(selectedDeviceName)
                    ? new Rect(0f, 0f, 1f, 1f)
                    : new Rect(1f, 0f, -1f, 1f);
            }

            cameraTexture.Play();
            var startedAt = Time.realtimeSinceStartup;
            while (cameraTexture.width <= 16)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartup - startedAt > 5f)
                {
                    Debug.LogWarning($"Camera preview did not become ready within 5 seconds: device={CurrentDeviceName}, source={currentDeviceSource}. Using simulated camera preview.");
                    StopCameraTextureOnly();
                    StartSimulatedPreview();
                    return;
                }

                await Task.Yield();
            }

            Debug.Log($"PhotoBooth camera preview ready: device={CurrentDeviceName}, source={currentDeviceSource}, texture={CurrentWidth}x{CurrentHeight}");
        }

        private async Task<bool> TryStartGPhoto2PreviewAsync(CancellationToken cancellationToken)
        {
            if (gPhoto2CaptureService == null)
            {
                return false;
            }

            for (var attempt = 1; attempt <= GPhoto2PreviewFailureThreshold; attempt++)
            {
                try
                {
                    var detectedCameraName = await gPhoto2CaptureService.DetectCameraNameAsync(cancellationToken);
                    if (string.IsNullOrWhiteSpace(detectedCameraName))
                    {
                        Debug.LogWarning($"gPhoto2 preview start attempt {attempt}/{GPhoto2PreviewFailureThreshold} could not detect a camera.");
                        StopGPhoto2Preview();
                        if (attempt < GPhoto2PreviewFailureThreshold)
                        {
                            await Task.Delay(1000, cancellationToken);
                        }

                        continue;
                    }

                    currentPreviewBackend = PreviewBackendKind.GPhoto2Preview;
                    currentDeviceSource = "gphoto2-preview";
                    CurrentDeviceName = detectedCameraName;
                    gPhoto2PreviewPaused = false;
                    gPhoto2PreviewTexture ??= CreatePreviewTexture();

                    if (await RefreshGPhoto2PreviewFrameAsync(cancellationToken))
                    {
                        ApplyPreviewTexture(gPhoto2PreviewTexture, flipHorizontally: false);
                        Debug.Log($"PhotoBooth camera selected: device={CurrentDeviceName}, source={currentDeviceSource}");
                        Debug.Log($"PhotoBooth camera preview ready: device={CurrentDeviceName}, source={currentDeviceSource}, texture={CurrentWidth}x{CurrentHeight}");

                        gPhoto2PreviewLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        gPhoto2PreviewLoopTask = RunGPhoto2PreviewLoopAsync(gPhoto2PreviewLoopCancellation.Token);
                        return true;
                    }

                    StopGPhoto2Preview();
                    if (attempt < GPhoto2PreviewFailureThreshold)
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"gPhoto2 preview start attempt {attempt}/{GPhoto2PreviewFailureThreshold} failed: {exception.Message}");
                    StopGPhoto2Preview();
                    if (attempt < GPhoto2PreviewFailureThreshold)
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
            }

            return false;
        }

        private async Task StartCanonPreviewAsync(CancellationToken cancellationToken)
        {
            if (canonCameraBackend == null)
            {
                throw new InvalidOperationException("Canon EDSDK backend is not configured.");
            }

            currentPreviewBackend = PreviewBackendKind.CanonEdsdk;
            currentDeviceSource = "canon-edsdk";
            gPhoto2PreviewTexture ??= CreatePreviewTexture();
            await canonCameraBackend.StartLiveViewAsync(cancellationToken);
            CurrentDeviceName = string.IsNullOrWhiteSpace(canonCameraBackend.CameraName)
                ? "Canon EDSDK Camera"
                : canonCameraBackend.CameraName;

            canonPreviewLoopCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var firstFrameReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            canonPreviewLoopTask = Task.Run(
                () => RunCanonPreviewLoopAsync(firstFrameReady, canonPreviewLoopCancellation.Token),
                CancellationToken.None);

            var completed = await Task.WhenAny(firstFrameReady.Task, Task.Delay(5000, cancellationToken));
            if (completed != firstFrameReady.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException(
                    "Canon Live View did not become ready within 5 seconds.",
                    Interlocked.Exchange(ref pendingCanonPreviewFailure, null));
            }

            await firstFrameReady.Task;
            UpdateCanonPreviewTexture(force: true);
            ApplyPreviewTexture(gPhoto2PreviewTexture, flipHorizontally: false);
            Debug.Log($"PhotoBooth Canon preview ready: device={CurrentDeviceName}, texture={CurrentWidth}x{CurrentHeight}");
        }

        private async Task RunCanonPreviewLoopAsync(
            TaskCompletionSource<bool> firstFrameReady,
            CancellationToken cancellationToken)
        {
            var delayMs = Math.Max(1, (int)Math.Round(1000d / Math.Max(1, Math.Min(requestedFps, 30))));
            var consecutiveFailures = 0;
            while (!cancellationToken.IsCancellationRequested && currentPreviewBackend == PreviewBackendKind.CanonEdsdk)
            {
                try
                {
                    var startedAt = BoothJpegMotionRecorder.NowSeconds;
                    var bytes = await canonCameraBackend.DownloadLiveViewFrameAsync(cancellationToken).ConfigureAwait(false);
                    if (bytes != null
                        && bytes.Length > 0
                        && currentPreviewBackend == PreviewBackendKind.CanonEdsdk)
                    {
                        var timestamp = BoothJpegMotionRecorder.NowSeconds;
                        Interlocked.Exchange(ref latestCanonPreviewBytes, bytes);
                        Interlocked.Increment(ref latestCanonPreviewVersion);
                        PreviewFrameReceived?.Invoke(new CanonPreviewFrame
                        {
                            JpegBytes = bytes,
                            TimestampSeconds = timestamp
                        });
                        firstFrameReady.TrySetResult(true);
                    }

                    consecutiveFailures = 0;
                    var elapsedMs = (BoothJpegMotionRecorder.NowSeconds - startedAt) * 1000d;
                    var remainingDelayMs = Math.Max(1, delayMs - (int)Math.Round(elapsedMs));
                    await Task.Delay(remainingDelayMs, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    firstFrameReady.TrySetCanceled();
                    break;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures >= 3)
                    {
                        Interlocked.Exchange(ref pendingCanonPreviewFailure, exception);
                        firstFrameReady.TrySetException(exception);
                        break;
                    }

                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        public void UpdateCanonPreviewTexture(bool force = false)
        {
            var failure = Interlocked.Exchange(ref pendingCanonPreviewFailure, null);
            if (failure != null)
            {
                Debug.LogError($"Canon EDSDK Live View failed: {failure.Message}");
                PreviewFailed?.Invoke(failure);
            }

            if (currentPreviewBackend != PreviewBackendKind.CanonEdsdk || gPhoto2PreviewTexture == null)
            {
                return;
            }

            var version = Volatile.Read(ref latestCanonPreviewVersion);
            if (version == 0 || (!force && version == appliedCanonPreviewVersion))
            {
                return;
            }

            var timestamp = BoothJpegMotionRecorder.NowSeconds;
            var uiInterval = 1d / canonPreviewUiFramesPerSecond;
            if (!force && timestamp - lastCanonPreviewUiUpdateAt < uiInterval)
            {
                return;
            }

            var bytes = Volatile.Read(ref latestCanonPreviewBytes);
            if (bytes == null || bytes.Length == 0)
            {
                return;
            }

            lastCanonPreviewUiUpdateAt = timestamp;
            if (ImageConversion.LoadImage(gPhoto2PreviewTexture, bytes, false))
            {
                appliedCanonPreviewVersion = version;
            }
        }

        private void StopCanonPreviewLoop()
        {
            canonPreviewLoopCancellation?.Cancel();
            canonPreviewLoopCancellation?.Dispose();
            canonPreviewLoopCancellation = null;
            canonPreviewLoopTask = null;
            Interlocked.Exchange(ref latestCanonPreviewBytes, null);
            Interlocked.Exchange(ref pendingCanonPreviewFailure, null);
            latestCanonPreviewVersion = 0;
            appliedCanonPreviewVersion = 0;
            lastCanonPreviewUiUpdateAt = 0d;
        }

        private async Task StopCanonPreviewAsync(CancellationToken cancellationToken)
        {
            StopCanonPreviewLoop();
            if (canonCameraBackend == null)
            {
                return;
            }

            await canonCameraBackend.StopLiveViewAsync(cancellationToken);
            await canonCameraBackend.CloseSessionAsync(cancellationToken);
        }

        private async Task RunGPhoto2PreviewLoopAsync(CancellationToken cancellationToken)
        {
            var delayMs = Mathf.RoundToInt(1000f / Mathf.Clamp(gPhoto2PreviewFramesPerSecond, 1, 30));
            var consecutiveFailures = 0;

            while (!cancellationToken.IsCancellationRequested && currentPreviewBackend == PreviewBackendKind.GPhoto2Preview)
            {
                if (gPhoto2PreviewPaused)
                {
                    await Task.Delay(Mathf.Max(delayMs, 33), cancellationToken);
                    continue;
                }

                try
                {
                    if (await RefreshGPhoto2PreviewFrameAsync(cancellationToken))
                    {
                        consecutiveFailures = 0;
                    }
                    else
                    {
                        consecutiveFailures++;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    consecutiveFailures++;
                    Debug.LogWarning($"gPhoto2 preview frame failed: {exception.Message}");
                }

                if (consecutiveFailures >= GPhoto2PreviewFailureThreshold)
                {
                    break;
                }

                await Task.Delay(Mathf.Max(1, delayMs), cancellationToken);
            }

            if (!cancellationToken.IsCancellationRequested
                && currentPreviewBackend == PreviewBackendKind.GPhoto2Preview
                && gPhoto2PreviewFailureFallbackEnabled
                && !gPhoto2PreviewPaused)
            {
                Debug.LogWarning("gPhoto2 preview failed repeatedly; falling back to webcam preview.");
                try
                {
                    await StartWebCamOrSimulatedPreviewAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"gPhoto2 preview fallback failed: {exception.Message}");
                    StartSimulatedPreview();
                }
            }
        }

        private async Task<bool> RefreshGPhoto2PreviewFrameAsync(CancellationToken cancellationToken)
        {
            if (gPhoto2CaptureService == null || currentPreviewBackend != PreviewBackendKind.GPhoto2Preview || gPhoto2PreviewTexture == null)
            {
                return false;
            }

            var previewBytes = await gPhoto2CaptureService.CapturePreviewBytesAsync(cancellationToken);
            if (previewBytes == null || previewBytes.Length == 0 || currentPreviewBackend != PreviewBackendKind.GPhoto2Preview)
            {
                return false;
            }

            return ImageConversion.LoadImage(gPhoto2PreviewTexture, previewBytes);
        }

        private Texture2D CreatePreviewTexture()
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            texture.name = "GPhoto2PreviewTexture";
            return texture;
        }

        private static Texture2D CreateReadableCopy(Texture sourceTexture)
        {
            try
            {
                if (sourceTexture == null)
                {
                    return null;
                }

                var sourceWidth = sourceTexture.width;
                var sourceHeight = sourceTexture.height;
                if (sourceWidth <= 0 || sourceHeight <= 0)
                {
                    return null;
                }

                var texture = new Texture2D(sourceWidth, sourceHeight, TextureFormat.RGBA32, false);
                if (sourceTexture is WebCamTexture webcamTexture && webcamTexture.width > 16)
                {
                    texture.SetPixels32(webcamTexture.GetPixels32());
                }
                else if (sourceTexture is Texture2D texture2D)
                {
                    texture.SetPixels32(texture2D.GetPixels32());
                }
                else
                {
                    UnityEngine.Object.Destroy(texture);
                    return null;
                }

                texture.Apply(false, false);
                return texture;
            }
            catch
            {
                return null;
            }
        }

        private void ApplyPreviewTexture(Texture texture, bool flipHorizontally)
        {
            if (previewTarget == null)
            {
                return;
            }

            previewTarget.texture = texture;
            previewTarget.color = Color.white;
            previewTarget.uvRect = flipHorizontally
                ? new Rect(1f, 0f, -1f, 1f)
                : new Rect(0f, 0f, 1f, 1f);
        }

        private void StopGPhoto2Preview()
        {
            gPhoto2PreviewLoopCancellation?.Cancel();
            gPhoto2PreviewLoopCancellation?.Dispose();
            gPhoto2PreviewLoopCancellation = null;
            gPhoto2PreviewLoopTask = null;
            gPhoto2PreviewPaused = false;
            currentPreviewBackend = PreviewBackendKind.None;
            if (previewTarget != null && previewTarget.texture == gPhoto2PreviewTexture)
            {
                previewTarget.texture = null;
            }

            if (gPhoto2PreviewTexture != null)
            {
                UnityEngine.Object.Destroy(gPhoto2PreviewTexture);
                gPhoto2PreviewTexture = null;
            }
        }

        private async Task<string> WaitForCameraDeviceNameAsync(CancellationToken cancellationToken)
        {
            var startedAt = Time.realtimeSinceStartup;
            var timeoutSeconds = preferredDeviceDiscoveryTimeoutSeconds;
            string[] latestDeviceNames = null;
            var lastLoggedDeviceSignature = string.Empty;

            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                latestDeviceNames = GetCurrentDeviceNames();
                var deviceSignature = latestDeviceNames.Length == 0
                    ? "(none)"
                    : string.Join(", ", latestDeviceNames);
                if (!string.Equals(lastLoggedDeviceSignature, deviceSignature, StringComparison.Ordinal))
                {
                    lastLoggedDeviceSignature = deviceSignature;
                    Debug.Log($"PhotoBooth available camera devices: {deviceSignature}");
                }

                var preferredDeviceName = SelectPrimaryPreferredDeviceName(latestDeviceNames, preferredDeviceNames);
                if (!string.IsNullOrWhiteSpace(preferredDeviceName))
                {
                    return preferredDeviceName;
                }

                if (timeoutSeconds <= 0 || preferredDeviceNames.Length == 0)
                {
                    break;
                }

                await Task.Yield();
            } while (Time.realtimeSinceStartup - startedAt < timeoutSeconds);

            var fallbackDeviceName = SelectPreferredDeviceName(latestDeviceNames, preferredDeviceNames);
            if (!string.IsNullOrWhiteSpace(fallbackDeviceName) && preferredDeviceNames.Length > 0)
            {
                var availableDevices = latestDeviceNames == null || latestDeviceNames.Length == 0
                    ? "(none)"
                    : string.Join(", ", latestDeviceNames);
                Debug.LogWarning($"Preferred camera device was not found within {timeoutSeconds} seconds; falling back to {fallbackDeviceName}. Available devices: {availableDevices}");
            }

            return fallbackDeviceName;
        }

        private static string[] GetCurrentDeviceNames()
        {
            return WebCamTexture.devices?
                .Select(device => device.name)
                .Where(deviceName => !string.IsNullOrWhiteSpace(deviceName))
                .Select(deviceName => deviceName.Trim())
                .ToArray() ?? Array.Empty<string>();
        }

        private static Texture2D CreateSimulatedCameraTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            var faceCenter = new Vector2(width * 0.5f, height * 0.46f);
            var faceRadius = Mathf.Min(width, height) * 0.24f;
            for (var y = 0; y < height; y++)
            {
                var vertical = height <= 1 ? 0f : y / (float)(height - 1);
                var background = Color.Lerp(new Color(0.08f, 0.12f, 0.16f, 1f), new Color(0.28f, 0.38f, 0.46f, 1f), vertical);
                for (var x = 0; x < width; x++)
                {
                    var point = new Vector2(x, y);
                    var distance = Vector2.Distance(point, faceCenter);
                    var color = background;
                    if (distance < faceRadius)
                    {
                        color = new Color(0.95f, 0.73f, 0.58f, 1f);
                    }

                    if (Mathf.Abs(distance - faceRadius) < 4f)
                    {
                        color = Color.white;
                    }

                    pixels[y * width + x] = color;
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false, false);
            return texture;
        }

        public static string SelectPreferredDeviceName(IEnumerable<string> deviceNames, IEnumerable<string> preferredNames = null)
        {
            var devices = NormalizeDeviceNames(deviceNames);
            if (devices.Length == 0)
            {
                return null;
            }

            var priorities = NormalizePreferredDeviceNames(preferredNames);
            foreach (var preferredName in priorities)
            {
                var exactMatch = devices.FirstOrDefault(deviceName =>
                    string.Equals(deviceName, preferredName, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(exactMatch))
                {
                    return exactMatch;
                }
            }

            foreach (var preferredName in priorities)
            {
                var partialMatch = devices.FirstOrDefault(deviceName =>
                    deviceName.IndexOf(preferredName, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!string.IsNullOrWhiteSpace(partialMatch))
                {
                    return partialMatch;
                }
            }

            return priorities.Length == 0
                ? SelectDefaultDeviceName(devices)
                : devices[0];
        }

        public static string SelectPrimaryPreferredDeviceName(IEnumerable<string> deviceNames, IEnumerable<string> preferredNames = null)
        {
            var devices = NormalizeDeviceNames(deviceNames);
            if (devices.Length == 0)
            {
                return null;
            }

            var priorities = NormalizePreferredDeviceNames(preferredNames);
            if (priorities.Length == 0)
            {
                return null;
            }

            var primaryName = priorities[0];
            var exactMatch = devices.FirstOrDefault(deviceName =>
                string.Equals(deviceName, primaryName, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(exactMatch))
            {
                return exactMatch;
            }

            return devices.FirstOrDefault(deviceName =>
                deviceName.IndexOf(primaryName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static string DescribeDeviceSelection(string deviceName)
        {
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                return "none";
            }

            return "camera";
        }

        private static string[] NormalizePreferredDeviceNames(IEnumerable<string> preferredNames)
        {
            var names = preferredNames?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return names == null || names.Length == 0
                ? Array.Empty<string>()
                : names;
        }

        private static string[] NormalizeDeviceNames(IEnumerable<string> deviceNames)
        {
            return deviceNames?
                .Where(deviceName => !string.IsNullOrWhiteSpace(deviceName))
                .Select(deviceName => deviceName.Trim())
                .ToArray() ?? Array.Empty<string>();
        }

        private static string SelectDefaultDeviceName(IReadOnlyList<string> deviceNames)
        {
            if (deviceNames == null || deviceNames.Count == 0)
            {
                return null;
            }

            return deviceNames.FirstOrDefault(IsCanonLikeCameraDeviceName)
                ?? deviceNames.FirstOrDefault(IsCaptureCardLikeCameraDeviceName)
                ?? deviceNames.FirstOrDefault(deviceName =>
                    !IsVirtualCameraDeviceName(deviceName)
                    && !IsObsbotDeviceName(deviceName)
                    && !IsBuiltInCameraDeviceName(deviceName))
                ?? deviceNames.FirstOrDefault(IsObsbotDeviceName)
                ?? deviceNames.FirstOrDefault(deviceName =>
                    !IsVirtualCameraDeviceName(deviceName)
                    && !IsBuiltInCameraDeviceName(deviceName))
                ?? deviceNames.FirstOrDefault(deviceName => !IsVirtualCameraDeviceName(deviceName))
                ?? deviceNames[0];
        }

        public static bool IsCanonLikeCameraDeviceName(string deviceName)
        {
            return !string.IsNullOrWhiteSpace(deviceName)
                && CanonCameraNameParts.Any(part => deviceName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsCaptureCardLikeCameraDeviceName(string deviceName)
        {
            return !string.IsNullOrWhiteSpace(deviceName)
                && CaptureCardNameParts.Any(part => deviceName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsHd33DeviceName(string deviceName)
        {
            return !string.IsNullOrWhiteSpace(deviceName)
                && deviceName.IndexOf("HD33", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsObsbotDeviceName(string deviceName)
        {
            return !string.IsNullOrWhiteSpace(deviceName)
                && deviceName.IndexOf(ObsbotDeviceName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsBuiltInCameraDeviceName(string deviceName)
        {
            return !string.IsNullOrWhiteSpace(deviceName)
                && BuiltInCameraNameParts.Any(part => deviceName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public static bool IsVirtualCameraDeviceName(string deviceName)
        {
            return !string.IsNullOrWhiteSpace(deviceName)
                && VirtualCameraNameParts.Any(part => deviceName.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        public void Dispose()
        {
            StopPreviewTextures();
            _ = DisposeCanonBackendSafelyAsync();
        }

        private async Task DisposeCanonBackendSafelyAsync()
        {
            if (canonCameraBackend == null)
            {
                return;
            }

            try
            {
                await canonCameraBackend.StopLiveViewAsync(CancellationToken.None);
                await canonCameraBackend.CloseSessionAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Canon camera disposal cleanup failed: {exception.Message}");
            }
            finally
            {
                canonCameraBackend.Dispose();
            }
        }
    }
}
