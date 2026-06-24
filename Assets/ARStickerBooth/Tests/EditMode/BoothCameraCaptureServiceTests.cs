using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using PhotoBooth.Booth.Frontend;
using UnityEngine;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothCameraCaptureServiceTests
    {
        [Test]
        public async Task CanonBackend_StartIsIdempotentAndStopClosesSession()
        {
            var backend = new FakeCanonCameraBackend(CreateTestJpeg());
            using var service = new BoothCameraCaptureService(
                null,
                canonCameraBackend: backend,
                useCanonEdsdk: true,
                allowCameraFallback: false);

            await service.StartPreviewAsync();
            await service.StartPreviewAsync();

            Assert.That(service.IsUsingCanonEdsdk, Is.True);
            Assert.That(backend.StartCount, Is.EqualTo(1));

            await service.StopPreviewAsync();

            Assert.That(backend.StopCount, Is.EqualTo(1));
            Assert.That(backend.CloseCount, Is.EqualTo(1));
        }

        [Test]
        public async Task CanonBackend_CaptureUsesRequestedFileName()
        {
            var backend = new FakeCanonCameraBackend(CreateTestJpeg());
            using var service = new BoothCameraCaptureService(
                null,
                canonCameraBackend: backend,
                useCanonEdsdk: true,
                allowCameraFallback: false);

            await service.StartPreviewAsync();
            var path = await service.CaptureCanonStillAsync("captures", "capture_03.jpg");

            Assert.That(path, Is.EqualTo(Path.Combine("captures", "capture_03.jpg")));
            Assert.That(backend.CaptureCount, Is.EqualTo(1));
        }

        [Test]
        public void SelectPreferredDeviceName_DefaultsToExternalCameraBeforeBuiltInAndObsbot()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "OBSBOT Meet 2",
                "USB Camera"
            });

            Assert.That(selected, Is.EqualTo("USB Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_DefaultDoesNotPrioritizeObsbot()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBSBOT Virtual Camera",
                "FaceTime HD Camera",
                "External Capture Card"
            });

            Assert.That(selected, Is.EqualTo("External Capture Card"));
        }

        [Test]
        public void SelectPreferredDeviceName_PrefersFirstExternalCameraLikeCanonBeforeObsbot()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBSBOT Virtual Camera",
                "Canon EOS 77D",
                "FaceTime HD Camera"
            });

            Assert.That(selected, Is.EqualTo("Canon EOS 77D"));
        }

        [Test]
        public void SelectPreferredDeviceName_PrefersEosWebcamBeforeObsbotAndBuiltInCamera()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBSBOT Virtual Camera",
                "FaceTime HD Camera",
                "EOS Webcam Utility"
            });

            Assert.That(selected, Is.EqualTo("EOS Webcam Utility"));
        }

        [Test]
        public void IsCanonLikeCameraDeviceName_MatchesCanonVirtualCameraNames()
        {
            Assert.That(BoothCameraCaptureService.IsCanonLikeCameraDeviceName("EOS Webcam Utility"), Is.True);
            Assert.That(BoothCameraCaptureService.IsCanonLikeCameraDeviceName("Canon EOS 77D"), Is.True);
            Assert.That(BoothCameraCaptureService.IsCanonLikeCameraDeviceName("Cam Link 4K"), Is.True);
            Assert.That(BoothCameraCaptureService.IsCanonLikeCameraDeviceName("OBSBOT Virtual Camera"), Is.False);
        }

        [Test]
        public void IsCaptureCardLikeCameraDeviceName_MatchesAcasisNames()
        {
            Assert.That(BoothCameraCaptureService.IsCaptureCardLikeCameraDeviceName("Acasis HD33"), Is.True);
            Assert.That(BoothCameraCaptureService.IsCaptureCardLikeCameraDeviceName("USB Video"), Is.True);
            Assert.That(BoothCameraCaptureService.IsCaptureCardLikeCameraDeviceName("HDMI Capture"), Is.True);
            Assert.That(BoothCameraCaptureService.IsCaptureCardLikeCameraDeviceName("FaceTime HD Camera"), Is.False);
            Assert.That(BoothCameraCaptureService.IsHd33DeviceName("HD33 video"), Is.True);
        }

        [Test]
        public void SelectPreferredDeviceName_PrefersAcasisBeforeObsbotAndBuiltInCamera()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBSBOT Virtual Camera",
                "FaceTime HD Camera",
                "Acasis HD33"
            });

            Assert.That(selected, Is.EqualTo("Acasis HD33"));
        }

        [Test]
        public void SelectPreferredDeviceName_DefaultDoesNotPrioritizeObsVirtualCamera()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBS Virtual Camera",
                "FaceTime HD Camera",
                "USB Camera"
            });

            Assert.That(selected, Is.EqualTo("USB Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_UsesObsbotWhenNoOtherCameraExists()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBSBOT Virtual Camera",
                "OBSBOT Meet 2"
            });

            Assert.That(selected, Is.EqualTo("OBSBOT Virtual Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_FallsBackToFirstDeviceWhenNoPreferredDeviceExists()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "USB Camera",
                "External Capture Card"
            });

            Assert.That(selected, Is.EqualTo("USB Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_UsesObsbotBeforeBuiltInCameraWhenOnlyThoseExist()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "OBSBOT Virtual Camera"
            });

            Assert.That(selected, Is.EqualTo("OBSBOT Virtual Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_UsesBuiltInCameraOnlyAsLastFallback()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera"
            });

            Assert.That(selected, Is.EqualTo("FaceTime HD Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_UsesBuiltInBeforeObsVirtualCamera()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "OBS Virtual Camera",
                "FaceTime HD Camera"
            });

            Assert.That(selected, Is.EqualTo("FaceTime HD Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_UsesCustomPreferredNames()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(
                new[]
                {
                    "FaceTime HD Camera",
                    "External Capture Card"
                },
                new[]
                {
                    "External Capture"
                });

            Assert.That(selected, Is.EqualTo("External Capture Card"));
        }

        [Test]
        public void SelectPreferredDeviceName_ReturnsNullWhenNoDevicesExist()
        {
            Assert.That(BoothCameraCaptureService.SelectPreferredDeviceName(null), Is.Null);
            Assert.That(BoothCameraCaptureService.SelectPreferredDeviceName(new[] { "", " " }), Is.Null);
        }

        [Test]
        public void SelectPrimaryPreferredDeviceName_DoesNotWaitWhenNoPreferredNamesConfigured()
        {
            var primarySelected = BoothCameraCaptureService.SelectPrimaryPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "OBSBOT Meet 2"
            });
            var fallbackSelected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "USB Camera"
            });

            Assert.That(primarySelected, Is.Null);
            Assert.That(fallbackSelected, Is.EqualTo("USB Camera"));
        }

        [Test]
        public void SelectPrimaryPreferredDeviceName_UsesCustomPreferredNameWhenConfigured()
        {
            var selected = BoothCameraCaptureService.SelectPrimaryPreferredDeviceName(
                new[]
                {
                    "FaceTime HD Camera",
                    "External Capture Card"
                },
                new[]
                {
                    "External Capture"
                });

            Assert.That(selected, Is.EqualTo("External Capture Card"));
        }

        [Test]
        public void DescribeDeviceSelection_UsesGenericCameraSource()
        {
            Assert.That(BoothCameraCaptureService.DescribeDeviceSelection("OBSBOT Virtual Camera"), Is.EqualTo("camera"));
            Assert.That(BoothCameraCaptureService.DescribeDeviceSelection("OBSBOT Meet 2"), Is.EqualTo("camera"));
            Assert.That(BoothCameraCaptureService.DescribeDeviceSelection("FaceTime HD Camera"), Is.EqualTo("camera"));
            Assert.That(BoothCameraCaptureService.DescribeDeviceSelection(null), Is.EqualTo("none"));
        }

        private static byte[] CreateTestJpeg()
        {
            var texture = new Texture2D(32, 24, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixel(0, 0, Color.cyan);
                texture.Apply();
                return ImageConversion.EncodeToJPG(texture);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private sealed class FakeCanonCameraBackend : ICanonCameraBackend
        {
            private readonly byte[] previewBytes;

            public FakeCanonCameraBackend(byte[] previewBytes)
            {
                this.previewBytes = previewBytes;
            }

            public bool IsSessionOpen { get; private set; }
            public bool IsLiveViewRunning { get; private set; }
            public string CameraName => "Fake Canon";
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }
            public int CloseCount { get; private set; }
            public int CaptureCount { get; private set; }
            public int AutoFocusCount { get; private set; }


            public Task StartLiveViewAsync(CancellationToken cancellationToken = default)
            {
                StartCount++;
                IsSessionOpen = true;
                IsLiveViewRunning = true;
                return Task.CompletedTask;
            }

            public Task AutoFocusAsync(CancellationToken cancellationToken = default)
            {
                AutoFocusCount++;
                IsSessionOpen = true;
                return Task.CompletedTask;
            }

            public Task<byte[]> DownloadLiveViewFrameAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(previewBytes);
            }

            public Task<string> CaptureStillAsync(string outputDirectory, string fileName, CancellationToken cancellationToken = default)
            {
                CaptureCount++;
                return Task.FromResult(Path.Combine(outputDirectory, fileName));
            }

            public Task StopLiveViewAsync(CancellationToken cancellationToken = default)
            {
                StopCount++;
                IsLiveViewRunning = false;
                return Task.CompletedTask;
            }

            public Task CloseSessionAsync(CancellationToken cancellationToken = default)
            {
                CloseCount++;
                IsSessionOpen = false;
                return Task.CompletedTask;
            }

            public void Dispose()
            {
                IsSessionOpen = false;
                IsLiveViewRunning = false;
            }
        }
    }
}
