using System;
using System.Collections.Generic;
using UnityEngine;

namespace PhotoBooth.Booth.AR
{
    [Serializable]
    public sealed class ArStickerDefinition
    {
        public string stickerId = "default_sunglasses";
        public bool enabled = true;
        public Texture2D texture;
        public ArStickerBuiltinShape builtinShape = ArStickerBuiltinShape.Sunglasses;
        public ArStickerAnchor anchor = ArStickerAnchor.Eyes;
        public Vector2 normalizedOffset;
        public Vector2 sizeScale = Vector2.one;
        public Color tint = Color.white;
    }

    public readonly struct ArStickerRenderItem
    {
        public ArStickerRenderItem(Texture2D texture, Rect pixelRect, float rotationDegrees, Color tint)
            : this(texture, pixelRect, rotationDegrees, Vector3.zero, false, tint)
        {
        }

        public ArStickerRenderItem(Texture2D texture, Rect pixelRect, float rotationDegrees, Vector3 faceEulerDegrees, bool hasFacePose, Color tint)
        {
            Texture = texture;
            PixelRect = pixelRect;
            RotationDegrees = rotationDegrees;
            FaceEulerDegrees = faceEulerDegrees;
            HasFacePose = hasFacePose;
            Tint = tint;
        }

        public Texture2D Texture { get; }
        public Rect PixelRect { get; }
        public float RotationDegrees { get; }
        public Vector3 FaceEulerDegrees { get; }
        public bool HasFacePose { get; }
        public Color Tint { get; }
    }

    public static class ArStickerTransformResolver
    {
        private const int LeftEyeIndex = 33;
        private const int RightEyeIndex = 263;
        private const int NoseIndex = 1;
        private const int ForeheadIndex = 10;
        private const int MouthLeftIndex = 61;
        private const int MouthRightIndex = 291;

        public static bool TryResolve(
            FaceTrack face,
            ArStickerDefinition sticker,
            Vector2Int pixelSize,
            bool mirrorHorizontally,
            out Rect pixelRect,
            out float rotationDegrees)
        {
            pixelRect = default;
            rotationDegrees = 0f;
            if (face == null || sticker == null || pixelSize.x <= 0 || pixelSize.y <= 0)
            {
                return false;
            }

            var leftEyeFallback = new Vector2(face.NormalizedBounds.xMin + (face.NormalizedBounds.width * 0.34f), face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f));
            var rightEyeFallback = new Vector2(face.NormalizedBounds.xMin + (face.NormalizedBounds.width * 0.66f), face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f));
            var useFaceBoundsEyeGuide = sticker.builtinShape == ArStickerBuiltinShape.Sunglasses && sticker.anchor == ArStickerAnchor.Eyes;
            var detectorLeftEye = GetPoint(face, LeftEyeIndex, leftEyeFallback);
            var detectorRightEye = GetPoint(face, RightEyeIndex, rightEyeFallback);
            var useDetectorEyes = (!useFaceBoundsEyeGuide || face.HasReliableEyeLandmarks)
                && IsUsableEyePair(face, detectorLeftEye, detectorRightEye);
            var leftEye = useDetectorEyes ? detectorLeftEye : leftEyeFallback;
            var rightEye = useDetectorEyes ? detectorRightEye : rightEyeFallback;
            SortByScreenX(ref leftEye, ref rightEye);

            var center = ResolveAnchorCenter(face, sticker.anchor, leftEye, rightEye);
            var eyeDistance = Mathf.Max(0.001f, Vector2.Distance(leftEye, rightEye));
            var width = ResolveBaseWidth(face, sticker.anchor, eyeDistance) * Mathf.Max(0.05f, sticker.sizeScale.x);
            var height = ResolveBaseHeight(face, sticker.anchor, width) * Mathf.Max(0.05f, sticker.sizeScale.y);

            center += new Vector2(sticker.normalizedOffset.x * width, sticker.normalizedOffset.y * height);
            if (mirrorHorizontally)
            {
                center.x = 1f - center.x;
                var mirroredLeft = new Vector2(1f - leftEye.x, leftEye.y);
                var mirroredRight = new Vector2(1f - rightEye.x, rightEye.y);
                SortByScreenX(ref mirroredLeft, ref mirroredRight);
                rotationDegrees = PixelAngle(mirroredLeft, mirroredRight, pixelSize);
            }
            else
            {
                rotationDegrees = PixelAngle(leftEye, rightEye, pixelSize);
            }

            var centerPixels = NormalizedTopLeftToPixels(center, pixelSize);
            var sizePixels = new Vector2(width * pixelSize.x, height * pixelSize.y);
            pixelRect = new Rect(centerPixels.x - (sizePixels.x * 0.5f), centerPixels.y - (sizePixels.y * 0.5f), sizePixels.x, sizePixels.y);
            return pixelRect.width > 0f && pixelRect.height > 0f;
        }

        private static Vector2 ResolveAnchorCenter(FaceTrack face, ArStickerAnchor anchor, Vector2 leftEye, Vector2 rightEye)
        {
            return anchor switch
            {
                ArStickerAnchor.Eyes => (leftEye + rightEye) * 0.5f,
                ArStickerAnchor.Forehead => GetPoint(face, ForeheadIndex, new Vector2(face.NormalizedBounds.center.x, face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.9f))),
                ArStickerAnchor.Nose => GetPoint(face, NoseIndex, face.NormalizedBounds.center),
                ArStickerAnchor.Mouth => (GetPoint(face, MouthLeftIndex, new Vector2(face.NormalizedBounds.xMin + (face.NormalizedBounds.width * 0.4f), face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.28f)))
                    + GetPoint(face, MouthRightIndex, new Vector2(face.NormalizedBounds.xMin + (face.NormalizedBounds.width * 0.6f), face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.28f)))) * 0.5f,
                _ => face.NormalizedBounds.center
            };
        }

        private static float ResolveBaseWidth(FaceTrack face, ArStickerAnchor anchor, float eyeDistance)
        {
            return anchor switch
            {
                ArStickerAnchor.FaceBounds => face.NormalizedBounds.width,
                ArStickerAnchor.Forehead => face.NormalizedBounds.width * 0.72f,
                ArStickerAnchor.Mouth => eyeDistance * 1.2f,
                ArStickerAnchor.Nose => eyeDistance * 0.55f,
                _ => eyeDistance * 1.75f
            };
        }

        private static float ResolveBaseHeight(FaceTrack face, ArStickerAnchor anchor, float width)
        {
            return anchor switch
            {
                ArStickerAnchor.FaceBounds => face.NormalizedBounds.height,
                ArStickerAnchor.Forehead => width * 0.42f,
                ArStickerAnchor.Mouth => width * 0.32f,
                ArStickerAnchor.Nose => width * 0.7f,
                _ => width * 0.34f
            };
        }

        private static Vector2 GetPoint(FaceTrack face, int index, Vector2 fallback)
        {
            return face.NormalizedLandmarks != null && index >= 0 && index < face.NormalizedLandmarks.Length
                ? face.NormalizedLandmarks[index]
                : fallback;
        }

        private static void SortByScreenX(ref Vector2 left, ref Vector2 right)
        {
            if (left.x <= right.x)
            {
                return;
            }

            (left, right) = (right, left);
        }

        private static bool IsUsableEyePair(FaceTrack face, Vector2 leftEye, Vector2 rightEye)
        {
            var bounds = face.NormalizedBounds;
            var center = (leftEye + rightEye) * 0.5f;
            var minEyeY = bounds.yMin + (bounds.height * 0.45f);
            var maxEyeY = bounds.yMin + (bounds.height * 0.88f);
            var minEyeDistance = bounds.width * 0.12f;
            return bounds.Contains(leftEye)
                && bounds.Contains(rightEye)
                && center.y >= minEyeY
                && center.y <= maxEyeY
                && Mathf.Abs(leftEye.x - rightEye.x) >= minEyeDistance;
        }

        private static Vector2 NormalizedTopLeftToPixels(Vector2 point, Vector2Int pixelSize)
        {
            return new Vector2(point.x * pixelSize.x, (1f - point.y) * pixelSize.y);
        }

        private static float PixelAngle(Vector2 from, Vector2 to, Vector2Int pixelSize)
        {
            var a = NormalizedTopLeftToPixels(from, pixelSize);
            var b = NormalizedTopLeftToPixels(to, pixelSize);
            var delta = b - a;
            return Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        }
    }

    public sealed class ArStickerRenderer : IDisposable
    {
        private Texture2D defaultSunglasses;
        private Texture2D defaultCrown;
        private Texture2D defaultMustache;

        public static ArStickerDefinition[] CreateDefaultStickers()
        {
            return new[]
            {
                new ArStickerDefinition
                {
                    stickerId = "default_sunglasses",
                    builtinShape = ArStickerBuiltinShape.Sunglasses,
                    anchor = ArStickerAnchor.Eyes,
                    sizeScale = new Vector2(1.25f, 1.25f),
                    tint = Color.white
                },
                new ArStickerDefinition
                {
                    stickerId = "default_crown",
                    builtinShape = ArStickerBuiltinShape.Crown,
                    anchor = ArStickerAnchor.Forehead,
                    normalizedOffset = new Vector2(0f, -0.55f),
                    sizeScale = new Vector2(1.18f, 1f),
                    tint = Color.white
                },
                new ArStickerDefinition
                {
                    stickerId = "default_mustache",
                    builtinShape = ArStickerBuiltinShape.Mustache,
                    anchor = ArStickerAnchor.Mouth,
                    normalizedOffset = new Vector2(0f, -0.22f),
                    sizeScale = new Vector2(0.82f, 0.72f),
                    tint = Color.white
                }
            };
        }

        public Texture2D RenderOverlayTexture(ArTrackingFrame frame, ArStickerDefinition[] stickers, int width, int height, bool mirrorHorizontally)
        {
            var overlay = new Texture2D(width, height, TextureFormat.RGBA32, false);
            ClearTransparent(overlay);
            ApplyToTexture(overlay, frame, stickers, mirrorHorizontally);
            return overlay;
        }

        public ArStickerRenderItem[] BuildRenderItems(ArTrackingFrame frame, ArStickerDefinition[] stickers, int width, int height, bool mirrorHorizontally)
        {
            if (frame?.Faces == null || stickers == null || stickers.Length == 0 || width <= 0 || height <= 0)
            {
                return Array.Empty<ArStickerRenderItem>();
            }

            var pixelSize = new Vector2Int(width, height);
            var items = new List<ArStickerRenderItem>();
            foreach (var face in frame.Faces)
            {
                if (face == null)
                {
                    continue;
                }

                foreach (var sticker in stickers)
                {
                    if (sticker == null || !sticker.enabled)
                    {
                        continue;
                    }

                    var source = ResolveStickerTexture(sticker);
                    if (source == null
                        || !ArStickerTransformResolver.TryResolve(face, sticker, pixelSize, mirrorHorizontally, out var rect, out var rotation))
                    {
                        continue;
                    }

                    items.Add(new ArStickerRenderItem(source, rect, rotation, face.FaceEulerDegrees, face.HasFaceTransform, sticker.tint));
                }
            }

            return items.ToArray();
        }

        public void ApplyToTexture(Texture2D target, ArTrackingFrame frame, ArStickerDefinition[] stickers, bool mirrorHorizontally)
        {
            if (target == null || frame?.Faces == null || stickers == null || stickers.Length == 0)
            {
                return;
            }

            var pixelSize = new Vector2Int(target.width, target.height);
            foreach (var face in frame.Faces)
            {
                if (face == null)
                {
                    continue;
                }

                foreach (var sticker in stickers)
                {
                    if (sticker == null || !sticker.enabled)
                    {
                        continue;
                    }

                    var source = ResolveStickerTexture(sticker);
                    if (source == null
                        || !ArStickerTransformResolver.TryResolve(face, sticker, pixelSize, mirrorHorizontally, out var rect, out var rotation))
                    {
                        continue;
                    }

                    DrawTexture(target, source, rect, rotation, sticker.tint);
                }
            }

            target.Apply(false, false);
        }

        public void Dispose()
        {
            DestroyTexture(defaultSunglasses);
            DestroyTexture(defaultCrown);
            DestroyTexture(defaultMustache);
            defaultSunglasses = null;
            defaultCrown = null;
            defaultMustache = null;
        }

        private Texture2D ResolveStickerTexture(ArStickerDefinition sticker)
        {
            if (sticker.texture != null)
            {
                return sticker.texture;
            }

            return sticker.builtinShape switch
            {
                ArStickerBuiltinShape.Crown => defaultCrown ??= CreateCrownTexture(),
                ArStickerBuiltinShape.Mustache => defaultMustache ??= CreateMustacheTexture(),
                _ => defaultSunglasses ??= CreateSunglassesTexture()
            };
        }

        private static void DrawTexture(Texture2D target, Texture2D source, Rect rect, float rotationDegrees, Color tint)
        {
            var targetPixels = target.GetPixels32();
            var sourcePixels = source.GetPixels32();
            var radians = -rotationDegrees * Mathf.Deg2Rad;
            var cos = Mathf.Cos(radians);
            var sin = Mathf.Sin(radians);
            var centerX = rect.x + (rect.width * 0.5f);
            var centerY = rect.y + (rect.height * 0.5f);
            var minX = Mathf.Max(0, Mathf.FloorToInt(rect.xMin));
            var maxX = Mathf.Min(target.width - 1, Mathf.CeilToInt(rect.xMax));
            var minY = Mathf.Max(0, Mathf.FloorToInt(rect.yMin));
            var maxY = Mathf.Min(target.height - 1, Mathf.CeilToInt(rect.yMax));

            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var localX = x - centerX;
                    var localY = y - centerY;
                    var normalizedX = ((localX * cos) - (localY * sin)) / rect.width + 0.5f;
                    var normalizedY = ((localX * sin) + (localY * cos)) / rect.height + 0.5f;
                    if (normalizedX < 0f || normalizedX > 1f || normalizedY < 0f || normalizedY > 1f)
                    {
                        continue;
                    }

                    var sourceX = Mathf.Clamp(Mathf.RoundToInt(normalizedX * (source.width - 1)), 0, source.width - 1);
                    var sourceY = Mathf.Clamp(Mathf.RoundToInt(normalizedY * (source.height - 1)), 0, source.height - 1);
                    var sourceColor = sourcePixels[sourceY * source.width + sourceX];
                    if (sourceColor.a == 0)
                    {
                        continue;
                    }

                    var tinted = new Color(
                        (sourceColor.r / 255f) * tint.r,
                        (sourceColor.g / 255f) * tint.g,
                        (sourceColor.b / 255f) * tint.b,
                        (sourceColor.a / 255f) * tint.a);
                    var index = y * target.width + x;
                    var existing = targetPixels[index];
                    targetPixels[index] = Blend(existing, tinted);
                }
            }

            target.SetPixels32(targetPixels);
        }

        private static Color32 Blend(Color32 destination, Color source)
        {
            var sourceAlpha = Mathf.Clamp01(source.a);
            var inverse = 1f - sourceAlpha;
            return new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt((source.r * 255f * sourceAlpha) + (destination.r * inverse)), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((source.g * 255f * sourceAlpha) + (destination.g * inverse)), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((source.b * 255f * sourceAlpha) + (destination.b * inverse)), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt((sourceAlpha * 255f) + (destination.a * inverse)), 0, 255));
        }

        private static Texture2D CreateSunglassesTexture()
        {
            var texture = CreateTransparentTexture(256, 96);
            FillEllipse(texture, new Rect(14, 24, 92, 54), Color.black);
            FillEllipse(texture, new Rect(150, 24, 92, 54), Color.black);
            FillRect(texture, new Rect(102, 48, 52, 12), Color.black);
            FillRect(texture, new Rect(0, 52, 28, 8), Color.black);
            FillRect(texture, new Rect(228, 52, 28, 8), Color.black);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateCrownTexture()
        {
            var texture = CreateTransparentTexture(256, 128);
            FillTriangle(texture, new Vector2(18, 24), new Vector2(64, 118), new Vector2(106, 24), new Color(1f, 0.78f, 0.08f, 1f));
            FillTriangle(texture, new Vector2(82, 24), new Vector2(128, 122), new Vector2(174, 24), new Color(1f, 0.86f, 0.12f, 1f));
            FillTriangle(texture, new Vector2(150, 24), new Vector2(194, 118), new Vector2(238, 24), new Color(1f, 0.78f, 0.08f, 1f));
            FillRect(texture, new Rect(22, 18, 212, 26), new Color(1f, 0.68f, 0.06f, 1f));
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateMustacheTexture()
        {
            var texture = CreateTransparentTexture(220, 90);
            FillEllipse(texture, new Rect(24, 18, 88, 48), Color.black);
            FillEllipse(texture, new Rect(108, 18, 88, 48), Color.black);
            FillTriangle(texture, new Vector2(20, 42), new Vector2(2, 28), new Vector2(42, 58), Color.black);
            FillTriangle(texture, new Vector2(200, 42), new Vector2(218, 28), new Vector2(178, 58), Color.black);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateTransparentTexture(int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            ClearTransparent(texture);
            return texture;
        }

        private static void ClearTransparent(Texture2D texture)
        {
            var pixels = new Color32[texture.width * texture.height];
            texture.SetPixels32(pixels);
            texture.Apply(false, false);
        }

        private static void FillRect(Texture2D texture, Rect rect, Color color)
        {
            for (var y = Mathf.Max(0, Mathf.FloorToInt(rect.yMin)); y < Mathf.Min(texture.height, Mathf.CeilToInt(rect.yMax)); y++)
            {
                for (var x = Mathf.Max(0, Mathf.FloorToInt(rect.xMin)); x < Mathf.Min(texture.width, Mathf.CeilToInt(rect.xMax)); x++)
                {
                    texture.SetPixel(x, y, color);
                }
            }
        }

        private static void FillEllipse(Texture2D texture, Rect rect, Color color)
        {
            var radiusX = rect.width * 0.5f;
            var radiusY = rect.height * 0.5f;
            var center = rect.center;
            for (var y = Mathf.Max(0, Mathf.FloorToInt(rect.yMin)); y < Mathf.Min(texture.height, Mathf.CeilToInt(rect.yMax)); y++)
            {
                for (var x = Mathf.Max(0, Mathf.FloorToInt(rect.xMin)); x < Mathf.Min(texture.width, Mathf.CeilToInt(rect.xMax)); x++)
                {
                    var dx = (x - center.x) / radiusX;
                    var dy = (y - center.y) / radiusY;
                    if ((dx * dx) + (dy * dy) <= 1f)
                    {
                        texture.SetPixel(x, y, color);
                    }
                }
            }
        }

        private static void FillTriangle(Texture2D texture, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            var maxX = Mathf.Min(texture.width - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            var maxY = Mathf.Min(texture.height - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            for (var y = minY; y <= maxY; y++)
            {
                for (var x = minX; x <= maxX; x++)
                {
                    var point = new Vector2(x, y);
                    if (IsInsideTriangle(point, a, b, c))
                    {
                        texture.SetPixel(x, y, color);
                    }
                }
            }
        }

        private static bool IsInsideTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = Sign(point, a, b);
            var d2 = Sign(point, b, c);
            var d3 = Sign(point, c, a);
            var hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
            var hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNegative && hasPositive);
        }

        private static float Sign(Vector2 p1, Vector2 p2, Vector2 p3)
        {
            return ((p1.x - p3.x) * (p2.y - p3.y)) - ((p2.x - p3.x) * (p1.y - p3.y));
        }

        private static void DestroyTexture(Texture2D texture)
        {
            if (texture != null)
            {
                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(texture);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }
    }
}
