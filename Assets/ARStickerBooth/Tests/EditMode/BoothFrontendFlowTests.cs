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
        public void BeginRetake_ResetsCaptureArtifactsBeforeUpload()
        {
            var sessions = CreateSessionService();
            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "classic");
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);

            var rawPath = Path.Combine(job.Paths.RawDirectory, "capture.png");
            WriteTestPng(rawPath, 64, 48);
            job = sessions.MarkCaptured(job.JobId, 1, rawPath);
            job = sessions.BeginComposing(job.JobId);
            var result = new BoothImageComposer().Compose(job, rawPath, new Vector2Int(32, 24));
            job = sessions.MarkComposed(job.JobId, result.ComposedImagePath, result.ThumbnailPath);

            var retakeJob = sessions.BeginRetake(job.JobId);

            Assert.That(retakeJob.Status, Is.EqualTo(BoothJobStatus.Capturing));
            Assert.That(retakeJob.RawCaptureCount, Is.EqualTo(0));
            Assert.That(retakeJob.Paths.RawImagePath, Is.Null);
            Assert.That(retakeJob.Paths.ComposedImagePath, Is.Null);
            Assert.That(retakeJob.Paths.ThumbnailPath, Is.Null);
            Assert.That(retakeJob.MotionClipFramePaths, Is.Null);
            Assert.That(retakeJob.MotionVideoPath, Is.Null);
            Assert.That(retakeJob.MotionVideoUrl, Is.Null);
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
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var i = 0; i < pixels.Length; i++)
                {
                    pixels[i] = Color.cyan;
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
    }
}
