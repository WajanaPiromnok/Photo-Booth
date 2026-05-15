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
        private static readonly RectInt[] DefaultFrameSlots =
        {
            new(680, 1619, 1267, 912),
            new(2143, 1619, 1267, 912),
            new(680, 455, 1267, 913),
            new(2143, 455, 1267, 913)
        };

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

            var composedPath = Path.Combine(job.Paths.ComposedDirectory, "composed.png");
            var thumbnailPath = Path.Combine(job.Paths.ThumbsDirectory, "thumbnail.png");

            var rawBytes = File.ReadAllBytes(rawImagePath);
            var composedBytes = ApplyLocalAiStyle(rawBytes, aiStyle, preStyleProcessor);
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

            var composedPath = Path.Combine(job.Paths.ComposedDirectory, "composed.png");
            var thumbnailPath = Path.Combine(job.Paths.ThumbsDirectory, "thumbnail.png");
            var composedBytes = ComposePhotoTemplateBytes(rawImagePaths, theme, aiStyle);
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
            File.WriteAllBytes(liveImagePath, ComposePhotoTemplateBytes(rawImagePaths, theme, null));
            return liveImagePath;
        }

        private static byte[] ComposePhotoTemplateBytes(IReadOnlyList<string> rawImagePaths, BoothThemeOption theme, BoothAiStyleOption aiStyle)
        {
            var template = ResolveFrameTemplate(theme);
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

                var slotCount = Mathf.Min(captures.Count, DefaultFrameSlots.Length);
                for (var i = 0; i < slotCount; i++)
                {
                    var slot = DefaultFrameSlots[i];
                    DrawAspectFill(captures[i], canvas, slot.x, slot.y, slot.width, slot.height);
                }

                canvas.Apply(false, false);
                return ImageConversion.EncodeToPNG(canvas);
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

        private static Texture2D ResolveFrameTemplate(BoothThemeOption theme)
        {
            if (theme?.frameTemplateTexture != null)
            {
                return theme.frameTemplateTexture;
            }

            if (theme?.frameTemplateSprite != null)
            {
                return theme.frameTemplateSprite.texture;
            }

            var resourcePath = theme?.FrameTemplateResourcePath;
            if (string.IsNullOrWhiteSpace(resourcePath))
            {
                resourcePath = DefaultFrameTemplateResourcePath;
            }

            return Resources.Load<Texture2D>(resourcePath);
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
                var gridBytes = ImageConversion.EncodeToPNG(canvas);
                return ApplyLocalAiStyle(gridBytes, aiStyle, null);
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
                return ImageConversion.EncodeToPNG(thumbnail);
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
    }

    public sealed class BoothCompositionResult
    {
        public string ComposedImagePath;
        public string ThumbnailPath;
    }
}
