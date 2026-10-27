using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace HowToFishVR;

[DefaultExecutionOrder(10000)]
public class VRRigManager : MonoBehaviour
{
    public static VRRigManager Instance { get; private set; }

    public VrFrameData CurrentFrame => _frame;
    public bool IsVRActive => VRConfig.EnableVR.Value && OpenXRBridge.SessionReady && _frame.IsSessionRunning;

    public Vector3 RigWorldPos { get; private set; }
    public Quaternion RigWorldRot { get; private set; } = Quaternion.identity;
    public float RigYaw => _rigYaw;
    public float MenuReferenceYaw => _menuReferenceYaw;

    public Vector3 HeadWorldPos { get; private set; }
    public Quaternion HeadWorldRot { get; private set; } = Quaternion.identity;

    public Vector3 LeftGripWorldPos { get; private set; }
    public Quaternion LeftGripWorldRot { get; private set; } = Quaternion.identity;
    public Vector3 LeftAimWorldPos { get; private set; }
    public Quaternion LeftAimWorldRot { get; private set; } = Quaternion.identity;
    public Vector3 LeftHandVelocity { get; private set; }
    public Vector3 LeftHandAngularVelocity { get; private set; }

    public Vector3 RightGripWorldPos { get; private set; }
    public Quaternion RightGripWorldRot { get; private set; } = Quaternion.identity;
    public Vector3 RightAimWorldPos { get; private set; }
    public Quaternion RightAimWorldRot { get; private set; } = Quaternion.identity;
    public Vector3 RightHandVelocity { get; private set; }
    public Vector3 RightHandAngularVelocity { get; private set; }

    public bool IsRightDominant => VRConfig.DominantHand.Value == VRDominantHand.Right;

    public ControllerStateData DominantHandState => IsRightDominant ? _frame.handRight : _frame.handLeft;
    public ControllerStateData OffHandState => IsRightDominant ? _frame.handLeft : _frame.handRight;

    public Vector3 DominantGripWorldPos => IsRightDominant ? RightGripWorldPos : LeftGripWorldPos;
    public Quaternion DominantGripWorldRot => IsRightDominant ? RightGripWorldRot : LeftGripWorldRot;
    public Vector3 DominantAimWorldPos => IsRightDominant ? RightAimWorldPos : LeftAimWorldPos;
    public Quaternion DominantAimWorldRot => IsRightDominant ? RightAimWorldRot : LeftAimWorldRot;
    public Vector3 DominantHandVelocity => IsRightDominant ? RightHandVelocity : LeftHandVelocity;
    public Vector3 DominantHandAngularVelocity => IsRightDominant ? RightHandAngularVelocity : LeftHandAngularVelocity;

    public Vector3 OffHandGripWorldPos => IsRightDominant ? LeftGripWorldPos : RightGripWorldPos;
    public Quaternion OffHandGripWorldRot => IsRightDominant ? LeftGripWorldRot : RightGripWorldRot;
    public Vector3 OffHandAimWorldPos => IsRightDominant ? LeftAimWorldPos : RightAimWorldPos;
    public Quaternion OffHandAimWorldRot => IsRightDominant ? LeftAimWorldRot : RightAimWorldRot;
    public Vector3 OffHandVelocity => IsRightDominant ? LeftHandVelocity : RightHandVelocity;

    public Camera ActiveBaseCamera => _activeBaseCam;
    public Camera LeftEyeCamera => _eyeCams[0];
    public Camera RightEyeCamera => _eyeCams[1];

    private VrFrameData _frame;
    private float _nextInitRetryTime;
    private bool _fatalInitError;
    private bool _loggedWaitingForHmd;

    private readonly Camera[] _eyeCams = new Camera[2];
    private readonly RenderTexture[] _eyeRTs = new RenderTexture[2];
    private readonly RenderTexture[] _submitRTs = new RenderTexture[2];
    private readonly IntPtr[] _submitPtrs = new IntPtr[2];

    private Camera _activeBaseCam;
    private Camera _disabledBaseCam;
    private int _savedVSync = -1;
    private int _savedTargetFps = -1;

    private bool _hasCalibratedHead;
    private Vector3 _calibratedHeadLocalPos = new Vector3(0f, 1.6f, 0f);
    private float _calibratedHeadLocalYaw;
    private float _rigYaw;
    private float _menuYawOffset;
    private float _menuReferenceYaw;
    private float _menuRefYawVel;
    private bool _menuRefYawInitialized;
    private bool _hasAlignedInitialMenu;
    private bool _wasInMainMenuState;
    private bool _wasInSkinCustomization;
    private bool _ensuredUrpTransparentUILayer;
    private bool _snapTurnReady = true;

    private bool _wasDrivingBoat;
    private float _lastBoatYaw;

    // Head-locked 3D fade / underwater overlay sphere
    private GameObject _overlaySphereObj;
    private MeshRenderer _overlaySphereRenderer;
    private Material _overlaySphereMat;

    // 3D controller visualizers when in Main Menu (before Player spawns) + In-Save Wrist Watch Holo-Bezel
    private GameObject _menuHandLeftVisual;
    private GameObject _menuHandRightVisual;
    private GameObject _menuWatchBezelLeft;
    private GameObject _menuWatchBezelRight;
    private GameObject _ingameWatchBezel;
    private Material _menuWatchBezelMat;
    private bool _patchedLiveUrpRenderer;

    // Cached reflection for CanvasManager fade/underwater state & SelfDrivingBoat steady seat anchor
    private static readonly FieldInfo CanvasInstanceField = typeof(CanvasManager).GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo BlackScreenField = typeof(CanvasManager).GetField("_blackScreen", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo UnderwaterImgField = typeof(CanvasManager).GetField("_underwaterImage", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo ActiveSelfBoatField = typeof(SelfDrivingBoat).GetField("_activeBoat", BindingFlags.NonPublic | BindingFlags.Static);
    private static readonly FieldInfo BoatDefaultCamPosField = typeof(SelfDrivingBoat).GetField("_defaultCamPos", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BoatDefaultHeightField = typeof(SelfDrivingBoat).GetField("_defaultHeight", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BoatDefaultLookAtField = typeof(SelfDrivingBoat).GetField("_defaultLookAt", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BoatSkinPosField = typeof(SelfDrivingBoat).GetField("_skinCustomizationPos", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BoatSkinHeightField = typeof(SelfDrivingBoat).GetField("_skinCustomizationHeight", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BoatSkinLookAtField = typeof(SelfDrivingBoat).GetField("_skinCustomizationLookAt", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo BoatCamFollowField = typeof(SelfDrivingBoat).GetField("_camFollowBoat", BindingFlags.NonPublic | BindingFlags.Instance);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        CreateMenuControllerVisuals();
        CreateOverlaySphere();
    }

    private void Start()
    {
        Application.onBeforeRender += OnBeforeRenderCallback;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        StartCoroutine(EndOfFrameRenderLoop());
    }

    private void OnDestroy()
    {
        Application.onBeforeRender -= OnBeforeRenderCallback;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RestoreBaseCamera();
        ReleaseEyeResources();
    }

    private void TryStartupOrRetryOpenXR()
    {
        if (!VRConfig.EnableVR.Value || _fatalInitError || OpenXRBridge.SessionReady) return;
        if (Time.unscaledTime < _nextInitRetryTime) return;
        _nextInitRetryTime = Time.unscaledTime + 2.5f;

        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            Plugin.Log.LogError($"HowToFishVR requires Direct3D 11, but active device is {SystemInfo.graphicsDeviceType}. Launch with -force-d3d11.");
            _fatalInitError = true;
            return;
        }

        if (!OpenXRBridge.InstanceReady)
        {
            int initRes = OpenXRBridge.TryInitOpenXRInstance();
            if (initRes == 0)
            {
                Plugin.Log.LogError($"OpenXR Instance initialization failed permanently: {OpenXRBridge.GetLastNativeError()}");
                _fatalInitError = true;
                return;
            }
            if (initRes < 0)
            {
                if (!_loggedWaitingForHmd)
                {
                    _loggedWaitingForHmd = true;
                    Plugin.Log.LogInfo($"Waiting for OpenXR runtime / VR headset ({OpenXRBridge.GetLastNativeError()}) — will connect automatically when ready.");
                }
                return;
            }
        }

        // Create temporary D3D11 RenderTexture to give OpenXR the exact ID3D11Device*
        RenderTexture sampleRT = new RenderTexture(16, 16, 0, RenderTextureFormat.ARGB32);
        sampleRT.Create();
        IntPtr samplePtr = sampleRT.GetNativeTexturePtr();
        bool created = OpenXRBridge.TryCreateSession(samplePtr);
        sampleRT.Release();
        Destroy(sampleRT);

        if (!created)
        {
            _fatalInitError = true;
            return;
        }

        SetupEyeCamerasAndTextures();
    }

    private void SetupEyeCamerasAndTextures()
    {
        ReleaseEyeResources();

        int w = OpenXRBridge.EyeWidth;
        int h = OpenXRBridge.EyeHeight;
        int msaa = ClampMsaa(VRConfig.MsaaSamples.Value);
        GraphicsFormat submitFormat = MapDxgiToGraphicsFormat(OpenXRBridge.SwapchainDxgiFormat);

        for (int e = 0; e < 2; e++)
        {
            GraphicsFormat depthStencilFmt = SystemInfo.GetGraphicsFormat(DefaultFormat.DepthStencil);
            RenderTextureDescriptor eyeDesc = new RenderTextureDescriptor(w, h)
            {
                dimension = TextureDimension.Tex2D,
                graphicsFormat = GraphicsFormat.B10G11R11_UFloatPack32,
                depthStencilFormat = depthStencilFmt,
                msaaSamples = msaa,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false
            };
            if (eyeDesc.depthStencilFormat == GraphicsFormat.None)
            {
                eyeDesc.depthBufferBits = 24;
            }
            // Fallback if HDR format isn't supported with MSAA
            if (!SystemInfo.IsFormatSupported(eyeDesc.graphicsFormat, GraphicsFormatUsage.Render))
            {
                eyeDesc.graphicsFormat = submitFormat;
            }

            _eyeRTs[e] = new RenderTexture(eyeDesc)
            {
                name = $"HTFVR_EyeRT_{e}",
                antiAliasing = msaa,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _eyeRTs[e].Create();

            RenderTextureDescriptor subDesc = new RenderTextureDescriptor(w, h)
            {
                dimension = TextureDimension.Tex2D,
                graphicsFormat = submitFormat,
                depthBufferBits = 0,
                msaaSamples = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            _submitRTs[e] = new RenderTexture(subDesc)
            {
                name = $"HTFVR_SubmitRT_{e}",
                antiAliasing = 1,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _submitRTs[e].Create();
            _submitPtrs[e] = _submitRTs[e].GetNativeTexturePtr();

            GameObject camObj = new GameObject(e == 0 ? "HTFVR_LeftEyeCamera" : "HTFVR_RightEyeCamera");
            camObj.transform.SetParent(transform, false);
            Camera cam = camObj.AddComponent<Camera>();
            // Enabled during active VR frames so Unity's native FinishFrameRendering renders right after PlayerEmitCanvasGeometry!
            cam.enabled = false;
            cam.depth = e == 0 ? -20f : -19f;
            cam.nearClipPlane = 0.025f;
            cam.farClipPlane = 2000f;
            cam.targetTexture = _eyeRTs[e];
            cam.useOcclusionCulling = false;

            var urpData = camObj.GetComponent<UniversalAdditionalCameraData>() ?? camObj.AddComponent<UniversalAdditionalCameraData>();
            urpData.renderType = CameraRenderType.Base;
            urpData.renderPostProcessing = true;
            urpData.renderShadows = true;
            urpData.requiresColorOption = CameraOverrideOption.UsePipelineSettings;
            urpData.requiresDepthOption = CameraOverrideOption.UsePipelineSettings;

            _eyeCams[e] = cam;
        }

        Plugin.Log.LogInfo($"Created stereo URP cameras & RTs ({w}x{h}, MSAA={msaa}x, SubmitFmt={submitFormat})");
    }

    private static int ClampMsaa(int requested)
    {
        if (requested >= 8) return 8;
        if (requested >= 4) return 4;
        if (requested >= 2) return 2;
        return 1;
    }

    private static GraphicsFormat MapDxgiToGraphicsFormat(int dxgiFormat)
    {
        return dxgiFormat switch
        {
            29 => GraphicsFormat.R8G8B8A8_SRGB,   // DXGI_FORMAT_R8G8B8A8_UNORM_SRGB
            91 => GraphicsFormat.B8G8R8A8_SRGB,   // DXGI_FORMAT_B8G8R8A8_UNORM_SRGB
            28 => GraphicsFormat.R8G8B8A8_UNorm,  // DXGI_FORMAT_R8G8B8A8_UNORM
            87 => GraphicsFormat.B8G8R8A8_UNorm,  // DXGI_FORMAT_B8G8R8A8_UNORM
            _ => GraphicsFormat.R8G8B8A8_SRGB
        };
    }

    private void ReleaseEyeResources()
    {
        for (int e = 0; e < 2; e++)
        {
            if (_eyeCams[e] != null)
            {
                Destroy(_eyeCams[e].gameObject);
                _eyeCams[e] = null;
            }
            if (_eyeRTs[e] != null)
            {
                _eyeRTs[e].Release();
                Destroy(_eyeRTs[e]);
                _eyeRTs[e] = null;
            }
            if (_submitRTs[e] != null)
            {
                _submitRTs[e].Release();
                Destroy(_submitRTs[e]);
                _submitRTs[e] = null;
            }
            _submitPtrs[e] = IntPtr.Zero;
        }
    }

    private void Update()
    {
        if (!VRConfig.EnableVR.Value)
        {
            SetEyeCamerasEnabled(false);
            if (_disabledBaseCam != null) RestoreBaseCamera();
            SetMenuVisualsActive(false);
            return;
        }

        if (Input.GetKeyDown(KeyCode.F12))
        {
            Recenter();
        }

        TryStartupOrRetryOpenXR();
        if (!OpenXRBridge.SessionReady)
        {
            SetEyeCamerasEnabled(false);
            return;
        }

        OpenXRBridge.SetPreferStageSpace(!VRConfig.SeatedMode.Value);

        if (!OpenXRBridge.WaitAndUpdateFrame(out _frame))
        {
            SetEyeCamerasEnabled(false);
            return;
        }

        if (_frame.IsSessionRunning)
        {
            if (_savedVSync < 0)
            {
                _savedVSync = QualitySettings.vSyncCount;
                _savedTargetFps = Application.targetFrameRate;
            }
            // Let xrWaitFrame pace the engine cleanly without double-waiting on desktop VSync
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Application.targetFrameRate != -1) Application.targetFrameRate = -1;

            if (!_hasCalibratedHead && _frame.headPose.IsValid)
            {
                CalibrateHeadNow();
            }

            ProcessRightStickTurn();
            UpdateRigAndPoses();
            PrepareEyeCamerasForFrame();
            VRInputManager.Instance?.UpdateVRInputs(_frame);
        }
        else
        {
            SetEyeCamerasEnabled(false);
            RestoreBaseCamera();
            SetMenuVisualsActive(false);
        }
    }

    private void LateUpdate()
    {
        if (!IsVRActive)
        {
            SetEyeCamerasEnabled(false);
            VRCombatAndBoatPatches.UpdateGlobalCombatVisuals();
            return;
        }
        UpdateRigAndPoses();
        VRCombatAndBoatPatches.UpdateGlobalCombatVisuals();
        UpdateMenuControllerVisuals();
        UpdateOverlaySphere();
        PrepareEyeCamerasForFrame();
    }

    private void OnBeforeRenderCallback()
    {
        if (!IsVRActive) return;
        UpdateRigAndPoses();
        PrepareEyeCamerasForFrame();
        VRUIManager.Instance?.SyncCanvasesImmediate();
    }

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (_overlaySphereObj == null || !_overlaySphereObj.activeSelf) return;
        if (cam == _eyeCams[0] || cam == _eyeCams[1])
        {
            _overlaySphereObj.transform.position = cam.transform.position;
        }
    }

    public void Recenter()
    {
        if (!_frame.headPose.IsValid) return;

        if (MainMenuManager.IsInMenu || !Player.LocalPlayer)
        {
            FaceTowardsWorldYaw(_menuReferenceYaw);
        }
        else
        {
            float prevHeadWorldYaw = HeadWorldRot.eulerAngles.y;
            CalibrateHeadNow();
            _rigYaw = prevHeadWorldYaw - _calibratedHeadLocalYaw;
            VRUIManager.Instance?.RequestMenuRecenter();
        }

        OpenXRBridge.TriggerHaptic(false, 0.25f, 0.04f, 160f);
        OpenXRBridge.TriggerHaptic(true, 0.25f, 0.04f, 160f);
        Plugin.Log.LogInfo("VR view recentered.");
    }

    /// <summary>
    /// Rotates and recenters the VR rig so the player's current HMD forward gaze faces targetWorldYawDegrees directly.
    /// </summary>
    public void FaceTowardsWorldYaw(float targetWorldYawDegrees)
    {
        if (_frame.headPose.IsValid)
        {
            _calibratedHeadLocalPos = _frame.headPose.Position;
            _calibratedHeadLocalYaw = _frame.headPose.Rotation.eulerAngles.y;
            _hasCalibratedHead = true;
        }

        bool inMenu = MainMenuManager.IsInMenu || SelfDrivingBoat.IsDriving || !Player.LocalPlayer;
        if (inMenu)
        {
            float baseYaw = GetSteadyMenuAnchor(out _, immediateYaw: true);
            _menuYawOffset = Mathf.DeltaAngle(baseYaw, targetWorldYawDegrees) - _calibratedHeadLocalYaw;
        }
        else
        {
            _rigYaw = targetWorldYawDegrees - _calibratedHeadLocalYaw;
        }

        UpdateRigAndPoses();
        VRUIManager.Instance?.SyncCanvasesImmediate();
        OpenXRBridge.TriggerHaptic(false, 0.22f, 0.035f, 165f);
        OpenXRBridge.TriggerHaptic(true, 0.22f, 0.035f, 165f);
    }

    public void AlignYawTo(float targetWorldYawDegrees)
    {
        float headLocalYaw = _frame.headPose.IsValid ? _frame.headPose.Rotation.eulerAngles.y : _calibratedHeadLocalYaw;
        _rigYaw = targetWorldYawDegrees - headLocalYaw;
    }

    private void CalibrateHeadNow()
    {
        _calibratedHeadLocalPos = _frame.headPose.Position;
        _calibratedHeadLocalYaw = _frame.headPose.Rotation.eulerAngles.y;
        _hasCalibratedHead = true;
        _menuYawOffset = -_calibratedHeadLocalYaw;
    }

    private void ProcessRightStickTurn()
    {
        if (PlayerThinking.IsThinking) return; // Right Stick X flips Findings pages while open!

        float rx = _frame.handRight.IsActive ? _frame.handRight.stickX : 0f;
        bool inMenu = MainMenuManager.IsInMenu || !Player.LocalPlayer;

        if (VRConfig.TurnMode.Value == VRTurnMode.Smooth)
        {
            if (Mathf.Abs(rx) > 0.2f)
            {
                float delta = rx * VRConfig.SmoothTurnSpeed.Value * Time.unscaledDeltaTime;
                if (inMenu) _menuYawOffset += delta;
                else _rigYaw += delta;
            }
        }
        else
        {
            if (Mathf.Abs(rx) > 0.68f)
            {
                if (_snapTurnReady)
                {
                    float step = Mathf.Sign(rx) * VRConfig.SnapTurnAngle.Value;
                    if (inMenu) _menuYawOffset += step;
                    else _rigYaw += step;
                    _snapTurnReady = false;
                    OpenXRBridge.TriggerHaptic(true, 0.12f, 0.015f, 120f);
                }
            }
            else if (Mathf.Abs(rx) < 0.35f)
            {
                _snapTurnReady = true;
            }
        }
    }

    private Camera ResolveBaseCamera()
    {
        try
        {
            if (MainMenuManager.IsInMenu || SelfDrivingBoat.IsDriving || !Player.LocalPlayer)
            {
                Camera menuCam = MainMenuManager.MenuCam;
                if (menuCam != null) return menuCam;
            }

            if (Player.LocalPlayer != null)
            {
                if (Player.LocalPlayer.Dying != null && Player.LocalPlayer.Dying.IsDead && Player.LocalPlayer.CurCam != null)
                {
                    return Player.LocalPlayer.CurCam;
                }
                if (Player.LocalPlayer.Camera != null && Player.LocalPlayer.Camera.Cam != null)
                {
                    return Player.LocalPlayer.Camera.Cam;
                }
            }

            if (GameInfo.CurCamera != null && GameInfo.CurCamera.targetTexture == null)
            {
                return GameInfo.CurCamera;
            }
        }
        catch
        {
            // Safe guard during scene bootstrap
        }

        return Camera.main;
    }

    private float GetSteadyMenuAnchor(out Vector3 anchorPos, bool immediateYaw = false)
    {
        anchorPos = _activeBaseCam != null ? _activeBaseCam.transform.position : new Vector3(0f, 1.6f, 0f);
        float rawYaw = _activeBaseCam != null ? _activeBaseCam.transform.eulerAngles.y : 0f;

        if (MainMenuManager.IsInMenu && ActiveSelfBoatField != null)
        {
            var boat = ActiveSelfBoatField.GetValue(null) as SelfDrivingBoat;
            if (boat != null)
            {
                bool camFollow = BoatCamFollowField == null || (bool)BoatCamFollowField.GetValue(boat);
                if (camFollow)
                {
                    bool skinMode = LocalSkin.IsInSkinCustomization;
                    Transform posTarget = (skinMode ? BoatSkinPosField?.GetValue(boat) : BoatDefaultCamPosField?.GetValue(boat)) as Transform;
                    Transform lookTarget = (skinMode ? BoatSkinLookAtField?.GetValue(boat) : BoatDefaultLookAtField?.GetValue(boat)) as Transform;
                    float steadyHeight = skinMode
                        ? (float)(BoatSkinHeightField?.GetValue(boat) ?? anchorPos.y)
                        : (float)(BoatDefaultHeightField?.GetValue(boat) ?? anchorPos.y);

                    if (posTarget != null)
                    {
                        // Lock Y to steadyHeight and XZ directly to the boat deck target — zero rubber-band lag or vertical wave heave!
                        anchorPos = new Vector3(posTarget.position.x, steadyHeight, posTarget.position.z);
                    }

                    if (lookTarget != null)
                    {
                        Vector3 flatLook = lookTarget.position - anchorPos;
                        flatLook.y = 0f;
                        if (flatLook.sqrMagnitude > 0.001f)
                        {
                            rawYaw = Quaternion.LookRotation(flatLook, Vector3.up).eulerAngles.y;
                        }
                    }
                    else
                    {
                        rawYaw = boat.transform.eulerAngles.y;
                    }
                }
            }
        }

        if (!_menuRefYawInitialized || immediateYaw)
        {
            _menuReferenceYaw = rawYaw;
            _menuRefYawVel = 0f;
            _menuRefYawInitialized = true;
        }
        else
        {
            // Heavily smooth reference yaw so wave micro-wobbles never twist the VR horizon!
            _menuReferenceYaw = Mathf.SmoothDampAngle(_menuReferenceYaw, rawYaw, ref _menuRefYawVel, 0.65f, 90f, Time.unscaledDeltaTime);
        }

        return _menuReferenceYaw;
    }

    public void UpdateRigAndPoses()
    {
        _activeBaseCam = ResolveBaseCamera();
        Vector3 baseVel = Vector3.zero;

        bool inMainMenu = MainMenuManager.IsInMenu && !Player.LocalPlayer;
        bool inMenuOrCutscene = MainMenuManager.IsInMenu || SelfDrivingBoat.IsDriving || !Player.LocalPlayer;
        bool isDead = !inMenuOrCutscene && Player.LocalPlayer.Dying != null && Player.LocalPlayer.Dying.IsDead;

        if (!inMainMenu)
        {
            _wasInMainMenuState = false;
            _wasInSkinCustomization = false;
        }

        if (inMenuOrCutscene || isDead)
        {
            _wasDrivingBoat = false;
            if (_activeBaseCam != null)
            {
                bool skinCustomizing = inMainMenu && LocalSkin.IsInSkinCustomization;
                bool modeChanged = (inMainMenu && !_wasInMainMenuState) || (inMainMenu && skinCustomizing != _wasInSkinCustomization);

                float steadyBaseYaw;
                Vector3 steadyBasePos;
                if (inMainMenu)
                {
                    steadyBaseYaw = GetSteadyMenuAnchor(out steadyBasePos, immediateYaw: modeChanged || !_hasAlignedInitialMenu);
                }
                else
                {
                    steadyBasePos = _activeBaseCam.transform.position;
                    steadyBaseYaw = _activeBaseCam.transform.eulerAngles.y;
                    _menuReferenceYaw = steadyBaseYaw;
                }

                // Automatically face the Main Menu on initial startup (and when switching to/from Character Customization)
                if (inMainMenu && _frame.headPose.IsValid && (!_hasAlignedInitialMenu || modeChanged))
                {
                    _calibratedHeadLocalPos = _frame.headPose.Position;
                    _calibratedHeadLocalYaw = _frame.headPose.Rotation.eulerAngles.y;
                    _hasCalibratedHead = true;
                    _menuYawOffset = -_calibratedHeadLocalYaw;
                    _hasAlignedInitialMenu = true;
                    _wasInMainMenuState = true;
                    _wasInSkinCustomization = skinCustomizing;
                    VRUIManager.Instance?.RequestMenuRecenter();
                }

                float finalRigYaw = steadyBaseYaw + _menuYawOffset;
                RigWorldRot = Quaternion.Euler(0f, finalRigYaw, 0f);

                // Keep natural HMD height offset relative to steady seat while zeroing horizontal offset on calibration
                Vector3 localHeadOffset = _calibratedHeadLocalPos;
                RigWorldPos = steadyBasePos - (RigWorldRot * localHeadOffset);
            }
        }
        else
        {
            Player player = Player.LocalPlayer;

            // Rotate rig yaw naturally with the boat hull while driving
            bool drivingBoat = Boat.IsDrivingLocally && BoatManager.Boat != null && BoatManager.Boat.VisualBoat != null;
            if (drivingBoat)
            {
                float boatYaw = BoatManager.Boat.VisualBoat.eulerAngles.y;
                if (_wasDrivingBoat)
                {
                    float deltaYaw = Mathf.DeltaAngle(_lastBoatYaw, boatYaw);
                    _rigYaw += deltaYaw;
                }
                _lastBoatYaw = boatYaw;
                _wasDrivingBoat = true;
            }
            else
            {
                _wasDrivingBoat = false;
            }

            RigWorldRot = Quaternion.Euler(0f, _rigYaw, 0f);

            float charEyeHeight = (player.Camera != null && player.Camera.CamHeight > 0.1f) ? player.Camera.CamHeight : 1.55f;
            float targetEyeY = charEyeHeight + VRConfig.SeatedHeightOffset.Value;
            float rigOriginY = player.Transform.position.y + targetEyeY - _calibratedHeadLocalPos.y;

            Vector3 localHeadXZ = new Vector3(_calibratedHeadLocalPos.x, 0f, _calibratedHeadLocalPos.z);
            RigWorldPos = new Vector3(player.Transform.position.x, rigOriginY, player.Transform.position.z) - (RigWorldRot * localHeadXZ);

            if (player.Movement != null)
            {
                baseVel = player.Movement.Velocity;
            }
        }

        // Transform HMD & controllers into world space
        Vector3 localHeadPos = _frame.headPose.IsValid ? _frame.headPose.Position : _calibratedHeadLocalPos;
        Quaternion localHeadRot = _frame.headPose.IsValid ? _frame.headPose.Rotation : Quaternion.identity;
        HeadWorldPos = RigWorldPos + (RigWorldRot * localHeadPos);
        HeadWorldRot = RigWorldRot * localHeadRot;

        UpdateHandWorldPose(_frame.handLeft, isLeft: true, baseVel);
        UpdateHandWorldPose(_frame.handRight, isLeft: false, baseVel);
    }

    private void UpdateHandWorldPose(in ControllerStateData hand, bool isLeft, Vector3 baseVel)
    {
        if (hand.gripPose.IsValid)
        {
            Vector3 gPos = RigWorldPos + (RigWorldRot * hand.gripPose.Position);
            Quaternion gRot = RigWorldRot * hand.gripPose.Rotation;
            Vector3 gVel = baseVel + (RigWorldRot * hand.gripPose.Velocity);
            Vector3 gAngVel = RigWorldRot * hand.gripPose.AngularVelocity;

            if (isLeft)
            {
                LeftGripWorldPos = gPos;
                LeftGripWorldRot = gRot;
                LeftHandVelocity = gVel;
                LeftHandAngularVelocity = gAngVel;
            }
            else
            {
                RightGripWorldPos = gPos;
                RightGripWorldRot = gRot;
                RightHandVelocity = gVel;
                RightHandAngularVelocity = gAngVel;
            }
        }
        else
        {
            // Graceful fallback pose in front of chest if a controller is turned off
            Vector3 fallbackOffset = isLeft ? new Vector3(-0.22f, -0.28f, 0.35f) : new Vector3(0.22f, -0.28f, 0.35f);
            Quaternion yawOnly = Quaternion.Euler(0f, HeadWorldRot.eulerAngles.y, 0f);
            if (isLeft)
            {
                LeftGripWorldPos = HeadWorldPos + yawOnly * fallbackOffset;
                LeftGripWorldRot = yawOnly;
                LeftHandVelocity = baseVel;
                LeftHandAngularVelocity = Vector3.zero;
            }
            else
            {
                RightGripWorldPos = HeadWorldPos + yawOnly * fallbackOffset;
                RightGripWorldRot = yawOnly;
                RightHandVelocity = baseVel;
                RightHandAngularVelocity = Vector3.zero;
            }
        }

        if (hand.aimPose.IsValid)
        {
            Vector3 aPos = RigWorldPos + (RigWorldRot * hand.aimPose.Position);
            Quaternion aRot = RigWorldRot * hand.aimPose.Rotation;
            if (isLeft)
            {
                LeftAimWorldPos = aPos;
                LeftAimWorldRot = aRot;
            }
            else
            {
                RightAimWorldPos = aPos;
                RightAimWorldRot = aRot;
            }
        }
        else
        {
            if (isLeft)
            {
                LeftAimWorldPos = LeftGripWorldPos;
                LeftAimWorldRot = LeftGripWorldRot;
            }
            else
            {
                RightAimWorldPos = RightGripWorldPos;
                RightAimWorldRot = RightGripWorldRot;
            }
        }
    }

    private void SetEyeCamerasEnabled(bool enabled)
    {
        for (int e = 0; e < 2; e++)
        {
            if (_eyeCams[e] != null && _eyeCams[e].enabled != enabled)
            {
                _eyeCams[e].enabled = enabled;
            }
        }
    }

    private void PrepareEyeCamerasForFrame()
    {
        if (_eyeCams[0] == null || _eyeCams[1] == null) return;

        bool canRender = IsVRActive && _frame.ShouldRender && _frame.AreViewsValid;
        if (!canRender)
        {
            SetEyeCamerasEnabled(false);
            return;
        }

        SyncBaseCameraSettingsAndSuppress();
        ApplyEyePoseAndProjection(0, _frame.eyeLeft);
        ApplyEyePoseAndProjection(1, _frame.eyeRight);
        SetEyeCamerasEnabled(true);
    }

    private void ApplyEyePoseAndProjection(int eyeIndex, in EyeViewData view)
    {
        Camera cam = _eyeCams[eyeIndex];
        Vector3 eyeWorldPos = RigWorldPos + (RigWorldRot * view.pose.Position);
        Quaternion eyeWorldRot = RigWorldRot * view.pose.Rotation;

        cam.transform.SetPositionAndRotation(eyeWorldPos, eyeWorldRot);
        cam.targetTexture = _eyeRTs[eyeIndex];
        cam.projectionMatrix = ComputeOffCenterProjection(
            view.angleLeft, view.angleRight, view.angleUp, view.angleDown,
            cam.nearClipPlane, cam.farClipPlane);
    }

    private IEnumerator EndOfFrameRenderLoop()
    {
        var waitEof = new WaitForEndOfFrame();
        while (true)
        {
            yield return waitEof;

            if (!IsVRActive || _eyeCams[0] == null || _eyeCams[1] == null)
            {
                continue;
            }

            if (_frame.ShouldRender && _frame.AreViewsValid)
            {
                // _eyeCams[0] and _eyeCams[1] were rendered natively by Unity's FinishFrameRendering right after PlayerEmitCanvasGeometry!
                // Resolve MSAA + vertical Y-flip from Unity URP BottomLeft to OpenXR D3D11 TopLeft
                Graphics.Blit(_eyeRTs[0], _submitRTs[0], new Vector2(1f, -1f), new Vector2(0f, 1f));
                Graphics.Blit(_eyeRTs[1], _submitRTs[1], new Vector2(1f, -1f), new Vector2(0f, 1f));
                RenderTexture.active = null;
            }

            OpenXRBridge.SubmitStereoFrame(_submitPtrs[0], _submitPtrs[1]);
        }
    }

    private void SyncBaseCameraSettingsAndSuppress()
    {
        if (_activeBaseCam == null) return;

        if (_disabledBaseCam != null && _disabledBaseCam != _activeBaseCam)
        {
            _disabledBaseCam.enabled = true;
            _disabledBaseCam = null;
        }

        // Prevent Unity from rendering the flatscreen camera a 3rd time while keeping its GameObject & AudioListener active
        if (_activeBaseCam.targetTexture == null && _activeBaseCam.enabled)
        {
            _activeBaseCam.enabled = false;
            _disabledBaseCam = _activeBaseCam;
        }

        int uiLayerMask = (1 << 0) | (1 << 5) | (VRUIManager.Instance != null ? VRUIManager.Instance.ConvertedCanvasLayerMask : 0);
        EnsureUrpTransparentUILayer(uiLayerMask);

        var baseUrp = _activeBaseCam.GetComponent<UniversalAdditionalCameraData>();
        if (baseUrp != null && baseUrp.scriptableRenderer != null)
        {
            PatchLiveUniversalRendererMasks(baseUrp.scriptableRenderer, uiLayerMask);
        }

        for (int e = 0; e < 2; e++)
        {
            Camera eyeCam = _eyeCams[e];
            eyeCam.cullingMask = _activeBaseCam.cullingMask | uiLayerMask;
            eyeCam.clearFlags = _activeBaseCam.clearFlags;
            eyeCam.backgroundColor = _activeBaseCam.backgroundColor;
            eyeCam.farClipPlane = Mathf.Max(500f, _activeBaseCam.farClipPlane);
            eyeCam.nearClipPlane = 0.025f;
            eyeCam.allowHDR = _activeBaseCam.allowHDR;
            eyeCam.useOcclusionCulling = false;

            if (baseUrp != null)
            {
                var eyeUrp = eyeCam.GetComponent<UniversalAdditionalCameraData>();
                if (eyeUrp != null)
                {
                    eyeUrp.renderPostProcessing = baseUrp.renderPostProcessing;
                    eyeUrp.volumeLayerMask = baseUrp.volumeLayerMask;
                }
            }
        }
    }

    private void PatchLiveUniversalRendererMasks(ScriptableRenderer sr, int uiLayerMask)
    {
        if (_patchedLiveUrpRenderer || sr == null) return;
        try
        {
            Type srType = sr.GetType();
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (string propName in new[] { "opaqueLayerMask", "transparentLayerMask", "prepassLayerMask" })
            {
                PropertyInfo pi = srType.GetProperty(propName, bf);
                if (pi != null && pi.CanRead && pi.CanWrite && pi.PropertyType == typeof(LayerMask))
                {
                    LayerMask cur = (LayerMask)pi.GetValue(sr);
                    cur.value |= uiLayerMask;
                    pi.SetValue(sr, cur);
                }
            }

            FieldInfo filterField = typeof(UnityEngine.Rendering.Universal.Internal.DrawObjectsPass).GetField("m_FilteringSettings", bf);
            if (filterField != null)
            {
                foreach (string passFieldName in new[]
                {
                    "m_RenderTransparentForwardPass",
                    "m_RenderOpaqueForwardPass",
                    "m_RenderOpaqueForwardOnlyPass",
                    "m_RenderOpaqueForwardWithRenderingLayersPass"
                })
                {
                    FieldInfo pf = srType.GetField(passFieldName, bf);
                    object passObj = pf?.GetValue(sr);
                    if (passObj != null)
                    {
                        FilteringSettings fs = (FilteringSettings)filterField.GetValue(passObj);
                        fs.layerMask |= uiLayerMask;
                        filterField.SetValue(passObj, fs);
                    }
                }
            }

            _patchedLiveUrpRenderer = true;
            Plugin.Log.LogInfo($"Patched live UniversalRenderer DrawObjectsPass layer masks to include UI (mask=0x{uiLayerMask:X}).");
        }
        catch (Exception ex)
        {
            _patchedLiveUrpRenderer = true;
            Plugin.Log.LogWarning($"Could not patch live UniversalRenderer masks: {ex.Message}");
        }
    }

    private void EnsureUrpTransparentUILayer(int uiLayerMask)
    {
        if (_ensuredUrpTransparentUILayer) return;
        try
        {
            var urpAsset = GameInfo.UrpAsset;
            if (urpAsset != null && urpAsset.rendererDataList != null)
            {
                foreach (var rd in urpAsset.rendererDataList)
                {
                    if (rd is UniversalRendererData urd)
                    {
                        if ((urd.transparentLayerMask.value & uiLayerMask) != uiLayerMask)
                        {
                            urd.transparentLayerMask |= uiLayerMask;
                        }
                        if ((urd.opaqueLayerMask.value & uiLayerMask) != uiLayerMask)
                        {
                            urd.opaqueLayerMask |= uiLayerMask;
                        }
                    }
                }
                _ensuredUrpTransparentUILayer = true;
            }
        }
        catch
        {
            // Safe guard during early boot
        }
    }

    private void RestoreBaseCamera()
    {
        SetEyeCamerasEnabled(false);
        if (_disabledBaseCam != null)
        {
            _disabledBaseCam.enabled = true;
            _disabledBaseCam = null;
        }
        if (_savedVSync >= 0)
        {
            QualitySettings.vSyncCount = _savedVSync;
            Application.targetFrameRate = _savedTargetFps;
            _savedVSync = -1;
        }
    }

    public static Matrix4x4 ComputeOffCenterProjection(float angleLeft, float angleRight, float angleUp, float angleDown, float near, float far)
    {
        float left = Mathf.Tan(angleLeft) * near;
        float right = Mathf.Tan(angleRight) * near;
        float top = Mathf.Tan(angleUp) * near;
        float bottom = Mathf.Tan(angleDown) * near;
        return Matrix4x4.Frustum(left, right, bottom, top, near, far);
    }

    private void OnGUI()
    {
        if (!IsVRActive) return;
        if (Event.current.type != EventType.Repaint) return;
        if (VRConfig.DesktopMirror.Value == VRDesktopMirrorMode.Disabled) return;

        int idx = VRConfig.DesktopMirror.Value == VRDesktopMirrorMode.RightEye ? 1 : 0;
        RenderTexture rt = _eyeRTs[idx];
        if (rt != null && rt.IsCreated())
        {
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), rt, ScaleMode.ScaleAndCrop, alphaBlend: false);
            VRDesktopSpectatorHUD.DrawOnDesktopWindow();
        }
    }

    private void CreateMenuControllerVisuals()
    {
        var unlitShader = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        _menuWatchBezelMat = new Material(unlitShader) { color = new Color(0.30f, 0.90f, 1.0f, 0.92f) };

        _menuHandLeftVisual = CreateWandVisual("HTFVR_MenuWandLeft", new Color(0.25f, 0.75f, 1.0f), isLeft: true, out _menuWatchBezelLeft);
        _menuHandRightVisual = CreateWandVisual("HTFVR_MenuWandRight", new Color(1.0f, 0.65f, 0.2f), isLeft: false, out _menuWatchBezelRight);

        // Sleek in-save wrist watch holo-projector worn on the playermodel's menu wrist
        _ingameWatchBezel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _ingameWatchBezel.name = "HTFVR_InGameWristWatchBezel";
        _ingameWatchBezel.transform.SetParent(transform, false);
        _ingameWatchBezel.transform.localScale = new Vector3(0.028f, 0.0028f, 0.028f);
        Destroy(_ingameWatchBezel.GetComponent<Collider>());
        var ingameBezelRenderer = _ingameWatchBezel.GetComponent<MeshRenderer>();
        ingameBezelRenderer.sharedMaterial = _menuWatchBezelMat;
        ingameBezelRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _ingameWatchBezel.SetActive(false);

        SetMenuVisualsActive(false);
    }

    private GameObject CreateWandVisual(string name, Color accent, bool isLeft, out GameObject watchBezel)
    {
        GameObject root = new GameObject(name);
        root.transform.SetParent(transform, false);

        GameObject grip = GameObject.CreatePrimitive(PrimitiveType.Cube);
        grip.transform.SetParent(root.transform, false);
        grip.transform.localScale = new Vector3(0.036f, 0.036f, 0.11f);
        grip.transform.localPosition = new Vector3(0f, -0.01f, -0.03f);
        Destroy(grip.GetComponent<Collider>());

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        var mat = new Material(shader) { color = accent };
        grip.GetComponent<MeshRenderer>().sharedMaterial = mat;

        // Subtle glowing inner-wrist holo-projector bezel showing which wrist holds the Main Menu
        watchBezel = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        watchBezel.name = "HTFVR_WristWatchBezel";
        watchBezel.transform.SetParent(root.transform, false);
        watchBezel.transform.localPosition = new Vector3(isLeft ? 0.022f : -0.022f, -0.018f, -0.065f);
        watchBezel.transform.localRotation = Quaternion.Euler(0f, 0f, isLeft ? -62f : 62f);
        watchBezel.transform.localScale = new Vector3(0.032f, 0.003f, 0.032f);
        Destroy(watchBezel.GetComponent<Collider>());
        var bezelRenderer = watchBezel.GetComponent<MeshRenderer>();
        bezelRenderer.sharedMaterial = _menuWatchBezelMat;
        bezelRenderer.shadowCastingMode = ShadowCastingMode.Off;

        return root;
    }

    private void SetMenuVisualsActive(bool active)
    {
        if (_menuHandLeftVisual != null && _menuHandLeftVisual.activeSelf != active)
            _menuHandLeftVisual.SetActive(active);
        if (_menuHandRightVisual != null && _menuHandRightVisual.activeSelf != active)
            _menuHandRightVisual.SetActive(active);
        if (!active && _ingameWatchBezel != null && _ingameWatchBezel.activeSelf)
            _ingameWatchBezel.SetActive(false);
    }

    private void UpdateMenuControllerVisuals()
    {
        bool showWands = IsVRActive && (MainMenuManager.IsInMenu || !Player.LocalPlayer);
        SetMenuVisualsActive(showWands);

        bool leftIsMenuWrist = VRUIManager.Instance != null ? VRUIManager.Instance.IsLeftMenuWrist : IsRightDominant;
        float wristAlpha = VRUIManager.Instance != null ? VRUIManager.Instance.WristMenuAlpha : 0f;

        if (showWands)
        {
            _menuHandLeftVisual.transform.SetPositionAndRotation(LeftGripWorldPos, LeftGripWorldRot);
            _menuHandRightVisual.transform.SetPositionAndRotation(RightGripWorldPos, RightGripWorldRot);

            bool showMenuBezel = MainMenuManager.IsInMenu && !Player.LocalPlayer && !LocalSkin.IsInSkinCustomization;
            if (_menuWatchBezelLeft != null && _menuWatchBezelLeft.activeSelf != (showMenuBezel && leftIsMenuWrist))
                _menuWatchBezelLeft.SetActive(showMenuBezel && leftIsMenuWrist);
            if (_menuWatchBezelRight != null && _menuWatchBezelRight.activeSelf != (showMenuBezel && !leftIsMenuWrist))
                _menuWatchBezelRight.SetActive(showMenuBezel && !leftIsMenuWrist);
            if (_ingameWatchBezel != null && _ingameWatchBezel.activeSelf)
                _ingameWatchBezel.SetActive(false);

            if (_menuWatchBezelMat != null && showMenuBezel)
            {
                float pulse = 0.72f + 0.28f * Mathf.Sin(Time.unscaledTime * 3.2f);
                _menuWatchBezelMat.color = Color.Lerp(
                    new Color(0.28f, 0.88f, 1.0f, pulse),
                    new Color(1.0f, 0.92f, 0.45f, 0.98f),
                    wristAlpha);
            }
            return;
        }

        // In-save: place sleek holographic wristwatch bezel on the local playermodel's menu wrist!
        Player lp = Player.LocalPlayer;
        bool handActive = leftIsMenuWrist ? _frame.handLeft.IsActive : _frame.handRight.IsActive;
        bool showInGameWatch = IsVRActive && lp != null && !MainMenuManager.IsInMenu &&
                               handActive && !(lp.Dying != null && lp.Dying.IsDead);

        if (_ingameWatchBezel != null && _ingameWatchBezel.activeSelf != showInGameWatch)
        {
            _ingameWatchBezel.SetActive(showInGameWatch);
        }

        if (showInGameWatch && _ingameWatchBezel != null)
        {
            Vector3 wPos = leftIsMenuWrist ? LeftGripWorldPos : RightGripWorldPos;
            Quaternion wRot = leftIsMenuWrist ? LeftGripWorldRot : RightGripWorldRot;
            Vector3 localOffset = new Vector3(leftIsMenuWrist ? 0.024f : -0.024f, -0.016f, -0.068f);
            Quaternion localTilt = Quaternion.Euler(0f, 0f, leftIsMenuWrist ? -62f : 62f);
            _ingameWatchBezel.transform.SetPositionAndRotation(wPos + (wRot * localOffset), wRot * localTilt);

            if (_menuWatchBezelMat != null)
            {
                float pulse = 0.65f + 0.30f * Mathf.Sin(Time.unscaledTime * 3.0f);
                _menuWatchBezelMat.color = Color.Lerp(
                    new Color(0.28f, 0.88f, 1.0f, pulse),
                    new Color(1.0f, 0.92f, 0.45f, 0.98f),
                    wristAlpha);
            }
        }
    }

    private void CreateOverlaySphere()
    {
        _overlaySphereObj = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _overlaySphereObj.name = "HTFVR_StereoOverlaySphere";
        _overlaySphereObj.layer = 0;
        _overlaySphereObj.transform.SetParent(transform, false);
        _overlaySphereObj.transform.localScale = Vector3.one * 0.18f;
        Destroy(_overlaySphereObj.GetComponent<Collider>());

        // Invert sphere triangles so inside faces render cleanly around each eye
        MeshFilter mf = _overlaySphereObj.GetComponent<MeshFilter>();
        if (mf != null && mf.mesh != null)
        {
            Mesh mesh = mf.mesh;
            int[] tris = mesh.triangles;
            for (int i = 0; i < tris.Length; i += 3)
            {
                (tris[i], tris[i + 1]) = (tris[i + 1], tris[i]);
            }
            mesh.triangles = tris;
        }

        Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        _overlaySphereMat = new Material(sh)
        {
            renderQueue = 4500,
            color = Color.clear
        };
        if (_overlaySphereMat.HasProperty("unity_GUIZTestMode"))
            _overlaySphereMat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        if (_overlaySphereMat.HasProperty("_ZTest"))
            _overlaySphereMat.SetInt("_ZTest", (int)CompareFunction.Always);

        _overlaySphereRenderer = _overlaySphereObj.GetComponent<MeshRenderer>();
        _overlaySphereRenderer.sharedMaterial = _overlaySphereMat;
        _overlaySphereRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _overlaySphereRenderer.receiveShadows = false;
        _overlaySphereObj.SetActive(false);
    }

    private void UpdateOverlaySphere()
    {
        if (_overlaySphereObj == null || _overlaySphereMat == null) return;

        Color finalColor = Color.clear;

        // Read CanvasManager blackScreen & underwaterImage
        if (CanvasInstanceField != null)
        {
            var cm = CanvasInstanceField.GetValue(null) as CanvasManager;
            if (cm != null)
            {
                var blackImg = BlackScreenField?.GetValue(cm) as Image;
                var waterImg = UnderwaterImgField?.GetValue(cm) as Image;

                if (waterImg != null)
                {
                    // Prevent flat fullscreen Image from obscuring WorldSpace UI or intercepting VR laser clicks
                    waterImg.raycastTarget = false;
                    if (waterImg.enabled) waterImg.enabled = false;

                    if (waterImg.gameObject.activeInHierarchy && waterImg.color.a > 0.01f)
                    {
                        finalColor = new Color(waterImg.color.r, waterImg.color.g, waterImg.color.b, Mathf.Clamp(waterImg.color.a * 0.72f, 0f, 0.85f));
                    }
                }

                if (blackImg != null)
                {
                    blackImg.raycastTarget = false;
                    if (blackImg.enabled) blackImg.enabled = false;

                    if (blackImg.gameObject.activeInHierarchy && blackImg.color.a > 0.005f)
                    {
                        float ba = Mathf.Clamp01(blackImg.color.a);
                        finalColor = Color.Lerp(finalColor, new Color(0f, 0f, 0f, 1f), ba);
                        finalColor.a = Mathf.Max(finalColor.a, ba);
                    }
                }
            }
        }

        // Optional comfort vignette on artificial movement
        if (VRConfig.ComfortVignetteOnMove.Value && Player.LocalPlayer != null && Player.LocalPlayer.Movement != null && finalColor.a < 0.35f)
        {
            float speed = new Vector2(Player.LocalPlayer.Movement.Velocity.x, Player.LocalPlayer.Movement.Velocity.z).magnitude;
            if (speed > 2.5f)
            {
                float vigAlpha = Mathf.Clamp01((speed - 2.5f) / 8f) * 0.28f;
                if (vigAlpha > finalColor.a)
                {
                    finalColor = new Color(0f, 0f, 0f, vigAlpha);
                }
            }
        }

        bool show = finalColor.a > 0.005f;
        if (_overlaySphereObj.activeSelf != show)
        {
            _overlaySphereObj.SetActive(show);
        }
        if (show)
        {
            _overlaySphereMat.color = finalColor;
        }
    }
}

// =============================================================================
// Main Menu Comfort Patches (Slow, Calm Boat & Ocean Pace Before Entering Game)
// =============================================================================
public static class VRMainMenuComfortPatches
{
    private static bool ShouldSlowMainMenu =>
        VRRigManager.Instance != null &&
        VRRigManager.Instance.IsVRActive &&
        MainMenuManager.IsInMenu &&
        !Player.LocalPlayer;

    private static readonly FieldInfo BoatRigField = AccessTools.Field(typeof(SelfDrivingBoat), "_boatRig");
    private static readonly FieldInfo BoatThrottleField = AccessTools.Field(typeof(SelfDrivingBoat), "_throttleAmount");

    [HarmonyPatch(typeof(SelfDrivingBoat))]
    public static class SelfDrivingBoatComfortPatches
    {
        [HarmonyPatch("FixedUpdate")]
        [HarmonyPostfix]
        public static void FixedUpdatePostfix(SelfDrivingBoat __instance)
        {
            if (!ShouldSlowMainMenu || !SelfDrivingBoat.IsDriving) return;

            float pace = Mathf.Clamp(VRConfig.MenuSimulationPace.Value, 0.05f, 1.0f);
            if (pace >= 0.99f) return;

            var rig = BoatRigField?.GetValue(__instance) as Rigidbody;
            if (rig == null) return;

            // Gently cruise at calm slow-motion speed and eliminate stomach-dropping wave bounce/roll!
            Vector3 vel = rig.linearVelocity;
            Vector3 horiz = new Vector3(vel.x, 0f, vel.z);
            float maxCruiseSpeed = 10.5f * pace; // ~2.1 m/s gentle drift at default 0.20x pace
            if (horiz.magnitude > maxCruiseSpeed)
            {
                horiz = Vector3.Lerp(horiz, horiz.normalized * maxCruiseSpeed, 0.35f);
            }
            vel.x = horiz.x;
            vel.z = horiz.z;
            vel.y *= 0.45f; // Damp vertical wave heave
            rig.linearVelocity = vel;

            rig.angularVelocity *= 0.30f;
            Quaternion levelRot = Quaternion.Euler(0f, rig.rotation.eulerAngles.y, 0f);
            rig.MoveRotation(Quaternion.Slerp(rig.rotation, levelRot, 0.35f));
        }

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void UpdatePostfix(SelfDrivingBoat __instance)
        {
            if (!ShouldSlowMainMenu || !SelfDrivingBoat.IsDriving || BoatThrottleField == null) return;

            float pace = Mathf.Clamp(VRConfig.MenuSimulationPace.Value, 0.05f, 1.0f);
            if (pace >= 0.99f) return;

            // Keep motor audio at a soothing low-throttle idle cruise while on the Main Menu
            float curThrottle = (float)BoatThrottleField.GetValue(__instance);
            float calmMax = Mathf.Clamp(pace * 1.25f, 0.1f, 1f);
            if (curThrottle > calmMax)
            {
                BoatThrottleField.SetValue(__instance, calmMax);
            }
        }
    }

    private static readonly FieldInfo WaterMatField = AccessTools.Field(typeof(WaterManager), "_waterMat");
    private static readonly FieldInfo WaveOffsetStaticField = AccessTools.Field(typeof(WaterManager), "_waveOffset");
    private static readonly int FakeTimeShaderId = Shader.PropertyToID("_Fake_Time");
    private static readonly int WaveOffsetShaderId = Shader.PropertyToID("_Wave_Offset");

    private static float _vrMenuWaterClock;
    private static bool _wasSlowingWater;

    [HarmonyPatch(typeof(WaterManager), "LateUpdate")]
    public static class WaterManagerComfortPatch
    {
        public static void Postfix(WaterManager __instance)
        {
            var mat = WaterMatField?.GetValue(__instance) as Material;
            if (mat == null) return;

            if (ShouldSlowMainMenu)
            {
                float pace = Mathf.Clamp(VRConfig.MenuSimulationPace.Value, 0.05f, 1.0f);
                if (!_wasSlowingWater)
                {
                    _vrMenuWaterClock = Time.time;
                    _wasSlowingWater = true;
                }

                _vrMenuWaterClock += Time.deltaTime * pace;

                // Keep both shader waves (_Fake_Time + _Wave_Offset) and C# GerstnerWave (Time.time + _waveOffset) at _vrMenuWaterClock!
                mat.SetFloat(FakeTimeShaderId, _vrMenuWaterClock);
                mat.SetFloat(WaveOffsetShaderId, 0f);
                WaveOffsetStaticField?.SetValue(null, _vrMenuWaterClock - Time.time);
            }
            else if (_wasSlowingWater)
            {
                _wasSlowingWater = false;
                mat.SetFloat(FakeTimeShaderId, Time.time);
                mat.SetFloat(WaveOffsetShaderId, 0f);
                WaveOffsetStaticField?.SetValue(null, 0f);
            }
        }
    }
}
