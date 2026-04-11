using System;
using PhotoBooth.Booth.Content;
using UnityEditor;
using UnityEngine;

namespace PhotoBooth.Booth.Editor.Content
{
    [CreateAssetMenu(
        fileName = "BoothContentBuildProfile",
        menuName = "Photo Booth/Content Build Profile",
        order = 1)]
    public sealed class BoothContentBuildProfile : ScriptableObject
    {
        [Header("Release")]
        public string version = "1.0.0";
        public string label = "Default Booth Content";
        [TextArea(2, 6)] public string releaseNotes = string.Empty;

        [Header("Bundle")]
        public string bundleFileName = "booth-content.bundle";
        public string outputDirectory = "Builds/Content";
        public BuildTarget target = BuildTarget.StandaloneWindows64;
        public BuildAssetBundleOptions bundleOptions = BuildAssetBundleOptions.None;

        [Header("Entries")]
        public BoothContentBuildEntry[] entries = Array.Empty<BoothContentBuildEntry>();
    }

    [Serializable]
    public sealed class BoothContentBuildEntry
    {
        public string entryId;
        public string label;
        public BoothContentEntryType entryType = BoothContentEntryType.Prefab;
        public UnityEngine.Object asset;
        public string themeId;
    }
}
