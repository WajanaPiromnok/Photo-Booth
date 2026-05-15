using System;
using System.Collections.Generic;
using PhotoBooth.Booth.AR;
using UnityEngine;
using UnityEngine.UI;

namespace PhotoBooth.Booth.Frontend
{
    public enum Tracked3dFaceModelAlignmentMode
    {
        CanonicalMatrix = 0,
        LandmarkAnchored = 1
    }

    public enum Tracked3dFacePartAnchor
    {
        FaceMaskCenter = 0,
        HeadCenter = 1,
        Eyes = 2,
        LeftEye = 3,
        RightEye = 4,
        Nose = 5,
        Mouth = 6,
        Forehead = 7,
        Chin = 8,
        LeftCheek = 9,
        RightCheek = 10
    }

    public readonly struct ArFaceProjectionGeometry
    {
        public ArFaceProjectionGeometry(int sourceWidth, int sourceHeight, int renderWidth, int renderHeight, Rect viewportRect)
        {
            SourceWidth = Mathf.Max(1, sourceWidth);
            SourceHeight = Mathf.Max(1, sourceHeight);
            RenderWidth = Mathf.Max(1, renderWidth);
            RenderHeight = Mathf.Max(1, renderHeight);
            ViewportRect = viewportRect.width > 0f && viewportRect.height > 0f
                ? viewportRect
                : new Rect(0f, 0f, RenderWidth, RenderHeight);
        }

        public int SourceWidth { get; }
        public int SourceHeight { get; }
        public int RenderWidth { get; }
        public int RenderHeight { get; }
        public Rect ViewportRect { get; }
        public bool IsValid => SourceWidth > 0 && SourceHeight > 0 && RenderWidth > 0 && RenderHeight > 0 && ViewportRect.width > 0f && ViewportRect.height > 0f;

        public static ArFaceProjectionGeometry CreateStretched(int sourceWidth, int sourceHeight, int renderWidth, int renderHeight)
        {
            var resolvedRenderWidth = Mathf.Max(1, renderWidth);
            var resolvedRenderHeight = Mathf.Max(1, renderHeight);
            return new ArFaceProjectionGeometry(
                sourceWidth,
                sourceHeight,
                resolvedRenderWidth,
                resolvedRenderHeight,
                new Rect(0f, 0f, resolvedRenderWidth, resolvedRenderHeight));
        }

        public Vector2 ProjectToRenderNormalized(Vector2 normalizedPoint)
        {
            var pixelX = ViewportRect.xMin + (Mathf.Clamp01(normalizedPoint.x) * ViewportRect.width);
            var pixelY = ViewportRect.yMin + (Mathf.Clamp01(normalizedPoint.y) * ViewportRect.height);
            return new Vector2(pixelX / RenderWidth, pixelY / RenderHeight);
        }

        public Vector3 ProjectToWorld(
            Vector2 normalizedPoint,
            float normalizedDepth,
            float orthographicSize,
            float baseDepth,
            float depthScale)
        {
            var renderNormalizedPoint = ProjectToRenderNormalized(normalizedPoint);
            var aspect = RenderHeight > 0 ? RenderWidth / (float)RenderHeight : 1f;
            var worldHeight = orthographicSize * 2f;
            var worldWidth = worldHeight * aspect;
            return new Vector3(
                (renderNormalizedPoint.x - 0.5f) * worldWidth,
                (renderNormalizedPoint.y - 0.5f) * worldHeight,
                baseDepth + (normalizedDepth * depthScale));
        }
    }

    [Serializable]
    public sealed class Tracked3dFacePartBinding
    {
        public string name;
        public Tracked3dFacePartAnchor anchor = Tracked3dFacePartAnchor.Eyes;
        public GameObject prefab;
        public string resourcePath;
        public bool visible = true;
        public float scaleMultiplier = 1f;
        public Vector3 localPositionOffset = Vector3.zero;
        public Vector3 localEulerOffset = Vector3.zero;
        public Vector3 localScale = Vector3.one;
        public bool mirrorX;
        public bool preservePrefabLocalTransform;
    }

    public sealed class ArPreviewFaceModelRig : MonoBehaviour
    {
        private const int DefaultArModelLayer = 30;

        [SerializeField] private RawImage targetOverlay;
        [SerializeField] private string defaultModelResourcePath = "ARStickerBooth/FaceAssets/3d_face_quads_v310";
        [SerializeField] private GameObject faceModelPrefab;
        [SerializeField] private bool loadDefaultModel = true;
        [SerializeField] private bool showGuideModel = true;
        [SerializeField] private int arModelLayer = DefaultArModelLayer;
        [SerializeField] private Vector2Int renderTextureSize = new(1280, 720);
        [SerializeField] private float overlayOrthographicSize = 5f;
        [SerializeField] private string modelAttachAnchorName = "FaceMaskCenter";
        [SerializeField] private float faceDepth = 0f;
        [SerializeField] private float faceDepthScale = 1.4f;
        // Scale multiplier for the 3D face model based on eye distance.
        // Diagnosis 2026-05-07: 2D tracking is accurate, adjust this only after localPositionOffset is correct.
        [SerializeField] private float modelScaleMultiplier = 1.9f;
        // Weight for canonical matrix scale (0=landmark-based, 1=canonical matrix scale).
        [SerializeField] private float canonicalMatrixScaleWeight = 0.35f;
        [SerializeField] private float neutralFaceMaskNoseBlend = 0.38f;
        [SerializeField] private float sideFaceMaskNoseBlend = 0.85f;
        [SerializeField] private float fullSideYawDegrees = 35f;
        [SerializeField] private Vector2 faceMaskScreenOffsetByEyeDistance = Vector2.zero;
        // Local position offset applied to model relative to FaceMaskCenter anchor.
        // Diagnosis 2026-05-07: Model appeared offset to bottom-right. Fixed by moving LEFT (negative X) and UP (positive Y).
        // Adjustment guide: X- = left, X+ = right, Y+ = up, Y- = down, Z = depth (adjust last).
        [SerializeField] private Vector3 modelLocalPositionOffset = new(-0.8f, 0.6f, 0f);
        // Local rotation offset (degrees) applied to model. Y=180° flips model to face camera.
        [SerializeField] private Vector3 modelLocalEulerOffset = new(0f, 180f, 0f);
        // Base local scale applied before modelScaleMultiplier. Adjust AFTER localPositionOffset is correct.
        [SerializeField] private Vector3 modelLocalScaleOffset = new(9f, 9f, 9f);
        [SerializeField] private bool mirrorModelX = true;
        [SerializeField] private bool invertFaceYaw = true;
        [SerializeField] private bool preserveExistingModelLocalTransform;
        [SerializeField] private Tracked3dFaceModelAlignmentMode alignmentMode = Tracked3dFaceModelAlignmentMode.CanonicalMatrix;
        [SerializeField] private Tracked3dFacePartBinding[] facePartModels = Array.Empty<Tracked3dFacePartBinding>();
        [SerializeField] private bool showRuntimeDebugInfo;

        private readonly Dictionary<string, Transform> anchors = new();
        private readonly Dictionary<int, Transform> facePartRoots = new();
        private readonly Dictionary<int, GameObject> facePartInstances = new();
        private Camera overlayCamera;
        private RenderTexture renderTexture;
        private Transform rigRoot;
        private GameObject modelInstance;
        private bool modelInstanceWasReused;
        private bool loggedModelLoaded;
        private bool loggedModelVisible;
        private bool loggedModelCalibration;
        private bool editorPreviewMode;
        private int currentSourceWidth;
        private int currentSourceHeight;
        private int currentRenderWidth;
        private int currentRenderHeight;
        private ArFaceProjectionGeometry currentProjectionGeometry;

        // Debug values for runtime display
        private float lastEyeWorldDistance;
        private float lastCalculatedScale;
        private Vector3 lastFaceMaskCenter;
        private Vector3 lastHeadEulerDegrees;
        private Tracked3dFaceModelAlignmentMode lastAlignmentMode;
        private Vector2Int lastSourceSize;
        private Vector2Int lastRenderSize;
        private Rect lastViewportRect;
        private Bounds lastModelBounds;
        private bool lastHasModelBounds;

        public Transform HeadCenter => GetAnchor("HeadCenter");
        public Transform FaceMaskCenter => GetAnchor("FaceMaskCenter");
        public Transform Eyes => GetAnchor("Eyes");
        public Transform LeftEye => GetAnchor("LeftEye");
        public Transform RightEye => GetAnchor("RightEye");
        public Transform Nose => GetAnchor("Nose");
        public Transform Mouth => GetAnchor("Mouth");
        public Transform Forehead => GetAnchor("Forehead");
        public Transform LeftCheek => GetAnchor("LeftCheek");
        public Transform RightCheek => GetAnchor("RightCheek");
        public Transform Chin => GetAnchor("Chin");

        public Tracked3dFaceModelAlignmentMode AlignmentMode
        {
            get => alignmentMode;
            set => alignmentMode = value;
        }

        public float ModelScaleMultiplier
        {
            get => modelScaleMultiplier;
            set => modelScaleMultiplier = Mathf.Max(0.01f, value);
        }

        public Vector3 ModelLocalPositionOffset
        {
            get => modelLocalPositionOffset;
            set => modelLocalPositionOffset = value;
        }

        public Vector3 ModelLocalEulerOffset
        {
            get => modelLocalEulerOffset;
            set => modelLocalEulerOffset = value;
        }

        public Vector3 ModelLocalScaleOffset
        {
            get => modelLocalScaleOffset;
            set => modelLocalScaleOffset = value;
        }

        public float NeutralFaceMaskNoseBlend
        {
            get => neutralFaceMaskNoseBlend;
            set => neutralFaceMaskNoseBlend = Mathf.Clamp01(value);
        }

        public float SideFaceMaskNoseBlend
        {
            get => sideFaceMaskNoseBlend;
            set => sideFaceMaskNoseBlend = Mathf.Clamp01(value);
        }

        public float FullSideYawDegrees
        {
            get => fullSideYawDegrees;
            set => fullSideYawDegrees = Mathf.Max(6f, value);
        }

        public Vector2 FaceMaskScreenOffsetByEyeDistance
        {
            get => faceMaskScreenOffsetByEyeDistance;
            set => faceMaskScreenOffsetByEyeDistance = value;
        }

        public bool MirrorModelX
        {
            get => mirrorModelX;
            set => mirrorModelX = value;
        }

        public bool InvertFaceYaw
        {
            get => invertFaceYaw;
            set => invertFaceYaw = value;
        }

        public bool PreserveExistingModelLocalTransform
        {
            get => preserveExistingModelLocalTransform;
            set => preserveExistingModelLocalTransform = value;
        }

        public bool ShowGuideModel
        {
            get => showGuideModel;
            set
            {
                showGuideModel = value;
                if (modelInstance != null)
                {
                    modelInstance.SetActive(showGuideModel);
                }
            }
        }

        public bool ShowRuntimeDebugInfo
        {
            get => showRuntimeDebugInfo;
            set => showRuntimeDebugInfo = value;
        }

        public Tracked3dFacePartBinding[] FacePartModels
        {
            get => facePartModels;
            set => facePartModels = value ?? Array.Empty<Tracked3dFacePartBinding>();
        }

        // Debug values for external access
        public float LastEyeWorldDistance => lastEyeWorldDistance;
        public float LastCalculatedScale => lastCalculatedScale;
        public Vector3 LastFaceMaskCenter => lastFaceMaskCenter;
        public Vector3 LastHeadEulerDegrees => lastHeadEulerDegrees;
        public Tracked3dFaceModelAlignmentMode LastAlignmentMode => lastAlignmentMode;
        public ArFaceProjectionGeometry LastProjectionGeometry => currentProjectionGeometry;
        public Vector2Int LastSourceSize => lastSourceSize;
        public Vector2Int LastRenderSize => lastRenderSize;
        public Rect LastViewportRect => lastViewportRect;
        public bool LastHasModelBounds => lastHasModelBounds;
        public Bounds LastModelBounds => lastModelBounds;

        public static Vector2 NormalizedToOverlayPosition(Vector2 normalizedPoint, Rect overlayRect)
        {
            return new Vector2(
                (normalizedPoint.x - 0.5f) * overlayRect.width,
                (normalizedPoint.y - 0.5f) * overlayRect.height);
        }

        public static Vector2 ApplyFaceMaskScreenOffset(Vector2 normalizedPoint, Vector2 eyeDistanceOffset, float normalizedEyeDistance)
        {
            return normalizedPoint + (eyeDistanceOffset * Mathf.Max(0f, normalizedEyeDistance));
        }

        public static Vector3 NormalizedToWorldPosition(
            Vector2 normalizedPoint,
            float normalizedDepth,
            int renderWidth,
            int renderHeight,
            float orthographicSize,
            float baseDepth,
            float depthScale)
        {
            var aspect = renderHeight > 0 ? renderWidth / (float)renderHeight : 1f;
            var worldHeight = orthographicSize * 2f;
            var worldWidth = worldHeight * aspect;
            return new Vector3(
                (normalizedPoint.x - 0.5f) * worldWidth,
                (normalizedPoint.y - 0.5f) * worldHeight,
                baseDepth + (normalizedDepth * depthScale));
        }

        public static void AlphaBlendOverlay(Texture2D target, Color32[] overlayPixels)
        {
            if (target == null || overlayPixels == null || overlayPixels.Length != target.width * target.height)
            {
                return;
            }

            var targetPixels = target.GetPixels32();
            for (var i = 0; i < targetPixels.Length; i++)
            {
                var overlay = overlayPixels[i];
                if (overlay.a == 0)
                {
                    continue;
                }

                if (overlay.a == 255)
                {
                    targetPixels[i] = overlay;
                    continue;
                }

                var alpha = overlay.a / 255f;
                var inverse = 1f - alpha;
                var basePixel = targetPixels[i];
                targetPixels[i] = new Color32(
                    (byte)Mathf.Clamp(Mathf.RoundToInt((overlay.r * alpha) + (basePixel.r * inverse)), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((overlay.g * alpha) + (basePixel.g * inverse)), 0, 255),
                    (byte)Mathf.Clamp(Mathf.RoundToInt((overlay.b * alpha) + (basePixel.b * inverse)), 0, 255),
                    255);
            }

            target.SetPixels32(targetPixels);
            target.Apply(false, false);
        }

        public static Tracked3dFaceModelAlignmentMode ResolveEffectiveAlignmentMode(
            Tracked3dFaceModelAlignmentMode requestedMode,
            FaceTrack face)
        {
            return requestedMode == Tracked3dFaceModelAlignmentMode.CanonicalMatrix
                && face != null
                && !face.HasFaceTransform
                    ? Tracked3dFaceModelAlignmentMode.LandmarkAnchored
                    : requestedMode;
        }

        public bool TryGetGuideModelLocalTransform(out Vector3 localPosition, out Quaternion localRotation, out Vector3 localScale)
        {
            var model = modelInstance != null ? modelInstance : FindAnyModelInstanceInRig();
            if (model == null)
            {
                localPosition = default;
                localRotation = Quaternion.identity;
                localScale = Vector3.one;
                return false;
            }

            modelInstance = model;
            localPosition = model.transform.localPosition;
            localRotation = model.transform.localRotation;
            localScale = model.transform.localScale;
            return true;
        }

        public void ApplyGuideModelLocalTransform(Vector3 localPosition, Quaternion localRotation, Vector3 localScale)
        {
            EnsureCameraAndTexture();
            EnsureAnchors();
            EnsureDefaultModel();
            if (modelInstance == null)
            {
                return;
            }

            modelInstance.transform.localPosition = localPosition;
            modelInstance.transform.localRotation = localRotation;
            modelInstance.transform.localScale = localScale;
            modelInstanceWasReused = true;
            preserveExistingModelLocalTransform = true;
        }

        public void Initialize(RawImage overlay, GameObject prefabOverride = null, bool editorPreview = false)
        {
            targetOverlay = overlay;
            editorPreviewMode = editorPreview;
            if (prefabOverride != null)
            {
                faceModelPrefab = prefabOverride;
            }

            EnsureCameraAndTexture(matchTargetOverlayRect: true);
            EnsureAnchors();
            if (showGuideModel)
            {
                EnsureDefaultModel();
            }

            Hide();
        }

        public Transform GetAnchor(string anchorName)
        {
            EnsureCameraAndTexture(matchTargetOverlayRect: true);
            EnsureAnchors();
            if (!anchors.ContainsKey(anchorName) || anchors[anchorName] == null)
            {
                EnsureAnchor(anchorName, rigRoot);
            }

            return anchors[anchorName];
        }

        public void ApplyFace(FaceTrack face)
        {
            var width = currentSourceWidth > 0 ? currentSourceWidth : renderTextureSize.x;
            var height = currentSourceHeight > 0 ? currentSourceHeight : renderTextureSize.y;
            ApplyFace(face, width, height);
        }

        public void ApplyFace(FaceTrack face, int sourceWidth, int sourceHeight)
        {
            if (face == null)
            {
                Hide();
                return;
            }

            var geometry = CreateLivePreviewProjectionGeometry(sourceWidth, sourceHeight);
            ApplyFace(face, geometry);
        }

        public void ApplyFace(FaceTrack face, ArFaceProjectionGeometry geometry)
        {
            if (face == null)
            {
                Hide();
                return;
            }

            if (!geometry.IsValid)
            {
                Hide();
                return;
            }

            EnsureCameraAndTexture(geometry);
            if (!ApplyFaceTransform(face, geometry, out var eyeWorldDistance, enableModel: true))
            {
                Hide();
                return;
            }

            if (targetOverlay != null)
            {
                targetOverlay.enabled = true;
                targetOverlay.color = Color.white;
            }

            rigRoot.gameObject.SetActive(true);
            overlayCamera.gameObject.SetActive(true);
            overlayCamera.enabled = true;
            if (!loggedModelVisible && modelInstance != null && showGuideModel)
            {
                loggedModelVisible = true;
                Debug.Log($"PhotoBooth AR face model visible: position={FaceMaskCenter.position}, scale={FaceMaskCenter.localScale.x:0.000}, eyeWorldDistance={eyeWorldDistance:0.000}, source={geometry.SourceWidth}x{geometry.SourceHeight}, render={geometry.RenderWidth}x{geometry.RenderHeight}, viewport={geometry.ViewportRect}, mode={ResolveEffectiveAlignmentMode(alignmentMode, face)}");
            }
        }

        public bool CompositeFaceModel(Texture2D target, FaceTrack face)
        {
            if (target == null)
            {
                return false;
            }

            var geometry = ArFaceProjectionGeometry.CreateStretched(target.width, target.height, target.width, target.height);
            return CompositeFaceModel(target, face, geometry);
        }

        public bool CompositeFaceModel(Texture2D target, FaceTrack face, ArFaceProjectionGeometry geometry)
        {
            if ((!showGuideModel && !HasFacePartModels()) || target == null || face == null)
            {
                return false;
            }

            if (!geometry.IsValid || geometry.RenderWidth != target.width || geometry.RenderHeight != target.height)
            {
                geometry = ArFaceProjectionGeometry.CreateStretched(
                    geometry.IsValid ? geometry.SourceWidth : target.width,
                    geometry.IsValid ? geometry.SourceHeight : target.height,
                    target.width,
                    target.height);
            }

            EnsureCameraAndTexture(geometry);
            if (!ApplyFaceTransform(face, geometry, out _, enableModel: true))
            {
                return false;
            }

            var temporary = RenderTexture.GetTemporary(target.width, target.height, 24, RenderTextureFormat.ARGB32);
            var previousTarget = overlayCamera.targetTexture;
            var previousActive = RenderTexture.active;
            var wasEnabled = overlayCamera.enabled;
            try
            {
                overlayCamera.enabled = false;
                overlayCamera.targetTexture = temporary;
                overlayCamera.Render();

                var overlay = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
                try
                {
                    RenderTexture.active = temporary;
                    overlay.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                    overlay.Apply(false, false);
                    AlphaBlendOverlay(target, overlay.GetPixels32());
                }
                finally
                {
                    DestroyUnityObject(overlay);
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                overlayCamera.targetTexture = previousTarget != null ? previousTarget : renderTexture;
                overlayCamera.enabled = wasEnabled;
                RenderTexture.ReleaseTemporary(temporary);
            }

            return true;
        }

        public void ApplyEditorPreview()
        {
            var landmarks = new Vector2[478];
            var landmarks3D = new Vector3[478];
            var bounds = new Rect(0.34f, 0.2f, 0.32f, 0.58f);
            var center = bounds.center;
            for (var index = 0; index < landmarks.Length; index++)
            {
                landmarks[index] = center;
                landmarks3D[index] = new Vector3(center.x, center.y, 0f);
            }

            SetLandmark(landmarks, landmarks3D, 10, center.x, bounds.yMin + (bounds.height * 0.94f));
            SetLandmark(landmarks, landmarks3D, 152, center.x, bounds.yMin + (bounds.height * 0.04f));
            SetLandmark(landmarks, landmarks3D, 234, bounds.xMin + (bounds.width * 0.08f), center.y);
            SetLandmark(landmarks, landmarks3D, 454, bounds.xMax - (bounds.width * 0.08f), center.y);
            SetLandmark(landmarks, landmarks3D, 1, center.x, bounds.yMin + (bounds.height * 0.52f));
            SetLandmark(landmarks, landmarks3D, 33, bounds.xMin + (bounds.width * 0.34f), bounds.yMin + (bounds.height * 0.68f));
            SetLandmark(landmarks, landmarks3D, 133, bounds.xMin + (bounds.width * 0.44f), bounds.yMin + (bounds.height * 0.68f));
            SetLandmark(landmarks, landmarks3D, 159, bounds.xMin + (bounds.width * 0.39f), bounds.yMin + (bounds.height * 0.72f));
            SetLandmark(landmarks, landmarks3D, 362, bounds.xMin + (bounds.width * 0.56f), bounds.yMin + (bounds.height * 0.68f));
            SetLandmark(landmarks, landmarks3D, 263, bounds.xMin + (bounds.width * 0.66f), bounds.yMin + (bounds.height * 0.68f));
            SetLandmark(landmarks, landmarks3D, 386, bounds.xMin + (bounds.width * 0.61f), bounds.yMin + (bounds.height * 0.72f));
            SetLandmark(landmarks, landmarks3D, 13, center.x, bounds.yMin + (bounds.height * 0.36f));
            SetLandmark(landmarks, landmarks3D, 14, center.x, bounds.yMin + (bounds.height * 0.32f));
            SetLandmark(landmarks, landmarks3D, 61, bounds.xMin + (bounds.width * 0.38f), bounds.yMin + (bounds.height * 0.35f));
            SetLandmark(landmarks, landmarks3D, 291, bounds.xMin + (bounds.width * 0.62f), bounds.yMin + (bounds.height * 0.35f));

            ApplyFace(new FaceTrack
            {
                TrackId = "editor-preview-face",
                NormalizedBounds = bounds,
                NormalizedLandmarks = landmarks,
                DebugNormalizedLandmarks = landmarks,
                NormalizedLandmarks3D = landmarks3D,
                HasReliableEyeLandmarks = true,
                HasFaceTransform = true,
                FaceEulerDegrees = Vector3.zero,
                FaceTransform = Matrix4x4.identity,
                Confidence = 1f
            }, renderTextureSize.x, renderTextureSize.y);
        }

        public void Hide()
        {
            if (modelInstance != null)
            {
                modelInstance.SetActive(false);
            }

            HideFacePartModels();

            if (targetOverlay != null)
            {
                targetOverlay.enabled = false;
            }

            if (rigRoot != null)
            {
                rigRoot.gameObject.SetActive(false);
            }

            if (overlayCamera != null)
            {
                overlayCamera.enabled = false;
            }
        }

        private void OnDestroy()
        {
            ReleaseRenderTexture();
        }

        private bool ApplyFaceTransform(FaceTrack face, ArFaceProjectionGeometry geometry, out float eyeWorldDistance, bool enableModel)
        {
            eyeWorldDistance = 0f;
            if (face == null || !geometry.IsValid)
            {
                return false;
            }

            EnsureAnchors();
            if (showGuideModel)
            {
                EnsureDefaultModel();
            }

            var landmarks = face.NormalizedLandmarks;
            var landmarks3D = face.NormalizedLandmarks3D;
            var leftEyeFallback = new Vector2(face.NormalizedBounds.xMin + (face.NormalizedBounds.width * 0.66f), face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f));
            var rightEyeFallback = new Vector2(face.NormalizedBounds.xMin + (face.NormalizedBounds.width * 0.34f), face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f));
            var noseFallback = new Vector2(face.NormalizedBounds.center.x, face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.55f));
            var mouthFallback = new Vector2(face.NormalizedBounds.center.x, face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.32f));
            var foreheadFallback = new Vector2(face.NormalizedBounds.center.x, face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.9f));
            var chinFallback = new Vector2(face.NormalizedBounds.center.x, face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.08f));
            var leftEyePoint = AverageOrFallback(landmarks, leftEyeFallback, 362, 263, 386);
            var rightEyePoint = AverageOrFallback(landmarks, rightEyeFallback, 33, 133, 159);
            var eyeCenter = (leftEyePoint + rightEyePoint) * 0.5f;
            var eyeDistanceNormalized = Vector2.Distance(leftEyePoint, rightEyePoint);
            var nosePoint = PointOrFallback(landmarks, 1, noseFallback);
            var effectiveMode = ResolveEffectiveAlignmentMode(alignmentMode, face);
            var rotation = ResolveRotation(face, effectiveMode);
            var yawT = Mathf.InverseLerp(5f, Mathf.Max(6f, fullSideYawDegrees), Mathf.Abs(ResolveYawDegrees(face)));
            var faceMaskNoseBlend = Mathf.Lerp(
                Mathf.Clamp01(neutralFaceMaskNoseBlend),
                Mathf.Clamp01(sideFaceMaskNoseBlend),
                yawT);
            var faceMaskCenter = ApplyFaceMaskScreenOffset(
                Vector2.Lerp(eyeCenter, nosePoint, faceMaskNoseBlend),
                faceMaskScreenOffsetByEyeDistance,
                eyeDistanceNormalized);

            SetAnchor("HeadCenter", AverageOrFallback(landmarks, face.NormalizedBounds.center, 10, 152, 234, 454, 1), ResolveDepth(landmarks3D, 1), rotation, geometry);
            SetAnchor("FaceMaskCenter", faceMaskCenter, ResolveDepth(landmarks3D, 1), rotation, geometry);
            SetAnchor("Eyes", eyeCenter, (ResolveDepth(landmarks3D, 263) + ResolveDepth(landmarks3D, 33)) * 0.5f, rotation, geometry);
            SetAnchor("LeftEye", leftEyePoint, ResolveDepth(landmarks3D, 263), rotation, geometry);
            SetAnchor("RightEye", rightEyePoint, ResolveDepth(landmarks3D, 33), rotation, geometry);
            SetAnchor("Nose", nosePoint, ResolveDepth(landmarks3D, 1), rotation, geometry);
            SetAnchor("Mouth", AverageOrFallback(landmarks, mouthFallback, 13, 14, 61, 291), ResolveDepth(landmarks3D, 13), rotation, geometry);
            SetAnchor("Forehead", PointOrFallback(landmarks, 10, foreheadFallback), ResolveDepth(landmarks3D, 10), rotation, geometry);
            SetAnchor("LeftCheek", PointOrFallback(landmarks, 454, new Vector2(face.NormalizedBounds.xMax, face.NormalizedBounds.center.y)), ResolveDepth(landmarks3D, 454), rotation, geometry);
            SetAnchor("RightCheek", PointOrFallback(landmarks, 234, new Vector2(face.NormalizedBounds.xMin, face.NormalizedBounds.center.y)), ResolveDepth(landmarks3D, 234), rotation, geometry);
            SetAnchor("Chin", PointOrFallback(landmarks, 152, chinFallback), ResolveDepth(landmarks3D, 152), rotation, geometry);

            eyeWorldDistance = Vector3.Distance(LeftEye.position, RightEye.position);
            var matrixScale = effectiveMode == Tracked3dFaceModelAlignmentMode.CanonicalMatrix
                ? ResolveUniformScale(face.FaceTransform)
                : 1f;
            var canonicalScale = Mathf.Lerp(1f, matrixScale, Mathf.Clamp01(canonicalMatrixScaleWeight));
            var calculatedScale = eyeWorldDistance * modelScaleMultiplier * canonicalScale;
            FaceMaskCenter.localScale = Vector3.one * Mathf.Max(0.0001f, calculatedScale);

            // Store debug values for runtime display
            lastEyeWorldDistance = eyeWorldDistance;
            lastCalculatedScale = FaceMaskCenter.localScale.x;
            lastFaceMaskCenter = FaceMaskCenter.position;
            lastHeadEulerDegrees = face.FaceEulerDegrees;
            lastAlignmentMode = effectiveMode;
            lastSourceSize = new Vector2Int(geometry.SourceWidth, geometry.SourceHeight);
            lastRenderSize = new Vector2Int(geometry.RenderWidth, geometry.RenderHeight);
            lastViewportRect = geometry.ViewportRect;

            rigRoot.gameObject.SetActive(true);

            if (modelInstance != null)
            {
                if (modelInstance.transform.parent != FaceMaskCenter)
                {
                    modelInstance.transform.SetParent(FaceMaskCenter, false);
                }

                if (!preserveExistingModelLocalTransform || !modelInstanceWasReused)
                {
                    ApplyModelLocalCalibration();
                }

                modelInstance.SetActive(enableModel && showGuideModel);
            }

            ApplyFacePartModels(eyeWorldDistance, enableModel);
            CaptureModelBoundsDebugInfo();

            return true;
        }

        private Quaternion ResolveRotation(FaceTrack face, Tracked3dFaceModelAlignmentMode effectiveMode)
        {
            if (effectiveMode == Tracked3dFaceModelAlignmentMode.CanonicalMatrix && face.HasFaceTransform)
            {
                var rotation = face.FaceTransform.rotation;
                if (invertFaceYaw)
                {
                    var euler = rotation.eulerAngles;
                    euler.y = -NormalizeAngle(euler.y);
                    rotation = Quaternion.Euler(euler);
                }

                return rotation;
            }

            var yaw = invertFaceYaw ? -face.FaceEulerDegrees.y : face.FaceEulerDegrees.y;
            return face.HasFaceTransform
                ? Quaternion.Euler(-face.FaceEulerDegrees.x, yaw, face.FaceEulerDegrees.z)
                : Quaternion.identity;
        }

        private static float ResolveYawDegrees(FaceTrack face)
        {
            return face != null ? NormalizeAngle(face.FaceEulerDegrees.y) : 0f;
        }

        private void ApplyModelLocalCalibration()
        {
            modelInstance.transform.localPosition = modelLocalPositionOffset;
            modelInstance.transform.localRotation = Quaternion.Euler(modelLocalEulerOffset);
            var mirrorX = mirrorModelX ? -1f : 1f;
            modelInstance.transform.localScale = new Vector3(
                NonZeroScale(modelLocalScaleOffset.x) * mirrorX,
                NonZeroScale(modelLocalScaleOffset.y),
                NonZeroScale(modelLocalScaleOffset.z));

            if (!loggedModelCalibration)
            {
                loggedModelCalibration = true;
                Debug.Log($"PhotoBooth AR face model calibration applied: localPosition={modelInstance.transform.localPosition}, localEuler={modelInstance.transform.localEulerAngles}, localScale={modelInstance.transform.localScale}");
            }
        }

        private void EnsureCameraAndTexture(int sourceWidth = 0, int sourceHeight = 0, bool matchTargetOverlayRect = false)
        {
            var resolvedSourceWidth = sourceWidth > 0 ? sourceWidth : (currentSourceWidth > 0 ? currentSourceWidth : renderTextureSize.x);
            var resolvedSourceHeight = sourceHeight > 0 ? sourceHeight : (currentSourceHeight > 0 ? currentSourceHeight : renderTextureSize.y);
            var width = Mathf.Max(256, resolvedSourceWidth);
            var height = Mathf.Max(256, resolvedSourceHeight);
            if (matchTargetOverlayRect && TryGetTargetOverlayRenderSize(out var overlaySize))
            {
                width = Mathf.Max(256, overlaySize.x);
                height = Mathf.Max(256, overlaySize.y);
            }

            EnsureCameraAndTexture(ArFaceProjectionGeometry.CreateStretched(resolvedSourceWidth, resolvedSourceHeight, width, height));
        }

        private void EnsureCameraAndTexture(ArFaceProjectionGeometry geometry)
        {
            EnsureCamera();
            if (!geometry.IsValid)
            {
                geometry = ArFaceProjectionGeometry.CreateStretched(renderTextureSize.x, renderTextureSize.y, renderTextureSize.x, renderTextureSize.y);
            }

            currentProjectionGeometry = geometry;
            currentSourceWidth = geometry.SourceWidth;
            currentSourceHeight = geometry.SourceHeight;
            currentRenderWidth = Mathf.Max(256, geometry.RenderWidth);
            currentRenderHeight = Mathf.Max(256, geometry.RenderHeight);

            if (renderTexture == null || renderTexture.width != currentRenderWidth || renderTexture.height != currentRenderHeight)
            {
                ReleaseRenderTexture();
                renderTexture = new RenderTexture(currentRenderWidth, currentRenderHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "ArFaceModelOverlayRT"
                };
            }

            overlayCamera.targetTexture = renderTexture;
            overlayCamera.orthographicSize = overlayOrthographicSize;

            if (targetOverlay != null && targetOverlay.texture != renderTexture)
            {
                targetOverlay.texture = renderTexture;
                targetOverlay.raycastTarget = false;
            }
        }

        private ArFaceProjectionGeometry CreateLivePreviewProjectionGeometry(int sourceWidth, int sourceHeight)
        {
            var resolvedSourceWidth = sourceWidth > 0 ? sourceWidth : (currentSourceWidth > 0 ? currentSourceWidth : renderTextureSize.x);
            var resolvedSourceHeight = sourceHeight > 0 ? sourceHeight : (currentSourceHeight > 0 ? currentSourceHeight : renderTextureSize.y);
            var renderWidth = Mathf.Max(256, resolvedSourceWidth);
            var renderHeight = Mathf.Max(256, resolvedSourceHeight);
            if (TryGetTargetOverlayRenderSize(out var overlaySize))
            {
                renderWidth = Mathf.Max(256, overlaySize.x);
                renderHeight = Mathf.Max(256, overlaySize.y);
            }

            return ArFaceProjectionGeometry.CreateStretched(resolvedSourceWidth, resolvedSourceHeight, renderWidth, renderHeight);
        }

        private bool TryGetTargetOverlayRenderSize(out Vector2Int size)
        {
            size = default;
            if (targetOverlay == null || targetOverlay.rectTransform == null)
            {
                return false;
            }

            var rect = targetOverlay.rectTransform.rect;
            var width = Mathf.RoundToInt(Mathf.Abs(rect.width));
            var height = Mathf.RoundToInt(Mathf.Abs(rect.height));
            if (width <= 0 || height <= 0)
            {
                return false;
            }

            size = new Vector2Int(width, height);
            return true;
        }

        private void EnsureCamera()
        {
            if (overlayCamera != null)
            {
                return;
            }

            var cameraHost = new GameObject("ArFaceModelOverlayCamera", typeof(Camera));
            cameraHost.transform.SetParent(transform, false);
            overlayCamera = cameraHost.GetComponent<Camera>();
            overlayCamera.clearFlags = CameraClearFlags.SolidColor;
            overlayCamera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            overlayCamera.cullingMask = 1 << arModelLayer;
            overlayCamera.orthographic = true;
            overlayCamera.orthographicSize = overlayOrthographicSize;
            overlayCamera.nearClipPlane = 0.01f;
            overlayCamera.farClipPlane = 50f;
            overlayCamera.transform.localPosition = new Vector3(0f, 0f, -10f);
            overlayCamera.transform.localRotation = Quaternion.identity;
            overlayCamera.depth = 50f;
            overlayCamera.allowHDR = false;
            overlayCamera.allowMSAA = false;
            overlayCamera.enabled = false;
        }

        private void ReleaseRenderTexture()
        {
            if (renderTexture == null)
            {
                return;
            }

            renderTexture.Release();
            DestroyUnityObject(renderTexture);
            renderTexture = null;
        }

        private static void DestroyUnityObject(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private void EnsureAnchors()
        {
            if (rigRoot == null)
            {
                var existingRoot = transform.Find("ArFaceModelRigRoot");
                rigRoot = existingRoot != null
                    ? existingRoot
                    : new GameObject("ArFaceModelRigRoot").transform;
                rigRoot.SetParent(transform, false);
                SetLayerRecursively(rigRoot.gameObject, arModelLayer);
            }

            EnsureAnchor("HeadCenter", rigRoot);
            EnsureAnchor("FaceMaskCenter", rigRoot);
            EnsureAnchor("Eyes", rigRoot);
            EnsureAnchor("LeftEye", rigRoot);
            EnsureAnchor("RightEye", rigRoot);
            EnsureAnchor("Nose", rigRoot);
            EnsureAnchor("Mouth", rigRoot);
            EnsureAnchor("Forehead", rigRoot);
            EnsureAnchor("LeftCheek", rigRoot);
            EnsureAnchor("RightCheek", rigRoot);
            EnsureAnchor("Chin", rigRoot);
        }

        private void EnsureAnchor(string anchorName, Transform parent)
        {
            if (anchors.ContainsKey(anchorName) && anchors[anchorName] != null)
            {
                return;
            }

            var existingAnchor = parent != null ? parent.Find(anchorName) : null;
            var anchor = existingAnchor != null
                ? existingAnchor
                : new GameObject(anchorName).transform;
            anchor.SetParent(parent, false);
            SetLayerRecursively(anchor.gameObject, arModelLayer);
            anchors[anchorName] = anchor;
        }

        private void EnsureDefaultModel()
        {
            if (modelInstance != null)
            {
                return;
            }

            var attachAnchor = GetAnchor(modelAttachAnchorName);
            modelInstance = FindExistingModelInstance(attachAnchor);
            if (modelInstance != null)
            {
                modelInstanceWasReused = true;
                modelInstance.name = GetModelInstanceName();
                SetLayerRecursively(modelInstance, arModelLayer);
                return;
            }

            var prefab = faceModelPrefab;
            var modelSource = prefab != null ? prefab.name : defaultModelResourcePath;
            if (prefab == null && loadDefaultModel && !string.IsNullOrWhiteSpace(defaultModelResourcePath))
            {
                prefab = Resources.Load<GameObject>(defaultModelResourcePath);
            }

            if (prefab == null)
            {
                Debug.LogWarning("PhotoBooth AR face model not assigned. Set BoothFrontendController.faceModelPrefab or keep the FBX under Resources/ARStickerBooth/FaceAssets.");
                return;
            }

            modelInstance = Instantiate(prefab, attachAnchor);
            modelInstanceWasReused = false;
            modelInstance.name = GetModelInstanceName();
            SetLayerRecursively(modelInstance, arModelLayer);
            if (!loggedModelLoaded)
            {
                loggedModelLoaded = true;
                var rendererCount = modelInstance.GetComponentsInChildren<Renderer>(true).Length;
                Debug.Log($"PhotoBooth AR face model loaded: {modelSource}, renderers={rendererCount}, layer={arModelLayer}");
            }
        }

        private GameObject FindExistingModelInstance(Transform attachAnchor)
        {
            if (attachAnchor == null)
            {
                return null;
            }

            var expectedName = GetModelInstanceName();
            var expected = attachAnchor.Find(expectedName);
            if (expected != null)
            {
                return expected.gameObject;
            }

            var legacy = attachAnchor.Find("FaceMaskModel_3d_face_quads_v310");
            if (legacy != null)
            {
                return legacy.gameObject;
            }

            var oppositeModeName = editorPreviewMode
                ? "FaceMaskModel_3d_face_quads_v310_RUNTIME"
                : "FaceMaskModel_3d_face_quads_v310_EDITOR_PREVIEW";
            var oppositeMode = attachAnchor.Find(oppositeModeName);
            return oppositeMode != null ? oppositeMode.gameObject : null;
        }

        private GameObject FindAnyModelInstanceInRig()
        {
            var transforms = GetComponentsInChildren<Transform>(true);
            foreach (var candidate in transforms)
            {
                if (candidate != null && candidate.name.StartsWith("FaceMaskModel_3d_face_quads_v310", StringComparison.Ordinal))
                {
                    return candidate.gameObject;
                }
            }

            return null;
        }

        private string GetModelInstanceName()
        {
            return editorPreviewMode
                ? "FaceMaskModel_3d_face_quads_v310_EDITOR_PREVIEW"
                : "FaceMaskModel_3d_face_quads_v310_RUNTIME";
        }

        private void ApplyFacePartModels(float eyeWorldDistance, bool enableModel)
        {
            facePartModels ??= Array.Empty<Tracked3dFacePartBinding>();
            for (var index = 0; index < facePartModels.Length; index++)
            {
                var binding = facePartModels[index];
                if (binding == null || !binding.visible)
                {
                    SetFacePartVisible(index, false);
                    continue;
                }

                var anchor = GetAnchor(ResolveFacePartAnchorName(binding.anchor));
                var root = EnsureFacePartRoot(index, binding, anchor);
                var instance = EnsureFacePartInstance(index, binding, root);
                if (instance == null)
                {
                    SetFacePartVisible(index, false);
                    continue;
                }

                root.SetParent(anchor, false);
                root.localPosition = Vector3.zero;
                root.localRotation = Quaternion.identity;
                root.localScale = Vector3.one * Mathf.Max(0.0001f, eyeWorldDistance * Mathf.Max(0.0001f, binding.scaleMultiplier));

                if (!binding.preservePrefabLocalTransform)
                {
                    ApplyFacePartLocalCalibration(instance.transform, binding);
                }

                root.gameObject.SetActive(enableModel);
            }

            foreach (var key in new List<int>(facePartRoots.Keys))
            {
                if (key >= facePartModels.Length)
                {
                    SetFacePartVisible(key, false);
                }
            }
        }

        private bool HasFacePartModels()
        {
            if (facePartModels == null)
            {
                return false;
            }

            foreach (var binding in facePartModels)
            {
                if (binding != null && binding.visible && (binding.prefab != null || !string.IsNullOrWhiteSpace(binding.resourcePath)))
                {
                    return true;
                }
            }

            return false;
        }

        private Transform EnsureFacePartRoot(int index, Tracked3dFacePartBinding binding, Transform anchor)
        {
            if (facePartRoots.TryGetValue(index, out var root) && root != null)
            {
                return root;
            }

            var rootName = $"FacePart_{index:00}_{ResolveFacePartLabel(binding)}";
            var rootHost = new GameObject(rootName);
            root = rootHost.transform;
            root.SetParent(anchor, false);
            SetLayerRecursively(root.gameObject, arModelLayer);
            facePartRoots[index] = root;
            return root;
        }

        private GameObject EnsureFacePartInstance(int index, Tracked3dFacePartBinding binding, Transform root)
        {
            if (binding == null || root == null)
            {
                return null;
            }

            var prefab = ResolveFacePartPrefab(binding);
            if (prefab == null)
            {
                return null;
            }

            var instanceName = $"FacePartModel_{prefab.name}";
            if (facePartInstances.TryGetValue(index, out var existing) && existing != null)
            {
                if (existing.name == instanceName)
                {
                    return existing;
                }

                DestroyUnityObject(existing);
            }

            var instance = Instantiate(prefab, root, false);
            instance.name = instanceName;
            SetLayerRecursively(instance, arModelLayer);
            facePartInstances[index] = instance;
            return instance;
        }

        private static GameObject ResolveFacePartPrefab(Tracked3dFacePartBinding binding)
        {
            if (binding.prefab != null)
            {
                return binding.prefab;
            }

            return string.IsNullOrWhiteSpace(binding.resourcePath)
                ? null
                : Resources.Load<GameObject>(binding.resourcePath);
        }

        private static void ApplyFacePartLocalCalibration(Transform target, Tracked3dFacePartBinding binding)
        {
            target.localPosition = binding.localPositionOffset;
            target.localRotation = Quaternion.Euler(binding.localEulerOffset);
            target.localScale = new Vector3(
                NonZeroScale(binding.localScale.x) * (binding.mirrorX ? -1f : 1f),
                NonZeroScale(binding.localScale.y),
                NonZeroScale(binding.localScale.z));
        }

        private void HideFacePartModels()
        {
            foreach (var root in facePartRoots.Values)
            {
                if (root != null)
                {
                    root.gameObject.SetActive(false);
                }
            }
        }

        private void SetFacePartVisible(int index, bool visible)
        {
            if (facePartRoots.TryGetValue(index, out var root) && root != null)
            {
                root.gameObject.SetActive(visible);
            }
        }

        private void CaptureModelBoundsDebugInfo()
        {
            lastHasModelBounds = false;
            lastModelBounds = default;
            if (rigRoot == null)
            {
                return;
            }

            var renderers = rigRoot.GetComponentsInChildren<Renderer>(false);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled)
                {
                    continue;
                }

                if (!lastHasModelBounds)
                {
                    lastModelBounds = renderer.bounds;
                    lastHasModelBounds = true;
                    continue;
                }

                lastModelBounds.Encapsulate(renderer.bounds);
            }
        }

        public static string ResolveFacePartAnchorName(Tracked3dFacePartAnchor anchor)
        {
            return anchor switch
            {
                Tracked3dFacePartAnchor.FaceMaskCenter => "FaceMaskCenter",
                Tracked3dFacePartAnchor.HeadCenter => "HeadCenter",
                Tracked3dFacePartAnchor.Eyes => "Eyes",
                Tracked3dFacePartAnchor.LeftEye => "LeftEye",
                Tracked3dFacePartAnchor.RightEye => "RightEye",
                Tracked3dFacePartAnchor.Nose => "Nose",
                Tracked3dFacePartAnchor.Mouth => "Mouth",
                Tracked3dFacePartAnchor.Forehead => "Forehead",
                Tracked3dFacePartAnchor.Chin => "Chin",
                Tracked3dFacePartAnchor.LeftCheek => "LeftCheek",
                Tracked3dFacePartAnchor.RightCheek => "RightCheek",
                _ => "FaceMaskCenter"
            };
        }

        private static string ResolveFacePartLabel(Tracked3dFacePartBinding binding)
        {
            return string.IsNullOrWhiteSpace(binding?.name) ? binding?.anchor.ToString() ?? "Part" : binding.name;
        }

        private void SetAnchor(string anchorName, Vector2 normalizedPoint, float normalizedDepth, Quaternion rotation, ArFaceProjectionGeometry geometry)
        {
            var anchor = anchors[anchorName];
            anchor.position = ViewportToWorld(normalizedPoint, normalizedDepth, geometry);
            anchor.rotation = rotation;
        }

        private Vector3 ViewportToWorld(Vector2 normalizedPoint, float normalizedDepth, ArFaceProjectionGeometry geometry)
        {
            return geometry.ProjectToWorld(
                normalizedPoint,
                normalizedDepth,
                overlayOrthographicSize,
                faceDepth,
                faceDepthScale);
        }

        private static Vector2 PointOrFallback(Vector2[] landmarks, int index, Vector2 fallback)
        {
            return TryGetPoint(landmarks, index, out var point) ? point : fallback;
        }

        private static Vector2 AverageOrFallback(Vector2[] landmarks, Vector2 fallback, params int[] indices)
        {
            var sum = Vector2.zero;
            var count = 0;
            foreach (var index in indices)
            {
                if (!TryGetPoint(landmarks, index, out var point))
                {
                    continue;
                }

                sum += point;
                count++;
            }

            return count > 0 ? sum / count : fallback;
        }

        private static bool TryGetPoint(Vector2[] landmarks, int index, out Vector2 point)
        {
            if (landmarks != null && index >= 0 && index < landmarks.Length)
            {
                point = landmarks[index];
                return true;
            }

            point = default;
            return false;
        }

        private static float ResolveDepth(Vector3[] landmarks3D, int index)
        {
            return landmarks3D != null && index >= 0 && index < landmarks3D.Length ? landmarks3D[index].z : 0f;
        }

        private static float ResolveUniformScale(Matrix4x4 matrix)
        {
            var x = new Vector3(matrix.m00, matrix.m10, matrix.m20).magnitude;
            var y = new Vector3(matrix.m01, matrix.m11, matrix.m21).magnitude;
            var z = new Vector3(matrix.m02, matrix.m12, matrix.m22).magnitude;
            var scale = (x + y + z) / 3f;
            return !float.IsNaN(scale) && !float.IsInfinity(scale) ? Mathf.Clamp(scale, 0.35f, 3f) : 1f;
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

        private static void SetLandmark(Vector2[] landmarks, Vector3[] landmarks3D, int index, float x, float y)
        {
            if (landmarks == null || landmarks3D == null || index < 0 || index >= landmarks.Length || index >= landmarks3D.Length)
            {
                return;
            }

            var point = new Vector2(x, y);
            landmarks[index] = point;
            landmarks3D[index] = new Vector3(point.x, point.y, 0f);
        }

        private static float NonZeroScale(float value)
        {
            return Mathf.Abs(value) < 0.0001f ? 0.0001f : value;
        }

        private static void SetLayerRecursively(GameObject target, int layer)
        {
            target.layer = layer;
            foreach (Transform child in target.transform)
            {
                SetLayerRecursively(child.gameObject, layer);
            }
        }

        private void OnGUI()
        {
            if (!showRuntimeDebugInfo || !Application.isPlaying)
            {
                return;
            }

            const int boxWidth = 320;
            const int boxHeight = 290;
            const int padding = 10;

            var skin = GUI.skin;
            var originalFontSize = skin.box.fontSize;
            var originalAlignment = skin.box.alignment;
            var originalFont = skin.box.font;
            skin.box.fontSize = 11;
            skin.box.alignment = TextAnchor.UpperLeft;

            GUILayout.BeginArea(new Rect(padding, padding, boxWidth, boxHeight));
            GUILayout.BeginVertical();
            {
                GUI.Box(new Rect(0, 0, boxWidth, boxHeight), "");
                GUILayout.Space(5);

                GUI.skin.label.fontStyle = FontStyle.Bold;
                GUILayout.Label("AR Face Model Debug Info");
                GUI.skin.label.fontStyle = FontStyle.Normal;
                GUILayout.Space(5);

                GUILayout.Label($"Eye Distance (World): {lastEyeWorldDistance:F4}");
                GUILayout.Label($"Calculated Scale: {lastCalculatedScale:F4}");
                GUILayout.Label($"Face Mask Center: ({lastFaceMaskCenter.x:F2}, {lastFaceMaskCenter.y:F2}, {lastFaceMaskCenter.z:F2})");
                GUILayout.Label($"Head Rotation: Y={lastHeadEulerDegrees.y:F1}°, P={lastHeadEulerDegrees.x:F1}°, R={lastHeadEulerDegrees.z:F1}°");
                GUILayout.Label($"Alignment Mode: {lastAlignmentMode}");
                GUILayout.Label($"Source: {lastSourceSize.x}x{lastSourceSize.y}");
                GUILayout.Label($"Render: {lastRenderSize.x}x{lastRenderSize.y}");
                GUILayout.Label($"Viewport: x={lastViewportRect.x:F0}, y={lastViewportRect.y:F0}, w={lastViewportRect.width:F0}, h={lastViewportRect.height:F0}");
                GUILayout.Label(lastHasModelBounds
                    ? $"Model Bounds: c=({lastModelBounds.center.x:F2},{lastModelBounds.center.y:F2},{lastModelBounds.center.z:F2}) s=({lastModelBounds.size.x:F2},{lastModelBounds.size.y:F2},{lastModelBounds.size.z:F2})"
                    : "Model Bounds: none");
                GUILayout.Label($"Camera: ortho={overlayOrthographicSize:F2}, aspect={(lastRenderSize.y > 0 ? lastRenderSize.x / (float)lastRenderSize.y : 0f):F3}");
                GUILayout.Label($"Model Scale Multiplier: {modelScaleMultiplier:F4}");
                GUILayout.Label($"Screen Offset: ({faceMaskScreenOffsetByEyeDistance.x:F3}, {faceMaskScreenOffsetByEyeDistance.y:F3})");
                GUILayout.Label($"Nose Blend (Neutral): {neutralFaceMaskNoseBlend:F3}");
            }
            GUILayout.EndVertical();
            GUILayout.EndArea();

            skin.box.fontSize = originalFontSize;
            skin.box.alignment = originalAlignment;
            skin.box.font = originalFont;
        }

        /// <summary>
        /// Logs a calibration session in CSV format for spreadsheet recording.
        /// </summary>
        /// <param name="personId">Identifier for the person (e.g., "Person1", "TestSubject_A")</param>
        /// <param name="notes">Additional notes about this calibration session</param>
        public void LogCalibrationSession(string personId, string notes)
        {
            var csv = $"[CALIBRATION] PersonID={personId}," +
                      $"Scale={modelScaleMultiplier:F4}," +
                      $"OffsetX={modelLocalPositionOffset.x:F3}," +
                      $"OffsetY={modelLocalPositionOffset.y:F3}," +
                      $"OffsetZ={modelLocalPositionOffset.z:F3}," +
                      $"ScreenOffsetX={faceMaskScreenOffsetByEyeDistance.x:F3}," +
                      $"ScreenOffsetY={faceMaskScreenOffsetByEyeDistance.y:F3}," +
                      $"NoseBlendNeutral={neutralFaceMaskNoseBlend:F3}," +
                      $"NoseBlendSide={sideFaceMaskNoseBlend:F3}," +
                      $"LocalScaleX={modelLocalScaleOffset.x:F2}," +
                      $"LocalScaleY={modelLocalScaleOffset.y:F2}," +
                      $"LocalScaleZ={modelLocalScaleOffset.z:F2}," +
                      $"AlignmentMode={alignmentMode}," +
                      $"Notes={notes}";

            Debug.Log(csv);
        }

        /// <summary>
        /// Applies a calibration preset from diagnosis testing. Use this to quickly test different calibration values.
        /// Call this method during runtime or from editor scripts to test different offset configurations.
        /// </summary>
        /// <param name="offsetX">X offset: negative = left, positive = right</param>
        /// <param name="offsetY">Y offset: positive = up, negative = down</param>
        /// <param name="offsetZ">Z offset: depth (usually keep at 0 unless model clips)</param>
        /// <param name="logSession">Whether to log the calibration session</param>
        public void ApplyCalibrationOffset(float offsetX, float offsetY, float offsetZ = 0f, bool logSession = true)
        {
            modelLocalPositionOffset = new Vector3(offsetX, offsetY, offsetZ);
            preserveExistingModelLocalTransform = false;

            if (modelInstance != null)
            {
                modelInstanceWasReused = false;
            }

            if (logSession)
            {
                Debug.Log($"[CALIBRATION] Applied offset: X={offsetX:F3}, Y={offsetY:F3}, Z={offsetZ:F3}. Test: face front, turn left, turn right, tilt up/down.");
            }
        }
    }
}
