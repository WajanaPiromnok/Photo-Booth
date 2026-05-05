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
        public void TransformResolver_UsesReliableEyeLandmarksForSunglasses()
        {
            var face = CreateFace(0.5f, 0.5f);
            face.HasReliableEyeLandmarks = true;
            face.NormalizedLandmarks[33] = new Vector2(0.38f, 0.65f);
            face.NormalizedLandmarks[263] = new Vector2(0.62f, 0.65f);
            var sticker = new ArStickerDefinition { anchor = ArStickerAnchor.Eyes, builtinShape = ArStickerBuiltinShape.Sunglasses };

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
            var sticker = new ArStickerDefinition { anchor = ArStickerAnchor.Eyes, builtinShape = ArStickerBuiltinShape.Sunglasses };

            Assert.That(ArStickerTransformResolver.TryResolve(face, sticker, new Vector2Int(1000, 1000), false, out var rect, out _), Is.True);

            var expectedGuideY = face.NormalizedBounds.yMin + (face.NormalizedBounds.height * 0.74f);
            Assert.That(rect.center.x, Is.EqualTo(500f).Within(1f));
            Assert.That(rect.center.y, Is.EqualTo((1f - expectedGuideY) * 1000f).Within(1f));
        }

        [Test]
        public void FaceModelRig_NormalizedMappingMatchesDebugOverlaySpace()
        {
            var overlayRect = new Rect(-430f, -430f, 860f, 860f);
            var mapped = ArPreviewFaceModelRig.NormalizedToOverlayPosition(new Vector2(0.25f, 0.75f), overlayRect);

            Assert.That(mapped.x, Is.EqualTo(-215f).Within(0.01f));
            Assert.That(mapped.y, Is.EqualTo(215f).Within(0.01f));
        }

        [Test]
        public void FaceModelRig_ScreenOffsetTracksEyeDistance()
        {
            var adjusted = ArPreviewFaceModelRig.ApplyFaceMaskScreenOffset(
                new Vector2(0.5f, 0.45f),
                new Vector2(0.1f, 0.45f),
                0.2f);

            Assert.That(adjusted.x, Is.EqualTo(0.52f).Within(0.001f));
            Assert.That(adjusted.y, Is.EqualTo(0.54f).Within(0.001f));
        }

        [Test]
        public void FaceModelRig_UsesOverlayRectSizeForLivePreviewRenderTexture()
        {
            var overlayHost = new GameObject("Overlay", typeof(RectTransform), typeof(RawImage));
            var rigHost = new GameObject("Rig", typeof(ArPreviewFaceModelRig));
            try
            {
                var overlayRect = overlayHost.GetComponent<RectTransform>();
                overlayRect.sizeDelta = new Vector2(860f, 860f);
                var overlay = overlayHost.GetComponent<RawImage>();
                var rig = rigHost.GetComponent<ArPreviewFaceModelRig>();
                rig.ShowGuideModel = false;
                rig.Initialize(overlay);

                rig.ApplyFace(CreateFace(0.5f, 0.5f), 1280, 720);

                var renderTexture = overlay.texture as RenderTexture;
                Assert.That(renderTexture, Is.Not.Null);
                Assert.That(renderTexture.width, Is.EqualTo(860));
                Assert.That(renderTexture.height, Is.EqualTo(860));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(rigHost);
                UnityEngine.Object.DestroyImmediate(overlayHost);
            }
        }

        [Test]
        public void FaceModelRig_CanonicalModeFallsBackWithoutFaceTransform()
        {
            var face = CreateFace(0.5f, 0.5f);
            face.HasFaceTransform = false;

            var effectiveMode = ArPreviewFaceModelRig.ResolveEffectiveAlignmentMode(
                Tracked3dFaceModelAlignmentMode.CanonicalMatrix,
                face);

            Assert.That(effectiveMode, Is.EqualTo(Tracked3dFaceModelAlignmentMode.LandmarkAnchored));
        }

        [Test]
        public void FaceModelRig_FacePartAnchorsResolveToExpectedRigAnchors()
        {
            Assert.That(ArPreviewFaceModelRig.ResolveFacePartAnchorName(Tracked3dFacePartAnchor.Eyes), Is.EqualTo("Eyes"));
            Assert.That(ArPreviewFaceModelRig.ResolveFacePartAnchorName(Tracked3dFacePartAnchor.Nose), Is.EqualTo("Nose"));
            Assert.That(ArPreviewFaceModelRig.ResolveFacePartAnchorName(Tracked3dFacePartAnchor.Mouth), Is.EqualTo("Mouth"));
            Assert.That(ArPreviewFaceModelRig.ResolveFacePartAnchorName(Tracked3dFacePartAnchor.Forehead), Is.EqualTo("Forehead"));
            Assert.That(ArPreviewFaceModelRig.ResolveFacePartAnchorName(Tracked3dFacePartAnchor.Chin), Is.EqualTo("Chin"));
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
        public void FaceModelRig_AlphaBlendOverlayKeepsTargetSize()
        {
            var target = SolidTexture(2, 1, Color.blue);
            try
            {
                var overlay = new[]
                {
                    new Color32(255, 0, 0, 128),
                    new Color32(0, 0, 0, 0)
                };

                ArPreviewFaceModelRig.AlphaBlendOverlay(target, overlay);
                var pixels = target.GetPixels32();

                Assert.That(target.width, Is.EqualTo(2));
                Assert.That(target.height, Is.EqualTo(1));
                Assert.That(pixels[0].r, Is.GreaterThan(100));
                Assert.That(pixels[0].b, Is.GreaterThan(100));
                Assert.That(pixels[1].b, Is.EqualTo(255));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void StickerRenderer_BakesStickerForEveryFace()
        {
            var texture = SolidTexture(256, 256, Color.cyan);
            var renderer = new ArStickerRenderer();
            try
            {
                var frame = new ArTrackingFrame
                {
                    PixelWidth = 256,
                    PixelHeight = 256,
                    Faces = new[] { CreateFace(0.32f, 0.42f), CreateFace(0.68f, 0.42f) }
                };

                renderer.ApplyToTexture(texture, frame, ArStickerRenderer.CreateDefaultStickers(), false);

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
            try
            {
                var frame = new ArTrackingFrame
                {
                    PixelWidth = 128,
                    PixelHeight = 128,
                    Faces = new[] { CreateFace(0.5f, 0.42f) }
                };

                var result = new BoothImageComposer().Compose(
                    job,
                    rawPath,
                    new Vector2Int(32, 32),
                    null,
                    texture => renderer.ApplyToTexture(texture, frame, ArStickerRenderer.CreateDefaultStickers(), false));

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
