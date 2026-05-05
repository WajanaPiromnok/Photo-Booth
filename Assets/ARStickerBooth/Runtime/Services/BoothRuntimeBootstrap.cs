using System;
using System.IO;
using System.Threading.Tasks;
using PhotoBooth.Booth.Content;
using PhotoBooth.Booth.Persistence;
using PhotoBooth.Booth.Printing;
using PhotoBooth.Booth.Sync;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Core.Environments;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace PhotoBooth.Booth.Services
{
    public sealed class BoothRuntimeBootstrap : MonoBehaviour
    {
        [Serializable]
        public sealed class StringChangedEvent : UnityEvent<string>
        {
        }

        [Serializable]
        public sealed class BoolChangedEvent : UnityEvent<bool>
        {
        }

        [Serializable]
        public sealed class FloatChangedEvent : UnityEvent<float>
        {
        }

        [SerializeField] private bool initializeOnAwake = true;
        [SerializeField] private string repositoryRootOverride = string.Empty;
        [SerializeField] private bool preferSqliteRepository = true;
        [SerializeField] private bool initializeUnityGamingServices = true;
        [SerializeField] private string unityServicesEnvironmentName = "production";
        [SerializeField] private bool startAnalyticsDataCollection = true;
        [SerializeField] private string boothId = "booth-a01";
        [SerializeField] private string shippedContentVersion = "1.0.0";
        [SerializeField] private string contentRootOverride = string.Empty;
        [SerializeField] private bool autoCheckContentOnStart = true;
        [SerializeField] private bool autoDownloadContentUpdate = false;
        [SerializeField] private bool autoLoadInstalledContentOnStart = true;
        [SerializeField] private string defaultPrinterName = "Photo Booth Printer";
        [SerializeField] private string demoThemeId = "demo_theme";
        [SerializeField] private long demoAmountMinorUnits = 12000;
        [SerializeField] private int demoRawCaptureCount = 1;
        [SerializeField] private Vector2Int demoComposedImageSize = new(1280, 720);
        [SerializeField] private Vector2Int demoThumbnailSize = new(320, 180);
        [SerializeField] private string backendDeviceId = "booth-a01";
        [SerializeField] private string backendDeviceToken = string.Empty;
        [SerializeField] private string backendBoothApiBaseUrl = string.Empty;
        [SerializeField] private string backendPublishApiBaseUrl = string.Empty;
        [SerializeField] private string backendDownloadBaseUrl = "https://example.invalid/d";
        [SerializeField] private string backendAssetUploadPathTemplate = "/v1/jobs/{jobId}/assets/upload";
        [SerializeField] private string backendAssetRegistrationPathTemplate = "/v1/jobs/{jobId}/assets";
        [SerializeField] private string backendPublishPathTemplate = "/v1/jobs/{jobId}/publish";
        [SerializeField] private bool backendSeparateAssetRegistration = true;
        [SerializeField] private bool backendUploadThumbnail = true;
        [SerializeField] private int backendRequestTimeoutSeconds = 30;
        [SerializeField] private StringChangedEvent onContentStatusMessageChanged = new();
        [SerializeField] private StringChangedEvent onInstalledContentVersionChanged = new();
        [SerializeField] private StringChangedEvent onRemoteContentVersionChanged = new();
        [SerializeField] private BoolChangedEvent onContentUpdateAvailabilityChanged = new();
        [SerializeField] private FloatChangedEvent onContentDownloadProgressChanged = new();
        [SerializeField] private StringChangedEvent onPrintStatusMessageChanged = new();
        [SerializeField] private StringChangedEvent onSyncStatusMessageChanged = new();

        public BoothSessionService SessionService { get; private set; }
        public BoothAnalyticsService AnalyticsService { get; private set; }
        public BoothContentManagementService ContentManagementService { get; private set; }
        public BoothContentRuntimeLoader ContentRuntimeLoader { get; private set; }
        public BoothPrintService PrintService { get; private set; }
        public BoothSyncService SyncService { get; private set; }
        public Task InitializationTask { get; private set; }
        public string BackendDeviceId => string.IsNullOrWhiteSpace(backendDeviceId) ? boothId : backendDeviceId.Trim();
        public string BackendDeviceToken => backendDeviceToken ?? string.Empty;
        public string BackendBoothApiBaseUrl => backendBoothApiBaseUrl ?? string.Empty;
        public int BackendRequestTimeoutSeconds => Math.Max(1, backendRequestTimeoutSeconds);

        private string lastDemoJobId;

        private void Awake()
        {
            if (!initializeOnAwake)
            {
                return;
            }

            InitializationTask = InitializeAsync();
        }

        public void Initialize()
        {
            InitializationTask = InitializeAsync();
        }

        public async Task EnsureReadyAsync()
        {
            await EnsureRuntimeReadyForUiAsync();
        }

        public async Task InitializeAsync()
        {
            if (SessionService != null)
            {
                return;
            }

            var repository = CreateRepository();
            var stateMachine = new BoothStateMachine();
            ContentManagementService = new BoothContentManagementService(contentRootOverride, shippedContentVersion);
            ContentManagementService.Initialize();
            ContentManagementService.SnapshotChanged += HandleContentSnapshotChanged;
            ContentRuntimeLoader = new BoothContentRuntimeLoader(ContentManagementService);

            AnalyticsService = new BoothAnalyticsService(boothId, () => ContentManagementService.GetInstalledVersion());
            SessionService = new BoothSessionService(repository, stateMachine, AnalyticsService);
            SessionService.Initialize();
            PrintService = new BoothPrintService(SessionService, new SimulatedPrintHelperClient());
            var backendConfig = CreateBackendScaffoldConfig();
            SyncService = new BoothSyncService(SessionService, CreateSyncClient(backendConfig), backendConfig);

            SceneManager.activeSceneChanged += HandleActiveSceneChanged;

            if (autoLoadInstalledContentOnStart)
            {
                await TryLoadInstalledContentAsync(false);
            }

            if (!initializeUnityGamingServices)
            {
                HandleContentSnapshotChanged(ContentManagementService.Snapshot);
                return;
            }

            try
            {
                await InitializeUnityGamingServicesAsync();

                if (autoCheckContentOnStart)
                {
                    await RefreshContentAsync(autoDownloadContentUpdate);
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"Booth runtime bootstrap failed: {exception}");
                onContentStatusMessageChanged.Invoke($"Initialization failed: {exception.Message}");
            }
        }

        public async void CheckForContentUpdatesFromUi()
        {
            try
            {
                await RefreshContentAsync(false);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Content check failed: {exception}");
                onContentStatusMessageChanged.Invoke($"Content check failed: {exception.Message}");
            }
        }

        public async void DownloadLatestContentFromUi()
        {
            if (ContentManagementService == null)
            {
                return;
            }

            try
            {
                var installStartedAt = Time.realtimeSinceStartup;
                var snapshot = await ContentManagementService.DownloadAndInstallAsync();
                await TryLoadInstalledContentAsync(true);
                var installDurationSeconds = Mathf.Max(0, Mathf.RoundToInt(Time.realtimeSinceStartup - installStartedAt));
                AnalyticsService?.TrackContentInstalled(snapshot, installDurationSeconds);
                onContentStatusMessageChanged.Invoke(snapshot.Message);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Content install failed: {exception}");
                onContentStatusMessageChanged.Invoke($"Content install failed: {exception.Message}");
            }
        }

        public void TrackFeatureUsed(string featureName)
        {
            AnalyticsService?.TrackFeatureUsed(featureName);
        }

        public async void PrintJobFromUi(string jobId)
        {
            await EnsureRuntimeReadyForUiAsync();

            var resolvedJobId = ResolveUiJobId(jobId);
            if (string.IsNullOrWhiteSpace(resolvedJobId) || PrintService == null)
            {
                onPrintStatusMessageChanged.Invoke("No demo job is ready for printing.");
                return;
            }

            try
            {
                var job = await PrintService.PrintAsync(resolvedJobId, defaultPrinterName);
                onPrintStatusMessageChanged.Invoke($"Print status: {job.PrintStatus}");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Print flow failed: {exception}");
                onPrintStatusMessageChanged.Invoke($"Print failed: {exception.Message}");
            }
        }

        public async void SyncJobFromUi(string jobId)
        {
            await EnsureRuntimeReadyForUiAsync();

            var resolvedJobId = ResolveUiJobId(jobId);
            if (string.IsNullOrWhiteSpace(resolvedJobId) || SyncService == null)
            {
                onSyncStatusMessageChanged.Invoke("No demo job is ready for sync.");
                return;
            }

            try
            {
                var job = await SyncService.SyncAsync(resolvedJobId);
                onSyncStatusMessageChanged.Invoke($"Sync status: {job.UploadStatus}");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Sync flow failed: {exception}");
                onSyncStatusMessageChanged.Invoke($"Sync failed: {exception.Message}");
            }
        }

        public async void CreateDemoJobFromUi()
        {
            try
            {
                await EnsureRuntimeReadyForUiAsync();
                var job = CreateDemoJob();
                lastDemoJobId = job.JobId;
                onSyncStatusMessageChanged.Invoke($"Demo job ready: {job.JobId}");
                onPrintStatusMessageChanged.Invoke($"Demo job ready: {job.JobId}");
                onContentStatusMessageChanged.Invoke($"Demo job created: {job.JobId}");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Demo job creation failed: {exception}");
                onSyncStatusMessageChanged.Invoke($"Demo job creation failed: {exception.Message}");
            }
        }

        public async void LoadInstalledContentFromUi()
        {
            try
            {
                var loaded = await TryLoadInstalledContentAsync(true);
                onContentStatusMessageChanged.Invoke(loaded ? "Installed content loaded." : "No installed content package found.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Content load failed: {exception}");
                onContentStatusMessageChanged.Invoke($"Content load failed: {exception.Message}");
            }
        }

        public async void LoadContentSceneFromEntry(string entryId)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                return;
            }

            try
            {
                await ContentRuntimeLoader.LoadSceneAsync(entryId, LoadSceneMode.Single);
                AnalyticsService?.TrackFeatureUsed("content_scene_loaded", null, entryId);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Content scene load failed: {exception}");
                onContentStatusMessageChanged.Invoke($"Content scene load failed: {exception.Message}");
            }
        }

        public async Task<GameObject> InstantiateContentPrefabAsync(string entryId, Transform parent = null)
        {
            if (string.IsNullOrWhiteSpace(entryId))
            {
                throw new ArgumentException("Content entry id is required.", nameof(entryId));
            }

            var instance = await ContentRuntimeLoader.InstantiatePrefabAsync(entryId, parent);
            AnalyticsService?.TrackFeatureUsed($"content_prefab_instantiated:{entryId}");
            return instance;
        }

        public void TrackDownloadRequested(string jobId)
        {
            if (SessionService == null)
            {
                return;
            }

            try
            {
                var job = SessionService.GetJob(jobId);
                AnalyticsService?.TrackDownloadRequested(job.JobId, job.ThemeId);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Download request tracking failed: {exception.Message}");
            }
        }

        public void TrackDownloadConfirmed(string jobId, int totalDownloads)
        {
            if (SessionService == null)
            {
                return;
            }

            try
            {
                var job = SessionService.GetJob(jobId);
                AnalyticsService?.TrackDownloadConfirmed(job.JobId, job.ThemeId, totalDownloads);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Download confirm tracking failed: {exception.Message}");
            }
        }

        private async Task InitializeUnityGamingServicesAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                var options = new InitializationOptions();
                if (!string.IsNullOrWhiteSpace(unityServicesEnvironmentName))
                {
                    options.SetEnvironmentName(unityServicesEnvironmentName.Trim());
                }

                await UnityServices.InitializeAsync(options);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            AnalyticsService.MarkReady(startAnalyticsDataCollection);
        }

        private async Task EnsureRuntimeReadyForUiAsync()
        {
            if (SessionService != null)
            {
                return;
            }

            if (InitializationTask == null)
            {
                InitializationTask = InitializeAsync();
            }

            await InitializationTask;
        }

        private async Task RefreshContentAsync(bool autoDownload)
        {
            var snapshot = await ContentManagementService.RefreshRemoteManifestAsync();
            AnalyticsService?.TrackContentCheck(snapshot, ContentManagementService.LastRequestOrigin);

            if (!autoDownload || !snapshot.HasUpdateAvailable)
            {
                return;
            }

            var installStartedAt = Time.realtimeSinceStartup;
            snapshot = await ContentManagementService.DownloadAndInstallAsync();
            await TryLoadInstalledContentAsync(true);
            var installDurationSeconds = Mathf.Max(0, Mathf.RoundToInt(Time.realtimeSinceStartup - installStartedAt));
            AnalyticsService?.TrackContentInstalled(snapshot, installDurationSeconds);
        }

        private async Task<bool> TryLoadInstalledContentAsync(bool forceReload)
        {
            if (ContentRuntimeLoader == null)
            {
                return false;
            }

            var loaded = await ContentRuntimeLoader.LoadInstalledContentAsync(forceReload);
            if (loaded)
            {
                AnalyticsService?.TrackFeatureUsed("content_bundle_loaded");
            }

            return loaded;
        }

        private BoothBackendScaffoldConfig CreateBackendScaffoldConfig()
        {
            return new BoothBackendScaffoldConfig
            {
                DeviceId = string.IsNullOrWhiteSpace(backendDeviceId) ? boothId : backendDeviceId.Trim(),
                DeviceToken = backendDeviceToken,
                BoothApiBaseUrl = backendBoothApiBaseUrl,
                PublishApiBaseUrl = backendPublishApiBaseUrl,
                DownloadBaseUrl = backendDownloadBaseUrl,
                AssetUploadPathTemplate = backendAssetUploadPathTemplate,
                AssetRegistrationPathTemplate = backendAssetRegistrationPathTemplate,
                PublishPathTemplate = backendPublishPathTemplate,
                SeparateAssetRegistration = backendSeparateAssetRegistration,
                UploadThumbnail = backendUploadThumbnail,
                RequestTimeoutSeconds = backendRequestTimeoutSeconds
            };
        }

        private IBoothSyncClient CreateSyncClient(BoothBackendScaffoldConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config?.BoothApiBaseUrl))
            {
                return new HttpBoothSyncClient(config);
            }

            return new ScaffoldBoothSyncClient(config);
        }

        private PhotoBooth.Booth.Domain.BoothJob CreateDemoJob()
        {
            if (SessionService == null)
            {
                throw new InvalidOperationException("Session service is not initialized.");
            }

            var job = SessionService.CreateJob(Math.Max(0, demoAmountMinorUnits), "THB");
            job = SessionService.SelectTheme(job.JobId, string.IsNullOrWhiteSpace(demoThemeId) ? "demo_theme" : demoThemeId.Trim());
            job = SessionService.BypassPayment(job.JobId);
            job = SessionService.BeginCapture(job.JobId);
            job = SessionService.MarkCaptured(job.JobId, Math.Max(1, demoRawCaptureCount));
            job = SessionService.BeginComposing(job.JobId);

            var composedPath = Path.Combine(job.Paths.ComposedDirectory, "demo-composed.png");
            var thumbnailPath = Path.Combine(job.Paths.ThumbsDirectory, "demo-thumb.png");
            WriteDemoImage(composedPath, demoComposedImageSize.x, demoComposedImageSize.y, new Color(0.98f, 0.64f, 0.35f), new Color(0.19f, 0.44f, 0.92f));
            WriteDemoImage(thumbnailPath, demoThumbnailSize.x, demoThumbnailSize.y, new Color(0.22f, 0.18f, 0.16f), new Color(0.88f, 0.84f, 0.72f));

            job = SessionService.MarkComposed(job.JobId, composedPath, thumbnailPath);
            return job;
        }

        private string ResolveUiJobId(string jobId)
        {
            if (!string.IsNullOrWhiteSpace(jobId))
            {
                return jobId.Trim();
            }

            return lastDemoJobId;
        }

        private static void WriteDemoImage(string absolutePath, int width, int height, Color topColor, Color bottomColor)
        {
            width = Math.Max(16, width);
            height = Math.Max(16, height);

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                var pixels = new Color[width * height];
                for (var y = 0; y < height; y++)
                {
                    var t = height <= 1 ? 0f : y / (float)(height - 1);
                    var rowColor = Color.Lerp(bottomColor, topColor, t);
                    for (var x = 0; x < width; x++)
                    {
                        pixels[(y * width) + x] = rowColor;
                    }
                }

                texture.SetPixels(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.Destroy(texture);
            }
        }

        private ILocalRepository CreateRepository()
        {
            if (!preferSqliteRepository)
            {
                return new FileSystemLocalRepository(repositoryRootOverride);
            }

            try
            {
                var repository = new SqliteLocalRepository(repositoryRootOverride);
                repository.Initialize();
                return repository;
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"SQLite repository unavailable, falling back to file system repository: {exception.Message}");
                onContentStatusMessageChanged.Invoke("SQLite unavailable, using file repository fallback.");
                return new FileSystemLocalRepository(repositoryRootOverride);
            }
        }

        private void HandleContentSnapshotChanged(BoothContentSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            onContentStatusMessageChanged.Invoke(snapshot.Message ?? string.Empty);
            onInstalledContentVersionChanged.Invoke(snapshot.InstalledVersion ?? string.Empty);
            onRemoteContentVersionChanged.Invoke(snapshot.RemoteVersion ?? string.Empty);
            onContentUpdateAvailabilityChanged.Invoke(snapshot.HasUpdateAvailable || snapshot.ForceUpdate);
            onContentDownloadProgressChanged.Invoke(snapshot.DownloadProgress);
        }

        private void HandleActiveSceneChanged(Scene previousScene, Scene nextScene)
        {
            AnalyticsService?.EnterScene(nextScene.name);
        }

        private void OnDestroy()
        {
            SceneManager.activeSceneChanged -= HandleActiveSceneChanged;

            if (ContentManagementService != null)
            {
                ContentManagementService.SnapshotChanged -= HandleContentSnapshotChanged;
            }

            ContentRuntimeLoader?.Unload(false);
            AnalyticsService?.ExitScene();
            AnalyticsService?.Flush();
        }
    }
}
