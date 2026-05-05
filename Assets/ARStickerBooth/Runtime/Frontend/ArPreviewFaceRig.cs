using System.Collections.Generic;
using PhotoBooth.Booth.AR;
using UnityEngine;
using UnityEngine.UI;

namespace PhotoBooth.Booth.Frontend
{
    [RequireComponent(typeof(RectTransform))]
    public sealed class ArPreviewFaceRig : MonoBehaviour
    {
        private static readonly int[] FaceOval = { 10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288, 397, 365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136, 172, 58, 132, 93, 234, 127, 162, 21, 54, 103, 67, 109, 10 };
        private static readonly int[] RightEye = { 33, 160, 158, 133, 153, 144, 33 };
        private static readonly int[] LeftEye = { 362, 385, 387, 263, 373, 380, 362 };
        private static readonly int[] RightBrow = { 46, 53, 52, 65, 55, 70, 63, 105, 66, 107 };
        private static readonly int[] LeftBrow = { 276, 283, 282, 295, 285, 300, 293, 334, 296, 336 };
        private static readonly int[] NoseBridge = { 168, 6, 197, 195, 5, 4 };
        private static readonly int[] NoseBase = { 48, 115, 220, 45, 4, 275, 440, 344, 278 };
        private static readonly int[] OuterMouth = { 61, 39, 37, 0, 267, 269, 291, 405, 314, 17, 84, 181, 61 };
        private static readonly int[] InnerMouth = { 78, 82, 13, 312, 308, 317, 14, 87, 78 };
        private static readonly Vector2Int[] SkinCrossLines =
        {
            new(10, 152), new(234, 454), new(127, 356), new(93, 323), new(33, 263),
            new(168, 4), new(4, 13), new(61, 291), new(58, 288), new(172, 397)
        };

        [SerializeField] private bool showWireframe = true;
        [SerializeField] private Color wireframeColor = new(0.1f, 1f, 0.55f, 0.55f);
        [SerializeField] private float wireframeThickness = 1.25f;
        [SerializeField] private float depthScale = 220f;
        [SerializeField] private Vector3 modelerEulerOffset;

        private readonly Dictionary<string, RectTransform> anchors = new();
        private readonly List<Image> wireLines = new();
        private RectTransform rectTransform;

        public RectTransform RectTransform => rectTransform != null ? rectTransform : rectTransform = GetComponent<RectTransform>();
        public Transform HeadCenter => GetAnchor("HeadCenter");
        public Transform LeftEyeAnchor => GetAnchor("LeftEye");
        public Transform RightEyeAnchor => GetAnchor("RightEye");
        public Transform Nose => GetAnchor("Nose");
        public Transform Mouth => GetAnchor("Mouth");
        public Transform Forehead => GetAnchor("Forehead");
        public Transform LeftCheek => GetAnchor("LeftCheek");
        public Transform RightCheek => GetAnchor("RightCheek");
        public Transform Chin => GetAnchor("Chin");
        public bool ShowWireframe
        {
            get => showWireframe;
            set => showWireframe = value;
        }

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            EnsureDefaultAnchors();
        }

        public Transform GetAnchor(string anchorName)
        {
            return EnsureAnchor(anchorName);
        }

        public void ApplyFace(FaceTrack face, Rect overlayRect)
        {
            if (face?.NormalizedLandmarks == null || face.NormalizedLandmarks.Length < 468)
            {
                Hide();
                return;
            }

            EnsureDefaultAnchors();
            var landmarks = face.NormalizedLandmarks;
            var landmarks3D = face.NormalizedLandmarks3D;
            var rotation = face.HasFaceTransform
                ? Quaternion.Euler(-face.FaceEulerDegrees.x, face.FaceEulerDegrees.y, face.FaceEulerDegrees.z) * Quaternion.Euler(modelerEulerOffset)
                : Quaternion.Euler(modelerEulerOffset);

            SetAnchor("HeadCenter", ResolveHeadCenter(face), 0f, overlayRect, rotation);
            SetAnchor("LeftEye", Average(landmarks, 362, 263, 386), ResolveDepth(landmarks3D, 263), overlayRect, rotation);
            SetAnchor("RightEye", Average(landmarks, 33, 133, 159), ResolveDepth(landmarks3D, 33), overlayRect, rotation);
            SetAnchor("Nose", PointOrBounds(face, 1), ResolveDepth(landmarks3D, 1), overlayRect, rotation);
            SetAnchor("Mouth", Average(landmarks, 13, 14, 61, 291), ResolveDepth(landmarks3D, 13), overlayRect, rotation);
            SetAnchor("Forehead", PointOrBounds(face, 10), ResolveDepth(landmarks3D, 10), overlayRect, rotation);
            SetAnchor("LeftCheek", PointOrBounds(face, 454), ResolveDepth(landmarks3D, 454), overlayRect, rotation);
            SetAnchor("RightCheek", PointOrBounds(face, 234), ResolveDepth(landmarks3D, 234), overlayRect, rotation);
            SetAnchor("Chin", PointOrBounds(face, 152), ResolveDepth(landmarks3D, 152), overlayRect, rotation);

            if (showWireframe)
            {
                DrawWireframe(landmarks, overlayRect);
            }
            else
            {
                HideWireLines(0);
            }

            gameObject.SetActive(true);
        }

        public void Hide()
        {
            foreach (var anchor in anchors.Values)
            {
                if (anchor != null)
                {
                    anchor.gameObject.SetActive(false);
                }
            }

            HideWireLines(0);
            gameObject.SetActive(false);
        }

        private void EnsureDefaultAnchors()
        {
            EnsureAnchor("HeadCenter");
            EnsureAnchor("LeftEye");
            EnsureAnchor("RightEye");
            EnsureAnchor("Nose");
            EnsureAnchor("Mouth");
            EnsureAnchor("Forehead");
            EnsureAnchor("LeftCheek");
            EnsureAnchor("RightCheek");
            EnsureAnchor("Chin");
        }

        private RectTransform EnsureAnchor(string anchorName)
        {
            if (anchors.TryGetValue(anchorName, out var anchor) && anchor != null)
            {
                return anchor;
            }

            var host = new GameObject(anchorName, typeof(RectTransform));
            host.transform.SetParent(transform, false);
            anchor = host.GetComponent<RectTransform>();
            anchor.anchorMin = Vector2.one * 0.5f;
            anchor.anchorMax = Vector2.one * 0.5f;
            anchor.pivot = Vector2.one * 0.5f;
            anchor.sizeDelta = Vector2.zero;
            anchors[anchorName] = anchor;
            return anchor;
        }

        private void SetAnchor(string anchorName, Vector2 normalizedPoint, float normalizedDepth, Rect overlayRect, Quaternion rotation)
        {
            var anchor = EnsureAnchor(anchorName);
            var anchored = NormalizedToOverlay(normalizedPoint, overlayRect);
            anchor.localPosition = new Vector3(anchored.x, anchored.y, normalizedDepth * depthScale);
            anchor.localRotation = rotation;
            anchor.gameObject.SetActive(true);
        }

        private void DrawWireframe(Vector2[] landmarks, Rect overlayRect)
        {
            var lineIndex = 0;
            lineIndex = AddPolyline(landmarks, FaceOval, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, RightEye, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, LeftEye, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, RightBrow, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, LeftBrow, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, NoseBridge, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, NoseBase, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, OuterMouth, overlayRect, lineIndex);
            lineIndex = AddPolyline(landmarks, InnerMouth, overlayRect, lineIndex);

            foreach (var line in SkinCrossLines)
            {
                if (!TryGetPoint(landmarks, line.x, out var from) || !TryGetPoint(landmarks, line.y, out var to))
                {
                    continue;
                }

                ApplyWireLine(lineIndex++, NormalizedToOverlay(from, overlayRect), NormalizedToOverlay(to, overlayRect));
            }

            HideWireLines(lineIndex);
        }

        private int AddPolyline(Vector2[] landmarks, int[] indices, Rect overlayRect, int lineIndex)
        {
            for (var i = 1; i < indices.Length; i++)
            {
                if (!TryGetPoint(landmarks, indices[i - 1], out var from) || !TryGetPoint(landmarks, indices[i], out var to))
                {
                    continue;
                }

                ApplyWireLine(lineIndex++, NormalizedToOverlay(from, overlayRect), NormalizedToOverlay(to, overlayRect));
            }

            return lineIndex;
        }

        private void ApplyWireLine(int index, Vector2 from, Vector2 to)
        {
            var line = EnsureWireLine(index);
            var rect = line.rectTransform;
            var delta = to - from;
            rect.anchoredPosition = (from + to) * 0.5f;
            rect.sizeDelta = new Vector2(Mathf.Max(1f, delta.magnitude), Mathf.Max(1f, wireframeThickness));
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            line.color = wireframeColor;
            line.enabled = true;
            line.gameObject.SetActive(true);
        }

        private Image EnsureWireLine(int index)
        {
            while (wireLines.Count <= index)
            {
                var host = new GameObject($"FaceRigWire_{wireLines.Count:00}", typeof(RectTransform), typeof(Image));
                host.transform.SetParent(transform, false);
                var rect = host.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.one * 0.5f;
                rect.anchorMax = Vector2.one * 0.5f;
                rect.pivot = Vector2.one * 0.5f;
                var image = host.GetComponent<Image>();
                image.raycastTarget = false;
                wireLines.Add(image);
            }

            return wireLines[index];
        }

        private void HideWireLines(int startIndex)
        {
            for (var i = Mathf.Max(0, startIndex); i < wireLines.Count; i++)
            {
                if (wireLines[i] == null)
                {
                    continue;
                }

                wireLines[i].enabled = false;
                wireLines[i].gameObject.SetActive(false);
            }
        }

        private static Vector2 ResolveHeadCenter(FaceTrack face)
        {
            var landmarks = face.NormalizedLandmarks;
            return landmarks != null && landmarks.Length > 454
                ? Average(landmarks, 10, 152, 234, 454, 1)
                : face.NormalizedBounds.center;
        }

        private static Vector2 PointOrBounds(FaceTrack face, int index)
        {
            return TryGetPoint(face.NormalizedLandmarks, index, out var point) ? point : face.NormalizedBounds.center;
        }

        private static Vector2 Average(Vector2[] landmarks, params int[] indices)
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

            return count > 0 ? sum / count : Vector2.one * 0.5f;
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

        private static Vector2 NormalizedToOverlay(Vector2 point, Rect overlayRect)
        {
            return new Vector2(
                (point.x - 0.5f) * overlayRect.width,
                (point.y - 0.5f) * overlayRect.height);
        }
    }
}
