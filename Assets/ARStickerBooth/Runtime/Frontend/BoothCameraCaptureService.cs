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
        private readonly RawImage previewTarget;
        private readonly int requestedWidth;
        private readonly int requestedHeight;
        private readonly int requestedFps;
        private readonly string[] preferredDeviceNames;

        private WebCamTexture cameraTexture;
        private Texture2D simulatedCameraTexture;

        public BoothCameraCaptureService(
            RawImage previewTarget,
            int requestedWidth = 1280,
            int requestedHeight = 720,
            int requestedFps = 30,
            IEnumerable<string> preferredDeviceNames = null)
        {
            this.previewTarget = previewTarget;
            this.requestedWidth = Math.Max(320, requestedWidth);
            this.requestedHeight = Math.Max(240, requestedHeight);
            this.requestedFps = Math.Max(15, requestedFps);
            this.preferredDeviceNames = NormalizePreferredDeviceNames(preferredDeviceNames);
        }

        public bool IsPreviewing => (cameraTexture != null && cameraTexture.isPlaying) || simulatedCameraTexture != null;

        public int CurrentWidth => cameraTexture != null && cameraTexture.width > 16 ? cameraTexture.width : simulatedCameraTexture != null ? simulatedCameraTexture.width : requestedWidth;

        public int CurrentHeight => cameraTexture != null && cameraTexture.height > 16 ? cameraTexture.height : simulatedCameraTexture != null ? simulatedCameraTexture.height : requestedHeight;

        public Texture CurrentPreviewTexture => cameraTexture != null ? cameraTexture : simulatedCameraTexture;

        public string CurrentDeviceName { get; private set; }

        public async Task StartPreviewAsync(CancellationToken cancellationToken = default)
        {
            if (IsPreviewing)
            {
                return;
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

            if (WebCamTexture.devices == null || WebCamTexture.devices.Length == 0)
            {
                Debug.LogWarning("No camera device was found. Using simulated camera preview.");
                StartSimulatedPreview();
                return;
            }

            var selectedDeviceName = SelectPreferredDeviceName(WebCamTexture.devices.Select(device => device.name), preferredDeviceNames);
            CurrentDeviceName = selectedDeviceName;
            Debug.Log($"PhotoBooth camera selected: {selectedDeviceName}");
            cameraTexture = new WebCamTexture(selectedDeviceName, requestedWidth, requestedHeight, requestedFps);
            if (previewTarget != null)
            {
                previewTarget.texture = cameraTexture;
                previewTarget.color = Color.white;
                // Flip U axis to un-mirror the webcam (selfie/front-cam is mirrored by default).
                previewTarget.uvRect = new Rect(1f, 0f, -1f, 1f);
            }

            cameraTexture.Play();
            var startedAt = Time.realtimeSinceStartup;
            while (cameraTexture.width <= 16)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Time.realtimeSinceStartup - startedAt > 5f)
                {
                    Debug.LogWarning("Camera preview did not become ready within 5 seconds. Using simulated camera preview.");
                    StopCameraTextureOnly();
                    StartSimulatedPreview();
                    return;
                }

                await Task.Yield();
            }

            Debug.Log($"PhotoBooth camera preview ready: device={CurrentDeviceName}, texture={CurrentWidth}x{CurrentHeight}");
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

        public Texture2D CaptureCurrentFrameTexture()
        {
            EnsureReady();
            var sourceWidth = cameraTexture != null && cameraTexture.width > 16 ? cameraTexture.width : simulatedCameraTexture.width;
            var sourceHeight = cameraTexture != null && cameraTexture.height > 16 ? cameraTexture.height : simulatedCameraTexture.height;
            var texture = new Texture2D(sourceWidth, sourceHeight, TextureFormat.RGBA32, false);
            texture.SetPixels32(cameraTexture != null && cameraTexture.width > 16 ? cameraTexture.GetPixels32() : simulatedCameraTexture.GetPixels32());
            texture.Apply(false, false);
            return texture;
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

        private void EnsureReady()
        {
            if (!IsPreviewing)
            {
                throw new InvalidOperationException("Camera preview is not ready.");
            }
        }

        public void StopPreview()
        {
            var stoppedDeviceName = CurrentDeviceName;
            var wasPreviewing = IsPreviewing;
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
            if (wasPreviewing)
            {
                Debug.Log($"PhotoBooth camera stopped: device={stoppedDeviceName ?? "(unknown)"}");
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
            CurrentDeviceName = "Simulated Camera";
            simulatedCameraTexture = CreateSimulatedCameraTexture(requestedWidth, requestedHeight);
            if (previewTarget != null)
            {
                previewTarget.texture = simulatedCameraTexture;
                previewTarget.color = Color.white;
            }
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
            var devices = deviceNames?
                .Where(deviceName => !string.IsNullOrWhiteSpace(deviceName))
                .Select(deviceName => deviceName.Trim())
                .ToArray() ?? Array.Empty<string>();
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

            return devices[0];
        }

        private static string[] NormalizePreferredDeviceNames(IEnumerable<string> preferredNames)
        {
            var names = preferredNames?
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return names == null || names.Length == 0
                ? new[] { "OBSBOT Virtual Camera", "OBSBOT" }
                : names;
        }

        public void Dispose()
        {
            StopPreview();
        }
    }
}
