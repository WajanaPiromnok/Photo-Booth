using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Unity.Services.CloudSave;
using Unity.Services.RemoteConfig;
using UnityEngine;
using UnityEngine.Networking;

namespace PhotoBooth.Booth.Content
{
    public sealed class BoothContentManagementService
    {
        private const string ContentVersionKey = "booth_content_version";
        private const string ContentLabelKey = "booth_content_label";
        private const string ContentManifestUrlKey = "booth_content_manifest_url";
        private const string ContentDownloadUrlKey = "booth_content_download_url";
        private const string ContentChecksumKey = "booth_content_checksum_sha256";
        private const string ContentSceneIdsKey = "booth_content_scene_ids";
        private const string ContentForceUpdateKey = "booth_content_force_update";
        private const string ContentReleaseNotesKey = "booth_content_release_notes";

        private readonly string contentRootDirectory;
        private readonly string packagesDirectory;
        private readonly string statePath;
        private readonly string defaultInstalledVersion;

        private BoothContentSnapshot snapshot;

        public BoothContentManagementService(string rootDirectoryOverride = null, string defaultInstalledVersion = null)
        {
            contentRootDirectory = string.IsNullOrWhiteSpace(rootDirectoryOverride)
                ? Path.Combine(Application.persistentDataPath, "BoothData", "content")
                : rootDirectoryOverride;
            packagesDirectory = Path.Combine(contentRootDirectory, "packages");
            statePath = Path.Combine(contentRootDirectory, "content-state.json");
            this.defaultInstalledVersion = string.IsNullOrWhiteSpace(defaultInstalledVersion)
                ? "0.0.0"
                : defaultInstalledVersion.Trim();
        }

        public BoothContentState LocalState { get; private set; }
        public BoothContentManifest RemoteManifest { get; private set; }
        public BoothContentSnapshot Snapshot => snapshot;
        public string LastRequestOrigin { get; private set; }

        public event Action<BoothContentSnapshot> SnapshotChanged;

        public void Initialize()
        {
            Directory.CreateDirectory(contentRootDirectory);
            Directory.CreateDirectory(packagesDirectory);

            LocalState = LoadState();
            snapshot = new BoothContentSnapshot
            {
                Status = BoothContentStatus.Idle,
                InstalledVersion = LocalState.InstalledVersion,
                RemoteVersion = LocalState.LastRemoteVersion,
                ActivePackagePath = LocalState.ActivePackagePath,
                Message = "Content management ready."
            };

            EmitSnapshot();
        }

        public string GetInstalledVersion()
        {
            return LocalState?.InstalledVersion ?? defaultInstalledVersion;
        }

        public async Task<BoothContentSnapshot> RefreshRemoteManifestAsync()
        {
            EnsureInitialized();
            UpdateSnapshot(BoothContentStatus.Checking, 0f, false, false, LocalState.LastRemoteVersion, "Checking remote content version...");

            var runtimeConfig = await RemoteConfigService.Instance.FetchConfigsAsync(new UserAttributes(), new AppAttributes
            {
                installedContentVersion = GetInstalledVersion(),
                applicationVersion = string.IsNullOrWhiteSpace(Application.version) ? "unknown" : Application.version,
                platform = Application.platform.ToString()
            });

            var remoteManifest = BuildRemoteManifest(runtimeConfig);
            if (!string.IsNullOrWhiteSpace(remoteManifest.ManifestUrl))
            {
                remoteManifest = await OverlayManifestFromUrlAsync(remoteManifest);
            }

            RemoteManifest = remoteManifest;
            LastRequestOrigin = runtimeConfig.origin.ToString();
            LocalState.LastCheckedAtUtc = DateTime.UtcNow.ToString("O");
            LocalState.LastRemoteVersion = RemoteManifest.Version;
            SaveState();

            var hasUpdate = BoothContentVersionComparer.Compare(RemoteManifest.Version, GetInstalledVersion()) > 0;
            var forceUpdate = RemoteManifest.ForceUpdate && !string.Equals(GetInstalledVersion(), RemoteManifest.Version, StringComparison.OrdinalIgnoreCase);
            var status = hasUpdate || forceUpdate ? BoothContentStatus.UpdateAvailable : BoothContentStatus.UpToDate;
            var message = status == BoothContentStatus.UpdateAvailable
                ? $"Content update available: {GetInstalledVersion()} -> {RemoteManifest.Version}"
                : $"Content is up to date at {GetInstalledVersion()}";

            UpdateSnapshot(status, 0f, hasUpdate, forceUpdate, RemoteManifest.Version, message);
            return Snapshot;
        }

        public async Task<BoothContentSnapshot> DownloadAndInstallAsync()
        {
            EnsureInitialized();

            if (RemoteManifest == null)
            {
                throw new InvalidOperationException("Remote manifest is not loaded. Call RefreshRemoteManifestAsync first.");
            }

            if (string.IsNullOrWhiteSpace(RemoteManifest.DownloadUrl))
            {
                throw new InvalidOperationException("Remote manifest is missing a download URL.");
            }

            var downloadStartedAt = Time.realtimeSinceStartup;
            var versionToken = SanitizeVersionToken(RemoteManifest.Version);
            var versionDirectory = Path.Combine(packagesDirectory, versionToken);
            var finalPackagePath = Path.Combine(versionDirectory, $"content{ResolveFileExtension(RemoteManifest.DownloadUrl)}");
            var tempPackagePath = finalPackagePath + ".download";
            var manifestPath = Path.Combine(versionDirectory, "manifest.json");

            Directory.CreateDirectory(versionDirectory);
            if (File.Exists(tempPackagePath))
            {
                File.Delete(tempPackagePath);
            }

            UpdateSnapshot(BoothContentStatus.Downloading, 0f, true, RemoteManifest.ForceUpdate, RemoteManifest.Version, "Downloading latest content package...");

            using (var request = UnityWebRequest.Get(RemoteManifest.DownloadUrl))
            {
                request.downloadHandler = new DownloadHandlerFile(tempPackagePath);
                await SendRequestAsync(request, progress =>
                {
                    UpdateSnapshot(BoothContentStatus.Downloading, progress, true, RemoteManifest.ForceUpdate, RemoteManifest.Version, "Downloading latest content package...");
                });
            }

            if (!string.IsNullOrWhiteSpace(RemoteManifest.ChecksumSha256))
            {
                var actualChecksum = ComputeSha256(tempPackagePath);
                if (!string.Equals(actualChecksum, RemoteManifest.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPackagePath);
                    UpdateSnapshot(BoothContentStatus.Failed, 0f, true, RemoteManifest.ForceUpdate, RemoteManifest.Version, "Downloaded content checksum mismatch.");
                    throw new InvalidOperationException("Downloaded content checksum mismatch.");
                }
            }

            UpdateSnapshot(BoothContentStatus.Installing, 1f, true, RemoteManifest.ForceUpdate, RemoteManifest.Version, "Installing content package...");

            if (File.Exists(finalPackagePath))
            {
                File.Delete(finalPackagePath);
            }

            File.Move(tempPackagePath, finalPackagePath);
            File.WriteAllText(manifestPath, JsonUtility.ToJson(RemoteManifest, true));

            LocalState.InstalledVersion = RemoteManifest.Version;
            LocalState.ActivePackagePath = finalPackagePath;
            LocalState.ActiveManifestPath = manifestPath;
            LocalState.LastRemoteVersion = RemoteManifest.Version;
            LocalState.LastDownloadAtUtc = DateTime.UtcNow.ToString("O");
            SaveState();

            await TrySyncInstalledVersionAsync();

            UpdateSnapshot(BoothContentStatus.Installed, 1f, false, false, RemoteManifest.Version, $"Installed content {RemoteManifest.Version}");
            return Snapshot;
        }

        private async Task<BoothContentManifest> OverlayManifestFromUrlAsync(BoothContentManifest seedManifest)
        {
            try
            {
                using var request = UnityWebRequest.Get(seedManifest.ManifestUrl);
                await SendRequestAsync(request, null);
                var json = request.downloadHandler.text;
                var remoteFromUrl = JsonUtility.FromJson<BoothContentManifest>(json);
                if (remoteFromUrl == null)
                {
                    return seedManifest;
                }

                if (string.IsNullOrWhiteSpace(remoteFromUrl.ManifestUrl))
                {
                    remoteFromUrl.ManifestUrl = seedManifest.ManifestUrl;
                }

                if (string.IsNullOrWhiteSpace(remoteFromUrl.DownloadUrl))
                {
                    remoteFromUrl.DownloadUrl = seedManifest.DownloadUrl;
                }

                if (string.IsNullOrWhiteSpace(remoteFromUrl.Version))
                {
                    remoteFromUrl.Version = seedManifest.Version;
                }

                if (remoteFromUrl.SceneIds == null || remoteFromUrl.SceneIds.Length == 0)
                {
                    remoteFromUrl.SceneIds = seedManifest.SceneIds;
                }

                if (string.IsNullOrWhiteSpace(remoteFromUrl.ChecksumSha256))
                {
                    remoteFromUrl.ChecksumSha256 = seedManifest.ChecksumSha256;
                }

                return remoteFromUrl;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Content manifest fetch failed: {exception.Message}");
                return seedManifest;
            }
        }

        private async Task TrySyncInstalledVersionAsync()
        {
            try
            {
                var stateToSave = new Dictionary<string, object>
                {
                    { "booth_installed_content_version", LocalState.InstalledVersion },
                    { "booth_last_content_sync_utc", DateTime.UtcNow.ToString("O") }
                };

                await CloudSaveService.Instance.Data.Player.SaveAsync(stateToSave);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Cloud Save content sync failed: {exception.Message}");
            }
        }

        private BoothContentManifest BuildRemoteManifest(RuntimeConfig runtimeConfig)
        {
            var version = runtimeConfig.GetString(ContentVersionKey, LocalState.LastRemoteVersion);
            var downloadUrl = runtimeConfig.GetString(ContentDownloadUrlKey, string.Empty);
            var manifestUrl = runtimeConfig.GetString(ContentManifestUrlKey, string.Empty);

            return new BoothContentManifest
            {
                Version = string.IsNullOrWhiteSpace(version) ? GetInstalledVersion() : version.Trim(),
                Label = runtimeConfig.GetString(ContentLabelKey, string.Empty),
                ManifestUrl = manifestUrl,
                DownloadUrl = downloadUrl,
                ChecksumSha256 = runtimeConfig.GetString(ContentChecksumKey, string.Empty),
                SceneIds = ParseSceneIds(runtimeConfig.GetString(ContentSceneIdsKey, string.Empty)),
                ForceUpdate = runtimeConfig.GetBool(ContentForceUpdateKey, false),
                PublishedAtUtc = DateTime.UtcNow.ToString("O"),
                ReleaseNotes = runtimeConfig.GetString(ContentReleaseNotesKey, string.Empty)
            };
        }

        private async Task SendRequestAsync(UnityWebRequest request, Action<float> progressCallback)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                progressCallback?.Invoke(operation.progress);
                await Task.Yield();
            }

            progressCallback?.Invoke(1f);

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException(request.error);
            }
        }

        private BoothContentState LoadState()
        {
            if (!File.Exists(statePath))
            {
                return new BoothContentState
                {
                    InstalledVersion = defaultInstalledVersion
                };
            }

            try
            {
                var json = File.ReadAllText(statePath);
                var state = JsonUtility.FromJson<BoothContentState>(json);
                if (state == null)
                {
                    throw new InvalidOperationException("State file is empty.");
                }

                if (string.IsNullOrWhiteSpace(state.InstalledVersion))
                {
                    state.InstalledVersion = defaultInstalledVersion;
                }

                return state;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Content state load failed, using defaults: {exception.Message}");
                return new BoothContentState
                {
                    InstalledVersion = defaultInstalledVersion
                };
            }
        }

        private void SaveState()
        {
            var json = JsonUtility.ToJson(LocalState, true);
            File.WriteAllText(statePath, json);
        }

        private void UpdateSnapshot(
            BoothContentStatus status,
            float downloadProgress,
            bool hasUpdateAvailable,
            bool forceUpdate,
            string remoteVersion,
            string message)
        {
            snapshot = new BoothContentSnapshot
            {
                Status = status,
                InstalledVersion = GetInstalledVersion(),
                RemoteVersion = remoteVersion,
                HasUpdateAvailable = hasUpdateAvailable,
                ForceUpdate = forceUpdate,
                DownloadProgress = Mathf.Clamp01(downloadProgress),
                ActivePackagePath = LocalState?.ActivePackagePath,
                Message = message
            };

            EmitSnapshot();
        }

        private void EmitSnapshot()
        {
            SnapshotChanged?.Invoke(snapshot);
        }

        private void EnsureInitialized()
        {
            if (LocalState == null)
            {
                throw new InvalidOperationException("Content service is not initialized.");
            }
        }

        private static string[] ParseSceneIds(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
            {
                return Array.Empty<string>();
            }

            var rawParts = csv.Split(',');
            var sceneIds = new List<string>();
            foreach (var rawPart in rawParts)
            {
                var trimmed = rawPart.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    sceneIds.Add(trimmed);
                }
            }

            return sceneIds.ToArray();
        }

        private static string SanitizeVersionToken(string version)
        {
            var clean = string.IsNullOrWhiteSpace(version) ? "content" : version.Trim();
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                clean = clean.Replace(invalidCharacter, '_');
            }

            return clean.Replace('/', '_').Replace('\\', '_').Replace(' ', '_');
        }

        private static string ResolveFileExtension(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return ".bundle";
            }

            try
            {
                var uri = new Uri(url);
                var extension = Path.GetExtension(uri.AbsolutePath);
                return string.IsNullOrWhiteSpace(extension) ? ".bundle" : extension;
            }
            catch
            {
                return ".bundle";
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private struct UserAttributes
        {
        }

        private struct AppAttributes
        {
            public string installedContentVersion;
            public string applicationVersion;
            public string platform;
        }
    }
}
