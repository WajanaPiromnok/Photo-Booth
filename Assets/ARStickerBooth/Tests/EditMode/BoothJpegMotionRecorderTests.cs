using System;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using PhotoBooth.Booth.Frontend;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothJpegMotionRecorderTests
    {
        private string tempDirectory;

        [SetUp]
        public void SetUp()
        {
            tempDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothMotionTests", Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, true);
            }
        }

        [Test]
        public async Task CompleteSegment_ResamplesSparseFramesToFixedFrameCount()
        {
            var recorder = new BoothJpegMotionRecorder();
            recorder.BeginSegment(4, 1);
            var now = BoothJpegMotionRecorder.NowSeconds;
            recorder.AcceptFrame(new CanonPreviewFrame { JpegBytes = new byte[] { 1, 2, 3 }, TimestampSeconds = now });
            recorder.AcceptFrame(new CanonPreviewFrame { JpegBytes = new byte[] { 4, 5, 6 }, TimestampSeconds = now + 0.7d });

            var result = recorder.CompleteSegment(tempDirectory, 7);
            await result.WriteTask;

            Assert.That(result.FramePaths, Has.Length.EqualTo(4));
            Assert.That(Path.GetFileName(result.FramePaths[0]), Is.EqualTo("motion_007.jpg"));
            Assert.That(Path.GetFileName(result.FramePaths[3]), Is.EqualTo("motion_010.jpg"));
            Assert.That(result.FramePaths, Has.All.Matches<string>(File.Exists));
        }

        [Test]
        public void CompleteSegment_WithoutAnyPreviewFrameThrows()
        {
            var recorder = new BoothJpegMotionRecorder();
            recorder.BeginSegment(15, 1);

            Assert.Throws<InvalidOperationException>(() => recorder.CompleteSegment(tempDirectory, 0));
        }
    }
}
