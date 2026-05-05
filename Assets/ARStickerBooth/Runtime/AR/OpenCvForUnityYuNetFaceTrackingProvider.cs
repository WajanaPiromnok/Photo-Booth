#if PHOTOBOOTH_OPENCVFORUNITY
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenCVForUnity.Calib3dModule;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.FaceModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.UnityIntegration;
using OpenCVForUnity.UnityIntegration.Worker.DataStruct;
using OpenCVForUnity.UnityIntegration.Worker.DnnModule;
using UnityEngine;
using CvPoint = OpenCVForUnity.CoreModule.Point;
using CvPoint3 = OpenCVForUnity.CoreModule.Point3;
using CvRect = OpenCVForUnity.CoreModule.Rect;
using UnityRect = UnityEngine.Rect;

namespace PhotoBooth.Booth.AR
{
    public sealed class OpenCvForUnityYuNetFaceTrackingProvider : IArTrackingProvider
    {
        private const string DefaultModelPath = "OpenCVForUnityExamples/dnn/face_detection_yunet_2023mar.onnx";
        private const string DefaultFaceMarkModelPath = "OpenCVForUnityExamples/face/lbfmodel.yaml";
        private readonly int maxFaces;
        private readonly string modelPath;
        private readonly string faceMarkModelPath;
        private readonly float confidenceThreshold;
        private readonly bool enableFaceMark;
        private YuNetV2FaceDetector detector;
        private Facemark faceMark;
        private Mat rgbaMat;
        private Mat bgrMat;
        private Mat grayMat;
        private Mat cameraMatrix;
        private MatOfDouble distortionCoefficients;
        private long frameIndex;

        public OpenCvForUnityYuNetFaceTrackingProvider(
            int maxFaces = 4,
            string modelPath = DefaultModelPath,
            float confidenceThreshold = 0.6f,
            bool enableFaceMark = false,
            string faceMarkModelPath = DefaultFaceMarkModelPath)
        {
            this.maxFaces = Mathf.Clamp(maxFaces, 1, 16);
            this.modelPath = string.IsNullOrWhiteSpace(modelPath) ? DefaultModelPath : modelPath;
            this.faceMarkModelPath = string.IsNullOrWhiteSpace(faceMarkModelPath) ? DefaultFaceMarkModelPath : faceMarkModelPath;
            this.confidenceThreshold = Mathf.Clamp01(confidenceThreshold);
            this.enableFaceMark = enableFaceMark;
        }

        public string ProviderName => faceMark != null
            ? "OpenCVForUnity YuNet + FaceMark LBF Face Tracker"
            : "OpenCVForUnity YuNet Face Tracker";

        public ArTrackingCapabilities Capabilities => faceMark != null
            ? ArTrackingCapabilities.Face2D | ArTrackingCapabilities.Face3D
            : ArTrackingCapabilities.Face2D;

        public bool IsRunning { get; private set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (IsRunning)
            {
                return;
            }

            var resolvedModelPath = await OpenCVEnv.GetFilePathTaskAsync(modelPath, cancellationToken: cancellationToken);
            if (string.IsNullOrEmpty(resolvedModelPath))
            {
                Debug.LogWarning($"OpenCVForUnity YuNet model was not found: {modelPath}");
                IsRunning = true;
                return;
            }

            detector = new YuNetV2FaceDetector(resolvedModelPath, null, new Size(320, 320), confidenceThreshold, 0.3f, Math.Max(1, maxFaces));
            if (enableFaceMark)
            {
                await TryStartFaceMarkAsync(cancellationToken);
            }

            IsRunning = true;
            Debug.Log($"{ProviderName} initialized for photo booth AR overlay.");
        }

        public Task<ArTrackingFrame> TrackAsync(Texture sourceTexture, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning || detector == null || sourceTexture is not Texture2D texture)
            {
                return Task.FromResult(CreateFrame(sourceTexture, Array.Empty<FaceTrack>()));
            }

            try
            {
                EnsureMats(texture.width, texture.height);
                OpenCVMatUtils.Texture2DToMat(texture, rgbaMat, true);
                Imgproc.cvtColor(rgbaMat, bgrMat, Imgproc.COLOR_RGBA2BGR);
                Imgproc.cvtColor(rgbaMat, grayMat, Imgproc.COLOR_RGBA2GRAY);
                Imgproc.equalizeHist(grayMat, grayMat);

                using var faces = detector.Detect(bgrMat, true);
                var data = detector.ToStructuredData(faces);
                return Task.FromResult(CreateFrame(texture, BuildTracks(data, texture.width, texture.height)));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"OpenCVForUnity YuNet tracking failed; hiding AR stickers for this frame. {exception.Message}");
                return Task.FromResult(CreateFrame(sourceTexture, Array.Empty<FaceTrack>()));
            }
        }

        public void Stop()
        {
            IsRunning = false;
        }

        public void Dispose()
        {
            Stop();
            detector?.Dispose();
            detector = null;
            rgbaMat?.Dispose();
            rgbaMat = null;
            bgrMat?.Dispose();
            bgrMat = null;
            grayMat?.Dispose();
            grayMat = null;
            faceMark?.Dispose();
            faceMark = null;
            cameraMatrix?.Dispose();
            cameraMatrix = null;
            distortionCoefficients?.Dispose();
            distortionCoefficients = null;
        }

        private void EnsureMats(int width, int height)
        {
            if (rgbaMat == null || rgbaMat.cols() != width || rgbaMat.rows() != height)
            {
                rgbaMat?.Dispose();
                bgrMat?.Dispose();
                grayMat?.Dispose();
                rgbaMat = new Mat(height, width, CvType.CV_8UC4);
                bgrMat = new Mat(height, width, CvType.CV_8UC3);
                grayMat = new Mat(height, width, CvType.CV_8UC1);
                RebuildCameraIntrinsics(width, height);
            }
        }

        private async Task TryStartFaceMarkAsync(CancellationToken cancellationToken)
        {
            var resolvedModelPath = await OpenCVEnv.GetFilePathTaskAsync(faceMarkModelPath, cancellationToken: cancellationToken);
            if (string.IsNullOrEmpty(resolvedModelPath))
            {
                Debug.LogWarning($"OpenCVForUnity FaceMark LBF model was not found: {faceMarkModelPath}. Falling back to YuNet 5-point landmarks.");
                return;
            }

            try
            {
                faceMark = Face.createFacemarkLBF();
                faceMark.loadModel(resolvedModelPath);
            }
            catch (Exception exception)
            {
                faceMark?.Dispose();
                faceMark = null;
                Debug.LogWarning($"OpenCVForUnity FaceMark LBF failed to initialize. Falling back to YuNet 5-point landmarks. {exception.Message}");
            }
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

        private FaceTrack[] BuildTracks(FaceDetection5LandmarkData[] detections, int width, int height)
        {
            if (detections == null || detections.Length == 0)
            {
                return Array.Empty<FaceTrack>();
            }

            var count = Mathf.Min(maxFaces, detections.Length);
            var faceMarkPoints = FitFaceMark(detections, count, width, height);
            var tracks = new FaceTrack[count];
            for (var i = 0; i < count; i++)
            {
                var detection = detections[i];
                var bounds = PixelRectToNormalizedBounds(detection.X, detection.Y, detection.Width, detection.Height, width, height);
                var points = faceMarkPoints != null && i < faceMarkPoints.Count ? faceMarkPoints[i] : null;
                var hasFaceMark = points != null && points.Length >= 68;
                var faceEuler = Vector3.zero;
                var faceTransform = Matrix4x4.identity;
                var hasPose = hasFaceMark && TryEstimateFacePose(points, width, height, out faceEuler, out faceTransform);
                var yuNetLandmarks = BuildLandmarks(detection, bounds, width, height);
                var stickerLandmarks = hasFaceMark
                    ? BuildLandmarksFromFaceMark(points, bounds, width, height)
                    : yuNetLandmarks;
                if (hasFaceMark)
                {
                    stickerLandmarks[33] = yuNetLandmarks[33];
                    stickerLandmarks[263] = yuNetLandmarks[263];
                }

                tracks[i] = new FaceTrack
                {
                    TrackId = $"opencvforunity-yunet-face-{i}",
                    NormalizedBounds = bounds,
                    NormalizedLandmarks = stickerLandmarks,
                    DebugNormalizedLandmarks = hasFaceMark
                        ? BuildDebugLandmarks(points, width, height)
                        : Array.Empty<Vector2>(),
                    NormalizedLandmarks3D = hasFaceMark
                        ? BuildPseudo3DLandmarks(points, width, height)
                        : Array.Empty<Vector3>(),
                    HasFaceTransform = hasPose,
                    FaceEulerDegrees = hasPose ? faceEuler : Vector3.zero,
                    FaceTransform = hasPose ? faceTransform : Matrix4x4.identity,
                    Confidence = detection.Score
                };
            }

            return tracks;
        }

        private List<CvPoint[]> FitFaceMark(FaceDetection5LandmarkData[] detections, int count, int width, int height)
        {
            if (faceMark == null || grayMat == null || detections == null || count <= 0)
            {
                return null;
            }

            using var faceRects = new MatOfRect(BuildFaceMarkRects(detections, count, width, height));
            var landmarkMats = new List<MatOfPoint2f>();
            try
            {
                if (!faceMark.fit(grayMat, faceRects, landmarkMats) || landmarkMats.Count == 0)
                {
                    DisposeLandmarkMats(landmarkMats);
                    return null;
                }

                var result = new List<CvPoint[]>(landmarkMats.Count);
                foreach (var landmarks in landmarkMats)
                {
                    result.Add(landmarks.toArray());
                }

                return result;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"OpenCVForUnity FaceMark fit failed; using YuNet 5-point landmarks for this frame. {exception.Message}");
                return null;
            }
            finally
            {
                DisposeLandmarkMats(landmarkMats);
            }
        }

        private static void DisposeLandmarkMats(List<MatOfPoint2f> landmarkMats)
        {
            if (landmarkMats == null)
            {
                return;
            }

            foreach (var landmark in landmarkMats)
            {
                landmark?.Dispose();
            }
        }

        private static CvRect[] BuildFaceMarkRects(FaceDetection5LandmarkData[] detections, int count, int imageWidth, int imageHeight)
        {
            var rects = new CvRect[count];
            for (var i = 0; i < count; i++)
            {
                var detection = detections[i];
                var paddingX = detection.Width * 0.08f;
                var paddingY = detection.Height * 0.08f;
                var x = Mathf.Clamp(Mathf.RoundToInt(detection.X - paddingX), 0, imageWidth - 1);
                var y = Mathf.Clamp(Mathf.RoundToInt(detection.Y - paddingY), 0, imageHeight - 1);
                var right = Mathf.Clamp(Mathf.RoundToInt(detection.X + detection.Width + paddingX), x + 1, imageWidth);
                var bottom = Mathf.Clamp(Mathf.RoundToInt(detection.Y + detection.Height + paddingY), y + 1, imageHeight);
                rects[i] = new CvRect(x, y, right - x, bottom - y);
            }

            return rects;
        }

        private static UnityRect PixelRectToNormalizedBounds(float x, float y, float width, float height, int imageWidth, int imageHeight)
        {
            var normalizedWidth = Mathf.Clamp(width / imageWidth, 0.01f, 1f);
            var normalizedHeight = Mathf.Clamp(height / imageHeight, 0.01f, 1f);
            var centerX = Mathf.Clamp((x + (width * 0.5f)) / imageWidth, normalizedWidth * 0.5f, 1f - (normalizedWidth * 0.5f));
            var centerY = Mathf.Clamp(1f - ((y + (height * 0.5f)) / imageHeight), normalizedHeight * 0.5f, 1f - (normalizedHeight * 0.5f));
            return new UnityRect(centerX - (normalizedWidth * 0.5f), centerY - (normalizedHeight * 0.5f), normalizedWidth, normalizedHeight);
        }

        private static Vector2[] BuildLandmarks(FaceDetection5LandmarkData detection, UnityRect bounds, int width, int height)
        {
            var landmarks = new Vector2[478];
            var center = bounds.center;
            for (var i = 0; i < landmarks.Length; i++)
            {
                landmarks[i] = center;
            }

            var detectedLeftEye = PixelToNormalized(detection.LeftEye.Item1, detection.LeftEye.Item2, width, height);
            var detectedRightEye = PixelToNormalized(detection.RightEye.Item1, detection.RightEye.Item2, width, height);
            var leftEye = detectedLeftEye.x <= detectedRightEye.x ? detectedLeftEye : detectedRightEye;
            var rightEye = detectedLeftEye.x <= detectedRightEye.x ? detectedRightEye : detectedLeftEye;
            var nose = PixelToNormalized(detection.Nose.Item1, detection.Nose.Item2, width, height);
            var detectedLeftMouth = PixelToNormalized(detection.LeftMouth.Item1, detection.LeftMouth.Item2, width, height);
            var detectedRightMouth = PixelToNormalized(detection.RightMouth.Item1, detection.RightMouth.Item2, width, height);
            var leftMouth = detectedLeftMouth.x <= detectedRightMouth.x ? detectedLeftMouth : detectedRightMouth;
            var rightMouth = detectedLeftMouth.x <= detectedRightMouth.x ? detectedRightMouth : detectedLeftMouth;

            landmarks[1] = nose;
            landmarks[10] = new Vector2(center.x, bounds.yMin + (bounds.height * 0.9f));
            landmarks[33] = leftEye;
            landmarks[61] = leftMouth;
            landmarks[152] = new Vector2(center.x, bounds.yMin);
            landmarks[263] = rightEye;
            landmarks[291] = rightMouth;
            return landmarks;
        }

        private static Vector2[] BuildLandmarksFromFaceMark(CvPoint[] points, UnityRect bounds, int width, int height)
        {
            var landmarks = new Vector2[478];
            var center = bounds.center;
            for (var i = 0; i < landmarks.Length; i++)
            {
                landmarks[i] = center;
            }

            var leftEyeCenter = AverageNormalized(points, 36, 41, width, height);
            var rightEyeCenter = AverageNormalized(points, 42, 47, width, height);
            if (leftEyeCenter.x > rightEyeCenter.x)
            {
                (leftEyeCenter, rightEyeCenter) = (rightEyeCenter, leftEyeCenter);
            }

            var leftMouth = PointToNormalized(points[48], width, height);
            var rightMouth = PointToNormalized(points[54], width, height);
            if (leftMouth.x > rightMouth.x)
            {
                (leftMouth, rightMouth) = (rightMouth, leftMouth);
            }

            var browCenter = (AverageNormalized(points, 17, 21, width, height) + AverageNormalized(points, 22, 26, width, height)) * 0.5f;
            var eyeCenter = (leftEyeCenter + rightEyeCenter) * 0.5f;
            var forehead = Vector2.Lerp(eyeCenter, browCenter, 1.6f);

            landmarks[1] = PointToNormalized(points[30], width, height);
            landmarks[10] = forehead;
            landmarks[33] = leftEyeCenter;
            landmarks[61] = leftMouth;
            landmarks[152] = PointToNormalized(points[8], width, height);
            landmarks[263] = rightEyeCenter;
            landmarks[291] = rightMouth;
            return landmarks;
        }

        private static Vector2[] BuildDebugLandmarks(CvPoint[] points, int width, int height)
        {
            var landmarks = new Vector2[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                landmarks[i] = PointToNormalized(points[i], width, height);
            }

            return landmarks;
        }

        private static Vector3[] BuildPseudo3DLandmarks(CvPoint[] points, int width, int height)
        {
            var landmarks = new Vector3[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                var point = PointToNormalized(points[i], width, height);
                landmarks[i] = new Vector3(point.x, point.y, 0f);
            }

            return landmarks;
        }

        private static Vector2 AverageNormalized(CvPoint[] points, int startIndex, int endIndex, int width, int height)
        {
            var sum = Vector2.zero;
            var count = 0;
            for (var i = startIndex; i <= endIndex && i < points.Length; i++)
            {
                sum += PointToNormalized(points[i], width, height);
                count++;
            }

            return count > 0 ? sum / count : Vector2.zero;
        }

        private static Vector2 PointToNormalized(CvPoint point, int width, int height)
        {
            return new Vector2(Mathf.Clamp01((float)(point.x / width)), Mathf.Clamp01(1f - (float)(point.y / height)));
        }

        private bool TryEstimateFacePose(CvPoint[] points, int width, int height, out Vector3 eulerDegrees, out Matrix4x4 faceTransform)
        {
            eulerDegrees = Vector3.zero;
            faceTransform = Matrix4x4.identity;
            if (points == null || points.Length < 68 || cameraMatrix == null || distortionCoefficients == null)
            {
                return false;
            }

            using var objectPoints = new MatOfPoint3f(
                new CvPoint3(0.0, 0.0, 0.0),
                new CvPoint3(0.0, -330.0, -65.0),
                new CvPoint3(-225.0, 170.0, -135.0),
                new CvPoint3(225.0, 170.0, -135.0),
                new CvPoint3(-150.0, -150.0, -125.0),
                new CvPoint3(150.0, -150.0, -125.0));
            using var imagePoints = new MatOfPoint2f(
                points[30],
                points[8],
                points[36],
                points[45],
                points[48],
                points[54]);
            using var rotationVector = new Mat(3, 1, CvType.CV_64FC1);
            using var translationVector = new Mat(3, 1, CvType.CV_64FC1);
            if (!Calib3d.solvePnP(objectPoints, imagePoints, cameraMatrix, distortionCoefficients, rotationVector, translationVector, false, Calib3d.SOLVEPNP_ITERATIVE))
            {
                return false;
            }

            using var rotationMatrix = new Mat(3, 3, CvType.CV_64FC1);
            Calib3d.Rodrigues(rotationVector, rotationMatrix);
            eulerDegrees = RotationMatrixToEulerDegrees(rotationMatrix);
            faceTransform = Matrix4x4.TRS(Vector3.zero, Quaternion.Euler(eulerDegrees), Vector3.one);
            return IsFinite(eulerDegrees);
        }

        private static Vector3 RotationMatrixToEulerDegrees(Mat rotationMatrix)
        {
            var data = new double[9];
            rotationMatrix.get(0, 0, data);
            var r00 = data[0];
            var r01 = data[1];
            var r10 = data[3];
            var r11 = data[4];
            var r20 = data[6];
            var r21 = data[7];
            var r22 = data[8];
            var sy = Math.Sqrt((r00 * r00) + (r10 * r10));
            var singular = sy < 1e-6;
            double x;
            double y;
            double z;
            if (!singular)
            {
                x = Math.Atan2(r21, r22);
                y = Math.Atan2(-r20, sy);
                z = Math.Atan2(r10, r00);
            }
            else
            {
                x = Math.Atan2(-data[5], r11);
                y = Math.Atan2(-r20, sy);
                z = 0;
            }

            return new Vector3((float)(x * Mathf.Rad2Deg), (float)(y * Mathf.Rad2Deg), (float)(z * Mathf.Rad2Deg));
        }

        private void RebuildCameraIntrinsics(int width, int height)
        {
            cameraMatrix?.Dispose();
            distortionCoefficients?.Dispose();
            var focalLength = width;
            cameraMatrix = new Mat(3, 3, CvType.CV_64FC1);
            cameraMatrix.put(0, 0,
                focalLength, 0.0, width * 0.5,
                0.0, focalLength, height * 0.5,
                0.0, 0.0, 1.0);
            distortionCoefficients = new MatOfDouble(0.0, 0.0, 0.0, 0.0);
        }

        private static bool IsFinite(Vector3 value)
        {
            return !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }

        private static Vector2 PixelToNormalized(float x, float y, int width, int height)
        {
            return new Vector2(Mathf.Clamp01(x / width), Mathf.Clamp01(1f - (y / height)));
        }
    }
}
#endif
