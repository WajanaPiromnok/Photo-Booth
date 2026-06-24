using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(RectTransform))]
    public sealed class CuteUiWiggle : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float pauseDuration = 0.75f;
        [SerializeField, Min(0f)] private float liftDistance = 8f;
        [SerializeField, Min(0.01f)] private float liftDuration = 0.24f;
        [SerializeField, Range(0f, 10f)] private float rotationAngle = 2.8f;
        [SerializeField, Min(0.01f)] private float wiggleDuration = 0.14f;
        [SerializeField, Min(0.01f)] private float settleDuration = 0.22f;

        private RectTransform rectTransform;
        private Vector2 restingPosition;
        private Quaternion restingRotation;

        private void Awake()
        {
            rectTransform = GetComponent<RectTransform>();
        }

        private void OnEnable()
        {
            if (rectTransform == null)
            {
                rectTransform = GetComponent<RectTransform>();
            }

            restingPosition = rectTransform.anchoredPosition;
            restingRotation = rectTransform.localRotation;
            PlaySequence();
        }

        private void PlaySequence()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }

            rectTransform.anchoredPosition = restingPosition;
            rectTransform.localRotation = restingRotation;

            var sequence = LeanTween.sequence();
            sequence.append(pauseDuration);
            sequence.append(
                LeanTween.moveY(rectTransform, restingPosition.y + liftDistance, liftDuration)
                    .setEase(LeanTweenType.easeOutBack)
                    .setIgnoreTimeScale(true));
            sequence.append(CreateRotationTween(0f, -rotationAngle, wiggleDuration));
            sequence.append(CreateRotationTween(-rotationAngle, rotationAngle, wiggleDuration * 1.35f));
            sequence.append(CreateRotationTween(rotationAngle, -rotationAngle, wiggleDuration * 1.35f));
            sequence.append(CreateRotationTween(-rotationAngle, rotationAngle * 0.45f, wiggleDuration));
            sequence.append(CreateRotationTween(rotationAngle * 0.45f, 0f, wiggleDuration * 0.8f));
            sequence.append(
                LeanTween.moveY(rectTransform, restingPosition.y, settleDuration)
                    .setEase(LeanTweenType.easeInOutQuad)
                    .setIgnoreTimeScale(true));
            sequence.append(gameObject, PlaySequence);
        }

        private LTDescr CreateRotationTween(float fromAngle, float toAngle, float duration)
        {
            return LeanTween.value(gameObject, fromAngle, toAngle, duration)
                .setEase(LeanTweenType.easeInOutSine)
                .setIgnoreTimeScale(true)
                .setOnUpdate(angle =>
                    rectTransform.localRotation = restingRotation * Quaternion.Euler(0f, 0f, angle));
        }

        private void OnDisable()
        {
            if (rectTransform == null)
            {
                return;
            }

            LeanTween.cancel(gameObject);
            rectTransform.anchoredPosition = restingPosition;
            rectTransform.localRotation = restingRotation;
        }
    }
}
