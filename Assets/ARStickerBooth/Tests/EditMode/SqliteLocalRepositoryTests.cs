using System;
using System.IO;
using NUnit.Framework;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Services;

namespace PhotoBooth.Booth.Tests.EditMode
{
    public sealed class SqliteLocalRepositoryTests
    {
        private string tempRootDirectory;

        [SetUp]
        public void SetUp()
        {
            tempRootDirectory = Path.Combine(Path.GetTempPath(), "PhotoBoothSqliteTests", Guid.NewGuid().ToString("N"));
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
        public void CreateAndUpdateJob_PersistsToSqliteAndMirrorsSnapshot()
        {
            var repository = new SqliteLocalRepository(tempRootDirectory);
            var sessions = new BoothSessionService(repository, new BoothStateMachine());

            sessions.Initialize();
            var created = sessions.CreateJob(12000, "THB");
            var themed = sessions.SelectTheme(created.JobId, "kawaii_01");

            Assert.That(File.Exists(Path.Combine(tempRootDirectory, "booth-jobs.db")), Is.True);
            Assert.That(File.Exists(themed.Paths.SnapshotPath), Is.True);

            var reloaded = repository.Get(created.JobId);
            Assert.That(reloaded.JobId, Is.EqualTo(created.JobId));
            Assert.That(reloaded.ThemeId, Is.EqualTo("kawaii_01"));
            Assert.That(reloaded.AmountMinorUnits, Is.EqualTo(12000));
            Assert.That(reloaded.CurrencyCode, Is.EqualTo("THB"));
        }

        [Test]
        public void Initialize_ImportsLegacyJobSnapshotsIntoSqlite()
        {
            var legacyRepository = new FileSystemLocalRepository(tempRootDirectory);
            var legacySessions = new BoothSessionService(legacyRepository, new BoothStateMachine());
            legacySessions.Initialize();

            var created = legacySessions.CreateJob(9900, "THB");
            legacySessions.SelectTheme(created.JobId, "retro_02");

            var sqliteRepository = new SqliteLocalRepository(tempRootDirectory);
            sqliteRepository.Initialize();

            var imported = sqliteRepository.Get(created.JobId);
            Assert.That(imported.JobId, Is.EqualTo(created.JobId));
            Assert.That(imported.ThemeId, Is.EqualTo("retro_02"));
            Assert.That(sqliteRepository.List().Count, Is.EqualTo(1));
        }
    }
}
