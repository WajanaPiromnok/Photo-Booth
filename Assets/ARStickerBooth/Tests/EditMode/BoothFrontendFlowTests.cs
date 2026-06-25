using System;
using System.IO;
using NUnit.Framework;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Frontend;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Services;
using UnityEngine;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothFrontendFlowTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothFrontendTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tempRootDirectory))
            {
                Directory.Delete(tempRootDirectory, true);
            }
        }

        [Test]
        public void MockPaymentFlow_ConfirmsPaymentBeforeCapture()
        {
            var sessions = CreateSessionService();
            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "classic");
            job = sessions.SetPassengerName(job.JobId, "PANYAWUTTICHAI");
            job = sessions.SelectAiStyle(job.JobId, "neon_ai", "Neon cyberpunk portrait");
            job = sessions.SetPaymentPending(job.JobId, $"MOCK-{job.JobId}");
            job = sessions.ConfirmPayment(job.JobId, $"MOCK-{job.JobId}");

            Assert.That(job.Status, Is.EqualTo(BoothJobStatus.PaymentConfirmed));
            Assert.That(job.PaymentStatus, Is.EqualTo(BoothPaymentStatus.Confirmed));
            Assert.That(job.AiStyleId, Is.EqualTo("neon_ai"));
            Assert.That(job.AiStylePrompt, Is.EqualTo("Neon cyberpunk portrait"));
            Assert.That(job.PassengerName, Is.EqualTo("PANYAWUTTICHAI"));
            Assert.That(job.PaymentReference, Is.EqualTo($"MOCK-{job.JobId}"));
        }

        [Test]
        public void Composer_CreatesComposedImageAndThumbnail()
        {
            var sessions = CreateSessionService();
            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "classic");
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);

            var rawPath = Path.Combine(job.Paths.RawDirectory, "capture.png");
            WriteTestPng(rawPath, 64, 48);
            var motionFrames = new[] { rawPath };
            job = sessions.MarkCaptured(job.JobId, 1, rawPath, motionFrames);
            job = sessions.BeginComposing(job.JobId);

            var result = new BoothImageComposer().Compose(
                job,
                rawPath,
                new Vector2Int(32, 24),
                new BoothAiStyleOption
                {
                    styleId = "neon_ai",
                    displayName = "Neon AI",
                    aiPrompt = "Neon cyberpunk portrait",
                    primaryColor = Color.cyan,
                    secondaryColor = Color.magenta,
                    applyLocalStylizedPreview = true
                });
            job = sessions.MarkComposed(job.JobId, result.ComposedImagePath, result.ThumbnailPath);

            Assert.That(job.Status, Is.EqualTo(BoothJobStatus.Composed));
            Assert.That(job.Paths.RawImagePath, Is.EqualTo(rawPath));
            Assert.That(job.MotionClipFramePaths, Is.EqualTo(motionFrames));
            Assert.That(File.Exists(job.Paths.ComposedImagePath), Is.True);
            Assert.That(File.Exists(job.Paths.ThumbnailPath), Is.True);
        }

        [Test]
        public void Composer_FirstKookyPhotoTemplateUsesThreeCapturedImages()
        {
            var result = ComposeKookyTemplate("theme_01", "image_preview_1", out var composed, out var print);
            try
            {
                Assert.That(File.Exists(result.ComposedImagePath), Is.True);
                Assert.That(File.Exists(result.PrintImagePath), Is.True);
                Assert.That(File.Exists(result.ThumbnailPath), Is.True);
                AssertKookyThreePhotoLayout(composed, print);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(composed);
                UnityEngine.Object.DestroyImmediate(print);
            }
        }

        [Test]
        public void Composer_SecondKookyPhotoTemplateUsesThreeCapturedImages()
        {
            var result = ComposeKookyTemplate("theme_02", "image_preview_2", out var composed, out var print);
            try
            {
                Assert.That(File.Exists(result.ComposedImagePath), Is.True);
                Assert.That(File.Exists(result.PrintImagePath), Is.True);
                Assert.That(File.Exists(result.ThumbnailPath), Is.True);
                AssertKookyThreePhotoLayout(composed, print);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(composed);
                UnityEngine.Object.DestroyImmediate(print);
            }
        }

        [Test]
        public void Capture_PersistsAllThreeRawImagePaths()
        {
            var sessions = CreateSessionService();
            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "classic");
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);
            var rawPaths = new[]
            {
                Path.Combine(job.Paths.RawDirectory, "capture_01.png"),
                Path.Combine(job.Paths.RawDirectory, "capture_02.png"),
                Path.Combine(job.Paths.RawDirectory, "capture_03.png")
            };

            job = sessions.MarkCaptured(job.JobId, 3, rawPaths[2], rawPaths, Array.Empty<string>(), null);
            var restored = sessions.GetJob(job.JobId);

            Assert.That(restored.RawCaptureCount, Is.EqualTo(3));
            Assert.That(restored.Paths.RawImagePath, Is.EqualTo(rawPaths[2]));
            Assert.That(restored.Paths.RawImagePaths, Is.EqualTo(rawPaths));
        }

        [Test]
        public void BeginRetake_ResetsCaptureArtifactsBeforeUpload()
        {
            var sessions = CreateSessionService();
            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "classic");
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);

            var rawPath = Path.Combine(job.Paths.RawDirectory, "capture.png");
            WriteTestPng(rawPath, 64, 48);
            job = sessions.MarkCaptured(job.JobId, 1, rawPath, new[] { rawPath }, Array.Empty<string>(), null);
            job = sessions.BeginComposing(job.JobId);
            var result = new BoothImageComposer().Compose(job, rawPath, new Vector2Int(32, 24));
            job = sessions.MarkComposed(job.JobId, result.ComposedImagePath, result.ThumbnailPath);

            var retakeJob = sessions.BeginRetake(job.JobId);

            Assert.That(retakeJob.Status, Is.EqualTo(BoothJobStatus.Capturing));
            Assert.That(retakeJob.RawCaptureCount, Is.EqualTo(0));
            Assert.That(retakeJob.Paths.RawImagePath, Is.Null);
            Assert.That(retakeJob.Paths.RawImagePaths, Is.Null);
            Assert.That(retakeJob.Paths.ComposedImagePath, Is.Null);
            Assert.That(retakeJob.Paths.ThumbnailPath, Is.Null);
            Assert.That(retakeJob.MotionClipFramePaths, Is.Null);
            Assert.That(retakeJob.MotionVideoPath, Is.Null);
            Assert.That(retakeJob.MotionVideoUrl, Is.Null);
        }

        private BoothCompositionResult ComposeKookyTemplate(string themeId, string imagePreviewId, out Texture2D composed, out Texture2D print)
        {
            var sessions = CreateSessionService();
            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, themeId);
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);

            var rawPaths = new[]
            {
                Path.Combine(job.Paths.RawDirectory, "capture_01.png"),
                Path.Combine(job.Paths.RawDirectory, "capture_02.png"),
                Path.Combine(job.Paths.RawDirectory, "capture_03.png")
            };

            WriteTestPng(rawPaths[0], 64, 48, Color.red);
            WriteTestPng(rawPaths[1], 64, 48, Color.green);
            WriteTestPng(rawPaths[2], 64, 48, Color.blue);
            job = sessions.MarkCaptured(job.JobId, 3, rawPaths[2], rawPaths);
            job = sessions.BeginComposing(job.JobId);

            var result = new BoothImageComposer().ComposePhotoGrid(
                job,
                rawPaths,
                new Vector2Int(32, 24),
                new BoothThemeOption { themeId = themeId, backendFrameId = themeId, imagePreviewId = imagePreviewId });

            composed = LoadPng(result.ComposedImagePath);
            print = LoadPng(result.PrintImagePath);
            return result;
        }

        private static void AssertKookyThreePhotoLayout(Texture2D composed, Texture2D print)
        {
            Assert.That(composed.width, Is.EqualTo(1800));
            Assert.That(composed.height, Is.EqualTo(1200));
            Assert.That(composed.GetPixel(368, 375).r, Is.GreaterThan(0.8f));
            Assert.That(composed.GetPixel(897, 375).g, Is.GreaterThan(0.4f));
            Assert.That(composed.GetPixel(1426, 375).b, Is.GreaterThan(0.8f));
            Assert.That(print.GetPixel(368, 375), Is.EqualTo(composed.GetPixel(368, 375)));
        }

        private BoothSessionService CreateSessionService()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());
            sessions.Initialize();
            return sessions;
        }

        private static void WriteTestPng(string path, int width, int height)
        {
            WriteTestPng(path, width, height, Color.cyan);
        }

        private static void WriteTestPng(string path, int width, int height, Color color)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = color;
                }

                texture.SetPixels(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(path, ImageConversion.EncodeToPNG(texture));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static Texture2D LoadPng(string path)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(texture, File.ReadAllBytes(path));
            return texture;
        }
    }
}
