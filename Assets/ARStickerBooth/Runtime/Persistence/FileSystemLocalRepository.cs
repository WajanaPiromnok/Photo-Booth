using System;
using System.Collections.Generic;
using System.IO;
using PhotoBooth.Booth.Domain;
using UnityEngine;

namespace PhotoBooth.Booth.Persistence
{
    public sealed class FileSystemLocalRepository : ILocalRepository
    {
        private readonly string rootDirectory;
        private readonly string jobsDirectory;

        public FileSystemLocalRepository(string rootDirectoryOverride = null)
        {
            rootDirectory = string.IsNullOrWhiteSpace(rootDirectoryOverride)
                ? Path.Combine(Application.persistentDataPath, "BoothData")
                : rootDirectoryOverride;
            jobsDirectory = Path.Combine(rootDirectory, "jobs");
        }

        public void Initialize()
        {
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(jobsDirectory);
        }

        public BoothJobPaths CreateJobPaths(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            var jobRoot = Path.Combine(jobsDirectory, jobId);
            var paths = new BoothJobPaths
            {
                RootDirectory = jobRoot,
                RawDirectory = Path.Combine(jobRoot, "raw"),
                ComposedDirectory = Path.Combine(jobRoot, "composed"),
                ThumbsDirectory = Path.Combine(jobRoot, "thumbs"),
                LogsDirectory = Path.Combine(jobRoot, "logs"),
                SnapshotPath = Path.Combine(jobRoot, "job.json")
            };

            Directory.CreateDirectory(paths.RootDirectory);
            Directory.CreateDirectory(paths.RawDirectory);
            Directory.CreateDirectory(paths.ComposedDirectory);
            Directory.CreateDirectory(paths.ThumbsDirectory);
            Directory.CreateDirectory(paths.LogsDirectory);

            return paths;
        }

        public BoothJob SaveNew(BoothJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            if (File.Exists(job.Paths.SnapshotPath))
            {
                throw new InvalidOperationException($"Job {job.JobId} already exists.");
            }

            return Write(job);
        }

        public BoothJob Save(BoothJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            return Write(job);
        }

        public BoothJob Get(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            var snapshotPath = Path.Combine(jobsDirectory, jobId, "job.json");
            if (!File.Exists(snapshotPath))
            {
                throw new FileNotFoundException($"Job snapshot not found for {jobId}.", snapshotPath);
            }

            var json = File.ReadAllText(snapshotPath);
            return JsonUtility.FromJson<BoothJob>(json);
        }

        public IReadOnlyList<BoothJob> List()
        {
            if (!Directory.Exists(jobsDirectory))
            {
                return Array.Empty<BoothJob>();
            }

            var jobs = new List<BoothJob>();
            foreach (var snapshotPath in Directory.GetFiles(jobsDirectory, "job.json", SearchOption.AllDirectories))
            {
                var json = File.ReadAllText(snapshotPath);
                jobs.Add(JsonUtility.FromJson<BoothJob>(json));
            }

            return jobs;
        }

        private static BoothJob Write(BoothJob job)
        {
            Directory.CreateDirectory(job.Paths.RootDirectory);
            Directory.CreateDirectory(job.Paths.RawDirectory);
            Directory.CreateDirectory(job.Paths.ComposedDirectory);
            Directory.CreateDirectory(job.Paths.ThumbsDirectory);
            Directory.CreateDirectory(job.Paths.LogsDirectory);

            var json = JsonUtility.ToJson(job, true);
            File.WriteAllText(job.Paths.SnapshotPath, json);
            return job;
        }
    }
}
