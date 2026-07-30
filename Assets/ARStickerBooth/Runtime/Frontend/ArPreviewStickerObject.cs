using PhotoBooth.Booth.AR;
using UnityEngine;
using UnityEngine.UI;

namespace PhotoBooth.Booth.Frontend
{
    [RequireComponent(typeof(RectTransform))]
    [RequireComponent(typeof(RawImage))]
    public sealed class ArPreviewStickerObject : MonoBehaviour
    {
        [SerializeField] private Vector2 manualAnchoredOffset;
        [SerializeField] private Vector2 manualSizeScale = Vector2.one;
        [SerializeField] private Vector3 manualEulerOffset;
        // 2D UI stickers must not rotate around their Y axis with the face pose:
        // that produces a mirrored/"flipped" nose as the person turns.
        [SerializeField] private bool useFacePose;
        [SerializeField] private bool useYawForeshortening;

        private RectTransform rectTransform;
        private RawImage rawImage;

        public RawImage RawImage => rawImage != null ? rawImage : rawImage = GetComponent<RawImage>();
        public RectTransform RectTransform => rectTransform != null ? rectTransform : rectTransform = GetComponent<RectTransform>();

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
            rawImage = GetComponent<RawImage>();
        }

        public void ApplyTrackedItem(ArStickerRenderItem item, Rect overlayRect, int sourceWidth, int sourceHeight)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
            {
                Hide();
                return;
            }

            var centerX = item.PixelRect.x + (item.PixelRect.width * 0.5f);
            var centerY = item.PixelRect.y + (item.PixelRect.height * 0.5f);
            RectTransform.anchoredPosition = new Vector2(
                ((centerX / sourceWidth) - 0.5f) * overlayRect.width,
                (0.5f - (centerY / sourceHeight)) * overlayRect.height) + manualAnchoredOffset;

            RectTransform.sizeDelta = new Vector2(
                (item.PixelRect.width / sourceWidth) * overlayRect.width * Mathf.Max(0.01f, manualSizeScale.x),
                (item.PixelRect.height / sourceHeight) * overlayRect.height * Mathf.Max(0.01f, manualSizeScale.y));

            var faceEuler = item.FaceEulerDegrees;
            var pitch = useFacePose && item.HasFacePose ? Mathf.Clamp(faceEuler.x, -24f, 24f) : 0f;
            var yaw = useFacePose && item.HasFacePose ? Mathf.Clamp(faceEuler.y, -34f, 34f) : 0f;
            RectTransform.localRotation = Quaternion.Euler(
                -pitch + manualEulerOffset.x,
                yaw + manualEulerOffset.y,
                -item.RotationDegrees + manualEulerOffset.z);

            var yawScale = useFacePose && useYawForeshortening && item.HasFacePose
                ? Mathf.Lerp(1f, 0.68f, Mathf.Abs(yaw) / 34f)
                : 1f;
            RectTransform.localScale = new Vector3(yawScale, 1f, 1f);

            RawImage.texture = item.Texture;
            RawImage.color = item.Tint;
            RawImage.enabled = true;
            gameObject.SetActive(true);
        }

        public void Hide()
        {
            RawImage.texture = null;
            RawImage.enabled = false;
            RectTransform.localScale = Vector3.one;
            RectTransform.localRotation = Quaternion.identity;
            gameObject.SetActive(false);
        }
    }
}
