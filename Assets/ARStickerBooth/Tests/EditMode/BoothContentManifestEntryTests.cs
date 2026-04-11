using NUnit.Framework;
using PhotoBooth.Booth.Content;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class BoothContentManifestEntryTests
    {
        [Test]
        public void Manifest_CanStorePrefabAndSceneEntries()
        {
            var manifest = new BoothContentManifest
            {
                Version = "1.2.0",
                Entries = new[]
                {
                    new BoothContentEntry
                    {
                        EntryId = "theme-kawaii",
                        EntryType = BoothContentEntryType.Prefab,
                        AssetPath = "Assets/Bundles/Kawaii.prefab"
                    },
                    new BoothContentEntry
                    {
                        EntryId = "scene-summer",
                        EntryType = BoothContentEntryType.Scene,
                        ScenePath = "Assets/Bundles/Summer.unity"
                    }
                }
            };

            Assert.That(manifest.Entries, Has.Length.EqualTo(2));
            Assert.That(manifest.Entries[0].EntryType, Is.EqualTo(BoothContentEntryType.Prefab));
            Assert.That(manifest.Entries[1].EntryType, Is.EqualTo(BoothContentEntryType.Scene));
            Assert.That(manifest.Entries[1].ScenePath, Is.EqualTo("Assets/Bundles/Summer.unity"));
        }
    }
}
