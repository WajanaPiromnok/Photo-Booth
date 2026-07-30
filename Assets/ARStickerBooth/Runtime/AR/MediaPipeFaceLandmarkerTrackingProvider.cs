using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mediapipe;
using Mediapipe.Tasks.Components.Containers;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.FaceLandmarker;
using UnityEngine;
using MediapipeImage = Mediapipe.Image;

namespace PhotoBooth.Booth.AR
{
    public sealed class MediaPipeFaceLandmarkerTrackingProvider : IArTrackingProvider
    {
        private const string DefaultModelStreamingAssetsPath = "MediaPipe/face_landmarker.task";
        private readonly int maxFaces;
        private readonly string modelStreamingAssetsPath;
        private readonly bool mirrorLandmarksX;
        private FaceLandmarker faceLandmarker;
        private FaceLandmarkerResult reusableResult;
        private long frameIndex;
        private long lastTimestampMillisec = -1;
        private Texture2D cachedFlippedTexture;
        private Color32[] cachedPixelBuffer;
        private static bool glogInitialized;
        private static bool glogInitializationAttempted;

        public MediaPipeFaceLandmarkerTrackingProvider(
            int maxFaces = 4,
            string modelStreamingAssetsPath = DefaultModelStreamingAssetsPath,
            bool mirrorLandmarksX = true)
        {
            this.maxFaces = Mathf.Clamp(maxFaces, 1, 16);
            this.modelStreamingAssetsPath = string.IsNullOrWhiteSpace(modelStreamingAssetsPath)
                ? DefaultModelStreamingAssetsPath
                : modelStreamingAssetsPath;
            this.mirrorLandmarksX = mirrorLandmarksX;
        }

        public string ProviderName => "MediaPipe Face Landmarker (3D)";

        public ArTrackingCapabilities Capabilities => ArTrackingCapabilities.Face2D | ArTrackingCapabilities.Face3D;

        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRunning)
            {
                return Task.CompletedTask;
            }

            var modelPath = Path.Combine(Application.streamingAssetsPath, modelStreamingAssetsPath);
            if (!File.Exists(modelPath))
            {
                throw new InvalidOperationException($"MediaPipe Face Landmarker model was not found at StreamingAssets/{modelStreamingAssetsPath}.");
            }

            EnsureGlogInitialized();

            var options = new FaceLandmarkerOptions(
                new BaseOptions(BaseOptions.Delegate.CPU, modelAssetPath: modelPath),
                runningMode: RunningMode.VIDEO,
                numFaces: maxFaces,
                minFaceDetectionConfidence: 0.5f,
                minFacePresenceConfidence: 0.5f,
                minTrackingConfidence: 0.5f,
                outputFaceBlendshapes: false,
                outputFaceTransformationMatrixes: true);

            faceLandmarker = FaceLandmarker.CreateFromOptions(options);
            reusableResult = FaceLandmarkerResult.Alloc(maxFaces, outputFaceTransformationMatrixes: true);
            IsRunning = true;
            Debug.Log("MediaPipe Face Landmarker initialized for photo booth AR overlay.");
            return Task.CompletedTask;
        }

        private static void EnsureGlogInitialized()
        {
            if (glogInitialized || glogInitializationAttempted)
            {
                return;
            }

            glogInitializationAttempted = true;
            try
            {
                Glog.Logtostderr = true;
                Glog.Initialize("PhotoBoothMediaPipe");
                glogInitialized = true;
            }
            catch (Exception exception)
            {
                // In the Unity Editor, Homuler/MediaPipe native logging can already be initialized
                // by a previous play session or plugin bootstrap. Continue and let FaceLandmarker
                // creation be the real availability check.
                Debug.LogWarning($"MediaPipe glog initialize was skipped; continuing with FaceLandmarker startup. {exception.GetType().Name}: {exception.Message}");
            }
        }

        public Task<ArTrackingFrame> TrackAsync(Texture sourceTexture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning || faceLandmarker == null || sourceTexture is not Texture2D texture)
            {
                return Task.FromResult(CreateFrame(sourceTexture, Array.Empty<FaceTrack>()));
            }

            try
            {
                // Unity's WebCamTexture.GetPixels32() returns a vertically flipped (bottom-up) image.
                // MediaPipe requires a top-down image to detect faces reliably.
                var flipped = FlipVertical(texture);
                using var image = new MediapipeImage(flipped);
                var currentTimeMs = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
                var timestampMillisec = Math.Max(lastTimestampMillisec + 1, currentTimeMs);
                lastTimestampMillisec = timestampMillisec;
                var hasFaces = faceLandmarker.TryDetectForVideo(image, timestampMillisec, null, ref reusableResult);
                // Always attempt to read results — some MediaPipe builds populate reusableResult
                // even when TryDetectForVideo returns false.
                var landmarkCount = reusableResult.faceLandmarks?.Count ?? 0;
                var faces = landmarkCount > 0 ? BuildTracks(reusableResult) : Array.Empty<FaceTrack>();
                if (!hasFaces && landmarkCount > 0)
                {
                    Debug.Log($"[MediaPipe] TryDetectForVideo returned false but reusableResult has {landmarkCount} landmark set(s). Using them.");
                }
                return Task.FromResult(CreateFrame(sourceTexture, faces));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"MediaPipe Face Landmarker tracking failed; hiding AR stickers for this frame. {exception.Message}");
                return Task.FromResult(CreateFrame(sourceTexture, Array.Empty<FaceTrack>()));
            }
        }

        /// <summary>
        /// Flips the texture vertically in place into cachedFlippedTexture to avoid allocations.
        /// Unity WebCamTexture pixels are stored bottom-row-first; MediaPipe needs top-row-first.
        /// </summary>
        private Texture2D FlipVertical(Texture2D source)
        {
            var width = source.width;
            var height = source.height;
            var src = source.GetPixels32();
            
            var totalPixels = src.Length;
            if (cachedPixelBuffer == null || cachedPixelBuffer.Length != totalPixels)
            {
                cachedPixelBuffer = new Color32[totalPixels];
            }
            
            for (var y = 0; y < height; y++)
            {
                var srcRow = y * width;
                var dstRow = (height - 1 - y) * width;
                Array.Copy(src, srcRow, cachedPixelBuffer, dstRow, width);
            }

            if (cachedFlippedTexture == null || cachedFlippedTexture.width != width || cachedFlippedTexture.height != height || cachedFlippedTexture.format != source.format)
            {
                if (cachedFlippedTexture != null)
                {
                    UnityEngine.Object.Destroy(cachedFlippedTexture);
                }
                cachedFlippedTexture = new Texture2D(width, height, source.format, false);
                cachedFlippedTexture.name = "MediaPipeFlippedFrame";
            }

            cachedFlippedTexture.SetPixels32(cachedPixelBuffer);
            cachedFlippedTexture.Apply(false, false);
            return cachedFlippedTexture;
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            Stop();
            faceLandmarker?.Close();
            faceLandmarker = null;

            if (cachedFlippedTexture != null)
            {
                UnityEngine.Object.Destroy(cachedFlippedTexture);
                cachedFlippedTexture = null;
            }
            cachedPixelBuffer = null;
        }

        private ArTrackingFrame CreateFrame(Texture sourceTexture, FaceTrack[] faces)
        {
            return new ArTrackingFrame
            {
                ProviderName = ProviderName,
                FrameIndex = frameIndex++,
                PixelWidth = sourceTexture != null ? sourceTexture.width : 0,
                PixelHeight = sourceTexture != null ? sourceTexture.height : 0,
                TimestampSeconds = Time.realtimeSinceStartupAsDouble,
                Faces = faces ?? Array.Empty<FaceTrack>()
            };
        }

        private FaceTrack[] BuildTracks(FaceLandmarkerResult result)
        {
            var landmarks = result.faceLandmarks;
            if (landmarks == null || landmarks.Count == 0)
            {
                return Array.Empty<FaceTrack>();
            }

            var count = Mathf.Min(maxFaces, landmarks.Count);
            var tracks = new System.Collections.Generic.List<FaceTrack>(count);
            for (var index = 0; index < count; index++)
            {
                var transform = result.facialTransformationMatrixes != null && index < result.facialTransformationMatrixes.Count
                    ? result.facialTransformationMatrixes[index]
                    : (Matrix4x4?)null;
                var euler = transform.HasValue
                    ? NormalizeEuler(transform.Value.rotation.eulerAngles)
                    : (Vector3?)null;
                var track = CreateTrackFromMediaPipeLandmarks(
                    $"mediapipe-face-{index}",
                    landmarks[index],
                    transform,
                    euler,
                    1f);
                if (track != null)
                {
                    tracks.Add(track);
                }
            }

            return tracks.ToArray();
        }

        private static Vector3 NormalizeEuler(Vector3 euler)
        {
            return new Vector3(NormalizeAngle(euler.x), NormalizeAngle(euler.y), NormalizeAngle(euler.z));
        }

        private static float NormalizeAngle(float degrees)
        {
            while (degrees > 180f)
            {
                degrees -= 360f;
            }

            while (degrees < -180f)
            {
                degrees += 360f;
            }

            return degrees;
        }

        internal static FaceTrack CreateTrackFromMediaPipeLandmarks(
            string trackId,
            Vector3[] mediaPipeNormalizedLandmarks,
            Matrix4x4? faceTransform,
            Vector3? faceEulerDegrees,
            float confidence = 1f)
        {
            return CreateTrackFromMediaPipeLandmarks(
                trackId,
                mediaPipeNormalizedLandmarks,
                faceTransform,
                faceEulerDegrees,
                confidence,
                mirrorLandmarksX: true);
        }

        private static FaceTrack CreateTrackFromMediaPipeLandmarks(
            string trackId,
            Vector3[] mediaPipeNormalizedLandmarks,
            Matrix4x4? faceTransform,
            Vector3? faceEulerDegrees,
            float confidence,
            bool mirrorLandmarksX)
        {
            if (mediaPipeNormalizedLandmarks == null || mediaPipeNormalizedLandmarks.Length == 0)
            {
                return null;
            }

            var landmarks2D = new Vector2[mediaPipeNormalizedLandmarks.Length];
            var landmarks3D = new Vector3[mediaPipeNormalizedLandmarks.Length];
            var minX = 1f;
            var minY = 1f;
            var maxX = 0f;
            var maxY = 0f;
            for (var index = 0; index < mediaPipeNormalizedLandmarks.Length; index++)
            {
                var source = mediaPipeNormalizedLandmarks[index];
                // The rest of the AR stack uses normalized bottom-up Y and converts
                // to UI top-left pixels when drawing. MediaPipe reports top-down Y.
                // Match the horizontal orientation of the configured camera preview.
                var x = Mathf.Clamp01(mirrorLandmarksX ? 1f - source.x : source.x);
                var y = Mathf.Clamp01(1f - source.y);
                landmarks2D[index] = new Vector2(x, y);
                landmarks3D[index] = new Vector3(x, y, source.z);
                minX = Mathf.Min(minX, x);
                minY = Mathf.Min(minY, y);
                maxX = Mathf.Max(maxX, x);
                maxY = Mathf.Max(maxY, y);
            }

            if (maxX <= minX || maxY <= minY)
            {
                return null;
            }

            return new FaceTrack
            {
                TrackId = string.IsNullOrWhiteSpace(trackId) ? "mediapipe-face-0" : trackId,
                NormalizedBounds = new UnityEngine.Rect(minX, minY, maxX - minX, maxY - minY),
                NormalizedLandmarks = landmarks2D,
                // DebugNormalizedLandmarks is used by UpdateFaceMarkDebugLines to draw
                // the green face-outline in the preview overlay.
                DebugNormalizedLandmarks = landmarks2D,
                NormalizedLandmarks3D = landmarks3D,
                HasReliableEyeLandmarks = HasMediaPipeEyeLandmarks(landmarks2D),
                HasFaceTransform = faceTransform.HasValue,
                FaceEulerDegrees = faceEulerDegrees ?? Vector3.zero,
                FaceTransform = faceTransform ?? Matrix4x4.identity,
                Confidence = Mathf.Clamp01(confidence)
            };
        }

        private FaceTrack CreateTrackFromMediaPipeLandmarks(
            string trackId,
            NormalizedLandmarks mediaPipeNormalizedLandmarks,
            Matrix4x4? faceTransform,
            Vector3? faceEulerDegrees,
            float confidence = 1f)
        {
            var landmarks = mediaPipeNormalizedLandmarks.landmarks;
            if (landmarks == null || landmarks.Count == 0)
            {
                return null;
            }

            var vectors = new Vector3[landmarks.Count];
            for (var index = 0; index < landmarks.Count; index++)
            {
                var landmark = landmarks[index];
                vectors[index] = new Vector3(landmark.x, landmark.y, landmark.z);
            }

            return CreateTrackFromMediaPipeLandmarks(
                trackId,
                vectors,
                faceTransform,
                faceEulerDegrees,
                confidence,
                mirrorLandmarksX);
        }

        private static bool HasMediaPipeEyeLandmarks(Vector2[] landmarks)
        {
            return landmarks != null
                && landmarks.Length > 263
                && landmarks[33] != default
                && landmarks[263] != default
                && Vector2.Distance(landmarks[33], landmarks[263]) > 0.01f;
        }
    }
}
