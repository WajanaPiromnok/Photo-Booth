using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhotoBooth.Booth.AR
{
    public sealed class ArTrackingStabilizer
    {
        private const int LeftEyeIndex = 33;
        private const int RightEyeIndex = 263;
        private readonly Dictionary<string, FaceState> states = new();

        // Keep a small amount of filtering for jitter, but favour the newest
        // webcam sample so a face sticker does not visibly trail the user.
        public float PositionAlpha { get; set; } = 0.94f;
        public float RotationAlpha { get; set; } = 0.96f;
        public float ScaleAlpha { get; set; } = 0.96f;
        public float CalibrationDurationSeconds { get; set; } = 1.2f;
        public ArTrackingDebugSnapshot DebugSnapshot { get; private set; } = ArTrackingDebugSnapshot.Empty;

        public ArTrackingFrame Update(ArTrackingFrame frame)
        {
            if (frame?.Faces == null || frame.Faces.Length == 0)
            {
                Reset();
                DebugSnapshot = ArTrackingDebugSnapshot.NoFace(frame);
                return frame ?? ArTrackingFrame.Empty;
            }

            var smoothedFaces = new FaceTrack[frame.Faces.Length];
            var activeKeys = new HashSet<string>();
            for (var i = 0; i < frame.Faces.Length; i++)
            {
                var face = frame.Faces[i];
                var key = string.IsNullOrWhiteSpace(face?.TrackId) ? i.ToString() : face.TrackId;
                activeKeys.Add(key);

                if (!states.TryGetValue(key, out var state))
                {
                    state = new FaceState();
                    states[key] = state;
                }

                smoothedFaces[i] = state.Update(face, frame.TimestampSeconds, PositionAlpha, RotationAlpha, ScaleAlpha, CalibrationDurationSeconds);
            }

            RemoveInactiveStates(activeKeys);
            DebugSnapshot = ArTrackingDebugSnapshot.FromFace(frame, smoothedFaces[0], states.TryGetValue(activeKeys.Count > 0 ? GetFirstKey(activeKeys) : string.Empty, out var firstState) ? firstState : null);

            return new ArTrackingFrame
            {
                ProviderName = frame.ProviderName,
                FrameIndex = frame.FrameIndex,
                PixelWidth = frame.PixelWidth,
                PixelHeight = frame.PixelHeight,
                TimestampSeconds = frame.TimestampSeconds,
                Faces = smoothedFaces
            };
        }

        public void Reset()
        {
            states.Clear();
        }

        private void RemoveInactiveStates(HashSet<string> activeKeys)
        {
            var keysToRemove = new List<string>();
            foreach (var key in states.Keys)
            {
                if (!activeKeys.Contains(key))
                {
                    keysToRemove.Add(key);
                }
            }

            foreach (var key in keysToRemove)
            {
                states.Remove(key);
            }
        }

        private static string GetFirstKey(HashSet<string> keys)
        {
            foreach (var key in keys)
            {
                return key;
            }

            return string.Empty;
        }

        private static float EstimateEyeDistance(FaceTrack face)
        {
            if (face?.NormalizedLandmarks != null
                && face.NormalizedLandmarks.Length > RightEyeIndex)
            {
                return Vector2.Distance(face.NormalizedLandmarks[LeftEyeIndex], face.NormalizedLandmarks[RightEyeIndex]);
            }

            return face != null ? face.NormalizedBounds.width * 0.32f : 0f;
        }

        internal sealed class FaceState
        {
            private bool hasSmoothedFace;
            private FaceTrack smoothedFace;
            private double previousTimestamp;
            private double calibrationSeconds;
            private int calibrationSamples;
            private Vector2 neutralCenterSum;
            private Vector3 neutralEulerSum;
            private float neutralEyeDistanceSum;
            private float calibrationTargetSeconds = 1.2f;

            public bool IsCalibrated { get; private set; }
            public float CalibrationProgress => IsCalibrated ? 1f : Mathf.Clamp01((float)(calibrationSeconds / Mathf.Max(0.01f, calibrationTargetSeconds)));
            public Vector2 NeutralCenter { get; private set; }
            public float NeutralEyeDistance { get; private set; }
            public Vector3 NeutralEulerDegrees { get; private set; }
            public float CurrentEyeDistance { get; private set; }
            public float CurrentScale => NeutralEyeDistance > 0.0001f ? CurrentEyeDistance / NeutralEyeDistance : 1f;

            public FaceTrack Update(FaceTrack current, double timestampSeconds, float positionAlpha, float rotationAlpha, float scaleAlpha, float calibrationDurationSeconds)
            {
                if (current == null)
                {
                    return null;
                }

                calibrationTargetSeconds = Mathf.Max(0.1f, calibrationDurationSeconds);
                var next = Clone(current);
                if (hasSmoothedFace)
                {
                    next.NormalizedBounds = SmoothRect(smoothedFace.NormalizedBounds, current.NormalizedBounds, positionAlpha, scaleAlpha);
                    next.NormalizedLandmarks = SmoothVectors(smoothedFace.NormalizedLandmarks, current.NormalizedLandmarks, positionAlpha);
                    next.DebugNormalizedLandmarks = current.DebugNormalizedLandmarks ?? Array.Empty<Vector2>();
                    next.NormalizedLandmarks3D = SmoothVectors(smoothedFace.NormalizedLandmarks3D, current.NormalizedLandmarks3D, positionAlpha);
                    next.FaceEulerDegrees = SmoothEuler(smoothedFace.FaceEulerDegrees, current.FaceEulerDegrees, rotationAlpha);
                    if (next.HasFaceTransform && smoothedFace.HasFaceTransform)
                    {
                        next.FaceTransform = SmoothTransform(smoothedFace.FaceTransform, current.FaceTransform, positionAlpha, rotationAlpha, scaleAlpha);
                        next.FaceEulerDegrees = NormalizeEuler(next.FaceTransform.rotation.eulerAngles);
                    }

                    next.Confidence = Mathf.Lerp(current.Confidence, smoothedFace.Confidence, scaleAlpha);
                }

                CurrentEyeDistance = EstimateEyeDistance(next);
                CaptureNeutralBaseline(next, timestampSeconds, calibrationDurationSeconds);
                if (IsCalibrated && next.HasFaceTransform)
                {
                    next.FaceEulerDegrees = new Vector3(
                        NormalizeAngle(next.FaceEulerDegrees.x - NeutralEulerDegrees.x),
                        NormalizeAngle(next.FaceEulerDegrees.y - NeutralEulerDegrees.y),
                        NormalizeAngle(next.FaceEulerDegrees.z - NeutralEulerDegrees.z));
                }

                smoothedFace = next;
                hasSmoothedFace = true;
                previousTimestamp = timestampSeconds;
                return Clone(next);
            }

            private void CaptureNeutralBaseline(FaceTrack face, double timestampSeconds, float calibrationDurationSeconds)
            {
                if (IsCalibrated)
                {
                    return;
                }

                var delta = previousTimestamp > 0d ? Mathf.Clamp((float)(timestampSeconds - previousTimestamp), 0.033f, 0.25f) : 0.033f;
                calibrationSeconds += delta;
                calibrationSamples++;
                neutralCenterSum += face.NormalizedBounds.center;
                neutralEyeDistanceSum += CurrentEyeDistance;
                neutralEulerSum += face.FaceEulerDegrees;

                if (calibrationSeconds < Mathf.Max(0.1f, calibrationDurationSeconds))
                {
                    return;
                }

                var sampleCount = Mathf.Max(1, calibrationSamples);
                NeutralCenter = neutralCenterSum / sampleCount;
                NeutralEyeDistance = neutralEyeDistanceSum / sampleCount;
                NeutralEulerDegrees = neutralEulerSum / sampleCount;
                IsCalibrated = true;
            }
        }

        public sealed class ArTrackingDebugSnapshot
        {
            public static readonly ArTrackingDebugSnapshot Empty = new();

            public bool FaceFound;
            public int FaceCount;
            public string ProviderName;
            public float Confidence;
            public bool IsCalibrated;
            public float CalibrationProgress;
            public float EyeDistance;
            public float NeutralEyeDistance;
            public float Scale;
            public Vector3 EulerDegrees;
            public double TimestampSeconds;

            internal static ArTrackingDebugSnapshot NoFace(ArTrackingFrame frame)
            {
                return new ArTrackingDebugSnapshot
                {
                    FaceFound = false,
                    FaceCount = 0,
                    ProviderName = frame?.ProviderName,
                    TimestampSeconds = frame?.TimestampSeconds ?? 0d
                };
            }

            internal static ArTrackingDebugSnapshot FromFace(ArTrackingFrame frame, FaceTrack face, FaceState state)
            {
                if (face == null)
                {
                    return NoFace(frame);
                }

                return new ArTrackingDebugSnapshot
                {
                    FaceFound = true,
                    FaceCount = frame?.Faces?.Length ?? 0,
                    ProviderName = frame?.ProviderName,
                    Confidence = face.Confidence,
                    IsCalibrated = state?.IsCalibrated ?? false,
                    CalibrationProgress = state?.CalibrationProgress ?? 0f,
                    EyeDistance = state?.CurrentEyeDistance ?? EstimateEyeDistance(face),
                    NeutralEyeDistance = state?.NeutralEyeDistance ?? 0f,
                    Scale = state?.CurrentScale ?? 1f,
                    EulerDegrees = face.FaceEulerDegrees,
                    TimestampSeconds = frame?.TimestampSeconds ?? 0d
                };
            }
        }

        private static FaceTrack Clone(FaceTrack source)
        {
            if (source == null)
            {
                return null;
            }

            return new FaceTrack
            {
                TrackId = source.TrackId,
                NormalizedBounds = source.NormalizedBounds,
                NormalizedLandmarks = CloneArray(source.NormalizedLandmarks),
                DebugNormalizedLandmarks = CloneArray(source.DebugNormalizedLandmarks),
                NormalizedLandmarks3D = CloneArray(source.NormalizedLandmarks3D),
                HasReliableEyeLandmarks = source.HasReliableEyeLandmarks,
                HasFaceTransform = source.HasFaceTransform,
                FaceEulerDegrees = source.FaceEulerDegrees,
                FaceTransform = source.FaceTransform,
                Confidence = source.Confidence
            };
        }

        private static Vector2[] CloneArray(Vector2[] source)
        {
            return source == null || source.Length == 0 ? Array.Empty<Vector2>() : (Vector2[])source.Clone();
        }

        private static Vector3[] CloneArray(Vector3[] source)
        {
            return source == null || source.Length == 0 ? Array.Empty<Vector3>() : (Vector3[])source.Clone();
        }

        private static Rect SmoothRect(Rect previous, Rect current, float positionAlpha, float scaleAlpha)
        {
            var center = Vector2.Lerp(current.center, previous.center, positionAlpha);
            var size = Vector2.Lerp(current.size, previous.size, scaleAlpha);
            return new Rect(center - (size * 0.5f), size);
        }

        private static Vector2[] SmoothVectors(Vector2[] previous, Vector2[] current, float alpha)
        {
            if (current == null || current.Length == 0)
            {
                return Array.Empty<Vector2>();
            }

            if (previous == null || previous.Length != current.Length)
            {
                return CloneArray(current);
            }

            var result = new Vector2[current.Length];
            for (var i = 0; i < current.Length; i++)
            {
                result[i] = Vector2.Lerp(current[i], previous[i], alpha);
            }

            return result;
        }

        private static Vector3[] SmoothVectors(Vector3[] previous, Vector3[] current, float alpha)
        {
            if (current == null || current.Length == 0)
            {
                return Array.Empty<Vector3>();
            }

            if (previous == null || previous.Length != current.Length)
            {
                return CloneArray(current);
            }

            var result = new Vector3[current.Length];
            for (var i = 0; i < current.Length; i++)
            {
                result[i] = Vector3.Lerp(current[i], previous[i], alpha);
            }

            return result;
        }

        private static Vector3 SmoothEuler(Vector3 previous, Vector3 current, float alpha)
        {
            var t = 1f - Mathf.Clamp01(alpha);
            return new Vector3(
                Mathf.LerpAngle(previous.x, current.x, t),
                Mathf.LerpAngle(previous.y, current.y, t),
                Mathf.LerpAngle(previous.z, current.z, t));
        }

        private static Matrix4x4 SmoothTransform(Matrix4x4 previous, Matrix4x4 current, float positionAlpha, float rotationAlpha, float scaleAlpha)
        {
            var positionT = 1f - Mathf.Clamp01(positionAlpha);
            var rotationT = 1f - Mathf.Clamp01(rotationAlpha);
            var scaleT = 1f - Mathf.Clamp01(scaleAlpha);
            var position = Vector3.Lerp(GetTranslation(previous), GetTranslation(current), positionT);
            var rotation = Quaternion.Slerp(previous.rotation, current.rotation, rotationT);
            var scale = Vector3.Lerp(GetScale(previous), GetScale(current), scaleT);
            return Matrix4x4.TRS(position, rotation, scale);
        }

        private static Vector3 GetTranslation(Matrix4x4 matrix)
        {
            return new Vector3(matrix.m03, matrix.m13, matrix.m23);
        }

        private static Vector3 GetScale(Matrix4x4 matrix)
        {
            return new Vector3(
                new Vector3(matrix.m00, matrix.m10, matrix.m20).magnitude,
                new Vector3(matrix.m01, matrix.m11, matrix.m21).magnitude,
                new Vector3(matrix.m02, matrix.m12, matrix.m22).magnitude);
        }

        private static Vector3 NormalizeEuler(Vector3 euler)
        {
            return new Vector3(NormalizeAngle(euler.x), NormalizeAngle(euler.y), NormalizeAngle(euler.z));
        }

        private static float NormalizeAngle(float angle)
        {
            while (angle > 180f)
            {
                angle -= 360f;
            }

            while (angle < -180f)
            {
                angle += 360f;
            }

            return angle;
        }
    }
}
