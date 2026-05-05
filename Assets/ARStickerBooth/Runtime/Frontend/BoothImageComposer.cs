using System;
using System.IO;
using PhotoBooth.Booth.Domain;
using UnityEngine;

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothImageComposer
    {
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
