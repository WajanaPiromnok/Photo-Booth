using NUnit.Framework;
using PhotoBooth.Booth.Sync;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothBackendPathResolverTests
    {
        [Test]
        public void Resolve_ReplacesJobIdAndNormalizesSlashes()
        {
            var url = BoothBackendPathResolver.Resolve("https://api.example.com/", "v1/jobs/{jobId}/publish", "JOB-001");

            Assert.That(url, Is.EqualTo("https://api.example.com/v1/jobs/JOB-001/publish"));
        }

        [Test]
        public void Resolve_EncodesUnsafeJobIdCharacters()
        {
            var url = BoothBackendPathResolver.Resolve("https://api.example.com", "/v1/jobs/{jobId}/assets", "JOB 001/A");

            Assert.That(url, Is.EqualTo("https://api.example.com/v1/jobs/JOB%20001%2FA/assets"));
        }
    }
}
