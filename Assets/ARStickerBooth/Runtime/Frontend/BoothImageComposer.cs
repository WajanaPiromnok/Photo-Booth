using System;
using System.Collections.Generic;
using System.IO;
using PhotoBooth.Booth.Domain;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothImageComposer
    {
        private const string DefaultFrameTemplateResourcePath = "MrkremeUi/piece_03";
        private const int FinalJpegQuality = 86;
        private const int ThumbnailJpegQuality = 82;
        private static readonly RectInt[] DefaultFrameSlots =
        {
            new(680, 1619, 1267, 912),
            new(2143, 1619, 1267, 912),
            new(680, 455, 1267, 913),
            new(2143, 455, 1267, 913)
        };
        private static readonly RectInt[] ImagePreview1FrameSlots =
        {
            new(62, 137, 2011, 1239)
        };
        private static readonly RectInt[] ImagePreview2FrameSlots =
        {
            new(121, 1425, 1896, 1084)
        };
        private static readonly RectInt ImagePreview1FromNameSlot = new(300, 2000, 900, 90);
        private static readonly RectInt ImagePreview2FromNameSlot = new(300, 1970, 900, 90);

        public BoothCompositionResult Compose(BoothJob job, string rawImagePath, Vector2Int thumbnailSize, BoothAiStyleOption aiStyle = null, Action<Texture2D> preStyleProcessor = null)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (string.IsNullOrWhiteSpace(rawImagePath) || !File.Exists(rawImagePath))
            {
                throw new FileNotFoundException("Raw capture image is missing.", rawImagePath);
            }

            Directory.CreateDirectory(job.Paths.ComposedDirectory);
            Directory.CreateDirectory(job.Paths.ThumbsDirectory);

            var composedPath = Path.Combine(job.Paths.ComposedDirectory, "composed.jpg");
            var thumbnailPath = Path.Combine(job.Paths.ThumbsDirectory, "thumbnail.jpg");

            var rawBytes = File.ReadAllBytes(rawImagePath);
            var composedBytes = EncodeJpeg(ApplyLocalAiStyle(rawBytes, aiStyle, preStyleProcessor), FinalJpegQuality);
            File.WriteAllBytes(composedPath, composedBytes);

            var thumbnail = CreateThumbnail(composedBytes, thumbnailSize);
            File.WriteAllBytes(thumbnailPath, thumbnail);

            return new BoothCompositionResult
            {
                ComposedImagePath = composedPath,
                ThumbnailPath = thumbnailPath
            };
        }

        public BoothCompositionResult ComposePhotoGrid(BoothJob job, IReadOnlyList<string> rawImagePaths, Vector2Int thumbnailSize, BoothAiStyleOption aiStyle = null)
        {
            return ComposePhotoGrid(job, rawImagePaths, thumbnailSize, null, aiStyle);
        }

        public BoothCompositionResult ComposePhotoGrid(BoothJob job, IReadOnlyList<string> rawImagePaths, Vector2Int thumbnailSize, BoothThemeOption theme, BoothAiStyleOption aiStyle = null)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (rawImagePaths == null || rawImagePaths.Count == 0)
            {
                throw new ArgumentException("At least one raw capture image is required.", nameof(rawImagePaths));
            }

            Directory.CreateDirectory(job.Paths.ComposedDirectory);
            Directory.CreateDirectory(job.Paths.ThumbsDirectory);

            var composedPath = Path.Combine(job.Paths.ComposedDirectory, "composed.jpg");
            var thumbnailPath = Path.Combine(job.Paths.ThumbsDirectory, "thumbnail.jpg");
            var composedBytes = ComposePhotoTemplateBytes(rawImagePaths, theme, aiStyle, job.PassengerName);
            File.WriteAllBytes(composedPath, composedBytes);

            var thumbnail = CreateThumbnail(composedBytes, thumbnailSize);
            File.WriteAllBytes(thumbnailPath, thumbnail);

            return new BoothCompositionResult
            {
                ComposedImagePath = composedPath,
                ThumbnailPath = thumbnailPath
            };
        }

        public string ComposeLiveImage(BoothJob job, IReadOnlyList<string> rawImagePaths)
        {
            return ComposeLiveImage(job, rawImagePaths, null);
        }

        public string ComposeLiveImage(BoothJob job, IReadOnlyList<string> rawImagePaths, BoothThemeOption theme)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (rawImagePaths == null || rawImagePaths.Count == 0)
            {
                throw new ArgumentException("At least one raw capture image is required.", nameof(rawImagePaths));
            }

            Directory.CreateDirectory(job.Paths.ComposedDirectory);
            var liveImagePath = Path.Combine(job.Paths.ComposedDirectory, "live.png");
            File.WriteAllBytes(liveImagePath, ComposePhotoTemplateBytes(rawImagePaths, theme, null, job.PassengerName));
            return liveImagePath;
        }

        private static byte[] ComposePhotoTemplateBytes(IReadOnlyList<string> rawImagePaths, BoothThemeOption theme, BoothAiStyleOption aiStyle, string passengerName)
        {
            var template = ResolveFrameTemplate(theme, out var shouldDestroyTemplate);
            if (template == null)
            {
                Debug.LogWarning($"Photo frame template not found for theme '{theme?.themeId ?? "default"}'. Falling back to grid composition.");
                return ComposePhotoGridBytes(rawImagePaths, aiStyle);
            }

            var captures = new List<Texture2D>();
            Texture2D canvas = null;
            try
            {
                LoadCaptures(rawImagePaths, captures);
                canvas = new Texture2D(template.width, template.height, TextureFormat.RGBA32, false);
                canvas.SetPixels(template.GetPixels());

                var frameSlots = ResolveFrameSlots(theme);
                var slotCount = Mathf.Min(captures.Count, frameSlots.Length);
                for (var i = 0; i < slotCount; i++)
                {
                    var slot = frameSlots[i];
                    DrawAspectFill(captures[i], canvas, slot.x, slot.y, slot.width, slot.height);
                }

                DrawPassengerName(canvas, ResolveFromNameSlot(theme), passengerName);
                canvas.Apply(false, false);
                return ImageConversion.EncodeToJPG(canvas, FinalJpegQuality);
            }
            finally
            {
                DestroyTextures(captures);
                if (canvas != null)
                {
                    UnityEngine.Object.Destroy(canvas);
                }

                if (shouldDestroyTemplate && template != null)
                {
                    UnityEngine.Object.Destroy(template);
                }
            }
        }

        private static Texture2D ResolveFrameTemplate(BoothThemeOption theme, out bool shouldDestroy)
        {
            shouldDestroy = false;
            if (theme?.frameTemplateTexture != null)
            {
                return theme.frameTemplateTexture;
            }

            var labelTemplate = LoadLabelFrameTemplate(theme);
            if (labelTemplate != null)
            {
                shouldDestroy = true;
                return labelTemplate;
            }

            if (theme?.frameTemplateSprite != null)
            {
                shouldDestroy = true;
                return CloneReadableTexture(theme.frameTemplateSprite.texture);
            }

            var resourcePath = theme?.FrameTemplateResourcePath;
            if (string.IsNullOrWhiteSpace(resourcePath))
            {
                resourcePath = DefaultFrameTemplateResourcePath;
            }

            return Resources.Load<Texture2D>(resourcePath);
        }

        private static Texture2D LoadLabelFrameTemplate(BoothThemeOption theme)
        {
            var previewId = ResolveImagePreviewId(theme);
            var fileName = previewId == "2" || previewId == "image_preview_2" || previewId == "theme_02"
                ? "2.png"
                : previewId == "1" || previewId == "image_preview_1" || previewId == "theme_01"
                    ? "1.png"
                    : null;
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var path = Path.Combine(Application.dataPath, "UI", "Label", fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
            {
                return texture;
            }

            UnityEngine.Object.Destroy(texture);
            return null;
        }

        private static Texture2D CloneReadableTexture(Texture2D source)
        {
            if (source == null)
            {
                return null;
            }

            var previous = RenderTexture.active;
            var temporary = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(source, temporary);
                RenderTexture.active = temporary;
                var clone = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                clone.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                clone.Apply(false, false);
                return clone;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temporary);
            }
        }

        private static RectInt[] ResolveFrameSlots(BoothThemeOption theme)
        {
            var previewId = ResolveImagePreviewId(theme);
            if (previewId == "2" || previewId == "image_preview_2" || previewId == "theme_02")
            {
                return ImagePreview2FrameSlots;
            }

            if (previewId == "1" || previewId == "image_preview_1" || previewId == "theme_01")
            {
                return ImagePreview1FrameSlots;
            }

            return DefaultFrameSlots;
        }

        private static RectInt ResolveFromNameSlot(BoothThemeOption theme)
        {
            var previewId = ResolveImagePreviewId(theme);
            return previewId == "2" || previewId == "image_preview_2" || previewId == "theme_02"
                ? ImagePreview2FromNameSlot
                : ImagePreview1FromNameSlot;
        }

        private static string ResolveImagePreviewId(BoothThemeOption theme)
        {
            var value = theme?.ImagePreviewId;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = theme?.BackendFrameId;
            }

            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static byte[] ComposePhotoGridBytes(IReadOnlyList<string> rawImagePaths, BoothAiStyleOption aiStyle)
        {
            var captures = new List<Texture2D>();
            Texture2D canvas = null;
            try
            {
                LoadCaptures(rawImagePaths, captures);

                var cellWidth = Math.Max(16, captures[0].width);
                var cellHeight = Math.Max(16, captures[0].height);
                var columns = captures.Count == 1 ? 1 : 2;
                var rows = Mathf.CeilToInt(captures.Count / (float)columns);
                canvas = new Texture2D(cellWidth * columns, cellHeight * rows, TextureFormat.RGBA32, false);

                for (var i = 0; i < captures.Count; i++)
                {
                    var column = i % columns;
                    var row = i / columns;
                    DrawAspectFill(captures[i], canvas, column * cellWidth, (rows - row - 1) * cellHeight, cellWidth, cellHeight);
                }

                canvas.Apply(false, false);
                var gridBytes = ImageConversion.EncodeToJPG(canvas, FinalJpegQuality);
                return EncodeJpeg(ApplyLocalAiStyle(gridBytes, aiStyle, null), FinalJpegQuality);
            }
            finally
            {
                DestroyTextures(captures);

                if (canvas != null)
                {
                    UnityEngine.Object.Destroy(canvas);
                }
            }
        }

        private static void LoadCaptures(IReadOnlyList<string> rawImagePaths, List<Texture2D> captures)
        {
            foreach (var rawImagePath in rawImagePaths)
            {
                if (string.IsNullOrWhiteSpace(rawImagePath) || !File.Exists(rawImagePath))
                {
                    throw new FileNotFoundException("Raw capture image is missing.", rawImagePath);
                }

                var capture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(capture, File.ReadAllBytes(rawImagePath)))
                {
                    UnityEngine.Object.Destroy(capture);
                    throw new InvalidDataException($"Raw capture image is not a readable PNG: {rawImagePath}");
                }

                captures.Add(capture);
            }
        }

        private static void DestroyTextures(IEnumerable<Texture2D> textures)
        {
            foreach (var texture in textures)
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        private static void DrawAspectFill(Texture2D source, Texture2D target, int targetX, int targetY, int targetWidth, int targetHeight)
        {
            var sourceAspect = source.width / (float)source.height;
            var targetAspect = targetWidth / (float)targetHeight;
            var sampleWidth = 1f;
            var sampleHeight = 1f;
            var sampleX = 0f;
            var sampleY = 0f;

            if (sourceAspect > targetAspect)
            {
                sampleWidth = targetAspect / sourceAspect;
                sampleX = (1f - sampleWidth) * 0.5f;
            }
            else if (sourceAspect < targetAspect)
            {
                sampleHeight = sourceAspect / targetAspect;
                sampleY = (1f - sampleHeight) * 0.5f;
            }

            for (var y = 0; y < targetHeight; y++)
            {
                var v = targetHeight <= 1 ? 0f : y / (float)(targetHeight - 1);
                for (var x = 0; x < targetWidth; x++)
                {
                    var u = targetWidth <= 1 ? 0f : x / (float)(targetWidth - 1);
                    target.SetPixel(targetX + x, targetY + y, source.GetPixelBilinear(sampleX + (u * sampleWidth), sampleY + (v * sampleHeight)));
                }
            }
        }

        private static void DrawPassengerName(Texture2D target, RectInt slot, string passengerName)
        {
            if (target == null || slot.width <= 0 || slot.height <= 0 || string.IsNullOrWhiteSpace(passengerName))
            {
                return;
            }

            var value = passengerName.Trim().ToUpperInvariant();
            var scale = Mathf.Max(3, Mathf.Min(slot.height / 9, slot.width / Math.Max(1, value.Length * 6)));
            var textWidth = value.Length * 6 * scale;
            var startX = slot.x + Mathf.Max(0, (slot.width - textWidth) / 2);
            var startY = slot.y + Mathf.Max(0, (slot.height - (7 * scale)) / 2);
            for (var i = 0; i < value.Length; i++)
            {
                DrawGlyph(target, value[i], startX + (i * 6 * scale), startY, scale, Color.black);
            }
        }

        private static void DrawGlyph(Texture2D target, char character, int startX, int startY, int scale, Color color)
        {
            var rows = ResolveGlyph(character);
            for (var row = 0; row < rows.Length; row++)
            {
                var bits = rows[rows.Length - row - 1];
                for (var column = 0; column < 5; column++)
                {
                    if (((bits >> (4 - column)) & 1) == 0)
                    {
                        continue;
                    }

                    for (var y = 0; y < scale; y++)
                    {
                        for (var x = 0; x < scale; x++)
                        {
                            var px = startX + (column * scale) + x;
                            var py = startY + (row * scale) + y;
                            if (px >= 0 && py >= 0 && px < target.width && py < target.height)
                            {
                                target.SetPixel(px, py, color);
                            }
                        }
                    }
                }
            }
        }

        private static int[] ResolveGlyph(char character)
        {
            return character switch
            {
                'A' => new[] { 0b01110, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
                'B' => new[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10001, 0b10001, 0b11110 },
                'C' => new[] { 0b01111, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b01111 },
                'D' => new[] { 0b11110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b11110 },
                'E' => new[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b11111 },
                'F' => new[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b10000, 0b10000, 0b10000 },
                'G' => new[] { 0b01111, 0b10000, 0b10000, 0b10111, 0b10001, 0b10001, 0b01111 },
                'H' => new[] { 0b10001, 0b10001, 0b10001, 0b11111, 0b10001, 0b10001, 0b10001 },
                'I' => new[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b11111 },
                'J' => new[] { 0b00111, 0b00010, 0b00010, 0b00010, 0b10010, 0b10010, 0b01100 },
                'K' => new[] { 0b10001, 0b10010, 0b10100, 0b11000, 0b10100, 0b10010, 0b10001 },
                'L' => new[] { 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b10000, 0b11111 },
                'M' => new[] { 0b10001, 0b11011, 0b10101, 0b10101, 0b10001, 0b10001, 0b10001 },
                'N' => new[] { 0b10001, 0b11001, 0b10101, 0b10011, 0b10001, 0b10001, 0b10001 },
                'O' => new[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
                'P' => new[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10000, 0b10000, 0b10000 },
                'Q' => new[] { 0b01110, 0b10001, 0b10001, 0b10001, 0b10101, 0b10010, 0b01101 },
                'R' => new[] { 0b11110, 0b10001, 0b10001, 0b11110, 0b10100, 0b10010, 0b10001 },
                'S' => new[] { 0b01111, 0b10000, 0b10000, 0b01110, 0b00001, 0b00001, 0b11110 },
                'T' => new[] { 0b11111, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100, 0b00100 },
                'U' => new[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01110 },
                'V' => new[] { 0b10001, 0b10001, 0b10001, 0b10001, 0b10001, 0b01010, 0b00100 },
                'W' => new[] { 0b10001, 0b10001, 0b10001, 0b10101, 0b10101, 0b10101, 0b01010 },
                'X' => new[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b01010, 0b10001, 0b10001 },
                'Y' => new[] { 0b10001, 0b10001, 0b01010, 0b00100, 0b00100, 0b00100, 0b00100 },
                'Z' => new[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b10000, 0b11111 },
                '0' => new[] { 0b01110, 0b10001, 0b10011, 0b10101, 0b11001, 0b10001, 0b01110 },
                '1' => new[] { 0b00100, 0b01100, 0b00100, 0b00100, 0b00100, 0b00100, 0b01110 },
                '2' => new[] { 0b01110, 0b10001, 0b00001, 0b00010, 0b00100, 0b01000, 0b11111 },
                '3' => new[] { 0b11110, 0b00001, 0b00001, 0b01110, 0b00001, 0b00001, 0b11110 },
                '4' => new[] { 0b00010, 0b00110, 0b01010, 0b10010, 0b11111, 0b00010, 0b00010 },
                '5' => new[] { 0b11111, 0b10000, 0b10000, 0b11110, 0b00001, 0b00001, 0b11110 },
                '6' => new[] { 0b01110, 0b10000, 0b10000, 0b11110, 0b10001, 0b10001, 0b01110 },
                '7' => new[] { 0b11111, 0b00001, 0b00010, 0b00100, 0b01000, 0b01000, 0b01000 },
                '8' => new[] { 0b01110, 0b10001, 0b10001, 0b01110, 0b10001, 0b10001, 0b01110 },
                '9' => new[] { 0b01110, 0b10001, 0b10001, 0b01111, 0b00001, 0b00001, 0b01110 },
                '-' => new[] { 0b00000, 0b00000, 0b00000, 0b11111, 0b00000, 0b00000, 0b00000 },
                _ => new[] { 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000, 0b00000 }
            };
        }

        private static byte[] ApplyLocalAiStyle(byte[] sourceBytes, BoothAiStyleOption aiStyle, Action<Texture2D> preStyleProcessor)
        {
            if ((aiStyle == null || !aiStyle.applyLocalStylizedPreview) && preStyleProcessor == null)
            {
                return sourceBytes;
            }

            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D styled = null;
            try
            {
                if (!ImageConversion.LoadImage(source, sourceBytes))
                {
                    return sourceBytes;
                }

                preStyleProcessor?.Invoke(source);

                if (aiStyle == null || !aiStyle.applyLocalStylizedPreview)
                {
                    return ImageConversion.EncodeToPNG(source);
                }

                styled = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                for (var y = 0; y < source.height; y++)
                {
                    var vertical = source.height <= 1 ? 0f : y / (float)(source.height - 1);
                    var backdrop = Color.Lerp(aiStyle.secondaryColor, aiStyle.primaryColor, vertical);
                    for (var x = 0; x < source.width; x++)
                    {
                        var pixel = source.GetPixel(x, y);
                        var luminance = (pixel.r * 0.299f) + (pixel.g * 0.587f) + (pixel.b * 0.114f);
                        var tinted = Color.Lerp(backdrop, pixel, 0.72f);

                        if (ContainsToken(aiStyle.styleId, "neon"))
                        {
                            tinted = Color.Lerp(tinted, new Color(1f - pixel.r, 0.45f + (luminance * 0.45f), 1f, 1f), 0.22f);
                        }
                        else if (ContainsToken(aiStyle.styleId, "water"))
                        {
                            tinted = Color.Lerp(tinted, new Color(luminance + 0.12f, pixel.g + 0.08f, pixel.b + 0.18f, 1f), 0.28f);
                        }

                        var border = x < 24 || y < 24 || x >= source.width - 24 || y >= source.height - 24;
                        styled.SetPixel(x, y, border ? Color.Lerp(tinted, backdrop, 0.45f) : tinted);
                    }
                }

                styled.Apply(false, false);
                return ImageConversion.EncodeToPNG(styled);
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
                if (styled != null)
                {
                    UnityEngine.Object.Destroy(styled);
                }
            }
        }

        private static bool ContainsToken(string value, string token)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static byte[] CreateThumbnail(byte[] sourceBytes, Vector2Int requestedSize)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D thumbnail = null;
            try
            {
                if (!ImageConversion.LoadImage(source, sourceBytes))
                {
                    return sourceBytes;
                }

                var width = Math.Max(16, requestedSize.x);
                var height = Math.Max(16, requestedSize.y);
                thumbnail = new Texture2D(width, height, TextureFormat.RGBA32, false);

                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var u = width <= 1 ? 0f : x / (float)(width - 1);
                        var v = height <= 1 ? 0f : y / (float)(height - 1);
                        thumbnail.SetPixel(x, y, source.GetPixelBilinear(u, v));
                    }
                }

                thumbnail.Apply(false, false);
                return ImageConversion.EncodeToJPG(thumbnail, ThumbnailJpegQuality);
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
                if (thumbnail != null)
                {
                    UnityEngine.Object.Destroy(thumbnail);
                }
            }
        }

        private static byte[] EncodeJpeg(byte[] sourceBytes, int quality)
        {
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                return ImageConversion.LoadImage(source, sourceBytes)
                    ? ImageConversion.EncodeToJPG(source, quality)
                    : sourceBytes;
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
            }
        }
    }

    public sealed class BoothCompositionResult
    {
        public string ComposedImagePath;
        public string ThumbnailPath;
    }
}
