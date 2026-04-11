using System;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PhotoBooth.Booth.Content
{
    public sealed class BoothContentRuntimeLoader
    {
        private readonly BoothContentManagementService contentManagementService;

        private AssetBundle activeBundle;
        private BoothContentManifest activeManifest;
        private string activePackagePath;

        public BoothContentRuntimeLoader(BoothContentManagementService contentManagementService)
        {
            this.contentManagementService = contentManagementService ?? throw new ArgumentNullException(nameof(contentManagementService));
        }

        public BoothContentManifest ActiveManifest => activeManifest;
        public bool HasLoadedContent => activeBundle != null;
        public string ActivePackagePath => activePackagePath;

        public async Task<bool> LoadInstalledContentAsync(bool forceReload = false)
        {
            var state = contentManagementService.LocalState;
            if (state == null || string.IsNullOrWhiteSpace(state.ActivePackagePath))
            {
                return false;
            }

            if (!forceReload
                && activeBundle != null
                && string.Equals(activePackagePath, state.ActivePackagePath, StringComparison.Ordinal))
            {
                return true;
            }

            if (!File.Exists(state.ActivePackagePath))
            {
                throw new FileNotFoundException("Installed content package not found.", state.ActivePackagePath);
            }

            Unload(false);

            var manifest = LoadManifestFromDisk(state.ActiveManifestPath) ?? contentManagementService.RemoteManifest;
            var createRequest = AssetBundle.LoadFromFileAsync(state.ActivePackagePath);
            await WaitForAsync(createRequest);

            if (createRequest.assetBundle == null)
            {
                throw new InvalidOperationException($"Failed to load AssetBundle at {state.ActivePackagePath}");
            }

            activeBundle = createRequest.assetBundle;
            activePackagePath = state.ActivePackagePath;
            activeManifest = manifest;
            return true;
        }

        public BoothContentEntry GetEntry(string entryId)
        {
            if (activeManifest?.Entries == null || string.IsNullOrWhiteSpace(entryId))
            {
                return null;
            }

            foreach (var entry in activeManifest.Entries)
            {
                if (entry != null && string.Equals(entry.EntryId, entryId, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        public async Task<T> LoadAssetAsync<T>(string entryId) where T : UnityEngine.Object
        {
            await EnsureBundleLoadedAsync();

            var entry = RequireEntry(entryId);
            if (string.IsNullOrWhiteSpace(entry.AssetPath))
            {
                throw new InvalidOperationException($"Content entry '{entryId}' does not define an asset path.");
            }

            var request = activeBundle.LoadAssetAsync<T>(entry.AssetPath);
            await WaitForAsync(request);
            return request.asset as T;
        }

        public async Task<GameObject> InstantiatePrefabAsync(string entryId, Transform parent = null)
        {
            await EnsureBundleLoadedAsync();

            var entry = RequireEntry(entryId);
            if (entry.EntryType != BoothContentEntryType.Prefab)
            {
                throw new InvalidOperationException($"Content entry '{entryId}' is not a prefab entry.");
            }

            var prefab = await LoadAssetAsync<GameObject>(entryId);
            if (prefab == null)
            {
                throw new InvalidOperationException($"Prefab entry '{entryId}' could not be loaded.");
            }

            return UnityEngine.Object.Instantiate(prefab, parent);
        }

        public async Task LoadSceneAsync(string entryId, LoadSceneMode mode = LoadSceneMode.Additive)
        {
            await EnsureBundleLoadedAsync();

            var entry = RequireEntry(entryId);
            if (entry.EntryType != BoothContentEntryType.Scene)
            {
                throw new InvalidOperationException($"Content entry '{entryId}' is not a scene entry.");
            }

            var scenePath = string.IsNullOrWhiteSpace(entry.ScenePath) ? entry.AssetPath : entry.ScenePath;
            if (string.IsNullOrWhiteSpace(scenePath))
            {
                throw new InvalidOperationException($"Scene entry '{entryId}' does not define a scene path.");
            }
            var operation = SceneManager.LoadSceneAsync(scenePath, mode);
            if (operation == null)
            {
                throw new InvalidOperationException($"Scene entry '{entryId}' could not start loading.");
            }

            while (!operation.isDone)
            {
                await Task.Yield();
            }
        }

        public void Unload(bool unloadAllLoadedObjects)
        {
            if (activeBundle != null)
            {
                activeBundle.Unload(unloadAllLoadedObjects);
            }

            activeBundle = null;
            activePackagePath = null;
            activeManifest = null;
        }

        private async Task EnsureBundleLoadedAsync()
        {
            if (activeBundle != null)
            {
                return;
            }

            var loaded = await LoadInstalledContentAsync(false);
            if (!loaded || activeBundle == null)
            {
                throw new InvalidOperationException("No installed content bundle is loaded.");
            }
        }

        private BoothContentEntry RequireEntry(string entryId)
        {
            var entry = GetEntry(entryId);
            if (entry == null)
            {
                throw new InvalidOperationException($"Content entry '{entryId}' is not defined in the active manifest.");
            }

            return entry;
        }

        private static BoothContentManifest LoadManifestFromDisk(string manifestPath)
        {
            if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifestPath);
            return JsonUtility.FromJson<BoothContentManifest>(json);
        }

        private static async Task WaitForAsync(AssetBundleCreateRequest request)
        {
            while (!request.isDone)
            {
                await Task.Yield();
            }
        }

        private static async Task WaitForAsync(AssetBundleRequest request)
        {
            while (!request.isDone)
            {
                await Task.Yield();
            }
        }
    }
}
