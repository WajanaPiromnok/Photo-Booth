using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PhotoBooth.Booth.AR;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Services;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothFrontendController : MonoBehaviour
    {
        private const string MrKremeResourceRoot = "MrkremeUi/";
        private const float DefaultYuNetSunglassesScale = 1.25f;
        // MediaPipe FaceMesh 478-point landmark indices for face regions.
        private static readonly int[] FaceMarkJawIndices = { 10, 338, 297, 332, 284, 251, 389, 356, 454, 323, 361, 288, 397, 365, 379, 378, 400, 377, 152, 148, 176, 149, 150, 136, 172, 58, 132, 93, 234, 127, 162, 21, 54, 103, 67, 109, 10 };
        private static readonly int[] FaceMarkRightBrowIndices = { 46, 53, 52, 65, 55, 70, 63, 105, 66, 107 };
        private static readonly int[] FaceMarkLeftBrowIndices = { 276, 283, 282, 295, 285, 300, 293, 334, 296, 336 };
        private static readonly int[] FaceMarkNoseBridgeIndices = { 168, 6, 197, 195, 5 };
        private static readonly int[] FaceMarkNoseBaseIndices = { 48, 115, 220, 45, 4, 275, 440, 344, 278 };
        private static readonly int[] FaceMarkRightEyeIndices = { 33, 160, 158, 133, 153, 144, 33 };
        private static readonly int[] FaceMarkLeftEyeIndices = { 362, 385, 387, 263, 373, 380, 362 };
        private static readonly int[] FaceMarkOuterMouthIndices = { 61, 39, 37, 0, 267, 269, 291, 405, 314, 17, 84, 181, 61 };
        private static readonly int[] FaceMarkInnerMouthIndices = { 78, 82, 13, 312, 308, 317, 14, 87, 78 };
        private static readonly int[] CvFaceMarkJawIndices = { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        private static readonly int[] CvFaceMarkRightBrowIndices = { 17, 18, 19, 20, 21 };
        private static readonly int[] CvFaceMarkLeftBrowIndices = { 22, 23, 24, 25, 26 };
        private static readonly int[] CvFaceMarkNoseBridgeIndices = { 27, 28, 29, 30 };
        private static readonly int[] CvFaceMarkNoseBaseIndices = { 31, 32, 33, 34, 35 };
        private static readonly int[] CvFaceMarkRightEyeIndices = { 36, 37, 38, 39, 40, 41, 36 };
        private static readonly int[] CvFaceMarkLeftEyeIndices = { 42, 43, 44, 45, 46, 47, 42 };
        private static readonly int[] CvFaceMarkOuterMouthIndices = { 48, 49, 50, 51, 52, 53, 54, 55, 56, 57, 58, 59, 48 };
        private static readonly int[] CvFaceMarkInnerMouthIndices = { 60, 61, 62, 63, 64, 65, 66, 67, 60 };
        private static readonly Color MetalColor = new(0.75f, 0.79f, 0.78f, 1f);
        private static readonly Color MrkremeLime = new(0.78f, 0.9f, 0.05f, 1f);

        [Serializable]
        public sealed class ScreenBinding
        {
            public BoothUiScreenId screenId;
            public GameObject root;
        }

        [SerializeField] private BoothRuntimeBootstrap runtime;
        [SerializeField] private bool autoBuildUi = true;
        [SerializeField] private bool autoBootstrapInPhotoBoothScene = true;
        [SerializeField] private bool forcePortraitResolution = true;
        [SerializeField] private int countdownSeconds = 3;
        [SerializeField] private int motionClipFramesPerSecond = 4;
        [SerializeField] private bool enableArTracking = true;
        [SerializeField] private int maxArFaces = 4;
        [SerializeField] private int arPreviewUpdateIntervalMs = 33;
        [SerializeField] private bool mirrorArOverlayHorizontally;
        [SerializeField] private bool showOnlySunglassesSticker = true;
        [SerializeField] private bool showFaceMarkDebugLines = true;
        [SerializeField] private bool showArDebugTelemetry = true;
        [SerializeField] private bool showEditorTracked3dFaceGuidePreview = true;
        [SerializeField] private bool enableModelerFaceRig;
        [SerializeField] private bool showModelerFaceMeshWireframe;
        [SerializeField] private bool enableTracked3dFaceModel = true;
        [SerializeField] private bool showTracked3dFaceGuideModel = true;
        [SerializeField] private bool enableTracked3dFaceParts = true;
        [SerializeField] private Tracked3dFaceModelAlignmentMode tracked3dFaceGuideAlignmentMode = Tracked3dFaceModelAlignmentMode.CanonicalMatrix;
        [SerializeField] private float tracked3dFaceGuideScale = 2.35f;
        [SerializeField] private bool mirrorTracked3dFaceGuideX = true;
        [SerializeField] private bool invertTracked3dFaceGuideYaw = true;
        [SerializeField] private bool preserveTracked3dFaceGuideSceneTransform;
        [SerializeField] private Vector3 tracked3dFaceGuideLocalOffset = Vector3.zero;
        [SerializeField] private Vector3 tracked3dFaceGuideEulerOffset = new(0f, 180f, 0f);
        [SerializeField] private Vector3 tracked3dFaceGuideLocalScale = new(10f, 10f, 10f);
        [SerializeField] private float tracked3dFaceGuideNeutralNoseBlend = 0.38f;
        [SerializeField] private float tracked3dFaceGuideSideNoseBlend = 0.85f;
        [SerializeField] private float tracked3dFaceGuideFullSideYawDegrees = 35f;
        [SerializeField] private Vector2 tracked3dFaceGuideScreenOffsetByEyeDistance = Vector2.zero;
        [SerializeField] private GameObject faceModelPrefab;
        [SerializeField] private Tracked3dFacePartBinding[] tracked3dFaceParts = Array.Empty<Tracked3dFacePartBinding>();
        [SerializeField] private float faceMarkDebugLineThickness = 2f;
        [SerializeField] private Color faceMarkDebugLineColor = new(0f, 1f, 0.55f, 0.9f);
        [SerializeField] private Vector2 sunglassesScale = new(DefaultYuNetSunglassesScale, DefaultYuNetSunglassesScale);
        [SerializeField] private Vector2 sunglassesOffset;
        [SerializeField] private string ffmpegExecutablePath = "ffmpeg";
        [SerializeField] private int ffmpegTimeoutSeconds = 20;
        [SerializeField] private Vector2Int cameraCaptureSize = new(1280, 720);
        [SerializeField] private Vector2Int thumbnailSize = new(320, 180);
        [SerializeField] private ArStickerDefinition[] arStickers = ArStickerRenderer.CreateDefaultStickers();
        [SerializeField] private BoothThemeOption[] themes =
        {
            new() { themeId = "classic", displayName = "Classic Booth", priceMinorUnits = 12000, currencyCode = "THB" },
            new() { themeId = "pop", displayName = "Pop Color", priceMinorUnits = 15000, currencyCode = "THB" }
        };
        [SerializeField] private BoothAiStyleOption[] aiStyles =
        {
            new() { styleId = "natural", displayName = "Natural", aiPrompt = "Natural photo booth portrait", applyLocalStylizedPreview = false },
            new() { styleId = "neon_ai", displayName = "Neon AI", aiPrompt = "Neon cyberpunk portrait with glowing studio background", primaryColor = new Color(0.08f, 0.95f, 1f, 1f), secondaryColor = new Color(0.65f, 0.05f, 0.9f, 1f), applyLocalStylizedPreview = true },
            new() { styleId = "watercolor_backdrop", displayName = "Watercolor Backdrop", aiPrompt = "Soft watercolor portrait with dreamy illustrated background", primaryColor = new Color(1f, 0.78f, 0.55f, 1f), secondaryColor = new Color(0.42f, 0.72f, 1f, 1f), applyLocalStylizedPreview = true }
        };

        [SerializeField] private ScreenBinding[] screens = Array.Empty<ScreenBinding>();
        [SerializeField] private TextMeshProUGUI titleText;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI priceText;
        [SerializeField] private TextMeshProUGUI countdownText;
        [SerializeField] private TextMeshProUGUI aiStyleText;
        [SerializeField] private TextMeshProUGUI nameEntryText;
        [SerializeField] private TextMeshProUGUI downloadUrlText;
        [SerializeField] private TextMeshProUGUI printStatusText;
        [SerializeField] private TextMeshProUGUI arDebugText;
        [SerializeField] private RawImage cameraPreview;
        [SerializeField] private RawImage arModelOverlay;
        [SerializeField] private RawImage arPreviewOverlay;
        [SerializeField] private RawImage motionPreview;
        [SerializeField] private RawImage composedPreview;
        [SerializeField] private RawImage qrPreview;
        [SerializeField] private Button captureButton;
        [SerializeField] private Button retakeButton;
        [SerializeField] private Button continueButton;
        [SerializeField] private Button printButton;
        [SerializeField] private Button doneButton;

        private readonly BoothImageComposer composer = new();
        private readonly BoothFfmpegMotionEncoder motionEncoder = new();
        private readonly ArStickerRenderer arStickerRenderer = new();
        private readonly ArTrackingStabilizer arTrackingStabilizer = new();
        private BoothCameraCaptureService cameraCaptureService;
        private IArTrackingProvider arTrackingProvider;
        private ArTrackingFrame latestArTrackingFrame = ArTrackingFrame.Empty;
        private BoothFrontendAnalyticsClient backendAnalytics;
        private BoothUiScreenId currentScreen = BoothUiScreenId.Attract;
        private float currentScreenStartedAt;
        private BoothJob currentJob;
        private BoothThemeOption selectedTheme;
        private BoothAiStyleOption selectedAiStyle;
        private BoothCaptureClip latestMotionClip;
        private Texture2D motionPreviewTexture;
        private Texture2D composedPreviewTexture;
        private Texture2D qrPreviewTexture;
        private Texture2D arOverlayPreviewTexture;
        private readonly List<ArPreviewStickerObject> arPreviewStickerObjects = new();
        private readonly List<Image> arFaceMarkLineImages = new();
        private ArPreviewFaceAnchor arPreviewFaceAnchor;
        private ArPreviewFaceRig arPreviewFaceRig;
        private ArPreviewFaceModelRig arPreviewFaceModelRig;
        private readonly Dictionary<string, Texture2D> resourceTextureCache = new();
        private CancellationTokenSource flowCancellation;
        private CancellationTokenSource motionPlaybackCancellation;
        private CancellationTokenSource arPreviewCancellation;
        private bool isStartingCapturePreview;
        private bool loggedFirstArFrame;
        private int lastLoggedArFaceCount = -1;
        private bool mediaPipeArUnavailable;
        private string mediaPipeFallbackReason = string.Empty;
        private double lastArDebugTimestamp;
        private float arDebugFps;
        private bool isBusy;
        private int pendingThemeIndex = -1;
        private string passengerName = string.Empty;

#if UNITY_EDITOR
        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                return;
            }

            ScheduleEditorTracked3dFaceGuidePreview();
        }

        private void OnValidate()
        {
            if (Application.isPlaying)
            {
                return;
            }

            ScheduleEditorTracked3dFaceGuidePreview();
        }

        private void ScheduleEditorTracked3dFaceGuidePreview()
        {
            EditorApplication.delayCall -= ApplyEditorTracked3dFaceGuidePreview;
            EditorApplication.delayCall += ApplyEditorTracked3dFaceGuidePreview;
        }

        private void ApplyEditorTracked3dFaceGuidePreview()
        {
            EditorApplication.delayCall -= ApplyEditorTracked3dFaceGuidePreview;
            if (this == null || Application.isPlaying)
            {
                return;
            }

            arPreviewFaceModelRig = arPreviewOverlay != null
                ? arPreviewOverlay.GetComponentInChildren<ArPreviewFaceModelRig>(true)
                : GetComponentInChildren<ArPreviewFaceModelRig>(true);

            if (!enableTracked3dFaceModel || !showEditorTracked3dFaceGuidePreview)
            {
                arPreviewFaceModelRig?.Hide();
                return;
            }

            EnsureArPreviewOverlay();
            AlignArOverlaysToCameraPreview();
            var rig = EnsureArPreviewFaceModelRig(editorPreview: true);
            ApplyTracked3dFaceGuideSettings(rig);
            rig.ApplyEditorPreview();
        }
#endif

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoInstallInPhotoBoothScene()
        {
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || activeScene.name != "PhotoBooth")
            {
                return;
            }

            if (FindFirstObjectByType<BoothFrontendController>() != null)
            {
                return;
            }

            var host = new GameObject("BoothFrontendController");
            var controller = host.AddComponent<BoothFrontendController>();
            controller.autoBuildUi = true;
            controller.autoBootstrapInPhotoBoothScene = true;
        }

        private void Awake()
        {
            if (!autoBootstrapInPhotoBoothScene && runtime == null)
            {
                enabled = false;
                return;
            }

            runtime ??= FindFirstObjectByType<BoothRuntimeBootstrap>();
            EnsureDefaultThemes();
            EnsureDefaultAiStyles();
            EnsureDefaultArStickers();
            UpgradeLegacySunglassesScale();
            ApplyPortraitResolution();

            if (autoBuildUi)
            {
                BuildRuntimeUi();
            }

            BindUiEvents();
            flowCancellation = new CancellationTokenSource();
            SwitchScreen(BoothUiScreenId.Attract, "Touch start to begin.");
        }

        private void ApplyPortraitResolution()
        {
            if (!forcePortraitResolution)
            {
                return;
            }

            Screen.orientation = ScreenOrientation.Portrait;
            if (Screen.width != 1080 || Screen.height != 1920)
            {
                Screen.SetResolution(1080, 1920, FullScreenMode.Windowed);
            }
        }

        private async void Start()
        {
            if (runtime == null)
            {
                ShowError("Booth runtime was not found in this scene.");
                return;
            }

            try
            {
                await runtime.EnsureReadyAsync();
                backendAnalytics = new BoothFrontendAnalyticsClient(
                    runtime.BackendBoothApiBaseUrl,
                    runtime.BackendDeviceId,
                    runtime.BackendDeviceToken,
                    runtime.BackendRequestTimeoutSeconds);
                SetStatus("Booth ready.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Frontend runtime initialization failed: {exception}");
                ShowError($"Runtime failed: {exception.Message}");
            }
        }

        [ContextMenu("Build Editable MRKREME UI In Scene")]
        public void BuildEditableUiInScene()
        {
            autoBuildUi = false;
            ClearBuiltUi();
            BuildRuntimeUi();
            BindUiEvents();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        public async void StartSessionFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                await runtime.EnsureReadyAsync();
                currentJob = null;
                selectedTheme = null;
                selectedAiStyle = null;
                pendingThemeIndex = -1;
                passengerName = string.Empty;
                UpdateNameEntryDisplay();
                latestMotionClip = null;
                StopMotionClipPlayback();
                ClearPreview(motionPreview, ref motionPreviewTexture);
                ClearPreview(composedPreview, ref composedPreviewTexture);
                ClearPreview(qrPreview, ref qrPreviewTexture);
                await TrackAsync("booth_frontend_session_started");
                SwitchScreen(BoothUiScreenId.ThemeSelect, "Choose your frame.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Start session failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public void ChooseThemeCandidateFromUi(int themeIndex)
        {
            pendingThemeIndex = themeIndex;
            var theme = ResolveTheme(themeIndex);
            SetStatus($"Selected frame: {theme.displayName}. Tap confirm to continue.");
            priceText?.SetText(FormatPrice(theme));
        }

        public void ConfirmThemeSelectionFromUi()
        {
            if (pendingThemeIndex < 0)
            {
                SetStatus("Choose a frame first.");
                return;
            }

            SelectThemeFromUi(pendingThemeIndex);
        }

        public async void SelectThemeFromUi(int themeIndex)
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                selectedTheme = ResolveTheme(themeIndex);
                currentJob = runtime.SessionService.CreateJob(selectedTheme.priceMinorUnits, selectedTheme.currencyCode);
                currentJob = runtime.SessionService.SelectTheme(currentJob.JobId, selectedTheme.themeId);
                selectedAiStyle = ResolveAiStyle(0);
                currentJob = runtime.SessionService.SelectAiStyle(currentJob.JobId, selectedAiStyle.styleId, selectedAiStyle.aiPrompt);
                priceText?.SetText(FormatPrice(selectedTheme));
                await TrackAsync("booth_frontend_theme_selected");
                runtime.TrackFeatureUsed($"theme_selected:{selectedTheme.themeId}");
                SwitchScreen(BoothUiScreenId.PaymentMock, $"Selected {selectedTheme.displayName}. Choose payment.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Theme selection failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void SelectAiStyleFromUi(int styleIndex)
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                selectedAiStyle = ResolveAiStyle(styleIndex);
                currentJob = runtime.SessionService.SelectAiStyle(currentJob.JobId, selectedAiStyle.styleId, selectedAiStyle.aiPrompt);
                aiStyleText?.SetText($"{selectedAiStyle.displayName}\n{selectedAiStyle.aiPrompt}");
                await TrackAsync("booth_frontend_ai_style_selected", metadata: BuildAiMetadata());
                SwitchScreen(BoothUiScreenId.PaymentMock, $"AI style: {selectedAiStyle.displayName}. Local preview now, backend AI later.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"AI style selection failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void PayMockFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJobOrCreateQuickDemo(paymentConfirmed: false);
                currentJob = runtime.SessionService.SetPaymentPending(currentJob.JobId, $"MOCK-{currentJob.JobId}");
                currentJob = runtime.SessionService.ConfirmPayment(currentJob.JobId, $"MOCK-{currentJob.JobId}");
                await TrackAsync("booth_frontend_mock_payment_confirmed", metadata: BuildAiMetadata());
                UpdateNameEntryDisplay();
                SwitchScreen(BoothUiScreenId.ArtStyleSelect, "Type your name.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Mock payment failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public void AppendNameCharacterFromUi(string character)
        {
            if (string.IsNullOrWhiteSpace(character) || passengerName.Length >= 15)
            {
                return;
            }

            passengerName += character.Trim().ToUpperInvariant();
            UpdateNameEntryDisplay();
        }

        public void BackspaceNameFromUi()
        {
            if (string.IsNullOrEmpty(passengerName))
            {
                return;
            }

            passengerName = passengerName[..^1];
            UpdateNameEntryDisplay();
        }

        public async void ConfirmNameFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                passengerName = string.IsNullOrWhiteSpace(passengerName) ? "PASSENGER" : passengerName.Trim().ToUpperInvariant();
                UpdateNameEntryDisplay();
                currentJob = runtime.SessionService.SetPassengerName(currentJob.JobId, passengerName);
                await TrackAsync("booth_frontend_name_confirmed", metadata: new Dictionary<string, string> { ["passenger_name"] = passengerName });
                await ShowCaptureAsync("Ready to capture.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Name entry failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        private void UpdateNameEntryDisplay()
        {
            nameEntryText?.SetText(passengerName);
            aiStyleText?.SetText($"{passengerName.Length} / 15");
        }

        public async void CaptureFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJobOrCreateQuickDemo(paymentConfirmed: true);
                if (currentJob.Status == BoothJobStatus.PaymentConfirmed || currentJob.Status == BoothJobStatus.PaymentBypassed)
                {
                    currentJob = runtime.SessionService.BeginCapture(currentJob.JobId);
                }

                cameraCaptureService ??= new BoothCameraCaptureService(cameraPreview, cameraCaptureSize.x, cameraCaptureSize.y);
                await cameraCaptureService.StartPreviewAsync(flowCancellation.Token);
                await StartArPreviewAsync(flowCancellation.Token);
                latestMotionClip = await RecordCountdownMotionClipAsync();

                countdownText?.SetText("Smile!");
                var captureTrackingFrame = await TrackCurrentFrameAsync(flowCancellation.Token);
                var rawPath = cameraCaptureService.CapturePng(currentJob.Paths.RawDirectory, "capture.png");
                currentJob = runtime.SessionService.MarkCaptured(currentJob.JobId, 1, rawPath, latestMotionClip.FramePaths, latestMotionClip.VideoPath);
                currentJob = runtime.SessionService.BeginComposing(currentJob.JobId);
                var composition = composer.Compose(currentJob, rawPath, thumbnailSize, selectedAiStyle, texture => ApplyArStickers(texture, captureTrackingFrame));
                currentJob = runtime.SessionService.MarkComposed(currentJob.JobId, composition.ComposedImagePath, composition.ThumbnailPath);

                cameraCaptureService.StopPreview();
                LoadPreviewImage(currentJob.Paths.ComposedImagePath);
                await TrackAsync("booth_frontend_capture_completed", metadata: BuildAiMetadata());
                SwitchScreen(BoothUiScreenId.Preview, "Motion preview and styled capture ready. Continue or retake.");
                StartMotionClipPlayback(latestMotionClip);
            }
            catch (Exception exception)
            {
                Debug.LogError($"Capture failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                countdownText?.SetText(string.Empty);
                EndBusy();
            }
        }

        public async void RetakeFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                currentJob = runtime.SessionService.BeginRetake(currentJob.JobId);
                latestMotionClip = null;
                StopMotionClipPlayback();
                ClearPreview(motionPreview, ref motionPreviewTexture);
                ClearPreview(composedPreview, ref composedPreviewTexture);
                await TrackAsync("booth_frontend_retake_tapped", metadata: BuildAiMetadata());
                await ShowCaptureAsync("Retake ready.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Retake failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void ContinueFromPreviewFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                SwitchScreen(BoothUiScreenId.Fulfillment, "Syncing photo...");
                await TrackAsync("booth_frontend_sync_started", metadata: BuildAiMetadata());
                currentJob = await runtime.SyncService.SyncAsync(currentJob.JobId, flowCancellation.Token);
                downloadUrlText?.SetText(BuildDownloadSummary(currentJob));
                runtime.TrackDownloadRequested(currentJob.JobId);
                await TrackAsync("booth_frontend_sync_completed", metadata: BuildAiMetadata());
                await TrackAsync("booth_frontend_download_link_shown", metadata: BuildAiMetadata());
                await LoadQrPreviewAsync(currentJob.DownloadUrl);
                SetStatus("Download link ready. Print is simulated in this MVP.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Fulfillment failed: {exception}");
                var metadata = BuildAiMetadata();
                metadata["error"] = exception.Message;
                await TrackAsync("booth_frontend_sync_failed", metadata: metadata);
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void PrintFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                SwitchScreen(BoothUiScreenId.Printing, "Printing is simulated for this MVP.");
                await TrackAsync("booth_frontend_print_requested");
                currentJob = await runtime.PrintService.PrintAsync(currentJob.JobId, "Simulated Photo Booth Printer", 1, flowCancellation.Token);
                printStatusText?.SetText($"Simulated print: {currentJob.PrintStatus}");
                await TrackAsync("booth_frontend_print_completed");
                SwitchScreen(BoothUiScreenId.Done, "Session complete.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Print failed: {exception}");
                await TrackAsync("booth_frontend_print_failed", metadata: new Dictionary<string, string> { ["error"] = exception.Message });
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void DoneFromUi()
        {
            try
            {
                if (currentJob != null && currentJob.Status == BoothJobStatus.LinkReady)
                {
                    currentJob = runtime.SessionService.MarkDone(currentJob.JobId);
                    await TrackAsync("booth_frontend_session_completed");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Done tracking failed: {exception.Message}");
            }

            SwitchScreen(BoothUiScreenId.Done, "Session complete.");
        }

        public void ResetFromUi()
        {
            currentJob = null;
            selectedTheme = null;
            selectedAiStyle = null;
            pendingThemeIndex = -1;
            passengerName = string.Empty;
            latestMotionClip = null;
            cameraCaptureService?.StopPreview();
            StopMotionClipPlayback();
            ClearPreview(motionPreview, ref motionPreviewTexture);
            ClearPreview(composedPreview, ref composedPreviewTexture);
            ClearPreview(qrPreview, ref qrPreviewTexture);
            UpdateNameEntryDisplay();
            downloadUrlText?.SetText(string.Empty);
            printStatusText?.SetText(string.Empty);
            SwitchScreen(BoothUiScreenId.Attract, "Touch start to begin.");
        }

        private async Task ShowCaptureAsync(string message)
        {
            SwitchScreen(BoothUiScreenId.Capture, message);
            cameraCaptureService ??= new BoothCameraCaptureService(cameraPreview, cameraCaptureSize.x, cameraCaptureSize.y);
            await cameraCaptureService.StartPreviewAsync(flowCancellation.Token);
            await StartArPreviewAsync(flowCancellation.Token);
            countdownText?.SetText(string.Empty);
        }

        private async Task<BoothCaptureClip> RecordCountdownMotionClipAsync()
        {
            var framePaths = new List<string>();
            var framesPerSecond = Mathf.Clamp(motionClipFramesPerSecond, 1, 8);
            var totalFrames = Mathf.Max(1, countdownSeconds) * framesPerSecond;
            var frameDelayMs = Mathf.RoundToInt(1000f / framesPerSecond);

            for (var frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                var remaining = Mathf.Max(1, Mathf.CeilToInt((totalFrames - frameIndex) / (float)framesPerSecond));
                countdownText?.SetText(remaining.ToString());
                var frame = await TrackCurrentFrameAsync(flowCancellation.Token);
                framePaths.Add(cameraCaptureService.CaptureMotionFramePng(currentJob.Paths.RawDirectory, frameIndex, texture => ApplyArStickers(texture, frame)));
                await Task.Delay(frameDelayMs, flowCancellation.Token);
            }

            var videoPath = await EncodeMotionVideoAsync(framePaths.ToArray(), framesPerSecond, flowCancellation.Token);
            return new BoothCaptureClip
            {
                FramePaths = framePaths.ToArray(),
                FrameRate = framesPerSecond,
                VideoPath = videoPath
            };
        }

        private async Task StartArPreviewAsync(CancellationToken cancellationToken)
        {
            Debug.Log($"PhotoBooth AR preview requested. enabled={enableArTracking}, hasCamera={cameraCaptureService != null}, isPreviewing={cameraCaptureService?.IsPreviewing.ToString() ?? "false"}, alreadyRunning={arPreviewCancellation != null}");
            if (!enableArTracking || cameraCaptureService == null || arPreviewCancellation != null)
            {
                return;
            }

            EnsureArPreviewOverlay();
            await EnsureArTrackingProviderAsync(cancellationToken);
            arPreviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _ = RunArPreviewLoopAsync(arPreviewCancellation.Token);
        }

        private async Task RunArPreviewLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && cameraCaptureService != null && cameraCaptureService.IsPreviewing)
            {
                try
                {
                    var frame = await TrackCurrentFrameAsync(cancellationToken);
                    UpdateArPreviewOverlay(frame);
                    await Task.Delay(Mathf.Clamp(arPreviewUpdateIntervalMs, 16, 250), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"AR preview update failed: {exception.Message}");
                    latestArTrackingFrame = ArTrackingFrame.Empty;
                    UpdateArPreviewOverlay(latestArTrackingFrame);
                    await Task.Delay(250, cancellationToken);
                }
            }
        }

        private async Task<ArTrackingFrame> TrackCurrentFrameAsync(CancellationToken cancellationToken)
        {
            if (!enableArTracking || cameraCaptureService == null || !cameraCaptureService.IsPreviewing)
            {
                arTrackingStabilizer.Reset();
                latestArTrackingFrame = ArTrackingFrame.Empty;
                return latestArTrackingFrame;
            }

            await EnsureArTrackingProviderAsync(cancellationToken);
            var frameTexture = cameraCaptureService.CaptureCurrentFrameTexture();
            try
            {
                var rawFrame = await arTrackingProvider.TrackAsync(frameTexture, cancellationToken) ?? ArTrackingFrame.Empty;
                latestArTrackingFrame = arTrackingStabilizer.Update(rawFrame);
                var faceCount = latestArTrackingFrame.Faces?.Length ?? 0;
                if (!loggedFirstArFrame || faceCount != lastLoggedArFaceCount)
                {
                    loggedFirstArFrame = true;
                    lastLoggedArFaceCount = faceCount;
                    Debug.Log($"PhotoBooth AR frame: provider={latestArTrackingFrame.ProviderName}, faces={faceCount}, size={latestArTrackingFrame.PixelWidth}x{latestArTrackingFrame.PixelHeight}");
                }

                return latestArTrackingFrame;
            }
            finally
            {
                Destroy(frameTexture);
            }
        }

        private async Task EnsureArTrackingProviderAsync(CancellationToken cancellationToken)
        {
            if (arTrackingProvider != null)
            {
                if (!arTrackingProvider.IsRunning)
                {
                    Debug.Log($"PhotoBooth AR starting provider: {arTrackingProvider.ProviderName}");
                    await arTrackingProvider.StartAsync(cancellationToken);
                    Debug.Log($"PhotoBooth AR provider running: {arTrackingProvider.ProviderName}");
                }

                return;
            }

            if (!mediaPipeArUnavailable
                && await TryStartArTrackingProviderAsync(new MediaPipeFaceLandmarkerTrackingProvider(maxArFaces), cancellationToken))
            {
                return;
            }

            var fallbackProvider = new MediaPipeFaceTrackingProvider(maxArFaces);
            if (!await TryStartArTrackingProviderAsync(fallbackProvider, cancellationToken))
            {
                fallbackProvider.Dispose();
                throw new InvalidOperationException("No AR tracking provider could be started.");
            }
        }

        private async Task<bool> TryStartArTrackingProviderAsync(IArTrackingProvider provider, CancellationToken cancellationToken)
        {
            try
            {
                Debug.Log($"PhotoBooth AR starting provider: {provider.ProviderName}");
                await provider.StartAsync(cancellationToken);
                arTrackingProvider = provider;
                Debug.Log($"PhotoBooth AR provider running: {provider.ProviderName}");
                return true;
            }
            catch (OperationCanceledException)
            {
                provider.Dispose();
                throw;
            }
            catch (Exception exception)
            {
                if (provider is MediaPipeFaceLandmarkerTrackingProvider)
                {
                    mediaPipeArUnavailable = true;
                    mediaPipeFallbackReason = $"{exception.GetType().Name}: {exception.Message}";
                    Debug.LogWarning($"PhotoBooth AR MediaPipe provider unavailable. Falling back to OpenCVForUnity. {mediaPipeFallbackReason}");
                    Debug.Log($"PhotoBooth AR MediaPipe fallback detail:\n{exception}");
                }
                else
                {
                    Debug.LogWarning($"PhotoBooth AR provider failed to start: {provider.ProviderName}. {exception.Message}");
                }

                provider.Dispose();
                return false;
            }
        }

        private void UpdateArPreviewOverlay(ArTrackingFrame frame)
        {
            if (arPreviewOverlay == null || cameraCaptureService == null || !cameraCaptureService.IsPreviewing)
            {
                return;
            }

            AlignArOverlaysToCameraPreview();

            if (frame?.Faces == null || frame.Faces.Length == 0)
            {
                ClearArPreviewOverlay();
                UpdateArPreviewFaceAnchor(frame, cameraCaptureService.CurrentWidth, cameraCaptureService.CurrentHeight);
                UpdateArDebugTelemetry(frame);
                return;
            }

            ClearPreview(arPreviewOverlay, ref arOverlayPreviewTexture);
            arPreviewOverlay.color = Color.clear;
            var renderItems = arStickerRenderer.BuildRenderItems(
                frame,
                ResolveArStickers(),
                cameraCaptureService.CurrentWidth,
                cameraCaptureService.CurrentHeight,
                mirrorArOverlayHorizontally);
            UpdateFaceMarkDebugLines(frame, cameraCaptureService.CurrentWidth, cameraCaptureService.CurrentHeight);
            UpdateArPreviewFaceAnchor(frame, cameraCaptureService.CurrentWidth, cameraCaptureService.CurrentHeight);
            UpdateArPreviewStickerObjects(renderItems, cameraCaptureService.CurrentWidth, cameraCaptureService.CurrentHeight);
            UpdateArDebugTelemetry(frame);
        }

        private void ApplyArStickers(Texture2D texture, ArTrackingFrame frame)
        {
            if (!enableArTracking || texture == null)
            {
                return;
            }

            ApplyTracked3dFaceModelToTexture(texture, frame);
            arStickerRenderer.ApplyToTexture(texture, frame, ResolveArStickers(), mirrorArOverlayHorizontally);
        }

        private void ApplyTracked3dFaceModelToTexture(Texture2D texture, ArTrackingFrame frame)
        {
            if ((!enableTracked3dFaceModel && !HasTracked3dFaceParts())
                || texture == null
                || frame?.Faces == null
                || frame.Faces.Length == 0)
            {
                return;
            }

            var rig = EnsureArPreviewFaceModelRig(editorPreview: false);
            ApplyTracked3dFaceGuideSettings(rig);
            rig.CompositeFaceModel(texture, frame.Faces[0]);
        }

        private async Task<string> EncodeMotionVideoAsync(string[] framePaths, float frameRate, CancellationToken cancellationToken)
        {
            if (framePaths == null || framePaths.Length == 0 || currentJob?.Paths == null)
            {
                return null;
            }

            var outputPath = Path.Combine(currentJob.Paths.RawDirectory, "motion.mp4");
            var result = await motionEncoder.EncodeAsync(ffmpegExecutablePath, framePaths, outputPath, frameRate, ffmpegTimeoutSeconds, cancellationToken);
            if (result.Success)
            {
                return result.VideoPath;
            }

            Debug.LogWarning($"Motion MP4 encoding failed; keeping PNG frame clip fallback. {result.ErrorMessage}");
            SetStatus("Motion video fallback: frame clip will be used.");
            return null;
        }

        private void StopArPreview()
        {
            if (arPreviewCancellation != null)
            {
                arPreviewCancellation.Cancel();
                arPreviewCancellation.Dispose();
                arPreviewCancellation = null;
            }

            arTrackingProvider?.Stop();
            arTrackingStabilizer.Reset();
            latestArTrackingFrame = ArTrackingFrame.Empty;
            loggedFirstArFrame = false;
            lastLoggedArFaceCount = -1;
            lastArDebugTimestamp = 0d;
            arDebugFps = 0f;
            mediaPipeFallbackReason = string.Empty;
            ClearArPreviewOverlay();
        }

        private void SwitchScreen(BoothUiScreenId screenId, string statusMessage)
        {
            TrackScreenExit();

            foreach (var binding in screens)
            {
                if (binding?.root != null)
                {
                    binding.root.SetActive(binding.screenId == screenId);
                }
            }

            if (screenId != BoothUiScreenId.Capture)
            {
                StopArPreview();
                cameraCaptureService?.StopPreview();
            }

            if (screenId != BoothUiScreenId.Preview)
            {
                StopMotionClipPlayback();
            }

            currentScreen = screenId;
            currentScreenStartedAt = Time.realtimeSinceStartup;
            titleText?.SetText(ScreenTitle(screenId));
            SetStatus(statusMessage);
            _ = TrackAsync("booth_frontend_screen_entered", screenIdOverride: screenId);
            if (screenId == BoothUiScreenId.Capture)
            {
                _ = EnsureCapturePreviewStartedAsync();
            }
        }

        private void TrackScreenExit()
        {
            if (currentScreenStartedAt <= 0f)
            {
                return;
            }

            var durationSeconds = Mathf.Max(0, Mathf.RoundToInt(Time.realtimeSinceStartup - currentScreenStartedAt));
            _ = TrackAsync("booth_frontend_screen_exited", screenIdOverride: currentScreen, durationSeconds: durationSeconds);
        }

        private async Task TrackAsync(
            string eventName,
            BoothUiScreenId? screenIdOverride = null,
            int? durationSeconds = null,
            IReadOnlyDictionary<string, string> metadata = null)
        {
            runtime?.AnalyticsService?.TrackFeatureUsed(eventName, selectedTheme?.themeId, screenIdOverride?.ToString());
            if (backendAnalytics == null)
            {
                return;
            }

            await backendAnalytics.TrackAsync(
                eventName,
                currentJob?.JobId,
                selectedTheme?.themeId,
                screenIdOverride ?? currentScreen,
                durationSeconds,
                metadata,
                flowCancellation?.Token ?? CancellationToken.None);
        }

        private void StartMotionClipPlayback(BoothCaptureClip clip)
        {
            StopMotionClipPlayback();
            if (motionPreview == null || clip?.FramePaths == null || clip.FramePaths.Length == 0)
            {
                return;
            }

            motionPlaybackCancellation = new CancellationTokenSource();
            _ = PlayMotionClipLoopAsync(clip, motionPlaybackCancellation.Token);
        }

        private async Task PlayMotionClipLoopAsync(BoothCaptureClip clip, CancellationToken cancellationToken)
        {
            try
            {
                var delayMs = Mathf.RoundToInt(1000f / Mathf.Max(1f, clip.FrameRate));
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var framePath in clip.FramePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(framePath) && File.Exists(framePath))
                        {
                            LoadMotionFrame(framePath);
                        }

                        await Task.Delay(delayMs, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void StopMotionClipPlayback()
        {
            if (motionPlaybackCancellation == null)
            {
                return;
            }

            motionPlaybackCancellation.Cancel();
            motionPlaybackCancellation.Dispose();
            motionPlaybackCancellation = null;
        }

        private void LoadMotionFrame(string framePath)
        {
            ClearPreview(motionPreview, ref motionPreviewTexture);
            if (motionPreview == null)
            {
                return;
            }

            motionPreviewTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(motionPreviewTexture, File.ReadAllBytes(framePath));
            motionPreview.texture = motionPreviewTexture;
            motionPreview.color = Color.white;
        }

        private void LoadPreviewImage(string imagePath)
        {
            ClearPreview(composedPreview, ref composedPreviewTexture);
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath) || composedPreview == null)
            {
                return;
            }

            composedPreviewTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(composedPreviewTexture, File.ReadAllBytes(imagePath));
            composedPreview.texture = composedPreviewTexture;
            composedPreview.color = Color.white;
        }

        private async Task LoadQrPreviewAsync(string downloadUrl)
        {
            ClearPreview(qrPreview, ref qrPreviewTexture);
            if (string.IsNullOrWhiteSpace(downloadUrl) || qrPreview == null)
            {
                return;
            }

            using var request = UnityWebRequestTexture.GetTexture($"{downloadUrl.TrimEnd('/')}/qr");
            request.timeout = Math.Max(1, runtime.BackendRequestTimeoutSeconds);
            request.disposeDownloadHandlerOnDispose = false;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"QR preview failed: {request.responseCode} {request.error}");
                SetStatus("Download link ready. QR preview failed, use the URL text.");
                return;
            }

            qrPreviewTexture = DownloadHandlerTexture.GetContent(request);
            qrPreview.texture = qrPreviewTexture;
            qrPreview.color = Color.white;
        }

        private async Task<bool> BeginBusyAsync()
        {
            if (isBusy)
            {
                return false;
            }

            isBusy = true;
            SetInteractable(false);
            if (runtime != null)
            {
                await runtime.EnsureReadyAsync();
            }

            return true;
        }

        private void EndBusy()
        {
            isBusy = false;
            SetInteractable(true);
        }

        private void SetInteractable(bool interactable)
        {
            if (captureButton != null) captureButton.interactable = interactable;
            if (retakeButton != null) retakeButton.interactable = interactable;
            if (continueButton != null) continueButton.interactable = interactable;
            if (printButton != null) printButton.interactable = interactable;
            if (doneButton != null) doneButton.interactable = interactable;
        }

        private void ShowError(string message)
        {
            SwitchScreen(BoothUiScreenId.Error, $"Error: {message}");
        }

        private void SetStatus(string message)
        {
            if (statusText != null)
            {
                statusText.color = currentScreen == BoothUiScreenId.Error || currentScreen == BoothUiScreenId.Operator
                    ? new Color(0.08f, 0.08f, 0.08f, 0.95f)
                    : new Color(0f, 0f, 0f, 0f);
            }

            statusText?.SetText(message ?? string.Empty);
        }

        private void EnsureCurrentJob()
        {
            if (currentJob == null)
            {
                throw new InvalidOperationException("No active booth job.");
            }
        }

        private void EnsureCurrentJobOrCreateQuickDemo(bool paymentConfirmed)
        {
            if (currentJob != null)
            {
                return;
            }

            EnsureDefaultThemes();
            EnsureDefaultAiStyles();
            selectedTheme ??= themes[0];
            selectedAiStyle ??= aiStyles[0];
            currentJob = runtime.SessionService.CreateJob(selectedTheme.priceMinorUnits, selectedTheme.currencyCode);
            currentJob = runtime.SessionService.SelectTheme(currentJob.JobId, selectedTheme.themeId);
            currentJob = runtime.SessionService.SelectAiStyle(currentJob.JobId, selectedAiStyle.styleId, selectedAiStyle.aiPrompt);
            if (paymentConfirmed)
            {
                currentJob = runtime.SessionService.BypassPayment(currentJob.JobId);
            }

            if (string.IsNullOrWhiteSpace(passengerName))
            {
                passengerName = "PASSENGER";
                UpdateNameEntryDisplay();
            }
        }

        private async Task EnsureCapturePreviewStartedAsync()
        {
            if (isStartingCapturePreview || currentScreen != BoothUiScreenId.Capture)
            {
                return;
            }

            isStartingCapturePreview = true;
            try
            {
                Debug.Log("PhotoBooth capture preview start requested.");
                cameraCaptureService ??= new BoothCameraCaptureService(cameraPreview, cameraCaptureSize.x, cameraCaptureSize.y);
                await cameraCaptureService.StartPreviewAsync(flowCancellation?.Token ?? CancellationToken.None);
                Debug.Log($"PhotoBooth capture preview running: {cameraCaptureService.IsPreviewing}, texture={cameraCaptureService.CurrentWidth}x{cameraCaptureService.CurrentHeight}");
                await StartArPreviewAsync(flowCancellation?.Token ?? CancellationToken.None);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Capture preview could not start: {exception.Message}");
                SetStatus("Camera preview unavailable. Using fallback when possible.");
            }
            finally
            {
                isStartingCapturePreview = false;
            }
        }

        private BoothThemeOption ResolveTheme(int index)
        {
            EnsureDefaultThemes();
            if (index < 0 || index >= themes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Theme index is out of range.");
            }

            return themes[index];
        }

        private BoothAiStyleOption ResolveAiStyle(int index)
        {
            EnsureDefaultAiStyles();
            if (index < 0 || index >= aiStyles.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "AI style index is out of range.");
            }

            return aiStyles[index];
        }

        private void EnsureDefaultThemes()
        {
            if (themes != null && themes.Length > 0)
            {
                return;
            }

            themes = new[]
            {
                new BoothThemeOption { themeId = "classic", displayName = "Classic Booth", priceMinorUnits = 12000, currencyCode = "THB" },
                new BoothThemeOption { themeId = "pop", displayName = "Pop Color", priceMinorUnits = 15000, currencyCode = "THB" }
            };
        }

        private void EnsureDefaultAiStyles()
        {
            if (aiStyles != null && aiStyles.Length > 0)
            {
                return;
            }

            aiStyles = new[]
            {
                new BoothAiStyleOption { styleId = "natural", displayName = "Natural", aiPrompt = "Natural photo booth portrait", applyLocalStylizedPreview = false },
                new BoothAiStyleOption { styleId = "neon_ai", displayName = "Neon AI", aiPrompt = "Neon cyberpunk portrait with glowing studio background", primaryColor = new Color(0.08f, 0.95f, 1f, 1f), secondaryColor = new Color(0.65f, 0.05f, 0.9f, 1f), applyLocalStylizedPreview = true },
                new BoothAiStyleOption { styleId = "watercolor_backdrop", displayName = "Watercolor Backdrop", aiPrompt = "Soft watercolor portrait with dreamy illustrated background", primaryColor = new Color(1f, 0.78f, 0.55f, 1f), secondaryColor = new Color(0.42f, 0.72f, 1f, 1f), applyLocalStylizedPreview = true }
            };
        }

        private void EnsureDefaultArStickers()
        {
            if (arStickers != null && arStickers.Length > 0)
            {
                return;
            }

            arStickers = ArStickerRenderer.CreateDefaultStickers();
        }

        private ArStickerDefinition[] ResolveArStickers()
        {
            EnsureDefaultArStickers();
            ApplySunglassesInspectorOverrides();
            return arStickers;
        }

        private void ApplySunglassesInspectorOverrides()
        {
            if (arStickers == null)
            {
                return;
            }

            foreach (var sticker in arStickers)
            {
                if (sticker == null || sticker.builtinShape != ArStickerBuiltinShape.Sunglasses)
                {
                    if (showOnlySunglassesSticker
                        && sticker != null
                        && (sticker.builtinShape == ArStickerBuiltinShape.Crown || sticker.builtinShape == ArStickerBuiltinShape.Mustache))
                    {
                        sticker.enabled = false;
                    }

                    continue;
                }

                sticker.sizeScale = new Vector2(Mathf.Max(0.05f, sunglassesScale.x), Mathf.Max(0.05f, sunglassesScale.y));
                sticker.normalizedOffset = sunglassesOffset;
            }
        }

        private void UpgradeLegacySunglassesScale()
        {
            if (sunglassesScale.x > 0.5f || sunglassesScale.y > 0.5f)
            {
                return;
            }

            sunglassesScale = new Vector2(DefaultYuNetSunglassesScale, DefaultYuNetSunglassesScale);
        }

        private void EnsureArPreviewOverlay()
        {
            if (cameraPreview == null)
            {
                return;
            }

            if (arPreviewOverlay == null)
            {
                var existing = cameraPreview.transform.parent != null
                    ? cameraPreview.transform.parent.Find("ArPreviewOverlay")
                    : null;
                arPreviewOverlay = existing != null && existing.TryGetComponent<RawImage>(out var existingOverlay)
                    ? existingOverlay
                    : CreateRawImage(cameraPreview.transform.parent, "ArPreviewOverlay", Vector2.one * 0.5f, Vector2.zero, cameraPreview.rectTransform.sizeDelta);
            }

            arPreviewOverlay.color = Color.clear;
            arPreviewOverlay.raycastTarget = false;
            AlignArOverlaysToCameraPreview();
        }

        private void ClearArPreviewOverlay()
        {
            ClearPreview(arPreviewOverlay, ref arOverlayPreviewTexture);
            if (arPreviewOverlay != null)
            {
                arPreviewOverlay.color = Color.clear;
            }

            HideArPreviewStickerObjects(0);
            HideFaceMarkDebugLines(0);
            arPreviewFaceAnchor?.Hide();
            arPreviewFaceRig?.Hide();
            arPreviewFaceModelRig?.Hide();
        }

        private void UpdateArPreviewFaceAnchor(ArTrackingFrame frame, int sourceWidth, int sourceHeight)
        {
            if (arPreviewOverlay == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                arPreviewFaceAnchor?.Hide();
                return;
            }

            var face = frame?.Faces != null && frame.Faces.Length > 0 ? frame.Faces[0] : null;
            if (face == null)
            {
                arPreviewFaceAnchor?.Hide();
                return;
            }

            var anchor = EnsureArPreviewFaceAnchor();
            anchor.ApplyFace(face, arPreviewOverlay.rectTransform.rect, sourceWidth, sourceHeight);
            UpdateArPreviewFaceRig(face);
            UpdateArPreviewFaceModelRig(face);
        }

        private ArPreviewFaceAnchor EnsureArPreviewFaceAnchor()
        {
            if (arPreviewFaceAnchor != null)
            {
                return arPreviewFaceAnchor;
            }

            var host = new GameObject("ArPreviewFaceAnchor", typeof(RectTransform), typeof(ArPreviewFaceAnchor));
            host.transform.SetParent(arPreviewOverlay.transform, false);
            var rectTransform = host.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.one * 0.5f;
            rectTransform.anchorMax = Vector2.one * 0.5f;
            rectTransform.pivot = Vector2.one * 0.5f;
            rectTransform.sizeDelta = Vector2.zero;
            arPreviewFaceAnchor = host.GetComponent<ArPreviewFaceAnchor>();
            return arPreviewFaceAnchor;
        }

        private void UpdateArPreviewFaceRig(FaceTrack face)
        {
            if (!enableModelerFaceRig || arPreviewOverlay == null || face == null)
            {
                arPreviewFaceRig?.Hide();
                return;
            }

            var rig = EnsureArPreviewFaceRig();
            rig.ShowWireframe = showModelerFaceMeshWireframe;
            rig.ApplyFace(face, arPreviewOverlay.rectTransform.rect);
        }

        private ArPreviewFaceRig EnsureArPreviewFaceRig()
        {
            if (arPreviewFaceRig != null)
            {
                return arPreviewFaceRig;
            }

            var host = new GameObject("ArPreviewFaceRig", typeof(RectTransform), typeof(ArPreviewFaceRig));
            host.transform.SetParent(arPreviewOverlay.transform, false);
            var rectTransform = host.GetComponent<RectTransform>();
            rectTransform.anchorMin = Vector2.one * 0.5f;
            rectTransform.anchorMax = Vector2.one * 0.5f;
            rectTransform.pivot = Vector2.one * 0.5f;
            rectTransform.sizeDelta = Vector2.zero;
            arPreviewFaceRig = host.GetComponent<ArPreviewFaceRig>();
            return arPreviewFaceRig;
        }

        private void UpdateArPreviewFaceModelRig(FaceTrack face)
        {
            if ((!enableTracked3dFaceModel && !HasTracked3dFaceParts()) || face == null)
            {
                arPreviewFaceModelRig?.Hide();
                return;
            }

            var rig = EnsureArPreviewFaceModelRig(editorPreview: false);
            ApplyTracked3dFaceGuideSettings(rig);
            rig.ApplyFace(face, cameraCaptureService.CurrentWidth, cameraCaptureService.CurrentHeight);
        }

        private void ApplyTracked3dFaceGuideSettings(ArPreviewFaceModelRig rig)
        {
            if (rig == null)
            {
                return;
            }

            rig.ShowGuideModel = showTracked3dFaceGuideModel;
            rig.AlignmentMode = tracked3dFaceGuideAlignmentMode;
            rig.ModelScaleMultiplier = tracked3dFaceGuideScale;
            rig.MirrorModelX = mirrorTracked3dFaceGuideX;
            rig.InvertFaceYaw = invertTracked3dFaceGuideYaw;
            rig.PreserveExistingModelLocalTransform = preserveTracked3dFaceGuideSceneTransform;
            rig.ModelLocalPositionOffset = tracked3dFaceGuideLocalOffset;
            rig.ModelLocalEulerOffset = tracked3dFaceGuideEulerOffset;
            rig.ModelLocalScaleOffset = tracked3dFaceGuideLocalScale;
            rig.NeutralFaceMaskNoseBlend = tracked3dFaceGuideNeutralNoseBlend;
            rig.SideFaceMaskNoseBlend = tracked3dFaceGuideSideNoseBlend;
            rig.FullSideYawDegrees = tracked3dFaceGuideFullSideYawDegrees;
            rig.FaceMaskScreenOffsetByEyeDistance = tracked3dFaceGuideScreenOffsetByEyeDistance;
            rig.FacePartModels = enableTracked3dFaceParts
                ? tracked3dFaceParts
                : Array.Empty<Tracked3dFacePartBinding>();
        }

        private bool HasTracked3dFaceParts()
        {
            if (!enableTracked3dFaceParts || tracked3dFaceParts == null)
            {
                return false;
            }

            foreach (var part in tracked3dFaceParts)
            {
                if (part != null && part.visible && (part.prefab != null || !string.IsNullOrWhiteSpace(part.resourcePath)))
                {
                    return true;
                }
            }

            return false;
        }

        private ArPreviewFaceModelRig EnsureArPreviewFaceModelRig(bool editorPreview)
        {
            var expectedName = editorPreview ? "ArPreviewFaceModelRig_EDITOR_PREVIEW" : "ArPreviewFaceModelRig_RUNTIME";
            if (arPreviewFaceModelRig != null && arPreviewFaceModelRig.name == expectedName)
            {
                return arPreviewFaceModelRig;
            }

            EnsureArModelOverlay();
            if (arPreviewOverlay != null)
            {
                if (!editorPreview)
                {
                    var editorPreviewTransform = arPreviewOverlay.transform.Find("ArPreviewFaceModelRig_EDITOR_PREVIEW");
                    var editorPreviewRig = editorPreviewTransform != null
                        ? editorPreviewTransform.GetComponent<ArPreviewFaceModelRig>()
                        : null;
                    editorPreviewRig?.Hide();
                }

                var existingTransform = arPreviewOverlay.transform.Find(expectedName);
                if (existingTransform == null && editorPreview && !Application.isPlaying)
                {
                    var legacyTransform = arPreviewOverlay.transform.Find("ArPreviewFaceModelRig");
                    if (legacyTransform != null && legacyTransform.TryGetComponent<ArPreviewFaceModelRig>(out _))
                    {
                        legacyTransform.name = expectedName;
                        existingTransform = legacyTransform;
                    }
                }

                arPreviewFaceModelRig = existingTransform != null
                    ? existingTransform.GetComponent<ArPreviewFaceModelRig>()
                    : null;
                if (arPreviewFaceModelRig != null)
                {
                    arPreviewFaceModelRig.Initialize(arModelOverlay, faceModelPrefab, editorPreview);
                    CopyEditorPreviewFaceGuideTransformToRuntime(arPreviewFaceModelRig, editorPreview);
                    return arPreviewFaceModelRig;
                }
            }

            var host = new GameObject(expectedName, typeof(ArPreviewFaceModelRig));
            host.transform.SetParent(arPreviewOverlay != null ? arPreviewOverlay.transform : transform, false);
#if UNITY_EDITOR
            if (editorPreview && !Application.isPlaying)
            {
                host.hideFlags = HideFlags.DontSaveInEditor;
            }
#endif
            arPreviewFaceModelRig = host.GetComponent<ArPreviewFaceModelRig>();
            arPreviewFaceModelRig.Initialize(arModelOverlay, faceModelPrefab, editorPreview);
            CopyEditorPreviewFaceGuideTransformToRuntime(arPreviewFaceModelRig, editorPreview);
            return arPreviewFaceModelRig;
        }

        private void CopyEditorPreviewFaceGuideTransformToRuntime(ArPreviewFaceModelRig runtimeRig, bool editorPreview)
        {
            if (editorPreview || !preserveTracked3dFaceGuideSceneTransform || runtimeRig == null || arPreviewOverlay == null)
            {
                return;
            }

            var editorPreviewTransform = arPreviewOverlay.transform.Find("ArPreviewFaceModelRig_EDITOR_PREVIEW");
            var editorPreviewRig = editorPreviewTransform != null
                ? editorPreviewTransform.GetComponent<ArPreviewFaceModelRig>()
                : null;
            if (editorPreviewRig == null
                || !editorPreviewRig.TryGetGuideModelLocalTransform(out var localPosition, out var localRotation, out var localScale))
            {
                return;
            }

            runtimeRig.ApplyGuideModelLocalTransform(localPosition, localRotation, localScale);
        }

        private void EnsureArModelOverlay()
        {
            if (cameraPreview == null)
            {
                return;
            }

            var parent = cameraPreview.transform.parent;
            if (arModelOverlay != null)
            {
                arModelOverlay.color = Color.clear;
                arModelOverlay.raycastTarget = false;
                AlignArOverlaysToCameraPreview();
                return;
            }

            var existing = parent != null ? parent.Find("ArModelOverlay") : null;
            if (existing != null && existing.TryGetComponent<RawImage>(out var existingOverlay))
            {
                arModelOverlay = existingOverlay;
                arModelOverlay.color = Color.clear;
                arModelOverlay.raycastTarget = false;
                AlignArOverlaysToCameraPreview();
                return;
            }

            arModelOverlay = CreateRawImage(parent, "ArModelOverlay", Vector2.one * 0.5f, Vector2.zero, cameraPreview.rectTransform.sizeDelta);
            arModelOverlay.color = Color.clear;
            arModelOverlay.raycastTarget = false;
            AlignArOverlaysToCameraPreview();
        }

        private void AlignArOverlaysToCameraPreview()
        {
            if (cameraPreview == null || cameraPreview.transform.parent == null)
            {
                return;
            }

            CopyCameraPreviewRect(arModelOverlay);
            CopyCameraPreviewRect(arPreviewOverlay);

            var cameraSiblingIndex = cameraPreview.transform.GetSiblingIndex();
            if (arModelOverlay != null)
            {
                arModelOverlay.transform.SetSiblingIndex(Mathf.Min(cameraSiblingIndex + 1, cameraPreview.transform.parent.childCount - 1));
            }

            if (arPreviewOverlay != null)
            {
                var modelSiblingIndex = arModelOverlay != null ? arModelOverlay.transform.GetSiblingIndex() : cameraSiblingIndex;
                arPreviewOverlay.transform.SetSiblingIndex(Mathf.Min(modelSiblingIndex + 1, cameraPreview.transform.parent.childCount - 1));
            }
        }

        private void CopyCameraPreviewRect(RawImage overlay)
        {
            if (overlay == null || cameraPreview == null)
            {
                return;
            }

            var cameraRect = cameraPreview.rectTransform;
            var overlayRect = overlay.rectTransform;
            if (overlayRect.parent != cameraRect.parent)
            {
                overlayRect.SetParent(cameraRect.parent, false);
            }

            overlayRect.anchorMin = cameraRect.anchorMin;
            overlayRect.anchorMax = cameraRect.anchorMax;
            overlayRect.pivot = cameraRect.pivot;
            overlayRect.anchoredPosition = cameraRect.anchoredPosition;
            overlayRect.sizeDelta = cameraRect.sizeDelta;
            overlayRect.localRotation = cameraRect.localRotation;
            overlayRect.localScale = cameraRect.localScale;
            overlayRect.localPosition = new Vector3(overlayRect.localPosition.x, overlayRect.localPosition.y, cameraRect.localPosition.z);
        }

        private void UpdateArDebugTelemetry(ArTrackingFrame frame)
        {
            if (!showArDebugTelemetry)
            {
                if (arDebugText != null)
                {
                    arDebugText.gameObject.SetActive(false);
                }

                return;
            }

            var debugText = EnsureArDebugText();
            if (debugText == null)
            {
                return;
            }

            UpdateArDebugFps(frame);
            var snapshot = arTrackingStabilizer.DebugSnapshot;
            var provider = string.IsNullOrWhiteSpace(snapshot.ProviderName) ? frame?.ProviderName ?? "None" : snapshot.ProviderName;
            var euler = snapshot.EulerDegrees;
            var fallbackLine = string.IsNullOrWhiteSpace(mediaPipeFallbackReason) ? string.Empty : $"\nMediaPipe fallback: {mediaPipeFallbackReason}";
            debugText.SetText(string.Format(
                "AR {0}\nfaces={1} calibrated={2} {3:0}% fps={4:0.0}\nconf={5:0.00} scale={6:0.00} eye={7:0.000}/{8:0.000}\npitch={9:0.0} yaw={10:0.0} roll={11:0.0}{12}",
                provider,
                snapshot.FaceCount,
                snapshot.IsCalibrated ? "yes" : "no",
                snapshot.CalibrationProgress * 100f,
                arDebugFps,
                snapshot.Confidence,
                snapshot.Scale,
                snapshot.EyeDistance,
                snapshot.NeutralEyeDistance,
                euler.x,
                euler.y,
                euler.z,
                fallbackLine));
            debugText.gameObject.SetActive(true);
        }

        private TextMeshProUGUI EnsureArDebugText()
        {
            if (arDebugText != null || arPreviewOverlay == null)
            {
                return arDebugText;
            }

            arDebugText = CreateText(
                arPreviewOverlay.transform,
                "ArDebugTelemetry",
                string.Empty,
                18,
                TextAlignmentOptions.TopLeft,
                new Vector2(0f, 1f),
                new Vector2(0f, 1f),
                new Vector2(12f, -12f),
                new Vector2(520f, 150f));
            arDebugText.color = new Color(0.1f, 1f, 0.55f, 0.95f);
            arDebugText.raycastTarget = false;
            arDebugText.transform.SetAsLastSibling();
            return arDebugText;
        }

        private void UpdateArDebugFps(ArTrackingFrame frame)
        {
            var timestamp = frame != null && frame.TimestampSeconds > 0d ? frame.TimestampSeconds : Time.realtimeSinceStartupAsDouble;
            if (lastArDebugTimestamp > 0d)
            {
                var delta = Mathf.Max(0.0001f, (float)(timestamp - lastArDebugTimestamp));
                var instantFps = 1f / delta;
                arDebugFps = arDebugFps <= 0f ? instantFps : Mathf.Lerp(arDebugFps, instantFps, 0.2f);
            }

            lastArDebugTimestamp = timestamp;
        }

        private void UpdateFaceMarkDebugLines(ArTrackingFrame frame, int sourceWidth, int sourceHeight)
        {
            if (!showFaceMarkDebugLines || arPreviewOverlay == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                HideFaceMarkDebugLines(0);
                return;
            }

            var overlayRect = arPreviewOverlay.rectTransform.rect;
            var lineIndex = 0;
            if (frame?.Faces != null)
            {
                foreach (var face in frame.Faces)
                {
                    var landmarks = face?.DebugNormalizedLandmarks;
                    if (landmarks != null && landmarks.Length >= 468)
                    {
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkJawIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkRightBrowIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkLeftBrowIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkNoseBridgeIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkNoseBaseIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkRightEyeIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkLeftEyeIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkOuterMouthIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, FaceMarkInnerMouthIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                    }
                    else if (landmarks != null && landmarks.Length >= 68)
                    {
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkJawIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkRightBrowIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkLeftBrowIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkNoseBridgeIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkNoseBaseIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkRightEyeIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkLeftEyeIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkOuterMouthIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                        lineIndex = AddFaceMarkPolyline(landmarks, CvFaceMarkInnerMouthIndices, overlayRect, sourceWidth, sourceHeight, lineIndex);
                    }
                    else if (face != null)
                    {
                        lineIndex = AddFaceBoundsDebugRect(face.NormalizedBounds, overlayRect, sourceWidth, sourceHeight, lineIndex);
                    }
                }
            }

            HideFaceMarkDebugLines(lineIndex);
        }

        private int AddFaceBoundsDebugRect(Rect normalizedBounds, Rect overlayRect, int sourceWidth, int sourceHeight, int lineIndex)
        {
            if (normalizedBounds.width <= 0f || normalizedBounds.height <= 0f)
            {
                return lineIndex;
            }

            var bottomLeft = LandmarkToOverlayPosition(new Vector2(normalizedBounds.xMin, normalizedBounds.yMin), overlayRect, sourceWidth, sourceHeight);
            var bottomRight = LandmarkToOverlayPosition(new Vector2(normalizedBounds.xMax, normalizedBounds.yMin), overlayRect, sourceWidth, sourceHeight);
            var topRight = LandmarkToOverlayPosition(new Vector2(normalizedBounds.xMax, normalizedBounds.yMax), overlayRect, sourceWidth, sourceHeight);
            var topLeft = LandmarkToOverlayPosition(new Vector2(normalizedBounds.xMin, normalizedBounds.yMax), overlayRect, sourceWidth, sourceHeight);
            ApplyFaceMarkDebugLine(lineIndex++, bottomLeft, bottomRight);
            ApplyFaceMarkDebugLine(lineIndex++, bottomRight, topRight);
            ApplyFaceMarkDebugLine(lineIndex++, topRight, topLeft);
            ApplyFaceMarkDebugLine(lineIndex++, topLeft, bottomLeft);
            return lineIndex;
        }

        private int AddFaceMarkPolyline(Vector2[] landmarks, int[] indices, Rect overlayRect, int sourceWidth, int sourceHeight, int lineIndex)
        {
            for (var i = 1; i < indices.Length; i++)
            {
                if (indices[i - 1] < 0 || indices[i - 1] >= landmarks.Length || indices[i] < 0 || indices[i] >= landmarks.Length)
                {
                    continue;
                }

                var from = LandmarkToOverlayPosition(landmarks[indices[i - 1]], overlayRect, sourceWidth, sourceHeight);
                var to = LandmarkToOverlayPosition(landmarks[indices[i]], overlayRect, sourceWidth, sourceHeight);
                ApplyFaceMarkDebugLine(lineIndex++, from, to);
            }

            return lineIndex;
        }

        private static Vector2 LandmarkToOverlayPosition(Vector2 normalizedPoint, Rect overlayRect, int sourceWidth, int sourceHeight)
        {
            var pixelX = normalizedPoint.x * sourceWidth;
            var pixelY = (1f - normalizedPoint.y) * sourceHeight;
            return new Vector2(
                ((pixelX / sourceWidth) - 0.5f) * overlayRect.width,
                (0.5f - (pixelY / sourceHeight)) * overlayRect.height);
        }

        private void ApplyFaceMarkDebugLine(int index, Vector2 from, Vector2 to)
        {
            var line = EnsureFaceMarkDebugLine(index);
            var rectTransform = line.rectTransform;
            var delta = to - from;
            rectTransform.anchoredPosition = (from + to) * 0.5f;
            rectTransform.sizeDelta = new Vector2(Mathf.Max(1f, delta.magnitude), Mathf.Max(1f, faceMarkDebugLineThickness));
            rectTransform.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            line.color = faceMarkDebugLineColor;
            line.enabled = true;
            line.gameObject.SetActive(true);
        }

        private Image EnsureFaceMarkDebugLine(int index)
        {
            while (arFaceMarkLineImages.Count <= index)
            {
                var host = new GameObject($"FaceMarkDebugLine_{arFaceMarkLineImages.Count:00}", typeof(RectTransform), typeof(Image));
                host.transform.SetParent(arPreviewOverlay.transform, false);
                var rectTransform = host.GetComponent<RectTransform>();
                rectTransform.anchorMin = Vector2.one * 0.5f;
                rectTransform.anchorMax = Vector2.one * 0.5f;
                rectTransform.pivot = Vector2.one * 0.5f;
                var image = host.GetComponent<Image>();
                image.raycastTarget = false;
                arFaceMarkLineImages.Add(image);
            }

            return arFaceMarkLineImages[index];
        }

        private void HideFaceMarkDebugLines(int startIndex)
        {
            for (var index = Mathf.Max(0, startIndex); index < arFaceMarkLineImages.Count; index++)
            {
                if (arFaceMarkLineImages[index] == null)
                {
                    continue;
                }

                arFaceMarkLineImages[index].enabled = false;
                arFaceMarkLineImages[index].gameObject.SetActive(false);
            }
        }

        private void UpdateArPreviewStickerObjects(ArStickerRenderItem[] renderItems, int sourceWidth, int sourceHeight)
        {
            if (arPreviewOverlay == null || sourceWidth <= 0 || sourceHeight <= 0)
            {
                return;
            }

            var overlayRect = arPreviewOverlay.rectTransform.rect;
            for (var index = 0; index < renderItems.Length; index++)
            {
                var item = renderItems[index];
                var stickerObject = EnsureArPreviewStickerObject(index);
                stickerObject.ApplyTrackedItem(item, overlayRect, sourceWidth, sourceHeight);
            }

            HideArPreviewStickerObjects(renderItems.Length);
        }

        private ArPreviewStickerObject EnsureArPreviewStickerObject(int index)
        {
            while (arPreviewStickerObjects.Count <= index)
            {
                var host = new GameObject($"ArPreviewSticker_{arPreviewStickerObjects.Count:00}", typeof(RectTransform), typeof(RawImage), typeof(ArPreviewStickerObject));
                host.transform.SetParent(arPreviewOverlay.transform, false);
                var rectTransform = host.GetComponent<RectTransform>();
                rectTransform.anchorMin = Vector2.one * 0.5f;
                rectTransform.anchorMax = Vector2.one * 0.5f;
                rectTransform.pivot = Vector2.one * 0.5f;
                var image = host.GetComponent<RawImage>();
                image.raycastTarget = false;
                arPreviewStickerObjects.Add(host.GetComponent<ArPreviewStickerObject>());
            }

            return arPreviewStickerObjects[index];
        }

        private void HideArPreviewStickerObjects(int startIndex)
        {
            for (var index = Mathf.Max(0, startIndex); index < arPreviewStickerObjects.Count; index++)
            {
                if (arPreviewStickerObjects[index] == null)
                {
                    continue;
                }

                arPreviewStickerObjects[index].Hide();
            }
        }

        private Dictionary<string, string> BuildAiMetadata()
        {
            var metadata = new Dictionary<string, string>();
            if (selectedAiStyle != null)
            {
                metadata["ai_style_id"] = selectedAiStyle.styleId;
                metadata["ai_style_prompt"] = selectedAiStyle.aiPrompt;
                metadata["local_ai_preview"] = selectedAiStyle.applyLocalStylizedPreview.ToString();
            }

            if (latestMotionClip?.FramePaths != null)
            {
                metadata["motion_clip_frame_count"] = latestMotionClip.FramePaths.Length.ToString();
                metadata["motion_clip_fps"] = latestMotionClip.FrameRate.ToString("0.##");
            }

            if (!string.IsNullOrWhiteSpace(latestMotionClip?.VideoPath))
            {
                metadata["motion_video_path"] = latestMotionClip.VideoPath;
            }

            if (!string.IsNullOrWhiteSpace(passengerName))
            {
                metadata["passenger_name"] = passengerName;
            }

            return metadata;
        }

        private static string FormatPrice(BoothThemeOption theme)
        {
            if (theme == null)
            {
                return string.Empty;
            }

            var amount = theme.priceMinorUnits / 100m;
            var currency = string.IsNullOrWhiteSpace(theme.currencyCode) ? "THB" : theme.currencyCode.Trim().ToUpperInvariant();
            return $"{amount:0.00} {currency}";
        }

        private static string ScreenTitle(BoothUiScreenId screenId)
        {
            return screenId switch
            {
                BoothUiScreenId.Attract => "MRKREME Booth",
                BoothUiScreenId.ThemeSelect => "Choose Your Frame",
                BoothUiScreenId.ArtStyleSelect => "Type Your Name",
                BoothUiScreenId.PaymentMock => "Choose Payment",
                BoothUiScreenId.Capture => "Take A Photo",
                BoothUiScreenId.Preview => "Preview",
                BoothUiScreenId.Fulfillment => "Download",
                BoothUiScreenId.Printing => "Print",
                BoothUiScreenId.Done => "Done",
                BoothUiScreenId.Error => "Error",
                BoothUiScreenId.Operator => "Operator",
                _ => "Photo Booth"
            };
        }

        private static string BuildDownloadSummary(BoothJob job)
        {
            if (job == null)
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(job.MotionClipUrl))
            {
                return job.DownloadUrl ?? string.Empty;
            }

            return $"Photo: {job.DownloadUrl}\nCountdown clip: {job.MotionClipUrl}";
        }

        private static void ClearPreview(RawImage image, ref Texture2D texture)
        {
            if (image != null)
            {
                image.texture = null;
                image.color = new Color(1f, 1f, 1f, 0.08f);
            }

            if (texture != null)
            {
                Destroy(texture);
                texture = null;
            }
        }

        private void BuildRuntimeUi()
        {
            var canvas = CreateCanvas(transform);
            var background = CreatePanel(canvas.transform, "EditableMrkremeFrontendRoot", Color.white);
            titleText = CreateText(background.transform, "Title", string.Empty, 12, TextAlignmentOptions.Center, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -12f), new Vector2(1000f, 24f));
            titleText.color = new Color(0f, 0f, 0f, 0f);
            statusText = CreateText(background.transform, "Status", "Booting...", 28, TextAlignmentOptions.Center, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(980f, 44f));
            statusText.color = new Color(0.08f, 0.08f, 0.08f, 0.9f);

            var builtScreens = new List<ScreenBinding>();
            var attract = CreateScreen(background.transform, BoothUiScreenId.Attract, builtScreens);
            CreateBackgroundImage(attract.transform, "YellowGridBackground", "piece_05");
            CreatePanelBlock(attract.transform, "TopMetalPanel", new Vector2(0.5f, 0.91f), new Vector2(1080f, 330f), MetalColor);
            CreatePanelBlock(attract.transform, "BottomMetalPanel", new Vector2(0.5f, 0.08f), new Vector2(1080f, 300f), MetalColor);
            CreateSheetImage(attract.transform, "MetroGuideHeaderArt", "piece_01", new Rect(55f, 90f, 2120f, 875f), new Vector2(0.5f, 0.88f), new Vector2(970f, 400f));
            CreateSheetImage(attract.transform, "MrkremeLogoArt", "piece_02", new Rect(75f, 95f, 1500f, 1150f), new Vector2(0.5f, 0.53f), new Vector2(900f, 690f));
            CreateSheetImage(attract.transform, "TrainArt", "piece_02", new Rect(85f, 1835f, 1750f, 575f), new Vector2(0.42f, 0.36f), new Vector2(760f, 250f));
            var startButton = CreateButton(attract.transform, "StartButton", "TAP TO START", new Vector2(0.5f, 0.095f), Vector2.zero, new Vector2(900f, 128f));
            StyleMrkremeButton(startButton);
            startButton.onClick.AddListener(StartSessionFromUi);

            var themeSelect = CreateScreen(background.transform, BoothUiScreenId.ThemeSelect, builtScreens);
            CreateBackgroundImage(themeSelect.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(themeSelect.transform, "LogoArt", "piece_01", new Rect(2380f, 160f, 1500f, 520f), new Vector2(0.5f, 0.86f), new Vector2(720f, 250f));
            CreateText(themeSelect.transform, "ChooseFrameTitle", "CHOOSE\nYOUR FRAME", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.76f), new Vector2(0.5f, 0.76f), Vector2.zero, new Vector2(760f, 160f)).color = Color.black;
            priceText = CreateText(themeSelect.transform, "ThemePrice", string.Empty, 30, TextAlignmentOptions.Center, new Vector2(0.5f, 0.79f), new Vector2(0.5f, 0.79f), Vector2.zero, new Vector2(600f, 44f));
            priceText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            var themeSlots = new[]
            {
                (new Vector2(0.285f, 0.64f), 0),
                (new Vector2(0.715f, 0.64f), 1),
                (new Vector2(0.285f, 0.375f), 0),
                (new Vector2(0.715f, 0.375f), 1)
            };

            for (var i = 0; i < themeSlots.Length; i++)
            {
                var index = themeSlots[i].Item2;
                var position = themeSlots[i].Item1;
                CreateSheetImage(themeSelect.transform, $"FramePreview{i + 1}", "piece_02", new Rect(2180f, 120f, 930f, 1250f), position, new Vector2(390f, 520f));
                var button = CreateInvisibleButton(themeSelect.transform, $"ThemeButton{i + 1}", position, new Vector2(390f, 520f));
                button.onClick.AddListener(() => ChooseThemeCandidateFromUi(index));
            }

            CreatePanelBlock(themeSelect.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            var themeBackButton = CreateButton(themeSelect.transform, "ThemeBackButton", "BACK", new Vector2(0.25f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
            var themeConfirmButton = CreateButton(themeSelect.transform, "ThemeConfirmButton", "CONFIRM", new Vector2(0.75f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
            StyleMrkremeButton(themeBackButton);
            StyleMrkremeButton(themeConfirmButton);
            themeBackButton.onClick.AddListener(ResetFromUi);
            themeConfirmButton.onClick.AddListener(ConfirmThemeSelectionFromUi);

            var aiStyle = CreateScreen(background.transform, BoothUiScreenId.ArtStyleSelect, builtScreens);
            CreateBackgroundImage(aiStyle.transform, "CreamPatternBackground", "piece_06");
            CreatePanelBlock(aiStyle.transform, "TopMetalPanel", new Vector2(0.5f, 0.91f), new Vector2(1080f, 330f), MetalColor);
            CreateText(aiStyle.transform, "NameTitle", "TYPE YOUR\nNAME HERE", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.91f), new Vector2(0.5f, 0.91f), Vector2.zero, new Vector2(760f, 180f)).color = Color.black;
            CreateSheetImage(aiStyle.transform, "BoardingPassArt", "piece_02", new Rect(95f, 2515f, 1350f, 1370f), new Vector2(0.5f, 0.62f), new Vector2(610f, 620f));
            nameEntryText = CreateText(aiStyle.transform, "PassengerName", string.Empty, 34, TextAlignmentOptions.Left, new Vector2(0.5f, 0.49f), new Vector2(0.5f, 0.49f), new Vector2(-78f, 0f), new Vector2(520f, 52f));
            nameEntryText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            aiStyleText = CreateText(aiStyle.transform, "NameCount", "0 / 15", 30, TextAlignmentOptions.Right, new Vector2(0.5f, 0.49f), new Vector2(0.5f, 0.49f), new Vector2(160f, 0f), new Vector2(240f, 52f));
            aiStyleText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            BuildNameKeyboard(aiStyle.transform);
            CreatePanelBlock(aiStyle.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            var confirmNameButton = CreateButton(aiStyle.transform, "ConfirmNameButton", "CONFIRM", new Vector2(0.5f, 0.075f), Vector2.zero, new Vector2(900f, 128f));
            StyleMrkremeButton(confirmNameButton);
            confirmNameButton.onClick.AddListener(ConfirmNameFromUi);

            var payment = CreateScreen(background.transform, BoothUiScreenId.PaymentMock, builtScreens);
            CreateBackgroundImage(payment.transform, "CreamPatternBackground", "piece_06");
            CreatePanelBlock(payment.transform, "TopMetalPanel", new Vector2(0.5f, 0.91f), new Vector2(1080f, 330f), MetalColor);
            CreateText(payment.transform, "PaymentTitle", "CHOOSE YOUR\nPAYMENT", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.91f), new Vector2(0.5f, 0.91f), Vector2.zero, new Vector2(760f, 180f)).color = Color.black;
            var qrPayButton = CreateButton(payment.transform, "QrPayButton", "QR PAYMENT", new Vector2(0.5f, 0.66f), Vector2.zero, new Vector2(860f, 150f));
            var cardPayButton = CreateButton(payment.transform, "CardPayButton", "VISA   CREDIT CARD", new Vector2(0.5f, 0.51f), Vector2.zero, new Vector2(860f, 150f));
            var voucherButton = CreateButton(payment.transform, "VoucherButton", "VOUCHER", new Vector2(0.5f, 0.36f), Vector2.zero, new Vector2(860f, 150f));
            StyleMrkremeButton(qrPayButton, new Color(0.09f, 0.21f, 0.39f, 1f), Color.white);
            StyleMrkremeButton(cardPayButton, Color.white, Color.black);
            StyleMrkremeButton(voucherButton, Color.black, Color.white);
            CreatePanelBlock(payment.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            var paymentBackButton = CreateButton(payment.transform, "PaymentBackButton", "BACK", new Vector2(0.5f, 0.075f), Vector2.zero, new Vector2(900f, 128f));
            StyleMrkremeButton(paymentBackButton);
            qrPayButton.onClick.AddListener(PayMockFromUi);
            cardPayButton.onClick.AddListener(PayMockFromUi);
            voucherButton.onClick.AddListener(PayMockFromUi);
            paymentBackButton.onClick.AddListener(() => SwitchScreen(BoothUiScreenId.ThemeSelect, "Choose your frame."));

            var capture = CreateScreen(background.transform, BoothUiScreenId.Capture, builtScreens);
            CreateBackgroundImage(capture.transform, "YellowGridBackground", "piece_05");
            CreatePanelBlock(capture.transform, "TopMetalPanel", new Vector2(0.5f, 0.91f), new Vector2(1080f, 330f), MetalColor);
            CreateText(capture.transform, "CaptureTitle", "TAKE A PHOTO", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.91f), new Vector2(0.5f, 0.91f), Vector2.zero, new Vector2(760f, 120f)).color = Color.black;
            CreatePanelBlock(capture.transform, "CameraFrame", new Vector2(0.5f, 0.54f), new Vector2(900f, 900f), Color.black);
            cameraPreview = CreateRawImage(capture.transform, "CameraPreview", new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(860f, 860f));
            cameraPreview.color = Color.white;
            arModelOverlay = CreateRawImage(capture.transform, "ArModelOverlay", new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(860f, 860f));
            arModelOverlay.color = Color.clear;
            arModelOverlay.raycastTarget = false;
            arPreviewOverlay = CreateRawImage(capture.transform, "ArPreviewOverlay", new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(860f, 860f));
            arPreviewOverlay.color = Color.clear;
            countdownText = CreateText(capture.transform, "Countdown", string.Empty, 120, TextAlignmentOptions.Center, new Vector2(0.5f, 0.54f), new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(360f, 180f));
            CreateText(capture.transform, "CaptureCount", "AMOUNT 1 / 4", 28, TextAlignmentOptions.Center, new Vector2(0.5f, 0.205f), new Vector2(0.5f, 0.205f), Vector2.zero, new Vector2(300f, 50f)).color = new Color(0.08f, 0.08f, 0.08f, 1f);
            CreatePanelBlock(capture.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            captureButton = CreateButton(capture.transform, "CaptureButton", string.Empty, new Vector2(0.5f, 0.075f), Vector2.zero, new Vector2(150f, 150f));
            StyleCaptureButton(captureButton);
            captureButton.onClick.AddListener(CaptureFromUi);

            var preview = CreateScreen(background.transform, BoothUiScreenId.Preview, builtScreens);
            CreateBackgroundImage(preview.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(preview.transform, "LogoArt", "piece_01", new Rect(2380f, 160f, 1500f, 520f), new Vector2(0.5f, 0.86f), new Vector2(680f, 235f));
            motionPreview = CreateRawImage(preview.transform, "MotionPreview", new Vector2(0.29f, 0.63f), Vector2.zero, new Vector2(360f, 280f));
            composedPreview = CreateRawImage(preview.transform, "ComposedPreview", new Vector2(0.71f, 0.63f), Vector2.zero, new Vector2(360f, 280f));
            CreateText(preview.transform, "MotionLabel", "COUNTDOWN CLIP", 20, TextAlignmentOptions.Center, new Vector2(0.29f, 0.81f), new Vector2(0.29f, 0.81f), Vector2.zero, new Vector2(360f, 40f)).color = new Color(0.08f, 0.08f, 0.08f, 1f);
            CreateText(preview.transform, "ComposedLabel", "PHOTO PREVIEW", 20, TextAlignmentOptions.Center, new Vector2(0.71f, 0.81f), new Vector2(0.71f, 0.81f), Vector2.zero, new Vector2(360f, 40f)).color = new Color(0.08f, 0.08f, 0.08f, 1f);
            retakeButton = CreateButton(preview.transform, "RetakeButton", "BACK", new Vector2(0.25f, 0.1f), Vector2.zero, new Vector2(260f, 84f));
            continueButton = CreateButton(preview.transform, "ContinueButton", "CONFIRM", new Vector2(0.75f, 0.1f), Vector2.zero, new Vector2(260f, 84f));
            StyleMrkremeButton(retakeButton);
            StyleMrkremeButton(continueButton);
            retakeButton.onClick.AddListener(RetakeFromUi);
            continueButton.onClick.AddListener(ContinueFromPreviewFromUi);

            var fulfillment = CreateScreen(background.transform, BoothUiScreenId.Fulfillment, builtScreens);
            CreateBackgroundImage(fulfillment.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(fulfillment.transform, "LogoArt", "piece_02", new Rect(75f, 95f, 1500f, 1150f), new Vector2(0.5f, 0.78f), new Vector2(700f, 540f));
            CreateSheetImage(fulfillment.transform, "SelectedFrameArt", "piece_03", new Rect(420f, 130f, 2920f, 2450f), new Vector2(0.5f, 0.46f), new Vector2(820f, 690f));
            qrPreview = CreateRawImage(fulfillment.transform, "QrPreview", new Vector2(0.79f, 0.78f), Vector2.zero, new Vector2(180f, 180f));
            downloadUrlText = CreateText(fulfillment.transform, "DownloadUrl", "", 20, TextAlignmentOptions.Center, new Vector2(0.5f, 0.18f), new Vector2(0.5f, 0.18f), Vector2.zero, new Vector2(920f, 100f));
            downloadUrlText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            printButton = CreateButton(fulfillment.transform, "PrintButton", "PRINT", new Vector2(0.32f, 0.1f), Vector2.zero, new Vector2(220f, 72f));
            doneButton = CreateButton(fulfillment.transform, "DoneButton", "HOME", new Vector2(0.68f, 0.1f), Vector2.zero, new Vector2(220f, 72f));
            StyleMrkremeButton(printButton);
            StyleMrkremeButton(doneButton);
            printButton.onClick.AddListener(PrintFromUi);
            doneButton.onClick.AddListener(DoneFromUi);

            var printing = CreateScreen(background.transform, BoothUiScreenId.Printing, builtScreens);
            CreateBackgroundImage(printing.transform, "PrintingBackground", "piece_06");
            printStatusText = CreateText(printing.transform, "PrintStatus", "Simulated print in progress...", 32, TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 100f));
            printStatusText.color = new Color(0.08f, 0.08f, 0.08f, 1f);

            var done = CreateScreen(background.transform, BoothUiScreenId.Done, builtScreens);
            CreateBackgroundImage(done.transform, "DoneBackground", "piece_06");
            CreateSheetImage(done.transform, "LogoArt", "piece_02", new Rect(75f, 95f, 1500f, 1150f), new Vector2(0.5f, 0.62f), new Vector2(820f, 630f));
            var resetButton = CreateButton(done.transform, "ResetButton", "BACK TO HOME", new Vector2(0.5f, 0.08f), Vector2.zero, new Vector2(900f, 128f));
            StyleMrkremeButton(resetButton);
            resetButton.onClick.AddListener(ResetFromUi);

            var error = CreateScreen(background.transform, BoothUiScreenId.Error, builtScreens);
            CreateBackgroundImage(error.transform, "ErrorBackground", "piece_06");
            var errorReset = CreateButton(error.transform, "ErrorResetButton", "RESET", new Vector2(0.5f, 0.28f), Vector2.zero, new Vector2(340f, 90f));
            StyleMrkremeButton(errorReset);
            errorReset.onClick.AddListener(ResetFromUi);

            screens = builtScreens.ToArray();
        }

        private void ClearBuiltUi()
        {
            var existing = transform.Find("BoothFrontendCanvas");
            if (existing == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Destroy(existing.gameObject);
            }
            else
            {
                DestroyImmediate(existing.gameObject);
            }
        }

        private void BindUiEvents()
        {
            WireButton("StartButton", StartSessionFromUi);
            WireButton("ThemeButton1", () => ChooseThemeCandidateFromUi(0));
            WireButton("ThemeButton2", () => ChooseThemeCandidateFromUi(1));
            WireButton("ThemeButton3", () => ChooseThemeCandidateFromUi(0));
            WireButton("ThemeButton4", () => ChooseThemeCandidateFromUi(1));
            WireButton("ThemeBackButton", ResetFromUi);
            WireButton("ThemeConfirmButton", ConfirmThemeSelectionFromUi);
            WireButton("QrPayButton", PayMockFromUi);
            WireButton("CardPayButton", PayMockFromUi);
            WireButton("VoucherButton", PayMockFromUi);
            WireButton("PaymentBackButton", () => SwitchScreen(BoothUiScreenId.ThemeSelect, "Choose your frame."));
            WireButton("ConfirmNameButton", ConfirmNameFromUi);
            WireButton("CaptureButton", CaptureFromUi);
            WireButton("RetakeButton", RetakeFromUi);
            WireButton("ContinueButton", ContinueFromPreviewFromUi);
            WireButton("PrintButton", PrintFromUi);
            WireButton("DoneButton", DoneFromUi);
            WireButton("ResetButton", ResetFromUi);
            WireButton("ErrorResetButton", ResetFromUi);

            const string keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            foreach (var key in keys)
            {
                var character = key.ToString();
                WireButton($"Key_{character}", () => AppendNameCharacterFromUi(character));
            }

            WireButton("Key_Back", BackspaceNameFromUi);
        }

        private void WireButton(string buttonName, UnityEngine.Events.UnityAction action)
        {
            var button = FindButton(buttonName);
            if (button == null)
            {
                return;
            }

            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
        }

        private Button FindButton(string buttonName)
        {
            var buttons = GetComponentsInChildren<Button>(true);
            foreach (var button in buttons)
            {
                if (button != null && button.name == buttonName)
                {
                    return button;
                }
            }

            return null;
        }

        private static Canvas CreateCanvas(Transform parent)
        {
            var host = new GameObject("BoothFrontendCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            host.transform.SetParent(parent, false);
            var canvas = host.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;

            var scaler = host.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080f, 1920f);
            scaler.matchWidthOrHeight = 1f;
            return canvas;
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            panel.GetComponent<Image>().color = color;
            return panel;
        }

        private static GameObject CreateScreen(Transform parent, BoothUiScreenId screenId, List<ScreenBinding> bindings)
        {
            var screen = new GameObject($"{screenId}Screen", typeof(RectTransform));
            screen.transform.SetParent(parent, false);
            var rect = screen.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            bindings.Add(new ScreenBinding { screenId = screenId, root = screen });
            return screen;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string text, int fontSize, TextAlignmentOptions alignment, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 size)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
            host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var label = host.GetComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = fontSize;
            label.alignment = alignment;
            label.color = Color.white;
            label.textWrappingMode = TextWrappingModes.Normal;
            return label;
        }

        private static RawImage CreateRawImage(Transform parent, string name, Vector2 anchor, Vector2 anchoredPosition, Vector2 size)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            var image = host.GetComponent<RawImage>();
            image.color = new Color(1f, 1f, 1f, 0.08f);
            return image;
        }

        private static Button CreateButton(Transform parent, string name, string label, Vector2 anchor, Vector2 anchoredPosition, Vector2 size)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;

            var image = host.GetComponent<Image>();
            image.color = new Color(0.95f, 0.9f, 0.78f, 1f);
            var button = host.GetComponent<Button>();
            button.targetGraphic = image;

            var text = CreateText(host.transform, "Label", label, 26, TextAlignmentOptions.Center, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var textRect = text.GetComponent<RectTransform>();
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            text.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            return button;
        }

        private void BuildNameKeyboard(Transform parent)
        {
            BuildKeyboardRow(parent, "QWERTYUIOP", 0.314f, 10, 86f);
            BuildKeyboardRow(parent, "ASDFGHJKL", 0.248f, 9, 86f);
            BuildKeyboardRow(parent, "ZXCVBNM", 0.182f, 7, 98f, includeBackspace: true);
        }

        private void BuildKeyboardRow(Transform parent, string characters, float anchorY, int keyCount, float keyWidth, bool includeBackspace = false)
        {
            var gap = 8f;
            var totalWidth = (keyCount * keyWidth) + ((keyCount - 1) * gap) + (includeBackspace ? keyWidth + 42f : 0f);
            var startX = (-totalWidth / 2f) + (keyWidth / 2f);

            for (var i = 0; i < characters.Length; i++)
            {
                var character = characters[i].ToString();
                var button = CreateButton(parent, $"Key_{character}", character, new Vector2(0.5f, anchorY), new Vector2(startX + (i * (keyWidth + gap)), 0f), new Vector2(keyWidth, 64f));
                StyleKeyboardButton(button);
                button.onClick.AddListener(() => AppendNameCharacterFromUi(character));
            }

            if (!includeBackspace)
            {
                return;
            }

            var backButton = CreateButton(parent, "Key_Back", "BACK", new Vector2(0.5f, anchorY), new Vector2(startX + (characters.Length * (keyWidth + gap)) + 21f, 0f), new Vector2(keyWidth + 42f, 64f));
            StyleKeyboardButton(backButton);
            backButton.onClick.AddListener(BackspaceNameFromUi);
        }

        private Button CreateInvisibleButton(Transform parent, string name, Vector2 anchor, Vector2 size, Vector2? anchoredPosition = null)
        {
            var button = CreateButton(parent, name, string.Empty, anchor, anchoredPosition ?? Vector2.zero, size);
            var image = button.GetComponent<Image>();
            image.color = new Color(1f, 1f, 1f, 0.01f);
            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.text = string.Empty;
                label.color = new Color(0f, 0f, 0f, 0f);
            }

            return button;
        }

        private static void StyleMrkremeButton(Button button)
        {
            StyleMrkremeButton(button, MrkremeLime, Color.black);
        }

        private static void StyleMrkremeButton(Button button, Color backgroundColor, Color textColor)
        {
            if (button == null)
            {
                return;
            }

            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = backgroundColor;
            }

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.color = textColor;
                label.fontSize = Mathf.Max(label.fontSize, 34f);
                label.fontStyle = FontStyles.Bold;
            }
        }

        private static void StyleKeyboardButton(Button button)
        {
            if (button == null)
            {
                return;
            }

            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = new Color(0.35f, 0.36f, 0.31f, 0.88f);
            }

            var label = button.GetComponentInChildren<TextMeshProUGUI>();
            if (label != null)
            {
                label.color = Color.white;
                label.fontSize = 28f;
                label.fontStyle = FontStyles.Bold;
            }
        }

        private static void StyleCaptureButton(Button button)
        {
            if (button == null)
            {
                return;
            }

            var image = button.GetComponent<Image>();
            if (image != null)
            {
                image.color = Color.white;
            }
        }

        private static void CreatePanelBlock(Transform parent, string name, Vector2 anchor, Vector2 size, Color color)
        {
            var panel = new GameObject(name, typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(parent, false);
            var rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;
            panel.GetComponent<Image>().color = color;
        }

        private void CreateSheetImage(Transform parent, string name, string resourceName, Rect pixelRectFromTopLeft, Vector2 anchor, Vector2 size)
        {
            var texture = LoadResourceTexture(resourceName);
            var host = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = size;

            var image = host.GetComponent<RawImage>();
            image.texture = texture;
            image.uvRect = PixelRectToUv(texture, pixelRectFromTopLeft);
            image.color = Color.white;
        }

        private static Rect PixelRectToUv(Texture texture, Rect pixelRectFromTopLeft)
        {
            if (texture == null || texture.width <= 0 || texture.height <= 0)
            {
                return new Rect(0f, 0f, 1f, 1f);
            }

            var x = pixelRectFromTopLeft.x / texture.width;
            var y = 1f - ((pixelRectFromTopLeft.y + pixelRectFromTopLeft.height) / texture.height);
            var width = pixelRectFromTopLeft.width / texture.width;
            var height = pixelRectFromTopLeft.height / texture.height;
            return new Rect(x, y, width, height);
        }

        private void CreateBackgroundImage(Transform parent, string name, string resourceName)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            var image = host.GetComponent<RawImage>();
            image.texture = LoadResourceTexture(resourceName);
            image.color = Color.white;
        }

        private Texture2D LoadResourceTexture(string resourceName)
        {
            if (resourceTextureCache.TryGetValue(resourceName, out var cached) && cached != null)
            {
                return cached;
            }

            var loaded = Resources.Load<Texture2D>($"{MrKremeResourceRoot}{resourceName}");
            if (loaded == null)
            {
                Debug.LogWarning($"Missing UI resource: {MrKremeResourceRoot}{resourceName}");
                return Texture2D.whiteTexture;
            }

            resourceTextureCache[resourceName] = loaded;
            return loaded;
        }

        private void OnDestroy()
        {
            TrackScreenExit();
            flowCancellation?.Cancel();
            flowCancellation?.Dispose();
            cameraCaptureService?.Dispose();
            StopArPreview();
            arTrackingProvider?.Dispose();
            arStickerRenderer.Dispose();
            StopMotionClipPlayback();
            ClearPreview(composedPreview, ref composedPreviewTexture);
            ClearPreview(motionPreview, ref motionPreviewTexture);
            ClearPreview(qrPreview, ref qrPreviewTexture);
            ClearArPreviewOverlay();
        }
    }
}
