using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using PhotoBooth.Booth.Domain;
using UnityEngine;

namespace PhotoBooth.Booth.Persistence
{
    public sealed class SqliteLocalRepository : ILocalRepository
    {
        private const string DatabaseFileName = "booth-jobs.db";
        private const string JobsTableName = "booth_jobs";

        private readonly string rootDirectory;
        private readonly string jobsDirectory;
        private readonly string databasePath;
        private readonly Func<IDbConnection> connectionFactory;

        public SqliteLocalRepository(string rootDirectoryOverride = null, Func<IDbConnection> connectionFactoryOverride = null)
        {
            rootDirectory = string.IsNullOrWhiteSpace(rootDirectoryOverride)
                ? Path.Combine(Application.persistentDataPath, "BoothData")
                : rootDirectoryOverride;
            jobsDirectory = Path.Combine(rootDirectory, "jobs");
            databasePath = Path.Combine(rootDirectory, DatabaseFileName);
            connectionFactory = connectionFactoryOverride ?? CreateSqliteConnection;
        }

        public void Initialize()
        {
            Directory.CreateDirectory(rootDirectory);
            Directory.CreateDirectory(jobsDirectory);

            using var connection = OpenConnection();
            CreateSchema(connection);
            ImportLegacySnapshots(connection);
        }

        public BoothJobPaths CreateJobPaths(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            var paths = BuildJobPaths(jobId);
            EnsurePathDirectories(paths);
            return paths;
        }

        public BoothJob SaveNew(BoothJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var normalized = Normalize(job, true);
            using var connection = OpenConnection();
            if (Exists(connection, normalized.JobId))
            {
                throw new InvalidOperationException($"Job {normalized.JobId} already exists.");
            }

            Insert(connection, normalized);
            WriteSnapshot(normalized);
            return normalized;
        }

        public BoothJob Save(BoothJob job)
        {
            if (job == null)
            {
                throw new ArgumentNullException(nameof(job));
            }

            var normalized = Normalize(job, true);
            using var connection = OpenConnection();
            Upsert(connection, normalized);
            WriteSnapshot(normalized);
            return normalized;
        }

        public BoothJob Get(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                throw new ArgumentException("Job id is required.", nameof(jobId));
            }

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT job_json FROM {JobsTableName} WHERE job_id = @jobId LIMIT 1";
            AddParameter(command, "@jobId", jobId.Trim());

            var result = command.ExecuteScalar();
            if (result == null || result == DBNull.Value)
            {
                throw new FileNotFoundException($"Job snapshot not found for {jobId}.", Path.Combine(jobsDirectory, jobId, "job.json"));
            }

            var json = Convert.ToString(result);
            return Normalize(DeserializeJob(json, jobId), false);
        }

        public IReadOnlyList<BoothJob> List()
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT job_json FROM {JobsTableName} ORDER BY created_at_utc DESC, updated_at_utc DESC, job_id DESC";

            var jobs = new List<BoothJob>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var json = reader.IsDBNull(0) ? null : reader.GetString(0);
                jobs.Add(Normalize(DeserializeJob(json, null), false));
            }

            return jobs;
        }

        private static IDbConnection CreateSqliteConnection()
        {
            var connectionType = Type.GetType("Mono.Data.Sqlite.SqliteConnection, Mono.Data.Sqlite", throwOnError: false);
            if (connectionType == null)
            {
                throw new InvalidOperationException("Mono.Data.Sqlite is not available in this Unity runtime.");
            }

            return (IDbConnection)Activator.CreateInstance(connectionType);
        }

        private IDbConnection OpenConnection()
        {
            var connection = connectionFactory();
            connection.ConnectionString = $"Data Source={databasePath};Version=3;";
            connection.Open();
            return connection;
        }

        private static void CreateSchema(IDbConnection connection)
        {
            ExecuteNonQuery(
                connection,
                $@"CREATE TABLE IF NOT EXISTS {JobsTableName} (
                    job_id TEXT PRIMARY KEY NOT NULL,
                    status INTEGER NOT NULL,
                    payment_status INTEGER NOT NULL,
                    print_status INTEGER NOT NULL,
                    upload_status INTEGER NOT NULL,
                    theme_id TEXT,
                    payment_reference TEXT,
                    amount_minor_units INTEGER NOT NULL,
                    currency_code TEXT,
                    raw_capture_count INTEGER NOT NULL,
                    print_attempts INTEGER NOT NULL,
                    upload_attempts INTEGER NOT NULL,
                    download_url TEXT,
                    printer_name TEXT,
                    remote_asset_key TEXT,
                    published_at_utc TEXT,
                    retry_count INTEGER NOT NULL,
                    last_error TEXT,
                    last_print_error TEXT,
                    last_upload_error TEXT,
                    created_at_utc TEXT,
                    updated_at_utc TEXT,
                    job_json TEXT NOT NULL
                )");

            ExecuteNonQuery(connection, $"CREATE INDEX IF NOT EXISTS idx_{JobsTableName}_updated_at ON {JobsTableName}(updated_at_utc DESC)");
            ExecuteNonQuery(connection, $"CREATE INDEX IF NOT EXISTS idx_{JobsTableName}_status ON {JobsTableName}(status)");
            ExecuteNonQuery(connection, $"CREATE INDEX IF NOT EXISTS idx_{JobsTableName}_theme ON {JobsTableName}(theme_id)");
        }

        private void ImportLegacySnapshots(IDbConnection connection)
        {
            if (!Directory.Exists(jobsDirectory))
            {
                return;
            }

            foreach (var snapshotPath in Directory.GetFiles(jobsDirectory, "job.json", SearchOption.AllDirectories))
            {
                try
                {
                    var json = File.ReadAllText(snapshotPath);
                    var job = DeserializeJob(json, Path.GetFileName(Path.GetDirectoryName(snapshotPath)));
                    job.Paths ??= BuildJobPaths(job.JobId);
                    job.Paths.SnapshotPath = snapshotPath;
                    job = Normalize(job, false);

                    if (!Exists(connection, job.JobId))
                    {
                        Insert(connection, job);
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"Skipping legacy booth job snapshot import at {snapshotPath}: {exception.Message}");
                }
            }
        }

        private BoothJob Normalize(BoothJob job, bool createDirectories)
        {
            if (string.IsNullOrWhiteSpace(job.JobId))
            {
                throw new InvalidOperationException("Job id is required before persistence.");
            }

            job.Paths ??= BuildJobPaths(job.JobId);

            var expectedPaths = BuildJobPaths(job.JobId);
            job.Paths.RootDirectory = expectedPaths.RootDirectory;
            job.Paths.RawDirectory = expectedPaths.RawDirectory;
            job.Paths.ComposedDirectory = expectedPaths.ComposedDirectory;
            job.Paths.ThumbsDirectory = expectedPaths.ThumbsDirectory;
            job.Paths.LogsDirectory = expectedPaths.LogsDirectory;
            job.Paths.SnapshotPath = expectedPaths.SnapshotPath;

            if (createDirectories)
            {
                EnsurePathDirectories(job.Paths);
            }

            return job;
        }

        private BoothJobPaths BuildJobPaths(string jobId)
        {
            var jobRoot = Path.Combine(jobsDirectory, jobId);
            return new BoothJobPaths
            {
                RootDirectory = jobRoot,
                RawDirectory = Path.Combine(jobRoot, "raw"),
                ComposedDirectory = Path.Combine(jobRoot, "composed"),
                ThumbsDirectory = Path.Combine(jobRoot, "thumbs"),
                LogsDirectory = Path.Combine(jobRoot, "logs"),
                SnapshotPath = Path.Combine(jobRoot, "job.json")
            };
        }

        private static void EnsurePathDirectories(BoothJobPaths paths)
        {
            Directory.CreateDirectory(paths.RootDirectory);
            Directory.CreateDirectory(paths.RawDirectory);
            Directory.CreateDirectory(paths.ComposedDirectory);
            Directory.CreateDirectory(paths.ThumbsDirectory);
            Directory.CreateDirectory(paths.LogsDirectory);
        }

        private static BoothJob DeserializeJob(string json, string fallbackJobId)
        {
            var job = string.IsNullOrWhiteSpace(json) ? null : JsonUtility.FromJson<BoothJob>(json);
            if (job == null)
            {
                throw new InvalidOperationException($"Failed to deserialize booth job {fallbackJobId ?? "<unknown>"}.");
            }

            if (string.IsNullOrWhiteSpace(job.JobId) && !string.IsNullOrWhiteSpace(fallbackJobId))
            {
                job.JobId = fallbackJobId;
            }

            return job;
        }

        private static void Insert(IDbConnection connection, BoothJob job)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $@"INSERT INTO {JobsTableName} (
                        job_id,
                        status,
                        payment_status,
                        print_status,
                        upload_status,
                        theme_id,
                        payment_reference,
                        amount_minor_units,
                        currency_code,
                        raw_capture_count,
                        print_attempts,
                        upload_attempts,
                        download_url,
                        printer_name,
                        remote_asset_key,
                        published_at_utc,
                        retry_count,
                        last_error,
                        last_print_error,
                        last_upload_error,
                        created_at_utc,
                        updated_at_utc,
                        job_json
                    ) VALUES (
                        @jobId,
                        @status,
                        @paymentStatus,
                        @printStatus,
                        @uploadStatus,
                        @themeId,
                        @paymentReference,
                        @amountMinorUnits,
                        @currencyCode,
                        @rawCaptureCount,
                        @printAttempts,
                        @uploadAttempts,
                        @downloadUrl,
                        @printerName,
                        @remoteAssetKey,
                        @publishedAtUtc,
                        @retryCount,
                        @lastError,
                        @lastPrintError,
                        @lastUploadError,
                        @createdAtUtc,
                        @updatedAtUtc,
                        @jobJson
                    )";
            BindJob(command, job);
            command.ExecuteNonQuery();
        }

        private static void Upsert(IDbConnection connection, BoothJob job)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                $@"INSERT OR REPLACE INTO {JobsTableName} (
                        job_id,
                        status,
                        payment_status,
                        print_status,
                        upload_status,
                        theme_id,
                        payment_reference,
                        amount_minor_units,
                        currency_code,
                        raw_capture_count,
                        print_attempts,
                        upload_attempts,
                        download_url,
                        printer_name,
                        remote_asset_key,
                        published_at_utc,
                        retry_count,
                        last_error,
                        last_print_error,
                        last_upload_error,
                        created_at_utc,
                        updated_at_utc,
                        job_json
                    ) VALUES (
                        @jobId,
                        @status,
                        @paymentStatus,
                        @printStatus,
                        @uploadStatus,
                        @themeId,
                        @paymentReference,
                        @amountMinorUnits,
                        @currencyCode,
                        @rawCaptureCount,
                        @printAttempts,
                        @uploadAttempts,
                        @downloadUrl,
                        @printerName,
                        @remoteAssetKey,
                        @publishedAtUtc,
                        @retryCount,
                        @lastError,
                        @lastPrintError,
                        @lastUploadError,
                        @createdAtUtc,
                        @updatedAtUtc,
                        @jobJson
                    )";
            BindJob(command, job);
            command.ExecuteNonQuery();
        }

        private static void BindJob(IDbCommand command, BoothJob job)
        {
            var json = JsonUtility.ToJson(job, true);
            AddParameter(command, "@jobId", job.JobId);
            AddParameter(command, "@status", (int)job.Status);
            AddParameter(command, "@paymentStatus", (int)job.PaymentStatus);
            AddParameter(command, "@printStatus", (int)job.PrintStatus);
            AddParameter(command, "@uploadStatus", (int)job.UploadStatus);
            AddParameter(command, "@themeId", job.ThemeId);
            AddParameter(command, "@paymentReference", job.PaymentReference);
            AddParameter(command, "@amountMinorUnits", job.AmountMinorUnits);
            AddParameter(command, "@currencyCode", job.CurrencyCode);
            AddParameter(command, "@rawCaptureCount", job.RawCaptureCount);
            AddParameter(command, "@printAttempts", job.PrintAttempts);
            AddParameter(command, "@uploadAttempts", job.UploadAttempts);
            AddParameter(command, "@downloadUrl", job.DownloadUrl);
            AddParameter(command, "@printerName", job.PrinterName);
            AddParameter(command, "@remoteAssetKey", job.RemoteAssetKey);
            AddParameter(command, "@publishedAtUtc", job.PublishedAtUtc);
            AddParameter(command, "@retryCount", job.RetryCount);
            AddParameter(command, "@lastError", job.LastError);
            AddParameter(command, "@lastPrintError", job.LastPrintError);
            AddParameter(command, "@lastUploadError", job.LastUploadError);
            AddParameter(command, "@createdAtUtc", job.CreatedAtUtc);
            AddParameter(command, "@updatedAtUtc", job.UpdatedAtUtc);
            AddParameter(command, "@jobJson", json);
        }

        private static bool Exists(IDbConnection connection, string jobId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM {JobsTableName} WHERE job_id = @jobId";
            AddParameter(command, "@jobId", jobId);
            var result = command.ExecuteScalar();
            return Convert.ToInt64(result) > 0;
        }

        private static void ExecuteNonQuery(IDbConnection connection, string commandText)
        {
            using var command = connection.CreateCommand();
            command.CommandText = commandText;
            command.ExecuteNonQuery();
        }

        private static void AddParameter(IDbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        private static void WriteSnapshot(BoothJob job)
        {
            EnsurePathDirectories(job.Paths);
            File.WriteAllText(job.Paths.SnapshotPath, JsonUtility.ToJson(job, true));
        }
    }
}
