using PhotoBooth.Booth.AR;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    [RequireComponent(typeof(RectTransform))]
    public sealed class ArPreviewFaceAnchor : MonoBehaviour
    {
        [SerializeField] private Vector2 manualAnchoredOffset;
        [SerializeField] private Vector2 manualSizeScale = Vector2.one;
        [SerializeField] private Vector3 manualEulerOffset;
        [SerializeField] private bool useFacePose = true;

        private RectTransform rectTransform;

        public RectTransform RectTransform => rectTransform != null ? rectTransform : rectTransform = GetComponent<RectTransform>();

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
        }

        public void ApplyFace(FaceTrack face, Rect overlayRect, int sourceWidth, int sourceHeight)
        {
            if (face == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                Hide();
                return;
            }

            var center = ResolveAnchorCenter(face);
            RectTransform.anchoredPosition = NormalizedToOverlay(center, overlayRect) + manualAnchoredOffset;

            var eyeDistance = ResolveEyeDistance(face);
            var anchorSize = new Vector2(
                Mathf.Max(0.001f, eyeDistance * sourceWidth) / sourceWidth * overlayRect.width * Mathf.Max(0.01f, manualSizeScale.x),
                Mathf.Max(0.001f, face.NormalizedBounds.height) * overlayRect.height * Mathf.Max(0.01f, manualSizeScale.y));
            RectTransform.sizeDelta = anchorSize;

            var euler = face.FaceEulerDegrees;
            RectTransform.localRotation = useFacePose && face.HasFaceTransform
                ? Quaternion.Euler(-euler.x + manualEulerOffset.x, euler.y + manualEulerOffset.y, euler.z + manualEulerOffset.z)
                : Quaternion.Euler(manualEulerOffset);
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            RectTransform.localRotation = Quaternion.identity;
            RectTransform.localScale = Vector3.one;
            gameObject.SetActive(false);
        }

        private static Vector2 ResolveAnchorCenter(FaceTrack face)
        {
            if (face.NormalizedLandmarks != null && face.NormalizedLandmarks.Length > 263)
            {
                var leftEye = face.NormalizedLandmarks[33];
                var rightEye = face.NormalizedLandmarks[263];
                return (leftEye + rightEye) * 0.5f;
            }

            return new Vector2(face.NormalizedBounds.center.x, face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f));
        }

        private static float ResolveEyeDistance(FaceTrack face)
        {
            if (face.NormalizedLandmarks != null && face.NormalizedLandmarks.Length > 263)
            {
                return Vector2.Distance(face.NormalizedLandmarks[33], face.NormalizedLandmarks[263]);
            }

            return face.NormalizedBounds.width * 0.32f;
        }

        private static Vector2 NormalizedToOverlay(Vector2 point, Rect overlayRect)
        {
            return new Vector2(
                (point.x - 0.5f) * overlayRect.width,
                (point.y - 0.5f) * overlayRect.height);
        }
    }
}
