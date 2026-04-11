using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using PhotoBooth.Booth.Content;
using UnityEditor;
using UnityEngine;

namespace PhotoBooth.Booth.Editor.Content
{
    public static class BoothContentBuilder
    {
        [MenuItem("Photo Booth/Content/Create Build Profile", priority = 100)]
        public static void CreateBuildProfileAsset()
        {
            const string defaultDirectory = "Assets/ARStickerBooth/Editor/ContentProfiles";
            Directory.CreateDirectory(Path.Combine(GetProjectRoot(), "Assets/ARStickerBooth/Editor/ContentProfiles"));

            var asset = ScriptableObject.CreateInstance<BoothContentBuildProfile>();
            var assetPath = AssetDatabase.GenerateUniqueAssetPath($"{defaultDirectory}/BoothContentBuildProfile.asset");
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = asset;
        }

        [MenuItem("Photo Booth/Content/Build Selected Profile", priority = 101)]
        public static void BuildSelectedProfile()
        {
            var profile = Selection.activeObject as BoothContentBuildProfile;
            if (profile == null)
            {
                EditorUtility.DisplayDialog("Photo Booth", "Select a BoothContentBuildProfile asset first.", "OK");
                return;
            }

            var report = Build(profile);
            EditorUtility.RevealInFinder(report.OutputDirectory);
            Debug.Log($"Built content release {report.Version} at {report.OutputDirectory}");
        }

        public static void BuildFromCommandLine()
        {
            var args = Environment.GetCommandLineArgs();
            var profilePath = GetArgumentValue(args, "-boothContentProfile");
            var outputDirectoryOverride = GetArgumentValue(args, "-boothContentOutput");
            var versionOverride = GetArgumentValue(args, "-boothContentVersion");

            if (string.IsNullOrWhiteSpace(profilePath))
            {
                throw new InvalidOperationException("Missing -boothContentProfile <AssetDatabasePath> argument.");
            }

            var profile = AssetDatabase.LoadAssetAtPath<BoothContentBuildProfile>(profilePath);
            if (profile == null)
            {
                throw new FileNotFoundException($"Could not load BoothContentBuildProfile at {profilePath}");
            }

            var report = Build(profile, versionOverride, outputDirectoryOverride);
            Debug.Log($"Built content release {report.Version} at {report.OutputDirectory}");
        }

        public static BoothContentBuildReport Build(
            BoothContentBuildProfile profile,
            string versionOverride = null,
            string outputDirectoryOverride = null)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            if (profile.entries == null || profile.entries.Length == 0)
            {
                throw new InvalidOperationException("The build profile does not contain any entries.");
            }

            var version = string.IsNullOrWhiteSpace(versionOverride) ? profile.version : versionOverride.Trim();
            if (string.IsNullOrWhiteSpace(version))
            {
                throw new InvalidOperationException("Content version is required.");
            }

            var bundleFileName = SanitizeBundleFileName(profile.bundleFileName);
            var outputRoot = ResolveOutputDirectory(profile.outputDirectory, outputDirectoryOverride);
            var releaseDirectory = Path.Combine(outputRoot, version);
            Directory.CreateDirectory(releaseDirectory);

            var assetPaths = CollectAssetPaths(profile.entries);
            var bundleBuild = new AssetBundleBuild
            {
                assetBundleName = bundleFileName,
                assetNames = assetPaths
            };

            var manifestAsset = BuildPipeline.BuildAssetBundles(
                releaseDirectory,
                new[] { bundleBuild },
                profile.bundleOptions,
                profile.target);

            if (manifestAsset == null)
            {
                throw new InvalidOperationException("BuildPipeline.BuildAssetBundles returned null.");
            }

            var bundlePath = Path.Combine(releaseDirectory, bundleFileName);
            if (!File.Exists(bundlePath))
            {
                throw new FileNotFoundException("Expected built AssetBundle file was not created.", bundlePath);
            }

            var manifest = new BoothContentManifest
            {
                Version = version,
                Label = profile.label,
                DownloadUrl = string.Empty,
                ManifestUrl = string.Empty,
                ChecksumSha256 = ComputeSha256(bundlePath),
                SceneIds = profile.entries
                    .Where(entry => entry != null && entry.entryType == BoothContentEntryType.Scene)
                    .Select(entry => entry.entryId?.Trim())
                    .Where(entryId => !string.IsNullOrWhiteSpace(entryId))
                    .Distinct()
                    .ToArray(),
                Entries = profile.entries
                    .Where(entry => entry != null)
                    .Select(entry => CreateManifestEntry(entry))
                    .ToArray(),
                ForceUpdate = false,
                PublishedAtUtc = DateTime.UtcNow.ToString("O"),
                ReleaseNotes = profile.releaseNotes
            };

            var manifestPath = Path.Combine(releaseDirectory, "manifest.json");
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));

            AssetDatabase.Refresh();

            return new BoothContentBuildReport
            {
                Version = version,
                BundlePath = bundlePath,
                ManifestPath = manifestPath,
                OutputDirectory = releaseDirectory,
                BundleFileName = bundleFileName,
                Target = profile.target
            };
        }

        private static BoothContentEntry CreateManifestEntry(BoothContentBuildEntry entry)
        {
            if (string.IsNullOrWhiteSpace(entry.entryId))
            {
                throw new InvalidOperationException("Every content entry must define entryId.");
            }

            if (entry.asset == null)
            {
                throw new InvalidOperationException($"Content entry '{entry.entryId}' is missing an asset reference.");
            }

            var assetPath = AssetDatabase.GetAssetPath(entry.asset);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new InvalidOperationException($"Content entry '{entry.entryId}' has an invalid asset path.");
            }

            return new BoothContentEntry
            {
                EntryId = entry.entryId.Trim(),
                Label = entry.label,
                EntryType = entry.entryType,
                AssetPath = entry.entryType == BoothContentEntryType.Scene ? string.Empty : assetPath,
                ScenePath = entry.entryType == BoothContentEntryType.Scene ? assetPath : string.Empty,
                ThemeId = entry.themeId
            };
        }

        private static string[] CollectAssetPaths(IEnumerable<BoothContentBuildEntry> entries)
        {
            var uniquePaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    continue;
                }

                if (entry.asset == null)
                {
                    throw new InvalidOperationException($"Content entry '{entry.entryId}' is missing an asset reference.");
                }

                var assetPath = AssetDatabase.GetAssetPath(entry.asset);
                if (string.IsNullOrWhiteSpace(assetPath))
                {
                    throw new InvalidOperationException($"Content entry '{entry.entryId}' has an invalid asset path.");
                }

                uniquePaths.Add(assetPath);
            }

            return uniquePaths.ToArray();
        }

        private static string ResolveOutputDirectory(string profileOutputDirectory, string overrideOutputDirectory)
        {
            var root = GetProjectRoot();
            var configured = string.IsNullOrWhiteSpace(overrideOutputDirectory) ? profileOutputDirectory : overrideOutputDirectory;
            if (string.IsNullOrWhiteSpace(configured))
            {
                configured = "Builds/Content";
            }

            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(root, configured));
        }

        private static string SanitizeBundleFileName(string bundleFileName)
        {
            var clean = string.IsNullOrWhiteSpace(bundleFileName) ? "booth-content.bundle" : bundleFileName.Trim();
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
            {
                clean = clean.Replace(invalidCharacter, '_');
            }

            return clean;
        }

        private static string GetArgumentValue(IReadOnlyList<string> args, string name)
        {
            for (var index = 0; index < args.Count - 1; index++)
            {
                if (string.Equals(args[index], name, StringComparison.Ordinal))
                {
                    return args[index + 1];
                }
            }

            return null;
        }

        private static string ComputeSha256(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static string GetProjectRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }
    }

    public sealed class BoothContentBuildReport
    {
        public string Version;
        public string BundlePath;
        public string ManifestPath;
        public string OutputDirectory;
        public string BundleFileName;
        public BuildTarget Target;
    }
}
