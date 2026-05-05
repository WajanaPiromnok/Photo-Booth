using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PhotoBooth.Booth.AR
{
    public sealed class MediaPipeFaceTrackingProvider : IArTrackingProvider
    {
        private readonly int maxFaces;
        private readonly bool useHeuristicFallback;
        private long frameIndex;
        private bool warnedUnavailable;

        public MediaPipeFaceTrackingProvider(int maxFaces = 4, bool useSyntheticFallback = true)
        {
            this.maxFaces = Mathf.Clamp(maxFaces, 1, 16);
            useHeuristicFallback = useSyntheticFallback;
        }

        public string ProviderName => "MediaPipe Face Landmarker";

        public ArTrackingCapabilities Capabilities =>
            ArTrackingCapabilities.Face2D | ArTrackingCapabilities.Face3D | ArTrackingCapabilities.Hand | ArTrackingCapabilities.Body;

        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task<ArTrackingFrame> TrackAsync(Texture sourceTexture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsRunning || sourceTexture == null)
            {
                return Task.FromResult(ArTrackingFrame.Empty);
            }

            if (!warnedUnavailable)
            {
                warnedUnavailable = true;
                Debug.LogWarning("MediaPipe Unity Plugin adapter is not bound in this project yet. Using a lightweight 2D image heuristic fallback for demo face tracking.");
            }

            var frame = useHeuristicFallback
                ? CreateHeuristicFrame(sourceTexture)
                : new ArTrackingFrame
                {
                    ProviderName = ProviderName,
                    FrameIndex = frameIndex++,
                    PixelWidth = sourceTexture.width,
                    PixelHeight = sourceTexture.height,
                    TimestampSeconds = Time.realtimeSinceStartupAsDouble,
                    Faces = Array.Empty<FaceTrack>()
                };

            return Task.FromResult(frame);
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            Stop();
        }

        private ArTrackingFrame CreateHeuristicFrame(Texture sourceTexture)
        {
            if (sourceTexture is Texture2D texture && TryDetectSkinBlob(texture, out var normalizedBounds, out var eyeHint))
            {
                return new ArTrackingFrame
                {
                    ProviderName = ProviderName,
                    FrameIndex = frameIndex++,
                    PixelWidth = sourceTexture.width,
                    PixelHeight = sourceTexture.height,
                    TimestampSeconds = Time.realtimeSinceStartupAsDouble,
                    Faces = new[]
                    {
                        CreateFaceFromBounds("heuristic-face-0", normalizedBounds, eyeHint, 0.62f)
                    }
                };
            }

            return CreateSyntheticFrame(sourceTexture.width, sourceTexture.height);
        }

        private ArTrackingFrame CreateSyntheticFrame(int width, int height)
        {
            var faces = maxFaces <= 0
                ? Array.Empty<FaceTrack>()
                : new[]
                {
                    new FaceTrack
                    {
                        TrackId = "synthetic-face-0",
                        NormalizedBounds = new Rect(0.31f, 0.2f, 0.38f, 0.48f),
                        NormalizedLandmarks = BuildLandmarksFromBounds(new Rect(0.31f, 0.2f, 0.38f, 0.48f)),
                        NormalizedLandmarks3D = Array.Empty<Vector3>(),
                        Confidence = 0.35f
                    }
                };

            return new ArTrackingFrame
            {
                ProviderName = ProviderName,
                FrameIndex = frameIndex++,
                PixelWidth = width,
                PixelHeight = height,
                TimestampSeconds = Time.realtimeSinceStartupAsDouble,
                Faces = faces
            };
        }

        private static FaceTrack CreateFaceFromBounds(string trackId, Rect bounds, EyeHint eyeHint, float confidence)
        {
            return new FaceTrack
            {
                TrackId = trackId,
                NormalizedBounds = bounds,
                NormalizedLandmarks = BuildLandmarksFromBounds(bounds, eyeHint),
                NormalizedLandmarks3D = Array.Empty<Vector3>(),
                Confidence = confidence
            };
        }

        private static Vector2[] BuildLandmarksFromBounds(Rect bounds, EyeHint eyeHint = default)
        {
            var landmarks = new Vector2[478];
            var center = bounds.center;
            var eyeY = bounds.yMin + (bounds.height * 0.34f);
            var leftEye = new Vector2(bounds.xMin + (bounds.width * 0.42f), eyeY);
            var rightEye = new Vector2(bounds.xMin + (bounds.width * 0.58f), eyeY);
            if (eyeHint.HasEyes)
            {
                leftEye = eyeHint.LeftEye;
                rightEye = eyeHint.RightEye;
                eyeY = (leftEye.y + rightEye.y) * 0.5f;
            }

            for (var index = 0; index < landmarks.Length; index++)
            {
                landmarks[index] = center;
            }

            landmarks[1] = new Vector2(center.x, eyeY + (bounds.height * 0.16f));
            landmarks[10] = new Vector2(center.x, Mathf.Max(0f, eyeY - (bounds.height * 0.26f)));
            landmarks[33] = leftEye;
            landmarks[61] = new Vector2(bounds.xMin + (bounds.width * 0.4f), bounds.yMin + (bounds.height * 0.68f));
            landmarks[152] = new Vector2(center.x, bounds.yMax);
            landmarks[263] = rightEye;
            landmarks[291] = new Vector2(bounds.xMin + (bounds.width * 0.6f), bounds.yMin + (bounds.height * 0.68f));
            return landmarks;
        }

        private static bool TryDetectSkinBlob(Texture2D texture, out Rect normalizedBounds, out EyeHint eyeHint)
        {
            normalizedBounds = default;
            eyeHint = default;
            var width = texture.width;
            var height = texture.height;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var pixels = texture.GetPixels32();
            var step = Mathf.Max(1, Mathf.Min(width, height) / 96);
            var minX = width;
            var minY = height;
            var maxX = 0;
            var maxY = 0;
            var matches = 0;

            for (var y = 0; y < height; y += step)
            {
                for (var x = 0; x < width; x += step)
                {
                    var pixel = pixels[y * width + x];
                    if (!LooksLikeSkin(pixel))
                    {
                        continue;
                    }

                    matches += 1;
                    minX = Mathf.Min(minX, x);
                    minY = Mathf.Min(minY, y);
                    maxX = Mathf.Max(maxX, x);
                    maxY = Mathf.Max(maxY, y);
                }
            }

            if (matches < 24 || maxX <= minX || maxY <= minY)
            {
                return false;
            }

            var paddingX = (maxX - minX) * 0.18f;
            var paddingY = (maxY - minY) * 0.24f;
            var left = Mathf.Clamp((minX - paddingX) / width, 0f, 1f);
            var top = Mathf.Clamp(1f - ((maxY + paddingY) / height), 0f, 1f);
            var right = Mathf.Clamp((maxX + paddingX) / width, 0f, 1f);
            var bottom = Mathf.Clamp(1f - ((minY - paddingY) / height), 0f, 1f);

            var faceWidth = Mathf.Clamp(right - left, 0.08f, 0.9f);
            var faceHeight = Mathf.Clamp(bottom - top, 0.1f, 0.9f);
            var centerX = Mathf.Clamp((left + right) * 0.5f, faceWidth * 0.5f, 1f - (faceWidth * 0.5f));
            var centerY = Mathf.Clamp((top + bottom) * 0.5f, faceHeight * 0.5f, 1f - (faceHeight * 0.5f));
            normalizedBounds = new Rect(centerX - (faceWidth * 0.5f), centerY - (faceHeight * 0.5f), faceWidth, faceHeight);
            eyeHint = DetectEyes(texture, normalizedBounds, step);
            return true;
        }

        private static EyeHint DetectEyes(Texture2D texture, Rect bounds, int step)
        {
            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var left = Mathf.RoundToInt(bounds.xMin * width);
            var right = Mathf.RoundToInt(bounds.xMax * width);
            var top = Mathf.RoundToInt((1f - bounds.yMin) * height);
            var bottom = Mathf.RoundToInt((1f - bounds.yMax) * height);
            var searchTop = Mathf.RoundToInt(Mathf.Lerp(top, bottom, 0.22f));
            var searchBottom = Mathf.RoundToInt(Mathf.Lerp(top, bottom, 0.52f));
            var middle = Mathf.RoundToInt((left + right) * 0.5f);

            var leftEye = AccumulateDarkCentroid(pixels, width, height, left, middle, searchBottom, searchTop, step);
            var rightEye = AccumulateDarkCentroid(pixels, width, height, middle, right, searchBottom, searchTop, step);
            if (!leftEye.HasValue || !rightEye.HasValue)
            {
                return default;
            }

            return new EyeHint
            {
                HasEyes = true,
                LeftEye = PixelToNormalizedTopLeft(leftEye.Value, width, height),
                RightEye = PixelToNormalizedTopLeft(rightEye.Value, width, height)
            };
        }

        private static Vector2? AccumulateDarkCentroid(Color32[] pixels, int width, int height, int minX, int maxX, int minY, int maxY, int step)
        {
            var totalWeight = 0f;
            var sumX = 0f;
            var sumY = 0f;
            for (var y = Mathf.Clamp(minY, 0, height - 1); y <= Mathf.Clamp(maxY, 0, height - 1); y += step)
            {
                for (var x = Mathf.Clamp(minX, 0, width - 1); x <= Mathf.Clamp(maxX, 0, width - 1); x += step)
                {
                    var pixel = pixels[y * width + x];
                    var luminance = (pixel.r * 0.299f) + (pixel.g * 0.587f) + (pixel.b * 0.114f);
                    if (luminance > 92f || LooksLikeSkin(pixel))
                    {
                        continue;
                    }

                    var weight = 96f - luminance;
                    totalWeight += weight;
                    sumX += x * weight;
                    sumY += y * weight;
                }
            }

            return totalWeight < 120f ? null : new Vector2(sumX / totalWeight, sumY / totalWeight);
        }

        private static Vector2 PixelToNormalizedTopLeft(Vector2 pixel, int width, int height)
        {
            return new Vector2(pixel.x / width, 1f - (pixel.y / height));
        }

        private static bool LooksLikeSkin(Color32 pixel)
        {
            var r = pixel.r;
            var g = pixel.g;
            var b = pixel.b;
            if (r < 55 || g < 35 || b < 20 || r <= b || r < g)
            {
                return false;
            }

            var max = Mathf.Max(r, Mathf.Max(g, b));
            var min = Mathf.Min(r, Mathf.Min(g, b));
            if (max - min < 15 || Mathf.Abs(r - g) < 8)
            {
                return false;
            }

            var cb = 128f - (0.168736f * r) - (0.331264f * g) + (0.5f * b);
            var cr = 128f + (0.5f * r) - (0.418688f * g) - (0.081312f * b);
            return cb >= 77f && cb <= 135f && cr >= 130f && cr <= 180f;
        }

        private struct EyeHint
        {
            public bool HasEyes;
            public Vector2 LeftEye;
            public Vector2 RightEye;
        }
    }
}
