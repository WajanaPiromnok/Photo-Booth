using NUnit.Framework;
using PhotoBooth.Booth.Frontend;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothCameraCaptureServiceTests
    {
        [Test]
        public void SelectPreferredDeviceName_PicksObsbothVirtualCameraBeforeGenericDevices()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "OBSBOT Meet 2",
                "OBSBOT Virtual Camera"
            });

            Assert.That(selected, Is.EqualTo("OBSBOT Virtual Camera"));
        }

        [Test]
        public void SelectPreferredDeviceName_FallsBackToObsbothDeviceBeforeFirstDevice()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "OBSBOT Meet 2"
            });

            Assert.That(selected, Is.EqualTo("OBSBOT Meet 2"));
        }

        [Test]
        public void SelectPreferredDeviceName_FallsBackToFirstDeviceWhenNoPreferredDeviceExists()
        {
            var selected = BoothCameraCaptureService.SelectPreferredDeviceName(new[]
            {
                "FaceTime HD Camera",
                "USB Camera"
            });

            Assert.That(selected, Is.EqualTo("FaceTime HD Camera"));
        }
    }
}
