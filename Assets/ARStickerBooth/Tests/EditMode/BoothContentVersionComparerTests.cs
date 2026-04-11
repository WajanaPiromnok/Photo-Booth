using NUnit.Framework;
using PhotoBooth.Booth.Content;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothContentVersionComparerTests
    {
        [Test]
        public void Compare_ReturnsPositive_WhenLeftVersionIsNewer()
        {
            var result = BoothContentVersionComparer.Compare("1.10.0", "1.2.9");

            Assert.That(result, Is.GreaterThan(0));
        }

        [Test]
        public void Compare_ReturnsZero_WhenVersionsMatchWithPrefix()
        {
            var result = BoothContentVersionComparer.Compare("v2.0.0", "2.0.0");

            Assert.That(result, Is.Zero);
        }

        [Test]
        public void Compare_ReturnsNegative_WhenLeftVersionIsOlder()
        {
            var result = BoothContentVersionComparer.Compare("1.0.0", "1.0.1");

            Assert.That(result, Is.LessThan(0));
        }
    }
}
