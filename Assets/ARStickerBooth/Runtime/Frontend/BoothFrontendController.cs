using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;
using OpenCVForUnity.ObjdetectModule;
using OpenCVForUnity.UnityIntegration;
using PhotoBooth.Booth.AR;
using PhotoBooth.Booth.Domain;
using PhotoBooth.Booth.Services;
using PhotoBooth.Booth.Sync;
using TMPro;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.InputSystem;
using Rect = UnityEngine.Rect;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace PhotoBooth.Booth.Frontend
{
    public sealed class BoothFrontendController : MonoBehaviour
    {
        private const string MrKremeResourceRoot = "MrkremeUi/";
        private const string PaymentBypassSceneName = "PhotoBooth-Kooky";
        private const int NoArPresetIndex = -2;
        private const int VoucherCodeMaxLength = 24;
        private const string VoucherCodePrefix = "PB-";
        private const int VoucherSuccessCountdownSeconds = 5;
        private const float VoucherQrScanIntervalSeconds = 0.25f;
        private const float UsbVoucherScannerFlushSeconds = 0.35f;
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
        private static readonly bool UseUnity3dAr = false;
        private static readonly Vector2 MonsterFrameOverlaySize = new(950.64f, 534.3f);
        private static readonly Rect PreviewFrameSpriteRectFallback = new(473f, 161f, 3150f, 3774f);
        private static readonly Rect[] PreviewFrameCaptureSlots =
        {
            new(680f, 1619f, 1267f, 912f),
            new(2143f, 1619f, 1267f, 912f),
            new(680f, 455f, 1267f, 913f),
            new(2143f, 455f, 1267f, 913f)
        };
        private static readonly Rect[] ImagePreview1CaptureSlots =
        {
            new(62f, 234f, 2014f, 1128f)
        };
        private static readonly Rect[] ImagePreview2CaptureSlots =
        {
            new(79f, 931f, 1240f, 708f)
        };

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
        [SerializeField] private bool forceNoMonsterThemeSelection;
        [SerializeField] private bool skipPaymentScreen;
        [SerializeField] private int countdownSeconds = 3;
        [SerializeField] private int capturesPerSession = 1;
        [SerializeField] private int motionClipFramesPerSecond = 15;
        [SerializeField] private bool enableArTracking = true;
        [SerializeField] private int maxArFaces = 4;
        [SerializeField] private int arPreviewUpdateIntervalMs = 100;
        [SerializeField] private bool mirrorArOverlayHorizontally;
        [SerializeField] private bool showFaceMarkDebugLines = true;
        [SerializeField] private bool showArDebugTelemetry = true;
        [SerializeField] private bool showEditorTracked3dFaceGuidePreview;
        [SerializeField] private bool enableModelerFaceRig;
        [SerializeField] private bool showModelerFaceMeshWireframe;
        [SerializeField] private bool enableTracked3dFaceModel;
        [SerializeField] private bool showTracked3dFaceGuideModel;
        [SerializeField] private bool enableTracked3dFaceParts;
        [SerializeField] private Tracked3dFaceModelAlignmentMode tracked3dFaceGuideAlignmentMode = Tracked3dFaceModelAlignmentMode.CanonicalMatrix;
        [SerializeField] private float tracked3dFaceGuideScale = 2.35f;
        [SerializeField] private bool mirrorTracked3dFaceGuideX = true;
        [SerializeField] private bool invertTracked3dFaceGuideYaw = true;
        [SerializeField] private bool preserveTracked3dFaceGuideSceneTransform;
        [SerializeField] private Vector3 tracked3dFaceGuideEulerOffset = new(0f, 180f, 0f);
        [SerializeField] private Vector3 tracked3dFaceGuideLocalScale = new(10f, 10f, 10f);
        [SerializeField] private float tracked3dFaceGuideNeutralNoseBlend = 0.38f;
        [SerializeField] private float tracked3dFaceGuideSideNoseBlend = 0.85f;
        [SerializeField] private float tracked3dFaceGuideFullSideYawDegrees = 35f;
        [SerializeField] private Vector2 tracked3dFaceGuideScreenOffsetByEyeDistance = Vector2.zero;
        [SerializeField] private Vector3 tracked3dFaceGuideLocalOffset = new(-0.8f, 0.6f, 0f);
        [SerializeField] private GameObject faceModelPrefab;
        [SerializeField] private Tracked3dFacePartBinding[] tracked3dFaceParts = Array.Empty<Tracked3dFacePartBinding>();
        [SerializeField] private float faceMarkDebugLineThickness = 2f;
        [SerializeField] private Color faceMarkDebugLineColor = new(0f, 1f, 0.55f, 0.9f);
        [SerializeField] private string ffmpegExecutablePath = "ffmpeg";
        [SerializeField] private int ffmpegTimeoutSeconds = 20;
        [SerializeField] private CameraDeviceControllerKind cameraDeviceControllerKind = CameraDeviceControllerKind.None;
        [SerializeField] private bool enableObsbotPowerControl = false;
        [SerializeField] private string obsbotControlExecutablePath = "tools/obsbot-control/bin/obsbot-control";
        [SerializeField] private string obsbotControlDeviceName = "OBSBOT";
        [SerializeField] private int obsbotControlTimeoutMs = 8000;
        [SerializeField] private string externalCameraWakeCommand = string.Empty;
        [SerializeField] private string externalCameraSleepCommand = string.Empty;
        [SerializeField] private string externalCameraStatusCommand = string.Empty;
        [SerializeField] private int externalCameraCommandTimeoutMs = 8000;
        [SerializeField] private bool useGPhoto2RawCapture = true;
        [SerializeField] private bool useGPhoto2Preview = false;
        [SerializeField] private int gPhoto2PreviewFramesPerSecond = 30;
        [SerializeField] private bool gPhoto2PreviewFailureFallbackEnabled = true;
        [SerializeField] private string gPhoto2ExecutablePath = "/opt/homebrew/bin/gphoto2";
        [SerializeField] private int gPhoto2CaptureTimeoutMs = 20000;
        [SerializeField] private bool gPhoto2KillPtpcameraBeforeCommand = true;
        [SerializeField] private Vector2Int cameraCaptureSize = new(1280, 720);
        [SerializeField] private Vector2Int thumbnailSize = new(320, 180);
        [SerializeField] private Sprite[] captureForegroundTextures;
        [SerializeField] private string[] captureForegroundResourceNames =
        {
            "capture_foreground_01",
            "capture_foreground_02"
        };
        [SerializeField] private Sprite monsterFrameOverlaySprite;

        [SerializeField] private ArStickerPreset[] arStickerPresets = Array.Empty<ArStickerPreset>();
        [SerializeField] private BoothThemeOption[] themes =
        {
            new() { themeId = "theme_01", displayName = "Frame 1", priceMinorUnits = 12000, currencyCode = "THB", backendFrameId = "theme_01" },
            new() { themeId = "theme_02", displayName = "Frame 2", priceMinorUnits = 12000, currencyCode = "THB", backendFrameId = "theme_02" }
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
        [SerializeField] private TextMeshProUGUI captureCountText;
        [SerializeField] private GameObject countdownRoot;
        [SerializeField] private TextMeshProUGUI captureStartText;
        [SerializeField] private GameObject captureShotCountRoot;
        [SerializeField] private Image captureShotCountImage;
        [SerializeField] private Sprite[] captureShotCountSprites = Array.Empty<Sprite>();
        [SerializeField] private TextMeshProUGUI aiStyleText;
        [SerializeField] private TextMeshProUGUI nameEntryText;
        [SerializeField] private TextMeshProUGUI voucherCodeEntryText;
        [SerializeField] private TextMeshProUGUI voucherCodeCountText;
        [SerializeField] private GameObject voucherModalRoot;
        [SerializeField] private TextMeshProUGUI voucherModalMessageText;
        [SerializeField] private Button voucherModalConfirmButton;
        [SerializeField] private Button voucherScanButton;
        [SerializeField] private Button voucherCameraOnButton;
        [SerializeField] private Button voucherCameraOffButton;
        [SerializeField] private TextMeshProUGUI voucherScanStatusText;
        [SerializeField] private GameObject voucherScanPreviewFrame;
        [SerializeField] private RawImage voucherScanPreview;
        [SerializeField] private GameObject voucherRemoteScanPanel;
        [SerializeField] private RawImage voucherRemoteQrImage;
        [SerializeField] private TextMeshProUGUI voucherRemoteScanStatusText;
        [SerializeField] private Button voucherRemoteRefreshButton;
        [SerializeField] private TextMeshProUGUI downloadUrlText;
        [SerializeField] private TextMeshProUGUI printStatusText;
        [SerializeField] private TextMeshProUGUI arDebugText;
        [SerializeField] private TextMeshProUGUI qrLoadingText;
        [SerializeField] private RawImage cameraPreview;
        [SerializeField] private RawImage arModelOverlay;
        [SerializeField] private RawImage arPreviewOverlay;
        [SerializeField] private Image captureForegroundOverlay;
        [SerializeField] private Sprite[] captureFrameOneOverlays = Array.Empty<Sprite>();
        [SerializeField] private Sprite[] captureFrameTwoOverlays = Array.Empty<Sprite>();
        [SerializeField] private RawImage motionPreview;
        [SerializeField] private RawImage composedPreview;
        [SerializeField] private RawImage qrPreview;
        [SerializeField] private Image qrPreviewImage;
        [SerializeField] private Image previewFrameImage;
        [SerializeField] private Sprite[] previewFrameSprites = Array.Empty<Sprite>();
        [SerializeField] private Rect[] previewFrameCaptureSlots = Array.Empty<Rect>();
        [SerializeField] private Button captureButton;
        // [SerializeField] private Button captureRetakeButton;
        // [SerializeField] private Button retakeButton;
        [SerializeField] private Button continueButton;
        [SerializeField] private Button printButton;
        [SerializeField] private Button doneButton;

        private readonly BoothImageComposer composer = new();
        private readonly BoothFfmpegMotionEncoder motionEncoder = new();
        private readonly ArStickerRenderer arStickerRenderer = new();
        private readonly ArTrackingStabilizer arTrackingStabilizer = new();
        private BoothCameraCaptureService cameraCaptureService;
        private GPhoto2CameraCaptureService gPhoto2CaptureService;
        private BoothCameraCaptureService voucherScannerCameraService;
        private IArTrackingProvider arTrackingProvider;
        private ArTrackingFrame latestArTrackingFrame = ArTrackingFrame.Empty;
        private BoothFrontendAnalyticsClient backendAnalytics;
        private BoothUiScreenId currentScreen = BoothUiScreenId.Attract;
        private float currentScreenStartedAt;
        private BoothJob currentJob;
        private BoothThemeOption selectedTheme;
        private ArStickerPreset selectedArPreset;
        private BoothAiStyleOption selectedAiStyle;
        private BoothCaptureClip latestMotionClip;
        private readonly List<string> capturedRawImagePaths = new();
        private readonly List<string> capturedMotionFramePaths = new();
        private int capturedPhotoCount;
        private int captureForegroundCaptureNumber = 1;
        private int pendingArPresetIndex = -1;
        private int selectedArPresetIndex = -1;
        private int selectedArStickerIndex = -1;
        private Texture2D motionPreviewTexture;
        private Texture2D composedPreviewTexture;
        private Texture2D captureReviewTexture;
        private Texture2D qrPreviewTexture;
        private Sprite qrPreviewSprite;
        private Sprite previewFrameRuntimeSprite;
        [SerializeField] private TextMeshProUGUI[] previewFrameFromNameText;
        private readonly RawImage[] previewFrameSlotImages = new RawImage[4];
        private readonly Texture2D[] previewFrameSlotTextures = new Texture2D[4];
        private int previewFrameLayoutIndex = -1;
        private Texture2D arOverlayPreviewTexture;
        private readonly List<ArPreviewStickerObject> arPreviewStickerObjects = new();
        private readonly List<Image> arFaceMarkLineImages = new();
        private ArPreviewFaceAnchor arPreviewFaceAnchor;
        private ArPreviewFaceRig arPreviewFaceRig;
        private ArPreviewFaceModelRig arPreviewFaceModelRig;
        private readonly Dictionary<string, Texture2D> resourceTextureCache = new();
        private CancellationTokenSource flowCancellation;
        private CancellationTokenSource motionPlaybackCancellation;
        private CancellationTokenSource livePhotoPlaybackCancellation;
        private CancellationTokenSource previewFrameMotionPlaybackCancellation;
        private CancellationTokenSource qrLoadingCancellation;
        private CancellationTokenSource arPreviewCancellation;
        private Texture2D arFrameTexture;
        private BoothRawCaptureUploadQueue rawCaptureUploadQueue;
        private bool rawCaptureUploadStarted;
        private bool previewScreenShowsFinalDownload;
        private bool printRequestStarted;
        private bool isCaptureReviewing;
        private GameObject captureIntroLozado;
        private GameObject captureTrainRoot;
        private readonly GameObject[] captureTrainLevels = new GameObject[3];
        private bool captureReviewRetakeUsed;
        private bool isStartingCapturePreview;
        private ICameraDeviceController cameraDeviceController;
        private string cameraDeviceControllerSignature = string.Empty;
        private bool hasDefaultCameraPreviewRect;
        private Vector2 defaultCameraPreviewAnchorMin;
        private Vector2 defaultCameraPreviewAnchorMax;
        private Vector2 defaultCameraPreviewPivot;
        private Vector2 defaultCameraPreviewAnchoredPosition;
        private Vector2 defaultCameraPreviewSizeDelta;
        private Quaternion defaultCameraPreviewLocalRotation;
        private Vector3 defaultCameraPreviewLocalScale;
        private bool loggedFirstArFrame;
        private int lastLoggedArFaceCount = -1;
        private bool mediaPipeArUnavailable;
        private string mediaPipeFallbackReason = string.Empty;
        private double lastArDebugTimestamp;
        private float arDebugFps;
        private bool isBusy;
        private int pendingThemeIndex = -1;
        private int pendingImagePreviewIndex = 1;
        private bool pendingThemeUsesMonsterFrame;
        private bool selectedThemeUsesMonsterFrame;
        private int selectedImagePreviewIndex = 1;
        private string passengerName = string.Empty;
        private string voucherCode = string.Empty;
        private string pendingVoucherCheckoutToken = string.Empty;
        private string pendingVoucherCode = string.Empty;
        private string pendingVoucherIdempotencyKey = string.Empty;
        private long pendingVoucherRedemptionId;
        private int pendingVoucherRemainingUsesAfterApply = -1;
        private int voucherAttemptCounter;
        private bool voucherSuccessContinueRequested;
        private bool isVoucherScannerStarting;
        private bool isConfirmingScannedVoucher;
        private bool isUsbVoucherScannerInputHooked;
        private float usbVoucherScannerLastInputAt;
        private string usbVoucherScannerBuffer = string.Empty;
        private CancellationTokenSource voucherScannerCancellation;
        private CancellationTokenSource voucherRemoteScanCancellation;
        private Texture2D voucherRemoteQrTexture;
        private string voucherRemoteScanSessionToken = string.Empty;
        private QRCodeDetector voucherQrDetector;
        private Mat voucherQrRgbaMat;
        private Mat voucherQrGrayMat;
        private Mat voucherQrPointsMat;
        private readonly List<string> voucherQrDecodedInfo = new();
        private readonly List<Mat> voucherQrStraightQrcode = new();

        private sealed class VoucherApiException : Exception
        {
            public VoucherApiException(string code, string message, long responseCode)
                : base(string.IsNullOrWhiteSpace(message) ? "Voucher request failed." : message)
            {
                Code = code ?? string.Empty;
                ResponseCode = responseCode;
            }

            public string Code { get; }
            public long ResponseCode { get; }
        }

        [Serializable]
        private sealed class BoothApiErrorPayload
        {
            public string code;
            public string message;
        }

        [Serializable]
        private sealed class VoucherValidateRequest
        {
            public string job_id;
            public string voucher_code;
            public string device_id;
        }

        [Serializable]
        private sealed class VoucherRemoteScanCreateRequest
        {
            public string job_id;
            public string device_id;
        }

        [Serializable]
        private sealed class VoucherRemoteScanAckRequest
        {
            public string status;
            public string device_id;
        }

        [Serializable]
        private sealed class VoucherReserveRequest
        {
            public string job_id;
            public string checkout_token;
            public string idempotency_key;
            public long gross_amount_minor;
            public string currency;
            public string device_id;
            public string theme_id;
            public string image_preview_id;
            public string passenger_name;
            public long amount_minor_units;
            public string payment_reference;
            public string session_started_at_utc;
        }

        [Serializable]
        private sealed class VoucherRedemptionActionRequest
        {
            public string device_id;
        }

        [Serializable]
        private sealed class VoucherValidateData
        {
            public bool valid;
            public string project_id;
            public string project_code;
            public long voucher_id;
            public string benefit_type;
            public long benefit_value_minor;
            public long benefit_percent;
            public int remaining_uses;
            public string checkout_token;
        }

        [Serializable]
        private sealed class VoucherStatusData
        {
            public bool exists;
            public string project_id;
            public string project_code;
            public string code_masked;
            public string status;
            public bool usable_now;
            public bool has_been_used;
            public bool quota_exhausted;
            public int used_count;
            public int max_uses;
            public int remaining_uses;
            public string benefit_type;
            public long benefit_value_minor;
            public long benefit_percent;
            public string valid_from;
            public string valid_until;
        }

        [Serializable]
        private sealed class VoucherRemoteScanSessionData
        {
            public string session_token;
            public string project_id;
            public string device_id;
            public string job_id;
            public string status;
            public string voucher_code;
            public VoucherStatusData voucher_status;
            public string scan_url;
            public string qr_png_url;
            public string expires_at;
        }

        [Serializable]
        private sealed class VoucherReserveData
        {
            public long redemption_id;
            public string status;
            public long gross_amount_minor;
            public long discount_amount_minor;
            public long net_amount_minor;
            public string currency_code;
            public bool payment_required;
        }

        [Serializable]
        private sealed class VoucherRedemptionActionData
        {
            public long redemption_id;
            public string status;
            public bool released;
        }

        [Serializable]
        private sealed class VoucherValidateEnvelope
        {
            public bool success;
            public VoucherValidateData data;
            public BoothApiErrorPayload error;
        }

        [Serializable]
        private sealed class VoucherRemoteScanSessionEnvelope
        {
            public bool success;
            public VoucherRemoteScanSessionData data;
            public BoothApiErrorPayload error;
        }

        [Serializable]
        private sealed class VoucherReserveEnvelope
        {
            public bool success;
            public VoucherReserveData data;
            public BoothApiErrorPayload error;
        }

        [Serializable]
        private sealed class VoucherRedemptionActionEnvelope
        {
            public bool success;
            public VoucherRedemptionActionData data;
            public BoothApiErrorPayload error;
        }

#if UNITY_EDITOR
        private void OnEnable()
        {
            if (Application.isPlaying)
            {
                return;
            }

            ScheduleEditorVoucherEntryScreenInstall();
            ScheduleEditorTracked3dFaceGuidePreview();
        }

        private void OnValidate()
        {
            NormalizeCaptureSettings();
            if (Application.isPlaying)
            {
                return;
            }

            ScheduleEditorVoucherEntryScreenInstall();
            ScheduleEditorTracked3dFaceGuidePreview();
        }

        private void ScheduleEditorVoucherEntryScreenInstall()
        {
            EditorApplication.delayCall -= ApplyEditorVoucherEntryScreenInstall;
            EditorApplication.delayCall += ApplyEditorVoucherEntryScreenInstall;
        }

        private void ApplyEditorVoucherEntryScreenInstall()
        {
            EditorApplication.delayCall -= ApplyEditorVoucherEntryScreenInstall;
            if (this == null || Application.isPlaying)
            {
                return;
            }

            if (FindScreenRoot(BoothUiScreenId.ArtStyleSelect) == null)
            {
                return;
            }

            if (FindScreenRoot(BoothUiScreenId.VoucherEntry) == null)
            {
                EnsureVoucherEntryScreen();
            }

            BindUiEvents();
            EditorUtility.SetDirty(this);
            if (gameObject.scene.IsValid())
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
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

            if (!UseUnity3dAr || !enableTracked3dFaceModel || !showEditorTracked3dFaceGuidePreview)
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
            NormalizeCaptureSettings();
            if (!autoBootstrapInPhotoBoothScene && runtime == null)
            {
                enabled = false;
                return;
            }

            runtime ??= FindFirstObjectByType<BoothRuntimeBootstrap>();
            EnsureDefaultThemes();
            EnsureDefaultAiStyles();
            EnsureDefaultArStickerPresets();
            ApplyPortraitResolution();

            if (autoBuildUi)
            {
                BuildRuntimeUi();
            }

            EnsureVoucherEntryScreen();
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

        private void NormalizeCaptureSettings()
        {
            capturesPerSession = Mathf.Max(1, capturesPerSession);
            countdownSeconds = Mathf.Max(1, countdownSeconds);
        }

        private int ResolveCapturesPerSession()
        {
            NormalizeCaptureSettings();
            return capturesPerSession;
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

        public void EnsureEditableVoucherEntryScreenInScene()
        {
            EnsureVoucherEntryScreen();
            BindUiEvents();
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }

        private void Update()
        {
            if (currentScreen != BoothUiScreenId.VoucherEntry)
            {
                return;
            }

            ProcessUsbVoucherScannerInput();
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
                selectedArPreset = null;
                selectedAiStyle = null;
                pendingThemeIndex = -1;
                pendingImagePreviewIndex = 1;
                pendingThemeUsesMonsterFrame = !ShouldForceNoMonsterThemeSelection();
                selectedThemeUsesMonsterFrame = false;
                selectedImagePreviewIndex = 1;
                pendingArPresetIndex = -1;
                selectedArPresetIndex = -1;
                selectedArStickerIndex = -1;
                passengerName = string.Empty;
                voucherCode = string.Empty;
                ClearPendingVoucherState();
                ResetCaptureSequence();
                UpdateNameEntryDisplay();
                UpdateVoucherCodeDisplay();
                latestMotionClip = null;
                EnsureDefaultArStickerPresets();
                ApplyNoArPresetSelection(updateStatus: false, trackSelection: false);
                ResetSessionSelectionVisuals();
                StopMotionClipPlayback();
                StopLivePhotoPreviewPlayback();
                StopPreviewFrameMotionPlayback();
                ClearCaptureReviewImage();
                ClearPreview(motionPreview, ref motionPreviewTexture);
                ClearPreview(composedPreview, ref composedPreviewTexture);
                ClearPreviewFrameSlots();
                ClearQrPreview();
                ApplyMonsterFrameSelection(pendingThemeUsesMonsterFrame);
                UpdateMonsterToggleSelectionVisuals(pendingThemeUsesMonsterFrame);
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
            ChooseThemeCandidateFromUi(themeIndex, pendingThemeUsesMonsterFrame);
        }

        public void ChooseNoMonsterThemeCandidateFromUi(int themeIndex)
        {
            ChooseThemeCandidateFromUi(themeIndex, false);
        }

        public void ChooseMonsterThemeCandidateFromUi(int themeIndex)
        {
            ChooseThemeCandidateFromUi(themeIndex, true);
        }

        private void ChooseThemeCandidateFromUi(int themeIndex, bool useMonsterFrame)
        {
            EnsureDefaultThemes();
            if (themeIndex < 0 || themeIndex >= themes.Length)
            {
                SetStatus("Frame is not configured.");
                return;
            }

            useMonsterFrame &= !ShouldForceNoMonsterThemeSelection();
            pendingThemeIndex = themeIndex;
            pendingThemeUsesMonsterFrame = useMonsterFrame;
            var theme = ResolveTheme(themeIndex);
            pendingImagePreviewIndex = ResolveImagePreviewIndex(theme, themeIndex);
            ApplyMonsterFrameSelection(useMonsterFrame);
            UpdateMonsterToggleSelectionVisuals(useMonsterFrame);
            SetStatus($"Selected frame: {theme.displayName}{BuildMonsterFrameStatusSuffix(useMonsterFrame)}. Tap confirm to continue.");
            priceText?.SetText(FormatPrice(theme));
        }

        public void SelectNoMonsterFilterFromUi()
        {
            SelectNoMonsterFrameFromUi();
        }

        public void SelectMonsterFilterFromUi()
        {
            SelectMonsterFrameFromUi();
        }

        public void SelectNoMonsterFrameFromUi()
        {
            pendingThemeUsesMonsterFrame = false;
            ApplyMonsterFrameSelection(false);
            UpdateMonsterToggleSelectionVisuals(false);
            if (pendingThemeIndex >= 0)
            {
                ChooseThemeCandidateFromUi(pendingThemeIndex, false);
            }
        }

        public void SelectMonsterFrameFromUi()
        {
            if (ShouldForceNoMonsterThemeSelection())
            {
                SelectNoMonsterFrameFromUi();
                return;
            }

            pendingThemeUsesMonsterFrame = true;
            ApplyMonsterFrameSelection(true);
            UpdateMonsterToggleSelectionVisuals(true);
            if (pendingThemeIndex >= 0)
            {
                ChooseThemeCandidateFromUi(pendingThemeIndex, true);
            }
        }

        public void ConfirmThemeSelectionFromUi()
        {
            if (pendingThemeIndex < 0)
            {
                SetStatus("Choose a frame first.");
                return;
            }
            Debug.Log($"Theme index: {pendingThemeIndex}, imagePreviewIndex: {pendingImagePreviewIndex}");

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
                selectedImagePreviewIndex = ResolveImagePreviewIndex(selectedTheme, themeIndex);
                selectedThemeUsesMonsterFrame = pendingThemeUsesMonsterFrame && !ShouldForceNoMonsterThemeSelection();
                ApplyMonsterFrameSelection(selectedThemeUsesMonsterFrame);
                previewFrameLayoutIndex = -1;
                previewFrameImage = null;
                currentJob = runtime.SessionService.CreateJob(selectedTheme.priceMinorUnits, selectedTheme.currencyCode);
                currentJob = runtime.SessionService.SelectTheme(currentJob.JobId, ResolveBackendImagePreviewId(selectedTheme));
                selectedAiStyle = ResolveAiStyle(0);
                currentJob = runtime.SessionService.SelectAiStyle(currentJob.JobId, selectedAiStyle.styleId, selectedAiStyle.aiPrompt);
                priceText?.SetText(FormatPrice(selectedTheme));
                await TrackAsync("booth_frontend_theme_selected");
                runtime.TrackFeatureUsed($"theme_selected:{selectedTheme.themeId}");
                runtime.TrackFeatureUsed($"frame_overlay_selected:{ResolveMonsterFrameOverlayId(selectedThemeUsesMonsterFrame)}");
                Debug.Log($"Selected photo booth layout: themeIndex={themeIndex}, imagePreviewIndex={selectedImagePreviewIndex}, backendFrameId={currentJob.ThemeId}, frameOverlay={ResolveMonsterFrameOverlayId(selectedThemeUsesMonsterFrame)}");
                if (ShouldSkipPaymentScreen())
                {
                    currentJob = runtime.SessionService.BypassPayment(currentJob.JobId);
                    ResetCaptureSequence();
                    EnsureDefaultArStickerPresets();
                    ApplyNoArPresetSelection(updateStatus: false, trackSelection: false);
                    await TrackAsync("booth_frontend_payment_bypassed", metadata: BuildAiMetadata());
                    await ShowCaptureAsync(BuildCaptureReadyMessage());
                }
                else
                {
                    SwitchScreen(BoothUiScreenId.PaymentMock, $"Selected {selectedTheme.displayName}{BuildMonsterFrameStatusSuffix(selectedThemeUsesMonsterFrame)}. Choose payment.");
                }
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

        public void ChooseArPresetCandidateFromUi(int presetIndex)
        {
            EnsureDefaultArStickerPresets();
            if (TryChooseStickerFromSinglePreset(presetIndex))
            {
                return;
            }

            if (presetIndex < 0 || presetIndex >= arStickerPresets.Length)
            {
                SetStatus("AR style is not configured.");
                return;
            }

            pendingArPresetIndex = presetIndex;
            selectedArPresetIndex = presetIndex;
            selectedArStickerIndex = -1;
            selectedArPreset = ResolveArPreset(presetIndex);
            SetStatus($"Selected AR: {selectedArPreset.displayName}.");
            UpdateArPreviewOverlay(latestArTrackingFrame);
            EnsureCaptureForegroundOverlay();
            _ = TrackAsync("booth_frontend_ar_preset_selected", metadata: BuildAiMetadata());
        }

        public void SelectMonsterArPresetFromUi()
        {
            EnsureDefaultArStickerPresets();
            if (arStickerPresets == null || arStickerPresets.Length == 0)
            {
                SetStatus("AR style is not configured.");
                return;
            }

            pendingArPresetIndex = 0;
            selectedArPresetIndex = 0;
            selectedArStickerIndex = -1;
            selectedArPreset = ResolveArPreset(0);
            SetStatus($"Selected AR: {selectedArPreset.displayName}.");
            UpdateArPreviewOverlay(latestArTrackingFrame);
            EnsureCaptureForegroundOverlay();
            _ = TrackAsync("booth_frontend_ar_preset_selected", metadata: BuildAiMetadata());
        }

        public void ChooseArStickerCandidateFromUi(int stickerIndex)
        {
            EnsureDefaultArStickerPresets();
            if (!TryChooseStickerFromSinglePreset(stickerIndex))
            {
                SetStatus("AR sticker is not configured.");
            }
        }

        public void SelectNoArPresetFromUi()
        {
            ApplyNoArPresetSelection(updateStatus: true, trackSelection: true);
        }

        private void ApplyNoArPresetSelection(bool updateStatus, bool trackSelection)
        {
            pendingArPresetIndex = NoArPresetIndex;
            selectedArPresetIndex = NoArPresetIndex;
            selectedArStickerIndex = -1;
            selectedArPreset = null;
            if (updateStatus)
            {
                SetStatus("Selected AR: None.");
            }

            ClearArPreviewOverlay();
            EnsureCaptureForegroundOverlay();
            if (trackSelection)
            {
                _ = TrackAsync("booth_frontend_ar_preset_selected", metadata: BuildAiMetadata());
            }
        }

        private bool TryChooseStickerFromSinglePreset(int stickerIndex)
        {
            if (arStickerPresets == null
                || arStickerPresets.Length != 1
                || arStickerPresets[0]?.stickers == null
                || stickerIndex < 0
                || stickerIndex >= arStickerPresets[0].stickers.Length)
            {
                return false;
            }

            pendingArPresetIndex = 0;
            selectedArPresetIndex = 0;
            selectedArStickerIndex = stickerIndex;
            selectedArPreset = arStickerPresets[0];
            var sticker = selectedArPreset.stickers[stickerIndex];
            SetStatus($"Selected AR: {ResolveArStickerDisplayName(sticker, stickerIndex)}.");
            UpdateArPreviewOverlay(latestArTrackingFrame);
            EnsureCaptureForegroundOverlay();
            _ = TrackAsync("booth_frontend_ar_preset_selected", metadata: BuildAiMetadata());
            return true;
        }

        public void ConfirmArPresetSelectionFromUi()
        {
            if (pendingArPresetIndex == NoArPresetIndex)
            {
                ApplyNoArPresetSelection(updateStatus: false, trackSelection: false);
                return;
            }

            if (pendingArPresetIndex < 0)
            {
                SetStatus("Choose an AR style first.");
                return;
            }

            if (arStickerPresets != null
                && arStickerPresets.Length == 1
                && pendingArPresetIndex == 0
                && selectedArPresetIndex == 0
                && selectedArStickerIndex < 0)
            {
                SelectMonsterArPresetFromUi();
                return;
            }

            ChooseArPresetCandidateFromUi(pendingArPresetIndex);
        }

        public async void SelectArPresetFromUi(int presetIndex)
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJobOrCreateQuickDemo(paymentConfirmed: true);
                ChooseArPresetCandidateFromUi(presetIndex);
                runtime.TrackFeatureUsed($"ar_preset_selected:{selectedArPreset?.presetId ?? "none"}");
                if (currentScreen != BoothUiScreenId.Capture)
                {
                    await ShowCaptureAsync(BuildCaptureReadyMessage());
                }
            }
            catch (Exception exception)
            {
                Debug.LogError($"AR preset selection failed: {exception}");
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

        public async void OpenVoucherEntryFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                if (pendingVoucherRedemptionId > 0)
                {
                    await ReleasePendingVoucherReservationAsync();
                    currentJob = CreateCheckoutRetryJobPreservingSelection(currentJob);
                }

                voucherCode = string.IsNullOrWhiteSpace(pendingVoucherCode) ? voucherCode : ExtractVoucherSuffix(pendingVoucherCode);
                UpdateVoucherCodeDisplay();
                SwitchScreen(BoothUiScreenId.VoucherEntry, "Enter voucher code.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Opening voucher entry failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void BackFromVoucherEntryFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                StopVoucherScanner("voucher_back");
                SwitchScreen(BoothUiScreenId.PaymentMock, "Choose payment.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Voucher back navigation failed: {exception}");
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
                var paymentReference = pendingVoucherRedemptionId > 0
                    ? $"MOCK-{currentJob.JobId}-VOUCHER-{pendingVoucherRedemptionId}"
                    : $"MOCK-{currentJob.JobId}";
                if (currentJob.Status == BoothJobStatus.ThemeSelected)
                {
                    currentJob = runtime.SessionService.SetPaymentPending(currentJob.JobId, paymentReference);
                }

                currentJob = runtime.SessionService.ConfirmPayment(currentJob.JobId, paymentReference);
                if (pendingVoucherRedemptionId > 0)
                {
                    await ApplyReservedVoucherAsync(pendingVoucherRedemptionId, flowCancellation.Token);
                }

                await TrackAsync("booth_frontend_mock_payment_confirmed", metadata: BuildAiMetadata());
                UpdateNameEntryDisplay();
                ResetCaptureSequence();
                EnsureDefaultArStickerPresets();
                ApplyNoArPresetSelection(updateStatus: false, trackSelection: false);

                await ShowCaptureAsync(BuildCaptureReadyMessage());
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

        public void AppendVoucherCharacterFromUi(string character)
        {
            var normalizedCharacter = NormalizeVoucherCodeCharacter(character);
            if (string.IsNullOrWhiteSpace(normalizedCharacter) || voucherCode.Length >= ResolveVoucherSuffixMaxLength())
            {
                return;
            }

            voucherCode += normalizedCharacter;
            UpdateVoucherCodeDisplay();
        }

        public void BackspaceVoucherFromUi()
        {
            if (string.IsNullOrEmpty(voucherCode))
            {
                return;
            }

            voucherCode = voucherCode[..^1];
            UpdateVoucherCodeDisplay();
        }

        public async void ConfirmVoucherFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                await ConfirmVoucherAsync(BuildFullVoucherCode(), "voucher_confirm");
            }
            catch (VoucherApiException exception)
            {
                Debug.LogWarning($"Voucher confirmation rejected: {exception.Code} {exception.Message}");
                ShowVoucherRetryModal(ResolveVoucherFailureMessage(exception));
            }
            catch (Exception exception)
            {
                Debug.LogError($"Voucher confirmation failed: {exception}");
                ShowVoucherRetryModal(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        private async Task ConfirmScannedVoucherAsync(string normalizedVoucherCode)
        {
            if (isConfirmingScannedVoucher)
            {
                return;
            }

            isConfirmingScannedVoucher = true;
            if (!await BeginBusyAsync())
            {
                isConfirmingScannedVoucher = false;
                return;
            }

            try
            {
                await ConfirmVoucherAsync(normalizedVoucherCode, "voucher_scanned");
            }
            catch (VoucherApiException exception)
            {
                Debug.LogWarning($"Scanned voucher confirmation rejected: {exception.Code} {exception.Message}");
                ShowVoucherRetryModal(ResolveVoucherFailureMessage(exception));
            }
            catch (Exception exception)
            {
                Debug.LogError($"Scanned voucher confirmation failed: {exception}");
                ShowVoucherRetryModal(exception.Message);
            }
            finally
            {
                isConfirmingScannedVoucher = false;
                EndBusy();
            }
        }

        private async Task ConfirmVoucherAsync(string rawVoucherCode, string stopScannerReason)
        {
            StopVoucherScanner(stopScannerReason);
            EnsureCurrentJob();
            if (currentJob.Status != BoothJobStatus.ThemeSelected)
            {
                currentJob = CreateCheckoutRetryJobPreservingSelection(currentJob);
            }

            var normalizedVoucherCode = NormalizeVoucherCode(rawVoucherCode);
            if (string.IsNullOrWhiteSpace(ExtractVoucherSuffix(normalizedVoucherCode)))
            {
                SetStatus("Enter a voucher code first.");
                return;
            }

            voucherCode = ExtractVoucherSuffix(normalizedVoucherCode);
            UpdateVoucherCodeDisplay();

            var validate = await ValidateVoucherAsync(normalizedVoucherCode, flowCancellation.Token);
            pendingVoucherCheckoutToken = validate.checkout_token ?? string.Empty;
            pendingVoucherCode = normalizedVoucherCode;
            pendingVoucherIdempotencyKey = ResolveVoucherIdempotencyKey(normalizedVoucherCode);
            pendingVoucherRemainingUsesAfterApply = Mathf.Max(0, validate.remaining_uses - 1);

            var reserve = await ReserveVoucherAsync(validate.checkout_token, pendingVoucherIdempotencyKey, flowCancellation.Token);
            pendingVoucherRedemptionId = reserve.redemption_id;
            currentJob = runtime.SessionService.SetPaymentPending(currentJob.JobId, $"VOUCHER-{reserve.redemption_id}");
            await TrackAsync(
                "booth_frontend_voucher_reserved",
                metadata: new Dictionary<string, string>
                {
                    ["voucher_code"] = normalizedVoucherCode,
                    ["voucher_status"] = reserve.status ?? string.Empty,
                    ["voucher_redemption_id"] = reserve.redemption_id.ToString(),
                    ["voucher_net_amount_minor"] = reserve.net_amount_minor.ToString()
                });

            if (!string.Equals(reserve.status, "APPLIED", StringComparison.OrdinalIgnoreCase))
            {
                await ApplyReservedVoucherAsync(reserve.redemption_id, flowCancellation.Token);
            }

            currentJob = runtime.SessionService.ConfirmPayment(currentJob.JobId, $"VOUCHER-{reserve.redemption_id}");
            pendingVoucherCheckoutToken = string.Empty;
            var remainingUsesAfterApply = pendingVoucherRemainingUsesAfterApply;
            ClearPendingVoucherState(clearEnteredCode: false);
            await ShowVoucherConfirmedCountdownAndCaptureAsync(remainingUsesAfterApply);
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
                SwitchScreen(BoothUiScreenId.Preview, "Preparing print...");
                ClearQrPreview();
                ShowCountdownClipInPreviewFrame(latestMotionClip);
                await ComposeCapturedPhotosAsync();
                await SendCurrentPrintJobAsync(showPrintingScreen: false, completeSessionOnSuccess: false);
                ShowQrLoading(ResolveQrPreviewRawImage(), ResolveQrPreviewImage());
                await PublishCurrentJobAndShowPreviewAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError($"Name confirmation and final QR flow failed: {exception}");
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
            UpdatePassengerNameLabelTexts();
        }

        private void UpdateVoucherCodeDisplay()
        {
            var fullCode = BuildFullVoucherCode();
            voucherCodeEntryText?.SetText(fullCode);
            voucherCodeCountText?.SetText($"{voucherCode.Length} / {ResolveVoucherSuffixMaxLength()}");
            UpdateVoucherPlaceholderSlots(fullCode);
        }

        private string BuildFullVoucherCode()
        {
            return $"{VoucherCodePrefix}{voucherCode}";
        }

        private int ResolveVoucherSuffixMaxLength()
        {
            var slotLabels = FindVoucherPlaceholderSlotLabels();
            if (slotLabels.Count > VoucherCodePrefix.Length)
            {
                return slotLabels.Count - VoucherCodePrefix.Length;
            }

            return Mathf.Max(0, VoucherCodeMaxLength - VoucherCodePrefix.Length);
        }

        private void UpdateVoucherPlaceholderSlots(string fullCode)
        {
            var slotLabels = FindVoucherPlaceholderSlotLabels();
            if (slotLabels.Count == 0)
            {
                return;
            }

            for (var i = 0; i < slotLabels.Count; i++)
            {
                var label = slotLabels[i];
                if (label == null)
                {
                    continue;
                }

                label.SetText(i < fullCode.Length ? fullCode[i].ToString() : string.Empty);
            }
        }

        private List<TextMeshProUGUI> FindVoucherPlaceholderSlotLabels()
        {
            var labels = new List<TextMeshProUGUI>();
            var root = FindScreenRoot(BoothUiScreenId.VoucherEntry);
            var grid = root != null ? FindDescendant(root, "Placeholder/Grid") : null;
            if (grid == null)
            {
                return labels;
            }

            for (var i = 0; i < grid.childCount; i++)
            {
                var child = grid.GetChild(i);
                var label = child.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null)
                {
                    labels.Add(label);
                }
            }

            return labels;
        }

        public void StartVoucherScannerFromUi()
        {
            StartVoucherScanner();
        }

        public void TurnVoucherCameraOnFromUi()
        {
            StartVoucherScanner();
        }

        public void TurnVoucherCameraOffFromUi()
        {
            StopVoucherScanner("camera_off_button");
            SetVoucherScanStatus("Camera off");
        }

        private void StartVoucherScanner(bool restart = false)
        {
            if (currentScreen != BoothUiScreenId.VoucherEntry)
            {
                return;
            }

            if (restart)
            {
                StopVoucherScanner("restart");
            }

            SetVoucherScanPreviewVisible(true);

            if (voucherScannerCameraService != null && voucherScannerCameraService.IsPreviewing)
            {
                SetVoucherScanStatus("Scanning...");
                return;
            }

            if (voucherScannerCancellation != null || isVoucherScannerStarting)
            {
                SetVoucherScanStatus("Scanning...");
                return;
            }

            if (voucherScannerCameraService != null && !voucherScannerCameraService.IsPreviewing)
            {
                voucherScannerCameraService.Dispose();
                voucherScannerCameraService = null;
            }

            voucherScannerCancellation = new CancellationTokenSource();
            RegisterUsbVoucherScannerInput();
            _ = RunVoucherScannerAsync(voucherScannerCancellation.Token);
        }

        private async Task RunVoucherScannerAsync(CancellationToken cancellationToken)
        {
            isVoucherScannerStarting = true;
            SetVoucherScanStatus("Scanning...");
            try
            {
                EnsureVoucherQrDetector();
                voucherScannerCameraService ??= CreateVoucherScannerCameraService();
                await WakeCameraDeviceAsync(cancellationToken);
                await voucherScannerCameraService.StartPreviewAsync(cancellationToken);
                SetVoucherScanStatus("Scanning...");
                while (!cancellationToken.IsCancellationRequested && currentScreen == BoothUiScreenId.VoucherEntry)
                {
                    TryDecodeVoucherQrFrame();
                    await Task.Delay(Mathf.RoundToInt(VoucherQrScanIntervalSeconds * 1000f), cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Voucher QR scanner unavailable: {exception.Message}");
                SetVoucherScanStatus("QR camera unavailable. Type code.");
                voucherScannerCameraService?.StopPreview();
                voucherScannerCameraService?.Dispose();
                voucherScannerCameraService = null;
                SafeCancelAndDisposeVoucherScannerCancellation();
                SetVoucherScanPreviewVisible(false);
                SleepObsbotAfterVoucherScannerStop("scanner_unavailable");
            }
            finally
            {
                isVoucherScannerStarting = false;
                if (voucherScannerCancellation != null && voucherScannerCancellation.IsCancellationRequested)
                {
                    SafeCancelAndDisposeVoucherScannerCancellation();
                }
            }
        }

        private BoothCameraCaptureService CreateVoucherScannerCameraService()
        {
            return new BoothCameraCaptureService(
                voucherScanPreview,
                cameraCaptureSize.x,
                cameraCaptureSize.y,
                preferredDeviceNames: runtime?.PreferredCameraDeviceNames,
                preferredDeviceDiscoveryTimeoutSeconds: runtime?.PreferredCameraDeviceDiscoveryTimeoutSeconds ?? 3,
                gPhoto2CaptureService: null,
                useGPhoto2Preview: false);
        }

        private void StopVoucherScanner(string reason = "stop")
        {
            if (voucherScannerCameraService != null && voucherScannerCameraService.IsPreviewing)
            {
                Debug.Log($"Voucher QR scanner stopping: reason={reason}, device={voucherScannerCameraService.CurrentDeviceName}");
            }

            SafeCancelAndDisposeVoucherScannerCancellation();

            isVoucherScannerStarting = false;
            usbVoucherScannerBuffer = string.Empty;
            UnregisterUsbVoucherScannerInput();
            voucherScannerCameraService?.StopPreview();
            voucherScannerCameraService?.Dispose();
            voucherScannerCameraService = null;
            ClearVoucherQrDecodeBuffers();
            SetVoucherScanPreviewVisible(false);
            SleepObsbotAfterVoucherScannerStop(reason);
        }

        private void SleepObsbotAfterVoucherScannerStop(string reason)
        {
            if (string.Equals(reason, "restart", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "voucher_detected", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "voucher_scanned", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "voucher_confirm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reason, "leave_voucher_for_capture", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _ = SleepCameraDeviceAsync();
        }

        private void SafeCancelAndDisposeVoucherScannerCancellation()
        {
            var cancellation = voucherScannerCancellation;
            voucherScannerCancellation = null;
            if (cancellation == null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void TryDecodeVoucherQrFrame()
        {
            if (voucherScannerCameraService == null || !voucherScannerCameraService.IsPreviewing)
            {
                return;
            }

            var frame = voucherScannerCameraService.CaptureCurrentFrameTexture();
            try
            {
                EnsureVoucherQrMats(frame.width, frame.height);
                OpenCVMatUtils.Texture2DToMat(frame, voucherQrRgbaMat);
                Imgproc.cvtColor(voucherQrRgbaMat, voucherQrGrayMat, Imgproc.COLOR_RGBA2GRAY);
                ClearVoucherQrDecodedCollections();
                if (!voucherQrDetector.detectAndDecodeMulti(voucherQrGrayMat, voucherQrDecodedInfo, voucherQrPointsMat, voucherQrStraightQrcode))
                {
                    return;
                }

                foreach (var payload in voucherQrDecodedInfo)
                {
                    if (TryApplyScannedVoucherPayload(payload))
                    {
                        return;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Voucher QR decode failed: {exception.Message}");
            }
            finally
            {
                Destroy(frame);
                ClearVoucherQrDecodedCollections();
            }
        }

        private void ProcessUsbVoucherScannerInput()
        {
#if ENABLE_LEGACY_INPUT_MANAGER
            var input = UnityEngine.Input.inputString;
            if (!string.IsNullOrEmpty(input))
            {
                foreach (var character in input)
                {
                    AppendUsbVoucherScannerCharacter(character);
                }
            }
#endif

#if ENABLE_INPUT_SYSTEM
            var keyboard = Keyboard.current;
            if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame))
            {
                FlushUsbVoucherScannerBuffer();
            }
#endif

            if (!string.IsNullOrWhiteSpace(usbVoucherScannerBuffer)
                && Time.realtimeSinceStartup - usbVoucherScannerLastInputAt >= UsbVoucherScannerFlushSeconds)
            {
                FlushUsbVoucherScannerBuffer();
            }
        }

        private void RegisterUsbVoucherScannerInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (isUsbVoucherScannerInputHooked || Keyboard.current == null)
            {
                return;
            }

            Keyboard.current.onTextInput += HandleUsbVoucherScannerTextInput;
            isUsbVoucherScannerInputHooked = true;
#endif
        }

        private void UnregisterUsbVoucherScannerInput()
        {
#if ENABLE_INPUT_SYSTEM
            if (!isUsbVoucherScannerInputHooked || Keyboard.current == null)
            {
                isUsbVoucherScannerInputHooked = false;
                return;
            }

            Keyboard.current.onTextInput -= HandleUsbVoucherScannerTextInput;
            isUsbVoucherScannerInputHooked = false;
#else
            isUsbVoucherScannerInputHooked = false;
#endif
        }

        private void HandleUsbVoucherScannerTextInput(char character)
        {
            if (currentScreen != BoothUiScreenId.VoucherEntry)
            {
                return;
            }

            AppendUsbVoucherScannerCharacter(character);
        }

        private void AppendUsbVoucherScannerCharacter(char character)
        {
            if (character is '\r' or '\n')
            {
                FlushUsbVoucherScannerBuffer();
                return;
            }

            if (!char.IsControl(character))
            {
                usbVoucherScannerBuffer += character;
                usbVoucherScannerLastInputAt = Time.realtimeSinceStartup;
            }
        }

        private void FlushUsbVoucherScannerBuffer()
        {
            var payload = usbVoucherScannerBuffer;
            usbVoucherScannerBuffer = string.Empty;
            if (!string.IsNullOrWhiteSpace(payload))
            {
                TryApplyScannedVoucherPayload(payload);
            }
        }

        private bool TryApplyScannedVoucherPayload(string payload)
        {
            if (!TryExtractScannedVoucherCode(payload, out var normalizedVoucherCode))
            {
                if (IsVoucherRemoteScanPayload(payload))
                {
                    SetVoucherScanStatus("Scan this QR with your phone");
                    return false;
                }

                SetVoucherScanStatus("Please scan voucher QR code");
                return false;
            }

            voucherCode = ExtractVoucherSuffix(normalizedVoucherCode);
            UpdateVoucherCodeDisplay();
            SetVoucherScanStatus("QR detected");
            StopVoucherScanner("voucher_detected");
            _ = ConfirmScannedVoucherAsync(normalizedVoucherCode);
            return true;
        }

        private static bool IsVoucherRemoteScanPayload(string payload)
        {
            return !string.IsNullOrWhiteSpace(payload)
                && payload.IndexOf("voucher-scan", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SetVoucherScanPreviewVisible(bool isVisible)
        {
            if (voucherScanPreviewFrame != null)
            {
                voucherScanPreviewFrame.SetActive(isVisible);
            }

            if (voucherScanPreview == null)
            {
                return;
            }

            voucherScanPreview.gameObject.SetActive(isVisible);
            voucherScanPreview.color = isVisible ? Color.white : new Color(1f, 1f, 1f, 0.08f);
            if (!isVisible)
            {
                voucherScanPreview.texture = null;
            }
        }

        private static bool TryExtractScannedVoucherCode(string payload, out string normalizedVoucherCode)
        {
            normalizedVoucherCode = string.Empty;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            var trimmed = payload.Trim();
            var prefixedCode = ExtractPrefixedVoucherCode(trimmed);
            if (!string.IsNullOrWhiteSpace(prefixedCode))
            {
                normalizedVoucherCode = NormalizeVoucherCode(prefixedCode);
                return normalizedVoucherCode.StartsWith(VoucherCodePrefix, StringComparison.OrdinalIgnoreCase);
            }

            var suffix = NormalizeVoucherCode(trimmed);
            if (IsPlainVoucherSuffix(suffix))
            {
                normalizedVoucherCode = NormalizeVoucherCode($"{VoucherCodePrefix}{suffix}");
                return true;
            }

            return false;
        }

        private static string ExtractPrefixedVoucherCode(string payload)
        {
            var upper = payload.ToUpperInvariant();
            var prefixIndex = upper.IndexOf(VoucherCodePrefix, StringComparison.Ordinal);
            if (prefixIndex < 0)
            {
                return string.Empty;
            }

            var chars = new List<char>();
            for (var i = prefixIndex; i < payload.Length; i++)
            {
                var character = char.ToUpperInvariant(payload[i]);
                if ((character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character is '-' or '_' or '.')
                {
                    chars.Add(character);
                    continue;
                }

                if (chars.Count > VoucherCodePrefix.Length)
                {
                    break;
                }
            }

            return new string(chars.ToArray());
        }

        private static bool IsPlainVoucherSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length < 6 || value.Length > VoucherCodeMaxLength - VoucherCodePrefix.Length)
            {
                return false;
            }

            var hasLetter = false;
            var hasDigit = false;
            foreach (var character in value)
            {
                if (character >= 'A' && character <= 'Z')
                {
                    hasLetter = true;
                    continue;
                }

                if (character >= '0' && character <= '9')
                {
                    hasDigit = true;
                    continue;
                }

                if (!((character >= 'A' && character <= 'Z') || (character >= '0' && character <= '9')))
                {
                    return false;
                }
            }

            return hasLetter && hasDigit;
        }

        private void SetVoucherScanStatus(string message)
        {
            voucherScanStatusText?.SetText(message ?? string.Empty);
        }

        public void RefreshVoucherRemoteScanFromUi()
        {
            StartVoucherRemoteScanSession(restart: true);
        }

        private void StartVoucherRemoteScanSession(bool restart = false)
        {
            if (currentScreen != BoothUiScreenId.VoucherEntry)
            {
                return;
            }

            EnsureVoucherRemoteScanUi(FindScreenRoot(BoothUiScreenId.VoucherEntry));
            SetVoucherRemoteScanPanelVisible(true);

            if (restart)
            {
                StopVoucherRemoteScanSession("CANCELLED");
                SetVoucherRemoteScanPanelVisible(true);
            }

            if (voucherRemoteScanCancellation != null)
            {
                SetVoucherRemoteScanStatus("Open this QR with your phone");
                return;
            }

            voucherRemoteScanCancellation = new CancellationTokenSource();
            _ = RunVoucherRemoteScanSessionAsync(voucherRemoteScanCancellation.Token);
        }

        private async Task RunVoucherRemoteScanSessionAsync(CancellationToken cancellationToken)
        {
            SetVoucherRemoteScanStatus("Preparing QR...");
            ClearVoucherRemoteQrTexture();
            try
            {
                EnsureCurrentJob();
                var session = await CreateVoucherRemoteScanSessionAsync(cancellationToken);
                voucherRemoteScanSessionToken = session.session_token ?? string.Empty;
                SetVoucherRemoteScanStatus("Open this QR with your phone");
                try
                {
                    await LoadVoucherRemoteQrTextureAsync(session.qr_png_url, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Debug.LogWarning($"Voucher remote QR image unavailable: {exception.Message}");
                    SetVoucherRemoteScanStatus(string.IsNullOrWhiteSpace(session.scan_url)
                        ? "QR unavailable. Tap refresh."
                        : $"QR unavailable\n{session.scan_url}");
                }

                while (!cancellationToken.IsCancellationRequested && currentScreen == BoothUiScreenId.VoucherEntry)
                {
                    await Task.Delay(1000, cancellationToken);
                    var polled = await PollVoucherRemoteScanSessionAsync(voucherRemoteScanSessionToken, cancellationToken);
                    if (polled == null)
                    {
                        continue;
                    }

                    if (string.Equals(polled.status, "PENDING", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (string.Equals(polled.status, "EXPIRED", StringComparison.OrdinalIgnoreCase))
                    {
                        SetVoucherRemoteScanStatus("QR expired. Tap refresh.");
                        StopVoucherRemoteScanSession(null);
                        return;
                    }

                    if ((string.Equals(polled.status, "ATTACHED", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(polled.status, "SUBMITTED_AVAILABLE", StringComparison.OrdinalIgnoreCase))
                        && TryExtractScannedVoucherCode(polled.voucher_code, out var normalizedVoucherCode))
                    {
                        voucherCode = ExtractVoucherSuffix(normalizedVoucherCode);
                        UpdateVoucherCodeDisplay();
                        SetVoucherRemoteScanStatus("Voucher attached");
                        var sessionToken = voucherRemoteScanSessionToken;
                        StopVoucherRemoteScanSession(null);
                        await ConfirmRemoteVoucherAsync(normalizedVoucherCode, sessionToken);
                        return;
                    }

                    if (string.Equals(polled.status, "SUBMITTED_REJECTED", StringComparison.OrdinalIgnoreCase))
                    {
                        var message = ResolveVoucherStatusFailureMessage(polled.voucher_status);
                        SetVoucherRemoteScanStatus(message);
                        StopVoucherRemoteScanSession("CANCELLED");
                        ShowVoucherRetryModal(message);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Voucher remote scan unavailable: {exception.Message}");
                SetVoucherRemoteScanStatus("Mobile link unavailable. Tap refresh.");
                StopVoucherRemoteScanSession(null);
            }
        }

        private async Task ConfirmRemoteVoucherAsync(string normalizedVoucherCode, string sessionToken)
        {
            if (isConfirmingScannedVoucher)
            {
                return;
            }

            isConfirmingScannedVoucher = true;
            if (!await BeginBusyAsync())
            {
                isConfirmingScannedVoucher = false;
                await AckVoucherRemoteScanSessionAsync(sessionToken, "CANCELLED", flowCancellation?.Token ?? CancellationToken.None);
                return;
            }

            try
            {
                await ConfirmVoucherAsync(normalizedVoucherCode, "voucher_scanned");
                await AckVoucherRemoteScanSessionAsync(sessionToken, "CONSUMED", flowCancellation?.Token ?? CancellationToken.None);
            }
            catch (VoucherApiException exception)
            {
                await AckVoucherRemoteScanSessionAsync(sessionToken, "CANCELLED", flowCancellation?.Token ?? CancellationToken.None);
                Debug.LogWarning($"Remote scanned voucher confirmation rejected: {exception.Code} {exception.Message}");
                ShowVoucherRetryModal(ResolveVoucherFailureMessage(exception));
            }
            catch (Exception exception)
            {
                await AckVoucherRemoteScanSessionAsync(sessionToken, "CANCELLED", flowCancellation?.Token ?? CancellationToken.None);
                Debug.LogError($"Remote scanned voucher confirmation failed: {exception}");
                ShowVoucherRetryModal(exception.Message);
            }
            finally
            {
                isConfirmingScannedVoucher = false;
                EndBusy();
            }
        }

        private void StopVoucherRemoteScanSession(string ackStatus)
        {
            var sessionToken = voucherRemoteScanSessionToken;
            voucherRemoteScanSessionToken = string.Empty;
            SafeCancelAndDisposeVoucherRemoteScanCancellation();
            if (!string.IsNullOrWhiteSpace(ackStatus) && !string.IsNullOrWhiteSpace(sessionToken))
            {
                _ = AckVoucherRemoteScanSessionAsync(sessionToken, ackStatus, flowCancellation?.Token ?? CancellationToken.None);
            }
        }

        private void SafeCancelAndDisposeVoucherRemoteScanCancellation()
        {
            var cancellation = voucherRemoteScanCancellation;
            voucherRemoteScanCancellation = null;
            if (cancellation == null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void SetVoucherRemoteScanStatus(string message)
        {
            voucherRemoteScanStatusText?.SetText(message ?? string.Empty);
        }

        private void SetVoucherRemoteScanPanelVisible(bool isVisible)
        {
            if (voucherRemoteScanPanel != null)
            {
                voucherRemoteScanPanel.SetActive(isVisible);
                if (isVisible)
                {
                    voucherRemoteScanPanel.transform.SetAsLastSibling();
                }
            }

            if (voucherRemoteQrImage != null)
            {
                voucherRemoteQrImage.gameObject.SetActive(isVisible);
            }
        }

        private static string ResolveVoucherStatusFailureMessage(VoucherStatusData status)
        {
            if (status == null || !status.exists || string.Equals(status.status, "NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                return "ไม่พบ Voucher";
            }

            if (status.quota_exhausted
                || status.has_been_used
                || string.Equals(status.status, "USED", StringComparison.OrdinalIgnoreCase))
            {
                return "Voucher นี้ถูกใช้ไปแล้ว";
            }

            return string.IsNullOrWhiteSpace(status.status) ? "Voucher ไม่พร้อมใช้งาน" : $"Voucher {status.status}";
        }

        private void ClearVoucherRemoteQrTexture()
        {
            if (voucherRemoteQrImage != null)
            {
                voucherRemoteQrImage.texture = null;
            }

            if (voucherRemoteQrTexture != null)
            {
                Destroy(voucherRemoteQrTexture);
                voucherRemoteQrTexture = null;
            }
        }

        private void EnsureVoucherQrDetector()
        {
            voucherQrDetector ??= new QRCodeDetector();
            voucherQrPointsMat ??= new Mat();
        }

        private void EnsureVoucherQrMats(int width, int height)
        {
            if (voucherQrRgbaMat == null || voucherQrRgbaMat.cols() != width || voucherQrRgbaMat.rows() != height)
            {
                voucherQrRgbaMat?.Dispose();
                voucherQrGrayMat?.Dispose();
                voucherQrRgbaMat = new Mat(height, width, CvType.CV_8UC4);
                voucherQrGrayMat = new Mat(height, width, CvType.CV_8UC1);
            }
        }

        private void ClearVoucherQrDecodedCollections()
        {
            foreach (var mat in voucherQrStraightQrcode)
            {
                mat?.Dispose();
            }

            voucherQrStraightQrcode.Clear();
            voucherQrDecodedInfo.Clear();
        }

        private void ClearVoucherQrDecodeBuffers()
        {
            ClearVoucherQrDecodedCollections();
            voucherQrDetector?.Dispose();
            voucherQrDetector = null;
            voucherQrRgbaMat?.Dispose();
            voucherQrRgbaMat = null;
            voucherQrGrayMat?.Dispose();
            voucherQrGrayMat = null;
            voucherQrPointsMat?.Dispose();
            voucherQrPointsMat = null;
        }

        private void ShowVoucherRetryModal(string message)
        {
            EnsureVoucherModal();
            if (voucherModalRoot == null)
            {
                SetStatus(message);
                return;
            }

            voucherModalMessageText?.SetText(string.IsNullOrWhiteSpace(message) ? "Voucher not found" : message);
            if (voucherModalConfirmButton != null)
            {
                SetButtonLabel(voucherModalConfirmButton, "confirm");
                voucherModalConfirmButton.interactable = true;
                voucherModalConfirmButton.onClick = new Button.ButtonClickedEvent();
                voucherModalConfirmButton.onClick.AddListener(RetryVoucherEntryFromModal);
                voucherModalConfirmButton.gameObject.SetActive(true);
            }

            voucherModalRoot.SetActive(true);
            voucherModalRoot.transform.SetAsLastSibling();
        }

        private void HideVoucherModal()
        {
            if (voucherModalRoot != null)
            {
                voucherModalRoot.SetActive(false);
            }
        }

        private void RetryVoucherEntryFromModal()
        {
            HideVoucherModal();
            if (currentScreen != BoothUiScreenId.VoucherEntry)
            {
                return;
            }

            SetVoucherScanStatus("Scanning...");
            StartVoucherScanner(restart: true);
            StartVoucherRemoteScanSession(restart: true);
        }

        private void ContinueVoucherSuccessFromUi()
        {
            voucherSuccessContinueRequested = true;
            if (voucherModalConfirmButton != null)
            {
                voucherModalConfirmButton.interactable = false;
            }
        }

        private async Task ShowVoucherConfirmedCountdownAndCaptureAsync(int remainingUsesAfterApply)
        {
            EnsureVoucherModal();
            voucherSuccessContinueRequested = false;
            var remainingUsesText = Mathf.Max(0, remainingUsesAfterApply).ToString();
            voucherModalMessageText?.SetText($"Voucher Confirm\nRemaining : {remainingUsesText}\nCountdown : {VoucherSuccessCountdownSeconds}");
            if (voucherModalRoot != null)
            {
                if (voucherModalConfirmButton != null)
                {
                    SetButtonLabel(voucherModalConfirmButton, "AGREE");
                    voucherModalConfirmButton.interactable = true;
                    voucherModalConfirmButton.onClick = new Button.ButtonClickedEvent();
                    voucherModalConfirmButton.onClick.AddListener(ContinueVoucherSuccessFromUi);
                    voucherModalConfirmButton.gameObject.SetActive(true);
                }

                voucherModalRoot.SetActive(true);
                voucherModalRoot.transform.SetAsLastSibling();
            }

            var deadline = Time.realtimeSinceStartup + VoucherSuccessCountdownSeconds;
            while (!voucherSuccessContinueRequested && Time.realtimeSinceStartup < deadline)
            {
                var remainingSeconds = Mathf.Max(1, Mathf.CeilToInt(deadline - Time.realtimeSinceStartup));
                voucherModalMessageText?.SetText($"Voucher Confirm\nRemaining : {remainingUsesText}\nCountdown : {remainingSeconds}");
                await Task.Delay(100, flowCancellation?.Token ?? CancellationToken.None);
            }

            HideVoucherModal();
            SetStatus("Voucher applied. Preparing camera...");
            ResetCaptureSequence();
            EnsureDefaultArStickerPresets();
            ApplyNoArPresetSelection(updateStatus: false, trackSelection: false);
            await ShowCaptureAsync(BuildCaptureReadyMessage());
        }

        private static string ResolveVoucherFailureMessage(VoucherApiException exception)
        {
            if (exception == null)
            {
                return "Voucher not found";
            }

            var code = exception.Code ?? string.Empty;
            if (code.Contains("QUOTA", StringComparison.OrdinalIgnoreCase)
                || code.Contains("USED", StringComparison.OrdinalIgnoreCase)
                || code.Contains("EXHAUSTED", StringComparison.OrdinalIgnoreCase)
                || exception.ResponseCode == 409)
            {
                return "Voucher already used";
            }

            if (code.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase)
                || exception.ResponseCode == 404)
            {
                return "Voucher not found";
            }

            return string.IsNullOrWhiteSpace(exception.Message) ? "Voucher not found" : exception.Message;
        }

        public async void CaptureFromUi()
        {
            if (isCaptureReviewing)
            {
                await ContinueCaptureReviewAsync();
                return;
            }

            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJobOrCreateQuickDemo(paymentConfirmed: true);
                ApplyMonsterFrameSelection(selectedThemeUsesMonsterFrame);
                if (currentJob.Status == BoothJobStatus.PaymentConfirmed || currentJob.Status == BoothJobStatus.PaymentBypassed)
                {
                    currentJob = runtime.SessionService.BeginCapture(currentJob.JobId);
                }

                cameraCaptureService ??= CreateCameraCaptureService();
                await WakeCameraDeviceAsync(flowCancellation.Token);
                await cameraCaptureService.StartPreviewAsync(flowCancellation.Token);
                ApplyHd33PreviewRotation(capturing: false);
                await StartArPreviewAsync(flowCancellation.Token);

                var totalCaptures = ResolveCapturesPerSession();
                BeginAutomaticCaptureSequenceUi();
                for (var captureNumber = capturedPhotoCount + 1; captureNumber <= totalCaptures; captureNumber += 1)
                {
                    UpdateAutomaticCaptureProgressUi(captureNumber, capturedPhotoCount);
                    SetStatus($"Get ready for photo {captureNumber} / {totalCaptures}.");

                    latestMotionClip = await RecordCountdownMotionClipAsync(capturedMotionFramePaths.Count);
                    if (latestMotionClip?.FramePaths != null)
                    {
                        capturedMotionFramePaths.AddRange(latestMotionClip.FramePaths);
                    }

                    SetCountdownVisible(false);
                    var rawPath = await CaptureRawImageAsync(captureNumber, flowCancellation.Token);
                    capturedRawImagePaths.Add(rawPath);
                    capturedPhotoCount = captureNumber;
                    UpdateAutomaticCaptureProgressUi(captureNumber, capturedPhotoCount);
                    SetStatus($"Captured {capturedPhotoCount} / {totalCaptures}.");
                }

                var allMotionFramePaths = capturedMotionFramePaths.ToArray();
                var aggregateDuration = Mathf.Max(1, countdownSeconds) * totalCaptures;
                var aggregateFrameRate = Mathf.Max(1f, allMotionFramePaths.Length / (float)aggregateDuration);
                var aggregateVideoPath = await EncodeMotionVideoAsync(allMotionFramePaths, aggregateFrameRate, flowCancellation.Token);
                latestMotionClip = new BoothCaptureClip
                {
                    FramePaths = allMotionFramePaths,
                    FrameRate = aggregateFrameRate,
                    VideoPath = aggregateVideoPath
                };

                currentJob = runtime.SessionService.MarkCaptured(
                    currentJob.JobId,
                    capturedPhotoCount,
                    GetLatestCapturedRawImagePath(),
                    GetCapturedRawImagePaths(),
                    latestMotionClip.FramePaths,
                    latestMotionClip.VideoPath);

                var acceptedRawPaths = GetCapturedRawImagePaths();
                for (var index = 0; index < acceptedRawPaths.Length; index += 1)
                {
                    EnqueueRawCaptureUpload(acceptedRawPaths[index], index + 1, totalCaptures);
                    Debug.Log($"Raw capture queued for upload: job={currentJob.JobId}, capture={index + 1}/{totalCaptures}, localPath={acceptedRawPaths[index]}");
                }

                isCaptureReviewing = false;
                ClearCaptureReviewImage();
                await TrackAsync("booth_frontend_capture_completed", metadata: BuildAiMetadata());
                SwitchScreen(BoothUiScreenId.ArtStyleSelect, "Type your name.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Capture failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                countdownText?.SetText(string.Empty);
                SetCountdownVisible(false);
                EndBusy();
            }
        }

#if false
        // ponytail: Legacy one-photo-per-click flow is intentionally retained for quick rollback.
        private async void CaptureOnePhotoPerClickFromUi()
        {
            var totalCaptures = ResolveCapturesPerSession();
            var captureNumber = Mathf.Clamp(capturedPhotoCount + 1, 1, totalCaptures);
            latestMotionClip = await RecordCountdownMotionClipAsync(capturedMotionFramePaths.Count);
            var rawPath = await CaptureRawImageAsync(captureNumber, flowCancellation.Token);
            capturedRawImagePaths.Add(rawPath);
            capturedPhotoCount = captureNumber;
            if (capturedPhotoCount < totalCaptures)
            {
                await ShowCaptureAsync(BuildCaptureReadyMessage());
            }
        }
#endif

        private async Task EnterCaptureReviewAsync()
        {
            EnsureCurrentJob();
            isCaptureReviewing = true;
            StopArPreview();
            cameraCaptureService?.StopPreview();
            ClearArPreviewOverlay();
            ShowCaptureReviewImage(GetLatestCapturedRawImagePath());
            UpdateCaptureReviewControls();
            SetStatus("Review your photo. Tap capture to continue or refresh to retake.");
            await TrackAsync("booth_frontend_capture_review_shown", metadata: BuildAiMetadata());
        }

        private async Task ContinueCaptureReviewAsync()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                var totalCaptures = ResolveCapturesPerSession();
                var acceptedRawPaths = GetCapturedRawImagePaths();
                for (var index = 0; index < acceptedRawPaths.Length; index += 1)
                {
                    EnqueueRawCaptureUpload(acceptedRawPaths[index], index + 1, totalCaptures);
                    Debug.Log($"Raw capture queued for upload: job={currentJob.JobId}, capture={index + 1}/{totalCaptures}, localPath={acceptedRawPaths[index]}");
                }

                isCaptureReviewing = false;
                ClearCaptureReviewImage();
                await TrackAsync("booth_frontend_capture_completed", metadata: BuildAiMetadata());
                SwitchScreen(BoothUiScreenId.ArtStyleSelect, "Type your name.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Capture review continue failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        private async Task RetakeCaptureReviewAsync()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                if (captureReviewRetakeUsed)
                {
                    SetStatus("Retake already used. Tap capture to continue.");
                    return;
                }

                isCaptureReviewing = false;
                captureReviewRetakeUsed = true;
                // ponytail: legacy retake reset stays disabled; retake now just advances to art style selection.
                await TrackAsync("booth_frontend_capture_review_retake_tapped", metadata: BuildAiMetadata());
                SwitchScreen(BoothUiScreenId.ArtStyleSelect, "Type your name.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Capture review retake failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        public async void RetakeFromUi()
        {
            if (isCaptureReviewing)
            {
                await RetakeCaptureReviewAsync();
                return;
            }

            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                EnsureCurrentJob();
                // ponytail: keep the old retake path commented out; the button now returns to art style selection.
                await TrackAsync("booth_frontend_retake_tapped", metadata: BuildAiMetadata());
                SwitchScreen(BoothUiScreenId.ArtStyleSelect, "Type your name.");
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
                if (previewScreenShowsFinalDownload && !string.IsNullOrWhiteSpace(currentJob?.DownloadUrl))
                {
                    DoneFromUi();
                    return;
                }

                await PublishCurrentJobAndShowPreviewAsync();
            }
            catch (Exception exception)
            {
                Debug.LogError($"Preview publish failed: {exception}");
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

        public async void FinishPreviewAndReturnHomeFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                await MarkCurrentJobDoneAsync();
                await ResetSessionToAttractAsync();
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
                await SendCurrentPrintJobAsync(showPrintingScreen: true, completeSessionOnSuccess: true);
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

        private async Task SendCurrentPrintJobAsync(bool showPrintingScreen, bool completeSessionOnSuccess)
        {
            EnsureCurrentJob();
            if (currentJob.PrintStatus == BoothPrintStatus.Printed)
            {
                Debug.Log($"Photo booth print skipped; already printed: job={currentJob.JobId}, printer={currentJob.PrinterName}");
                if (showPrintingScreen)
                {
                    printStatusText?.SetText(BuildPrintStatusMessage(currentJob));
                }

                return;
            }

            if (printRequestStarted && currentJob.PrintStatus == BoothPrintStatus.Printing)
            {
                Debug.Log($"Photo booth print skipped; print already in progress: job={currentJob.JobId}");
                return;
            }

            printRequestStarted = true;
            if (showPrintingScreen)
            {
                SwitchScreen(BoothUiScreenId.Printing, "Sending photo to printer...");
                printStatusText?.SetText("Printing your photo...");
            }
            else
            {
                SetStatus("Printing your photo...");
            }

            Debug.Log($"Photo booth print requested: job={currentJob.JobId}, printer=(local default), image={currentJob.Paths?.PrintImagePath ?? currentJob.Paths?.ComposedImagePath}");
            await TrackAsync("booth_frontend_print_requested");
            currentJob = await runtime.PrintService.PrintAsync(currentJob.JobId, null, 1, flowCancellation.Token);
            Debug.Log($"Photo booth print result: job={currentJob.JobId}, status={currentJob.Status}, printStatus={currentJob.PrintStatus}, printer={currentJob.PrinterName}, error={currentJob.LastPrintError ?? currentJob.LastError ?? string.Empty}");
            printStatusText?.SetText(BuildPrintStatusMessage(currentJob));

            if (currentJob.PrintStatus == BoothPrintStatus.Printed)
            {
                await TrackAsync("booth_frontend_print_completed");
                SetStatus(string.IsNullOrWhiteSpace(currentJob.DownloadUrl)
                    ? "Printed successfully. Preparing download link..."
                    : "Download link ready. Printed successfully.");
                if (completeSessionOnSuccess)
                {
                    SwitchScreen(BoothUiScreenId.Done, "Session complete.");
                }

                return;
            }

            printRequestStarted = false;
            if (currentJob.PrintStatus == BoothPrintStatus.RetryWait)
            {
                await TrackAsync("booth_frontend_print_retry_wait", metadata: new Dictionary<string, string> { ["error"] = currentJob.LastPrintError ?? string.Empty });
                SetStatus("Printer error. Please retry or call staff.");
                return;
            }

            await TrackAsync("booth_frontend_print_failed", metadata: new Dictionary<string, string> { ["error"] = currentJob.LastPrintError ?? currentJob.LastError ?? string.Empty });
            if (showPrintingScreen)
            {
                ShowError(BuildPrintStatusMessage(currentJob));
            }
            else
            {
                SetStatus(BuildPrintStatusMessage(currentJob));
            }
        }

        private static string BuildPrintStatusMessage(BoothJob job)
        {
            if (job == null)
            {
                return "Print job is unavailable.";
            }

            return job.PrintStatus switch
            {
                BoothPrintStatus.Printed => "Printed successfully.",
                BoothPrintStatus.Printing => "Printing your photo...",
                BoothPrintStatus.RetryWait => string.IsNullOrWhiteSpace(job.LastPrintError)
                    ? "Printer error. Please retry or call staff."
                    : $"Printer error: {job.LastPrintError}",
                BoothPrintStatus.FailedHard => string.IsNullOrWhiteSpace(job.LastPrintError ?? job.LastError)
                    ? "Print failed. Please call staff."
                    : $"Print failed: {job.LastPrintError ?? job.LastError}",
                _ => "Print is pending."
            };
        }

        public async void DoneFromUi()
        {
            await MarkCurrentJobDoneAsync();
            SwitchScreen(BoothUiScreenId.Done, "Session complete.");
        }

        private async Task MarkCurrentJobDoneAsync()
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
        }

        public async void ResetFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                await ResetSessionToAttractAsync();
            }
            finally
            {
                EndBusy();
            }
        }

        private async Task ResetSessionToAttractAsync()
        {
            try
            {
                await ReleasePendingVoucherReservationAsync();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Voucher release during reset failed: {exception.Message}");
            }

            currentJob = null;
            selectedTheme = null;
            selectedArPreset = null;
            selectedAiStyle = null;
            pendingThemeIndex = -1;
            pendingImagePreviewIndex = 1;
            pendingThemeUsesMonsterFrame = false;
            selectedThemeUsesMonsterFrame = false;
            selectedImagePreviewIndex = 1;
            pendingArPresetIndex = -1;
            selectedArPresetIndex = -1;
            selectedArStickerIndex = -1;
            passengerName = string.Empty;
            voucherCode = string.Empty;
            ClearPendingVoucherState();
            latestMotionClip = null;
            ResetCaptureSequence();
            cameraCaptureService?.StopPreview();
            StopMotionClipPlayback();
            StopLivePhotoPreviewPlayback();
            ClearCaptureReviewImage();
            ClearPreview(motionPreview, ref motionPreviewTexture);
            ClearPreview(composedPreview, ref composedPreviewTexture);
            ClearPreviewFrameSlots();
            ClearQrPreview();
            SetMonsterFramePreviewMode(false);
            ResetSessionSelectionVisuals();
            UpdateNameEntryDisplay();
            UpdateVoucherCodeDisplay();
            downloadUrlText?.SetText(string.Empty);
            printStatusText?.SetText(string.Empty);
            SwitchScreen(BoothUiScreenId.Attract, "Touch start to begin.");
        }

        private async Task ShowCaptureAsync(string message)
        {
            SwitchScreen(BoothUiScreenId.Capture, message);
            cameraCaptureService ??= CreateCameraCaptureService();
            await WakeCameraDeviceAsync(flowCancellation.Token);
            await cameraCaptureService.StartPreviewAsync(flowCancellation.Token);
            ApplyHd33PreviewRotation(capturing: false);
            SetStatus(await BuildCaptureStatusMessageAsync(message, flowCancellation.Token));
            await StartArPreviewAsync(flowCancellation.Token);
            EnsureCaptureForegroundOverlay();
            ApplyMonsterFrameSelection(selectedThemeUsesMonsterFrame);
            countdownText?.SetText(string.Empty);
            ClearCaptureReviewImage();
            isCaptureReviewing = false;
            UpdateCaptureReviewControls();
            UpdateCaptureCountText();
        }

        private BoothCameraCaptureService CreateCameraCaptureService()
        {
            var gPhoto2Capture = GetOrCreateGPhoto2CaptureService();
            return new BoothCameraCaptureService(
                cameraPreview,
                cameraCaptureSize.x,
                cameraCaptureSize.y,
                preferredDeviceNames: runtime?.PreferredCameraDeviceNames,
                preferredDeviceDiscoveryTimeoutSeconds: runtime?.PreferredCameraDeviceDiscoveryTimeoutSeconds ?? 3,
                gPhoto2CaptureService: gPhoto2Capture,
                useGPhoto2Preview: useGPhoto2Preview,
                gPhoto2PreviewFramesPerSecond: gPhoto2PreviewFramesPerSecond,
                gPhoto2PreviewFailureFallbackEnabled: gPhoto2PreviewFailureFallbackEnabled);
        }

        private GPhoto2CameraCaptureService GetOrCreateGPhoto2CaptureService()
        {
            gPhoto2CaptureService ??= new GPhoto2CameraCaptureService(
                gPhoto2ExecutablePath,
                gPhoto2CaptureTimeoutMs,
                gPhoto2KillPtpcameraBeforeCommand);
            return gPhoto2CaptureService;
        }

        private static string BuildCameraStatusSuffix(BoothCameraCaptureService cameraService)
        {
            if (cameraService == null || string.IsNullOrWhiteSpace(cameraService.CurrentDeviceName))
            {
                return "Camera: unavailable.";
            }

            var deviceName = cameraService.CurrentDeviceName;
            var deviceSource = cameraService.CurrentDeviceSource;
            return string.Equals(deviceName, deviceSource, StringComparison.OrdinalIgnoreCase)
                ? $"Camera: {deviceName}."
                : $"Camera: {deviceName} ({deviceSource}).";
        }

        private async Task<string> BuildCaptureStatusMessageAsync(string message, CancellationToken cancellationToken)
        {
            var status = $"{message} {BuildCameraStatusSuffix(cameraCaptureService)}";
            if (!ShouldAttemptGPhoto2StillCapture())
            {
                return status;
            }

            var gPhoto2Ready = await CheckGPhoto2CameraReadyAsync(cancellationToken);
            return $"{status} Camera/gPhoto2: {(gPhoto2Ready ? "ready" : "not detected")}.";
        }

        private bool ShouldAttemptGPhoto2StillCapture()
        {
            return useGPhoto2RawCapture
                   && cameraCaptureService != null
                   && !IsBlockedGPhoto2StillCaptureDevice(cameraCaptureService.CurrentDeviceName);
        }

        private static bool IsBlockedGPhoto2StillCaptureDevice(string deviceName)
        {
            return string.IsNullOrWhiteSpace(deviceName)
                   || BoothCameraCaptureService.IsObsbotDeviceName(deviceName)
                   || BoothCameraCaptureService.IsVirtualCameraDeviceName(deviceName)
                   || BoothCameraCaptureService.IsBuiltInCameraDeviceName(deviceName);
        }

        private async Task<bool> CheckGPhoto2CameraReadyAsync(CancellationToken cancellationToken)
        {
            var cameraName = await GetOrCreateGPhoto2CaptureService().DetectCameraNameAsync(cancellationToken);
            var ready = BoothCameraCaptureService.IsCanonLikeCameraDeviceName(cameraName);
            Debug.Log($"PhotoBooth gPhoto2 camera check: ready={ready}, device={cameraName ?? "(none)"}");
            return ready;
        }

        private Task WakeCameraDeviceAsync(CancellationToken cancellationToken = default)
        {
            return ResolveCameraDeviceController().WakeAsync(cancellationToken);
        }

        private Task SleepCameraDeviceAsync(CancellationToken cancellationToken = default)
        {
            return ResolveCameraDeviceController().SleepAsync(cancellationToken);
        }

        private ICameraDeviceController ResolveCameraDeviceController()
        {
            var signature = BuildCameraDeviceControllerSignature();
            if (cameraDeviceController != null && string.Equals(cameraDeviceControllerSignature, signature, StringComparison.Ordinal))
            {
                return cameraDeviceController;
            }

            cameraDeviceControllerSignature = signature;
            cameraDeviceController = CreateCameraDeviceController();
            return cameraDeviceController;
        }

        private ICameraDeviceController CreateCameraDeviceController()
        {
            return cameraDeviceControllerKind switch
            {
                CameraDeviceControllerKind.ObsbotCli when enableObsbotPowerControl => new ObsbotCliCameraDeviceController(
                    ResolveProjectRelativePath(obsbotControlExecutablePath),
                    obsbotControlDeviceName,
                    obsbotControlTimeoutMs),
                CameraDeviceControllerKind.ExternalCommand => new ExternalCommandCameraDeviceController(
                    externalCameraWakeCommand,
                    externalCameraSleepCommand,
                    externalCameraStatusCommand,
                    externalCameraCommandTimeoutMs),
                _ => NoopCameraDeviceController.Instance
            };
        }

        private string BuildCameraDeviceControllerSignature()
        {
            return string.Join(
                "|",
                cameraDeviceControllerKind.ToString(),
                enableObsbotPowerControl.ToString(),
                obsbotControlExecutablePath ?? string.Empty,
                obsbotControlDeviceName ?? string.Empty,
                obsbotControlTimeoutMs.ToString(),
                externalCameraWakeCommand ?? string.Empty,
                externalCameraSleepCommand ?? string.Empty,
                externalCameraStatusCommand ?? string.Empty,
                externalCameraCommandTimeoutMs.ToString());
        }

        private static string ResolveProjectRelativePath(string configuredPath)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            var trimmedPath = configuredPath.Trim();
            return Path.IsPathRooted(trimmedPath)
                ? trimmedPath
                : Path.GetFullPath(Path.Combine(Application.dataPath, "..", trimmedPath));
        }

        private async Task<BoothCaptureClip> RecordCountdownMotionClipAsync(int startingFrameIndex)
        {
            var framePaths = new List<string>();
            var framesPerSecond = Mathf.Clamp(motionClipFramesPerSecond, 1, 15);
            var durationSeconds = Mathf.Max(1, countdownSeconds);
            var frameInterval = 1f / framesPerSecond;
            var countdownDeadline = Time.realtimeSinceStartup + durationSeconds;
            var nextFrameAt = Time.realtimeSinceStartup;
            var lastRemaining = -1;

            SetCountdownVisible(true);

            while (Time.realtimeSinceStartup < countdownDeadline)
            {
                flowCancellation.Token.ThrowIfCancellationRequested();
                var remaining = Mathf.Max(1, Mathf.CeilToInt(countdownDeadline - Time.realtimeSinceStartup));
                if (remaining != lastRemaining)
                {
                    countdownText?.SetText(remaining.ToString());
                    lastRemaining = remaining;
                }

                if (Time.realtimeSinceStartup >= nextFrameAt)
                {
                    var outputIndex = Mathf.Max(0, startingFrameIndex + framePaths.Count);
                    framePaths.Add(cameraCaptureService.CaptureMotionFramePng(
                        currentJob.Paths.RawDirectory,
                        outputIndex,
                        ApplyCaptureEffects));
                    nextFrameAt = Mathf.Max(nextFrameAt + frameInterval, Time.realtimeSinceStartup + frameInterval);
                }

                await Task.Yield();
            }

            return new BoothCaptureClip
            {
                FramePaths = framePaths.ToArray(),
                FrameRate = Mathf.Max(1f, framePaths.Count / (float)durationSeconds),
                VideoPath = null
            };
        }

        private void ApplyCaptureEffects(Texture2D texture)
        {
            ApplyArStickers(texture, latestArTrackingFrame);
            ApplyTracked3dFaceModelToTexture(texture, latestArTrackingFrame);
        }

        private async Task<string> CaptureRawImageAsync(int captureNumber, CancellationToken cancellationToken)
        {
            var safeCaptureNumber = Mathf.Max(1, captureNumber);
            var useGPhoto2StillCapture = ShouldAttemptGPhoto2StillCapture() && await CheckGPhoto2CameraReadyAsync(cancellationToken);
            Debug.Log($"PhotoBooth still capture path: source={(useGPhoto2StillCapture ? "gphoto2" : "preview")}, device={cameraCaptureService?.CurrentDeviceName ?? "(unknown)"}, previewSource={cameraCaptureService?.CurrentDeviceSource ?? "none"}");

            if (!useGPhoto2StillCapture)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return cameraCaptureService.CapturePng(
                    currentJob.Paths.RawDirectory,
                    $"capture_{safeCaptureNumber:00}.png",
                    ApplyCaptureEffects);
            }

            if (currentJob?.Paths == null)
            {
                throw new InvalidOperationException("Current job paths are not ready for raw capture.");
            }

            SetStatus("Capturing with camera...");
            var cameraService = cameraCaptureService;
            if (cameraService != null)
            {
                if (cameraService.IsUsingGPhoto2Preview)
                {
                    await cameraService.PausePreviewAsync(cancellationToken);
                }
                else
                {
                    cameraService.StopPreview();
                }
            }

            try
            {
                return await GetOrCreateGPhoto2CaptureService().CaptureImageAndDownloadAsync(
                    currentJob.Paths.RawDirectory,
                    $"capture_{safeCaptureNumber:00}.jpg",
                    cancellationToken);
            }
            finally
            {
                if (cameraService != null)
                {
                    if (cameraService.IsUsingGPhoto2Preview)
                    {
                        cameraService.ResumePreview();
                    }
                    else if (currentScreen == BoothUiScreenId.Capture)
                    {
                        await cameraService.StartPreviewAsync(cancellationToken);
                    }
                }
            }
        }

        private async Task ComposeCapturedPhotosAndShowPreviewAsync()
        {
            await ComposeCapturedPhotosAsync();
            SwitchScreen(BoothUiScreenId.Preview, "Motion preview and styled capture ready. Continue or retake.");
            ClearQrPreview();
            ShowQrLoading(ResolveQrPreviewRawImage(), ResolveQrPreviewImage());
            ShowCountdownClipInPreviewFrame(latestMotionClip);
        }

        private async Task ComposeCapturedPhotosAsync()
        {
            EnsureCurrentJob();
            var rawPaths = GetCapturedRawImagePaths();
            var printablePassengerName = ResolvePassengerNameForLabel();
            if (!string.IsNullOrWhiteSpace(printablePassengerName)
                && !string.Equals(currentJob.PassengerName, printablePassengerName, StringComparison.Ordinal))
            {
                currentJob = runtime.SessionService.SetPassengerName(currentJob.JobId, printablePassengerName);
            }

            currentJob = runtime.SessionService.BeginComposing(currentJob.JobId);
            if (!string.IsNullOrWhiteSpace(printablePassengerName)
                && !string.Equals(currentJob.PassengerName, printablePassengerName, StringComparison.Ordinal))
            {
                currentJob = runtime.SessionService.SetPassengerName(currentJob.JobId, printablePassengerName);
            }

            Debug.Log($"Photo booth composition requested: job={currentJob.JobId}, passengerName={currentJob.PassengerName ?? string.Empty}, uiPassengerName={passengerName ?? string.Empty}");
            var composition = composer.ComposePhotoGrid(currentJob, rawPaths, thumbnailSize, selectedTheme, selectedAiStyle);
            currentJob = runtime.SessionService.MarkComposed(currentJob.JobId, composition.ComposedImagePath, composition.PrintImagePath, composition.ThumbnailPath);
            var liveImagePath = composer.ComposeLiveImage(currentJob, rawPaths, selectedTheme);
            Debug.Log($"Photo booth live image composed: job={currentJob.JobId}, passengerName={currentJob.PassengerName ?? string.Empty}, path={liveImagePath}");

            await TrackAsync("booth_frontend_composition_completed", metadata: BuildAiMetadata());
            Debug.Log($"Photo booth final image composed: job={currentJob.JobId}, passengerName={currentJob.PassengerName ?? string.Empty}, path={currentJob.Paths.ComposedImagePath}, printPath={currentJob.Paths.PrintImagePath ?? string.Empty}");
        }

        private async Task PublishCurrentJobAndShowPreviewAsync()
        {
            EnsureCurrentJob();
            previewScreenShowsFinalDownload = true;
            SwitchScreen(BoothUiScreenId.Preview, "Syncing photo...");
            StopMotionClipPlayback();
            await TrackAsync("booth_frontend_sync_started", metadata: BuildAiMetadata());
            if (rawCaptureUploadQueue != null)
            {
                await rawCaptureUploadQueue.FlushAsync(flowCancellation.Token, ResolvePassengerNameForLabel());
            }

            currentJob = await runtime.SyncService.SyncAsync(currentJob.JobId, flowCancellation.Token);
            if (currentJob.Status != BoothJobStatus.LinkReady && currentJob.Status != BoothJobStatus.Done)
            {
                var reason = !string.IsNullOrWhiteSpace(currentJob.LastUploadError)
                    ? currentJob.LastUploadError
                    : !string.IsNullOrWhiteSpace(currentJob.LastError)
                        ? currentJob.LastError
                        : $"Upload did not finish. Current status is {currentJob.Status} / {currentJob.UploadStatus}.";
                if (capturedRawImagePaths.Count >= ResolveCapturesPerSession())
                {
                    currentJob.DownloadUrl = BuildFallbackDownloadUrl(currentJob.JobId);
                    Debug.LogWarning($"Photo booth final upload did not complete, using raw-capture download page: job={currentJob.JobId}, status={currentJob.Status}, uploadStatus={currentJob.UploadStatus}, error={reason}, link={currentJob.DownloadUrl}");
                }
                else
                {
                    Debug.LogError($"Photo booth final upload did not complete: job={currentJob.JobId}, status={currentJob.Status}, uploadStatus={currentJob.UploadStatus}, error={reason}");
                    throw new InvalidOperationException(reason);
                }
            }

            if (string.IsNullOrWhiteSpace(currentJob.DownloadUrl))
            {
                throw new InvalidOperationException("Photo booth final upload completed without a download URL.");
            }

            downloadUrlText?.SetText(BuildDownloadSummary(currentJob));
            Debug.Log($"Photo booth download link ready: job={currentJob.JobId}, link={currentJob.DownloadUrl}");
            if (!string.IsNullOrWhiteSpace(currentJob.MotionClipUrl))
            {
                Debug.Log($"Photo booth motion link ready: job={currentJob.JobId}, link={currentJob.MotionClipUrl}");
            }

            runtime.TrackDownloadRequested(currentJob.JobId);
            await TrackAsync("booth_frontend_sync_completed", metadata: BuildAiMetadata());
            await TrackAsync("booth_frontend_download_link_shown", metadata: BuildAiMetadata());
            await LoadQrPreviewAsync(currentJob.DownloadUrl);
            SetStatus(BuildPostPublishStatusMessage(currentJob));
        }

        private static string BuildPostPublishStatusMessage(BoothJob job)
        {
            if (job == null)
            {
                return "Download link ready.";
            }

            return job.PrintStatus switch
            {
                BoothPrintStatus.Printed => "Download link ready. Printed successfully.",
                BoothPrintStatus.RetryWait => string.IsNullOrWhiteSpace(job.LastPrintError)
                    ? "Download link ready. Printer error, please retry."
                    : $"Download link ready. Printer error: {job.LastPrintError}",
                BoothPrintStatus.FailedHard => string.IsNullOrWhiteSpace(job.LastPrintError ?? job.LastError)
                    ? "Download link ready. Print failed."
                    : $"Download link ready. Print failed: {job.LastPrintError ?? job.LastError}",
                _ => "Download link ready."
            };
        }

        private string BuildFallbackDownloadUrl(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
            {
                return string.Empty;
            }

            var downloadBaseUrl = runtime != null ? runtime.BackendDownloadBaseUrl : string.Empty;
            if (string.IsNullOrWhiteSpace(downloadBaseUrl) && runtime != null && !string.IsNullOrWhiteSpace(runtime.BackendBoothApiBaseUrl))
            {
                downloadBaseUrl = $"{runtime.BackendBoothApiBaseUrl.Trim().TrimEnd('/')}/world-tour";
            }

            if (string.IsNullOrWhiteSpace(downloadBaseUrl))
            {
                return string.Empty;
            }

            return $"{downloadBaseUrl.Trim().TrimEnd('/')}/{Uri.EscapeDataString(jobId)}";
        }

        private string GetLatestCapturedRawImagePath()
        {
            if (capturedRawImagePaths.Count > 0)
            {
                return capturedRawImagePaths[^1];
            }

            if (!string.IsNullOrWhiteSpace(currentJob?.Paths?.RawImagePath))
            {
                return currentJob.Paths.RawImagePath;
            }

            throw new InvalidOperationException("Capture at least one photo before continuing.");
        }

        private string[] GetCapturedRawImagePaths()
        {
            if (capturedRawImagePaths.Count > 0)
            {
                return capturedRawImagePaths.ToArray();
            }

            if (currentJob?.Paths?.RawImagePaths != null && currentJob.Paths.RawImagePaths.Length > 0)
            {
                return currentJob.Paths.RawImagePaths;
            }

            return new[] { GetLatestCapturedRawImagePath() };
        }

        private void ShowCaptureReviewImage(string imagePath)
        {
            ClearCaptureReviewImage();
            if (cameraPreview == null || string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            {
                return;
            }

            captureReviewTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(captureReviewTexture, File.ReadAllBytes(imagePath)))
            {
                ClearCaptureReviewImage();
                return;
            }

            cameraPreview.texture = captureReviewTexture;
            cameraPreview.color = Color.white;
            cameraPreview.uvRect = new Rect(0f, 0f, 1f, 1f);
            ApplyHd33PreviewRotation(capturing: false);
        }

        private void ClearCaptureReviewImage()
        {
            if (cameraPreview != null && ReferenceEquals(cameraPreview.texture, captureReviewTexture))
            {
                cameraPreview.texture = null;
            }

            if (captureReviewTexture != null)
            {
                Destroy(captureReviewTexture);
                captureReviewTexture = null;
            }
        }

        private void ApplyHd33PreviewRotation(bool capturing)
        {
            if (cameraPreview == null || !IsHd33PreviewDevice())
            {
                return;
            }

            var euler = cameraPreview.rectTransform.localEulerAngles;
            euler.y = 0f;
            cameraPreview.rectTransform.localEulerAngles = euler;
        }

        private bool IsHd33PreviewDevice()
        {
            return cameraCaptureService != null
                && !string.IsNullOrWhiteSpace(cameraCaptureService.CurrentDeviceName)
                && BoothCameraCaptureService.IsHd33DeviceName(cameraCaptureService.CurrentDeviceName);
        }

        private void UpdateCaptureReviewControls()
        {
            // EnsureCaptureRetakeButton();
            // if (captureRetakeButton != null)
            // {
            //     captureRetakeButton.gameObject.SetActive(isCaptureReviewing);
            //     captureRetakeButton.interactable = isCaptureReviewing && !captureReviewRetakeUsed && !isBusy;
            //     SetButtonLabel(captureRetakeButton, captureReviewRetakeUsed ? "USED" : "RETAKE");
            // }

            // SetButtonLabel(captureButton, isCaptureReviewing ? "NEXT" : string.Empty);
        }

        // private void EnsureCaptureRetakeButton()
        // {
        //     if (captureRetakeButton != null)
        //     {
        //         return;
        //     }

        //     captureRetakeButton = FindButtonInScreen(
        //         BoothUiScreenId.Capture,
        //         "CaptureRetakeButton",
        //         "CaptureRefreshButton",
        //         "RetakeButton",
        //         "RefreshButton");
        //     if (captureRetakeButton == null)
        //     {
        //         var captureRoot = FindScreenRoot(BoothUiScreenId.Capture);
        //         if (captureRoot == null)
        //         {
        //             return;
        //         }

        //         captureRetakeButton = CreateButton(
        //             captureRoot,
        //             "CaptureRetakeButton",
        //             "RETAKE",
        //             new Vector2(0.22f, 0.075f),
        //             Vector2.zero,
        //             new Vector2(220f, 72f));
        //         StyleMrkremeButton(captureRetakeButton, new Color(0.94f, 0.91f, 0.82f, 1f), Color.black);
        //     }

        //     captureRetakeButton.onClick = new Button.ButtonClickedEvent();
        //     captureRetakeButton.onClick.AddListener(RetakeFromUi);
        //     captureRetakeButton.gameObject.SetActive(false);
        // }

        private void ResetCaptureSequence(bool resetReviewRetake = true)
        {
            if (!string.IsNullOrWhiteSpace(currentJob?.Paths?.RawDirectory) && Directory.Exists(currentJob.Paths.RawDirectory))
            {
                foreach (var motionFramePath in Directory.GetFiles(currentJob.Paths.RawDirectory, "motion_*.png"))
                {
                    File.Delete(motionFramePath);
                }

                var motionVideoPath = Path.Combine(currentJob.Paths.RawDirectory, "motion.mp4");
                if (File.Exists(motionVideoPath))
                {
                    File.Delete(motionVideoPath);
                }
            }

            capturedRawImagePaths.Clear();
            capturedMotionFramePaths.Clear();
            capturedPhotoCount = 0;
            rawCaptureUploadQueue = null;
            rawCaptureUploadStarted = false;
            printRequestStarted = false;
            previewScreenShowsFinalDownload = false;
            isCaptureReviewing = false;
            if (resetReviewRetake)
            {
                captureReviewRetakeUsed = false;
            }

            UpdateCaptureReviewControls();
            UpdateCaptureCountText();
            ResetAutomaticCaptureUi();
        }

        private void UpdateCaptureCountText()
        {
            if (captureShotCountImage != null)
            {
                UpdateCaptureCountSprite(capturedPhotoCount + 1);
                captureCountText?.gameObject.SetActive(false);
                UpdateCaptureForegroundOverlay();
                return;
            }

            var totalCaptures = ResolveCapturesPerSession();
            var displayCount = Mathf.Clamp(capturedPhotoCount + 1, 1, totalCaptures);
            if (capturedPhotoCount >= totalCaptures)
            {
                displayCount = totalCaptures;
            }

            captureCountText?.SetText(totalCaptures == 1 ? "AMOUNT 1 / 1" : $"AMOUNT {displayCount} / {totalCaptures}");
            if (captureCountText != null)
            {
                captureCountText.gameObject.SetActive(true);
            }
            UpdateCaptureForegroundOverlay();
        }

        private void BindAutomaticCaptureUi()
        {
            var root = FindScreenRoot(BoothUiScreenId.Capture);
            if (root == null)
            {
                return;
            }

            captureStartText ??= FindTextInScreen(BoothUiScreenId.Capture, "Start Text");
            captureShotCountRoot ??= root.Find("Count")?.gameObject;
            captureShotCountImage ??= FindImageInScreen(BoothUiScreenId.Capture, "Count");
            if (captureShotCountImage == null && captureShotCountRoot != null)
            {
                captureShotCountImage = captureShotCountRoot.GetComponent<Image>() ?? captureShotCountRoot.GetComponentInChildren<Image>(true);
            }
            countdownRoot ??= countdownText != null && countdownText.transform.parent != null
                ? countdownText.transform.parent.gameObject
                : countdownText?.gameObject;
            captureIntroLozado ??= root.Find("TopMetalPanel/Lozado")?.gameObject;
            captureTrainRoot ??= root.Find("TopMetalPanel/Train")?.gameObject;
            for (var index = 0; index < captureTrainLevels.Length; index += 1)
            {
                captureTrainLevels[index] ??= root.Find($"TopMetalPanel/Train/Level {index + 1}")?.gameObject;
            }
        }

        private void ResetAutomaticCaptureUi()
        {
            BindAutomaticCaptureUi();
            captureForegroundCaptureNumber = 1;
            UpdateCaptureForegroundOverlay();
            captureIntroLozado?.SetActive(true);
            captureTrainRoot?.SetActive(false);
            captureStartText?.SetText("TAKE A PHOTO");
            captureStartText?.gameObject.SetActive(true);
            if (captureShotCountImage != null)
            {
                captureShotCountImage.gameObject.SetActive(false);
            }
            if (captureCountText != null)
            {
                captureCountText.gameObject.SetActive(captureShotCountImage == null);
            }
            SetCountdownVisible(false);
            foreach (var level in captureTrainLevels)
            {
                level?.SetActive(false);
            }
        }

        private void BeginAutomaticCaptureSequenceUi()
        {
            BindAutomaticCaptureUi();
            captureIntroLozado?.SetActive(false);
            captureTrainRoot?.SetActive(true);
            captureStartText?.gameObject.SetActive(false);
            if (captureShotCountImage != null)
            {
                captureShotCountImage.gameObject.SetActive(true);
            }
            if (captureCountText != null)
            {
                captureCountText.gameObject.SetActive(captureShotCountImage == null);
            }
        }

        private void UpdateAutomaticCaptureProgressUi(int captureNumber, int completedCount)
        {
            captureForegroundCaptureNumber = Mathf.Clamp(captureNumber, 1, ResolveCapturesPerSession());
            UpdateCaptureForegroundOverlay();
            UpdateCaptureCountSprite(captureNumber);

            for (var index = 0; index < captureTrainLevels.Length; index += 1)
            {
                captureTrainLevels[index]?.SetActive(index == completedCount - 1);
            }
        }

        private void UpdateCaptureCountSprite(int captureNumber)
        {
            if (captureShotCountImage == null)
            {
                return;
            }

            captureShotCountImage.gameObject.SetActive(true);
            if (captureShotCountSprites != null && captureShotCountSprites.Length > 0)
            {
                var spriteIndex = Mathf.Clamp(captureNumber - 1, 0, captureShotCountSprites.Length - 1);
                var nextSprite = captureShotCountSprites[spriteIndex];
                if (nextSprite != null)
                {
                    captureShotCountImage.sprite = nextSprite;
                }
            }
        }

        private void SetCountdownVisible(bool visible)
        {
            if (countdownRoot != null)
            {
                countdownRoot.SetActive(visible);
            }
            else if (countdownText != null)
            {
                countdownText.gameObject.SetActive(visible);
            }

            if (!visible)
            {
                countdownText?.SetText(string.Empty);
            }
        }

        private void UpdateCaptureForegroundOverlay()
        {
            if (captureForegroundOverlay == null)
            {
                return;
            }

            Sprite sprite = null;
            if (selectedThemeUsesMonsterFrame)
            {
                sprite = monsterFrameOverlaySprite;
            }
            else
            {
                var overlays = ResolveSelectedCaptureFrameOverlays();
                if (overlays != null && overlays.Length > 0)
                {
                    var spriteIndex = Mathf.Clamp(captureForegroundCaptureNumber - 1, 0, overlays.Length - 1);
                    sprite = overlays[spriteIndex];
                }
            }

            captureForegroundOverlay.sprite = sprite;
            captureForegroundOverlay.type = Image.Type.Simple;
            captureForegroundOverlay.preserveAspect = false;
            captureForegroundOverlay.color = sprite != null ? Color.white : Color.clear;
        }

        private Sprite[] ResolveSelectedCaptureFrameOverlays()
        {
            return ResolveSelectedImagePreviewIndex() == 2
                ? captureFrameTwoOverlays
                : captureFrameOneOverlays;
        }

        private int GetCaptureForegroundIndex()
        {
            if (selectedArPresetIndex < 0)
            {
                return -1;
            }

            var themeIndex = ResolveSelectedThemeIndex();
            if (HasCaptureForegroundAt(themeIndex))
            {
                return themeIndex;
            }

            var totalCaptures = ResolveCapturesPerSession();
            var captureIndex = capturedPhotoCount >= totalCaptures
                ? totalCaptures - 1
                : Mathf.Clamp(capturedPhotoCount, 0, totalCaptures - 1);
            var themedIndex = themeIndex * totalCaptures + captureIndex;
            if (HasCaptureForegroundAt(themedIndex))
            {
                return themedIndex;
            }

            return HasCaptureForegroundAt(captureIndex) ? captureIndex : -1;
        }

        private bool HasCaptureForegroundAt(int index)
        {
            if (index < 0)
            {
                return false;
            }

            var hasTexture = captureForegroundTextures != null
                && index < captureForegroundTextures.Length
                && captureForegroundTextures[index] != null;
            var hasResource = captureForegroundResourceNames != null
                && index < captureForegroundResourceNames.Length
                && !string.IsNullOrWhiteSpace(captureForegroundResourceNames[index]);
            return hasTexture || hasResource;
        }

        private int ResolveSelectedThemeIndex()
        {
            EnsureDefaultThemes();
            if (selectedTheme == null || themes == null || themes.Length == 0)
            {
                return 0;
            }

            for (var index = 0; index < themes.Length; index += 1)
            {
                var theme = themes[index];
                if (ReferenceEquals(theme, selectedTheme)
                    || string.Equals(theme?.themeId, selectedTheme.themeId, StringComparison.Ordinal)
                    || string.Equals(theme?.BackendFrameId, selectedTheme.BackendFrameId, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return 0;
        }

        private Sprite ResolveCaptureForegroundSprite(int index)
        {
            if (captureForegroundTextures != null
                && index >= 0
                && index < captureForegroundTextures.Length
                && captureForegroundTextures[index] != null)
            {
                return captureForegroundTextures[index];
            }

            if (captureForegroundResourceNames == null || index < 0 || index >= captureForegroundResourceNames.Length)
            {
                return null;
            }

            var resourceName = NormalizeResourceName(captureForegroundResourceNames[index]);
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                return null;
            }

            return Resources.Load<Sprite>($"{MrKremeResourceRoot}{resourceName}");
        }

        private static string NormalizeResourceName(string resourceName)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                return string.Empty;
            }

            var normalized = resourceName.Trim();
            if (normalized.StartsWith(MrKremeResourceRoot, StringComparison.Ordinal))
            {
                normalized = normalized.Substring(MrKremeResourceRoot.Length);
            }

            var extension = Path.GetExtension(normalized);
            return string.IsNullOrEmpty(extension) ? normalized : normalized.Substring(0, normalized.Length - extension.Length);
        }

        private string BuildCaptureReadyMessage()
        {
            var totalCaptures = ResolveCapturesPerSession();
            var nextCapture = Mathf.Clamp(capturedPhotoCount + 1, 1, totalCaptures);
            return totalCaptures == 1
                ? "Ready to capture photo."
                : $"Ready to capture photo {nextCapture} / {totalCaptures}.";
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
            cameraCaptureService.CaptureCurrentFrameTexture(ref arFrameTexture);
            var rawFrame = await arTrackingProvider.TrackAsync(arFrameTexture, cancellationToken) ?? ArTrackingFrame.Empty;
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
                ShouldMirrorArOverlay());
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

            arStickerRenderer.ApplyToTexture(texture, frame, ResolveArStickers(), ShouldMirrorArOverlay());
        }

        private bool ShouldMirrorArOverlay()
        {
            return mirrorArOverlayHorizontally ^ IsHd33PreviewDevice();
        }

        private void ApplyTracked3dFaceModelToTexture(Texture2D texture, ArTrackingFrame frame)
        {
            if (!UseUnity3dAr
                || (!enableTracked3dFaceModel && !HasTracked3dFaceParts())
                || texture == null
                || frame?.Faces == null
                || frame.Faces.Length == 0)
            {
                return;
            }

            var rig = EnsureArPreviewFaceModelRig(editorPreview: false);
            ApplyTracked3dFaceGuideSettings(rig);
            var geometry = ArFaceProjectionGeometry.CreateStretched(texture.width, texture.height, texture.width, texture.height);
            rig.CompositeFaceModel(texture, frame.Faces[0], geometry);
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
            var wasCaptureScreen = currentScreen == BoothUiScreenId.Capture;
            var wasVoucherEntryScreen = currentScreen == BoothUiScreenId.VoucherEntry;

            foreach (var binding in screens)
            {
                if (binding?.root != null)
                {
                    binding.root.SetActive(binding.screenId == screenId);
                }
            }

            if (screenId != BoothUiScreenId.Capture)
            {
                if (cameraCaptureService != null && cameraCaptureService.IsPreviewing)
                {
                    Debug.Log($"PhotoBooth leaving Capture; stopping camera before entering {screenId}: device={cameraCaptureService.CurrentDeviceName}");
                }

                StopArPreview();
                cameraCaptureService?.StopPreview();
                if (wasCaptureScreen)
                {
                    _ = SleepCameraDeviceAsync();
                }
            }

            if (screenId != BoothUiScreenId.Preview)
            {
                StopMotionClipPlayback();
                StopLivePhotoPreviewPlayback();
                StopPreviewFrameMotionPlayback();
                HideQrLoading();
            }

            if (wasVoucherEntryScreen && screenId != BoothUiScreenId.VoucherEntry)
            {
                StopVoucherScanner(screenId == BoothUiScreenId.Capture ? "leave_voucher_for_capture" : "leave_voucher");
                StopVoucherRemoteScanSession("CANCELLED");
            }

            currentScreen = screenId;
            currentScreenStartedAt = Time.realtimeSinceStartup;
            titleText?.SetText(ScreenTitle(screenId));
            SetStatus(statusMessage);
            UpdatePassengerNameLabelTexts();
            _ = TrackAsync("booth_frontend_screen_entered", screenIdOverride: screenId);
            if (screenId == BoothUiScreenId.VoucherEntry)
            {
                StartVoucherScanner();
                StartVoucherRemoteScanSession();
            }

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

        private void StartLivePhotoPreviewPlayback(IReadOnlyList<string> rawImagePaths)
        {
            StopLivePhotoPreviewPlayback();
            if (composedPreview == null || rawImagePaths == null || rawImagePaths.Count == 0)
            {
                return;
            }

            livePhotoPlaybackCancellation = new CancellationTokenSource();
            _ = PlayLivePhotoLoopAsync(rawImagePaths, livePhotoPlaybackCancellation.Token);
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

        private async Task PlayLivePhotoLoopAsync(IReadOnlyList<string> rawImagePaths, CancellationToken cancellationToken)
        {
            try
            {
                const int delayMs = 450;
                while (!cancellationToken.IsCancellationRequested)
                {
                    foreach (var rawImagePath in rawImagePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!string.IsNullOrWhiteSpace(rawImagePath) && File.Exists(rawImagePath))
                        {
                            LoadLivePhotoFrame(rawImagePath);
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

        private void StopPreviewFrameMotionPlayback()
        {
            if (previewFrameMotionPlaybackCancellation == null)
            {
                return;
            }

            previewFrameMotionPlaybackCancellation.Cancel();
            previewFrameMotionPlaybackCancellation.Dispose();
            previewFrameMotionPlaybackCancellation = null;
        }

        private void StopLivePhotoPreviewPlayback()
        {
            if (livePhotoPlaybackCancellation == null)
            {
                return;
            }

            livePhotoPlaybackCancellation.Cancel();
            livePhotoPlaybackCancellation.Dispose();
            livePhotoPlaybackCancellation = null;
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

        private void LoadLivePhotoFrame(string framePath)
        {
            ClearPreview(composedPreview, ref composedPreviewTexture);
            if (composedPreview == null)
            {
                return;
            }

            composedPreviewTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(composedPreviewTexture, File.ReadAllBytes(framePath));
            composedPreview.texture = composedPreviewTexture;
            composedPreview.color = Color.white;
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

        private void ShowRawCapturesInPreviewFrame(IReadOnlyList<string> rawImagePaths)
        {
            StopPreviewFrameMotionPlayback();
            StopLivePhotoPreviewPlayback();
            ApplySelectedThemePreviewFrame();
            EnsurePreviewFrameSlots();
            if (previewFrameImage == null || rawImagePaths == null || rawImagePaths.Count == 0)
            {
                StartLivePhotoPreviewPlayback(rawImagePaths);
                return;
            }

            if (composedPreview != null)
            {
                composedPreview.gameObject.SetActive(false);
            }

            previewFrameImage.gameObject.SetActive(true);
            var captureSlots = ResolveSelectedPreviewCaptureSlots();
            for (var i = 0; i < previewFrameSlotImages.Length; i++)
            {
                var slot = previewFrameSlotImages[i];
                if (slot == null)
                {
                    continue;
                }

                if (i >= captureSlots.Length)
                {
                    ClearPreviewFrameSlot(i);
                    slot.gameObject.SetActive(false);
                    continue;
                }

                if (i >= rawImagePaths.Count || string.IsNullOrWhiteSpace(rawImagePaths[i]) || !File.Exists(rawImagePaths[i]))
                {
                    ClearPreviewFrameSlot(i);
                    slot.gameObject.SetActive(false);
                    continue;
                }

                LoadPreviewFrameSlot(i, rawImagePaths[i]);
                slot.gameObject.SetActive(true);
            }
        }

        private void ShowCountdownClipInPreviewFrame(BoothCaptureClip clip)
        {
            StopPreviewFrameMotionPlayback();
            StopLivePhotoPreviewPlayback();
            ApplySelectedThemePreviewFrame();
            EnsurePreviewFrameSlots();
            if (previewFrameImage == null || clip?.FramePaths == null || clip.FramePaths.Length == 0)
            {
                ShowRawCapturesInPreviewFrame(GetCapturedRawImagePaths());
                return;
            }

            if (composedPreview != null)
            {
                composedPreview.gameObject.SetActive(false);
            }

            previewFrameImage.gameObject.SetActive(true);
            var captureSlots = ResolveSelectedPreviewCaptureSlots();
            for (var i = 0; i < previewFrameSlotImages.Length; i++)
            {
                var slot = previewFrameSlotImages[i];
                if (slot == null)
                {
                    continue;
                }

                if (i >= captureSlots.Length)
                {
                    ClearPreviewFrameSlot(i);
                    slot.gameObject.SetActive(false);
                    continue;
                }

                slot.gameObject.SetActive(true);
            }

            previewFrameMotionPlaybackCancellation = new CancellationTokenSource();
            var captureCount = Mathf.Clamp(ResolveCapturesPerSession(), 1, previewFrameSlotImages.Length);
            _ = PlayPreviewFrameMotionLoopAsync(
                BuildMotionFrameSlotPaths(clip.FramePaths, captureCount),
                Mathf.Max(1f, clip.FrameRate),
                previewFrameMotionPlaybackCancellation.Token);
        }

        private static string[][] BuildMotionFrameSlotPaths(IReadOnlyList<string> framePaths, int captureCount)
        {
            var slots = new List<string>[4];
            for (var i = 0; i < slots.Length; i++)
            {
                slots[i] = new List<string>();
            }

            if (framePaths == null)
            {
                return Array.ConvertAll(slots, slot => slot.ToArray());
            }

            var activeSlotCount = Mathf.Clamp(captureCount, 1, slots.Length);
            for (var frameIndex = 0; frameIndex < framePaths.Count; frameIndex += 1)
            {
                // Motion files are numbered continuously across all captures, so split the
                // ordered sequence into one five-second segment per preview slot.
                var slotIndex = Mathf.Min(activeSlotCount - 1, frameIndex * activeSlotCount / framePaths.Count);
                slots[slotIndex].Add(framePaths[frameIndex]);
            }

            return Array.ConvertAll(slots, slot => slot.ToArray());
        }

        private async Task PlayPreviewFrameMotionLoopAsync(string[][] slotFrames, float frameRate, CancellationToken cancellationToken)
        {
            try
            {
                var delayMs = Mathf.RoundToInt(1000f / Mathf.Max(1f, frameRate));
                var frameIndex = 0;
                while (!cancellationToken.IsCancellationRequested)
                {
                    var slotLimit = ResolveSelectedPreviewCaptureSlots().Length;
                    for (var slotIndex = 0; slotIndex < previewFrameSlotImages.Length; slotIndex++)
                    {
                        if (slotIndex >= slotLimit)
                        {
                            continue;
                        }

                        var frames = slotIndex >= 0 && slotIndex < slotFrames.Length ? slotFrames[slotIndex] : null;
                        if (frames == null || frames.Length == 0)
                        {
                            continue;
                        }

                        var framePath = frames[frameIndex % frames.Length];
                        if (!string.IsNullOrWhiteSpace(framePath) && File.Exists(framePath))
                        {
                            LoadPreviewFrameSlot(slotIndex, framePath);
                            previewFrameSlotImages[slotIndex].gameObject.SetActive(true);
                        }
                    }

                    frameIndex += 1;
                    await Task.Delay(delayMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void ApplySelectedThemePreviewFrame()
        {
            previewFrameImage = ResolvePreviewFrameImageForSelectedLayout();
            if (previewFrameImage == null)
            {
                return;
            }

            SetPreviewFrameImageVisibility(previewFrameImage);
            var sprite = ResolveSelectedPreviewFrameSprite();
            if (sprite == null && selectedTheme != null)
            {
                sprite = selectedTheme.frameTemplateSprite
                    ?? LoadSelectedLabelPreviewSprite()
                    ?? selectedTheme.previewSprite;

                if (sprite == null && selectedTheme.frameTemplateTexture != null)
                {
                    if (previewFrameRuntimeSprite != null)
                    {
                        Destroy(previewFrameRuntimeSprite);
                        previewFrameRuntimeSprite = null;
                    }

                    var texture = selectedTheme.frameTemplateTexture;
                    previewFrameRuntimeSprite = Sprite.Create(
                        texture,
                        new Rect(0f, 0f, texture.width, texture.height),
                        Vector2.one * 0.5f);
                    sprite = previewFrameRuntimeSprite;
                }
            }

            if (sprite == null)
            {
                sprite = previewFrameImage.sprite;
            }

            if (sprite == null)
            {
                return;
            }

            previewFrameImage.sprite = sprite;
            previewFrameImage.preserveAspect = true;
            previewFrameImage.color = Color.white;
        }

        private Sprite ResolveSelectedPreviewFrameSprite()
        {
            var layoutIndex = ResolveSelectedImagePreviewIndex();
            var spriteIndex = layoutIndex - 1;
            return previewFrameSprites != null
                && spriteIndex >= 0
                && spriteIndex < previewFrameSprites.Length
                ? previewFrameSprites[spriteIndex]
                : null;
        }

        private Sprite LoadSelectedLabelPreviewSprite()
        {
            var layoutIndex = ResolveSelectedImagePreviewIndex();
            var path = Path.Combine(Application.dataPath, "UI", "Label", $"{layoutIndex}.png");
            if (!File.Exists(path))
            {
                return null;
            }

            if (previewFrameRuntimeSprite != null)
            {
                Destroy(previewFrameRuntimeSprite);
                previewFrameRuntimeSprite = null;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path)))
            {
                Destroy(texture);
                return null;
            }

            previewFrameRuntimeSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                Vector2.one * 0.5f);
            return previewFrameRuntimeSprite;
        }

        private void EnsurePreviewFrameSlots()
        {
            var layoutIndex = ResolveSelectedImagePreviewIndex();
            if (previewFrameLayoutIndex != layoutIndex)
            {
                previewFrameLayoutIndex = layoutIndex;
                Array.Clear(previewFrameSlotImages, 0, previewFrameSlotImages.Length);
                previewFrameImage = null;
            }

            previewFrameImage = ResolvePreviewFrameImageForSelectedLayout();
            if (previewFrameImage == null)
            {
                return;
            }

            previewFrameImage.raycastTarget = false;
            SetFallbackPreviewFrameSlotsVisible(layoutIndex == 1);
            var spriteRect = ResolvePreviewFrameSpriteRect(previewFrameImage);
            var captureSlots = ResolveSelectedPreviewCaptureSlots();
            ApplyPreviewFramePassengerName();
            for (var i = 0; i < previewFrameSlotImages.Length; i++)
            {
                var isLayoutSpecificSlot = false;
                var existingSlot = previewFrameSlotImages[i];
                var isSceneAuthoredSlot = existingSlot != null;
                if (existingSlot == null)
                {
                    existingSlot = FindPreviewFrameSlot(i, out isLayoutSpecificSlot);
                    isSceneAuthoredSlot = existingSlot != null;
                }

                if (existingSlot != null)
                {
                    previewFrameSlotImages[i] = existingSlot;
                    if (!isLayoutSpecificSlot && previewFrameSlotImages[i].transform.parent != previewFrameImage.transform)
                    {
                        previewFrameSlotImages[i].transform.SetParent(previewFrameImage.transform, false);
                    }

                    previewFrameSlotImages[i].raycastTarget = false;
                }
                else
                {
                    previewFrameSlotImages[i] = CreatePreviewFrameSlot(previewFrameImage.transform, i);
                }

                if (i >= captureSlots.Length)
                {
                    previewFrameSlotImages[i].gameObject.SetActive(false);
                    continue;
                }

                if (!isSceneAuthoredSlot)
                {
                    PositionPreviewFrameSlot(previewFrameSlotImages[i].rectTransform, captureSlots[i], spriteRect, previewFrameImage.rectTransform);
                }

                previewFrameSlotImages[i].transform.SetAsLastSibling();
            }

            UpdatePreviewFrameSlotOverlays();
        }

        private void UpdatePreviewFrameSlotOverlays()
        {
            var overlays = ResolveSelectedCaptureFrameOverlays();
            for (var index = 0; index < previewFrameSlotImages.Length; index += 1)
            {
                var slot = previewFrameSlotImages[index];
                if (slot == null)
                {
                    continue;
                }

                var overlayName = $"PreviewFrameOverlay_{index + 1:00}";
                var overlayTransform = slot.transform.Find(overlayName);
                var overlay = overlayTransform != null ? overlayTransform.GetComponent<Image>() : null;
                if (overlay == null)
                {
                    var overlayObject = new GameObject(overlayName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    overlayObject.transform.SetParent(slot.transform, false);
                    overlay = overlayObject.GetComponent<Image>();
                }

                var sprite = overlays != null && index < overlays.Length ? overlays[index] : null;
                overlay.sprite = sprite;
                overlay.type = Image.Type.Simple;
                overlay.preserveAspect = false;
                overlay.raycastTarget = false;
                overlay.color = sprite != null ? Color.white : Color.clear;
                StretchGraphicToParent(overlay);
                overlay.gameObject.SetActive(sprite != null);
                overlay.transform.SetAsLastSibling();
            }
        }

        private Rect[] ResolveSelectedPreviewCaptureSlots()
        {
            var layoutIndex = ResolveSelectedImagePreviewIndex();
            var slotIndex = layoutIndex - 1;
            if (previewFrameCaptureSlots != null
                && slotIndex >= 0
                && slotIndex < previewFrameCaptureSlots.Length
                && previewFrameCaptureSlots[slotIndex].width > 0f
                && previewFrameCaptureSlots[slotIndex].height > 0f)
            {
                return new[]
                {
                    previewFrameCaptureSlots[slotIndex],
                    previewFrameCaptureSlots[slotIndex],
                    previewFrameCaptureSlots[slotIndex]
                };
            }

            return new[]
            {
                new Rect(0f, 0f, 1f, 1f),
                new Rect(0f, 0f, 1f, 1f),
                new Rect(0f, 0f, 1f, 1f)
            };
        }

        private void ApplyPreviewFramePassengerName()
        {
            SetPreviewFramePassengerNameTexts(ResolvePassengerNameForLabel());
        }

        private string ResolvePassengerNameForLabel()
        {
            var value = !string.IsNullOrWhiteSpace(currentJob?.PassengerName) ? currentJob.PassengerName : passengerName;
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        private void UpdatePassengerNameLabelTexts()
        {
            var value = ResolvePassengerNameForLabel();
            SetPreviewFramePassengerNameTexts(value);

            var root = FindScreenRoot(currentScreen);
            if (root == null)
            {
                root = transform;
            }

            var labels = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var label in labels)
            {
                if (label != null && string.Equals(label.name, "From", StringComparison.OrdinalIgnoreCase))
                {
                    label.SetText(value);
                    label.gameObject.SetActive(!string.IsNullOrWhiteSpace(value));
                }
            }
        }

        private void SetPreviewFramePassengerNameTexts(string value)
        {
            if (previewFrameFromNameText == null)
            {
                return;
            }

            var selectedLabelIndex = ResolveSelectedImagePreviewIndex() - 1;
            var canShowSelectedLabel = currentScreen == BoothUiScreenId.Preview && !string.IsNullOrWhiteSpace(value);
            for (var i = 0; i < previewFrameFromNameText.Length; i++)
            {
                var label = previewFrameFromNameText[i];
                if (label == null)
                {
                    continue;
                }

                var isVisible = canShowSelectedLabel && i == selectedLabelIndex;
                label.SetText(value);
                label.gameObject.SetActive(isVisible);
                if (isVisible)
                {
                    label.transform.SetAsLastSibling();
                }
            }
        }

        private void HidePreviewFramePassengerNameTexts()
        {
            if (previewFrameFromNameText == null)
            {
                return;
            }

            foreach (var label in previewFrameFromNameText)
            {
                if (label != null)
                {
                    label.gameObject.SetActive(false);
                }
            }
        }

        private int ResolveSelectedImagePreviewIndex()
        {
            if (selectedImagePreviewIndex == 1 || selectedImagePreviewIndex == 2)
            {
                return selectedImagePreviewIndex;
            }

            var value = currentJob?.ThemeId ?? selectedTheme?.ImagePreviewId ?? selectedTheme?.BackendFrameId ?? selectedTheme?.themeId;
            var resolved = ResolveImagePreviewIndex(value);
            return resolved == 1 || resolved == 2 ? resolved : 1;
        }

        private static int ResolveImagePreviewIndex(BoothThemeOption theme, int fallbackThemeIndex)
        {
            var value = theme?.ImagePreviewId ?? theme?.BackendFrameId ?? theme?.themeId;
            var resolved = ResolveImagePreviewIndex(value);
            if (resolved == 1 || resolved == 2)
            {
                return resolved;
            }

            return fallbackThemeIndex == 1 ? 2 : 1;
        }

        private static int ResolveImagePreviewIndex(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized == "2" || normalized == "image_preview_2" || normalized == "theme_02" || normalized.EndsWith("_02", StringComparison.Ordinal))
            {
                return 2;
            }

            if (normalized == "1" || normalized == "image_preview_1" || normalized == "theme_01" || normalized.EndsWith("_01", StringComparison.Ordinal))
            {
                return 1;
            }

            return 0;
        }

        private Image FindSelectedPreviewFrameImage()
        {
            var layoutIndex = ResolveSelectedImagePreviewIndex();
            return FindPreviewFrameImageByName(
                $"Image Preview {layoutIndex}",
                $"ImagePreview{layoutIndex}",
                $"ImagePreview_{layoutIndex}",
                $"PreviewFrame{layoutIndex}",
                $"FramePreview{layoutIndex}");
        }

        private Image ResolvePreviewFrameImageForSelectedLayout()
        {
            return FindSelectedPreviewFrameImage() ?? previewFrameImage ?? FindPreviewFrameImage();
        }

        private Image FindPreviewFrameImageByName(params string[] imageNames)
        {
            var root = FindScreenRoot(BoothUiScreenId.Preview);
            if (root == null || imageNames == null || imageNames.Length == 0)
            {
                return null;
            }

            var images = root.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image == null)
                {
                    continue;
                }

                foreach (var imageName in imageNames)
                {
                    if (!string.IsNullOrWhiteSpace(imageName) && string.Equals(image.name, imageName, StringComparison.OrdinalIgnoreCase))
                    {
                        return image;
                    }
                }
            }

            return null;
        }

        private void SetPreviewFrameImageVisibility(Image selectedImage)
        {
            var preview1 = FindPreviewFrameImageByName("Image Preview 1", "ImagePreview1", "ImagePreview_1", "PreviewFrame1", "FramePreview1");
            var preview2 = FindPreviewFrameImageByName("Image Preview 2", "ImagePreview2", "ImagePreview_2", "PreviewFrame2", "FramePreview2");
            if (preview1 != null)
            {
                preview1.gameObject.SetActive(preview1 == selectedImage);
            }

            if (preview2 != null)
            {
                preview2.gameObject.SetActive(preview2 == selectedImage);
            }
        }

        private RawImage CreatePreviewFrameSlot(Transform parent, int index)
        {
            var layoutIndex = ResolveSelectedImagePreviewIndex();
            var host = new GameObject($"PreviewFrameSlot_{layoutIndex}_{index + 1:00}", typeof(RectTransform), typeof(RawImage));
            host.transform.SetParent(parent, false);
            var image = host.GetComponent<RawImage>();
            image.color = Color.white;
            image.raycastTarget = false;
            return image;
        }

        private void SetFallbackPreviewFrameSlotsVisible(bool isVisible)
        {
            var root = FindScreenRoot(BoothUiScreenId.Preview);
            if (root == null)
            {
                return;
            }

            var rawImages = root.GetComponentsInChildren<RawImage>(true);
            foreach (var rawImage in rawImages)
            {
                if (rawImage == null)
                {
                    continue;
                }

                for (var i = 1; i <= previewFrameSlotImages.Length; i++)
                {
                    if (string.Equals(rawImage.name, $"PreviewFrameSlot_{i:00}", StringComparison.OrdinalIgnoreCase))
                    {
                        rawImage.gameObject.SetActive(isVisible);
                        break;
                    }
                }
            }
        }

        private RawImage FindPreviewFrameSlot(int index, out bool isLayoutSpecific)
        {
            isLayoutSpecific = false;
            var root = FindScreenRoot(BoothUiScreenId.Preview);
            if (root == null)
            {
                return null;
            }

            var layoutIndex = ResolveSelectedImagePreviewIndex();
            var slotNumber = index + 1;
            var layoutSlotNames = new[]
            {
                $"ImagePreview{layoutIndex}_Slot_{slotNumber:00}",
                $"ImagePreview{layoutIndex}Slot{slotNumber:00}",
                $"Image Preview {layoutIndex} Slot {slotNumber:00}",
                $"PreviewFrameSlot_{layoutIndex}_{slotNumber:00}",
                $"PreviewFrameSlot_{slotNumber:00}_Layout{layoutIndex}",
                $"PreviewFrameSlot_{slotNumber:00}",
                $"PreviewFrameSlot_{slotNumber}"
            };
            var rawImages = root.GetComponentsInChildren<RawImage>(true);
            foreach (var rawImage in rawImages)
            {
                if (rawImage == null)
                {
                    continue;
                }

                foreach (var slotName in layoutSlotNames)
                {
                    if (string.Equals(rawImage.name, slotName, StringComparison.OrdinalIgnoreCase))
                    {
                        isLayoutSpecific = true;
                        return rawImage;
                    }
                }
            }
            return null;
        }

        private static void PositionPreviewFrameSlot(RectTransform rect, Rect slotRect, Rect spriteRect, RectTransform frameRect)
        {
            var frameSize = frameRect.rect.size;
            if (frameSize.x <= 0f || frameSize.y <= 0f)
            {
                frameSize = frameRect.sizeDelta;
            }

            var normalizedLeft = (slotRect.xMin - spriteRect.xMin) / spriteRect.width;
            var normalizedBottom = (slotRect.yMin - spriteRect.yMin) / spriteRect.height;
            var normalizedWidth = slotRect.width / spriteRect.width;
            var normalizedHeight = slotRect.height / spriteRect.height;
            rect.anchorMin = Vector2.one * 0.5f;
            rect.anchorMax = Vector2.one * 0.5f;
            rect.pivot = Vector2.one * 0.5f;
            rect.sizeDelta = new Vector2(normalizedWidth * frameSize.x, normalizedHeight * frameSize.y);
            rect.anchoredPosition = new Vector2(
                (normalizedLeft + (normalizedWidth * 0.5f) - 0.5f) * frameSize.x,
                (normalizedBottom + (normalizedHeight * 0.5f) - 0.5f) * frameSize.y);
        }

        private static Rect ResolvePreviewFrameSpriteRect(Image frameImage)
        {
            return frameImage != null && frameImage.sprite != null
                ? frameImage.sprite.rect
                : PreviewFrameSpriteRectFallback;
        }

        private Image FindPreviewFrameImage()
        {
            var root = FindScreenRoot(BoothUiScreenId.Preview);
            if (root == null)
            {
                return null;
            }

            var images = root.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image != null && image.sprite != null && image.sprite.name == "piece_03_0")
                {
                    return image;
                }
            }

            foreach (var image in images)
            {
                if (image != null && NameMatches(image.name, new[] { "PreviewFrameArt", "SelectedFrameArt" }))
                {
                    return image;
                }
            }

            foreach (var image in images)
            {
                if (image != null && image.name == "Image" && image.sprite != null)
                {
                    return image;
                }
            }

            return null;
        }

        private void LoadPreviewFrameSlot(int slotIndex, string imagePath)
        {
            ClearPreviewFrameSlot(slotIndex);
            if (slotIndex < 0 || slotIndex >= previewFrameSlotImages.Length || previewFrameSlotImages[slotIndex] == null)
            {
                return;
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            ImageConversion.LoadImage(texture, File.ReadAllBytes(imagePath));
            previewFrameSlotTextures[slotIndex] = texture;
            previewFrameSlotImages[slotIndex].texture = texture;
            previewFrameSlotImages[slotIndex].color = Color.white;
            previewFrameSlotImages[slotIndex].uvRect = BuildCoverUvRect(texture.width, texture.height, previewFrameSlotImages[slotIndex].rectTransform.rect);
        }

        private void ClearPreviewFrameSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= previewFrameSlotImages.Length)
            {
                return;
            }

            if (previewFrameSlotImages[slotIndex] != null)
            {
                previewFrameSlotImages[slotIndex].texture = null;
                previewFrameSlotImages[slotIndex].color = Color.clear;
                previewFrameSlotImages[slotIndex].uvRect = new Rect(0f, 0f, 1f, 1f);
            }

            if (previewFrameSlotTextures[slotIndex] != null)
            {
                Destroy(previewFrameSlotTextures[slotIndex]);
                previewFrameSlotTextures[slotIndex] = null;
            }
        }

        private void ClearPreviewFrameSlots()
        {
            StopPreviewFrameMotionPlayback();
            for (var i = 0; i < previewFrameSlotImages.Length; i++)
            {
                ClearPreviewFrameSlot(i);
                if (previewFrameSlotImages[i] != null)
                {
                    previewFrameSlotImages[i].gameObject.SetActive(false);
                }
            }

            if (composedPreview != null)
            {
                composedPreview.gameObject.SetActive(true);
            }

            HidePreviewFramePassengerNameTexts();
        }

        private static Rect BuildCoverUvRect(int textureWidth, int textureHeight, Rect targetRect)
        {
            if (textureWidth <= 0 || textureHeight <= 0 || targetRect.width <= 0f || targetRect.height <= 0f)
            {
                return new Rect(0f, 0f, 1f, 1f);
            }

            var textureAspect = textureWidth / (float)textureHeight;
            var targetAspect = Mathf.Abs(targetRect.width / targetRect.height);
            if (textureAspect > targetAspect)
            {
                var width = targetAspect / textureAspect;
                return new Rect((1f - width) * 0.5f, 0f, width, 1f);
            }

            var height = textureAspect / targetAspect;
            return new Rect(0f, (1f - height) * 0.5f, 1f, height);
        }

        private async Task LoadQrPreviewAsync(string downloadUrl)
        {
            var rawTarget = ResolveQrPreviewRawImage();
            var imageTarget = ResolveQrPreviewImage();
            ClearQrPreview(rawTarget, imageTarget);
            if (string.IsNullOrWhiteSpace(downloadUrl) || (rawTarget == null && imageTarget == null))
            {
                return;
            }

            ShowQrLoading(rawTarget, imageTarget);
            try
            {
                UnityWebRequest request = null;
                var qrUrl = BuildQrPreviewUrl(downloadUrl);
                for (var attempt = 0; attempt < 8; attempt += 1)
                {
                    request?.Dispose();
                    request = UnityWebRequestTexture.GetTexture($"{qrUrl}?attempt={attempt}");
                    request.timeout = Math.Max(1, runtime.BackendRequestTimeoutSeconds);
                    request.disposeDownloadHandlerOnDispose = false;
                    request.SetRequestHeader("Accept", "image/png");
                    var operation = request.SendWebRequest();
                    while (!operation.isDone)
                    {
                        await Task.Yield();
                    }

                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        break;
                    }

                    Debug.LogWarning($"QR preview attempt {attempt + 1} failed: {request.responseCode} {request.error}");
                    await Task.Delay(450);
                }

                if (request == null || request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"QR preview failed: {request?.responseCode ?? 0} {request?.error}");
                    request?.Dispose();
                    SetStatus("Download link ready. QR preview failed, use the URL text.");
                    return;
                }

                qrPreviewTexture = DownloadHandlerTexture.GetContent(request);
                request.Dispose();
                request = null;
                ApplyQrPreviewTexture(rawTarget, imageTarget, qrPreviewTexture);
            }
            finally
            {
                HideQrLoading();
            }
        }

        private string BuildQrPreviewUrl(string downloadUrl)
        {
            if (currentJob != null && !string.IsNullOrWhiteSpace(currentJob.JobId))
            {
                var downloadBaseUrl = runtime != null ? runtime.BackendDownloadBaseUrl : string.Empty;
                if (string.IsNullOrWhiteSpace(downloadBaseUrl) && runtime != null && !string.IsNullOrWhiteSpace(runtime.BackendBoothApiBaseUrl))
                {
                    downloadBaseUrl = $"{runtime.BackendBoothApiBaseUrl.Trim().TrimEnd('/')}/world-tour";
                }

                if (!string.IsNullOrWhiteSpace(downloadBaseUrl))
                {
                    return $"{downloadBaseUrl.Trim().TrimEnd('/')}/{Uri.EscapeDataString(currentJob.JobId)}/qr";
                }
            }

            return $"{downloadUrl.Trim().TrimEnd('/')}/qr";
        }

        private void ApplyQrPreviewTexture(RawImage rawTarget, Image imageTarget, Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            if (rawTarget != null)
            {
                rawTarget.texture = texture;
                rawTarget.color = Color.white;
            }

            if (imageTarget == null)
            {
                return;
            }

            if (qrPreviewSprite != null)
            {
                Destroy(qrPreviewSprite);
                qrPreviewSprite = null;
            }

            qrPreviewSprite = Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                Vector2.one * 0.5f);
            imageTarget.sprite = qrPreviewSprite;
            imageTarget.preserveAspect = true;
            imageTarget.color = Color.white;
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

        public async void BackFromPaymentFromUi()
        {
            if (!await BeginBusyAsync())
            {
                return;
            }

            try
            {
                await ReleasePendingVoucherReservationAsync();
                SwitchScreen(BoothUiScreenId.ThemeSelect, "Choose your frame.");
            }
            catch (Exception exception)
            {
                Debug.LogError($"Payment back navigation failed: {exception}");
                ShowError(exception.Message);
            }
            finally
            {
                EndBusy();
            }
        }

        private void SetInteractable(bool interactable)
        {
            if (captureButton != null) captureButton.interactable = interactable;
            // if (captureRetakeButton != null) captureRetakeButton.interactable = interactable && isCaptureReviewing && !captureReviewRetakeUsed;
            // if (retakeButton != null) retakeButton.interactable = interactable;
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

        private void ClearPendingVoucherState(bool clearEnteredCode = true)
        {
            pendingVoucherCheckoutToken = string.Empty;
            pendingVoucherCode = string.Empty;
            pendingVoucherIdempotencyKey = string.Empty;
            pendingVoucherRedemptionId = 0;
            pendingVoucherRemainingUsesAfterApply = -1;
            voucherAttemptCounter = 0;
            if (clearEnteredCode)
            {
                voucherCode = string.Empty;
            }
        }

        private async Task ReleasePendingVoucherReservationAsync()
        {
            if (pendingVoucherRedemptionId <= 0)
            {
                return;
            }

            try
            {
                await ReleaseReservedVoucherAsync(pendingVoucherRedemptionId, flowCancellation?.Token ?? CancellationToken.None);
            }
            finally
            {
                ClearPendingVoucherState(clearEnteredCode: false);
            }
        }

        private string ResolveVoucherIdempotencyKey(string normalizedVoucherCode)
        {
            if (!string.IsNullOrWhiteSpace(pendingVoucherIdempotencyKey)
                && string.Equals(pendingVoucherCode, normalizedVoucherCode, StringComparison.OrdinalIgnoreCase))
            {
                return pendingVoucherIdempotencyKey;
            }

            voucherAttemptCounter += 1;
            return $"{runtime.BackendDeviceId}-{currentJob.JobId}-voucher-{voucherAttemptCounter}";
        }

        private async Task<VoucherValidateData> ValidateVoucherAsync(string normalizedVoucherCode, CancellationToken cancellationToken)
        {
            EnsureVoucherBackendConfigured();
            var envelope = await SendVoucherRequestAsync<VoucherValidateEnvelope>(
                "/api/kiosk/v1/checkout/voucher/validate",
                new VoucherValidateRequest
                {
                    job_id = currentJob.JobId,
                    voucher_code = normalizedVoucherCode,
                    device_id = runtime.BackendDeviceId
                },
                cancellationToken);
            return envelope.data ?? throw new InvalidOperationException("Voucher validation returned no data.");
        }

        private async Task<VoucherRemoteScanSessionData> CreateVoucherRemoteScanSessionAsync(CancellationToken cancellationToken)
        {
            EnsureVoucherBackendConfigured();
            var envelope = await SendVoucherRequestAsync<VoucherRemoteScanSessionEnvelope>(
                "/api/kiosk/v1/kiosk-sessions",
                new VoucherRemoteScanCreateRequest
                {
                    job_id = currentJob.JobId,
                    device_id = runtime.BackendDeviceId
                },
                cancellationToken);
            return envelope.data ?? throw new InvalidOperationException("Voucher scan session returned no data.");
        }

        private async Task<VoucherRemoteScanSessionData> PollVoucherRemoteScanSessionAsync(string sessionToken, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken))
            {
                return null;
            }

            EnsureVoucherBackendConfigured();
            var envelope = await SendVoucherGetRequestAsync<VoucherRemoteScanSessionEnvelope>(
                $"/api/kiosk/v1/kiosk-sessions/{UnityWebRequest.EscapeURL(sessionToken)}",
                cancellationToken);
            return envelope.data;
        }

        private async Task AckVoucherRemoteScanSessionAsync(string sessionToken, string status, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sessionToken) || string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            try
            {
                EnsureVoucherBackendConfigured();
                await SendVoucherRequestAsync<VoucherRemoteScanSessionEnvelope>(
                    $"/api/kiosk/v1/kiosk-sessions/{UnityWebRequest.EscapeURL(sessionToken)}/ack",
                    new VoucherRemoteScanAckRequest
                    {
                        status = status,
                        device_id = runtime.BackendDeviceId
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"Voucher scan session ack failed: {exception.Message}");
            }
        }

        private async Task LoadVoucherRemoteQrTextureAsync(string qrPngUrl, CancellationToken cancellationToken)
        {
            if (voucherRemoteQrImage == null || string.IsNullOrWhiteSpace(qrPngUrl))
            {
                return;
            }

            using var request = UnityWebRequestTexture.GetTexture(qrPngUrl);
            request.timeout = runtime.BackendRequestTimeoutSeconds;
            await SendWebRequestAsync(request, cancellationToken);
            if (request.result != UnityWebRequest.Result.Success || request.responseCode is < 200 or >= 300)
            {
                throw new InvalidOperationException($"Voucher remote QR download failed: {request.error}");
            }

            ClearVoucherRemoteQrTexture();
            voucherRemoteQrTexture = DownloadHandlerTexture.GetContent(request);
            voucherRemoteQrTexture.name = "VoucherRemoteQrTexture";
            voucherRemoteQrImage.texture = voucherRemoteQrTexture;
            voucherRemoteQrImage.color = Color.white;
        }

        private async Task<VoucherReserveData> ReserveVoucherAsync(string checkoutToken, string idempotencyKey, CancellationToken cancellationToken)
        {
            EnsureVoucherBackendConfigured();
            var envelope = await SendVoucherRequestAsync<VoucherReserveEnvelope>(
                "/api/kiosk/v1/checkout/voucher/reserve",
                new VoucherReserveRequest
                {
                    job_id = currentJob.JobId,
                    checkout_token = checkoutToken,
                    idempotency_key = idempotencyKey,
                    gross_amount_minor = currentJob.AmountMinorUnits,
                    currency = currentJob.CurrencyCode,
                    device_id = runtime.BackendDeviceId,
                    theme_id = currentJob.ThemeId,
                    image_preview_id = currentJob.ThemeId,
                    passenger_name = string.IsNullOrWhiteSpace(passengerName) ? null : passengerName,
                    amount_minor_units = currentJob.AmountMinorUnits,
                    payment_reference = currentJob.PaymentReference,
                    session_started_at_utc = ResolveSessionStartedAtUtc(currentJob)
                },
                cancellationToken);
            return envelope.data ?? throw new InvalidOperationException("Voucher reserve returned no data.");
        }

        private async Task ApplyReservedVoucherAsync(long redemptionId, CancellationToken cancellationToken)
        {
            EnsureVoucherBackendConfigured();
            var envelope = await SendVoucherRequestAsync<VoucherRedemptionActionEnvelope>(
                $"/api/kiosk/v1/checkout/voucher/{redemptionId}/apply",
                new VoucherRedemptionActionRequest { device_id = runtime.BackendDeviceId },
                cancellationToken);
            if (envelope.data == null || !string.Equals(envelope.data.status, "APPLIED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Voucher apply did not return APPLIED.");
            }

            ClearPendingVoucherState(clearEnteredCode: false);
        }

        private async Task ReleaseReservedVoucherAsync(long redemptionId, CancellationToken cancellationToken)
        {
            EnsureVoucherBackendConfigured();
            await SendVoucherRequestAsync<VoucherRedemptionActionEnvelope>(
                $"/api/kiosk/v1/checkout/voucher/{redemptionId}/release",
                new VoucherRedemptionActionRequest { device_id = runtime.BackendDeviceId },
                cancellationToken);
        }

        private async Task<TEnvelope> SendVoucherRequestAsync<TEnvelope>(string path, object payload, CancellationToken cancellationToken)
        {
            var baseUrl = runtime.BackendBoothApiBaseUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}{path}";
            var body = JsonUtility.ToJson(payload);
            using var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body)),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = runtime.BackendRequestTimeoutSeconds
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {runtime.BackendDeviceToken}");
            request.SetRequestHeader("X-Device-Id", runtime.BackendDeviceId);
            await SendWebRequestAsync(request, cancellationToken);

            var responseText = request.downloadHandler?.text ?? string.Empty;
            if (request.result != UnityWebRequest.Result.Success || request.responseCode is < 200 or >= 300)
            {
                var apiError = ParseVoucherApiError(responseText);
                throw new VoucherApiException(
                    apiError?.code,
                    BuildVoucherRequestErrorMessage(apiError, request.responseCode, request.error),
                    request.responseCode);
            }

            var envelope = JsonUtility.FromJson<TEnvelope>(responseText);
            if (envelope == null)
            {
                throw new InvalidOperationException("Voucher API returned an unreadable response.");
            }

            var successField = typeof(TEnvelope).GetField("success");
            var errorField = typeof(TEnvelope).GetField("error");
            if (successField != null && successField.FieldType == typeof(bool) && !(bool)successField.GetValue(envelope))
            {
                var error = errorField?.GetValue(envelope) as BoothApiErrorPayload;
                throw new VoucherApiException(error?.code, error?.message, request.responseCode);
            }

            return envelope;
        }

        private async Task<TEnvelope> SendVoucherGetRequestAsync<TEnvelope>(string path, CancellationToken cancellationToken)
        {
            var baseUrl = runtime.BackendBoothApiBaseUrl.Trim().TrimEnd('/');
            var url = $"{baseUrl}{path}";
            using var request = UnityWebRequest.Get(url);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = runtime.BackendRequestTimeoutSeconds;
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader("Authorization", $"Bearer {runtime.BackendDeviceToken}");
            request.SetRequestHeader("X-Device-Id", runtime.BackendDeviceId);
            await SendWebRequestAsync(request, cancellationToken);

            var responseText = request.downloadHandler?.text ?? string.Empty;
            if (request.result != UnityWebRequest.Result.Success || request.responseCode is < 200 or >= 300)
            {
                var apiError = ParseVoucherApiError(responseText);
                throw new VoucherApiException(
                    apiError?.code,
                    BuildVoucherRequestErrorMessage(apiError, request.responseCode, request.error),
                    request.responseCode);
            }

            var envelope = JsonUtility.FromJson<TEnvelope>(responseText);
            if (envelope == null)
            {
                throw new InvalidOperationException("Voucher API returned an unreadable response.");
            }

            var successField = typeof(TEnvelope).GetField("success");
            var errorField = typeof(TEnvelope).GetField("error");
            if (successField != null && successField.FieldType == typeof(bool) && !(bool)successField.GetValue(envelope))
            {
                var error = errorField?.GetValue(envelope) as BoothApiErrorPayload;
                throw new VoucherApiException(error?.code, error?.message, request.responseCode);
            }

            return envelope;
        }

        private static async Task SendWebRequestAsync(UnityWebRequest request, CancellationToken cancellationToken)
        {
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        private static BoothApiErrorPayload ParseVoucherApiError(string responseText)
        {
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                try
                {
                    var envelope = JsonUtility.FromJson<VoucherReserveEnvelope>(responseText);
                    if (envelope?.error != null)
                    {
                        return envelope.error;
                    }
                }
                catch
                {
                    // Ignore parse failures and use fallback values from UnityWebRequest.
                }
            }

            return null;
        }

        private static string BuildVoucherRequestErrorMessage(BoothApiErrorPayload apiError, long responseCode, string fallbackError)
        {
            if (!string.IsNullOrWhiteSpace(apiError?.message))
            {
                return apiError.message;
            }

            if (!string.IsNullOrWhiteSpace(fallbackError))
            {
                return fallbackError;
            }

            return responseCode > 0
                ? $"Voucher request failed with HTTP {responseCode}."
                : "Voucher request failed.";
        }

        private void EnsureVoucherBackendConfigured()
        {
            if (runtime == null)
            {
                throw new InvalidOperationException("Booth runtime is unavailable.");
            }

            if (string.IsNullOrWhiteSpace(runtime.BackendBoothApiBaseUrl))
            {
                throw new InvalidOperationException("Backend booth API base URL is not configured.");
            }

            if (string.IsNullOrWhiteSpace(runtime.BackendDeviceToken))
            {
                throw new InvalidOperationException("Backend device token is not configured.");
            }
        }

        private static string NormalizeVoucherCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim().ToUpperInvariant().Replace(" ", string.Empty);
            var chars = new List<char>(trimmed.Length);
            foreach (var character in trimmed)
            {
                if ((character >= 'A' && character <= 'Z')
                    || (character >= '0' && character <= '9')
                    || character is '-' or '_' or '.')
                {
                    chars.Add(character);
                }
            }

            return new string(chars.ToArray(), 0, Math.Min(chars.Count, VoucherCodeMaxLength));
        }

        private static string ExtractVoucherSuffix(string value)
        {
            var normalized = NormalizeVoucherCode(value);
            if (normalized.StartsWith(VoucherCodePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return normalized[VoucherCodePrefix.Length..];
            }

            return normalized;
        }

        private static string NormalizeVoucherCodeCharacter(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = NormalizeVoucherCode(value);
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            return normalized[0].ToString();
        }

        private static string FormatAmountMinor(long amountMinor, string currencyCode)
        {
            var amount = amountMinor / 100m;
            var currency = string.IsNullOrWhiteSpace(currencyCode) ? "THB" : currencyCode.Trim().ToUpperInvariant();
            return $"{amount:0.00} {currency}";
        }

        private void EnsureCurrentJobOrCreateQuickDemo(bool paymentConfirmed)
        {
            if (currentJob != null)
            {
                return;
            }

            EnsureDefaultThemes();
            EnsureDefaultAiStyles();
            EnsureDefaultArStickerPresets();
            selectedTheme ??= themes[0];
            selectedAiStyle ??= aiStyles[0];
            EnsureArPresetSelection();
            currentJob = runtime.SessionService.CreateJob(selectedTheme.priceMinorUnits, selectedTheme.currencyCode);
            currentJob = runtime.SessionService.SelectTheme(currentJob.JobId, ResolveBackendImagePreviewId(selectedTheme));
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

            currentJob = runtime.SessionService.SetPassengerName(currentJob.JobId, passengerName);
        }

        private BoothJob CreateCheckoutRetryJobPreservingSelection(BoothJob previousJob)
        {
            EnsureDefaultThemes();
            EnsureDefaultAiStyles();
            selectedTheme ??= themes[0];
            selectedAiStyle ??= aiStyles[0];

            var amountMinor = previousJob?.AmountMinorUnits ?? selectedTheme.priceMinorUnits;
            var currencyCode = string.IsNullOrWhiteSpace(previousJob?.CurrencyCode) ? selectedTheme.currencyCode : previousJob.CurrencyCode;
            var nextJob = runtime.SessionService.CreateJob(amountMinor, currencyCode);
            nextJob = runtime.SessionService.SelectTheme(nextJob.JobId, ResolveBackendImagePreviewId(selectedTheme));
            nextJob = runtime.SessionService.SelectAiStyle(nextJob.JobId, selectedAiStyle.styleId, selectedAiStyle.aiPrompt);
            if (!string.IsNullOrWhiteSpace(passengerName))
            {
                nextJob = runtime.SessionService.SetPassengerName(nextJob.JobId, passengerName);
            }

            ClearPendingVoucherState(clearEnteredCode: false);
            return nextJob;
        }

        private void EnqueueRawCaptureUpload(string rawPath, int captureIndex, int captureTotal)
        {
            if (runtime?.SyncService == null || currentJob == null || string.IsNullOrWhiteSpace(rawPath))
            {
                return;
            }

            rawCaptureUploadQueue ??= new BoothRawCaptureUploadQueue(runtime.SyncService, autoProcess: false);
            rawCaptureUploadStarted = true;
            rawCaptureUploadQueue.Enqueue(new RawCaptureUploadRequest
            {
                JobId = currentJob.JobId,
                DeviceId = runtime.BackendDeviceId,
                ThemeId = currentJob.ThemeId,
                ImagePreviewId = currentJob.ThemeId,
                PassengerName = ResolvePassengerNameForLabel(),
                RawCapturePath = rawPath,
                CaptureIndex = Math.Max(1, captureIndex),
                CaptureTotal = Math.Max(1, captureTotal),
                SessionStartedAtUtc = ResolveSessionStartedAtUtc(currentJob),
                CaptureTakenAtUtc = DateTime.UtcNow.ToString("O"),
                CurrencyCode = currentJob.CurrencyCode,
                AmountMinorUnits = currentJob.AmountMinorUnits,
                PaymentReference = currentJob.PaymentReference
            });
        }

        private BoothJob CreateRetakeJobPreservingSessionState(BoothJob previousJob)
        {
            EnsureDefaultThemes();
            EnsureDefaultAiStyles();
            EnsureDefaultArStickerPresets();
            selectedTheme ??= themes[0];
            selectedAiStyle ??= aiStyles[0];
            EnsureArPresetSelection();

            var retakeJob = runtime.SessionService.CreateJob(previousJob.AmountMinorUnits, previousJob.CurrencyCode);
            retakeJob = runtime.SessionService.SelectTheme(retakeJob.JobId, ResolveBackendImagePreviewId(selectedTheme));
            retakeJob = runtime.SessionService.SelectAiStyle(retakeJob.JobId, selectedAiStyle.styleId, selectedAiStyle.aiPrompt);

            if (!string.IsNullOrWhiteSpace(passengerName))
            {
                retakeJob = runtime.SessionService.SetPassengerName(retakeJob.JobId, passengerName);
            }

            if (previousJob.PaymentStatus == BoothPaymentStatus.Bypassed)
            {
                retakeJob = runtime.SessionService.BypassPayment(retakeJob.JobId);
            }
            else if (previousJob.PaymentStatus == BoothPaymentStatus.Confirmed)
            {
                retakeJob = runtime.SessionService.SetPaymentPending(retakeJob.JobId, previousJob.PaymentReference);
                retakeJob = runtime.SessionService.ConfirmPayment(retakeJob.JobId, previousJob.PaymentReference);
            }

            rawCaptureUploadQueue = null;
            rawCaptureUploadStarted = false;
            return retakeJob;
        }

        private static string ResolveSessionStartedAtUtc(BoothJob job)
        {
            return string.IsNullOrWhiteSpace(job?.CreatedAtUtc)
                ? DateTime.UtcNow.ToString("O")
                : job.CreatedAtUtc;
        }

        private async Task EnsureCapturePreviewStartedAsync()
        {
            ApplyMonsterFrameSelection(selectedThemeUsesMonsterFrame);
            if (isCaptureReviewing || isStartingCapturePreview || currentScreen != BoothUiScreenId.Capture)
            {
                return;
            }

            isStartingCapturePreview = true;
            try
            {
                Debug.Log("PhotoBooth capture preview start requested.");
                cameraCaptureService ??= CreateCameraCaptureService();
                await WakeCameraDeviceAsync(flowCancellation?.Token ?? CancellationToken.None);
                await cameraCaptureService.StartPreviewAsync(flowCancellation?.Token ?? CancellationToken.None);
                ApplyHd33PreviewRotation(capturing: false);
                Debug.Log($"PhotoBooth capture preview running: {cameraCaptureService.IsPreviewing}, device={cameraCaptureService.CurrentDeviceName}, source={cameraCaptureService.CurrentDeviceSource}, texture={cameraCaptureService.CurrentWidth}x{cameraCaptureService.CurrentHeight}");
                SetStatus(await BuildCaptureStatusMessageAsync(BuildCaptureReadyMessage(), flowCancellation?.Token ?? CancellationToken.None));
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

        private static string ResolveBackendImagePreviewId(BoothThemeOption theme)
        {
            if (theme == null)
            {
                return "image_preview_1";
            }

            var value = theme.ImagePreviewId;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = theme.BackendFrameId;
            }

            return string.IsNullOrWhiteSpace(value) ? "image_preview_1" : value.Trim();
        }

        private ArStickerPreset ResolveArPreset(int index)
        {
            EnsureDefaultArStickerPresets();
            if (index < 0 || index >= arStickerPresets.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "AR preset index is out of range.");
            }

            return arStickerPresets[index];
        }

        private void ApplyMonsterFrameSelection(bool useMonsterFrame)
        {
            SetMonsterFramePreviewMode(useMonsterFrame);
            ApplyCaptureFrameRect(useMonsterFrame);
            EnsureCaptureForegroundOverlay();
        }

        private void SetMonsterFramePreviewMode(bool useMonsterFrame)
        {
            useMonsterFrame &= !ShouldForceNoMonsterThemeSelection();
            SetObjectActive("Grid - No Monster", !useMonsterFrame);
            SetObjectActive("Grid - Monster", useMonsterFrame);
        }

        private bool ShouldForceNoMonsterThemeSelection()
        {
            if (forceNoMonsterThemeSelection)
            {
                return true;
            }

            var root = FindScreenRoot(BoothUiScreenId.ThemeSelect);
            if (root == null)
            {
                return false;
            }

            var hasNoMonsterGrid = false;
            var hasMonsterGrid = false;
            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate == null)
                {
                    continue;
                }

                hasNoMonsterGrid |= string.Equals(candidate.name, "Grid - No Monster", StringComparison.Ordinal);
                hasMonsterGrid |= string.Equals(candidate.name, "Grid - Monster", StringComparison.Ordinal);
            }

            return hasNoMonsterGrid && !hasMonsterGrid;
        }

        private bool ShouldSkipPaymentScreen()
        {
            return skipPaymentScreen
                || ShouldForceNoMonsterThemeSelection()
                || string.Equals(SceneManager.GetActiveScene().name, PaymentBypassSceneName, StringComparison.Ordinal);
        }

        private void ResetSessionSelectionVisuals()
        {
            ResetThemeSelectionVisuals();
            ResetArSelectionVisuals();
            UpdateCaptureForegroundOverlay();
        }

        private void ResetThemeSelectionVisuals()
        {
            var root = FindScreenRoot(BoothUiScreenId.ThemeSelect);
            if (root == null)
            {
                return;
            }

            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate == null || candidate.parent == null)
                {
                    continue;
                }

                var parentName = candidate.parent.name;
                var candidateName = candidate.name;
                var isFrameSelectionMarker = parentName.StartsWith("FramePreview", StringComparison.Ordinal)
                    && (candidateName.StartsWith("ThemeButton", StringComparison.Ordinal)
                        || string.Equals(candidateName, "Select", StringComparison.Ordinal));
                var isMonsterToggleMarker = (string.Equals(parentName, "Monster", StringComparison.Ordinal)
                        || string.Equals(parentName, "No Monster", StringComparison.Ordinal))
                    && string.Equals(candidateName, "Select", StringComparison.Ordinal);

                if (isFrameSelectionMarker || isMonsterToggleMarker)
                {
                    candidate.gameObject.SetActive(false);
                }
            }
        }

        private void UpdateMonsterToggleSelectionVisuals(bool useMonsterFrame)
        {
            var root = FindScreenRoot(BoothUiScreenId.ThemeSelect);
            if (root == null)
            {
                return;
            }

            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate == null || candidate.parent == null || !string.Equals(candidate.name, "Select", StringComparison.Ordinal))
                {
                    continue;
                }

                var parentName = candidate.parent.name;
                if (string.Equals(parentName, "Monster", StringComparison.Ordinal))
                {
                    candidate.gameObject.SetActive(useMonsterFrame);
                }
                else if (string.Equals(parentName, "No Monster", StringComparison.Ordinal))
                {
                    candidate.gameObject.SetActive(!useMonsterFrame);
                }
            }
        }

        private void ResetArSelectionVisuals()
        {
            var root = FindScreenRoot(BoothUiScreenId.Capture);
            if (root == null)
            {
                return;
            }

            foreach (var candidate in root.GetComponentsInChildren<Transform>(true))
            {
                if (candidate == null || candidate.parent == null)
                {
                    continue;
                }

                var parentName = candidate.parent.name;
                var isArSelectionMarker = string.Equals(candidate.name, "Select", StringComparison.Ordinal)
                    && (parentName.StartsWith("ArPreset", StringComparison.Ordinal)
                        || string.Equals(parentName, "Nose", StringComparison.Ordinal)
                        || string.Equals(parentName, "Horn", StringComparison.Ordinal)
                        || string.Equals(parentName, "Mushroom", StringComparison.Ordinal)
                        || string.Equals(parentName, "Monster", StringComparison.Ordinal)
                        || string.Equals(parentName, "No Monster", StringComparison.Ordinal));

                if (isArSelectionMarker)
                {
                    candidate.gameObject.SetActive(false);
                }
            }
        }

        private static string ResolveMonsterFrameOverlayId(bool useMonsterFrame)
        {
            return useMonsterFrame ? "monster_frame" : "none";
        }

        private static string BuildMonsterFrameStatusSuffix(bool useMonsterFrame)
        {
            return useMonsterFrame ? " + Monster frame" : string.Empty;
        }

        private void EnsureDefaultThemes()
        {
            var defaults = new[]
            {
                new BoothThemeOption { themeId = "theme_01", displayName = "Frame 1", priceMinorUnits = 12000, currencyCode = "THB", backendFrameId = "image_preview_1", imagePreviewId = "image_preview_1" },
                new BoothThemeOption { themeId = "theme_02", displayName = "Frame 2", priceMinorUnits = 12000, currencyCode = "THB", backendFrameId = "image_preview_2", imagePreviewId = "image_preview_2" }
            };

            if (themes == null || themes.Length == 0)
            {
                themes = defaults;
                return;
            }

            if (themes.Length < defaults.Length)
            {
                var existingLength = themes.Length;
                Array.Resize(ref themes, defaults.Length);
                for (var index = existingLength; index < themes.Length; index += 1)
                {
                    themes[index] = defaults[index];
                }
            }

            for (var index = 0; index < themes.Length; index += 1)
            {
                var fallback = index < defaults.Length ? defaults[index] : defaults[defaults.Length - 1];
                themes[index] ??= fallback;
                if (string.IsNullOrWhiteSpace(themes[index].themeId))
                {
                    themes[index].themeId = index < defaults.Length ? fallback.themeId : $"theme_{index + 1:00}";
                }

                if (string.IsNullOrWhiteSpace(themes[index].displayName))
                {
                    themes[index].displayName = index < defaults.Length ? fallback.displayName : $"Frame {index + 1}";
                }

                if (string.IsNullOrWhiteSpace(themes[index].backendFrameId))
                {
                    themes[index].backendFrameId = index == 1 ? "image_preview_2" : "image_preview_1";
                }

                if (string.IsNullOrWhiteSpace(themes[index].imagePreviewId))
                {
                    themes[index].imagePreviewId = index == 1 ? "image_preview_2" : "image_preview_1";
                }
            }
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

        private void EnsureDefaultArStickerPresets()
        {
            if (arStickerPresets != null)
            {
                return;
            }

            arStickerPresets = Array.Empty<ArStickerPreset>();
        }

        private void EnsureArPresetSelection()
        {
            EnsureDefaultArStickerPresets();
            if (selectedArPresetIndex < 0)
            {
                selectedArPreset = null;
                selectedArStickerIndex = -1;
                return;
            }

            if (selectedArPresetIndex >= 0 && selectedArPresetIndex < arStickerPresets.Length)
            {
                selectedArPreset = ResolveArPreset(selectedArPresetIndex);
                return;
            }

            selectedArPresetIndex = NoArPresetIndex;
            selectedArStickerIndex = -1;
            selectedArPreset = null;
        }

        private void EnsureDefaultMonsterArSelection()
        {
            EnsureDefaultArStickerPresets();
            if (selectedArPresetIndex >= 0
                || selectedArPresetIndex == NoArPresetIndex
                || arStickerPresets == null
                || arStickerPresets.Length == 0)
            {
                return;
            }

            pendingArPresetIndex = 0;
            selectedArPresetIndex = 0;
            selectedArStickerIndex = -1;
            selectedArPreset = ResolveArPreset(0);
        }

        private ArStickerDefinition[] ResolveArStickers()
        {
            if (selectedArPresetIndex == NoArPresetIndex)
            {
                return Array.Empty<ArStickerDefinition>();
            }

            EnsureDefaultArStickerPresets();
            EnsureArPresetSelection();
            var stickers = selectedArPreset?.stickers;
            if (stickers == null || stickers.Length == 0)
            {
                return Array.Empty<ArStickerDefinition>();
            }

            if (selectedArStickerIndex >= 0)
            {
                return selectedArStickerIndex < stickers.Length && stickers[selectedArStickerIndex] != null
                    ? new[] { stickers[selectedArStickerIndex] }
                    : Array.Empty<ArStickerDefinition>();
            }

            return stickers;
        }

        private static string ResolveArStickerDisplayName(ArStickerDefinition sticker, int stickerIndex)
        {
            return !string.IsNullOrWhiteSpace(sticker?.stickerId)
                ? sticker.stickerId
                : $"Sticker {stickerIndex + 1}";
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
            EnsureArPreviewMask();
            AlignArOverlaysToCameraPreview();
        }

        private void EnsureArPreviewMask()
        {
            if (arPreviewOverlay == null)
            {
                return;
            }

            var mask = arPreviewOverlay.GetComponent<RectMask2D>();
            if (mask == null)
            {
                mask = arPreviewOverlay.gameObject.AddComponent<RectMask2D>();
            }

            mask.padding = Vector4.zero;
            mask.softness = Vector2Int.zero;
        }

        private void EnsureCaptureForegroundOverlay()
        {
            if (cameraPreview == null)
            {
                return;
            }

            if (captureForegroundOverlay == null)
            {
                var existing = cameraPreview.transform.parent != null
                    ? cameraPreview.transform.parent.Find("CaptureForegroundOverlay")
                    : null;
                if (existing != null)
                {
                    if (existing.TryGetComponent<RawImage>(out var legacyRawImage))
                    {
                        legacyRawImage.enabled = false;
                    }

                    captureForegroundOverlay = existing.TryGetComponent<Image>(out var existingOverlay)
                        ? existingOverlay
                        : existing.gameObject.AddComponent<Image>();
                }
                else
                {
                    captureForegroundOverlay = CreateImage(cameraPreview.transform.parent, "CaptureForegroundOverlay", Vector2.one * 0.5f, Vector2.zero, new Vector2(1080f, 1920f));
                }
            }

            captureForegroundOverlay.raycastTarget = false;
            StretchGraphicToParent(captureForegroundOverlay);
            AlignArOverlaysToCameraPreview();
            UpdateCaptureForegroundOverlay();
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
            arPreviewFaceModelRig?.Hide();
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
            if (!UseUnity3dAr || (!enableTracked3dFaceModel && !HasTracked3dFaceParts()) || face == null)
            {
                arPreviewFaceModelRig?.Hide();
                return;
            }

            var rig = EnsureArPreviewFaceModelRig(editorPreview: false);
            ApplyTracked3dFaceGuideSettings(rig);
            rig.ApplyFace(face, CreateLivePreviewFaceProjectionGeometry(cameraCaptureService.CurrentWidth, cameraCaptureService.CurrentHeight));
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
            if (!UseUnity3dAr || !enableTracked3dFaceParts || tracked3dFaceParts == null)
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

        private ArFaceProjectionGeometry CreateLivePreviewFaceProjectionGeometry(int sourceWidth, int sourceHeight)
        {
            var renderWidth = Mathf.Max(256, sourceWidth);
            var renderHeight = Mathf.Max(256, sourceHeight);
            var overlay = arModelOverlay != null ? arModelOverlay : arPreviewOverlay != null ? arPreviewOverlay : cameraPreview;
            if (overlay != null && overlay.rectTransform != null)
            {
                var rect = overlay.rectTransform.rect;
                var overlayWidth = Mathf.RoundToInt(Mathf.Abs(rect.width));
                var overlayHeight = Mathf.RoundToInt(Mathf.Abs(rect.height));
                if (overlayWidth > 0 && overlayHeight > 0)
                {
                    renderWidth = Mathf.Max(256, overlayWidth);
                    renderHeight = Mathf.Max(256, overlayHeight);
                }
            }

            return ArFaceProjectionGeometry.CreateStretched(sourceWidth, sourceHeight, renderWidth, renderHeight);
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

            if (captureForegroundOverlay != null)
            {
                if (selectedThemeUsesMonsterFrame)
                {
                    ApplyMonsterFrameOverlayRect(captureForegroundOverlay);
                }
                else if (ResolveSelectedCaptureFrameOverlays()?.Length > 0)
                {
                    StretchGraphicToParent(captureForegroundOverlay);
                }
                else
                {
                    CopyCameraPreviewRect(captureForegroundOverlay);
                }

                var overlaySiblingIndex = arPreviewOverlay != null ? arPreviewOverlay.transform.GetSiblingIndex() : cameraSiblingIndex;
                captureForegroundOverlay.transform.SetSiblingIndex(Mathf.Min(overlaySiblingIndex + 1, cameraPreview.transform.parent.childCount - 1));
            }
        }

        private void ApplyMonsterFrameOverlayRect(Graphic overlay)
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

            overlayRect.anchorMin = Vector2.one * 0.5f;
            overlayRect.anchorMax = Vector2.one * 0.5f;
            overlayRect.pivot = Vector2.one * 0.5f;
            overlayRect.anchoredPosition = Vector2.zero;
            overlayRect.sizeDelta = MonsterFrameOverlaySize;
            overlayRect.localRotation = Quaternion.identity;
            overlayRect.localScale = Vector3.one;
            overlayRect.localPosition = new Vector3(overlayRect.localPosition.x, overlayRect.localPosition.y, cameraRect.localPosition.z);
        }

        private void ApplyCaptureFrameRect(bool useMonsterFrame)
        {
            if (cameraPreview == null)
            {
                return;
            }

            CaptureDefaultCameraPreviewRect();
            if (useMonsterFrame)
            {
                ApplyMonsterFrameRect(cameraPreview.rectTransform);
            }
            else
            {
                RestoreDefaultCameraPreviewRect();
            }

            AlignArOverlaysToCameraPreview();
        }

        private void ApplyMonsterFrameRect(RectTransform rect)
        {
            if (rect == null)
            {
                return;
            }

            rect.anchorMin = Vector2.one * 0.5f;
            rect.anchorMax = Vector2.one * 0.5f;
            rect.pivot = Vector2.one * 0.5f;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = MonsterFrameOverlaySize;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
        }

        private void CaptureDefaultCameraPreviewRect()
        {
            if (hasDefaultCameraPreviewRect || cameraPreview == null)
            {
                return;
            }

            var rect = cameraPreview.rectTransform;
            defaultCameraPreviewAnchorMin = rect.anchorMin;
            defaultCameraPreviewAnchorMax = rect.anchorMax;
            defaultCameraPreviewPivot = rect.pivot;
            defaultCameraPreviewAnchoredPosition = rect.anchoredPosition;
            defaultCameraPreviewSizeDelta = rect.sizeDelta;
            defaultCameraPreviewLocalRotation = rect.localRotation;
            defaultCameraPreviewLocalScale = rect.localScale;
            hasDefaultCameraPreviewRect = true;
        }

        private void RestoreDefaultCameraPreviewRect()
        {
            if (!hasDefaultCameraPreviewRect || cameraPreview == null)
            {
                return;
            }

            var rect = cameraPreview.rectTransform;
            rect.anchorMin = defaultCameraPreviewAnchorMin;
            rect.anchorMax = defaultCameraPreviewAnchorMax;
            rect.pivot = defaultCameraPreviewPivot;
            rect.anchoredPosition = defaultCameraPreviewAnchoredPosition;
            rect.sizeDelta = defaultCameraPreviewSizeDelta;
            rect.localRotation = defaultCameraPreviewLocalRotation;
            rect.localScale = defaultCameraPreviewLocalScale;
        }

        private void CopyCameraPreviewRect(Graphic overlay)
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

        private static void StretchGraphicToParent(Graphic graphic)
        {
            if (graphic == null)
            {
                return;
            }

            var rect = graphic.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = Vector2.one * 0.5f;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.localRotation = Quaternion.identity;
            rect.localScale = Vector3.one;
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
            metadata["frame_overlay_id"] = ResolveMonsterFrameOverlayId(selectedThemeUsesMonsterFrame);
            if (selectedAiStyle != null)
            {
                metadata["ai_style_id"] = selectedAiStyle.styleId;
                metadata["ai_style_prompt"] = selectedAiStyle.aiPrompt;
                metadata["local_ai_preview"] = selectedAiStyle.applyLocalStylizedPreview.ToString();
            }

            if (selectedArPreset != null)
            {
                metadata["ar_preset_id"] = selectedArPreset.presetId;
                metadata["ar_preset_name"] = selectedArPreset.displayName;
                if (selectedArStickerIndex >= 0 && selectedArPreset.stickers != null && selectedArStickerIndex < selectedArPreset.stickers.Length)
                {
                    metadata["ar_sticker_index"] = selectedArStickerIndex.ToString();
                    metadata["ar_sticker_id"] = selectedArPreset.stickers[selectedArStickerIndex]?.stickerId ?? string.Empty;
                }
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
                BoothUiScreenId.ArSelect => "Choose Your AR",
                BoothUiScreenId.ArtStyleSelect => "Type Your Name",
                BoothUiScreenId.VoucherEntry => "Enter Voucher",
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

        private void ClearQrPreview()
        {
            ClearQrPreview(ResolveQrPreviewRawImage(), ResolveQrPreviewImage());
        }

        private void ClearQrPreview(RawImage rawTarget, Image imageTarget)
        {
            HideQrLoading();
            if (rawTarget != null)
            {
                rawTarget.texture = null;
                rawTarget.color = new Color(1f, 1f, 1f, 0.08f);
            }

            if (imageTarget != null)
            {
                imageTarget.sprite = null;
                imageTarget.color = new Color(1f, 1f, 1f, 0.08f);
            }

            if (qrPreviewSprite != null)
            {
                Destroy(qrPreviewSprite);
                qrPreviewSprite = null;
            }

            if (qrPreviewTexture != null)
            {
                Destroy(qrPreviewTexture);
                qrPreviewTexture = null;
            }
        }

        private void ShowQrLoading(RawImage rawTarget, Image imageTarget)
        {
            if (qrLoadingCancellation != null)
            {
                qrLoadingCancellation.Cancel();
                qrLoadingCancellation.Dispose();
                qrLoadingCancellation = null;
            }

            qrLoadingText ??= FindTextInScreen(BoothUiScreenId.Preview, "QrLoading", "QrLoadingText") ?? FindText("QrLoading");
            if (qrLoadingText == null)
            {
                var targetTransform = rawTarget != null ? rawTarget.transform : imageTarget != null ? imageTarget.transform : null;
                if (targetTransform?.parent != null)
                {
                    qrLoadingText = CreateText(
                        targetTransform.parent,
                        "QrLoading",
                        "LOADING",
                        20,
                        TextAlignmentOptions.Center,
                        Vector2.one * 0.5f,
                        Vector2.one * 0.5f,
                        ((RectTransform)targetTransform).anchoredPosition,
                        ((RectTransform)targetTransform).sizeDelta);
                    qrLoadingText.color = new Color(0.08f, 0.08f, 0.08f, 0.82f);
                }
            }

            if (qrLoadingText == null)
            {
                return;
            }

            AlignQrLoadingToTarget(rawTarget, imageTarget);
            qrLoadingText.gameObject.SetActive(true);
            qrLoadingCancellation = new CancellationTokenSource();
            _ = RunQrLoadingAsync(qrLoadingCancellation.Token);
        }

        private void AlignQrLoadingToTarget(RawImage rawTarget, Image imageTarget)
        {
            var targetTransform = rawTarget != null ? rawTarget.transform as RectTransform : imageTarget != null ? imageTarget.transform as RectTransform : null;
            if (qrLoadingText == null || targetTransform == null || qrLoadingText.transform is not RectTransform loadingTransform)
            {
                return;
            }

            loadingTransform.SetParent(targetTransform.parent, false);
            loadingTransform.anchorMin = targetTransform.anchorMin;
            loadingTransform.anchorMax = targetTransform.anchorMax;
            loadingTransform.pivot = targetTransform.pivot;
            loadingTransform.anchoredPosition = targetTransform.anchoredPosition;
            loadingTransform.sizeDelta = targetTransform.sizeDelta;
            loadingTransform.localScale = Vector3.one;
            loadingTransform.SetAsLastSibling();
        }

        private void HideQrLoading()
        {
            if (qrLoadingCancellation != null)
            {
                qrLoadingCancellation.Cancel();
                qrLoadingCancellation.Dispose();
                qrLoadingCancellation = null;
            }

            if (qrLoadingText != null)
            {
                qrLoadingText.gameObject.SetActive(false);
            }
        }

        private async Task RunQrLoadingAsync(CancellationToken cancellationToken)
        {
            var frames = new[] { "LOADING", "LOADING.", "LOADING..", "LOADING..." };
            var index = 0;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (qrLoadingText != null)
                    {
                        qrLoadingText.SetText(frames[index % frames.Length]);
                    }

                    index += 1;
                    await Task.Delay(220, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private RawImage ResolveQrPreviewRawImage()
        {
            var previewRawImage = FindRawImageInScreen(BoothUiScreenId.Preview, "QrPreview", "QR Code");
            if (previewRawImage != null)
            {
                qrPreview = previewRawImage;
                return previewRawImage;
            }

            if (currentScreen == BoothUiScreenId.Preview)
            {
                return null;
            }

            return qrPreview;
        }

        private Image ResolveQrPreviewImage()
        {
            var previewImage = FindImageInScreen(BoothUiScreenId.Preview, "QR Code", "QrPreview");
            if (previewImage != null)
            {
                qrPreviewImage = previewImage;
                return previewImage;
            }

            qrPreviewImage ??= FindImage("QR Code");
            return qrPreviewImage;
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
            EnsureDefaultThemes();
            CreateBackgroundImage(themeSelect.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(themeSelect.transform, "LogoArt", "piece_01", new Rect(2380f, 160f, 1500f, 520f), new Vector2(0.5f, 0.86f), new Vector2(720f, 250f));
            CreateText(themeSelect.transform, "ChooseFrameTitle", "CHOOSE\nYOUR FRAME", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.76f), new Vector2(0.5f, 0.76f), Vector2.zero, new Vector2(760f, 160f)).color = Color.black;
            priceText = CreateText(themeSelect.transform, "ThemePrice", string.Empty, 30, TextAlignmentOptions.Center, new Vector2(0.5f, 0.79f), new Vector2(0.5f, 0.79f), Vector2.zero, new Vector2(600f, 44f));
            priceText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            var themeMonsterButton = CreateButton(themeSelect.transform, "ThemeMonsterButton", "MONSTER", new Vector2(0.45f, 0.64f), Vector2.zero, new Vector2(170f, 112f));
            var themeMonsterOffButton = CreateButton(themeSelect.transform, "ThemeMonsterOffButton", "OFF", new Vector2(0.60f, 0.64f), Vector2.zero, new Vector2(120f, 112f));
            StyleMrkremeButton(themeMonsterButton, new Color(0.94f, 0.91f, 0.82f, 1f), Color.black);
            StyleMrkremeButton(themeMonsterOffButton, Color.black, Color.white);
            themeMonsterButton.onClick.AddListener(SelectMonsterFrameFromUi);
            themeMonsterOffButton.onClick.AddListener(SelectNoMonsterFrameFromUi);
            var themeSlots = new[]
            {
                (new Vector2(0.285f, 0.48f), 0),
                (new Vector2(0.715f, 0.48f), 1)
            };

            for (var i = 0; i < themeSlots.Length; i++)
            {
                var index = themeSlots[i].Item2;
                var position = themeSlots[i].Item1;
                var theme = index >= 0 && index < themes.Length ? themes[index] : null;
                var themePreview = theme?.previewSprite ?? theme?.frameTemplateSprite;
                if (themePreview != null)
                {
                    var image = CreateImage(themeSelect.transform, $"FramePreview{i + 1}", position, Vector2.zero, new Vector2(390f, 520f));
                    image.sprite = themePreview;
                    image.preserveAspect = true;
                    image.color = Color.white;
                }
                else
                {
                    CreateSheetImage(themeSelect.transform, $"FramePreview{i + 1}", "piece_02", new Rect(2180f, 120f, 930f, 1250f), position, new Vector2(390f, 520f));
                }

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

            var voucherEntry = CreateScreen(background.transform, BoothUiScreenId.VoucherEntry, builtScreens);
            CreateBackgroundImage(voucherEntry.transform, "CreamPatternBackground", "piece_06");
            CreatePanelBlock(voucherEntry.transform, "TopMetalPanel", new Vector2(0.5f, 0.91f), new Vector2(1080f, 330f), MetalColor);
            CreateText(voucherEntry.transform, "VoucherTitle", "TYPE YOUR\nCODE HERE", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.91f), new Vector2(0.5f, 0.91f), Vector2.zero, new Vector2(760f, 180f)).color = Color.black;
            CreateSheetImage(voucherEntry.transform, "BoardingPassArt", "piece_02", new Rect(95f, 2515f, 1350f, 1370f), new Vector2(0.5f, 0.62f), new Vector2(610f, 620f));
            voucherCodeEntryText = CreateText(voucherEntry.transform, "VoucherCodeText", string.Empty, 34, TextAlignmentOptions.Left, new Vector2(0.5f, 0.49f), new Vector2(0.5f, 0.49f), new Vector2(-78f, 0f), new Vector2(520f, 52f));
            voucherCodeEntryText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            voucherCodeCountText = CreateText(voucherEntry.transform, "VoucherCodeCountText", $"0 / {VoucherCodeMaxLength}", 30, TextAlignmentOptions.Right, new Vector2(0.5f, 0.49f), new Vector2(0.5f, 0.49f), new Vector2(160f, 0f), new Vector2(240f, 52f));
            voucherCodeCountText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            BuildVoucherKeyboard(voucherEntry.transform);
            CreatePanelBlock(voucherEntry.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            var voucherBackButton = CreateButton(voucherEntry.transform, "VoucherBackButton", "BACK", new Vector2(0.25f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
            var confirmVoucherButton = CreateButton(voucherEntry.transform, "ConfirmVoucherButton", "APPLY", new Vector2(0.75f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
            StyleMrkremeButton(voucherBackButton, new Color(0.85f, 0.24f, 0.18f, 1f), Color.white);
            StyleMrkremeButton(confirmVoucherButton);
            voucherBackButton.onClick.AddListener(BackFromVoucherEntryFromUi);
            confirmVoucherButton.onClick.AddListener(ConfirmVoucherFromUi);

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

            var arSelect = CreateScreen(background.transform, BoothUiScreenId.ArSelect, builtScreens);
            EnsureDefaultArStickerPresets();
            CreateBackgroundImage(arSelect.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(arSelect.transform, "LogoArt", "piece_01", new Rect(2380f, 160f, 1500f, 520f), new Vector2(0.5f, 0.86f), new Vector2(720f, 250f));
            CreateText(arSelect.transform, "ChooseArTitle", "CHOOSE\nYOUR AR", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.76f), new Vector2(0.5f, 0.76f), Vector2.zero, new Vector2(760f, 160f)).color = Color.black;
            var arSlots = new[]
            {
                new Vector2(0.42f, 0.52f),
                new Vector2(0.58f, 0.52f)
            };
            var arNoneSelectButton = CreateButton(arSelect.transform, "ArPresetNoneButton", "OFF", arSlots[1], Vector2.zero, new Vector2(150f, 120f));
            StyleMrkremeButton(arNoneSelectButton, Color.black, Color.white);
            arNoneSelectButton.onClick.AddListener(SelectNoArPresetFromUi);
            for (var i = 0; i < 1; i++)
            {
                var index = i;
                var preset = index >= 0 && index < arStickerPresets.Length ? arStickerPresets[index] : null;
                if (preset?.previewSprite != null)
                {
                    var image = CreateImage(arSelect.transform, $"ArPresetPreview{i + 1}", arSlots[i], Vector2.zero, new Vector2(360f, 260f));
                    image.sprite = preset.previewSprite;
                    image.preserveAspect = true;
                    image.color = Color.white;
                }
                else
                {
                    CreatePanelBlock(arSelect.transform, $"ArPresetPreview{i + 1}", arSlots[i], new Vector2(360f, 260f), new Color(0.08f, 0.08f, 0.08f, 0.12f));
                }

                var button = CreateInvisibleButton(arSelect.transform, $"ArPresetButton{i + 1}", arSlots[i], new Vector2(360f, 260f));
                button.onClick.AddListener(SelectMonsterArPresetFromUi);
            }

            CreatePanelBlock(arSelect.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            var arBackButton = CreateButton(arSelect.transform, "ArPresetBackButton", "BACK", new Vector2(0.25f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
            var arConfirmButton = CreateButton(arSelect.transform, "ArPresetConfirmButton", "CONFIRM", new Vector2(0.75f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
            StyleMrkremeButton(arBackButton);
            StyleMrkremeButton(arConfirmButton);
            arBackButton.onClick.AddListener(() => SwitchScreen(BoothUiScreenId.PaymentMock, "Choose payment."));
            arConfirmButton.onClick.AddListener(ConfirmArPresetSelectionFromUi);

            var capture = CreateScreen(background.transform, BoothUiScreenId.Capture, builtScreens);
            CreateBackgroundImage(capture.transform, "YellowGridBackground", "piece_05");
            CreatePanelBlock(capture.transform, "TopMetalPanel", new Vector2(0.5f, 0.91f), new Vector2(1080f, 330f), MetalColor);
            CreateText(capture.transform, "CaptureTitle", "TAKE A PHOTO", 62, TextAlignmentOptions.Center, new Vector2(0.5f, 0.91f), new Vector2(0.5f, 0.91f), Vector2.zero, new Vector2(760f, 120f)).color = Color.black;
            CreatePanelBlock(capture.transform, "CameraFrame", new Vector2(0.5f, 0.54f), new Vector2(900f, 900f), Color.black);
            cameraPreview = CreateRawImage(capture.transform, "CameraPreview", new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(860f, 860f));
            cameraPreview.color = Color.white;
            arModelOverlay = null;
            arPreviewOverlay = CreateRawImage(capture.transform, "ArPreviewOverlay", new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(860f, 860f));
            arPreviewOverlay.color = Color.clear;
            captureForegroundOverlay = CreateImage(capture.transform, "CaptureForegroundOverlay", Vector2.one * 0.5f, Vector2.zero, new Vector2(1080f, 1920f));
            captureForegroundOverlay.color = Color.clear;
            captureForegroundOverlay.raycastTarget = false;
            StretchGraphicToParent(captureForegroundOverlay);
            countdownText = CreateText(capture.transform, "Countdown", string.Empty, 120, TextAlignmentOptions.Center, new Vector2(0.5f, 0.54f), new Vector2(0.5f, 0.54f), Vector2.zero, new Vector2(360f, 180f));
            captureCountText = CreateText(capture.transform, "CaptureCount", "AMOUNT 1 / 1", 28, TextAlignmentOptions.Center, new Vector2(0.5f, 0.205f), new Vector2(0.5f, 0.205f), Vector2.zero, new Vector2(300f, 50f));
            captureCountText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            EnsureDefaultArStickerPresets();
            var captureArSlots = new[]
            {
                new Vector2(0.42f, 0.155f),
                new Vector2(0.58f, 0.155f)
            };
            var noArButton = CreateButton(capture.transform, "ArPresetNoneButton", "OFF", captureArSlots[0], Vector2.zero, new Vector2(130f, 104f));
            StyleMrkremeButton(noArButton, new Color(0.94f, 0.91f, 0.82f, 1f), Color.black);
            noArButton.onClick.AddListener(SelectNoArPresetFromUi);
            for (var i = 1; i < 2; i++)
            {
                const int presetIndex = 0;
                var preset = presetIndex < arStickerPresets.Length ? arStickerPresets[presetIndex] : null;
                if (preset?.previewSprite != null)
                {
                    var image = CreateImage(capture.transform, $"ArPresetPreview{i}", captureArSlots[i], Vector2.zero, new Vector2(130f, 104f));
                    image.sprite = preset.previewSprite;
                    image.preserveAspect = true;
                    image.color = Color.white;
                }
                else
                {
                    CreatePanelBlock(capture.transform, $"ArPresetPreview{i}", captureArSlots[i], new Vector2(130f, 104f), new Color(0.94f, 0.91f, 0.82f, 1f));
                }

                var arButton = CreateInvisibleButton(capture.transform, $"ArPresetButton{i}", captureArSlots[i], new Vector2(130f, 104f));
                arButton.onClick.AddListener(SelectMonsterArPresetFromUi);
            }
            CreatePanelBlock(capture.transform, "BottomMetalPanel", new Vector2(0.5f, 0.075f), new Vector2(1080f, 280f), MetalColor);
            captureButton = CreateButton(capture.transform, "CaptureButton", string.Empty, new Vector2(0.5f, 0.075f), Vector2.zero, new Vector2(150f, 150f));
            StyleCaptureButton(captureButton);
            captureButton.onClick.AddListener(CaptureFromUi);
            // captureRetakeButton = CreateButton(capture.transform, "CaptureRetakeButton", "RETAKE", new Vector2(0.22f, 0.075f), Vector2.zero, new Vector2(220f, 72f));
            // StyleMrkremeButton(captureRetakeButton, new Color(0.94f, 0.91f, 0.82f, 1f), Color.black);
            // captureRetakeButton.onClick.AddListener(RetakeFromUi);
            // captureRetakeButton.gameObject.SetActive(false);

            var preview = CreateScreen(background.transform, BoothUiScreenId.Preview, builtScreens);
            CreateBackgroundImage(preview.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(preview.transform, "LogoArt", "piece_01", new Rect(2380f, 160f, 1500f, 520f), new Vector2(0.5f, 0.86f), new Vector2(680f, 235f));
            qrPreview = CreateRawImage(preview.transform, "QrPreview", new Vector2(0.79f, 0.78f), Vector2.zero, new Vector2(180f, 180f));
            qrLoadingText = CreateText(preview.transform, "QrLoading", "LOADING", 20, TextAlignmentOptions.Center, new Vector2(0.79f, 0.78f), new Vector2(0.79f, 0.78f), Vector2.zero, new Vector2(180f, 180f));
            qrLoadingText.color = new Color(0.08f, 0.08f, 0.08f, 0.82f);
            qrLoadingText.gameObject.SetActive(false);
            motionPreview = CreateRawImage(preview.transform, "MotionPreview", new Vector2(0.29f, 0.63f), Vector2.zero, new Vector2(360f, 280f));
            composedPreview = CreateRawImage(preview.transform, "ComposedPreview", new Vector2(0.71f, 0.63f), Vector2.zero, new Vector2(360f, 280f));
            CreateText(preview.transform, "MotionLabel", "COUNTDOWN CLIP", 20, TextAlignmentOptions.Center, new Vector2(0.29f, 0.81f), new Vector2(0.29f, 0.81f), Vector2.zero, new Vector2(360f, 40f)).color = new Color(0.08f, 0.08f, 0.08f, 1f);
            CreateText(preview.transform, "ComposedLabel", "PHOTO PREVIEW", 20, TextAlignmentOptions.Center, new Vector2(0.71f, 0.81f), new Vector2(0.71f, 0.81f), Vector2.zero, new Vector2(360f, 40f)).color = new Color(0.08f, 0.08f, 0.08f, 1f);
            // retakeButton = CreateButton(preview.transform, "RetakeButton", "BACK", new Vector2(0.25f, 0.1f), Vector2.zero, new Vector2(260f, 84f));
            // continueButton = CreateButton(preview.transform, "ContinueButton", "CONFIRM", new Vector2(0.75f, 0.1f), Vector2.zero, new Vector2(260f, 84f));
            // StyleMrkremeButton(retakeButton);
            // StyleMrkremeButton(continueButton);
            // retakeButton.onClick.AddListener(RetakeFromUi);
            // continueButton.onClick.AddListener(FinishPreviewAndReturnHomeFromUi);

            var fulfillment = CreateScreen(background.transform, BoothUiScreenId.Fulfillment, builtScreens);
            CreateBackgroundImage(fulfillment.transform, "CreamPatternBackground", "piece_06");
            CreateSheetImage(fulfillment.transform, "LogoArt", "piece_02", new Rect(75f, 95f, 1500f, 1150f), new Vector2(0.5f, 0.78f), new Vector2(700f, 540f));
            CreateSheetImage(fulfillment.transform, "SelectedFrameArt", "piece_03", new Rect(420f, 130f, 2920f, 2450f), new Vector2(0.5f, 0.46f), new Vector2(820f, 690f));
            var fulfillmentQrPreview = CreateRawImage(fulfillment.transform, "QrPreview", new Vector2(0.79f, 0.78f), Vector2.zero, new Vector2(180f, 180f));
            qrPreview ??= fulfillmentQrPreview;
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
            printStatusText = CreateText(printing.transform, "PrintStatus", "Printing your photo...", 32, TextAlignmentOptions.Center, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(760f, 100f));
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
            EnsureVoucherEntryScreen();
            cameraPreview ??= FindRawImageInScreen(BoothUiScreenId.Capture, "CameraPreview") ?? FindRawImage("CameraPreview");
            captureCountText ??= FindText("CaptureCount");
            captureForegroundOverlay ??= FindImage("CaptureForegroundOverlay");
            motionPreview ??= FindRawImageInScreen(BoothUiScreenId.Preview, "MotionPreview");
            composedPreview ??= FindRawImageInScreen(BoothUiScreenId.Preview, "ComposedPreview");
            qrPreviewImage ??= FindImageInScreen(BoothUiScreenId.Preview, "QR Code", "QrPreview") ?? FindImage("QR Code");
            qrPreview = FindRawImageInScreen(BoothUiScreenId.Preview, "QrPreview", "QR Code") ?? qrPreview ?? FindRawImage("QrPreview");
            qrLoadingText ??= FindTextInScreen(BoothUiScreenId.Preview, "QrLoading", "QrLoadingText") ?? FindText("QrLoading");
            previewFrameImage ??= FindPreviewFrameImage();
            voucherCodeEntryText ??= FindTextInScreen(BoothUiScreenId.VoucherEntry, "VoucherCodeText");
            voucherCodeCountText ??= FindTextInScreen(BoothUiScreenId.VoucherEntry, "VoucherCodeCountText");
            BindAutomaticCaptureUi();
            EnsureVoucherModal();
            EnsureVoucherScanUi(FindScreenRoot(BoothUiScreenId.VoucherEntry));
            if (captureForegroundOverlay != null)
            {
                captureForegroundOverlay.raycastTarget = false;
            }

            UpdateCaptureCountText();
            UpdateVoucherCodeDisplay();
            SetObjectActive("ThemeButton3", false);
            SetObjectActive("ThemeButton4", false);
            SetObjectActive("FramePreview3", false);
            SetObjectActive("FramePreview4", false);
            SetObjectActive("ArPresetButton2", false);
            SetObjectActive("ArPresetButton3", false);
            SetObjectActive("ArPresetButton4", false);
            SetObjectActive("ArPresetPreview2", false);
            SetObjectActive("ArPresetPreview3", false);
            SetObjectActive("ArPresetPreview4", false);

            WireButton("StartButton", StartSessionFromUi);
            WireButton("ThemeButton1", () => ChooseThemeCandidateFromUi(0));
            WireButton("ThemeButton2", () => ChooseThemeCandidateFromUi(1));
            WireButton("ThemeMonsterButton", SelectMonsterFrameFromUi);
            WireButton("ThemeMonsterOffButton", SelectNoMonsterFrameFromUi);
            WireButton("Monster", SelectMonsterFrameFromUi);
            WireButton("No Monster", SelectNoMonsterFrameFromUi);
            WireClickableGraphic("FramePreview1", () => ChooseThemeCandidateFromUi(0));
            WireClickableGraphic("FramePreview2", () => ChooseThemeCandidateFromUi(1));
            WireMonsterFramePreviewButtons();
            WireButton("ThemeBackButton", ResetFromUi);
            WireButton("ThemeConfirmButton", ConfirmThemeSelectionFromUi);
            WireButton("ArPresetButton1", SelectMonsterArPresetFromUi);
            WireButton("ArPresetNoneButton", SelectNoArPresetFromUi);
            WireButton("ArPresetButton0", SelectNoArPresetFromUi);
            WireButton("ArPresetBackButton", () => SwitchScreen(BoothUiScreenId.PaymentMock, "Choose payment."));
            WireButton("ArPresetConfirmButton", ConfirmArPresetSelectionFromUi);
            WireButton("QrPayButton", PayMockFromUi);
            WireButton("CardPayButton", PayMockFromUi);
            WireButton("VoucherButton", OpenVoucherEntryFromUi);
            WireButton("PaymentBackButton", BackFromPaymentFromUi);
            WireButton("ConfirmNameButton", ConfirmNameFromUi);
            WireButton("ConfirmVoucherButton", ConfirmVoucherFromUi);
            WireButton("VoucherBackButton", BackFromVoucherEntryFromUi);
            WireButton("VoucherModalConfirmButton", HideVoucherModal);
            WireButton("VoucherScanButton", StartVoucherScannerFromUi);
            WireButton("VoucherCameraOnButton", TurnVoucherCameraOnFromUi);
            WireButton("VoucherCameraOffButton", TurnVoucherCameraOffFromUi);
            WireButton("VoucherRemoteRefreshButton", RefreshVoucherRemoteScanFromUi);
            WireButton("CaptureButton", CaptureFromUi);
            // WireButtonInScreen(BoothUiScreenId.Capture, "CaptureRetakeButton", RetakeFromUi);
            WireButtonInScreen(BoothUiScreenId.Capture, "CaptureRefreshButton", RetakeFromUi);
            WireButtonInScreen(BoothUiScreenId.Preview, "RetakeButton", RetakeFromUi);
            WireButtonInScreen(BoothUiScreenId.Preview, "ContinueButton", FinishPreviewAndReturnHomeFromUi);
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

            const string digits = "1234567890";
            foreach (var digit in digits)
            {
                var character = digit.ToString();
                WireButton($"Key_{character}", () => AppendNameCharacterFromUi(character));
            }

            WireButton("Key_Back", BackspaceNameFromUi);
            WireButton("Key_Delete", BackspaceNameFromUi);
            WireVoucherKeyboardButtons();
        }

        private void WireMonsterFramePreviewButtons()
        {
            WireThemePreviewGrid("Grid - No Monster", useMonsterFrame: false);
            WireThemePreviewGrid("Grid - Monster", useMonsterFrame: true);
        }

        private void EnsureVoucherEntryScreen()
        {
            if (FindScreenRoot(BoothUiScreenId.VoucherEntry) != null)
            {
                return;
            }

            var artStyleRoot = FindScreenRoot(BoothUiScreenId.ArtStyleSelect);
            if (artStyleRoot == null)
            {
                return;
            }

            var voucherScreen = Instantiate(artStyleRoot.gameObject, artStyleRoot.parent);
            voucherScreen.name = "VoucherEntryScreen";
            voucherScreen.SetActive(false);
            RegisterScreenBinding(BoothUiScreenId.VoucherEntry, voucherScreen);
            ConfigureVoucherEntryScreen(voucherScreen.transform);
        }

        private void RegisterScreenBinding(BoothUiScreenId screenId, GameObject root)
        {
            if (root == null)
            {
                return;
            }

            if (screens == null)
            {
                screens = new[] { new ScreenBinding { screenId = screenId, root = root } };
                return;
            }

            for (var i = 0; i < screens.Length; i++)
            {
                if (screens[i] == null || screens[i].screenId != screenId)
                {
                    continue;
                }

                screens[i].root = root;
                return;
            }

            Array.Resize(ref screens, screens.Length + 1);
            screens[^1] = new ScreenBinding { screenId = screenId, root = root };
        }

        private void ConfigureVoucherEntryScreen(Transform root)
        {
            if (root == null)
            {
                return;
            }

            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                if (button != null)
                {
                    button.onClick = new Button.ButtonClickedEvent();
                }
            }

            var title = FindChildText(root, "NameTitle");
            if (title != null)
            {
                title.SetText("TYPE YOUR\nCODE HERE");
            }

            var entryText = FindChildText(root, "PassengerName");
            if (entryText != null)
            {
                entryText.name = "VoucherCodeText";
                entryText.SetText(string.Empty);
                voucherCodeEntryText = entryText;
            }

            var countText = FindChildText(root, "NameCount");
            if (countText != null)
            {
                countText.name = "VoucherCodeCountText";
                countText.SetText($"0 / {VoucherCodeMaxLength}");
                voucherCodeCountText = countText;
            }

            var confirmButton = FindChildButton(root, "ConfirmNameButton");
            if (confirmButton != null)
            {
                confirmButton.name = "ConfirmVoucherButton";
                var label = confirmButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null)
                {
                    label.SetText("APPLY");
                }
            }

            var bottomPanel = root.Find("BottomMetalPanel");
            if (bottomPanel != null)
            {
                var backButton = CreateButton(bottomPanel.parent, "VoucherBackButton", "BACK", new Vector2(0.25f, 0.075f), Vector2.zero, new Vector2(420f, 120f));
                StyleMrkremeButton(backButton, new Color(0.85f, 0.24f, 0.18f, 1f), Color.white);

                var applyButton = confirmButton != null ? confirmButton.GetComponent<RectTransform>() : null;
                if (applyButton != null)
                {
                    applyButton.anchorMin = new Vector2(0.75f, 0.075f);
                    applyButton.anchorMax = new Vector2(0.75f, 0.075f);
                    applyButton.anchoredPosition = Vector2.zero;
                    applyButton.sizeDelta = new Vector2(420f, 120f);
                }
            }

            RemoveKeyboardButtons(root);
            BuildVoucherKeyboard(root);
            EnsureVoucherModal();
            EnsureVoucherScanUi(root);
            EnsureVoucherRemoteScanUi(root);
            UpdateVoucherCodeDisplay();
        }

        private void EnsureVoucherScanUi(Transform root)
        {
            if (root == null)
            {
                return;
            }

            voucherScanButton ??= FindChildButton(root, "VoucherScanButton");
            if (voucherScanButton == null)
            {
                voucherScanButton = CreateButton(root, "VoucherScanButton", "SCAN QR", new Vector2(0.5f, 0.405f), Vector2.zero, new Vector2(360f, 70f));
                StyleKeyboardButton(voucherScanButton);
            }

            voucherCameraOnButton ??= FindChildButton(root, "VoucherCameraOnButton");
            if (voucherCameraOnButton == null)
            {
                voucherCameraOnButton = CreateButton(root, "VoucherCameraOnButton", "CAM ON", new Vector2(0.39f, 0.455f), Vector2.zero, new Vector2(180f, 54f));
                StyleKeyboardButton(voucherCameraOnButton);
            }

            voucherCameraOffButton ??= FindChildButton(root, "VoucherCameraOffButton");
            if (voucherCameraOffButton == null)
            {
                voucherCameraOffButton = CreateButton(root, "VoucherCameraOffButton", "CAM OFF", new Vector2(0.61f, 0.455f), Vector2.zero, new Vector2(180f, 54f));
                StyleKeyboardButton(voucherCameraOffButton);
            }

            voucherScanStatusText ??= FindChildText(root, "VoucherScanStatusText");
            if (voucherScanStatusText == null)
            {
                voucherScanStatusText = CreateText(root, "VoucherScanStatusText", string.Empty, 24, TextAlignmentOptions.Center, new Vector2(0.5f, 0.365f), new Vector2(0.5f, 0.365f), Vector2.zero, new Vector2(620f, 54f));
                voucherScanStatusText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            }

            voucherScanPreviewFrame ??= FindDescendant(root, "VoucherScanPreviewFrame")?.gameObject;
            voucherScanPreview ??= FindChildRawImage(root, "VoucherScanPreview");
            if (voucherScanPreview == null)
            {
                if (voucherScanPreviewFrame == null)
                {
                    CreatePanelBlock(root, "VoucherScanPreviewFrame", new Vector2(0.5f, 0.62f), new Vector2(380f, 260f), new Color(0.05f, 0.05f, 0.05f, 0.8f));
                    voucherScanPreviewFrame = FindDescendant(root, "VoucherScanPreviewFrame")?.gameObject;
                }

                voucherScanPreview = CreateRawImage(root, "VoucherScanPreview", new Vector2(0.5f, 0.62f), Vector2.zero, new Vector2(360f, 240f));
                voucherScanPreview.gameObject.SetActive(false);
            }

            SetVoucherScanPreviewVisible(false);
            EnsureVoucherRemoteScanUi(root);
        }

        private void EnsureVoucherRemoteScanUi(Transform root)
        {
            if (root == null)
            {
                return;
            }

            voucherRemoteScanPanel ??= FindDescendant(root, "VoucherRemoteScanPanel")?.gameObject;
            if (voucherRemoteScanPanel == null)
            {
                voucherRemoteScanPanel = new GameObject("VoucherRemoteScanPanel", typeof(RectTransform), typeof(Image));
                voucherRemoteScanPanel.transform.SetParent(root, false);
            }

            voucherRemoteScanPanel.SetActive(true);
            voucherRemoteScanPanel.transform.SetAsLastSibling();
            var panelRect = voucherRemoteScanPanel.GetComponent<RectTransform>();
            if (panelRect != null)
            {
                panelRect.anchorMin = new Vector2(0.5f, 0.62f);
                panelRect.anchorMax = new Vector2(0.5f, 0.62f);
                panelRect.pivot = new Vector2(0.5f, 0.5f);
                panelRect.anchoredPosition = Vector2.zero;
                panelRect.sizeDelta = new Vector2(460f, 360f);
            }

            var panelBackground = voucherRemoteScanPanel.GetComponent<Image>();
            if (panelBackground != null)
            {
                panelBackground.color = new Color(0.95f, 0.9f, 0.78f, 0.96f);
                panelBackground.raycastTarget = false;
            }

            var panel = voucherRemoteScanPanel.transform;
            voucherRemoteQrImage ??= FindChildRawImage(panel, "VoucherRemoteQrImage");
            if (voucherRemoteQrImage == null)
            {
                voucherRemoteQrImage = CreateRawImage(panel, "VoucherRemoteQrImage", new Vector2(0.5f, 0.64f), Vector2.zero, new Vector2(260f, 260f));
            }
            voucherRemoteQrImage.gameObject.SetActive(true);
            voucherRemoteQrImage.color = Color.white;
            var qrRect = voucherRemoteQrImage.GetComponent<RectTransform>();
            if (qrRect != null)
            {
                qrRect.anchorMin = new Vector2(0.5f, 0.64f);
                qrRect.anchorMax = new Vector2(0.5f, 0.64f);
                qrRect.anchoredPosition = Vector2.zero;
                qrRect.sizeDelta = new Vector2(260f, 260f);
            }

            voucherRemoteScanStatusText ??= FindChildText(panel, "VoucherRemoteScanStatusText");
            if (voucherRemoteScanStatusText == null)
            {
                voucherRemoteScanStatusText = CreateText(panel, "VoucherRemoteScanStatusText", "Preparing QR...", 22, TextAlignmentOptions.Center, new Vector2(0.5f, 0.19f), new Vector2(0.5f, 0.19f), Vector2.zero, new Vector2(400f, 74f));
            }
            voucherRemoteScanStatusText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            var statusRect = voucherRemoteScanStatusText.GetComponent<RectTransform>();
            if (statusRect != null)
            {
                statusRect.anchorMin = new Vector2(0.5f, 0.19f);
                statusRect.anchorMax = new Vector2(0.5f, 0.19f);
                statusRect.anchoredPosition = Vector2.zero;
                statusRect.sizeDelta = new Vector2(400f, 74f);
            }

            voucherRemoteRefreshButton ??= FindChildButton(panel, "VoucherRemoteRefreshButton");
            if (voucherRemoteRefreshButton == null)
            {
                voucherRemoteRefreshButton = CreateButton(panel, "VoucherRemoteRefreshButton", "REFRESH", new Vector2(0.5f, 0.055f), Vector2.zero, new Vector2(250f, 54f));
                StyleKeyboardButton(voucherRemoteRefreshButton);
            }
            var refreshRect = voucherRemoteRefreshButton.GetComponent<RectTransform>();
            if (refreshRect != null)
            {
                refreshRect.anchorMin = new Vector2(0.5f, 0.055f);
                refreshRect.anchorMax = new Vector2(0.5f, 0.055f);
                refreshRect.anchoredPosition = Vector2.zero;
                refreshRect.sizeDelta = new Vector2(250f, 54f);
            }
        }

        private void EnsureVoucherModal()
        {
            var root = FindScreenRoot(BoothUiScreenId.VoucherEntry);
            if (root == null)
            {
                return;
            }

            var existingModal = FindDescendant(root, "VoucherAlertModal");
            if (existingModal != null)
            {
                voucherModalRoot = existingModal.gameObject;
                voucherModalMessageText ??= FindChildText(existingModal, "VoucherModalMessageText");
                voucherModalConfirmButton ??= FindChildButton(existingModal, "VoucherModalConfirmButton")
                    ?? FindChildButton(existingModal, "ContinueButton")
                    ?? FindChildButton(existingModal, "ConfirmButton")
                    ?? FindChildButton(existingModal, "AgreeButton");
                if (voucherModalConfirmButton != null)
                {
                    voucherModalConfirmButton.onClick = new Button.ButtonClickedEvent();
                    voucherModalConfirmButton.onClick.AddListener(HideVoucherModal);
                }

                voucherModalRoot.SetActive(false);
                return;
            }

            var modal = new GameObject("VoucherAlertModal", typeof(RectTransform), typeof(Image));
            modal.transform.SetParent(root, false);
            var modalRect = modal.GetComponent<RectTransform>();
            modalRect.anchorMin = Vector2.zero;
            modalRect.anchorMax = Vector2.one;
            modalRect.offsetMin = Vector2.zero;
            modalRect.offsetMax = Vector2.zero;
            modal.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.62f);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(modal.transform, false);
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(780f, 460f);
            panel.GetComponent<Image>().color = new Color(0.95f, 0.9f, 0.78f, 1f);

            voucherModalMessageText = CreateText(panel.transform, "VoucherModalMessageText", string.Empty, 48, TextAlignmentOptions.Center, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.62f), Vector2.zero, new Vector2(680f, 190f));
            voucherModalMessageText.color = new Color(0.08f, 0.08f, 0.08f, 1f);
            voucherModalConfirmButton = CreateButton(panel.transform, "VoucherModalConfirmButton", "confirm", new Vector2(0.5f, 0.24f), Vector2.zero, new Vector2(420f, 110f));
            StyleMrkremeButton(voucherModalConfirmButton);
            voucherModalConfirmButton.onClick.AddListener(HideVoucherModal);

            voucherModalRoot = modal;
            voucherModalRoot.SetActive(false);
        }

        private void RemoveKeyboardButtons(Transform root)
        {
            var buttons = new List<GameObject>();
            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                if (button == null)
                {
                    continue;
                }

                if (button.name.StartsWith("Key_", StringComparison.Ordinal) || button.name.StartsWith("VoucherKey_", StringComparison.Ordinal))
                {
                    buttons.Add(button.gameObject);
                }
            }

            foreach (var button in buttons)
            {
                if (button != null)
                {
                    if (Application.isPlaying)
                    {
                        Destroy(button);
                    }
                    else
                    {
                        DestroyImmediate(button);
                    }
                }
            }
        }

        private void BuildVoucherKeyboard(Transform parent)
        {
            BuildVoucherKeyboardRow(parent, "1234567890", 0.314f, 10, 86f);
            BuildVoucherKeyboardRow(parent, "QWERTYUIOP", 0.248f, 10, 86f);
            BuildVoucherKeyboardRow(parent, "ASDFGHJKL", 0.182f, 9, 86f);
            BuildVoucherKeyboardRow(parent, "ZXCVBNM-", 0.116f, 8, 92f, includeBackspace: true);
        }

        private void BuildVoucherKeyboardRow(Transform parent, string characters, float anchorY, int keyCount, float keyWidth, bool includeBackspace = false)
        {
            var gap = 8f;
            var totalWidth = (keyCount * keyWidth) + ((keyCount - 1) * gap) + (includeBackspace ? keyWidth + 42f : 0f);
            var startX = (-totalWidth / 2f) + (keyWidth / 2f);

            for (var i = 0; i < characters.Length; i++)
            {
                var character = characters[i].ToString();
                var button = CreateButton(parent, $"VoucherKey_{NormalizeVoucherKeyName(character)}", character, new Vector2(0.5f, anchorY), new Vector2(startX + (i * (keyWidth + gap)), 0f), new Vector2(keyWidth, 64f));
                StyleKeyboardButton(button);
            }

            if (!includeBackspace)
            {
                return;
            }

            var backButton = CreateButton(parent, "VoucherKey_Back", "⌫", new Vector2(0.5f, anchorY), new Vector2(startX + (characters.Length * (keyWidth + gap)) + 21f, 0f), new Vector2(keyWidth + 42f, 64f));
            StyleKeyboardButton(backButton);
        }

        private void WireVoucherKeyboardButtons()
        {
            const string keys = "1234567890QWERTYUIOPASDFGHJKLZXCVBNM";
            foreach (var key in keys)
            {
                var character = key.ToString();
                WireButton($"VoucherKey_{character}", () => AppendVoucherCharacterFromUi(character));
            }

            WireButton("VoucherKey_Dash", () => AppendVoucherCharacterFromUi("-"));
            WireButton("VoucherKey_Back", BackspaceVoucherFromUi);
        }

        private static string NormalizeVoucherKeyName(string value)
        {
            return value switch
            {
                "-" => "Dash",
                "." => "Dot",
                "_" => "Underscore",
                _ => value
            };
        }

        private static TextMeshProUGUI FindChildText(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            foreach (var label in root.GetComponentsInChildren<TextMeshProUGUI>(true))
            {
                if (label != null && label.name == name)
                {
                    return label;
                }
            }

            return null;
        }

        private static Button FindChildButton(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                if (button != null && button.name == name)
                {
                    return button;
                }
            }

            return null;
        }

        private static RawImage FindChildRawImage(Transform root, string name)
        {
            if (root == null)
            {
                return null;
            }

            foreach (var rawImage in root.GetComponentsInChildren<RawImage>(true))
            {
                if (rawImage != null && rawImage.name == name)
                {
                    return rawImage;
                }
            }

            return null;
        }

        private void WireThemePreviewGrid(string gridName, bool useMonsterFrame)
        {
            var root = FindScreenRoot(BoothUiScreenId.ThemeSelect);
            if (root == null)
            {
                return;
            }

            foreach (var grid in root.GetComponentsInChildren<Transform>(true))
            {
                if (grid == null || !string.Equals(grid.name, gridName, StringComparison.Ordinal))
                {
                    continue;
                }

                WireThemePreviewInGrid(grid, "FramePreview1", 0, useMonsterFrame);
                WireThemePreviewInGrid(grid, "FramePreview2", 1, useMonsterFrame);
            }
        }

        private void WireThemePreviewInGrid(Transform grid, string previewName, int themeIndex, bool useMonsterFrame)
        {
            foreach (var preview in grid.GetComponentsInChildren<Transform>(true))
            {
                if (preview == null || !string.Equals(preview.name, previewName, StringComparison.Ordinal))
                {
                    continue;
                }

                var button = preview.GetComponent<Button>();
                if (button == null)
                {
                    button = preview.gameObject.AddComponent<Button>();
                    button.transition = Selectable.Transition.None;
                }

                if (button.targetGraphic == null && preview.TryGetComponent<Graphic>(out var graphic))
                {
                    button.targetGraphic = graphic;
                }

                if (button.targetGraphic != null)
                {
                    button.targetGraphic.raycastTarget = true;
                }

                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => ChooseThemeCandidateFromUi(themeIndex, useMonsterFrame));
            }
        }

        private void WireButton(string buttonName, UnityEngine.Events.UnityAction action)
        {
            var button = FindButton(buttonName);
            if (button == null)
            {
                return;
            }

            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
        }

        private void WireButtonInScreen(BoothUiScreenId screenId, string buttonName, UnityEngine.Events.UnityAction action)
        {
            var button = FindButtonInScreen(screenId, buttonName);
            if (button == null)
            {
                WireButton(buttonName, action);
                return;
            }

            button.onClick = new Button.ButtonClickedEvent();
            button.onClick.AddListener(action);
        }

        private void SetObjectActive(string objectName, bool isActive)
        {
            var target = FindTransform(objectName);
            if (target != null)
            {
                target.gameObject.SetActive(isActive);
            }
        }

        private void WireClickableGraphic(string objectName, UnityEngine.Events.UnityAction action)
        {
            var host = FindTransform(objectName);
            if (host == null)
            {
                return;
            }

            var button = host.GetComponent<Button>();
            if (button == null)
            {
                button = host.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
            }

            if (button.targetGraphic == null && host.TryGetComponent<Graphic>(out var graphic))
            {
                button.targetGraphic = graphic;
            }

            if (button.targetGraphic != null)
            {
                button.targetGraphic.raycastTarget = true;
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

        private Button FindButtonInScreen(BoothUiScreenId screenId, params string[] names)
        {
            var root = FindScreenRoot(screenId);
            if (root == null)
            {
                return null;
            }

            foreach (var button in root.GetComponentsInChildren<Button>(true))
            {
                if (button != null && NameMatches(button.name, names))
                {
                    return button;
                }
            }

            return null;
        }

        private Transform FindTransform(string objectName)
        {
            var transforms = GetComponentsInChildren<Transform>(true);
            foreach (var candidate in transforms)
            {
                if (candidate != null && candidate.name == objectName)
                {
                    return candidate;
                }
            }

            return null;
        }

        private static Transform FindDescendant(Transform root, string path)
        {
            if (root == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var current = root;
            var parts = path.Split('/');
            foreach (var part in parts)
            {
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                current = FindDirectChild(current, part);
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        private static Transform FindDirectChild(Transform parent, string childName)
        {
            if (parent == null)
            {
                return null;
            }

            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child != null && string.Equals(child.name, childName, StringComparison.Ordinal))
                {
                    return child;
                }
            }

            return null;
        }

        private TextMeshProUGUI FindText(string textName)
        {
            var labels = GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var label in labels)
            {
                if (label != null && label.name == textName)
                {
                    return label;
                }
            }

            return null;
        }

        private TextMeshProUGUI FindTextInScreen(BoothUiScreenId screenId, params string[] names)
        {
            var root = FindScreenRoot(screenId);
            if (root == null)
            {
                return null;
            }

            var labels = root.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var label in labels)
            {
                if (label != null && NameMatches(label.name, names))
                {
                    return label;
                }
            }

            return null;
        }

        private Transform FindScreenRoot(BoothUiScreenId screenId)
        {
            if (screens == null)
            {
                return null;
            }

            foreach (var binding in screens)
            {
                if (binding != null && binding.screenId == screenId && binding.root != null)
                {
                    return binding.root.transform;
                }
            }

            return null;
        }

        private RawImage FindRawImageInScreen(BoothUiScreenId screenId, params string[] names)
        {
            var root = FindScreenRoot(screenId);
            if (root == null)
            {
                return null;
            }

            var rawImages = root.GetComponentsInChildren<RawImage>(true);
            foreach (var rawImage in rawImages)
            {
                if (rawImage == null || !NameMatches(rawImage.name, names))
                {
                    continue;
                }

                return rawImage;
            }

            return null;
        }

        private RawImage FindRawImage(string rawImageName)
        {
            var rawImages = GetComponentsInChildren<RawImage>(true);
            foreach (var rawImage in rawImages)
            {
                if (rawImage != null && rawImage.name == rawImageName)
                {
                    return rawImage;
                }
            }

            return null;
        }

        private Image FindImageInScreen(BoothUiScreenId screenId, params string[] names)
        {
            var root = FindScreenRoot(screenId);
            if (root == null)
            {
                return null;
            }

            var images = root.GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image == null || !NameMatches(image.name, names))
                {
                    continue;
                }

                return image;
            }

            return null;
        }

        private Image FindImage(string imageName)
        {
            var images = GetComponentsInChildren<Image>(true);
            foreach (var image in images)
            {
                if (image != null && image.name == imageName)
                {
                    return image;
                }
            }

            return null;
        }

        private static bool NameMatches(string candidate, IReadOnlyList<string> names)
        {
            if (string.IsNullOrWhiteSpace(candidate) || names == null)
            {
                return false;
            }

            foreach (var name in names)
            {
                if (candidate == name)
                {
                    return true;
                }
            }

            return false;
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

        private static Image CreateImage(Transform parent, string name, Vector2 anchor, Vector2 anchoredPosition, Vector2 size)
        {
            var host = new GameObject(name, typeof(RectTransform), typeof(Image));
            host.transform.SetParent(parent, false);
            var rect = host.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.anchoredPosition = anchoredPosition;
            rect.sizeDelta = size;
            var image = host.GetComponent<Image>();
            image.color = Color.clear;
            image.type = Image.Type.Simple;
            image.preserveAspect = false;
            image.raycastTarget = false;
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
            BuildKeyboardRow(parent, "1234567890", 0.38f, 10, 64f);
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
                if (character == "0" || char.IsDigit(character[0]) || char.IsLetter(character[0]))
                {
                    button.onClick.AddListener(() => AppendNameCharacterFromUi(character));
                }
            }

            if (!includeBackspace)
            {
                return;
            }

            var backButton = CreateButton(parent, "Key_Back", "⌫", new Vector2(0.5f, anchorY), new Vector2(startX + (characters.Length * (keyWidth + gap)) + 21f, 0f), new Vector2(keyWidth + 42f, 64f));
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

        private static void SetButtonLabel(Button button, string text)
        {
            if (button == null)
            {
                return;
            }

            var label = button.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null && !string.IsNullOrWhiteSpace(text))
            {
                label = CreateText(button.transform, "Label", string.Empty, 26, TextAlignmentOptions.Center, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                var rect = label.GetComponent<RectTransform>();
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
                label.color = new Color(0.08f, 0.08f, 0.08f, 1f);
                label.fontStyle = FontStyles.Bold;
            }

            if (label != null)
            {
                label.SetText(text ?? string.Empty);
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
            StopVoucherScanner("destroy");
            StopVoucherRemoteScanSession(null);
            ClearVoucherRemoteQrTexture();
            cameraCaptureService?.Dispose();
            StopArPreview();
            arTrackingProvider?.Dispose();
            arStickerRenderer.Dispose();
            StopMotionClipPlayback();
            StopLivePhotoPreviewPlayback();
            StopPreviewFrameMotionPlayback();
            HideQrLoading();
            ClearCaptureReviewImage();
            ClearPreview(composedPreview, ref composedPreviewTexture);
            ClearPreview(motionPreview, ref motionPreviewTexture);
            ClearPreviewFrameSlots();
            ClearQrPreview();
            ClearArPreviewOverlay();

            if (arFrameTexture != null)
            {
                Destroy(arFrameTexture);
                arFrameTexture = null;
            }
        }
    }
}
