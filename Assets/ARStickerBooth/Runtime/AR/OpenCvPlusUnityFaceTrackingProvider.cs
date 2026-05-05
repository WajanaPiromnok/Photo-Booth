using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PhotoBooth.Booth.AR
{
    public sealed class OpenCvPlusUnityFaceTrackingProvider : IArTrackingProvider
    {
        private const string FaceEyeBridgeTypeName = "OpenCvSharp.Demo.PhotoBoothOpenCvFaceEyeBridge";
        private const string FaceCascadePath = "OpenCV+Unity/Demo/Face_Detector/haarcascade_frontalface_default.xml";
        private const string EyeCascadePath = "OpenCV+Unity/Demo/Face_Detector/haarcascade_eye_tree_eyeglasses.xml";
        private const int LocalFallbackStep = 4;

        private readonly int maxFaces;
        private object bridge;
        private MethodInfo detectMethod;
        private long frameIndex;
        private bool warnedUnavailable;
        private bool loggedLocalFallback;
        private bool loggedLocalFallbackRejected;

        public OpenCvPlusUnityFaceTrackingProvider(int maxFaces = 4)
        {
            this.maxFaces = Mathf.Clamp(maxFaces, 1, 16);
        }

        public string ProviderName => "OpenCV+Unity Haar Face Tracker";

        public ArTrackingCapabilities Capabilities => ArTrackingCapabilities.Face2D;

        public bool IsRunning { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            IsRunning = true;
            TryInitializeOpenCv();
            return Task.CompletedTask;
        }

        public Task<ArTrackingFrame> TrackAsync(Texture sourceTexture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning || sourceTexture == null)
            {
                return Task.FromResult(ArTrackingFrame.Empty);
            }

            if (sourceTexture is not Texture2D texture)
            {
                return Task.FromResult(CreateEmptyFrame(sourceTexture));
            }

            if (bridge == null || detectMethod == null)
            {
                WarnUnavailableOnce("OpenCV+Unity bridge is unavailable. Using local photo booth head detector fallback.");
                return Task.FromResult(CreateFrame(sourceTexture, DetectLocalHeadFallback(texture)));
            }

            try
            {
                var detections = detectMethod.Invoke(bridge, new object[] { texture }) as IEnumerable;
                var tracks = BuildTracks(detections, texture);
                if (tracks.Length == 0)
                {
                    tracks = DetectLocalHeadFallback(texture);
                }

                return Task.FromResult(CreateFrame(texture, tracks));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"OpenCV+Unity face tracking failed; using local photo booth head detector fallback for this frame. {exception.GetBaseException().Message}");
                return Task.FromResult(CreateFrame(texture, DetectLocalHeadFallback(texture)));
            }
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            Stop();
            if (bridge is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private bool TryInitializeOpenCv()
        {
            if (bridge != null)
            {
                return true;
            }

            try
            {
                var bridgeType = FindType(FaceEyeBridgeTypeName);
                if (bridgeType == null)
                {
                    WarnUnavailableOnce("OpenCV+Unity PhotoBooth face/eye bridge was not found. Local photo booth head detector fallback will be used.");
                    return false;
                }

                bridge = Activator.CreateInstance(bridgeType, nonPublic: true);
                var initialize = bridgeType.GetMethod("Initialize", BindingFlags.Instance | BindingFlags.Public);
                detectMethod = bridgeType.GetMethod("Detect", BindingFlags.Instance | BindingFlags.Public);
                if (initialize == null || detectMethod == null)
                {
                    WarnUnavailableOnce("OpenCV+Unity face/eye bridge API shape did not match the expected package. Local photo booth head detector fallback will be used.");
                    bridge = null;
                    return false;
                }

                initialize.Invoke(bridge, new object[] { ReadAssetText(FaceCascadePath), ReadOptionalAssetText(EyeCascadePath) });
                Debug.Log("OpenCV+Unity face+eye tracker initialized for photo booth AR overlay.");
                return true;
            }
            catch (Exception exception)
            {
                WarnUnavailableOnce($"OpenCV+Unity face tracker initialization failed; local photo booth head detector fallback will be used. {exception.GetBaseException().Message}");
                bridge = null;
                return false;
            }
        }

        private ArTrackingFrame CreateEmptyFrame(Texture sourceTexture)
        {
            return CreateFrame(sourceTexture, Array.Empty<FaceTrack>());
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

        private FaceTrack[] DetectLocalHeadFallback(Texture2D texture)
        {
            if (texture == null || !TryDetectLocalHeadBounds(texture, out var faceRect))
            {
                return Array.Empty<FaceTrack>();
            }

            if (!loggedLocalFallback)
            {
                loggedLocalFallback = true;
                Debug.Log($"PhotoBooth local head detector found a face candidate: x={faceRect.x}, y={faceRect.y}, w={faceRect.width}, h={faceRect.height}");
            }

            var bounds = OpenCvRectToNormalizedBounds(faceRect, texture.width, texture.height);
            var eyeHint = DetectEyes(texture, bounds, LocalFallbackStep);
            if (!eyeHint.HasEyes)
            {
                if (!loggedLocalFallbackRejected)
                {
                    loggedLocalFallbackRejected = true;
                    Debug.Log("PhotoBooth local head detector rejected a candidate because no eye pair was found.");
                }

                return Array.Empty<FaceTrack>();
            }

            return new[]
            {
                new FaceTrack
                {
                    TrackId = "photo-booth-local-head-0",
                    NormalizedBounds = bounds,
                    NormalizedLandmarks = BuildLandmarksFromBounds(bounds, eyeHint),
                    NormalizedLandmarks3D = Array.Empty<Vector3>(),
                    Confidence = 0.62f
                }
            };
        }

        private static bool TryDetectLocalHeadBounds(Texture2D texture, out RectInt face)
        {
            face = default;
            var width = texture.width;
            var height = texture.height;
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            var pixels = texture.GetPixels32();
            var step = LocalFallbackStep;
            var gridWidth = Mathf.Max(1, width / step);
            var gridHeight = Mathf.Max(1, height / step);
            var headMask = new bool[gridWidth * gridHeight];
            var visited = new bool[headMask.Length];
            var minSearchX = Mathf.RoundToInt(width * 0.18f);
            var maxSearchX = Mathf.RoundToInt(width * 0.82f);
            var minSearchY = Mathf.RoundToInt(height * 0.18f);
            var maxSearchY = Mathf.RoundToInt(height * 0.68f);
            var aggregate = new ComponentBounds
            {
                MinX = gridWidth,
                MinY = gridHeight,
                MaxX = 0,
                MaxY = 0,
                Count = 0
            };

            for (var gy = 0; gy < gridHeight; gy++)
            {
                var pixelYFromBottom = gy * step;
                var topLeftY = height - 1 - pixelYFromBottom;
                if (topLeftY < minSearchY || topLeftY > maxSearchY)
                {
                    continue;
                }

                for (var gx = 0; gx < gridWidth; gx++)
                {
                    var pixelX = gx * step;
                    if (pixelX < minSearchX || pixelX > maxSearchX)
                    {
                        continue;
                    }

                    var pixel = pixels[(pixelYFromBottom * width) + pixelX];
                    if (!IsLikelyHeadPixel(pixel))
                    {
                        continue;
                    }

                    var index = (gy * gridWidth) + gx;
                    headMask[index] = true;
                    aggregate.Count++;
                    aggregate.MinX = Mathf.Min(aggregate.MinX, gx);
                    aggregate.MaxX = Mathf.Max(aggregate.MaxX, gx);
                    aggregate.MinY = Mathf.Min(aggregate.MinY, gy);
                    aggregate.MaxY = Mathf.Max(aggregate.MaxY, gy);
                }
            }

            var best = default(ComponentBounds);
            var queue = new int[headMask.Length];
            for (var index = 0; index < headMask.Length; index++)
            {
                if (!headMask[index] || visited[index])
                {
                    continue;
                }

                var component = FloodHeadComponent(index, headMask, visited, queue, gridWidth, gridHeight);
                if (IsBetterFaceComponent(component, best, width, height, step))
                {
                    best = component;
                }
            }

            if (best.Count == 0 && IsBetterAggregateFace(aggregate, width, height, step))
            {
                best = aggregate;
            }

            if (best.Count == 0)
            {
                return false;
            }

            var left = best.MinX * step;
            var right = Mathf.Min(width - 1, ((best.MaxX + 1) * step) - 1);
            var bottomFromBottom = best.MinY * step;
            var topFromBottom = Mathf.Min(height - 1, ((best.MaxY + 1) * step) - 1);
            var top = height - 1 - topFromBottom;
            var bottom = height - 1 - bottomFromBottom;
            var componentWidth = right - left + 1;
            var componentHeight = bottom - top + 1;
            if (componentWidth <= 0 || componentHeight <= 0)
            {
                return false;
            }

            var expandX = Mathf.RoundToInt(componentWidth * 0.42f);
            var expandTop = Mathf.RoundToInt(componentHeight * 0.45f);
            var expandBottom = Mathf.RoundToInt(componentHeight * 0.70f);
            left = Mathf.Max(0, left - expandX);
            right = Mathf.Min(width - 1, right + expandX);
            top = Mathf.Max(0, top - expandTop);
            bottom = Mathf.Min(height - 1, bottom + expandBottom);
            face = new RectInt(left, top, right - left + 1, bottom - top + 1);
            return face.width >= width * 0.045f
                && face.height >= height * 0.065f
                && IsFaceRectInCaptureZone(face, width, height);
        }

        private static bool IsFaceRectInCaptureZone(RectInt face, int imageWidth, int imageHeight)
        {
            var centerX = (face.x + (face.width * 0.5f)) / imageWidth;
            var centerY = (face.y + (face.height * 0.5f)) / imageHeight;
            var aspect = face.width / (float)Mathf.Max(1, face.height);
            if (centerX < 0.22f || centerX > 0.78f)
            {
                return false;
            }

            if (centerY < 0.12f || centerY > 0.64f)
            {
                return false;
            }

            return aspect >= 0.38f && aspect <= 1.35f;
        }

        private static ComponentBounds FloodHeadComponent(int start, bool[] headMask, bool[] visited, int[] queue, int gridWidth, int gridHeight)
        {
            var head = 0;
            var tail = 0;
            queue[tail++] = start;
            visited[start] = true;
            var component = new ComponentBounds
            {
                MinX = start % gridWidth,
                MaxX = start % gridWidth,
                MinY = start / gridWidth,
                MaxY = start / gridWidth,
                Count = 0
            };

            while (head < tail)
            {
                var index = queue[head++];
                var x = index % gridWidth;
                var y = index / gridWidth;
                component.Count++;
                component.MinX = Mathf.Min(component.MinX, x);
                component.MaxX = Mathf.Max(component.MaxX, x);
                component.MinY = Mathf.Min(component.MinY, y);
                component.MaxY = Mathf.Max(component.MaxY, y);

                EnqueueHeadNeighbor(x - 1, y, headMask, visited, queue, ref tail, gridWidth, gridHeight);
                EnqueueHeadNeighbor(x + 1, y, headMask, visited, queue, ref tail, gridWidth, gridHeight);
                EnqueueHeadNeighbor(x, y - 1, headMask, visited, queue, ref tail, gridWidth, gridHeight);
                EnqueueHeadNeighbor(x, y + 1, headMask, visited, queue, ref tail, gridWidth, gridHeight);
            }

            return component;
        }

        private static void EnqueueHeadNeighbor(int x, int y, bool[] headMask, bool[] visited, int[] queue, ref int tail, int gridWidth, int gridHeight)
        {
            if (x < 0 || y < 0 || x >= gridWidth || y >= gridHeight)
            {
                return;
            }

            var index = (y * gridWidth) + x;
            if (!headMask[index] || visited[index])
            {
                return;
            }

            visited[index] = true;
            queue[tail++] = index;
        }

        private static bool IsBetterFaceComponent(ComponentBounds candidate, ComponentBounds current, int imageWidth, int imageHeight, int step)
        {
            if (candidate.Count < 14)
            {
                return false;
            }

            var componentWidth = ((candidate.MaxX - candidate.MinX) + 1) * step;
            var componentHeight = ((candidate.MaxY - candidate.MinY) + 1) * step;
            if (componentWidth < imageWidth * 0.018f || componentHeight < imageHeight * 0.028f)
            {
                return false;
            }

            var aspect = componentWidth / (float)Mathf.Max(1, componentHeight);
            if (aspect < 0.18f || aspect > 1.9f)
            {
                return false;
            }

            var centerX = ((candidate.MinX + candidate.MaxX + 1) * 0.5f * step) / imageWidth;
            var centerYFromBottom = ((candidate.MinY + candidate.MaxY + 1) * 0.5f * step) / imageHeight;
            if (centerX < 0.20f || centerX > 0.80f || centerYFromBottom < 0.32f || centerYFromBottom > 0.88f)
            {
                return false;
            }

            return current.Count == 0 || candidate.Count > current.Count;
        }

        private static bool IsBetterAggregateFace(ComponentBounds candidate, int imageWidth, int imageHeight, int step)
        {
            if (candidate.Count < 20)
            {
                return false;
            }

            var componentWidth = ((candidate.MaxX - candidate.MinX) + 1) * step;
            var componentHeight = ((candidate.MaxY - candidate.MinY) + 1) * step;
            if (componentWidth < imageWidth * 0.025f || componentHeight < imageHeight * 0.04f)
            {
                return false;
            }

            var aspect = componentWidth / (float)Mathf.Max(1, componentHeight);
            if (aspect < 0.16f || aspect > 2.1f)
            {
                return false;
            }

            var density = candidate.Count / (float)Mathf.Max(1, (candidate.MaxX - candidate.MinX + 1) * (candidate.MaxY - candidate.MinY + 1));
            if (density < 0.045f)
            {
                return false;
            }

            var centerX = ((candidate.MinX + candidate.MaxX + 1) * 0.5f * step) / imageWidth;
            var centerYFromBottom = ((candidate.MinY + candidate.MaxY + 1) * 0.5f * step) / imageHeight;
            return centerX >= 0.20f && centerX <= 0.80f && centerYFromBottom >= 0.32f && centerYFromBottom <= 0.88f;
        }

        private static bool IsLikelyHeadPixel(Color32 color)
        {
            return IsLikelySkin(color) || IsLikelyHairOrGlasses(color);
        }

        private static bool IsLikelySkin(Color32 color)
        {
            var r = color.r;
            var g = color.g;
            var b = color.b;
            var max = Mathf.Max(r, Mathf.Max(g, b));
            var min = Mathf.Min(r, Mathf.Min(g, b));
            if (r < 38 || g < 24 || b < 15 || max - min < 7 || r < g * 0.78f)
            {
                return false;
            }

            var y = (0.299f * r) + (0.587f * g) + (0.114f * b);
            var cb = 128f - (0.168736f * r) - (0.331264f * g) + (0.5f * b);
            var cr = 128f + (0.5f * r) - (0.418688f * g) - (0.081312f * b);
            return y > 28f && cb >= 55f && cb <= 165f && cr >= 105f && cr <= 205f;
        }

        private static bool IsLikelyHairOrGlasses(Color32 color)
        {
            var r = color.r;
            var g = color.g;
            var b = color.b;
            var max = Mathf.Max(r, Mathf.Max(g, b));
            var min = Mathf.Min(r, Mathf.Min(g, b));
            var luminance = (0.299f * r) + (0.587f * g) + (0.114f * b);
            if (luminance > 105f || max - min > 55)
            {
                return false;
            }

            if (b > r * 1.55f || b > g * 1.55f)
            {
                return false;
            }

            return r > 10 && g > 10 && b > 10;
        }

        private FaceTrack[] BuildTracks(IEnumerable detections, Texture2D texture)
        {
            if (detections == null)
            {
                return Array.Empty<FaceTrack>();
            }

            var tracks = new System.Collections.Generic.List<FaceTrack>();
            foreach (var detection in detections)
            {
                if (tracks.Count >= maxFaces)
                {
                    break;
                }

                if (!TryReadDetection(detection, out var faceRect, out var leftEyeRect, out var rightEyeRect, out var hasEyes)
                    || faceRect.width <= 0
                    || faceRect.height <= 0)
                {
                    continue;
                }

                var bounds = OpenCvRectToNormalizedBounds(faceRect, texture.width, texture.height);
                var eyeHint = hasEyes
                    ? EyeHintFromRects(leftEyeRect, rightEyeRect, texture.width, texture.height)
                    : DetectEyes(texture, bounds, LocalFallbackStep);
                if (!eyeHint.HasEyes)
                {
                    continue;
                }

                tracks.Add(new FaceTrack
                {
                    TrackId = $"opencv-face-{tracks.Count}",
                    NormalizedBounds = bounds,
                    NormalizedLandmarks = BuildLandmarksFromBounds(bounds, eyeHint),
                    NormalizedLandmarks3D = Array.Empty<Vector3>(),
                    Confidence = 0.82f
                });
            }

            return tracks.ToArray();
        }

        private static Rect OpenCvRectToNormalizedBounds(RectInt rect, int width, int height)
        {
            var faceWidth = Mathf.Clamp(rect.width / (float)width, 0.06f, 0.95f);
            var faceHeight = Mathf.Clamp(rect.height / (float)height, 0.08f, 0.95f);
            var centerX = Mathf.Clamp((rect.x + (rect.width * 0.5f)) / width, faceWidth * 0.5f, 1f - (faceWidth * 0.5f));
            var centerY = Mathf.Clamp(1f - ((rect.y + (rect.height * 0.5f)) / height), faceHeight * 0.5f, 1f - (faceHeight * 0.5f));
            return new Rect(centerX - (faceWidth * 0.5f), centerY - (faceHeight * 0.5f), faceWidth, faceHeight);
        }

        private static Vector2[] BuildLandmarksFromBounds(Rect bounds, EyeHint eyeHint)
        {
            var landmarks = new Vector2[478];
            var center = bounds.center;
            var leftEye = new Vector2(bounds.xMin + (bounds.width * 0.42f), bounds.yMin + (bounds.height * 0.34f));
            var rightEye = new Vector2(bounds.xMin + (bounds.width * 0.58f), bounds.yMin + (bounds.height * 0.34f));
            if (eyeHint.HasEyes)
            {
                leftEye = eyeHint.LeftEye;
                rightEye = eyeHint.RightEye;
            }

            for (var index = 0; index < landmarks.Length; index++)
            {
                landmarks[index] = center;
            }

            var eyeY = (leftEye.y + rightEye.y) * 0.5f;
            landmarks[1] = new Vector2(center.x, eyeY + (bounds.height * 0.15f));
            landmarks[10] = new Vector2(center.x, Mathf.Max(0f, eyeY - (bounds.height * 0.25f)));
            landmarks[33] = leftEye;
            landmarks[61] = new Vector2(bounds.xMin + (bounds.width * 0.42f), bounds.yMin + (bounds.height * 0.69f));
            landmarks[152] = new Vector2(center.x, bounds.yMax);
            landmarks[263] = rightEye;
            landmarks[291] = new Vector2(bounds.xMin + (bounds.width * 0.58f), bounds.yMin + (bounds.height * 0.69f));
            return landmarks;
        }

        private static EyeHint DetectEyes(Texture2D texture, Rect bounds, int step)
        {
            var width = texture.width;
            var height = texture.height;
            var pixels = texture.GetPixels32();
            var left = Mathf.RoundToInt(bounds.xMin * width);
            var right = Mathf.RoundToInt(bounds.xMax * width);
            var top = Mathf.RoundToInt((1f - bounds.yMax) * height);
            var bottom = Mathf.RoundToInt((1f - bounds.yMin) * height);
            var faceWidth = right - left;
            var searchTop = Mathf.RoundToInt(Mathf.Lerp(top, bottom, 0.27f));
            var searchBottom = Mathf.RoundToInt(Mathf.Lerp(top, bottom, 0.43f));
            var leftEye = AccumulateDarkCentroid(
                pixels,
                width,
                height,
                Mathf.RoundToInt(left + (faceWidth * 0.28f)),
                Mathf.RoundToInt(left + (faceWidth * 0.48f)),
                searchTop,
                searchBottom,
                step);
            var rightEye = AccumulateDarkCentroid(
                pixels,
                width,
                height,
                Mathf.RoundToInt(left + (faceWidth * 0.52f)),
                Mathf.RoundToInt(left + (faceWidth * 0.72f)),
                searchTop,
                searchBottom,
                step);
            if (!leftEye.HasValue || !rightEye.HasValue)
            {
                return default;
            }

            var normalizedLeft = PixelToNormalized(leftEye.Value, width, height);
            var normalizedRight = PixelToNormalized(rightEye.Value, width, height);
            var distance = Vector2.Distance(normalizedLeft, normalizedRight);
            if (distance < bounds.width * 0.12f || distance > bounds.width * 0.34f)
            {
                return default;
            }

            return new EyeHint
            {
                HasEyes = true,
                LeftEye = normalizedLeft,
                RightEye = normalizedRight
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
                    if (luminance > 90f)
                    {
                        continue;
                    }

                    var weight = 95f - luminance;
                    totalWeight += weight;
                    sumX += x * weight;
                    sumY += y * weight;
                }
            }

            return totalWeight < 140f ? null : new Vector2(sumX / totalWeight, sumY / totalWeight);
        }

        private static Vector2 PixelToNormalized(Vector2 pixel, int width, int height)
        {
            return new Vector2(pixel.x / width, 1f - (pixel.y / height));
        }

        private static EyeHint EyeHintFromRects(RectInt leftEye, RectInt rightEye, int width, int height)
        {
            return new EyeHint
            {
                HasEyes = true,
                LeftEye = PixelToNormalized(new Vector2(leftEye.x + (leftEye.width * 0.5f), leftEye.y + (leftEye.height * 0.5f)), width, height),
                RightEye = PixelToNormalized(new Vector2(rightEye.x + (rightEye.width * 0.5f), rightEye.y + (rightEye.height * 0.5f)), width, height)
            };
        }

        private static bool TryReadDetection(object detection, out RectInt face, out RectInt leftEye, out RectInt rightEye, out bool hasEyes)
        {
            face = default;
            leftEye = default;
            rightEye = default;
            hasEyes = false;
            if (detection == null)
            {
                return false;
            }

            var type = detection.GetType();
            face = new RectInt(
                ReadIntField(detection, type, "FaceX"),
                ReadIntField(detection, type, "FaceY"),
                ReadIntField(detection, type, "FaceWidth"),
                ReadIntField(detection, type, "FaceHeight"));
            hasEyes = ReadBoolField(detection, type, "HasEyes");
            if (hasEyes)
            {
                leftEye = new RectInt(
                    ReadIntField(detection, type, "LeftEyeX"),
                    ReadIntField(detection, type, "LeftEyeY"),
                    ReadIntField(detection, type, "LeftEyeWidth"),
                    ReadIntField(detection, type, "LeftEyeHeight"));
                rightEye = new RectInt(
                    ReadIntField(detection, type, "RightEyeX"),
                    ReadIntField(detection, type, "RightEyeY"),
                    ReadIntField(detection, type, "RightEyeWidth"),
                    ReadIntField(detection, type, "RightEyeHeight"));
            }

            return true;
        }

        private static int ReadIntField(object target, Type type, string fieldName)
        {
            return Convert.ToInt32(type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target) ?? 0);
        }

        private static bool ReadBoolField(object target, Type type, string fieldName)
        {
            return Convert.ToBoolean(type.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target) ?? false);
        }

        private static Type FindType(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(typeName, throwOnError: false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static string ReadAssetText(string relativePath)
        {
            var path = Path.Combine(Application.dataPath, relativePath);
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"OpenCV asset file was not found at {path}.", path);
            }

            return File.ReadAllText(path);
        }

        private static string ReadOptionalAssetText(string relativePath)
        {
            var path = Path.Combine(Application.dataPath, relativePath);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        private void WarnUnavailableOnce(string message = null)
        {
            if (warnedUnavailable)
            {
                return;
            }

            warnedUnavailable = true;
            Debug.LogWarning(string.IsNullOrWhiteSpace(message)
                ? "OpenCV+Unity face tracker is unavailable. AR stickers will stay hidden until a face is detected."
                : message);
        }

        private struct EyeHint
        {
            public bool HasEyes;
            public Vector2 LeftEye;
            public Vector2 RightEye;
        }

        private struct ComponentBounds
        {
            public int MinX;
            public int MaxX;
            public int MinY;
            public int MaxY;
            public int Count;
        }
    }
}
