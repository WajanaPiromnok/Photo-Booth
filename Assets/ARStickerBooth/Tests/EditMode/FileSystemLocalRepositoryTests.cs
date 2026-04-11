using System;
using System.IO;
using NUnit.Framework;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Services;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class FileSystemLocalRepositoryTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothTests", Guid.NewGuid().ToString("N"));
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
        public void CreateAndUpdateJob_PersistsSnapshotAndDirectories()
        {
            var repository = new FileSystemLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());

            sessions.Initialize();
            var created = sessions.CreateJob(12000, "THB");
            var themed = sessions.SelectTheme(created.JobId, "kawaii_01");

            Assert.That(File.Exists(themed.Paths.SnapshotPath), Is.True);
            Assert.That(Directory.Exists(themed.Paths.RawDirectory), Is.True);
            Assert.That(Directory.Exists(themed.Paths.ComposedDirectory), Is.True);
            Assert.That(Directory.Exists(themed.Paths.ThumbsDirectory), Is.True);
            Assert.That(Directory.Exists(themed.Paths.LogsDirectory), Is.True);

            var reloaded = repository.Get(created.JobId);
            Assert.That(reloaded.JobId, Is.EqualTo(created.JobId));
            Assert.That(reloaded.ThemeId, Is.EqualTo("kawaii_01"));
            Assert.That(reloaded.AmountMinorUnits, Is.EqualTo(12000));
            Assert.That(reloaded.CurrencyCode, Is.EqualTo("THB"));
        }
    }
}
