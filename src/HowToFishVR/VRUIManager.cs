using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Steamworks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace HowToFishVR;

[DefaultExecutionOrder(10010)]
public class VRUIManager : MonoBehaviour
{
    public static VRUIManager Instance { get; private set; }

    public bool IsLaserActiveOnUI { get; private set; }
    public bool IsConsumingRightStick { get; private set; }
    public int ConvertedCanvasLayerMask { get; private set; } = (1 << 0) | (1 << 5);
    public bool IsLeftMenuWrist { get; private set; } = true;
    public float WristMenuAlpha { get; private set; }

    // Reflection into CanvasManager to extract _howToFishTitle, _versionText, and CanvasGroups cleanly
    private static readonly FieldInfo CanvasInstanceField = AccessTools.Field(typeof(CanvasManager), "_instance");
    private static readonly FieldInfo HowToFishTitleField = AccessTools.Field(typeof(CanvasManager), "_howToFishTitle");
    private static readonly FieldInfo MainMenuGroupField = AccessTools.Field(typeof(CanvasManager), "_mainMenuCanvas");
    private static readonly FieldInfo PermaGroupField = AccessTools.Field(typeof(CanvasManager), "_permaCanvas");
    private static readonly FieldInfo CanvasVersionTextField = AccessTools.Field(typeof(CanvasManager), "_versionText");

    private class TrackedCanvas
    {
        public Canvas Canvas;
        public RectTransform Rect;
        public CanvasScaler Scaler;
        public CanvasGroup Group;
        public bool AddedCanvasGroup;
        public RenderMode OrigRenderMode;
        public Vector3 OrigLocalScale;
        public Vector2 OrigSizeDelta;
        public Vector2 OrigAnchorMin;
        public Vector2 OrigAnchorMax;
        public Vector2 OrigPivot;
        public Vector2 VirtualSize;
        public bool Converted;
    }

    private readonly List<TrackedCanvas> _trackedCanvases = new List<TrackedCanvas>();
    private readonly Dictionary<GameObject, int> _savedObjectLayers = new Dictionary<GameObject, int>();
    private readonly Dictionary<Graphic, Material> _savedGraphicMaterials = new Dictionary<Graphic, Material>();
    private readonly Dictionary<TMP_Text, Material> _savedTmpMaterials = new Dictionary<TMP_Text, Material>();
    private readonly Dictionary<(Material, int), Material> _uiOverlayMatCache = new Dictionary<(Material, int), Material>();
    private readonly Dictionary<(Material, int), Material> _tmpOverlayMatCache = new Dictionary<(Material, int), Material>();
    private readonly HashSet<Material> _createdOverlayMats = new HashSet<Material>();
    private readonly List<Graphic> _tempGraphics = new List<Graphic>(128);
    private readonly List<TMP_Text> _tempTmpTexts = new List<TMP_Text>(64);
    private readonly List<TMP_SubMeshUI> _tempTmpSubMeshes = new List<TMP_SubMeshUI>(32);

    private float _nextCanvasScanTime;

    private Camera _uiEventCamera;
    private LineRenderer _laserLine;
    private GameObject _laserDot;
    private bool _laserUseRightHand = true;

    // Shared ZTest-Always materials for custom VR canvases
    private Material _worldTitleGraphicMat;
    private Material _wristHoloPadGraphicMat;
    private Material _overlayUiDefaultMat;

    // 3D In-World Main Menu Game Title + Sub-text ("Look at your wrist...")
    private Canvas _worldTitleCanvas;
    private RectTransform _worldTitleRootRect;
    private RectTransform _worldTitleCloneParent;
    private GameObject _worldTitleClonedObj;
    private GameObject _origHowToFishTitleObj;
    private Vector3 _origHowToFishTitleScale = Vector3.one;
    private bool _origTitleScaleCaptured;
    private bool _origTitleHiddenForWrist;
    private TextMeshProUGUI _worldTitleFallbackText;
    private TextMeshProUGUI _worldSubtitleText;
    private TextMeshProUGUI _worldVersionBadgeText;

    // Translucent Wrist Holo-Pad backing behind Wrist Main Menu & In-Save Wrist Menu
    private Canvas _wristHoloPadCanvas;
    private CanvasGroup _wristHoloPadGroup;
    private RectTransform _wristHoloPadRect;
    private GameObject _mainMenuReloadToggleObj;
    private Image _mainMenuReloadToggleFill;
    private Image _mainMenuReloadToggleBorder;
    private TextMeshProUGUI _mainMenuReloadToggleText;
    private GameObject _mainMenuWristVersionObj;
    private TextMeshProUGUI _mainMenuWristVersionText;

    // Clickable VR Findings / Collection Navigation & Close Bar (prevents ever getting stuck in PlayerThinking)
    private Canvas _thinkingNavCanvas;
    private RectTransform _thinkingNavRect;
    private readonly List<WristButtonVisual> _thinkingNavButtons = new List<WristButtonVisual>(4);

    // Dedicated In-Save Wrist Menu (fades in when playing in a save & turning/looking at left wrist)
    private struct WristButtonVisual
    {
        public GameObject RootObj;
        public RectTransform Rect;
        public Image Fill;
        public Image Border;
        public TextMeshProUGUI Label;
    }

    private Canvas _inGameWristCanvas;
    private CanvasGroup _inGameWristGroup;
    private RectTransform _inGameWristRect;
    private TextMeshProUGUI _inGameWristTitleText;
    private TextMeshProUGUI _inGameWristStatusText;
    private WristButtonVisual _btnWristPause;
    private WristButtonVisual _btnWristJournal;
    private WristButtonVisual _btnWristReloadMode;
    private WristButtonVisual _btnWristDropItem;
    private WristButtonVisual _btnWristHolster;
    private WristButtonVisual _btnWristTurnMode;
    private WristButtonVisual _btnWristRecenter;
    private readonly List<WristButtonVisual> _inGameWristButtons = new List<WristButtonVisual>(8);

    // Wrist Menu state & smooth pose
    private bool _isWristMenuOpen;
    private bool _wasInWristMainMenu;
    private bool _wasInSaveWristMode;
    private bool _wristPoseInitialized;
    private Vector3 _smoothedWristMenuPos;
    private Quaternion _smoothedWristMenuRot = Quaternion.identity;

    // Modal Menu anchor state (zero world-translation lag!)
    private bool _wasInIngameModal;
    private float _ingameModalWorldYaw;
    private float _menuWorldYaw;
    private Vector3 _menuAnchorPos;
    private Quaternion _menuAnchorRot = Quaternion.identity;

    // Floating "Reset Position" icon button when facing away from the Main Menu / Modal Menu
    private Canvas _resetPromptCanvas;
    private CanvasGroup _resetPromptGroup;
    private RectTransform _resetPromptRootRect;
    private RectTransform _resetButtonVisualRect;
    private GameObject _resetButtonObj;
    private Image _resetRingImg;
    private Image _resetBgImg;
    private TextMeshProUGUI _resetLabelText;
    private TMP_FontAsset _cachedGameFont;
    private bool _showResetPrompt;
    private bool _resetPromptPoseInit;
    private Vector3 _resetPromptHeadOffset = new Vector3(0f, -0.14f, 1.08f);

    // Smooth Gameplay HUD follow state
    private Vector3 _hudSmoothedOffset = new Vector3(0f, 0f, 1.35f);
    private Quaternion _hudSmoothedRot = Quaternion.identity;
    private bool _hudInitialized;

    // EventSystem pointer state
    private PointerEventData _pointerData;
    private readonly List<RaycastResult> _raycastResults = new List<RaycastResult>();
    private GameObject _currentHoverObj;
    private GameObject _currentPressObj;
    private GameObject _currentDragObj;
    private bool _wasLaserTriggerPressed;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        ConvertedCanvasLayerMask = (1 << 0) | (1 << 5);

        InitializeOverlayMaterials();
        CreateUIEventCameraAndLaser();
        CreateWorldMenuTitleWidget();
        CreateWristHoloPadWidget();
        CreateInGameWristMenuWidget();
        CreateThinkingNavWidget();
        CreateResetPositionWidget();
    }

    private void OnDestroy()
    {
        RestoreOriginalTitleIfHidden();
    }

    private void InitializeOverlayMaterials()
    {
        Material defaultUiMat = Canvas.GetDefaultCanvasMaterial();
        if (defaultUiMat != null)
        {
            _worldTitleGraphicMat = new Material(defaultUiMat)
            {
                name = "HTFVR_WorldTitleUIMat",
                renderQueue = 4050
            };
            if (_worldTitleGraphicMat.HasProperty("unity_GUIZTestMode"))
                _worldTitleGraphicMat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            _wristHoloPadGraphicMat = new Material(defaultUiMat)
            {
                name = "HTFVR_WristHoloPadUIMat",
                renderQueue = 4080
            };
            if (_wristHoloPadGraphicMat.HasProperty("unity_GUIZTestMode"))
                _wristHoloPadGraphicMat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            _overlayUiDefaultMat = new Material(defaultUiMat)
            {
                name = "HTFVR_OverlayUIDefaultMat",
                renderQueue = 4100
            };
            if (_overlayUiDefaultMat.HasProperty("unity_GUIZTestMode"))
                _overlayUiDefaultMat.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            _createdOverlayMats.Add(_worldTitleGraphicMat);
            _createdOverlayMats.Add(_wristHoloPadGraphicMat);
            _createdOverlayMats.Add(_overlayUiDefaultMat);
        }
    }

    private Material GetOrCreateOverlayGraphicMaterial(Material srcMat, int renderQueue = 4100)
    {
        if (srcMat == null) return renderQueue == 4050 ? _worldTitleGraphicMat : _overlayUiDefaultMat;
        if (_createdOverlayMats.Contains(srcMat)) return srcMat;

        if (_uiOverlayMatCache.TryGetValue((srcMat, renderQueue), out Material cached) && cached != null)
            return cached;

        if (!srcMat.HasProperty("unity_GUIZTestMode"))
            return srcMat;

        Material clone = new Material(srcMat)
        {
            name = srcMat.name + $"_HTFVROverlay_{renderQueue}",
            renderQueue = renderQueue
        };
        clone.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        _uiOverlayMatCache[(srcMat, renderQueue)] = clone;
        _createdOverlayMats.Add(clone);
        return clone;
    }

    private Material GetOrCreateOverlayTmpMaterial(Material srcMat, int renderQueue = 4100)
    {
        if (srcMat == null) return null;
        if (_createdOverlayMats.Contains(srcMat)) return srcMat;

        if (_tmpOverlayMatCache.TryGetValue((srcMat, renderQueue), out Material cached) && cached != null)
            return cached;

        Material clone = new Material(srcMat)
        {
            name = srcMat.name + $"_HTFVROverlay_{renderQueue}",
            renderQueue = renderQueue
        };
        if (clone.HasProperty("unity_GUIZTestMode"))
        {
            clone.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        }
        _tmpOverlayMatCache[(srcMat, renderQueue)] = clone;
        _createdOverlayMats.Add(clone);
        return clone;
    }

    private void CreateUIEventCameraAndLaser()
    {
        GameObject camGo = new GameObject("HTFVR_UIEventCamera");
        camGo.transform.SetParent(transform, false);
        _uiEventCamera = camGo.AddComponent<Camera>();
        // Disabled so URP never logs missing-depth RenderGraph warnings; _eyeCams[0..1] render natively in FinishFrameRendering!
        // GraphicRaycaster only needs a Camera reference for ScreenPointToRay along _uiEventCamera.transform.forward.
        _uiEventCamera.enabled = false;
        _uiEventCamera.targetTexture = null;
        _uiEventCamera.clearFlags = CameraClearFlags.Nothing;
        _uiEventCamera.cullingMask = ~0;
        _uiEventCamera.nearClipPlane = 0.005f;
        _uiEventCamera.farClipPlane = 20f;
        _uiEventCamera.fieldOfView = 60f;

        Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");

        GameObject lineGo = new GameObject("HTFVR_LaserLine");
        lineGo.layer = 0;
        lineGo.transform.SetParent(transform, false);
        _laserLine = lineGo.AddComponent<LineRenderer>();
        _laserLine.positionCount = 2;
        _laserLine.startWidth = 0.0040f;
        _laserLine.endWidth = 0.0018f;
        _laserLine.useWorldSpace = true;
        _laserLine.shadowCastingMode = ShadowCastingMode.Off;
        _laserLine.receiveShadows = false;
        _laserLine.material = new Material(sh) { color = new Color(0.35f, 0.88f, 1.0f, 0.88f), renderQueue = 4200 };
        _laserLine.enabled = false;

        _laserDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _laserDot.name = "HTFVR_LaserDot";
        _laserDot.layer = 0;
        _laserDot.transform.SetParent(transform, false);
        _laserDot.transform.localScale = Vector3.one * 0.011f;
        Destroy(_laserDot.GetComponent<Collider>());
        var dotRenderer = _laserDot.GetComponent<MeshRenderer>();
        dotRenderer.sharedMaterial = new Material(sh) { color = new Color(1f, 0.95f, 0.4f, 0.98f), renderQueue = 4201 };
        dotRenderer.shadowCastingMode = ShadowCastingMode.Off;
        dotRenderer.receiveShadows = false;
        _laserDot.SetActive(false);
    }

    private void CreateWorldMenuTitleWidget()
    {
        CreateRoundedRectSprites(
            256, 72, cornerRadius: 30f, borderThickness: 3.0f,
            out Sprite pillFillSprite, out Sprite pillBorderSprite);

        GameObject canvasObj = new GameObject("HTFVR_WorldTitleCanvas");
        canvasObj.layer = 0;
        canvasObj.transform.SetParent(transform, false);

        _worldTitleCanvas = canvasObj.AddComponent<Canvas>();
        _worldTitleCanvas.renderMode = RenderMode.WorldSpace;
        _worldTitleCanvas.worldCamera = _uiEventCamera;
        _worldTitleCanvas.sortingOrder = -3;
        _worldTitleCanvas.additionalShaderChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        _worldTitleRootRect = canvasObj.GetComponent<RectTransform>();
        _worldTitleRootRect.anchorMin = new Vector2(0.5f, 0.5f);
        _worldTitleRootRect.anchorMax = new Vector2(0.5f, 0.5f);
        _worldTitleRootRect.pivot = new Vector2(0.5f, 0.5f);
        _worldTitleRootRect.sizeDelta = new Vector2(1600f, 880f);
        _worldTitleRootRect.localScale = Vector3.one * 0.00145f;

        // Container for the cloned official _howToFishTitle (900x550 visual bounds -> spans y=[-120, +430] at y=+155, zero overlap with subtitle!)
        GameObject titleHolder = new GameObject("TitleHolder");
        titleHolder.layer = 0;
        titleHolder.transform.SetParent(canvasObj.transform, false);
        _worldTitleCloneParent = titleHolder.AddComponent<RectTransform>();
        _worldTitleCloneParent.anchorMin = new Vector2(0.5f, 0.5f);
        _worldTitleCloneParent.anchorMax = new Vector2(0.5f, 0.5f);
        _worldTitleCloneParent.pivot = new Vector2(0.5f, 0.5f);
        _worldTitleCloneParent.anchoredPosition = new Vector2(0f, 155f);
        _worldTitleCloneParent.sizeDelta = new Vector2(1400f, 340f);

        GameObject fallbackTitleObj = new GameObject("FallbackTitleText");
        fallbackTitleObj.layer = 0;
        fallbackTitleObj.transform.SetParent(titleHolder.transform, false);
        RectTransform fbRect = fallbackTitleObj.AddComponent<RectTransform>();
        fbRect.anchorMin = Vector2.zero;
        fbRect.anchorMax = Vector2.one;
        fbRect.offsetMin = Vector2.zero;
        fbRect.offsetMax = Vector2.zero;
        _worldTitleFallbackText = fallbackTitleObj.AddComponent<TextMeshProUGUI>();
        _worldTitleFallbackText.text = "HOW TO FISH";
        _worldTitleFallbackText.fontSize = 116f;
        _worldTitleFallbackText.fontStyle = FontStyles.Bold;
        _worldTitleFallbackText.alignment = TextAlignmentOptions.Center;
        _worldTitleFallbackText.color = Color.white;
        _worldTitleFallbackText.raycastTarget = false;

        // Sleek rounded pill banner positioned cleanly below the 550px-tall Game Title artwork (y=-218, spans [-264, -172])
        GameObject subPillObj = new GameObject("SubtitlePill");
        subPillObj.layer = 0;
        subPillObj.transform.SetParent(canvasObj.transform, false);
        RectTransform subPillRect = subPillObj.AddComponent<RectTransform>();
        subPillRect.anchorMin = new Vector2(0.5f, 0.5f);
        subPillRect.anchorMax = new Vector2(0.5f, 0.5f);
        subPillRect.pivot = new Vector2(0.5f, 0.5f);
        subPillRect.anchoredPosition = new Vector2(0f, -218f);
        subPillRect.sizeDelta = new Vector2(1180f, 92f);

        Image subPillFill = subPillObj.AddComponent<Image>();
        subPillFill.sprite = pillFillSprite;
        subPillFill.color = new Color(0.04f, 0.09f, 0.16f, 0.88f);
        subPillFill.material = _worldTitleGraphicMat;
        subPillFill.raycastTarget = false;

        GameObject subBorderObj = new GameObject("SubtitleBorder");
        subBorderObj.layer = 0;
        subBorderObj.transform.SetParent(subPillObj.transform, false);
        RectTransform subBorderRect = subBorderObj.AddComponent<RectTransform>();
        subBorderRect.anchorMin = Vector2.zero;
        subBorderRect.anchorMax = Vector2.one;
        subBorderRect.offsetMin = Vector2.zero;
        subBorderRect.offsetMax = Vector2.zero;
        Image subPillBorder = subBorderObj.AddComponent<Image>();
        subPillBorder.sprite = pillBorderSprite;
        subPillBorder.color = new Color(0.38f, 0.88f, 1.0f, 0.76f);
        subPillBorder.material = _worldTitleGraphicMat;
        subPillBorder.raycastTarget = false;

        GameObject subTxtObj = new GameObject("SubtitleText");
        subTxtObj.layer = 0;
        subTxtObj.transform.SetParent(subPillObj.transform, false);
        RectTransform subTxtRect = subTxtObj.AddComponent<RectTransform>();
        subTxtRect.anchorMin = Vector2.zero;
        subTxtObj.GetComponent<RectTransform>().anchorMax = Vector2.one;
        subTxtRect.offsetMin = new Vector2(28f, 8f);
        subTxtRect.offsetMax = new Vector2(-28f, -8f);
        _worldSubtitleText = subTxtObj.AddComponent<TextMeshProUGUI>();
        _worldSubtitleText.text = "Turn your <color=#62E3FF>left wrist</color> around & look at it for the Menu";
        _worldSubtitleText.fontSize = 34f;
        _worldSubtitleText.richText = true;
        _worldSubtitleText.alignment = TextAlignmentOptions.Center;
        _worldSubtitleText.color = new Color(0.95f, 0.98f, 1.0f, 0.98f);
        _worldSubtitleText.raycastTarget = false;

        // Imprinted VR Mod Version Badge beneath the subtitle on the Main Menu!
        GameObject verPillObj = new GameObject("VRModVersionBadge");
        verPillObj.layer = 0;
        verPillObj.transform.SetParent(canvasObj.transform, false);
        RectTransform verPillRect = verPillObj.AddComponent<RectTransform>();
        verPillRect.anchorMin = new Vector2(0.5f, 0.5f);
        verPillRect.anchorMax = new Vector2(0.5f, 0.5f);
        verPillRect.pivot = new Vector2(0.5f, 0.5f);
        verPillRect.anchoredPosition = new Vector2(0f, -304f);
        verPillRect.sizeDelta = new Vector2(540f, 52f);

        Image verPillFill = verPillObj.AddComponent<Image>();
        verPillFill.sprite = pillFillSprite;
        verPillFill.color = new Color(0.03f, 0.08f, 0.14f, 0.78f);
        verPillFill.material = _worldTitleGraphicMat;
        verPillFill.raycastTarget = false;

        GameObject verBorderObj = new GameObject("VersionBorder");
        verBorderObj.layer = 0;
        verBorderObj.transform.SetParent(verPillObj.transform, false);
        RectTransform verBorderRect = verBorderObj.AddComponent<RectTransform>();
        verBorderRect.anchorMin = Vector2.zero;
        verBorderRect.anchorMax = Vector2.one;
        verBorderRect.offsetMin = Vector2.zero;
        verBorderRect.offsetMax = Vector2.zero;
        Image verBorderImg = verBorderObj.AddComponent<Image>();
        verBorderImg.sprite = pillBorderSprite;
        verBorderImg.color = new Color(0.38f, 0.88f, 1.0f, 0.55f);
        verBorderImg.material = _worldTitleGraphicMat;
        verBorderImg.raycastTarget = false;

        GameObject verTxtObj = new GameObject("VersionText");
        verTxtObj.layer = 0;
        verTxtObj.transform.SetParent(verPillObj.transform, false);
        RectTransform verTxtRect = verTxtObj.AddComponent<RectTransform>();
        verTxtRect.anchorMin = Vector2.zero;
        verTxtRect.anchorMax = Vector2.one;
        verTxtRect.offsetMin = new Vector2(16f, 4f);
        verTxtRect.offsetMax = new Vector2(-16f, -4f);
        _worldVersionBadgeText = verTxtObj.AddComponent<TextMeshProUGUI>();
        _worldVersionBadgeText.text = $"HOW TO FISH VR MOD  \u2022  <color=#62E3FF>v{Plugin.PluginVersion}</color>";
        _worldVersionBadgeText.fontSize = 24f;
        _worldVersionBadgeText.fontStyle = FontStyles.Bold;
        _worldVersionBadgeText.richText = true;
        _worldVersionBadgeText.alignment = TextAlignmentOptions.Center;
        _worldVersionBadgeText.color = new Color(0.88f, 0.95f, 1.0f, 0.96f);
        _worldVersionBadgeText.raycastTarget = false;

        canvasObj.SetActive(false);
    }

    private void CreateWristHoloPadWidget()
    {
        CreateRoundedRectSprites(
            320, 192, cornerRadius: 20f, borderThickness: 3.5f,
            out Sprite padFillSprite, out Sprite padBorderSprite);

        GameObject canvasObj = new GameObject("HTFVR_WristHoloPadCanvas");
        canvasObj.layer = 0;
        canvasObj.transform.SetParent(transform, false);

        _wristHoloPadCanvas = canvasObj.AddComponent<Canvas>();
        _wristHoloPadCanvas.renderMode = RenderMode.WorldSpace;
        _wristHoloPadCanvas.worldCamera = _uiEventCamera;
        _wristHoloPadCanvas.sortingOrder = -5;

        _wristHoloPadGroup = canvasObj.AddComponent<CanvasGroup>();
        _wristHoloPadGroup.alpha = 0f;
        _wristHoloPadGroup.interactable = true;
        _wristHoloPadGroup.blocksRaycasts = true;

        var padRaycaster = canvasObj.AddComponent<GraphicRaycaster>();
        padRaycaster.ignoreReversedGraphics = true;
        padRaycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;

        _wristHoloPadRect = canvasObj.GetComponent<RectTransform>();
        _wristHoloPadRect.anchorMin = new Vector2(0.5f, 0.5f);
        _wristHoloPadRect.anchorMax = new Vector2(0.5f, 0.5f);
        _wristHoloPadRect.pivot = new Vector2(0.5f, 0.5f);
        _wristHoloPadRect.sizeDelta = new Vector2(2040f, 1180f);
        _wristHoloPadRect.localScale = Vector3.one * 0.00024f;

        GameObject fillObj = new GameObject("HoloPadFill");
        fillObj.layer = 0;
        fillObj.transform.SetParent(canvasObj.transform, false);
        RectTransform fillRect = fillObj.AddComponent<RectTransform>();
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;
        Image fillImg = fillObj.AddComponent<Image>();
        fillImg.sprite = padFillSprite;
        fillImg.color = new Color(0.03f, 0.08f, 0.14f, 0.72f);
        fillImg.material = _wristHoloPadGraphicMat;
        fillImg.raycastTarget = false;

        GameObject borderObj = new GameObject("HoloPadBorder");
        borderObj.layer = 0;
        borderObj.transform.SetParent(canvasObj.transform, false);
        RectTransform borderRect = borderObj.AddComponent<RectTransform>();
        borderRect.anchorMin = Vector2.zero;
        borderRect.anchorMax = Vector2.one;
        borderRect.offsetMin = Vector2.zero;
        borderRect.offsetMax = Vector2.zero;
        Image borderImg = borderObj.AddComponent<Image>();
        borderImg.sprite = padBorderSprite;
        borderImg.color = new Color(0.35f, 0.90f, 1.0f, 0.85f);
        borderImg.material = _wristHoloPadGraphicMat;
        borderImg.raycastTarget = false;

        // Compact quick-toggle pill along bottom edge of the Main Menu Wrist Holo-Pad for Manual vs Auto Reload!
        CreateRoundedRectSprites(
            256, 64, cornerRadius: 22f, borderThickness: 3.0f,
            out Sprite pillFill, out Sprite pillBorder);

        _mainMenuReloadToggleObj = new GameObject("MainMenuReloadModeToggle");
        _mainMenuReloadToggleObj.layer = 0;
        _mainMenuReloadToggleObj.transform.SetParent(canvasObj.transform, false);
        RectTransform toggleRt = _mainMenuReloadToggleObj.AddComponent<RectTransform>();
        toggleRt.anchorMin = new Vector2(0.5f, 0f);
        toggleRt.anchorMax = new Vector2(0.5f, 0f);
        toggleRt.pivot = new Vector2(0.5f, 0.5f);
        toggleRt.anchoredPosition = new Vector2(0f, -58f);
        toggleRt.sizeDelta = new Vector2(820f, 82f);

        _mainMenuReloadToggleFill = _mainMenuReloadToggleObj.AddComponent<Image>();
        _mainMenuReloadToggleFill.sprite = pillFill;
        _mainMenuReloadToggleFill.color = new Color(0.05f, 0.13f, 0.22f, 0.92f);
        _mainMenuReloadToggleFill.material = _overlayUiDefaultMat;
        _mainMenuReloadToggleFill.raycastTarget = true;

        Button toggleBtn = _mainMenuReloadToggleObj.AddComponent<Button>();
        toggleBtn.targetGraphic = _mainMenuReloadToggleFill;
        toggleBtn.transition = Selectable.Transition.None;
        toggleBtn.onClick.AddListener(OnClickToggleWeaponReloadMode);

        GameObject toggleBorderObj = new GameObject("Border");
        toggleBorderObj.layer = 0;
        toggleBorderObj.transform.SetParent(_mainMenuReloadToggleObj.transform, false);
        RectTransform tbRt = toggleBorderObj.AddComponent<RectTransform>();
        tbRt.anchorMin = Vector2.zero;
        tbRt.anchorMax = Vector2.one;
        tbRt.offsetMin = Vector2.zero;
        tbRt.offsetMax = Vector2.zero;
        _mainMenuReloadToggleBorder = toggleBorderObj.AddComponent<Image>();
        _mainMenuReloadToggleBorder.sprite = pillBorder;
        _mainMenuReloadToggleBorder.color = new Color(0.38f, 0.90f, 1.0f, 0.90f);
        _mainMenuReloadToggleBorder.material = _overlayUiDefaultMat;
        _mainMenuReloadToggleBorder.raycastTarget = false;

        GameObject toggleTxtObj = new GameObject("Text");
        toggleTxtObj.layer = 0;
        toggleTxtObj.transform.SetParent(_mainMenuReloadToggleObj.transform, false);
        RectTransform ttRt = toggleTxtObj.AddComponent<RectTransform>();
        ttRt.anchorMin = Vector2.zero;
        ttRt.anchorMax = Vector2.one;
        ttRt.offsetMin = new Vector2(20f, 6f);
        ttRt.offsetMax = new Vector2(-20f, -6f);
        _mainMenuReloadToggleText = toggleTxtObj.AddComponent<TextMeshProUGUI>();
        _mainMenuReloadToggleText.fontSize = 31f;
        _mainMenuReloadToggleText.richText = true;
        _mainMenuReloadToggleText.alignment = TextAlignmentOptions.Center;
        _mainMenuReloadToggleText.color = Color.white;
        _mainMenuReloadToggleText.raycastTarget = false;

        // Imprinted VR Mod Version header stamp along top-right of the Main Menu Wrist Holo-Pad!
        _mainMenuWristVersionObj = new GameObject("MainMenuWristVersionStamp");
        _mainMenuWristVersionObj.layer = 0;
        _mainMenuWristVersionObj.transform.SetParent(canvasObj.transform, false);
        RectTransform wvRt = _mainMenuWristVersionObj.AddComponent<RectTransform>();
        wvRt.anchorMin = new Vector2(1f, 1f);
        wvRt.anchorMax = new Vector2(1f, 1f);
        wvRt.pivot = new Vector2(1f, 1f);
        wvRt.anchoredPosition = new Vector2(-36f, -24f);
        wvRt.sizeDelta = new Vector2(520f, 48f);
        _mainMenuWristVersionText = _mainMenuWristVersionObj.AddComponent<TextMeshProUGUI>();
        _mainMenuWristVersionText.text = $"HOW TO FISH VR  \u2022  <color=#62E3FF>v{Plugin.PluginVersion}</color>";
        _mainMenuWristVersionText.fontSize = 27f;
        _mainMenuWristVersionText.fontStyle = FontStyles.Bold;
        _mainMenuWristVersionText.richText = true;
        _mainMenuWristVersionText.alignment = TextAlignmentOptions.TopRight;
        _mainMenuWristVersionText.color = new Color(0.84f, 0.95f, 1.0f, 0.90f);
        _mainMenuWristVersionText.raycastTarget = false;

        canvasObj.SetActive(false);
    }

    private void CreateInGameWristMenuWidget()
    {
        CreateRoundedRectSprites(
            256, 84, cornerRadius: 18f, borderThickness: 3.0f,
            out Sprite btnFillSprite, out Sprite btnBorderSprite);

        GameObject canvasObj = new GameObject("HTFVR_InGameWristCanvas");
        canvasObj.layer = 0;
        canvasObj.transform.SetParent(transform, false);

        _inGameWristCanvas = canvasObj.AddComponent<Canvas>();
        _inGameWristCanvas.renderMode = RenderMode.WorldSpace;
        _inGameWristCanvas.worldCamera = _uiEventCamera;
        _inGameWristCanvas.sortingOrder = 60;
        _inGameWristCanvas.additionalShaderChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        _inGameWristGroup = canvasObj.AddComponent<CanvasGroup>();
        _inGameWristGroup.alpha = 0f;
        _inGameWristGroup.interactable = false;
        _inGameWristGroup.blocksRaycasts = false;

        var raycaster = canvasObj.AddComponent<GraphicRaycaster>();
        raycaster.ignoreReversedGraphics = true;
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;

        _inGameWristRect = canvasObj.GetComponent<RectTransform>();
        _inGameWristRect.anchorMin = new Vector2(0.5f, 0.5f);
        _inGameWristRect.anchorMax = new Vector2(0.5f, 0.5f);
        _inGameWristRect.pivot = new Vector2(0.5f, 0.5f);
        _inGameWristRect.sizeDelta = new Vector2(920f, 640f);
        _inGameWristRect.localScale = Vector3.one * 0.00028f;

        // Header Title (Left) + Held Item / Weapon Ammo Status (Right)
        GameObject titleObj = new GameObject("WristHeaderTitle");
        titleObj.layer = 0;
        titleObj.transform.SetParent(canvasObj.transform, false);
        RectTransform titleRt = titleObj.AddComponent<RectTransform>();
        titleRt.anchorMin = new Vector2(0.5f, 0.5f);
        titleRt.anchorMax = new Vector2(0.5f, 0.5f);
        titleRt.pivot = new Vector2(0f, 0.5f);
        titleRt.anchoredPosition = new Vector2(-420f, 262f);
        titleRt.sizeDelta = new Vector2(360f, 54f);
        _inGameWristTitleText = titleObj.AddComponent<TextMeshProUGUI>();
        _inGameWristTitleText.text = "<color=#62E3FF>VR WRIST MENU</color>";
        _inGameWristTitleText.fontSize = 28f;
        _inGameWristTitleText.fontStyle = FontStyles.Bold;
        _inGameWristTitleText.richText = true;
        _inGameWristTitleText.alignment = TextAlignmentOptions.MidlineLeft;
        _inGameWristTitleText.color = Color.white;
        _inGameWristTitleText.raycastTarget = false;

        GameObject statusObj = new GameObject("WristHeaderStatus");
        statusObj.layer = 0;
        statusObj.transform.SetParent(canvasObj.transform, false);
        RectTransform statusRt = statusObj.AddComponent<RectTransform>();
        statusRt.anchorMin = new Vector2(0.5f, 0.5f);
        statusRt.anchorMax = new Vector2(0.5f, 0.5f);
        statusRt.pivot = new Vector2(1f, 0.5f);
        statusRt.anchoredPosition = new Vector2(420f, 262f);
        statusRt.sizeDelta = new Vector2(480f, 54f);
        _inGameWristStatusText = statusObj.AddComponent<TextMeshProUGUI>();
        _inGameWristStatusText.text = "EMPTY HANDS";
        _inGameWristStatusText.fontSize = 25f;
        _inGameWristStatusText.richText = true;
        _inGameWristStatusText.alignment = TextAlignmentOptions.MidlineRight;
        _inGameWristStatusText.color = new Color(0.86f, 0.94f, 1.0f, 0.95f);
        _inGameWristStatusText.raycastTarget = false;

        // Divider bar under header
        GameObject divObj = new GameObject("HeaderDivider");
        divObj.layer = 0;
        divObj.transform.SetParent(canvasObj.transform, false);
        RectTransform divRt = divObj.AddComponent<RectTransform>();
        divRt.anchoredPosition = new Vector2(0f, 224f);
        divRt.sizeDelta = new Vector2(840f, 3f);
        Image divImg = divObj.AddComponent<Image>();
        divImg.color = new Color(0.35f, 0.88f, 1.0f, 0.45f);
        divImg.material = _overlayUiDefaultMat;
        divImg.raycastTarget = false;

        _inGameWristButtons.Clear();

        // Row 1: Pause / Options  &  Findings / Journal
        _btnWristPause = CreateWristMenuButton(
            canvasObj.transform, "BtnPauseMenu", new Vector2(-215f, 148f), new Vector2(410f, 102f),
            "Pause / Options", btnFillSprite, btnBorderSprite, OnClickInGameWristPause);
        _btnWristJournal = CreateWristMenuButton(
            canvasObj.transform, "BtnJournal", new Vector2(215f, 148f), new Vector2(410f, 102f),
            "Findings / Journal", btnFillSprite, btnBorderSprite, OnClickInGameWristJournal);

        // Row 2: Full-width Manual vs Automatic VR Combat & Weapon Reload Toggle
        _btnWristReloadMode = CreateWristMenuButton(
            canvasObj.transform, "BtnReloadMode", new Vector2(0f, 24f), new Vector2(840f, 104f),
            "Combat & Reload: MANUAL (VR)", btnFillSprite, btnBorderSprite, OnClickToggleWeaponReloadMode);

        // Row 3: Drop Held Item  &  Holster Hands
        _btnWristDropItem = CreateWristMenuButton(
            canvasObj.transform, "BtnDropItem", new Vector2(-215f, -100f), new Vector2(410f, 100f),
            "Drop Held Item", btnFillSprite, btnBorderSprite, OnClickInGameWristDrop);
        _btnWristHolster = CreateWristMenuButton(
            canvasObj.transform, "BtnHolster", new Vector2(215f, -100f), new Vector2(410f, 100f),
            "Holster Hands", btnFillSprite, btnBorderSprite, OnClickInGameWristHolster);

        // Row 4: Turn Mode  &  Recenter VR
        _btnWristTurnMode = CreateWristMenuButton(
            canvasObj.transform, "BtnTurnMode", new Vector2(-215f, -222f), new Vector2(410f, 96f),
            "Turn: SNAP", btnFillSprite, btnBorderSprite, OnClickInGameWristTurnMode);
        _btnWristRecenter = CreateWristMenuButton(
            canvasObj.transform, "BtnRecenter", new Vector2(215f, -222f), new Vector2(410f, 96f),
            "Recenter VR", btnFillSprite, btnBorderSprite, OnClickInGameWristRecenter);

        RefreshReloadToggleLabels();
        canvasObj.SetActive(false);
    }

    private void CreateThinkingNavWidget()
    {
        CreateRoundedRectSprites(
            256, 76, cornerRadius: 20f, borderThickness: 3.0f,
            out Sprite btnFillSprite, out Sprite btnBorderSprite);

        GameObject canvasObj = new GameObject("HTFVR_ThinkingNavCanvas");
        canvasObj.layer = 0;
        canvasObj.transform.SetParent(transform, false);

        _thinkingNavCanvas = canvasObj.AddComponent<Canvas>();
        _thinkingNavCanvas.renderMode = RenderMode.WorldSpace;
        _thinkingNavCanvas.worldCamera = _uiEventCamera;
        _thinkingNavCanvas.sortingOrder = 85;
        _thinkingNavCanvas.additionalShaderChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        var raycaster = canvasObj.AddComponent<GraphicRaycaster>();
        raycaster.ignoreReversedGraphics = true;
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;

        _thinkingNavRect = canvasObj.GetComponent<RectTransform>();
        _thinkingNavRect.anchorMin = new Vector2(0.5f, 0.5f);
        _thinkingNavRect.anchorMax = new Vector2(0.5f, 0.5f);
        _thinkingNavRect.pivot = new Vector2(0.5f, 0.5f);
        _thinkingNavRect.sizeDelta = new Vector2(1140f, 96f);
        _thinkingNavRect.localScale = Vector3.one * 0.00088f;

        _thinkingNavButtons.Clear();

        _thinkingNavButtons.Add(CreateSimpleOverlayButton(
            canvasObj.transform, "BtnThinkingPrev", new Vector2(-375f, 0f), new Vector2(280f, 80f),
            "\u25C0  Prev Page", btnFillSprite, btnBorderSprite,
            () => { PlayWristUiClickFeedback(); PlayerUI.ScrollThinkingPage(-1); }));

        _thinkingNavButtons.Add(CreateSimpleOverlayButton(
            canvasObj.transform, "BtnThinkingClose", new Vector2(0f, 0f), new Vector2(410f, 84f),
            $"Close Findings  {VRControllerIcons.Tag("vr_b")}", btnFillSprite, btnBorderSprite,
            () => { PlayWristUiClickFeedback(); VRInputManager.TriggerJournalToggle(); }));

        _thinkingNavButtons.Add(CreateSimpleOverlayButton(
            canvasObj.transform, "BtnThinkingNext", new Vector2(375f, 0f), new Vector2(280f, 80f),
            "Next Page  \u25B6", btnFillSprite, btnBorderSprite,
            () => { PlayWristUiClickFeedback(); PlayerUI.ScrollThinkingPage(1); }));

        canvasObj.SetActive(false);
    }

    private WristButtonVisual CreateSimpleOverlayButton(
        Transform parent,
        string name,
        Vector2 anchoredPos,
        Vector2 size,
        string initialLabel,
        Sprite fillSprite,
        Sprite borderSprite,
        UnityEngine.Events.UnityAction onClick)
    {
        GameObject btnObj = new GameObject(name);
        btnObj.layer = 0;
        btnObj.transform.SetParent(parent, false);

        RectTransform rt = btnObj.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        Image fill = btnObj.AddComponent<Image>();
        fill.sprite = fillSprite;
        fill.color = new Color(0.05f, 0.13f, 0.22f, 0.94f);
        fill.material = _overlayUiDefaultMat;
        fill.raycastTarget = true;

        Button btn = btnObj.AddComponent<Button>();
        btn.targetGraphic = fill;
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(onClick);

        GameObject borderObj = new GameObject("Border");
        borderObj.layer = 0;
        borderObj.transform.SetParent(btnObj.transform, false);
        RectTransform brt = borderObj.AddComponent<RectTransform>();
        brt.anchorMin = Vector2.zero;
        brt.anchorMax = Vector2.one;
        brt.offsetMin = Vector2.zero;
        brt.offsetMax = Vector2.zero;
        Image border = borderObj.AddComponent<Image>();
        border.sprite = borderSprite;
        border.color = new Color(0.38f, 0.90f, 1.0f, 0.88f);
        border.material = _overlayUiDefaultMat;
        border.raycastTarget = false;

        GameObject txtObj = new GameObject("Label");
        txtObj.layer = 0;
        txtObj.transform.SetParent(btnObj.transform, false);
        RectTransform trt = txtObj.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(14f, 4f);
        trt.offsetMax = new Vector2(-14f, -4f);
        TextMeshProUGUI label = txtObj.AddComponent<TextMeshProUGUI>();
        label.text = initialLabel;
        label.fontSize = 27f;
        label.richText = true;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;
        VRControllerIcons.EnsureRegisteredOnText(label);

        return new WristButtonVisual
        {
            RootObj = btnObj,
            Rect = rt,
            Fill = fill,
            Border = border,
            Label = label
        };
    }

    private WristButtonVisual CreateWristMenuButton(
        Transform parent,
        string name,
        Vector2 anchoredPos,
        Vector2 size,
        string initialLabel,
        Sprite fillSprite,
        Sprite borderSprite,
        UnityEngine.Events.UnityAction onClick)
    {
        GameObject btnObj = new GameObject(name);
        btnObj.layer = 0;
        btnObj.transform.SetParent(parent, false);

        RectTransform rt = btnObj.AddComponent<RectTransform>();
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = anchoredPos;
        rt.sizeDelta = size;

        Image fill = btnObj.AddComponent<Image>();
        fill.sprite = fillSprite;
        fill.color = new Color(0.06f, 0.14f, 0.24f, 0.90f);
        fill.material = _overlayUiDefaultMat;
        fill.raycastTarget = true;

        Button btn = btnObj.AddComponent<Button>();
        btn.targetGraphic = fill;
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(onClick);

        GameObject borderObj = new GameObject("Border");
        borderObj.layer = 0;
        borderObj.transform.SetParent(btnObj.transform, false);
        RectTransform brt = borderObj.AddComponent<RectTransform>();
        brt.anchorMin = Vector2.zero;
        brt.anchorMax = Vector2.one;
        brt.offsetMin = Vector2.zero;
        brt.offsetMax = Vector2.zero;
        Image border = borderObj.AddComponent<Image>();
        border.sprite = borderSprite;
        border.color = new Color(0.36f, 0.88f, 1.0f, 0.82f);
        border.material = _overlayUiDefaultMat;
        border.raycastTarget = false;

        GameObject txtObj = new GameObject("Label");
        txtObj.layer = 0;
        txtObj.transform.SetParent(btnObj.transform, false);
        RectTransform trt = txtObj.AddComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(16f, 6f);
        trt.offsetMax = new Vector2(-16f, -6f);
        TextMeshProUGUI label = txtObj.AddComponent<TextMeshProUGUI>();
        label.text = initialLabel;
        label.fontSize = 27f;
        label.richText = true;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.raycastTarget = false;

        var vis = new WristButtonVisual
        {
            RootObj = btnObj,
            Rect = rt,
            Fill = fill,
            Border = border,
            Label = label
        };
        _inGameWristButtons.Add(vis);
        return vis;
    }

    private void PlayWristUiClickFeedback()
    {
        try
        {
            AudioManager.PlayGlobalClip("Click", variation: true, 1.05f, 0.05f);
        }
        catch
        {
            // Ignore if AudioManager not ready
        }
        OpenXRBridge.TriggerHaptic(_laserUseRightHand, 0.20f, 0.02f, 175f);
    }

    private void OnClickToggleWeaponReloadMode()
    {
        VRConfig.AutoReloadWeapons.Value = !VRConfig.AutoReloadWeapons.Value;
        VRConfig.AutoReloadWeapons.ConfigFile?.Save();
        RefreshReloadToggleLabels();
        VRControllerIcons.RefreshAllPromptUIs();
        PlayWristUiClickFeedback();
        Plugin.Log.LogInfo($"Toggled VR Weapon Reload mode: AutoReloadWeapons={VRConfig.AutoReloadWeapons.Value}");
    }

    private void OnClickInGameWristPause()
    {
        PlayWristUiClickFeedback();
        _isWristMenuOpen = false;
        WristMenuAlpha = 0f;
        PauseManager.TogglePause(!PauseManager.IsPaused);
    }

    private void OnClickInGameWristJournal()
    {
        PlayWristUiClickFeedback();
        _isWristMenuOpen = false;
        WristMenuAlpha = 0f;
        VRInputManager.TriggerJournalToggle();
    }

    private void OnClickInGameWristDrop()
    {
        PlayWristUiClickFeedback();
        VRInputManager.TriggerDropHeldItemImmediate();
    }

    private void OnClickInGameWristHolster()
    {
        PlayWristUiClickFeedback();
        VRInputManager.SelectInventorySlot(-1);
    }

    private void OnClickInGameWristTurnMode()
    {
        VRConfig.TurnMode.Value = VRConfig.TurnMode.Value == VRTurnMode.Snap ? VRTurnMode.Smooth : VRTurnMode.Snap;
        VRConfig.TurnMode.ConfigFile?.Save();
        RefreshReloadToggleLabels();
        PlayWristUiClickFeedback();
    }

    private void OnClickInGameWristRecenter()
    {
        PlayWristUiClickFeedback();
        VRRigManager.Instance?.Recenter();
    }

    private void RefreshReloadToggleLabels()
    {
        bool autoReload = VRConfig.AutoReloadWeapons.Value;
        string reloadLabel = autoReload
            ? "Combat & Reload: <color=#7CFF9B>AUTOMATIC</color>"
            : "Combat & Reload: <color=#62E3FF>MANUAL (VR)</color>";

        if (_btnWristReloadMode.Label != null && _btnWristReloadMode.Label.text != reloadLabel)
        {
            _btnWristReloadMode.Label.text = reloadLabel;
        }
        if (_mainMenuReloadToggleText != null && _mainMenuReloadToggleText.text != reloadLabel)
        {
            _mainMenuReloadToggleText.text = reloadLabel;
        }

        string turnLabel = VRConfig.TurnMode.Value == VRTurnMode.Snap
            ? $"Turn: <color=#62E3FF>SNAP ({VRConfig.SnapTurnAngle.Value:F0}\u00B0)</color>"
            : "Turn: <color=#FFE875>SMOOTH</color>";
        if (_btnWristTurnMode.Label != null && _btnWristTurnMode.Label.text != turnLabel)
        {
            _btnWristTurnMode.Label.text = turnLabel;
        }
    }

    private void CreateResetPositionWidget()
    {
        CreateResetSprites(out Sprite discSprite, out Sprite ringSprite, out Sprite resetIconSprite);

        GameObject canvasObj = new GameObject("HTFVR_ResetPositionCanvas");
        canvasObj.layer = 0;
        canvasObj.transform.SetParent(transform, false);

        _resetPromptCanvas = canvasObj.AddComponent<Canvas>();
        _resetPromptCanvas.renderMode = RenderMode.WorldSpace;
        _resetPromptCanvas.worldCamera = _uiEventCamera;
        _resetPromptCanvas.sortingOrder = 999;
        _resetPromptCanvas.additionalShaderChannels =
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        _resetPromptGroup = canvasObj.AddComponent<CanvasGroup>();
        _resetPromptGroup.alpha = 0f;
        _resetPromptGroup.interactable = true;
        _resetPromptGroup.blocksRaycasts = true;

        var raycaster = canvasObj.AddComponent<GraphicRaycaster>();
        raycaster.ignoreReversedGraphics = true;
        raycaster.blockingObjects = GraphicRaycaster.BlockingObjects.None;

        _resetPromptRootRect = canvasObj.GetComponent<RectTransform>();
        _resetPromptRootRect.anchorMin = new Vector2(0.5f, 0.5f);
        _resetPromptRootRect.anchorMax = new Vector2(0.5f, 0.5f);
        _resetPromptRootRect.pivot = new Vector2(0.5f, 0.5f);
        _resetPromptRootRect.sizeDelta = new Vector2(280f, 210f);
        _resetPromptRootRect.localScale = Vector3.one * 0.00082f;

        // Circular clickable icon button
        _resetButtonObj = new GameObject("ResetPositionButton");
        _resetButtonObj.layer = 0;
        _resetButtonObj.transform.SetParent(canvasObj.transform, false);
        _resetButtonVisualRect = _resetButtonObj.AddComponent<RectTransform>();
        _resetButtonVisualRect.anchoredPosition = new Vector2(0f, 24f);
        _resetButtonVisualRect.sizeDelta = new Vector2(116f, 116f);

        _resetBgImg = _resetButtonObj.AddComponent<Image>();
        _resetBgImg.sprite = discSprite;
        _resetBgImg.color = new Color(0.06f, 0.10f, 0.16f, 0.90f);
        _resetBgImg.material = _overlayUiDefaultMat;
        _resetBgImg.raycastTarget = true;

        Button btn = _resetButtonObj.AddComponent<Button>();
        btn.targetGraphic = _resetBgImg;
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(SnapPlayerToFaceMenu);

        // Glowing outer ring
        GameObject ringObj = new GameObject("Ring");
        ringObj.layer = 0;
        ringObj.transform.SetParent(_resetButtonObj.transform, false);
        RectTransform ringRect = ringObj.AddComponent<RectTransform>();
        ringRect.anchorMin = Vector2.zero;
        ringRect.anchorMax = Vector2.one;
        ringRect.offsetMin = Vector2.zero;
        ringRect.offsetMax = Vector2.zero;
        _resetRingImg = ringObj.AddComponent<Image>();
        _resetRingImg.sprite = ringSprite;
        _resetRingImg.color = new Color(0.38f, 0.88f, 1.0f, 0.95f);
        _resetRingImg.material = _overlayUiDefaultMat;
        _resetRingImg.raycastTarget = false;

        // Crisp circular-arrow + center-target recenter icon
        GameObject iconObj = new GameObject("Icon");
        iconObj.layer = 0;
        iconObj.transform.SetParent(_resetButtonObj.transform, false);
        RectTransform iconRect = iconObj.AddComponent<RectTransform>();
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(78f, 78f);
        Image iconImg = iconObj.AddComponent<Image>();
        iconImg.sprite = resetIconSprite;
        iconImg.color = Color.white;
        iconImg.material = _overlayUiDefaultMat;
        iconImg.raycastTarget = false;

        // Compact pill background + label below icon
        GameObject pillObj = new GameObject("LabelPill");
        pillObj.layer = 0;
        pillObj.transform.SetParent(canvasObj.transform, false);
        RectTransform pillRect = pillObj.AddComponent<RectTransform>();
        pillRect.anchoredPosition = new Vector2(0f, -64f);
        pillRect.sizeDelta = new Vector2(246f, 40f);
        Image pillImg = pillObj.AddComponent<Image>();
        pillImg.color = new Color(0.06f, 0.10f, 0.16f, 0.85f);
        pillImg.material = _overlayUiDefaultMat;
        pillImg.raycastTarget = false;

        GameObject txtObj = new GameObject("LabelText");
        txtObj.layer = 0;
        txtObj.transform.SetParent(pillObj.transform, false);
        RectTransform txtRect = txtObj.AddComponent<RectTransform>();
        txtRect.anchorMin = Vector2.zero;
        txtRect.anchorMax = Vector2.one;
        txtRect.offsetMin = new Vector2(6f, 2f);
        txtRect.offsetMax = new Vector2(-6f, -2f);
        _resetLabelText = txtObj.AddComponent<TextMeshProUGUI>();
        _resetLabelText.text = "Reset Position";
        _resetLabelText.fontSize = 21f;
        _resetLabelText.alignment = TextAlignmentOptions.Center;
        _resetLabelText.color = Color.white;
        _resetLabelText.raycastTarget = false;

        canvasObj.SetActive(false);
    }

    private static void CreateRoundedRectSprites(
        int width, int height, float cornerRadius, float borderThickness,
        out Sprite fillSprite, out Sprite borderSprite)
    {
        Texture2D fillTex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        Texture2D borderTex = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        Color[] fillPixels = new Color[width * height];
        Color[] borderPixels = new Color[width * height];

        Vector2 halfSize = new Vector2((width - 1) * 0.5f, (height - 1) * 0.5f);
        Vector2 innerHalf = halfSize - new Vector2(cornerRadius + 1.5f, cornerRadius + 1.5f);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int idx = y * width + x;
                Vector2 p = new Vector2(Mathf.Abs(x - halfSize.x), Mathf.Abs(y - halfSize.y));
                Vector2 q = p - innerHalf;
                float dist = Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - cornerRadius;

                float fillAlpha = Mathf.Clamp01(0.5f - dist);
                float borderAlpha = Mathf.Clamp01(0.5f - dist) * Mathf.Clamp01(dist + borderThickness + 0.5f);

                fillPixels[idx] = new Color(1f, 1f, 1f, fillAlpha);
                borderPixels[idx] = new Color(1f, 1f, 1f, borderAlpha);
            }
        }

        fillTex.SetPixels(fillPixels);
        fillTex.Apply(false, true);
        borderTex.SetPixels(borderPixels);
        borderTex.Apply(false, true);

        Rect rect = new Rect(0, 0, width, height);
        Vector2 pivot = new Vector2(0.5f, 0.5f);
        fillSprite = Sprite.Create(fillTex, rect, pivot, 100f);
        borderSprite = Sprite.Create(borderTex, rect, pivot, 100f);
    }

    private static void CreateResetSprites(out Sprite discSprite, out Sprite ringSprite, out Sprite iconSprite)
    {
        const int size = 128;
        Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);

        Texture2D discTex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        Texture2D ringTex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        Texture2D iconTex = new Texture2D(size, size, TextureFormat.RGBA32, mipChain: false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };

        Color[] discPixels = new Color[size * size];
        Color[] ringPixels = new Color[size * size];
        Color[] iconPixels = new Color[size * size];

        const float arcRadius = 38f;
        float tipRad = 42f * Mathf.Deg2Rad;
        float baseRad = 70f * Mathf.Deg2Rad;
        Vector2 triTip = center + new Vector2(Mathf.Cos(tipRad), Mathf.Sin(tipRad)) * arcRadius;
        Vector2 triIn = center + new Vector2(Mathf.Cos(baseRad), Mathf.Sin(baseRad)) * (arcRadius - 14f);
        Vector2 triOut = center + new Vector2(Mathf.Cos(baseRad), Mathf.Sin(baseRad)) * (arcRadius + 14f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int idx = y * size + x;
                Vector2 p = new Vector2(x, y);
                Vector2 d = p - center;
                float r = d.magnitude;

                float discAlpha = Mathf.Clamp01(60.5f - r);
                discPixels[idx] = new Color(1f, 1f, 1f, discAlpha);

                float ringAlpha = Mathf.Clamp01(60.5f - r) * Mathf.Clamp01(r - 54.5f);
                ringPixels[idx] = new Color(1f, 1f, 1f, ringAlpha);

                float centerDot = Mathf.Clamp01(9.0f - r);
                float angleDeg = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
                if (angleDeg < 0f) angleDeg += 360f;
                bool inArcSpan = angleDeg >= 64f || angleDeg <= 6f;
                float arcStroke = inArcSpan ? Mathf.Clamp01(4.2f - Mathf.Abs(r - arcRadius)) : 0f;

                float triDist = SignedDistToTriangle(p, triTip, triIn, triOut);
                float arrowAlpha = Mathf.Clamp01(1.1f - triDist);

                float iconAlpha = Mathf.Max(centerDot, Mathf.Max(arcStroke, arrowAlpha));
                iconPixels[idx] = new Color(1f, 1f, 1f, iconAlpha);
            }
        }

        discTex.SetPixels(discPixels);
        discTex.Apply(false, true);
        ringTex.SetPixels(ringPixels);
        ringTex.Apply(false, true);
        iconTex.SetPixels(iconPixels);
        iconTex.Apply(false, true);

        Rect fullRect = new Rect(0, 0, size, size);
        Vector2 pivot = new Vector2(0.5f, 0.5f);
        discSprite = Sprite.Create(discTex, fullRect, pivot, 100f);
        ringSprite = Sprite.Create(ringTex, fullRect, pivot, 100f);
        iconSprite = Sprite.Create(iconTex, fullRect, pivot, 100f);
    }

    private static float SignedDistToTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 e0 = b - a, e1 = c - b, e2 = a - c;
        Vector2 v0 = p - a, v1 = p - b, v2 = p - c;
        Vector2 pq0 = v0 - e0 * Mathf.Clamp01(Vector2.Dot(v0, e0) / Vector2.Dot(e0, e0));
        Vector2 pq1 = v1 - e1 * Mathf.Clamp01(Vector2.Dot(v1, e1) / Vector2.Dot(e1, e1));
        Vector2 pq2 = v2 - e2 * Mathf.Clamp01(Vector2.Dot(v2, e2) / Vector2.Dot(e2, e2));
        float s = Mathf.Sign(e0.x * e2.y - e0.y * e2.x);
        Vector2 d = Vector2.Min(Vector2.Min(
            new Vector2(Vector2.Dot(pq0, pq0), s * (v0.x * e0.y - v0.y * e0.x)),
            new Vector2(Vector2.Dot(pq1, pq1), s * (v1.x * e1.y - v1.y * e1.x))),
            new Vector2(Vector2.Dot(pq2, pq2), s * (v2.x * e2.y - v2.y * e2.x)));
        return -Mathf.Sqrt(d.x) * Mathf.Sign(d.y);
    }

    public void SnapPlayerToFaceMenu()
    {
        var vr = VRRigManager.Instance;
        if (vr == null) return;

        float targetYaw = (MainMenuManager.IsInMenu && !Player.LocalPlayer) ? vr.MenuReferenceYaw : _menuWorldYaw;
        vr.FaceTowardsWorldYaw(targetYaw);

        _showResetPrompt = false;
        _resetPromptPoseInit = false;
        if (_resetPromptGroup != null) _resetPromptGroup.alpha = 0f;
        if (_resetPromptCanvas != null && _resetPromptCanvas.gameObject.activeSelf)
        {
            _resetPromptCanvas.gameObject.SetActive(false);
        }

        try
        {
            AudioManager.PlayGlobalClip("Click", variation: true, 1.15f, 0.05f);
        }
        catch
        {
            // Safe guard if AudioManager isn't loaded yet
        }

        SyncCanvasesImmediate();
    }

    public void RequestMenuRecenter()
    {
        _wasInIngameModal = false;
        _hudInitialized = false;
        _showResetPrompt = false;
        _resetPromptPoseInit = false;
    }

    public void SyncCanvasesImmediate()
    {
        if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive) return;
        UpdateWristMenuState(advanceTimers: false);
        UpdateWorldMenuTitleWidget();
        UpdateCanvasTransforms();
        UpdateThinkingNavWidget(advanceTimers: false);
        UpdateResetPositionWidget(advanceTimers: false);
    }

    private void LateUpdate()
    {
        if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive)
        {
            RestoreAllCanvases();
            SetWidgetActiveSafe(_worldTitleCanvas != null ? _worldTitleCanvas.gameObject : null, false);
            SetWidgetActiveSafe(_wristHoloPadCanvas != null ? _wristHoloPadCanvas.gameObject : null, false);
            SetWidgetActiveSafe(_inGameWristCanvas != null ? _inGameWristCanvas.gameObject : null, false);
            SetWidgetActiveSafe(_thinkingNavCanvas != null ? _thinkingNavCanvas.gameObject : null, false);
            SetWidgetActiveSafe(_resetPromptCanvas != null ? _resetPromptCanvas.gameObject : null, false);
            SetLaserVisible(false, Vector3.zero, Vector3.zero);
            IsLaserActiveOnUI = false;
            IsConsumingRightStick = false;
            return;
        }

        if (Time.unscaledTime >= _nextCanvasScanTime)
        {
            _nextCanvasScanTime = Time.unscaledTime + 0.25f;
            ScanForOverlayCanvases();
        }

        UpdateWristMenuState(advanceTimers: true);
        UpdateWorldMenuTitleWidget();
        UpdateCanvasTransforms();
        UpdateThinkingNavWidget(advanceTimers: true);
        UpdateResetPositionWidget(advanceTimers: true);
        UpdateLaserPointerAndEvents();
    }

    private static void SetWidgetActiveSafe(GameObject go, bool active)
    {
        if (go != null && go.activeSelf != active)
        {
            go.SetActive(active);
        }
    }

    private void ApplyCachedFontToCustomWidgets()
    {
        if (_cachedGameFont == null) return;

        if (_resetLabelText != null && _resetLabelText.font != _cachedGameFont)
        {
            _resetLabelText.font = _cachedGameFont;
            _resetLabelText.fontSharedMaterial = GetOrCreateOverlayTmpMaterial(_cachedGameFont.material, 4100);
            _resetLabelText.SetAllDirty();
        }

        if (_worldTitleFallbackText != null && _worldTitleFallbackText.font != _cachedGameFont)
        {
            _worldTitleFallbackText.font = _cachedGameFont;
            _worldTitleFallbackText.fontSharedMaterial = GetOrCreateOverlayTmpMaterial(_cachedGameFont.material, 4050);
            _worldTitleFallbackText.SetAllDirty();
        }

        if (_worldSubtitleText != null && _worldSubtitleText.font != _cachedGameFont)
        {
            _worldSubtitleText.font = _cachedGameFont;
            _worldSubtitleText.fontSharedMaterial = GetOrCreateOverlayTmpMaterial(_cachedGameFont.material, 4050);
            _worldSubtitleText.SetAllDirty();
        }

        ApplyFontToTmp(_worldVersionBadgeText, 4050);
        ApplyFontToTmp(_mainMenuReloadToggleText, 4100);
        ApplyFontToTmp(_mainMenuWristVersionText, 4100);
        ApplyFontToTmp(_inGameWristTitleText, 4100);
        ApplyFontToTmp(_inGameWristStatusText, 4100);
        for (int i = 0; i < _inGameWristButtons.Count; i++)
        {
            ApplyFontToTmp(_inGameWristButtons[i].Label, 4100);
        }
        for (int i = 0; i < _thinkingNavButtons.Count; i++)
        {
            ApplyFontToTmp(_thinkingNavButtons[i].Label, 4100);
        }
    }

    private void ApplyFontToTmp(TextMeshProUGUI tmp, int renderQueue)
    {
        if (tmp == null || _cachedGameFont == null || tmp.font == _cachedGameFont) return;
        tmp.font = _cachedGameFont;
        tmp.fontSharedMaterial = GetOrCreateOverlayTmpMaterial(_cachedGameFont.material, renderQueue);
        VRControllerIcons.EnsureRegisteredOnText(tmp);
        tmp.SetAllDirty();
    }

    private void ScanForOverlayCanvases()
    {
        _trackedCanvases.RemoveAll(t => t == null || t.Canvas == null);
        Canvas[] allCanvases = FindObjectsByType<Canvas>(FindObjectsInactive.Include);
        bool anyConvertedNow = false;

        foreach (Canvas c in allCanvases)
        {
            if (c == null || !c.isRootCanvas) continue;
            if (c == _resetPromptCanvas || c == _worldTitleCanvas || c == _wristHoloPadCanvas ||
                c == _inGameWristCanvas || c == _thinkingNavCanvas) continue;

            TrackedCanvas tc = null;
            for (int i = 0; i < _trackedCanvases.Count; i++)
            {
                if (_trackedCanvases[i].Canvas == c)
                {
                    tc = _trackedCanvases[i];
                    break;
                }
            }

            if (tc == null)
            {
                // Leave native WorldSpace canvases (player nametags, red dot sights, boat radar, world signs) completely untouched!
                if (c.renderMode == RenderMode.WorldSpace) continue;

                RectTransform rt = c.GetComponent<RectTransform>();
                CanvasScaler scaler = c.GetComponent<CanvasScaler>();
                Vector2 virtualSize = ComputeVirtualCanvasSize(rt, scaler);

                tc = new TrackedCanvas
                {
                    Canvas = c,
                    Rect = rt,
                    Scaler = scaler,
                    OrigRenderMode = c.renderMode,
                    OrigLocalScale = rt != null ? rt.localScale : Vector3.one,
                    OrigSizeDelta = rt != null ? rt.sizeDelta : new Vector2(1920f, 1080f),
                    OrigAnchorMin = rt != null ? rt.anchorMin : Vector2.zero,
                    OrigAnchorMax = rt != null ? rt.anchorMax : Vector2.one,
                    OrigPivot = rt != null ? rt.pivot : new Vector2(0.5f, 0.5f),
                    VirtualSize = virtualSize,
                    Converted = false
                };
                _trackedCanvases.Add(tc);
            }

            if (!tc.Converted && tc.Canvas != null)
            {
                ConvertToWorldSpace(tc);
                anyConvertedNow = true;
            }
            else if (tc.Converted && tc.Canvas != null)
            {
                if (tc.Canvas.worldCamera != _uiEventCamera)
                {
                    tc.Canvas.worldCamera = _uiEventCamera;
                }
                // Refresh newly instantiated UI rows (e.g. server browser entries, save slots) onto Layer 0 & ZTest-Always materials
                EnsureHierarchyLayerAndOverlayMaterials(tc.Canvas.transform, targetLayer: 0, renderQueue: 4100);
            }
        }

        if (anyConvertedNow)
        {
            Canvas.ForceUpdateCanvases();
        }
    }

    private static Vector2 ComputeVirtualCanvasSize(RectTransform rt, CanvasScaler scaler)
    {
        const float targetW = 1920f;
        const float targetH = 1080f;

        if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize)
        {
            Vector2 refRes = scaler.referenceResolution;
            if (refRes.x > 50f && refRes.y > 50f)
            {
                float scaleFactor = 1f;
                switch (scaler.screenMatchMode)
                {
                    case CanvasScaler.ScreenMatchMode.MatchWidthOrHeight:
                        float logW = Mathf.Log(targetW / refRes.x, 2f);
                        float logH = Mathf.Log(targetH / refRes.y, 2f);
                        scaleFactor = Mathf.Pow(2f, Mathf.Lerp(logW, logH, scaler.matchWidthOrHeight));
                        break;
                    case CanvasScaler.ScreenMatchMode.Expand:
                        scaleFactor = Mathf.Min(targetW / refRes.x, targetH / refRes.y);
                        break;
                    case CanvasScaler.ScreenMatchMode.Shrink:
                        scaleFactor = Mathf.Max(targetW / refRes.x, targetH / refRes.y);
                        break;
                }

                if (scaleFactor > 0.001f)
                {
                    return new Vector2(targetW / scaleFactor, targetH / scaleFactor);
                }
            }
        }
        else if (scaler != null && scaler.uiScaleMode == CanvasScaler.ScaleMode.ConstantPixelSize)
        {
            float sf = Mathf.Max(0.1f, scaler.scaleFactor);
            return new Vector2(targetW / sf, targetH / sf);
        }

        if (rt != null && rt.rect.width > 100f && rt.rect.height > 100f)
        {
            return new Vector2(rt.rect.width, rt.rect.height);
        }

        return new Vector2(targetW, targetH);
    }

    private void ConvertToWorldSpace(TrackedCanvas tc)
    {
        if (tc.Scaler != null)
        {
            tc.Scaler.enabled = false;
        }

        tc.Canvas.renderMode = RenderMode.WorldSpace;
        tc.Canvas.worldCamera = _uiEventCamera;
        tc.Canvas.additionalShaderChannels |=
            AdditionalCanvasShaderChannels.TexCoord1 |
            AdditionalCanvasShaderChannels.Normal |
            AdditionalCanvasShaderChannels.Tangent;

        GraphicRaycaster gr = tc.Canvas.GetComponent<GraphicRaycaster>();
        if (gr != null)
        {
            gr.blockingObjects = GraphicRaycaster.BlockingObjects.None;
        }

        if (tc.Group == null)
        {
            tc.Group = tc.Canvas.GetComponent<CanvasGroup>();
            if (tc.Group == null && IsWristMainMenuCanvasName(tc.Canvas.name))
            {
                tc.Group = tc.Canvas.gameObject.AddComponent<CanvasGroup>();
                tc.AddedCanvasGroup = true;
            }
        }

        if (tc.Rect != null)
        {
            tc.Rect.anchorMin = new Vector2(0.5f, 0.5f);
            tc.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            tc.Rect.pivot = new Vector2(0.5f, 0.5f);
            tc.Rect.sizeDelta = tc.VirtualSize;
        }

        // Keep converted canvases on Layer 0 (Default) so URP Forward Transparent always renders them in FinishFrameRendering!
        EnsureHierarchyLayerAndOverlayMaterials(tc.Canvas.transform, targetLayer: 0, renderQueue: 4100);

        tc.Converted = true;
        Plugin.Log.LogInfo($"Converted root Canvas '{tc.Canvas.name}' to WorldSpace (virtualSize={tc.VirtualSize.x:F0}x{tc.VirtualSize.y:F0}, sortOrder={tc.Canvas.sortingOrder}, layer={tc.Canvas.gameObject.layer})");
    }

    private void EnsureHierarchyLayerAndOverlayMaterials(Transform root, int targetLayer, int renderQueue)
    {
        if (root == null) return;
        EnsureHierarchyLayerFast(root, targetLayer);

        _tempGraphics.Clear();
        root.GetComponentsInChildren(true, _tempGraphics);
        for (int i = 0; i < _tempGraphics.Count; i++)
        {
            Graphic g = _tempGraphics[i];
            if (g == null || g is TMP_Text || g is TMP_SubMeshUI) continue;

            Material curMat = g.material;
            if (curMat != null && _createdOverlayMats.Contains(curMat))
                continue;

            if (!_savedGraphicMaterials.ContainsKey(g))
            {
                _savedGraphicMaterials[g] = curMat;
            }

            if (curMat == null || curMat == g.defaultMaterial)
            {
                g.material = renderQueue == 4050 ? _worldTitleGraphicMat : _overlayUiDefaultMat;
            }
            else
            {
                g.material = GetOrCreateOverlayGraphicMaterial(curMat, renderQueue);
            }
        }

        _tempTmpTexts.Clear();
        root.GetComponentsInChildren(true, _tempTmpTexts);
        for (int i = 0; i < _tempTmpTexts.Count; i++)
        {
            TMP_Text tmp = _tempTmpTexts[i];
            if (tmp == null) continue;

            VRControllerIcons.EnsureRegisteredOnText(tmp);

            if (_cachedGameFont == null && tmp.font != null)
            {
                _cachedGameFont = tmp.font;
                ApplyCachedFontToCustomWidgets();
            }

            Material sharedMat = tmp.fontSharedMaterial;
            if (sharedMat != null && !_createdOverlayMats.Contains(sharedMat))
            {
                if (!_savedTmpMaterials.ContainsKey(tmp))
                {
                    _savedTmpMaterials[tmp] = sharedMat;
                }
                tmp.fontSharedMaterial = GetOrCreateOverlayTmpMaterial(sharedMat, renderQueue);
                tmp.SetAllDirty();
            }
        }

        _tempTmpSubMeshes.Clear();
        root.GetComponentsInChildren(true, _tempTmpSubMeshes);
        for (int i = 0; i < _tempTmpSubMeshes.Count; i++)
        {
            TMP_SubMeshUI sub = _tempTmpSubMeshes[i];
            if (sub == null) continue;

            // VRControllerIcons.OverlaySpriteMaterial already has renderQueue=4105 & ZTest=Always
            Material subMat = sub.sharedMaterial;
            if (subMat == null || subMat == VRControllerIcons.OverlaySpriteMaterial || _createdOverlayMats.Contains(subMat))
                continue;

            sub.sharedMaterial = GetOrCreateOverlayTmpMaterial(subMat, renderQueue);
        }
    }

    private void EnsureHierarchyLayerFast(Transform root, int targetLayer)
    {
        if (root == null) return;
        GameObject go = root.gameObject;
        if (go.layer != targetLayer)
        {
            if (!_savedObjectLayers.ContainsKey(go))
            {
                _savedObjectLayers[go] = go.layer;
            }
            go.layer = targetLayer;
        }

        int childCount = root.childCount;
        for (int i = 0; i < childCount; i++)
        {
            EnsureHierarchyLayerFast(root.GetChild(i), targetLayer);
        }
    }

    private void RestoreOriginalTitleIfHidden()
    {
        if (_origTitleHiddenForWrist && _origHowToFishTitleObj != null)
        {
            _origHowToFishTitleObj.transform.localScale = _origHowToFishTitleScale;
        }
        _origTitleHiddenForWrist = false;
    }

    private void RestoreAllCanvases()
    {
        RestoreOriginalTitleIfHidden();

        foreach (var kv in _savedGraphicMaterials)
        {
            if (kv.Key != null) kv.Key.material = kv.Value;
        }
        _savedGraphicMaterials.Clear();

        foreach (var kv in _savedTmpMaterials)
        {
            if (kv.Key != null && kv.Value != null) kv.Key.fontSharedMaterial = kv.Value;
        }
        _savedTmpMaterials.Clear();

        foreach (var tc in _trackedCanvases)
        {
            if (tc == null || !tc.Converted || tc.Canvas == null) continue;

            if (tc.AddedCanvasGroup && tc.Group != null)
            {
                Destroy(tc.Group);
                tc.Group = null;
                tc.AddedCanvasGroup = false;
            }
            else if (tc.Group != null)
            {
                tc.Group.interactable = true;
                tc.Group.blocksRaycasts = true;
            }

            tc.Canvas.renderMode = tc.OrigRenderMode;
            tc.Canvas.worldCamera = null;
            if (tc.Scaler != null) tc.Scaler.enabled = true;
            if (tc.Rect != null)
            {
                tc.Rect.anchorMin = tc.OrigAnchorMin;
                tc.Rect.anchorMax = tc.OrigAnchorMax;
                tc.Rect.pivot = tc.OrigPivot;
                tc.Rect.localScale = tc.OrigLocalScale;
                tc.Rect.sizeDelta = tc.OrigSizeDelta;
            }
            tc.Converted = false;
        }

        if (_savedObjectLayers.Count > 0)
        {
            foreach (var kv in _savedObjectLayers)
            {
                if (kv.Key != null)
                {
                    kv.Key.layer = kv.Value;
                }
            }
            _savedObjectLayers.Clear();
        }
    }

    private static bool IsModalMenuOpen()
    {
        return MainMenuManager.IsInMenu || PauseManager.IsPaused || PlayerThinking.IsThinking || ChatManager.IsTyping;
    }

    private static bool IsInWristMainMenuMode()
    {
        return MainMenuManager.IsInMenu && !Player.LocalPlayer && !LocalSkin.IsInSkinCustomization;
    }

    private static bool IsInSaveWristMenuMode()
    {
        Player lp = Player.LocalPlayer;
        if (lp == null || MainMenuManager.IsInMenu) return false;
        if (PauseManager.IsPaused || PlayerThinking.IsThinking || NpcUI.NPCDialougeOpen || ChatManager.IsTyping) return false;
        if (lp.Dying != null && lp.Dying.IsDead) return false;
        return true;
    }

    private static bool IsOffHandBusyForWristMenu()
    {
        if (VRFishingPatches.IsCrankingReel || VRCombatAndBoatPatches.IsOffHandBusyWithCombatOrBoat)
            return true;

        var vr = VRRigManager.Instance;
        Player lp = Player.LocalPlayer;
        Item held = lp != null && lp.Holding != null ? lp.Holding.HeldItem : null;
        if (vr != null && held != null && held.Weapon != null)
        {
            // Suppress accidental opening when hands are close together aiming or reloading a firearm/bow
            float handSeparation = Vector3.Distance(vr.OffHandGripWorldPos, vr.DominantGripWorldPos);
            if (handSeparation < 0.36f)
                return true;
        }
        return false;
    }

    private static bool IsWristMainMenuCanvasName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        return name.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Perma", StringComparison.OrdinalIgnoreCase) >= 0 ||
               name.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void UpdateWristMenuState(bool advanceTimers)
    {
        var vr = VRRigManager.Instance;
        if (vr == null) return;

        var frame = vr.CurrentFrame;
        bool inWristMainMenu = IsInWristMainMenuMode();
        bool inSaveWristMode = !inWristMainMenu && IsInSaveWristMenuMode();
        bool anyWristMode = inWristMainMenu || inSaveWristMode;

        // Pick off-hand wrist for menu by default (Left wrist when Right-dominant), falling back if only 1 controller is on
        bool preferLeft = vr.IsRightDominant;
        if (preferLeft && !frame.handLeft.IsActive && frame.handRight.IsActive)
            IsLeftMenuWrist = false;
        else if (!preferLeft && !frame.handRight.IsActive && frame.handLeft.IsActive)
            IsLeftMenuWrist = true;
        else
            IsLeftMenuWrist = preferLeft;

        // Clean up MainMenu CanvasGroup alpha if transitioning out of Main Menu Wrist mode
        if (!inWristMainMenu && _wasInWristMainMenu)
        {
            _wasInWristMainMenu = false;
            _isWristMenuOpen = false;
            WristMenuAlpha = 0f;
            _wristPoseInitialized = false;
            RestoreWristCanvasGroupsOnExit();
        }

        if (!inSaveWristMode && _wasInSaveWristMode)
        {
            _wasInSaveWristMode = false;
            _isWristMenuOpen = false;
            WristMenuAlpha = 0f;
            _wristPoseInitialized = false;
        }

        if (!anyWristMode)
        {
            _isWristMenuOpen = false;
            WristMenuAlpha = 0f;
            _wristPoseInitialized = false;
            SetWidgetActiveSafe(_wristHoloPadCanvas != null ? _wristHoloPadCanvas.gameObject : null, false);
            SetWidgetActiveSafe(_inGameWristCanvas != null ? _inGameWristCanvas.gameObject : null, false);
            return;
        }

        if (inWristMainMenu) _wasInWristMainMenu = true;
        if (inSaveWristMode) _wasInSaveWristMode = true;

        bool wristHandActive = IsLeftMenuWrist ? frame.handLeft.IsActive : frame.handRight.IsActive;
        Vector3 wristPos = IsLeftMenuWrist ? vr.LeftGripWorldPos : vr.RightGripWorldPos;
        Quaternion wristRot = IsLeftMenuWrist ? vr.LeftGripWorldRot : vr.RightGripWorldRot;

        Vector3 toHead = vr.HeadWorldPos - wristPos;
        float distToHead = toHead.magnitude;
        Vector3 toHeadDir = distToHead > 0.001f ? (toHead / distToHead) : Vector3.up;

        // Target world position of the holographic screen hovering right above the upturned wrist
        float hoverHeight = inSaveWristMode ? 0.125f : 0.145f;
        Vector3 desiredMenuPos = wristPos + Vector3.up * hoverHeight + toHeadDir * 0.045f;
        Vector3 lookVec = desiredMenuPos - vr.HeadWorldPos;
        Quaternion desiredMenuRot = lookVec.sqrMagnitude > 0.0005f
            ? Quaternion.LookRotation(lookVec.normalized, Vector3.up)
            : vr.HeadWorldRot;

        bool offHandBusy = inSaveWristMode && IsOffHandBusyForWristMenu();

        if (!wristHandActive || offHandBusy)
        {
            _isWristMenuOpen = false;
        }
        else
        {
            // Check 1: Player turned their wrist around (either inner-wrist/palm toward face OR wristwatch-check toward face)
            Vector3 localInnerNormal = new Vector3(IsLeftMenuWrist ? 0.88f : -0.88f, -0.47f, 0f).normalized;
            Vector3 localOuterNormal = new Vector3(IsLeftMenuWrist ? -0.90f : 0.90f, 0.25f, -0.35f).normalized;
            float dotInner = Vector3.Dot(wristRot * localInnerNormal, toHeadDir);
            float dotOuter = Vector3.Dot(wristRot * localOuterNormal, toHeadDir);

            // Check 2: Player is looking directly toward their wrist / wrist-menu area
            Vector3 gazeTarget = wristPos + Vector3.up * 0.07f;
            Vector3 headToWristDir = (gazeTarget - vr.HeadWorldPos).normalized;
            float headLookDot = Vector3.Dot(vr.HeadWorldRot * Vector3.forward, headToWristDir);
            bool inReach = distToHead > 0.10f && distToHead < 0.95f;

            if (!_isWristMenuOpen)
            {
                bool wristTurned = dotInner > 0.44f || dotOuter > 0.58f;
                bool lookingDirectlyAtWrist = headLookDot > 0.78f;
                if (inReach && wristTurned && lookingDirectlyAtWrist)
                {
                    _isWristMenuOpen = true;
                    RefreshReloadToggleLabels();
                    if (advanceTimers)
                    {
                        OpenXRBridge.TriggerHaptic(!IsLeftMenuWrist, 0.11f, 0.015f, 165f);
                    }
                }
            }
            else
            {
                // Comfortable hysteresis while open so scanning top/bottom buttons doesn't flicker the menu shut
                bool wristStillTurned = dotInner > 0.18f || dotOuter > 0.32f;
                bool stillLookingAtWrist = headLookDot > 0.56f;
                if (!inReach || !wristStillTurned || !stillLookingAtWrist)
                {
                    _isWristMenuOpen = false;
                }
            }
        }

        if (advanceTimers)
        {
            float fadeRate = _isWristMenuOpen ? 5.5f : 4.5f;
            WristMenuAlpha = Mathf.MoveTowards(WristMenuAlpha, _isWristMenuOpen ? 1f : 0f, Time.unscaledDeltaTime * fadeRate);
        }

        if (!_wristPoseInitialized || WristMenuAlpha <= 0.01f)
        {
            _smoothedWristMenuPos = desiredMenuPos;
            _smoothedWristMenuRot = desiredMenuRot;
            _wristPoseInitialized = true;
        }
        else if (advanceTimers)
        {
            float posT = Mathf.Clamp01(Time.unscaledDeltaTime * 22f);
            float rotT = Mathf.Clamp01(Time.unscaledDeltaTime * 18f);
            _smoothedWristMenuPos = Vector3.Lerp(_smoothedWristMenuPos, desiredMenuPos, posT);
            _smoothedWristMenuRot = Quaternion.Slerp(_smoothedWristMenuRot, desiredMenuRot, rotT);
        }
        else
        {
            // Keep locked to wrist delta during OnBeforeRender so walking/boat motion never introduces 1-frame jitter
            _smoothedWristMenuPos = Vector3.Lerp(_smoothedWristMenuPos, desiredMenuPos, 0.65f);
            _smoothedWristMenuRot = Quaternion.Slerp(_smoothedWristMenuRot, desiredMenuRot, 0.65f);
        }

        float pop = Mathf.Lerp(0.86f, 1.0f, Mathf.SmoothStep(0f, 1f, WristMenuAlpha));

        // Update translucent Wrist Holo-Pad backing right behind the wrist menu
        if (_wristHoloPadCanvas != null && _wristHoloPadGroup != null && _wristHoloPadRect != null)
        {
            bool showPad = WristMenuAlpha > 0.01f;
            SetWidgetActiveSafe(_wristHoloPadCanvas.gameObject, showPad);
            if (showPad)
            {
                _wristHoloPadGroup.alpha = WristMenuAlpha;
                bool showMainMenuPill = inWristMainMenu && !PauseManager.IsPaused;
                SetWidgetActiveSafe(_mainMenuReloadToggleObj, showMainMenuPill);
                SetWidgetActiveSafe(_mainMenuWristVersionObj, inWristMainMenu);
                _wristHoloPadGroup.interactable = showMainMenuPill && WristMenuAlpha > 0.22f;
                _wristHoloPadGroup.blocksRaycasts = showMainMenuPill && WristMenuAlpha > 0.22f;

                _wristHoloPadRect.sizeDelta = inSaveWristMode ? new Vector2(960f, 670f) : new Vector2(2040f, 1180f);
                float basePadScale = inSaveWristMode ? 0.00028f : 0.00024f;
                float padScale = basePadScale * VRConfig.HudScale.Value * pop;
                Vector3 padPos = _smoothedWristMenuPos + (_smoothedWristMenuRot * new Vector3(0f, 0f, 0.004f));
                _wristHoloPadRect.SetPositionAndRotation(padPos, _smoothedWristMenuRot);
                _wristHoloPadRect.localScale = Vector3.one * padScale;

                if (showMainMenuPill && advanceTimers)
                {
                    RefreshReloadToggleLabels();
                    bool hovered = _currentHoverObj != null && _mainMenuReloadToggleObj != null &&
                                   (_currentHoverObj == _mainMenuReloadToggleObj || _currentHoverObj.transform.IsChildOf(_mainMenuReloadToggleObj.transform));
                    float pillScale = hovered ? 1.06f : 1.0f;
                    _mainMenuReloadToggleObj.transform.localScale = Vector3.Lerp(
                        _mainMenuReloadToggleObj.transform.localScale, Vector3.one * pillScale, Mathf.Clamp01(Time.unscaledDeltaTime * 14f));
                    if (_mainMenuReloadToggleBorder != null)
                    {
                        Color targetBorder = hovered ? new Color(1.0f, 0.90f, 0.35f, 1.0f) : new Color(0.38f, 0.90f, 1.0f, 0.90f);
                        _mainMenuReloadToggleBorder.color = Color.Lerp(_mainMenuReloadToggleBorder.color, targetBorder, Mathf.Clamp01(Time.unscaledDeltaTime * 14f));
                    }
                }
            }
        }

        // Update dedicated In-Save Wrist Menu canvas & buttons
        UpdateInGameWristMenuWidget(inSaveWristMode, pop, advanceTimers);
    }

    private void UpdateInGameWristMenuWidget(bool inSaveWristMode, float pop, bool advanceTimers)
    {
        if (_inGameWristCanvas == null || _inGameWristGroup == null || _inGameWristRect == null) return;

        bool showInGameWrist = inSaveWristMode && WristMenuAlpha > 0.01f;
        SetWidgetActiveSafe(_inGameWristCanvas.gameObject, showInGameWrist);
        if (!showInGameWrist)
        {
            _inGameWristGroup.alpha = 0f;
            _inGameWristGroup.interactable = false;
            _inGameWristGroup.blocksRaycasts = false;
            return;
        }

        _inGameWristGroup.alpha = WristMenuAlpha;
        bool clickable = WristMenuAlpha > 0.22f;
        _inGameWristGroup.interactable = clickable;
        _inGameWristGroup.blocksRaycasts = clickable;

        float wristScale = 0.00028f * VRConfig.HudScale.Value * pop;
        _inGameWristRect.SetPositionAndRotation(_smoothedWristMenuPos, _smoothedWristMenuRot);
        _inGameWristRect.localScale = Vector3.one * wristScale;

        if (!advanceTimers) return;

        RefreshReloadToggleLabels();

        // Update dynamic held-item / ammo status & Drop button label
        Player lp = Player.LocalPlayer;
        Item held = lp != null && lp.Holding != null ? lp.Holding.HeldItem : null;
        string statusStr;
        if (Boat.IsDrivingLocally)
        {
            statusStr = "<color=#62E3FF>DRIVING BOAT</color>";
        }
        else if (held == null)
        {
            statusStr = "EMPTY HANDS";
        }
        else if (held.Weapon != null && !(held.Weapon is Bow) && held.Weapon.Attachments != null)
        {
            string itemName = FormatCleanItemName(held);
            string reloadStage = VRCombatAndBoatPatches.GetCurrentManualReloadPrompt(held.Weapon, richText: true);
            if (!string.IsNullOrEmpty(reloadStage))
            {
                statusStr = $"{itemName}  \u2022  {reloadStage}";
            }
            else
            {
                int ammo = held.Weapon.Ammo;
                int maxAmmo = held.Weapon.Attachments.AmmoPerMag;
                statusStr = ammo <= 0
                    ? $"{itemName}  \u2022  <color=#FF9E4A>EMPTY (RELOAD!)</color>"
                    : $"{itemName}  \u2022  <color=#7CFF9B>{ammo} / {maxAmmo}</color>";
            }
        }
        else
        {
            statusStr = FormatCleanItemName(held);
        }

        if (_inGameWristStatusText != null && _inGameWristStatusText.text != statusStr)
        {
            _inGameWristStatusText.text = statusStr;
        }

        if (_btnWristDropItem.Label != null)
        {
            string dropLabel = Boat.IsDrivingLocally
                ? "Exit Boat Helm"
                : (held != null ? "Drop Held Item" : "<color=#7E97AD>Drop Item (Empty)</color>");
            if (_btnWristDropItem.Label.text != dropLabel)
            {
                _btnWristDropItem.Label.text = dropLabel;
            }
        }

        // Smooth hover feedback on In-Save Wrist Menu buttons
        float dtLerp = Mathf.Clamp01(Time.unscaledDeltaTime * 15f);
        for (int i = 0; i < _inGameWristButtons.Count; i++)
        {
            WristButtonVisual b = _inGameWristButtons[i];
            if (b.RootObj == null || b.Rect == null) continue;

            bool hovered = _currentHoverObj != null &&
                           (_currentHoverObj == b.RootObj || _currentHoverObj.transform.IsChildOf(b.RootObj.transform));
            float targetScale = hovered ? 1.05f : 1.0f;
            b.Rect.localScale = Vector3.Lerp(b.Rect.localScale, Vector3.one * targetScale, dtLerp);

            if (b.Border != null)
            {
                Color targetBorder = hovered
                    ? new Color(1.0f, 0.90f, 0.35f, 1.0f)
                    : new Color(0.36f, 0.88f, 1.0f, 0.82f);
                b.Border.color = Color.Lerp(b.Border.color, targetBorder, dtLerp);
            }
            if (b.Fill != null)
            {
                Color targetFill = hovered
                    ? new Color(0.11f, 0.24f, 0.38f, 0.96f)
                    : new Color(0.06f, 0.14f, 0.24f, 0.90f);
                b.Fill.color = Color.Lerp(b.Fill.color, targetFill, dtLerp);
            }
        }
    }

    private static string FormatCleanItemName(Item item)
    {
        if (item == null || string.IsNullOrEmpty(item.name)) return "HOLDING ITEM";
        string clean = item.name.Replace("(Clone)", string.Empty).Trim();
        return string.IsNullOrEmpty(clean) ? "HOLDING ITEM" : clean.ToUpperInvariant();
    }

    private void RestoreWristCanvasGroupsOnExit()
    {
        var cm = CanvasInstanceField?.GetValue(null) as CanvasManager;
        if (cm != null)
        {
            var mainGroup = MainMenuGroupField?.GetValue(cm) as CanvasGroup;
            if (mainGroup != null)
            {
                mainGroup.interactable = true;
                mainGroup.blocksRaycasts = true;
                // Only force alpha=1 if still in MainMenu (e.g. entering Character Customization);
                // if CrashAnimation started (IsInMenu==false), let its LeanTween fade-out run undisturbed!
                if (MainMenuManager.IsInMenu && !PauseManager.IsPaused)
                {
                    mainGroup.alpha = 1f;
                }
            }

            var permaGroup = PermaGroupField?.GetValue(cm) as CanvasGroup;
            if (permaGroup != null)
            {
                permaGroup.alpha = 1f;
            }
        }

        foreach (var tc in _trackedCanvases)
        {
            if (tc?.Group == null || tc.Canvas == null) continue;
            if (tc.Canvas.name.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                tc.Group.alpha = 1f;
                tc.Group.interactable = true;
                tc.Group.blocksRaycasts = true;
            }
        }
    }

    private void UpdateWorldMenuTitleWidget()
    {
        if (_worldTitleCanvas == null || _worldTitleRootRect == null) return;

        var vr = VRRigManager.Instance;
        bool inMainMenu = vr != null && MainMenuManager.IsInMenu && !Player.LocalPlayer;
        if (!inMainMenu)
        {
            SetWidgetActiveSafe(_worldTitleCanvas.gameObject, false);
            RestoreOriginalTitleIfHidden();
            return;
        }

        SetWidgetActiveSafe(_worldTitleCanvas.gameObject, true);

        // Imprint VR Mod version onto CanvasManager's corner version text as well so both game + VR mod versions show cleanly!
        var cm = CanvasInstanceField?.GetValue(null) as CanvasManager;
        if (cm != null && CanvasVersionTextField != null)
        {
            var verTmp = CanvasVersionTextField.GetValue(cm) as TextMeshProUGUI;
            if (verTmp != null && verTmp.text != null && !verTmp.text.Contains("VR Mod"))
            {
                string baseVer = string.IsNullOrWhiteSpace(verTmp.text) ? (Application.version ?? "") : verTmp.text.Trim();
                verTmp.text = string.IsNullOrEmpty(baseVer)
                    ? $"VR Mod v{Plugin.PluginVersion}"
                    : $"{baseVer}  \u2022  VR Mod v{Plugin.PluginVersion}";
            }
        }

        // Clone the official _howToFishTitle from CanvasManager onto our 3D World Title Canvas once available,
        // and hide the duplicate inside MainMenuCanvas while in wrist-menu mode!
        TrySyncGameTitleClone(hideOriginalOnWrist: !LocalSkin.IsInSkinCustomization);

        // Place the Game Title + Sub-text out in the 3D Main Menu world over the boat's bow
        Quaternion worldRefRot = Quaternion.Euler(0f, vr.MenuReferenceYaw, 0f);
        float gentleBob = Mathf.Sin(Time.unscaledTime * 1.35f) * 0.032f;
        float heightOffset = LocalSkin.IsInSkinCustomization ? (0.94f + gentleBob) : (0.36f + gentleBob);
        Vector3 titleWorldPos = vr.HeadWorldPos + (worldRefRot * new Vector3(0f, heightOffset, 3.05f));

        Vector3 lookFromHead = titleWorldPos - vr.HeadWorldPos;
        Quaternion titleWorldRot = lookFromHead.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(lookFromHead.normalized, Vector3.up)
            : worldRefRot;

        _worldTitleRootRect.SetPositionAndRotation(titleWorldPos, titleWorldRot);
        float worldScale = LocalSkin.IsInSkinCustomization ? 0.00110f : 0.00145f;
        _worldTitleRootRect.localScale = Vector3.one * worldScale;

        // Update dynamic sub-text instructing the player to turn & look at their wrist
        if (_worldSubtitleText != null)
        {
            var frame = vr.CurrentFrame;
            bool anyHandActive = frame.handLeft.IsActive || frame.handRight.IsActive;
            string wristName = IsLeftMenuWrist ? "left wrist" : "right wrist";
            string pointHandName = IsLeftMenuWrist ? "right hand" : "left hand";

            string desiredSub;
            if (LocalSkin.IsInSkinCustomization)
            {
                desiredSub = "Customize your character  \u2022  Click <color=#62E3FF>Back</color> to return to wrist";
            }
            else if (!anyHandActive)
            {
                desiredSub = "Wake your VR controllers, then turn your <color=#62E3FF>wrist</color> & look at it for Menu";
            }
            else if (WristMenuAlpha >= 0.45f)
            {
                desiredSub = $"Point with your <color=#FFE875>{pointHandName}</color> & pull Trigger to select";
            }
            else
            {
                desiredSub = $"Turn your <color=#62E3FF>{wristName}</color> around & look at it for the Menu";
            }

            if (_worldSubtitleText.text != desiredSub)
            {
                _worldSubtitleText.text = desiredSub;
            }
        }
    }

    private void TrySyncGameTitleClone(bool hideOriginalOnWrist)
    {
        if (_origHowToFishTitleObj == null && CanvasInstanceField != null && HowToFishTitleField != null)
        {
            var cm = CanvasInstanceField.GetValue(null) as CanvasManager;
            if (cm != null)
            {
                _origHowToFishTitleObj = HowToFishTitleField.GetValue(cm) as GameObject;
                if (_origHowToFishTitleObj != null && !_origTitleScaleCaptured)
                {
                    _origHowToFishTitleScale = _origHowToFishTitleObj.transform.localScale;
                    if (_origHowToFishTitleScale.sqrMagnitude < 0.0001f)
                        _origHowToFishTitleScale = Vector3.one;
                    _origTitleScaleCaptured = true;
                }
            }
        }

        if (_origHowToFishTitleObj != null && _worldTitleClonedObj == null && _worldTitleCloneParent != null)
        {
            // Temporarily restore original scale if needed so CalculateRelativeRectTransformBounds measures true pixel dimensions
            Vector3 savedScale = _origHowToFishTitleObj.transform.localScale;
            _origHowToFishTitleObj.transform.localScale = _origHowToFishTitleScale;

            RectTransform origRt = _origHowToFishTitleObj.GetComponent<RectTransform>();
            Vector2 origBoxSize = origRt != null ? origRt.rect.size : Vector2.zero;

            // Compute exact visual bounds across all visible Graphics inside _origHowToFishTitleObj (handles zero-size pivots AND full-canvas containers!)
            bool hasVisualBounds = false;
            Bounds visualBounds = default;
            foreach (Graphic g in _origHowToFishTitleObj.GetComponentsInChildren<Graphic>(false))
            {
                Bounds gb = RectTransformUtility.CalculateRelativeRectTransformBounds(_origHowToFishTitleObj.transform, g.transform);
                if (gb.size.x > 1f && gb.size.y > 1f)
                {
                    if (!hasVisualBounds)
                    {
                        visualBounds = gb;
                        hasVisualBounds = true;
                    }
                    else
                    {
                        visualBounds.Encapsulate(gb);
                    }
                }
            }
            if (!hasVisualBounds)
            {
                visualBounds = RectTransformUtility.CalculateRelativeRectTransformBounds(_origHowToFishTitleObj.transform);
                hasVisualBounds = visualBounds.size.x > 20f && visualBounds.size.y > 10f;
            }

            _origHowToFishTitleObj.transform.localScale = savedScale;

            if (hasVisualBounds)
            {
                _worldTitleClonedObj = Instantiate(_origHowToFishTitleObj, _worldTitleCloneParent, false);
                _worldTitleClonedObj.name = "HTFVR_ClonedHowToFishTitle";
                _worldTitleClonedObj.SetActive(true);

                float fitScale = 1f;
                if (visualBounds.size.x > 1450f)
                {
                    fitScale = 1450f / visualBounds.size.x;
                }
                _worldTitleClonedObj.transform.localScale = _origHowToFishTitleScale * fitScale;

                RectTransform cloneRt = _worldTitleClonedObj.GetComponent<RectTransform>();
                if (cloneRt != null)
                {
                    cloneRt.anchorMin = new Vector2(0.5f, 0.5f);
                    cloneRt.anchorMax = new Vector2(0.5f, 0.5f);
                    cloneRt.pivot = origRt != null ? origRt.pivot : new Vector2(0.5f, 0.5f);
                    cloneRt.sizeDelta = origBoxSize;
                    // Shift clone so the visual center of the title artwork lands squarely at (0,0) inside TitleHolder!
                    cloneRt.anchoredPosition = -new Vector2(visualBounds.center.x, visualBounds.center.y) * fitScale;
                }

                foreach (Graphic g in _worldTitleClonedObj.GetComponentsInChildren<Graphic>(true))
                {
                    g.raycastTarget = false;
                }

                EnsureHierarchyLayerAndOverlayMaterials(_worldTitleClonedObj.transform, targetLayer: 0, renderQueue: 4050);

                if (_worldTitleFallbackText != null)
                {
                    _worldTitleFallbackText.gameObject.SetActive(false);
                }
                Plugin.Log.LogInfo($"Cloned official Game Title '{_origHowToFishTitleObj.name}' (visualBounds={visualBounds.size.x:F0}x{visualBounds.size.y:F0}) into 3D Main Menu World.");
            }
        }

        if (_origHowToFishTitleObj != null)
        {
            if (hideOriginalOnWrist)
            {
                _origHowToFishTitleObj.transform.localScale = Vector3.zero;
                _origTitleHiddenForWrist = true;
            }
            else if (_origTitleHiddenForWrist)
            {
                _origHowToFishTitleObj.transform.localScale = _origHowToFishTitleScale;
                _origTitleHiddenForWrist = false;
            }
        }
    }

    private void UpdateCanvasTransforms()
    {
        var vr = VRRigManager.Instance;
        if (vr == null) return;

        float dist = Mathf.Clamp(VRConfig.HudDistance.Value, 0.7f, 2.5f);
        float baseScale = 0.00090f * VRConfig.HudScale.Value * (dist / 1.35f);

        bool inMainMenu = MainMenuManager.IsInMenu && !Player.LocalPlayer;
        bool inWristMainMenu = IsInWristMainMenuMode();
        bool modalMenu = IsModalMenuOpen();

        if (modalMenu)
        {
            if (inMainMenu)
            {
                _menuWorldYaw = vr.MenuReferenceYaw;
                _wasInIngameModal = false;
            }
            else
            {
                if (!_wasInIngameModal)
                {
                    _wasInIngameModal = true;
                    _ingameModalWorldYaw = vr.HeadWorldRot.eulerAngles.y;
                }
                _menuWorldYaw = _ingameModalWorldYaw;
            }

            _menuAnchorRot = Quaternion.Euler(0f, _menuWorldYaw, 0f);
            _menuAnchorPos = vr.HeadWorldPos + (_menuAnchorRot * new Vector3(0f, -0.02f, dist));
        }
        else
        {
            _wasInIngameModal = false;
        }

        // Update Smooth Gameplay HUD pose (relative to HeadWorldPos so vehicle motion never lags HUD!)
        Vector3 headForward = vr.HeadWorldRot * Vector3.forward;
        Vector3 flatForward = new Vector3(headForward.x, Mathf.Clamp(headForward.y * 0.45f, -0.35f, 0.35f), headForward.z).normalized;
        if (flatForward.sqrMagnitude < 0.01f) flatForward = Vector3.forward;
        Quaternion targetHudRot = Quaternion.LookRotation(flatForward, Vector3.up);
        Vector3 targetHudOffset = flatForward * dist;

        if (!_hudInitialized)
        {
            _hudInitialized = true;
            _hudSmoothedOffset = targetHudOffset;
            _hudSmoothedRot = targetHudRot;
        }
        else
        {
            float t = Mathf.Clamp01(Time.unscaledDeltaTime * 11f);
            _hudSmoothedOffset = Vector3.Lerp(_hudSmoothedOffset, targetHudOffset, t);
            _hudSmoothedRot = Quaternion.Slerp(_hudSmoothedRot, targetHudRot, t);
        }
        Vector3 hudWorldPos = vr.HeadWorldPos + _hudSmoothedOffset;

        float wristPop = Mathf.Lerp(0.86f, 1.0f, Mathf.SmoothStep(0f, 1f, WristMenuAlpha));

        for (int i = _trackedCanvases.Count - 1; i >= 0; i--)
        {
            TrackedCanvas tc = _trackedCanvases[i];
            if (tc == null || tc.Canvas == null || tc.Rect == null)
            {
                _trackedCanvases.RemoveAt(i);
                continue;
            }
            if (!tc.Converted || !tc.Canvas.gameObject.activeInHierarchy) continue;

            string cName = tc.Canvas.name;

            // UnderwaterCanvas holds flat _blackScreen & _underwaterImage; VRRigManager renders stereo 360° _overlaySphereObj from their colors!
            if (cName.IndexOf("Underwater", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                tc.Rect.localScale = Vector3.zero;
                continue;
            }

            float normScale = baseScale * (1080f / Mathf.Max(240f, tc.VirtualSize.y));
            float sortDepth = -Mathf.Clamp(tc.Canvas.sortingOrder, -20, 50) * 0.0006f;

            if (inWristMainMenu && IsWristMainMenuCanvasName(cName))
            {
                // Mount MainMenuCanvas, PermaCanvas, and Main-Menu PauseCanvas (Options) on the player's upturned wrist!
                if (tc.Group != null)
                {
                    bool allowClicks = WristMenuAlpha > 0.22f;
                    tc.Group.interactable = allowClicks;
                    tc.Group.blocksRaycasts = allowClicks;

                    // Keep MainMenuCanvas hidden while Options (PauseManager.IsPaused) is open, otherwise fade with WristMenuAlpha
                    bool forceHiddenByPause = PauseManager.IsPaused && cName.IndexOf("MainMenu", StringComparison.OrdinalIgnoreCase) >= 0;
                    tc.Group.alpha = forceHiddenByPause ? 0f : WristMenuAlpha;
                }

                if (WristMenuAlpha <= 0.005f)
                {
                    tc.Rect.localScale = Vector3.zero;
                }
                else
                {
                    float wristScale = 0.00024f * VRConfig.HudScale.Value * wristPop * (1080f / Mathf.Max(240f, tc.VirtualSize.y));
                    Vector3 wristPosWithDepth = _smoothedWristMenuPos + (_smoothedWristMenuRot * new Vector3(0f, 0f, sortDepth));
                    tc.Rect.SetPositionAndRotation(wristPosWithDepth, _smoothedWristMenuRot);
                    SetWorldAdjustedScale(tc.Rect, wristScale);
                }
                continue;
            }

            // Ensure PauseCanvas CanvasGroup is fully opaque & clickable when opened as an in-game modal
            if (tc.Group != null && cName.IndexOf("Pause", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                tc.Group.alpha = 1f;
                tc.Group.interactable = true;
                tc.Group.blocksRaycasts = true;
            }

            bool isGameplayWristHud = VRConfig.WristHud.Value && !modalMenu &&
                                      (cName.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       cName.IndexOf("Inventory", StringComparison.OrdinalIgnoreCase) >= 0);

            if (modalMenu)
            {
                Vector3 posWithDepth = _menuAnchorPos + (_menuAnchorRot * new Vector3(0f, 0f, sortDepth));
                tc.Rect.SetPositionAndRotation(posWithDepth, _menuAnchorRot);
                SetWorldAdjustedScale(tc.Rect, normScale);
            }
            else if (isGameplayWristHud && vr.OffHandState.IsActive)
            {
                Quaternion wristRot = vr.OffHandGripWorldRot * Quaternion.Euler(35f, vr.IsRightDominant ? 85f : -85f, 0f);
                Vector3 wristPos = vr.OffHandGripWorldPos + (wristRot * new Vector3(0f, 0.06f, 0.04f));
                tc.Rect.SetPositionAndRotation(wristPos, wristRot);
                SetWorldAdjustedScale(tc.Rect, 0.00018f * VRConfig.HudScale.Value * (1080f / Mathf.Max(240f, tc.VirtualSize.y)));
            }
            else
            {
                Vector3 posWithDepth = hudWorldPos + (_hudSmoothedRot * new Vector3(0f, 0f, sortDepth));
                tc.Rect.SetPositionAndRotation(posWithDepth, _hudSmoothedRot);
                SetWorldAdjustedScale(tc.Rect, normScale);
            }
        }
    }

    private static void SetWorldAdjustedScale(RectTransform rect, float desiredLossyScale)
    {
        Transform parent = rect.parent;
        if (parent != null)
        {
            Vector3 ps = parent.lossyScale;
            float sx = Mathf.Abs(ps.x) > 0.0001f ? desiredLossyScale / ps.x : desiredLossyScale;
            float sy = Mathf.Abs(ps.y) > 0.0001f ? desiredLossyScale / ps.y : desiredLossyScale;
            float sz = Mathf.Abs(ps.z) > 0.0001f ? desiredLossyScale / ps.z : desiredLossyScale;
            rect.localScale = new Vector3(sx, sy, sz);
        }
        else
        {
            rect.localScale = Vector3.one * desiredLossyScale;
        }
    }

    private bool _gazeOnInGameUI;

    private void UpdateThinkingNavWidget(bool advanceTimers)
    {
        if (_thinkingNavCanvas == null || _thinkingNavRect == null) return;

        var vr = VRRigManager.Instance;
        bool show = vr != null && PlayerThinking.IsThinking && !MainMenuManager.IsInMenu;
        SetWidgetActiveSafe(_thinkingNavCanvas.gameObject, show);
        if (!show) return;

        float dist = Mathf.Clamp(VRConfig.HudDistance.Value, 0.7f, 2.5f);
        float navScale = 0.00086f * VRConfig.HudScale.Value * (dist / 1.35f);
        float verticalDrop = -0.53f * VRConfig.HudScale.Value * (dist / 1.35f);

        Vector3 navPos = _menuAnchorPos + (_menuAnchorRot * new Vector3(0f, verticalDrop, -0.025f));
        _thinkingNavRect.SetPositionAndRotation(navPos, _menuAnchorRot);
        _thinkingNavRect.localScale = Vector3.one * navScale;

        if (!advanceTimers) return;

        float dtLerp = Mathf.Clamp01(Time.unscaledDeltaTime * 15f);
        for (int i = 0; i < _thinkingNavButtons.Count; i++)
        {
            WristButtonVisual b = _thinkingNavButtons[i];
            if (b.RootObj == null || b.Rect == null) continue;

            bool hovered = _currentHoverObj != null &&
                           (_currentHoverObj == b.RootObj || _currentHoverObj.transform.IsChildOf(b.RootObj.transform));
            float targetScale = hovered ? 1.07f : 1.0f;
            b.Rect.localScale = Vector3.Lerp(b.Rect.localScale, Vector3.one * targetScale, dtLerp);
            if (b.Border != null)
            {
                Color targetBorder = hovered
                    ? new Color(1.0f, 0.90f, 0.35f, 1.0f)
                    : new Color(0.38f, 0.90f, 1.0f, 0.88f);
                b.Border.color = Color.Lerp(b.Border.color, targetBorder, dtLerp);
            }
        }
    }

    /// <summary>
    /// Returns true when the VR player is actively looking at their in-game 3D UI
    /// (Wrist Menu, Wrist HUD, Modal Menu, or glancing down/up directly at the 3D WorldSpace HUD strips).
    /// Used by VRDesktopSpectatorHUD to fade the static 2D desktop UI out while looking at in-game UI
    /// and fade it back in when looking away!
    /// </summary>
    public bool IsPlayerLookingAtInGameUI()
    {
        var vr = VRRigManager.Instance;
        if (vr == null || !vr.IsVRActive) return false;

        // 1. Open Left/Right Wrist Menu
        if (WristMenuAlpha > (_gazeOnInGameUI ? 0.08f : 0.16f))
        {
            _gazeOnInGameUI = true;
            return true;
        }

        Vector3 headPos = vr.HeadWorldPos;
        Vector3 headFwd = vr.HeadWorldRot * Vector3.forward;

        // 2. Open In-Game Modal UI (Pause, Findings/Collection, NPC Dialogue, Chat)
        if (PauseManager.IsPaused || PlayerThinking.IsThinking || NpcUI.NPCDialougeOpen || ChatManager.IsTyping)
        {
            Vector3 toModal = _menuAnchorPos - headPos;
            float modalAngle = toModal.sqrMagnitude > 0.01f ? Vector3.Angle(headFwd, toModal.normalized) : 0f;
            float modalThresh = _gazeOnInGameUI ? 46f : 38f;
            _gazeOnInGameUI = modalAngle < modalThresh;
            return _gazeOnInGameUI;
        }

        // 3. Wrist-Mounted Gameplay HUD mode
        if (VRConfig.WristHud.Value && vr.OffHandState.IsActive)
        {
            Quaternion wristRot = vr.OffHandGripWorldRot * Quaternion.Euler(35f, vr.IsRightDominant ? 85f : -85f, 0f);
            Vector3 wristHudPos = vr.OffHandGripWorldPos + (wristRot * new Vector3(0f, 0.06f, 0.04f));
            Vector3 toWristHud = wristHudPos - headPos;
            float dist = toWristHud.magnitude;
            float angle = dist > 0.01f ? Vector3.Angle(headFwd, toWristHud / dist) : 180f;
            float thresh = _gazeOnInGameUI ? 31f : 23f;
            _gazeOnInGameUI = dist < 1.05f && angle < thresh;
            return _gazeOnInGameUI;
        }

        // 4. Floating 3D WorldSpace HUD: check if player's gaze ray intersects or aims directly at the
        //    bottom Vitals/Inventory strip or top Money/Boss strip on the 3D HUD plane!
        float hudDist = Mathf.Clamp(VRConfig.HudDistance.Value, 0.7f, 2.5f);
        float hudScale = 0.00090f * VRConfig.HudScale.Value * (hudDist / 1.35f);
        Vector3 hudCenter = headPos + _hudSmoothedOffset;

        // Bottom Vitals + Hotbar strip sits at y ≈ -415 px on the 1920x1080 canvas; Top Money/Boss strip sits at y ≈ +430 px
        Vector3 bottomStripWorld = hudCenter + (_hudSmoothedRot * new Vector3(0f, -415f * hudScale, 0f));
        Vector3 topRightMoneyWorld = hudCenter + (_hudSmoothedRot * new Vector3(680f * hudScale, 430f * hudScale, 0f));

        float angleToBottomHud = Vector3.Angle(headFwd, (bottomStripWorld - headPos).normalized);
        float angleToTopMoney = Vector3.Angle(headFwd, (topRightMoneyWorld - headPos).normalized);

        bool bossActive = false;
        try { bossActive = PlayerUI.BossCanvasActive && BossManager.Boss != null; } catch { /* safe */ }
        float angleToTopBoss = bossActive
            ? Vector3.Angle(headFwd, (hudCenter + (_hudSmoothedRot * new Vector3(0f, 430f * hudScale, 0f)) - headPos).normalized)
            : 180f;

        float enterDeg = 14.5f;
        float exitDeg = 19.5f;
        float activeThresh = _gazeOnInGameUI ? exitDeg : enterDeg;

        _gazeOnInGameUI = angleToBottomHud < activeThresh ||
                          angleToTopMoney < activeThresh ||
                          angleToTopBoss < activeThresh;
        return _gazeOnInGameUI;
    }

    private void UpdateResetPositionWidget(bool advanceTimers)
    {
        if (_resetPromptCanvas == null || _resetPromptGroup == null) return;

        var vr = VRRigManager.Instance;
        bool modalMenu = vr != null && IsModalMenuOpen();
        bool inWristMainMenu = IsInWristMainMenuMode();

        // Hide Reset Position widget when no modal is open OR while the player is actively looking at their open Wrist Menu
        if (!modalMenu || (inWristMainMenu && WristMenuAlpha > 0.25f))
        {
            _showResetPrompt = false;
            _resetPromptPoseInit = false;
            if (_resetPromptCanvas.gameObject.activeSelf)
            {
                _resetPromptGroup.alpha = 0f;
                _resetPromptCanvas.gameObject.SetActive(false);
            }
            return;
        }

        float signedYawDiff = Mathf.DeltaAngle(vr.HeadWorldRot.eulerAngles.y, _menuWorldYaw);
        float absYawDiff = Mathf.Abs(signedYawDiff);

        Vector3 toMenu = _menuAnchorPos - vr.HeadWorldPos;
        float viewAngle = toMenu.sqrMagnitude > 0.01f
            ? Vector3.Angle(vr.HeadWorldRot * Vector3.forward, toMenu.normalized)
            : absYawDiff;

        float openYawThreshold = inWristMainMenu ? 65f : 46f;
        float openViewThreshold = inWristMainMenu ? 70f : 52f;

        if (!_showResetPrompt && (absYawDiff > openYawThreshold || viewAngle > openViewThreshold))
        {
            _showResetPrompt = true;
        }
        else if (_showResetPrompt && absYawDiff < 28f && viewAngle < 34f)
        {
            _showResetPrompt = false;
        }

        float targetAlpha = _showResetPrompt ? 1f : 0f;
        if (_showResetPrompt && !_resetPromptCanvas.gameObject.activeSelf)
        {
            _resetPromptCanvas.gameObject.SetActive(true);
            _resetPromptPoseInit = false;
        }

        if (advanceTimers)
        {
            _resetPromptGroup.alpha = Mathf.MoveTowards(_resetPromptGroup.alpha, targetAlpha, Time.unscaledDeltaTime * 6.5f);
        }
        _resetPromptGroup.blocksRaycasts = _resetPromptGroup.alpha > 0.15f;

        if (!_showResetPrompt && _resetPromptGroup.alpha <= 0.01f)
        {
            if (_resetPromptCanvas.gameObject.activeSelf)
            {
                _resetPromptCanvas.gameObject.SetActive(false);
            }
            return;
        }

        Vector3 headFwd = vr.HeadWorldRot * Vector3.forward;
        Vector3 clampedDir = new Vector3(headFwd.x, Mathf.Clamp(headFwd.y, -0.35f, 0.30f), headFwd.z);
        if (clampedDir.sqrMagnitude < 0.01f) clampedDir = Vector3.forward;
        clampedDir.Normalize();

        Vector3 desiredOffset = clampedDir * 1.05f + Vector3.down * 0.14f;
        if (!_resetPromptPoseInit)
        {
            _resetPromptHeadOffset = desiredOffset;
            _resetPromptPoseInit = true;
        }
        else if (advanceTimers)
        {
            _resetPromptHeadOffset = Vector3.Slerp(_resetPromptHeadOffset, desiredOffset, Mathf.Clamp01(Time.unscaledDeltaTime * 6f));
        }

        Vector3 promptWorldPos = vr.HeadWorldPos + _resetPromptHeadOffset;
        Vector3 lookDir = promptWorldPos - vr.HeadWorldPos;
        lookDir.y *= 0.65f;
        Quaternion promptRot = lookDir.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(lookDir.normalized, Vector3.up)
            : vr.HeadWorldRot;

        _resetPromptRootRect.SetPositionAndRotation(promptWorldPos, promptRot);

        if (_resetLabelText != null)
        {
            string targetNoun = inWristMainMenu ? "Title" : "Menu";
            string label = absYawDiff > 150f
                ? "Reset Position"
                : (signedYawDiff < 0f ? $"\u2190 Face {targetNoun}" : $"Face {targetNoun} \u2192");
            if (_resetLabelText.text != label)
            {
                _resetLabelText.text = label;
            }
        }

        bool hovered = _currentHoverObj != null &&
                       (_currentHoverObj == _resetButtonObj || _currentHoverObj.transform.IsChildOf(_resetButtonObj.transform));
        if (advanceTimers)
        {
            float targetScale = hovered ? 1.15f : 1.0f;
            _resetButtonVisualRect.localScale = Vector3.Lerp(_resetButtonVisualRect.localScale, Vector3.one * targetScale, Mathf.Clamp01(Time.unscaledDeltaTime * 14f));
            if (_resetRingImg != null)
            {
                Color targetRing = hovered ? new Color(1.0f, 0.90f, 0.35f, 1.0f) : new Color(0.38f, 0.88f, 1.0f, 0.95f);
                _resetRingImg.color = Color.Lerp(_resetRingImg.color, targetRing, Mathf.Clamp01(Time.unscaledDeltaTime * 14f));
            }
        }
    }

    private void UpdateLaserPointerAndEvents()
    {
        var vr = VRRigManager.Instance;
        var frame = vr.CurrentFrame;
        bool inWristMainMenu = IsInWristMainMenuMode();
        bool inSaveWristMenu = !inWristMainMenu && IsInSaveWristMenuMode();
        bool anyWristMenu = inWristMainMenu || inSaveWristMenu;
        bool wristMenuOpenForLaser = anyWristMenu && WristMenuAlpha > 0.15f;

        // While the Wrist Menu (Main Menu or In-Save) is open and both hands are tracked, always point with the opposite (free) hand!
        if (wristMenuOpenForLaser && frame.handLeft.IsActive && frame.handRight.IsActive)
        {
            _laserUseRightHand = IsLeftMenuWrist;
        }
        else
        {
            if (frame.handRight.IsActive && (frame.handRight.trigger > 0.35f || frame.handRight.PrimaryButton) && !_laserUseRightHand)
                _laserUseRightHand = true;
            else if (frame.handLeft.IsActive && (frame.handLeft.trigger > 0.35f || frame.handLeft.PrimaryButton) && _laserUseRightHand)
                _laserUseRightHand = false;
            else if (!_laserUseRightHand && !frame.handLeft.IsActive && frame.handRight.IsActive)
                _laserUseRightHand = true;
            else if (_laserUseRightHand && !frame.handRight.IsActive && frame.handLeft.IsActive)
                _laserUseRightHand = false;
        }

        ControllerStateData laserHand = _laserUseRightHand ? frame.handRight : frame.handLeft;
        Vector3 rayOrigin = _laserUseRightHand ? vr.RightAimWorldPos : vr.LeftAimWorldPos;
        Quaternion rayRot = _laserUseRightHand ? vr.RightAimWorldRot : vr.LeftAimWorldRot;
        Vector3 rayDir = rayRot * Vector3.forward;

        bool modalOpen = IsModalMenuOpen();

        if (EventSystem.current == null || (!modalOpen && !wristMenuOpenForLaser && Player.LocalPlayer != null && !Cursor.visible))
        {
            ClearHoverAndPress();
            SetLaserVisible(false, Vector3.zero, Vector3.zero);
            IsLaserActiveOnUI = false;
            IsConsumingRightStick = modalOpen;
            return;
        }

        // Align _uiEventCamera straight down the controller laser ray for exact UI raycasting
        _uiEventCamera.transform.SetPositionAndRotation(rayOrigin, rayRot);
        Vector2 centerScreenPos = (Vector2)_uiEventCamera.ViewportToScreenPoint(new Vector3(0.5f, 0.5f, 0f));

        _pointerData ??= new PointerEventData(EventSystem.current);
        _pointerData.Reset();
        _pointerData.button = PointerEventData.InputButton.Left;
        _pointerData.position = centerScreenPos;
        _pointerData.scrollDelta = frame.handRight.IsActive ? new Vector2(0f, frame.handRight.stickY * 3f) : Vector2.zero;

        _raycastResults.Clear();
        EventSystem.current.RaycastAll(_pointerData, _raycastResults);

        RaycastResult hitResult = default;
        foreach (var r in _raycastResults)
        {
            if (r.gameObject != null && r.distance < 8.0f)
            {
                hitResult = r;
                break;
            }
        }

        bool hitUI = hitResult.gameObject != null;
        float defaultLaserLen = anyWristMenu ? 0.65f : 2.2f;
        Vector3 hitWorldPos = hitUI
            ? (hitResult.worldPosition != Vector3.zero ? hitResult.worldPosition : rayOrigin + rayDir * hitResult.distance)
            : (rayOrigin + rayDir * defaultLaserLen);

        // On the Wrist Menu, display the laser beam when the wrist menu is fading in/open or hovering UI (e.g. Reset Position)
        bool showLaser = inWristMainMenu
            ? (WristMenuAlpha > 0.15f || hitUI)
            : (wristMenuOpenForLaser || modalOpen || hitUI);

        SetLaserVisible(showLaser, rayOrigin, hitWorldPos);
        IsLaserActiveOnUI = showLaser;
        IsConsumingRightStick = showLaser;

        _pointerData.pointerCurrentRaycast = hitResult;
        if (!_wasLaserTriggerPressed)
        {
            // Keep pressEventCamera populated during hover so ColorPicker.OnPointerMove works before clicking!
            _pointerData.pointerPressRaycast = hitResult;
        }

        GameObject newHover = showLaser ? hitResult.gameObject : null;

        // Handle Hover Enter / Exit / Move (needed by UIButton & ColorPicker!)
        if (newHover != _currentHoverObj)
        {
            if (_currentHoverObj != null)
            {
                ExecuteEvents.ExecuteHierarchy(_currentHoverObj, _pointerData, ExecuteEvents.pointerExitHandler);
            }
            _currentHoverObj = newHover;
            if (_currentHoverObj != null)
            {
                ExecuteEvents.ExecuteHierarchy(_currentHoverObj, _pointerData, ExecuteEvents.pointerEnterHandler);
                OpenXRBridge.TriggerHaptic(_laserUseRightHand, 0.08f, 0.01f, 180f);
            }
        }
        if (_currentHoverObj != null)
        {
            ExecuteEvents.ExecuteHierarchy(_currentHoverObj, _pointerData, ExecuteEvents.pointerMoveHandler);
        }

        // Scroll wheel from Right Stick Y when hovering scrollable UI
        if (_currentHoverObj != null && Mathf.Abs(_pointerData.scrollDelta.y) > 0.4f)
        {
            GameObject scrollHandler = ExecuteEvents.GetEventHandler<IScrollHandler>(_currentHoverObj);
            if (scrollHandler != null)
            {
                ExecuteEvents.Execute(scrollHandler, _pointerData, ExecuteEvents.scrollHandler);
            }
        }

        // Trigger Click / Drag
        bool triggerPressed = showLaser && laserHand.IsActive && (laserHand.TriggerClick || laserHand.PrimaryButton);

        if (triggerPressed && !_wasLaserTriggerPressed)
        {
            _pointerData.eligibleForClick = true;
            _pointerData.delta = Vector2.zero;
            _pointerData.dragging = false;
            _pointerData.useDragThreshold = true;
            _pointerData.pressPosition = centerScreenPos;
            _pointerData.pointerPressRaycast = hitResult;

            if (newHover != null)
            {
                GameObject pressHandler = ExecuteEvents.ExecuteHierarchy(newHover, _pointerData, ExecuteEvents.pointerDownHandler);
                if (pressHandler == null)
                {
                    pressHandler = ExecuteEvents.GetEventHandler<IPointerClickHandler>(newHover);
                }

                _currentPressObj = pressHandler;
                _pointerData.pointerPress = pressHandler;
                _pointerData.rawPointerPress = newHover;

                _currentDragObj = ExecuteEvents.GetEventHandler<IDragHandler>(newHover);
                _pointerData.pointerDrag = _currentDragObj;
                if (_currentDragObj != null)
                {
                    ExecuteEvents.Execute(_currentDragObj, _pointerData, ExecuteEvents.initializePotentialDrag);
                    ExecuteEvents.Execute(_currentDragObj, _pointerData, ExecuteEvents.beginDragHandler);
                    _pointerData.dragging = true;
                }

                // Open Steam Overlay Virtual Keyboard automatically when clicking a TMP_InputField in VR!
                TMP_InputField tmpInput = newHover.GetComponentInParent<TMP_InputField>();
                if (tmpInput != null)
                {
                    tmpInput.ActivateInputField();
                    TryOpenSteamVirtualKeyboard();
                }

                OpenXRBridge.TriggerHaptic(_laserUseRightHand, 0.22f, 0.02f, 175f);
            }
        }
        else if (triggerPressed && _wasLaserTriggerPressed)
        {
            if (_currentDragObj != null && _pointerData.dragging)
            {
                ExecuteEvents.Execute(_currentDragObj, _pointerData, ExecuteEvents.dragHandler);
            }
        }
        else if (!triggerPressed && _wasLaserTriggerPressed)
        {
            if (_currentPressObj != null)
            {
                ExecuteEvents.Execute(_currentPressObj, _pointerData, ExecuteEvents.pointerUpHandler);
                GameObject clickCandidate = ExecuteEvents.GetEventHandler<IPointerClickHandler>(newHover);
                if (_currentPressObj == clickCandidate)
                {
                    ExecuteEvents.Execute(_currentPressObj, _pointerData, ExecuteEvents.pointerClickHandler);
                }
            }

            if (_currentDragObj != null && _pointerData.dragging)
            {
                ExecuteEvents.Execute(_currentDragObj, _pointerData, ExecuteEvents.endDragHandler);
            }

            _pointerData.dragging = false;
            _pointerData.pointerDrag = null;
            _pointerData.pointerPress = null;
            _currentPressObj = null;
            _currentDragObj = null;
        }

        _wasLaserTriggerPressed = triggerPressed;
    }

    private void ClearHoverAndPress()
    {
        if (_currentHoverObj != null && _pointerData != null)
        {
            ExecuteEvents.ExecuteHierarchy(_currentHoverObj, _pointerData, ExecuteEvents.pointerExitHandler);
        }
        _currentHoverObj = null;
        _currentPressObj = null;
        _currentDragObj = null;
        _wasLaserTriggerPressed = false;
    }

    private void SetLaserVisible(bool visible, Vector3 start, Vector3 end)
    {
        if (_laserLine != null)
        {
            _laserLine.enabled = visible;
            if (visible)
            {
                _laserLine.SetPosition(0, start);
                _laserLine.SetPosition(1, end);
            }
        }
        if (_laserDot != null)
        {
            if (_laserDot.activeSelf != visible) _laserDot.SetActive(visible);
            if (visible) _laserDot.transform.position = end;
        }
    }

    private static void TryOpenSteamVirtualKeyboard()
    {
        try
        {
            SteamUtils.ShowFloatingGamepadTextInput(EFloatingGamepadTextInputMode.k_EFloatingGamepadTextInputModeModeSingleLine, 0, 0, 800, 200);
        }
        catch
        {
            // Ignore if Steam overlay disabled
        }
    }

    /// <summary>
    /// Projects a 3D world position along the VR HMD sightline onto a WorldSpace UI element's parent Canvas plane!
    /// </summary>
    public static Vector3 ProjectWorldOntoCanvas(Transform uiElement, Vector3 worldTargetPos, out bool isInFront)
    {
        isInFront = false;
        if (VRRigManager.Instance == null || uiElement == null) return Vector3.zero;

        Vector3 eyeOrigin = VRRigManager.Instance.HeadWorldPos;
        Vector3 headFwd = VRRigManager.Instance.HeadWorldRot * Vector3.forward;
        Vector3 toTarget = worldTargetPos - eyeOrigin;
        if (toTarget.sqrMagnitude < 0.0001f) return uiElement.position;

        Vector3 dir = toTarget.normalized;
        if (Vector3.Dot(dir, headFwd) <= 0.08f)
        {
            return eyeOrigin - headFwd * 2f;
        }

        Canvas parentCanvas = uiElement.GetComponentInParent<Canvas>();
        if (parentCanvas != null)
        {
            Canvas rootCanvas = parentCanvas.rootCanvas != null ? parentCanvas.rootCanvas : parentCanvas;
            Plane plane = new Plane(rootCanvas.transform.forward, rootCanvas.transform.position);
            if (plane.Raycast(new Ray(eyeOrigin, dir), out float enter) && enter > 0.05f && enter < 12f)
            {
                isInFront = true;
                return eyeOrigin + dir * enter;
            }
        }

        isInFront = true;
        return eyeOrigin + dir * VRConfig.HudDistance.Value;
    }
}

// =============================================================================
// Harmony Patches for all WorldToScreenPoint UI Scripts
// =============================================================================
public static class VRWorldToScreenUIPatches
{
    private static bool IsVRActive => VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive;

    // 1. ItemUI (Look-at hover prompt)
    private static readonly FieldInfo ItemUILookTargetField = AccessTools.Field(typeof(ItemUI), "_lookAtTarget");
    private static readonly FieldInfo ItemUITextOffsetField = AccessTools.Field(typeof(ItemUI), "_textOffset");
    private static readonly FieldInfo ItemUILookingTextField = AccessTools.Field(typeof(ItemUI), "_lookingAtText");

    [HarmonyPatch(typeof(ItemUI), "LateUpdate")]
    public static class ItemUILateUpdatePatch
    {
        public static bool Prefix(ItemUI __instance)
        {
            if (!IsVRActive) return true;
            Transform target = ItemUILookTargetField?.GetValue(__instance) as Transform;
            TextMeshProUGUI text = ItemUILookingTextField?.GetValue(__instance) as TextMeshProUGUI;
            if (target == null || text == null) return false;

            Vector3 offset = (Vector3)(ItemUITextOffsetField?.GetValue(__instance) ?? Vector3.zero);
            Vector3 projected = VRUIManager.ProjectWorldOntoCanvas(text.transform, target.position + offset, out bool inFront);
            if (inFront)
            {
                text.transform.position = projected;
            }
            return false;
        }
    }

    // 2. CloseItemsUI (Nearby item indicator dots)
    private static readonly FieldInfo CloseDotsEnabledField = AccessTools.Field(typeof(CloseItemsUI), "_dotsEnabled");
    private static readonly FieldInfo CloseDotsListField = AccessTools.Field(typeof(CloseItemsUI), "_closeItemsDots");
    private static readonly FieldInfo ClosestItemsArrField = AccessTools.Field(typeof(CloseItemsUI), "_closestItems");
    private static readonly FieldInfo NextRefreshTimeField = AccessTools.Field(typeof(CloseItemsUI), "_nextClosestItemsRefreshTime");
    private static readonly FieldInfo AliveColorField = AccessTools.Field(typeof(CloseItemsUI), "_aliveColor");
    private static readonly MethodInfo RefreshClosestMethod = AccessTools.Method(typeof(CloseItemsUI), "RefreshClosestItems");
    private static readonly MethodInfo HideDotsMethod = AccessTools.Method(typeof(CloseItemsUI), "HideDots");
    private static readonly MethodInfo ShouldHideItemMethod = AccessTools.Method(typeof(CloseItemsUI), "ShouldHideItem");

    [HarmonyPatch(typeof(CloseItemsUI), "LateUpdate")]
    public static class CloseItemsUILateUpdatePatch
    {
        public static bool Prefix(CloseItemsUI __instance)
        {
            if (!IsVRActive) return true;

            bool dotsEnabled = (bool)(CloseDotsEnabledField?.GetValue(null) ?? false);
            if (!Player.LocalPlayer || !dotsEnabled || EndGameEffects.IsShowingEndGame)
            {
                HideDotsMethod?.Invoke(__instance, null);
                return false;
            }

            float nextTime = (float)(NextRefreshTimeField?.GetValue(__instance) ?? 0f);
            if (Time.unscaledTime >= nextTime)
            {
                RefreshClosestMethod?.Invoke(__instance, null);
            }

            var dots = CloseDotsListField?.GetValue(__instance) as List<Image>;
            var items = ClosestItemsArrField?.GetValue(__instance) as Item[];
            if (dots == null || items == null) return false;

            Color aliveColor = (Color)(AliveColorField?.GetValue(__instance) ?? Color.red);
            int count = Mathf.Min(dots.Count, items.Length);

            for (int i = 0; i < count; i++)
            {
                Item item = items[i];
                bool shouldHide = item == null || (bool)(ShouldHideItemMethod?.Invoke(null, new object[] { item }) ?? true);
                if (shouldHide)
                {
                    dots[i].color = Color.clear;
                    continue;
                }

                Vector3 proj = VRUIManager.ProjectWorldOntoCanvas(dots[i].transform, item.transform.position, out bool inFront);
                if (!inFront)
                {
                    dots[i].color = Color.clear;
                    continue;
                }

                bool isAliveCreature = item.Creature != null && !item.Creature.IsDead;
                bool isWeaponOrMelee = item.Melee != null || item.Weapon != null;
                Color c = isAliveCreature
                    ? aliveColor
                    : (isWeaponOrMelee ? GameInfo.OrangeColor : (item.DeadPlayer ? Color.cyan : Color.white));

                dots[i].color = c;
                dots[i].transform.position = proj;
            }

            for (int j = count; j < dots.Count; j++)
            {
                dots[j].color = Color.clear;
            }
            return false;
        }
    }

    // 3. OnHitUI (Hitmarkers & floating damage numbers)
    private static readonly FieldInfo HitMarkersListField = AccessTools.Field(typeof(OnHitUI), "_hitMarkers");
    private static readonly FieldInfo HitMarkerPosListField = AccessTools.Field(typeof(OnHitUI), "_hitMarkerPositions");
    private static readonly FieldInfo DamageTextsListField = AccessTools.Field(typeof(OnHitUI), "_damageTexts");
    private static readonly FieldInfo DamagePosListField = AccessTools.Field(typeof(OnHitUI), "_damageTextPositions");
    private static readonly FieldInfo DamageUpsListField = AccessTools.Field(typeof(OnHitUI), "_damageUps");

    [HarmonyPatch(typeof(OnHitUI))]
    public static class OnHitUIPatches
    {
        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static bool LateUpdatePrefix(OnHitUI __instance)
        {
            if (!IsVRActive) return true;

            var markers = HitMarkersListField?.GetValue(__instance) as List<Image>;
            var markerPos = HitMarkerPosListField?.GetValue(__instance) as List<Vector3>;
            if (markers != null && markerPos != null)
            {
                for (int i = 0; i < markers.Count; i++)
                {
                    if (markers[i].transform.localScale.x > 0.01f)
                    {
                        Vector3 p = VRUIManager.ProjectWorldOntoCanvas(markers[i].transform, markerPos[i], out bool inFront);
                        if (!inFront)
                        {
                            LeanTween.cancel(markers[i].gameObject);
                            markers[i].transform.localScale = Vector3.zero;
                        }
                        else
                        {
                            markers[i].transform.position = p;
                        }
                    }
                }
            }

            var dmgTexts = DamageTextsListField?.GetValue(__instance) as List<TextMeshProUGUI>;
            var dmgPos = DamagePosListField?.GetValue(__instance) as List<Vector3>;
            var dmgUps = DamageUpsListField?.GetValue(__instance) as List<float>;
            if (dmgTexts != null && dmgPos != null && dmgUps != null)
            {
                for (int j = 0; j < dmgTexts.Count; j++)
                {
                    if (dmgTexts[j].transform.localScale.x > 0.01f)
                    {
                        Vector3 p = VRUIManager.ProjectWorldOntoCanvas(dmgTexts[j].transform, dmgPos[j], out bool inFront);
                        if (!inFront)
                        {
                            LeanTween.cancel(dmgTexts[j].gameObject);
                            dmgTexts[j].transform.localScale = Vector3.zero;
                        }
                        else
                        {
                            dmgTexts[j].transform.position = p + dmgTexts[j].transform.up * (dmgUps[j] * 0.001f);
                        }
                    }
                }
            }
            return false;
        }

        [HarmonyPatch("AddDamageNumber")]
        [HarmonyPostfix]
        public static void AddDamageNumberPostfix(OnHitUI __instance, Vector3 pos, bool killed)
        {
            if (!IsVRActive) return;
            var dmgTexts = DamageTextsListField?.GetValue(__instance) as List<TextMeshProUGUI>;
            if (dmgTexts == null || dmgTexts.Count == 0) return;

            int curIdx = (int)(AccessTools.Field(typeof(OnHitUI), "_curDamageText")?.GetValue(__instance) ?? 0);
            if (curIdx >= 0 && curIdx < dmgTexts.Count)
            {
                Vector3 p = VRUIManager.ProjectWorldOntoCanvas(dmgTexts[curIdx].transform, pos, out bool inFront);
                dmgTexts[curIdx].color = inFront ? (killed ? GameInfo.RedColor : Color.white) : Color.clear;
                if (inFront) dmgTexts[curIdx].transform.position = p;
            }
        }
    }

    // 4. FishingUI (CaughtFishUI popup)
    private static readonly FieldInfo CaughtFishPosField = AccessTools.Field(typeof(FishingUI), "_caughtFishPos");
    private static readonly FieldInfo CaughtFishTextField = AccessTools.Field(typeof(FishingUI), "_caughtFishText");
    private static readonly FieldInfo CaughtFishHolderField = AccessTools.Field(typeof(FishingUI), "_caughtFishHolder");

    [HarmonyPatch(typeof(FishingUI))]
    public static class FishingUIPatches
    {
        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static bool LateUpdatePrefix(FishingUI __instance)
        {
            if (!IsVRActive) return true;
            TextMeshProUGUI txt = CaughtFishTextField?.GetValue(__instance) as TextMeshProUGUI;
            if (txt == null) return false;

            Vector3 worldPos = (Vector3)(CaughtFishPosField?.GetValue(__instance) ?? Vector3.zero);
            Vector3 proj = VRUIManager.ProjectWorldOntoCanvas(txt.transform, worldPos, out bool inFront);
            txt.transform.position = proj;
            txt.color = inFront ? Color.white : Color.clear;
            return false;
        }

        [HarmonyPatch("CaughtFishUI")]
        [HarmonyPostfix]
        public static void CaughtFishUIPostfix(FishingUI __instance, Vector3 pos)
        {
            if (!IsVRActive) return;
            GameObject holder = CaughtFishHolderField?.GetValue(__instance) as GameObject;
            if (holder != null)
            {
                Vector3 proj = VRUIManager.ProjectWorldOntoCanvas(holder.transform, pos, out _);
                holder.transform.position = proj;
            }
        }
    }

    // 5. NpcUI (NPC speech bubbles)
    private static readonly FieldInfo NpcTargetField = AccessTools.Field(typeof(NpcUI), "_npcTarget");
    private static readonly FieldInfo NpcBgField = AccessTools.Field(typeof(NpcUI), "_npcBackground");
    private static readonly MethodInfo HideNpcCanvasMethod = AccessTools.Method(typeof(NpcUI), "HideNpcCanvas");

    [HarmonyPatch(typeof(NpcUI))]
    public static class NpcUIPatches
    {
        [HarmonyPatch("SetNpcText")]
        [HarmonyPostfix]
        public static void SetNpcTextPostfix(NpcUI __instance, Transform target)
        {
            if (!IsVRActive || target == null) return;
            Image bg = NpcBgField?.GetValue(__instance) as Image;
            if (bg != null)
            {
                bg.transform.position = VRUIManager.ProjectWorldOntoCanvas(bg.transform, target.position, out _);
            }
        }

        [HarmonyPatch("UpdateNpcTextPosition")]
        [HarmonyPrefix]
        public static bool UpdatePosPrefix(NpcUI __instance)
        {
            if (!IsVRActive) return true;
            if (!NpcUI.NPCDialougeOpen) return false;

            Transform target = NpcTargetField?.GetValue(__instance) as Transform;
            Image bg = NpcBgField?.GetValue(__instance) as Image;
            if (target == null || bg == null)
            {
                HideNpcCanvasMethod?.Invoke(__instance, null);
                return false;
            }

            Vector3 proj = VRUIManager.ProjectWorldOntoCanvas(bg.transform, target.position, out bool inFront);
            if (!inFront)
            {
                HideNpcCanvasMethod?.Invoke(__instance, null);
            }
            else
            {
                bg.transform.position = proj;
            }
            return false;
        }
    }

    // 6. SwapOutfitButtons (Main Menu Character Customizer arrows)
    private static readonly FieldInfo HatRectField = AccessTools.Field(typeof(SwapOutfitButtons), "_hatRect");
    private static readonly FieldInfo AccRectField = AccessTools.Field(typeof(SwapOutfitButtons), "_accessoryRect");
    private static readonly FieldInfo OutfitRectField = AccessTools.Field(typeof(SwapOutfitButtons), "_outfitRect");

    [HarmonyPatch(typeof(SwapOutfitButtons), "Update")]
    public static class SwapOutfitButtonsUpdatePatch
    {
        public static bool Prefix(SwapOutfitButtons __instance)
        {
            if (!IsVRActive) return true;

            RectTransform hat = HatRectField?.GetValue(__instance) as RectTransform;
            RectTransform acc = AccRectField?.GetValue(__instance) as RectTransform;
            RectTransform outfit = OutfitRectField?.GetValue(__instance) as RectTransform;

            if (hat != null && LocalSkin.HatTarget != null)
                hat.position = VRUIManager.ProjectWorldOntoCanvas(hat, LocalSkin.HatTarget.position, out _);
            if (acc != null && LocalSkin.AccessoryTarget != null)
                acc.position = VRUIManager.ProjectWorldOntoCanvas(acc, LocalSkin.AccessoryTarget.position, out _);
            if (outfit != null && LocalSkin.OutfitTarget != null)
                outfit.position = VRUIManager.ProjectWorldOntoCanvas(outfit, LocalSkin.OutfitTarget.position, out _);

            return false;
        }
    }

    // 7. WeaponUI (Suppress fullscreen Sniper UI in VR)
    [HarmonyPatch(typeof(WeaponUI), "ToggleSniperUI")]
    public static class WeaponUIToggleSniperPatch
    {
        public static void Prefix(ref bool to)
        {
            if (IsVRActive && to)
            {
                to = false;
            }
        }
    }
}
