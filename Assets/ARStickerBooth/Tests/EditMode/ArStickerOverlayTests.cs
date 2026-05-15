using System;
using System.IO;
using NUnit.Framework;
using PhotoBooth.Booth.AR;
using PhotoBooth.Booth.Frontend;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Services;
using UnityEngine;
using UnityEngine.UI;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class ArStickerOverlayTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothArTests", Guid.NewGuid().ToString("N"));
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
        public void TransformResolver_MirrorsStickerCenterHorizontally()
        {
            var face = CreateFace(0.3f, 0.4f);
            var sticker = new ArStickerDefinition { anchor = ArStickerAnchor.Eyes };

            Assert.That(ArStickerTransformResolver.TryResolve(face, sticker, new Vector2Int(1000, 1000), false, out var normalRect, out _), Is.True);
            Assert.That(ArStickerTransformResolver.TryResolve(face, sticker, new Vector2Int(1000, 1000), true, out var mirroredRect, out _), Is.True);

            Assert.That(normalRect.center.x, Is.EqualTo(300f).Within(1f));
            Assert.That(mirroredRect.center.x, Is.EqualTo(700f).Within(1f));
        }

        [Test]
        public void TransformResolver_UsesReliableEyeLandmarksForEyeAnchoredSticker()
        {
            var face = CreateFace(0.5f, 0.5f);
            face.HasReliableEyeLandmarks = true;
            face.NormalizedLandmarks[33] = new Vector2(0.38f, 0.65f);
            face.NormalizedLandmarks[263] = new Vector2(0.62f, 0.65f);
            var sticker = new ArStickerDefinition { anchor = ArStickerAnchor.Eyes, builtinShape = ArStickerBuiltinShape.Nose };

            Assert.That(ArStickerTransformResolver.TryResolve(face, sticker, new Vector2Int(1000, 1000), false, out var rect, out _), Is.True);

            Assert.That(rect.center.x, Is.EqualTo(500f).Within(1f));
            Assert.That(rect.center.y, Is.EqualTo(350f).Within(1f));
        }

        [Test]
        public void TransformResolver_FallsBackToRaisedFaceGuideWhenEyesAreNotReliable()
        {
            var face = CreateFace(0.5f, 0.4f);
            face.HasReliableEyeLandmarks = false;
            face.NormalizedLandmarks[33] = new Vector2(0.48f, 0.32f);
            face.NormalizedLandmarks[263] = new Vector2(0.52f, 0.32f);
            var sticker = new ArStickerDefinition { anchor = ArStickerAnchor.Eyes, builtinShape = ArStickerBuiltinShape.Nose };

            Assert.That(ArStickerTransformResolver.TryResolve(face, sticker, new Vector2Int(1000, 1000), false, out var rect, out _), Is.True);

            var expectedGuideY = face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f);
            Assert.That(rect.center.x, Is.EqualTo(500f).Within(1f));
            Assert.That(rect.center.y, Is.EqualTo((1f - expectedGuideY) * 1000f).Within(1f));
        }

        [Test]
        public void Stabilizer_SmoothsAndPreservesFaceTransform()
        {
            var stabilizer = new ArTrackingStabilizer
            {
                PositionAlpha = 0.5f,
                RotationAlpha = 0.5f,
                ScaleAlpha = 0.5f
            };
            var first = CreateFace(0.5f, 0.5f);
            first.HasFaceTransform = true;
            first.FaceTransform = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, Vector3.one);
            var second = CreateFace(0.52f, 0.5f);
            second.HasFaceTransform = true;
            second.FaceTransform = Matrix4x4.TRS(new Vector3(2f, 0f, 0f), Quaternion.Euler(0f, 40f, 0f), Vector3.one * 1.2f);

            stabilizer.Update(new ArTrackingFrame { TimestampSeconds = 0.01d, Faces = new[] { first } });
            var smoothed = stabilizer.Update(new ArTrackingFrame { TimestampSeconds = 0.04d, Faces = new[] { second } });

            Assert.That(smoothed.Faces[0].HasFaceTransform, Is.True);
            Assert.That(smoothed.Faces[0].FaceTransform.m03, Is.GreaterThan(0f).And.LessThan(2f));
            Assert.That(smoothed.Faces[0].FaceEulerDegrees.y, Is.GreaterThan(0f).And.LessThan(40f));
        }

        [Test]
        public void StickerRenderer_BakesStickerForEveryFace()
        {
            var texture = SolidTexture(256, 256, Color.cyan);
            var stickerTexture = SolidTexture(32, 16, Color.black);
            var renderer = new ArStickerRenderer();
            try
            {
                var frame = new ArTrackingFrame
                {
                    PixelWidth = 256,
                    PixelHeight = 256,
                    Faces = new[] { CreateFace(0.32f, 0.42f), CreateFace(0.68f, 0.42f) }
                };

                var stickers = new[]
                {
                    new ArStickerDefinition
                    {
                        stickerId = "test_eye_overlay",
                        texture = stickerTexture,
                        anchor = ArStickerAnchor.Eyes,
                        sizeScale = Vector2.one,
                        tint = Color.white
                    }
                };

                renderer.ApplyToTexture(texture, frame, stickers, false);

                var darkPixels = 0;
                foreach (var pixel in texture.GetPixels32())
                {
                    if (pixel.r < 32 && pixel.g < 32 && pixel.b < 32 && pixel.a > 200)
                    {
                        darkPixels += 1;
                    }
                }

                Assert.That(darkPixels, Is.GreaterThan(1000));
            }
            finally
            {
                renderer.Dispose();
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(stickerTexture);
            }
        }

        [Test]
        public void Composer_BakesArStickerIntoComposedImage()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());
            sessions.Initialize();

            var job = sessions.CreateJob(12000, "THB");
            job = sessions.SelectTheme(job.JobId, "classic");
            job = sessions.BypassPayment(job.JobId);
            job = sessions.BeginCapture(job.JobId);

            var rawPath = Path.Combine(job.Paths.RawDirectory, "capture.png");
            WriteSolidPng(rawPath, 128, 128, Color.cyan);
            job = sessions.MarkCaptured(job.JobId, 1, rawPath);
            job = sessions.BeginComposing(job.JobId);

            var renderer = new ArStickerRenderer();
            var stickerTexture = SolidTexture(32, 16, Color.black);
            try
            {
                var frame = new ArTrackingFrame
                {
                    PixelWidth = 128,
                    PixelHeight = 128,
                    Faces = new[] { CreateFace(0.5f, 0.42f) }
                };
                var stickers = new[]
                {
                    new ArStickerDefinition
                    {
                        stickerId = "test_eye_overlay",
                        texture = stickerTexture,
                        anchor = ArStickerAnchor.Eyes,
                        sizeScale = Vector2.one,
                        tint = Color.white
                    }
                };

                var result = new BoothImageComposer().Compose(
                    job,
                    rawPath,
                    new Vector2Int(32, 32),
                    null,
                    texture => renderer.ApplyToTexture(texture, frame, stickers, false));

                var composed = LoadPng(result.ComposedImagePath);
                try
                {
                    Assert.That(ContainsDarkPixel(composed), Is.True);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(composed);
                }
            }
            finally
            {
                renderer.Dispose();
                UnityEngine.Object.DestroyImmediate(stickerTexture);
            }
        }

        private static FaceTrack CreateFace(float centerX, float centerY)
        {
            var landmarks = new Vector2[478];
            for (var i = 0; i < landmarks.Length; i++)
            {
                landmarks[i] = new Vector2(centerX, centerY);
            }

            landmarks[33] = new Vector2(centerX - 0.08f, centerY);
            landmarks[263] = new Vector2(centerX + 0.08f, centerY);
            landmarks[1] = new Vector2(centerX, centerY + 0.05f);
            landmarks[10] = new Vector2(centerX, centerY - 0.16f);
            landmarks[61] = new Vector2(centerX - 0.04f, centerY + 0.15f);
            landmarks[291] = new Vector2(centerX + 0.04f, centerY + 0.15f);

            return new FaceTrack
            {
                TrackId = $"face-{centerX:0.00}",
                NormalizedBounds = new Rect(centerX - 0.18f, centerY - 0.2f, 0.36f, 0.42f),
                NormalizedLandmarks = landmarks,
                Confidence = 1f
            };
        }

        private static Texture2D SolidTexture(int width, int height, Color color)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color[width * height];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void WriteSolidPng(string path, int width, int height, Color color)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var texture = SolidTexture(width, height, color);
            try
            {
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

        private static bool ContainsDarkPixel(Texture2D texture)
        {
            foreach (var pixel in texture.GetPixels32())
            {
                if (pixel.r < 32 && pixel.g < 32 && pixel.b < 32 && pixel.a > 200)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
