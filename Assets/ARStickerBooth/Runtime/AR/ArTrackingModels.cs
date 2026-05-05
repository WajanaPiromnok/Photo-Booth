using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace PhotoBooth.Booth.AR
{
    [Flags]
    public enum ArTrackingCapabilities
    {
        None = 0,
        Face2D = 1 << 0,
        Face3D = 1 << 1,
        Hand = 1 << 2,
        Body = 1 << 3
    }

    public enum ArStickerAnchor
    {
        FaceBounds = 0,
        Eyes = 1,
        Forehead = 2,
        Nose = 3,
        Mouth = 4
    }

    public enum ArStickerBuiltinShape
    {
        None = 0,
        Sunglasses = 1,
        Crown = 2,
        Mustache = 3
    }

    [Serializable]
    public sealed class FaceTrack
    {
        public string TrackId;
        public Rect NormalizedBounds;
        public Vector2[] NormalizedLandmarks = Array.Empty<Vector2>();
        public Vector2[] DebugNormalizedLandmarks = Array.Empty<Vector2>();
        public Vector3[] NormalizedLandmarks3D = Array.Empty<Vector3>();
        public bool HasReliableEyeLandmarks;
        public bool HasFaceTransform;
        public Vector3 FaceEulerDegrees;
        public Matrix4x4 FaceTransform = Matrix4x4.identity;
        public float Confidence = 1f;
    }

    [Serializable]
    public sealed class ArTrackingFrame
    {
        public static readonly ArTrackingFrame Empty = new()
        {
            Faces = Array.Empty<FaceTrack>()
        };

        public string ProviderName;
        public long FrameIndex;
        public int PixelWidth;
        public int PixelHeight;
        public double TimestampSeconds;
        public FaceTrack[] Faces = Array.Empty<FaceTrack>();
    }

    public interface IArTrackingProvider : IDisposable
    {
        string ProviderName { get; }
        ArTrackingCapabilities Capabilities { get; }
        bool IsRunning { get; }
        Task StartAsync(CancellationToken cancellationToken = default);
        Task<ArTrackingFrame> TrackAsync(Texture sourceTexture, CancellationToken cancellationToken = default);
        void Stop();
    }
}
