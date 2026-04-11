using System;
using System.IO;
using NUnit.Framework;
using PhotoBooth.Booth.Sync;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothChecksumUtilityTests
    {
        private string tempFilePath;

        [SetUp]
        public void SetUp()
        {
            tempFilePath = Path.Combine(Path.GetTempPath(), "PhotoBoothChecksumTests", Guid.NewGuid().ToString("N") + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(tempFilePath));
            File.WriteAllText(tempFilePath, "photo-booth");
        }

        [TearDown]
        public void TearDown()
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }

        [Test]
        public void ComputeSha256Tag_ReturnsTaggedLowercaseHash()
        {
            var hash = BoothChecksumUtility.ComputeSha256Tag(tempFilePath);

            Assert.That(hash, Does.StartWith("sha256:"));
            Assert.That(hash, Is.EqualTo(hash.ToLowerInvariant()));
            Assert.That(hash.Length, Is.EqualTo("sha256:".Length + 64));
        }
    }
}
