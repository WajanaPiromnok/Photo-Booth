using System;

namespace PhotoBooth.Booth.Content
{
    public enum BoothContentStatus
    {
        Unknown = 0,
        Idle = 1,
        Checking = 2,
        UpdateAvailable = 3,
        UpToDate = 4,
        Downloading = 5,
        Installing = 6,
        Installed = 7,
        Failed = 8
    }

    public enum BoothContentEntryType
    {
        Unknown = 0,
        Prefab = 1,
        Scene = 2,
        Texture = 3,
        Material = 4,
        AudioClip = 5
    }

    [Serializable]
    public sealed class BoothContentEntry
    {
        public string EntryId;
        public string Label;
        public BoothContentEntryType EntryType;
        public string AssetPath;
        public string ScenePath;
        public string ThemeId;
    }

    [Serializable]
    public sealed class BoothContentManifest
    {
        public string Version;
        public string Label;
        public string ManifestUrl;
        public string DownloadUrl;
        public string ChecksumSha256;
        public string[] SceneIds;
        public BoothContentEntry[] Entries;
        public bool ForceUpdate;
        public string PublishedAtUtc;
        public string ReleaseNotes;
    }

    [Serializable]
    public sealed class BoothContentState
    {
        public string InstalledVersion;
        public string ActivePackagePath;
        public string ActiveManifestPath;
        public string LastCheckedAtUtc;
        public string LastRemoteVersion;
        public string LastDownloadAtUtc;
    }

    [Serializable]
    public sealed class BoothContentSnapshot
    {
        public BoothContentStatus Status;
        public string InstalledVersion;
        public string RemoteVersion;
        public bool HasUpdateAvailable;
        public bool ForceUpdate;
        public float DownloadProgress;
        public string ActivePackagePath;
        public string Message;
    }
}
