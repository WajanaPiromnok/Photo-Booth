using System;
using System.Collections.Generic;
using System.IO;
using PhotoBooth.Booth.Domain;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothImageComposer
    {
        private const string DefaultFrameTemplateResourcePath = "MrkremeUi/piece_03";
        private const string PassengerNameTmpFontAssetPath = "Assets/UI/Kooky/Fonts/Franie-SBold SDF.asset";
        private const string PassengerNameFontAssetPath = "Assets/UI/Kooky/Fonts/Franie-SBold.otf";
        private const string PassengerNameFontPath = "UI/Kooky/Fonts/Franie-SBold.otf";
        private const string KookyPhotoBoothDirectory = "UI/Kooky/Separate pieces/Photo booth";
        private const string KookyFontsDirectory = "UI/Kooky/Fonts";
        private const string KookySceneName = "PhotoBooth-Kooky";
        private const int FinalJpegQuality = 86;
        private const int PrintJpegQuality = 96;
        private const int ThumbnailJpegQuality = 82;
        private const int KookyServerWidth = 1800;
        private const int KookyServerHeight = 1200;
        private const int KookyPrintWidth = 3600;
        private const int KookyPrintHeight = 2400;
        private const int ImagePreview1TemplateSourceWidth = 12657;
        private const int ImagePreview1TemplateSourceHeight = 8445;
        private const int ImagePreview2TemplateSourceWidth = 12640;
        private const int ImagePreview2TemplateSourceHeight = 8399;
        private const int PassengerNameFontSize = 36;
        private static readonly Color PassengerNameFrame1Color = new Color32(0x23, 0x1F, 0x20, 0xFF);
        private static readonly Color PassengerNameFrame2Color = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
        private static readonly RectInt[] DefaultFrameSlots =
        {
            new(680, 1619, 1267, 912),
            new(2143, 1619, 1267, 912),
            new(680, 455, 1267, 913),
            new(2143, 455, 1267, 913)
        };
        private static readonly RectInt[] ImagePreview1FrameSlots =
        {
            new(1090, 1133, 3240, 3067),
            new(4689, 1133, 3240, 3067),
            new(8276, 1133, 3240, 3067)
        };
        private static readonly RectInt[] ImagePreview2FrameSlots =
        {
            new(1080, 1110, 3240, 3067),
            new(4670, 1110, 3240, 3067),
            new(8260, 1110, 3240, 3067)
        };
        private static readonly RectInt[] ImagePreview1OverlaySlots =
        {
            new(1090, 1133, 3240, 3067),
            new(4689, 1133, 3240, 3067),
            new(8276, 1133, 3240, 3067)
        };
        private static readonly string[] ImagePreview1OverlayFileNames =
        {
            "frame01_01.png",
            "frame01_02.png",
            "frame01_03.png"
        };
        private static readonly RectInt[] ImagePreview2OverlaySlots =
        {
            new(1080, 1110, 3240, 3067),
            new(4670, 1110, 3240, 3067),
            new(8260, 1110, 3240, 3067)
        };
        private static readonly string[] ImagePreview2OverlayFileNames =
        {
            "frame02_01.png",
            "frame02_02.png",
            "frame02_03.png"
        };
        private static readonly RectInt ImagePreview1FromNameSlot = new(5775, 6550, 2200, 280);
        private static readonly RectInt ImagePreview2FromNameSlot = new(4380, 6200, 1950, 280);

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
            var printPath = Path.Combine(job.Paths.ComposedDirectory, "print.jpg");
            var thumbnailPath = Path.Combine(job.Paths.ThumbsDirectory, "thumbnail.jpg");
            var composedBytes = ComposePhotoTemplateBytes(rawImagePaths, theme, aiStyle, job.PassengerName);
            var printBytes = ComposePhotoTemplateBytes(
                rawImagePaths,
                theme,
                aiStyle,
                job.PassengerName,
                usePrintResolution: true,
                jpegQuality: PrintJpegQuality);
            File.WriteAllBytes(composedPath, composedBytes);
            File.WriteAllBytes(printPath, printBytes);

            var thumbnail = CreateThumbnail(composedBytes, thumbnailSize);
            File.WriteAllBytes(thumbnailPath, thumbnail);

            return new BoothCompositionResult
            {
                ComposedImagePath = composedPath,
                PrintImagePath = printPath,
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

        private static byte[] ComposePhotoTemplateBytes(
            IReadOnlyList<string> rawImagePaths,
            BoothThemeOption theme,
            BoothAiStyleOption aiStyle,
            string passengerName,
            float captureBrightenAmount = 0f,
            bool usePrintResolution = false,
            int jpegQuality = FinalJpegQuality)
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
                canvas = CreateTemplateCanvas(template, theme, usePrintResolution, out var scaleX, out var scaleY);

                var frameSlots = ResolveFrameSlots(theme);
                var slotCount = Mathf.Min(captures.Count, frameSlots.Length);
                for (var i = 0; i < slotCount; i++)
                {
                    var slot = ScaleRect(frameSlots[i], scaleX, scaleY);
                    DrawAspectFill(captures[i], canvas, slot.x, slot.y, slot.width, slot.height, captureBrightenAmount);
                }

                DrawFrameOverlays(canvas, theme, scaleX, scaleY);
                DrawPassengerName(canvas, ScaleRect(ResolveFromNameSlot(theme), scaleX, scaleY), passengerName, ResolvePassengerNameColor(theme));
                canvas.Apply(false, false);
                return ImageConversion.EncodeToJPG(canvas, jpegQuality);
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
            if (IsImagePreview1(theme))
            {
                return LoadTextureFromAssets(KookyPhotoBoothDirectory, "ticket_1.png");
            }

            if (IsImagePreview2(theme))
            {
                return LoadTextureFromAssets(KookyPhotoBoothDirectory, "ticket_2.png");
            }

            return null;
        }

        private static Texture2D LoadTextureFromAssets(string relativeDirectory, string fileName)
        {
            var path = Path.Combine(Application.dataPath, relativeDirectory, fileName);
            if (File.Exists(path))
            {
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
                {
                    return texture;
                }

                UnityEngine.Object.Destroy(texture);
            }

            var resourcePath = $"{relativeDirectory}/{Path.GetFileNameWithoutExtension(fileName)}";
            var resourceTexture = Resources.Load<Texture2D>(resourcePath);
            return resourceTexture != null ? CloneReadableTexture(resourceTexture) : null;
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

        private static bool IsImagePreview1(BoothThemeOption theme)
        {
            var previewId = ResolveImagePreviewId(theme);
            return previewId == "1"
                || previewId == "image_preview_1"
                || previewId == "theme_01"
                || (string.IsNullOrWhiteSpace(previewId) && IsKookyScene());
        }

        private static bool IsImagePreview2(BoothThemeOption theme)
        {
            var previewId = ResolveImagePreviewId(theme);
            return previewId == "2"
                || previewId == "image_preview_2"
                || previewId == "theme_02";
        }

        private static bool IsKookyPrintTemplate(BoothThemeOption theme)
        {
            return IsImagePreview1(theme) || IsImagePreview2(theme);
        }

        private static RectInt ResolveFromNameSlot(BoothThemeOption theme)
        {
            var previewId = ResolveImagePreviewId(theme);
            return previewId == "2" || previewId == "image_preview_2" || previewId == "theme_02"
                ? ImagePreview2FromNameSlot
                : ImagePreview1FromNameSlot;
        }

        private static Color ResolvePassengerNameColor(BoothThemeOption theme)
        {
            return IsImagePreview2(theme) ? PassengerNameFrame2Color : PassengerNameFrame1Color;
        }

        private static string ResolveImagePreviewId(BoothThemeOption theme)
        {
            var value = theme?.ImagePreviewId;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = theme?.BackendFrameId;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                value = theme?.themeId;
            }

            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static bool IsKookyScene()
        {
            return string.Equals(SceneManager.GetActiveScene().name, KookySceneName, StringComparison.Ordinal);
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

        private static Texture2D CreateTemplateCanvas(Texture2D template, BoothThemeOption theme, bool usePrintResolution, out float scaleX, out float scaleY)
        {
            if (IsKookyPrintTemplate(theme))
            {
                var sourceSize = ResolveKookyTemplateSourceSize(theme);
                var targetWidth = usePrintResolution ? KookyPrintWidth : KookyServerWidth;
                var targetHeight = usePrintResolution ? KookyPrintHeight : KookyServerHeight;
                scaleX = targetWidth / (float)sourceSize.x;
                scaleY = targetHeight / (float)sourceSize.y;
                var scaled = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
                DrawTextureScaledAlpha(template, scaled, 0, 0, scaled.width, scaled.height);
                return scaled;
            }

            scaleX = 1f;
            scaleY = 1f;
            var canvas = new Texture2D(template.width, template.height, TextureFormat.RGBA32, false);
            canvas.SetPixels(template.GetPixels());
            return canvas;
        }

        private static Vector2Int ResolveKookyTemplateSourceSize(BoothThemeOption theme)
        {
            return IsImagePreview2(theme)
                ? new Vector2Int(ImagePreview2TemplateSourceWidth, ImagePreview2TemplateSourceHeight)
                : new Vector2Int(ImagePreview1TemplateSourceWidth, ImagePreview1TemplateSourceHeight);
        }

        private static RectInt ScaleRect(RectInt source, float scaleX, float scaleY)
        {
            return new RectInt(
                Mathf.RoundToInt(source.x * scaleX),
                Mathf.RoundToInt(source.y * scaleY),
                Mathf.Max(1, Mathf.RoundToInt(source.width * scaleX)),
                Mathf.Max(1, Mathf.RoundToInt(source.height * scaleY)));
        }

        private static void DrawFrameOverlays(Texture2D canvas, BoothThemeOption theme, float scaleX, float scaleY)
        {
            if (canvas == null || !IsKookyPrintTemplate(theme))
            {
                return;
            }

            var overlayFileNames = IsImagePreview2(theme) ? ImagePreview2OverlayFileNames : ImagePreview1OverlayFileNames;
            var overlaySlots = IsImagePreview2(theme) ? ImagePreview2OverlaySlots : ImagePreview1OverlaySlots;
            var overlays = new List<Texture2D>();
            try
            {
                for (var i = 0; i < overlayFileNames.Length && i < overlaySlots.Length; i++)
                {
                    var overlay = LoadTextureFromAssets(KookyPhotoBoothDirectory, overlayFileNames[i]);
                    if (overlay == null)
                    {
                        Debug.LogWarning($"Kooky photo overlay not found: {overlayFileNames[i]}");
                        continue;
                    }

                    overlays.Add(overlay);
                    var slot = ScaleRect(overlaySlots[i], scaleX, scaleY);
                    DrawTextureScaledAlpha(overlay, canvas, slot.x, slot.y, slot.width, slot.height);
                }
            }
            finally
            {
                DestroyTextures(overlays);
            }
        }

        private static void DrawTextureScaledAlpha(Texture2D source, Texture2D target, int targetX, int targetY, int targetWidth, int targetHeight)
        {
            if (source == null || target == null || targetWidth <= 0 || targetHeight <= 0)
            {
                return;
            }

            for (var y = 0; y < targetHeight; y++)
            {
                var v = targetHeight <= 1 ? 0f : y / (float)(targetHeight - 1);
                for (var x = 0; x < targetWidth; x++)
                {
                    var u = targetWidth <= 1 ? 0f : x / (float)(targetWidth - 1);
                    var overlay = source.GetPixelBilinear(u, v);
                    if (overlay.a <= 0.001f)
                    {
                        continue;
                    }

                    var px = targetX + x;
                    var py = targetY + y;
                    if (px < 0 || py < 0 || px >= target.width || py >= target.height)
                    {
                        continue;
                    }

                    target.SetPixel(px, py, Color.Lerp(target.GetPixel(px, py), overlay, overlay.a));
                }
            }
        }

        private static void DrawAspectFill(Texture2D source, Texture2D target, int targetX, int targetY, int targetWidth, int targetHeight, float brightenAmount = 0f)
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
                    var pixel = source.GetPixelBilinear(sampleX + (u * sampleWidth), sampleY + (v * sampleHeight));
                    target.SetPixel(targetX + x, targetY + y, BrightenPhotoPixel(pixel, brightenAmount));
                }
            }
        }

        private static Color BrightenPhotoPixel(Color pixel, float amount)
        {
            amount = Mathf.Clamp01(amount);
            if (amount <= 0f)
            {
                return pixel;
            }

            return new Color(
                AdjustPrintPhotoChannel(pixel.r, amount),
                AdjustPrintPhotoChannel(pixel.g, amount),
                AdjustPrintPhotoChannel(pixel.b, amount),
                pixel.a);
        }

        private static float AdjustPrintPhotoChannel(float value, float amount)
        {
            var lifted = Mathf.Pow(Mathf.Clamp01(value), 1f / (1f + (amount * 0.9f)));
            var contrast = 1f + (amount * 0.28f);
            return Mathf.Clamp01(((lifted - 0.5f) * contrast) + 0.5f);
        }

        private static void DrawPassengerName(Texture2D target, RectInt slot, string passengerName, Color color)
        {
            if (target == null || slot.width <= 0 || slot.height <= 0 || string.IsNullOrWhiteSpace(passengerName))
            {
                return;
            }

            var value = NormalizePassengerName(passengerName);
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (DrawPassengerNameWithUiFont(target, slot, value, color))
            {
                return;
            }

            if (DrawPassengerNameWithFont(target, slot, value, color))
            {
                return;
            }

            var scale = Mathf.Max(3, Mathf.Min(slot.height / 9, slot.width / Math.Max(1, value.Length * 6)));
            var textWidth = value.Length * 6 * scale;
            var startX = slot.x;
            var startY = slot.y + Mathf.Max(0, (slot.height - (7 * scale)) / 2);
            for (var i = 0; i < value.Length; i++)
            {
                var glyphX = startX + (i * 6 * scale);
                DrawGlyph(target, value[i], glyphX, startY, scale, color);
                DrawGlyph(target, value[i], glyphX + Mathf.Max(1, scale / 4), startY, scale, color);
            }
        }

        private static string NormalizePassengerName(string passengerName)
        {
            if (string.IsNullOrWhiteSpace(passengerName))
            {
                return string.Empty;
            }

            var value = passengerName.Trim().ToUpperInvariant();
            return value == "-" ? string.Empty : value;
        }

        private static bool DrawPassengerNameWithTmpFont(Texture2D target, RectInt slot, string value, Color color)
        {
            var fontAsset = LoadPassengerNameTmpFont();
            if (fontAsset == null)
            {
                return false;
            }

            try
            {
                var renderedText = RenderTmpTextToTexture(fontAsset, value, slot.width, slot.height, color);
                if (renderedText == null)
                {
                    return false;
                }

                try
                {
                    if (!HasVisibleTextPixels(renderedText))
                    {
                        Debug.LogWarning("Franie TMP font rendered no visible pixels; falling back to dynamic font label text.");
                        return false;
                    }

                    CompositeTextTexture(target, renderedText, slot.x, slot.y, color);
                }
                finally
                {
                    UnityEngine.Object.Destroy(renderedText);
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Franie TMP font rendering failed; falling back to dynamic font label text. {exception.Message}");
                return false;
            }
        }

        private static TMP_FontAsset LoadPassengerNameTmpFont()
        {
#if UNITY_EDITOR
            var editorFont = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(PassengerNameTmpFontAssetPath);
            if (editorFont != null)
            {
                return editorFont;
            }
#else
            var resourceFont = Resources.Load<TMP_FontAsset>($"{KookyFontsDirectory}/Franie-SBold SDF");
            if (resourceFont != null)
            {
                return resourceFont;
            }
#endif
            return null;
        }

        private static Texture2D RenderTmpTextToTexture(TMP_FontAsset fontAsset, string value, int width, int height, Color color)
        {
            width = Mathf.Max(16, width);
            height = Mathf.Max(16, height);
            var previous = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Camera camera = null;
            GameObject textHost = null;
            Texture2D texture = null;
            try
            {
                renderTexture.Create();
                var layer = 30;
                var cameraHost = new GameObject("PhotoBoothPassengerNameCamera");
                camera = cameraHost.AddComponent<Camera>();
                cameraHost.transform.position = new Vector3(width * 0.5f, height * 0.5f, -10f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.aspect = width / (float)height;
                camera.cullingMask = 1 << layer;
                camera.targetTexture = renderTexture;

                textHost = new GameObject("PhotoBoothPassengerNameText", typeof(RectTransform), typeof(TextMeshPro));
                textHost.layer = layer;
                var rectTransform = textHost.GetComponent<RectTransform>();
                rectTransform.position = new Vector3(width * 0.5f, height * 0.5f, 0f);
                rectTransform.sizeDelta = new Vector2(width, height);

                var label = textHost.GetComponent<TextMeshPro>();
                label.font = fontAsset;
                label.text = value;
                label.color = color;
                label.alignment = TextAlignmentOptions.Center;
                label.enableWordWrapping = false;
                label.enableAutoSizing = true;
                label.fontSizeMin = 8f;
                label.fontSizeMax = height;
                label.rectTransform.sizeDelta = new Vector2(width, height);
                label.ForceMeshUpdate();
                SetLayerRecursively(textHost, layer);

                camera.Render();
                RenderTexture.active = renderTexture;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
                if (camera != null)
                {
                    UnityEngine.Object.Destroy(camera.gameObject);
                }

                if (textHost != null)
                {
                    UnityEngine.Object.Destroy(textHost);
                }
            }
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null)
            {
                return;
            }

            root.layer = layer;
            for (var i = 0; i < root.transform.childCount; i++)
            {
                SetLayerRecursively(root.transform.GetChild(i).gameObject, layer);
            }
        }

        private static void CompositeTextTexture(Texture2D target, Texture2D textTexture, int targetX, int targetY, Color color)
        {
            for (var y = 0; y < textTexture.height; y++)
            {
                var py = targetY + y;
                if (py < 0 || py >= target.height)
                {
                    continue;
                }

                for (var x = 0; x < textTexture.width; x++)
                {
                    var px = targetX + x;
                    if (px < 0 || px >= target.width)
                    {
                        continue;
                    }

                    var sample = textTexture.GetPixel(x, y);
                    var alpha = Mathf.Clamp01(sample.a);
                    if (alpha <= 0.02f)
                    {
                        continue;
                    }

                    target.SetPixel(px, py, Color.Lerp(target.GetPixel(px, py), color, alpha));
                }
            }
        }

        private static bool HasVisibleTextPixels(Texture2D textTexture)
        {
            if (textTexture == null)
            {
                return false;
            }

            var visiblePixels = 0;
            var pixels = textTexture.GetPixels32();
            for (var i = 0; i < pixels.Length; i++)
            {
                if (pixels[i].a > 12)
                {
                    visiblePixels += 1;
                    if (visiblePixels > 8)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool DrawPassengerNameWithFont(Texture2D target, RectInt slot, string value, Color color)
        {
            Font font = null;
            var shouldDestroyFont = false;
            try
            {
                font = LoadPassengerNameFont(out shouldDestroyFont);
                if (font == null)
                {
                    return false;
                }

                var fontSize = ResolvePassengerNameFontSize(font, value, slot);
                if (fontSize <= 0)
                {
                    return false;
                }

                font.RequestCharactersInTexture(value, fontSize, FontStyle.Normal);
                if (!MeasureFontText(font, value, fontSize, out var textWidth, out var minY, out var maxY))
                {
                    return false;
                }

                var startX = slot.x;
                var baselineY = slot.y + ((slot.height - (maxY - minY)) * 0.5f) - minY;
                var drawnPixels = DrawFontText(target, font, value, fontSize, startX, baselineY, color);
                if (drawnPixels <= 8)
                {
                    Debug.LogWarning("Franie font rendered no visible pixels; falling back to bitmap label text.");
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Franie font rendering failed; falling back to bitmap label text. {exception.Message}");
                return false;
            }
            finally
            {
                if (shouldDestroyFont && font != null)
                {
                    UnityEngine.Object.Destroy(font);
                }
            }
        }

        private static bool DrawPassengerNameWithUiFont(Texture2D target, RectInt slot, string value, Color color)
        {
            Font font = null;
            var shouldDestroyFont = false;
            try
            {
                font = LoadPassengerNameFont(out shouldDestroyFont);
                if (font == null)
                {
                    return false;
                }

                var renderedText = RenderUiTextToTexture(font, value, slot.width, slot.height, color);
                if (renderedText == null)
                {
                    return false;
                }

                try
                {
                    if (!HasVisibleTextPixels(renderedText))
                    {
                        Debug.LogWarning("Franie UI font rendered no visible pixels; falling back to bitmap label text.");
                        return false;
                    }

                    CompositeTextTexture(target, renderedText, slot.x, slot.y, color);
                    return true;
                }
                finally
                {
                    UnityEngine.Object.Destroy(renderedText);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Franie UI font rendering failed; falling back to bitmap label text. {exception.Message}");
                return false;
            }
            finally
            {
                if (shouldDestroyFont && font != null)
                {
                    UnityEngine.Object.Destroy(font);
                }
            }
        }

        private static Texture2D RenderUiTextToTexture(Font font, string value, int width, int height, Color color)
        {
            width = Mathf.Max(16, width);
            height = Mathf.Max(16, height);
            var previous = RenderTexture.active;
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            Camera camera = null;
            GameObject canvasHost = null;
            Texture2D texture = null;
            try
            {
                renderTexture.Create();
                var layer = 30;

                var cameraHost = new GameObject("PhotoBoothPassengerNameUiCamera");
                camera = cameraHost.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.clear;
                camera.orthographic = true;
                camera.orthographicSize = height * 0.5f;
                camera.aspect = width / (float)height;
                camera.cullingMask = 1 << layer;
                camera.targetTexture = renderTexture;
                camera.transform.position = new Vector3(width * 0.5f, height * 0.5f, -10f);

                canvasHost = new GameObject("PhotoBoothPassengerNameUiCanvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                SetLayerRecursively(canvasHost, layer);
                var canvasRect = canvasHost.GetComponent<RectTransform>();
                canvasRect.position = new Vector3(width * 0.5f, height * 0.5f, 0f);
                canvasRect.sizeDelta = new Vector2(width, height);

                var canvas = canvasHost.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                canvas.pixelPerfect = true;

                var scaler = canvasHost.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
                scaler.scaleFactor = 1f;

                var textHost = new GameObject("PhotoBoothPassengerNameUiText", typeof(RectTransform), typeof(Text));
                textHost.transform.SetParent(canvasHost.transform, false);
                SetLayerRecursively(textHost, layer);

                var textRect = textHost.GetComponent<RectTransform>();
                textRect.anchorMin = Vector2.zero;
                textRect.anchorMax = Vector2.one;
                textRect.pivot = new Vector2(0f, 0.5f);
                textRect.offsetMin = Vector2.zero;
                textRect.offsetMax = Vector2.zero;

                var label = textHost.GetComponent<Text>();
                label.font = font;
                label.text = value;
                label.color = color;
                label.alignment = TextAnchor.MiddleLeft;
                label.fontSize = Mathf.Min(PassengerNameFontSize, Mathf.Max(18, height));
                label.resizeTextForBestFit = false;
                label.horizontalOverflow = HorizontalWrapMode.Overflow;
                label.verticalOverflow = VerticalWrapMode.Overflow;
                label.raycastTarget = false;

                Canvas.ForceUpdateCanvases();
                camera.Render();

                RenderTexture.active = renderTexture;
                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                return texture;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
                if (camera != null)
                {
                    UnityEngine.Object.Destroy(camera.gameObject);
                }

                if (canvasHost != null)
                {
                    UnityEngine.Object.Destroy(canvasHost);
                }
            }
        }

        private static Font LoadPassengerNameFont(out bool shouldDestroy)
        {
            shouldDestroy = false;
#if UNITY_EDITOR
            var editorFont = AssetDatabase.LoadAssetAtPath<Font>(PassengerNameFontAssetPath);
            if (editorFont != null)
            {
                return editorFont;
            }
#endif
            var resourceFont = Resources.Load<Font>($"{KookyFontsDirectory}/Franie-SBold");
            if (resourceFont != null)
            {
                return resourceFont;
            }

            var fontPath = Path.Combine(Application.dataPath, PassengerNameFontPath);
            if (!File.Exists(fontPath))
            {
                return null;
            }

            shouldDestroy = true;
            return new Font(fontPath);
        }

        private static int ResolvePassengerNameFontSize(Font font, string value, RectInt slot)
        {
            var fontSize = Mathf.Max(18, slot.height);
            for (; fontSize >= 18; fontSize -= 2)
            {
                font.RequestCharactersInTexture(value, fontSize, FontStyle.Normal);
                if (!MeasureFontText(font, value, fontSize, out var textWidth, out var minY, out var maxY))
                {
                    continue;
                }

                if (textWidth <= slot.width && maxY - minY <= slot.height)
                {
                    return fontSize;
                }
            }

            return 0;
        }

        private static bool MeasureFontText(Font font, string value, int fontSize, out float textWidth, out float minY, out float maxY)
        {
            textWidth = 0f;
            minY = float.PositiveInfinity;
            maxY = float.NegativeInfinity;

            foreach (var character in value)
            {
                if (!font.GetCharacterInfo(character, out var info, fontSize, FontStyle.Normal))
                {
                    return false;
                }

                textWidth += info.advance;
                minY = Mathf.Min(minY, info.minY);
                maxY = Mathf.Max(maxY, info.maxY);
            }

            if (float.IsInfinity(minY) || float.IsInfinity(maxY))
            {
                minY = 0f;
                maxY = 0f;
            }

            return true;
        }

        private static int DrawFontText(Texture2D target, Font font, string value, int fontSize, float startX, float baselineY, Color color)
        {
            var atlas = font.material?.mainTexture as Texture2D;
            if (atlas == null)
            {
                return 0;
            }

            var drawnPixels = 0;
            var cursorX = startX;
            foreach (var character in value)
            {
                if (!font.GetCharacterInfo(character, out var info, fontSize, FontStyle.Normal))
                {
                    continue;
                }

                drawnPixels += DrawFontCharacter(target, atlas, info, cursorX, baselineY, color);
                cursorX += info.advance;
            }

            return drawnPixels;
        }

        private static int DrawFontCharacter(Texture2D target, Texture2D atlas, CharacterInfo info, float cursorX, float baselineY, Color color)
        {
            var width = Mathf.Max(1, info.maxX - info.minX);
            var height = Mathf.Max(1, info.maxY - info.minY);
            var minU = Mathf.Min(info.uvBottomLeft.x, info.uvBottomRight.x, info.uvTopLeft.x, info.uvTopRight.x);
            var maxU = Mathf.Max(info.uvBottomLeft.x, info.uvBottomRight.x, info.uvTopLeft.x, info.uvTopRight.x);
            var minV = Mathf.Min(info.uvBottomLeft.y, info.uvBottomRight.y, info.uvTopLeft.y, info.uvTopRight.y);
            var maxV = Mathf.Max(info.uvBottomLeft.y, info.uvBottomRight.y, info.uvTopLeft.y, info.uvTopRight.y);
            var drawnPixels = 0;

            for (var y = 0; y < height; y++)
            {
                var v = Mathf.Lerp(minV, maxV, height <= 1 ? 0f : y / (float)(height - 1));
                var targetY = Mathf.RoundToInt(baselineY + info.minY + y);
                if (targetY < 0 || targetY >= target.height)
                {
                    continue;
                }

                for (var x = 0; x < width; x++)
                {
                    var u = Mathf.Lerp(minU, maxU, width <= 1 ? 0f : x / (float)(width - 1));
                    var targetX = Mathf.RoundToInt(cursorX + info.minX + x);
                    if (targetX < 0 || targetX >= target.width)
                    {
                        continue;
                    }

                    var sample = atlas.GetPixelBilinear(u, v);
                    var alpha = Mathf.Clamp01(sample.a);
                    if (alpha <= 0.02f)
                    {
                        continue;
                    }

                    target.SetPixel(targetX, targetY, Color.Lerp(target.GetPixel(targetX, targetY), color, alpha));
                    drawnPixels += 1;
                }
            }

            return drawnPixels;
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
        public string PrintImagePath;
        public string ThumbnailPath;
    }
}
