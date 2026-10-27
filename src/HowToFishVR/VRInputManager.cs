using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace HowToFishVR;

public class VRInputManager : MonoBehaviour
{
    public static VRInputManager Instance { get; private set; }

    public Gamepad VirtualGamepad => _vrGamepad;

    private Gamepad _vrGamepad;
    private PlayerInput _cachedPlayerInput;
    private float _nextSchemePairCheckTime;
    private readonly Dictionary<string, uint> _actionButtonMaskCache = new Dictionary<string, uint>(StringComparer.Ordinal);

    // Edge-detection states
    private bool _prevLeftPrimary;
    private bool _prevLeftSecondary;
    private bool _prevRightPrimary;
    private bool _prevRightSecondary;
    private bool _prevLeftMenu;
    private bool _prevRightStickClick;

    private bool _dropHoldActive;
    private float _leftSecondaryDownTime;
    private bool _leftSecondaryHolsterTriggered;
    private float _rightStickHoldTime;
    private bool _recenterTriggeredOnHold;
    private bool _rightStickSlotReady = true;
    private bool _thinkingPageStickReady = true;

    private const BindingFlags AnyInstanceFlags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;

    // Cached reflection into PlayerInventory for quick VR slot/bait cycling
    private static readonly MethodInfo LocalTrySelectSlotMethod =
        typeof(PlayerInventory).GetMethod("LocalTrySelectSlot", AnyInstanceFlags);
    private static readonly FieldInfo LocalCurSlotField =
        typeof(PlayerInventory).GetField("_localCurSlot", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo GetTotalSlotsMethod =
        typeof(PlayerInventory).GetMethod("GetTotalSlots", AnyInstanceFlags);
    private static readonly MethodInfo ChangeBaitInputMethod =
        typeof(PlayerInventory).GetMethod("ChangeBaitInput", AnyInstanceFlags);

    // Cached reflection into PlayerHolding & Boat for reliable VR Drop / Charge-Throw / Exit-Boat
    private static readonly MethodInfo DropInputMethod =
        typeof(PlayerHolding).GetMethod("DropInput", AnyInstanceFlags);
    private static readonly MethodInfo DropInputCanceledMethod =
        typeof(PlayerHolding).GetMethod("DropInputCanceled", AnyInstanceFlags) ??
        typeof(PlayerHolding).GetMethod("DropInputCancelled", AnyInstanceFlags);
    private static readonly MethodInfo BoatStopDrivingInputMethod =
        typeof(Boat).GetMethod("StopDrivingInput", AnyInstanceFlags);

    private static readonly FieldInfo PlayerThinkingField =
        typeof(Player).GetField("_thinking", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly MethodInfo ThinkingInputMethod =
        typeof(PlayerThinking).GetMethod("ThinkingInput", AnyInstanceFlags);

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void EnsureVirtualGamepad()
    {
        if (_vrGamepad != null && _vrGamepad.added) return;
        try
        {
            _vrGamepad = InputSystem.AddDevice<Gamepad>("HTFVRGamepad");
            _nextSchemePairCheckTime = 0f;
            Plugin.Log.LogInfo("Registered virtual InputSystem Gamepad 'HTFVRGamepad' for VR controllers.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Failed to register virtual Gamepad: {ex}");
        }
    }

    private void EnsurePlayerInputPairedToVRGamepad()
    {
        if (_vrGamepad == null || !_vrGamepad.added) return;
        if (Time.unscaledTime < _nextSchemePairCheckTime) return;
        _nextSchemePairCheckTime = Time.unscaledTime + 0.5f;

        try
        {
            PlayerInput pi = GameInfo.Input;
            if (pi == null || !pi.enabled) return;

            if (_cachedPlayerInput != pi)
            {
                _cachedPlayerInput = pi;
                _actionButtonMaskCache.Clear();
            }

            bool pairedToVr = string.Equals(pi.currentControlScheme, "Controller", StringComparison.OrdinalIgnoreCase);
            if (pairedToVr)
            {
                pairedToVr = false;
                var devices = pi.devices;
                for (int i = 0; i < devices.Count; i++)
                {
                    if (devices[i] == _vrGamepad)
                    {
                        pairedToVr = true;
                        break;
                    }
                }
            }

            if (!pairedToVr)
            {
                pi.SwitchCurrentControlScheme("Controller", _vrGamepad);
                _actionButtonMaskCache.Clear();
                VRControllerIcons.RefreshAllPromptUIs();
                Plugin.Log.LogInfo("Paired GameInfo.Input ('Controller' scheme) to HTFVRGamepad and refreshed VR button icons.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogDebug($"EnsurePlayerInputPairedToVRGamepad info: {ex.Message}");
        }
    }

    private uint ResolveActionGamepadButtonMask(string actionName, GamepadButton fallbackButton)
    {
        if (_actionButtonMaskCache.TryGetValue(actionName, out uint cached))
        {
            return cached;
        }

        uint fallbackMask = 1u << (int)fallbackButton;
        try
        {
            PlayerInput pi = GameInfo.Input;
            InputAction action = pi != null && pi.actions != null ? pi.actions.FindAction(actionName, throwIfNotFound: false) : null;
            if (action != null)
            {
                var bindings = action.bindings;
                for (int i = 0; i < bindings.Count; i++)
                {
                    string path = bindings[i].effectivePath;
                    if (string.IsNullOrEmpty(path)) continue;
                    if (path.IndexOf("Gamepad", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    if (TryMapControlPathToGamepadButton(path, out GamepadButton btn))
                    {
                        uint mask = 1u << (int)btn;
                        _actionButtonMaskCache[actionName] = mask;
                        return mask;
                    }
                }
            }
        }
        catch
        {
            // Fallback safely
        }

        _actionButtonMaskCache[actionName] = fallbackMask;
        return fallbackMask;
    }

    private static bool TryMapControlPathToGamepadButton(string path, out GamepadButton button)
    {
        int slash = path.LastIndexOf('/');
        string control = (slash >= 0 ? path.Substring(slash + 1) : path).Trim().ToLowerInvariant();
        switch (control)
        {
            case "buttonsouth":
            case "a":
                button = GamepadButton.South;
                return true;
            case "buttoneast":
            case "b":
                button = GamepadButton.East;
                return true;
            case "buttonwest":
            case "x":
                button = GamepadButton.West;
                return true;
            case "buttonnorth":
            case "y":
                button = GamepadButton.North;
                return true;
            case "leftshoulder":
                button = GamepadButton.LeftShoulder;
                return true;
            case "rightshoulder":
                button = GamepadButton.RightShoulder;
                return true;
            case "leftstickpress":
                button = GamepadButton.LeftStick;
                return true;
            case "rightstickpress":
                button = GamepadButton.RightStick;
                return true;
            case "start":
                button = GamepadButton.Start;
                return true;
            case "select":
                button = GamepadButton.Select;
                return true;
            case "dpad/up":
            case "up":
                button = GamepadButton.DpadUp;
                return true;
            case "dpad/down":
            case "down":
                button = GamepadButton.DpadDown;
                return true;
            case "dpad/left":
            case "left":
                button = GamepadButton.DpadLeft;
                return true;
            case "dpad/right":
            case "right":
                button = GamepadButton.DpadRight;
                return true;
            default:
                button = GamepadButton.South;
                return false;
        }
    }

    private void OnDestroy()
    {
        if (_vrGamepad != null && _vrGamepad.added)
        {
            InputSystem.RemoveDevice(_vrGamepad);
            _vrGamepad = null;
        }
    }

    public void UpdateVRInputs(in VrFrameData frame)
    {
        if (!frame.handLeft.IsActive && !frame.handRight.IsActive) return;

        EnsureVirtualGamepad();
        if (_vrGamepad == null) return;

        EnsurePlayerInputPairedToVRGamepad();

        bool rightDom = VRConfig.DominantHand.Value == VRDominantHand.Right;
        ControllerStateData dom = rightDom ? frame.handRight : frame.handLeft;
        ControllerStateData off = rightDom ? frame.handLeft : frame.handRight;

        ControllerStateData left = frame.handLeft;
        ControllerStateData right = frame.handRight;

        // Long-press Right Stick Click (0.6s) -> Recenter VR view
        if (right.StickClick)
        {
            _rightStickHoldTime += Time.unscaledDeltaTime;
            if (_rightStickHoldTime >= 0.6f && !_recenterTriggeredOnHold)
            {
                _recenterTriggeredOnHold = true;
                VRRigManager.Instance?.Recenter();
            }
        }
        else
        {
            _rightStickHoldTime = 0f;
            _recenterTriggeredOnHold = false;
        }

        bool uiConsumingLaser = VRUIManager.Instance != null && VRUIManager.Instance.IsLaserActiveOnUI;
        bool inMenuOrDialogue = MainMenuManager.IsInMenu || PauseManager.IsPaused ||
                                PlayerThinking.IsThinking || NpcUI.NPCDialougeOpen || ChatManager.IsTyping;
        bool inGameplay = !MainMenuManager.IsInMenu && Player.LocalPlayer != null && !PauseManager.IsPaused;

        // Map OpenXR controls onto GamepadState so GameInfo.Input natively receives all bound Controller actions!
        GamepadState state = default;

        // Left Stick -> PlayerMove (and Boat Steering!)
        state.leftStick = left.Stick;

        // Right Stick -> VRRigManager handles Snap/Smooth turn on X (suppressed while flipping Findings pages);
        // Y scrolls UI when modal open or cycles inventory in gameplay
        state.rightStick = Vector2.zero;
        if (PlayerThinking.IsThinking)
        {
            HandleThinkingPageFlip(right.stickX);
        }
        else
        {
            _thinkingPageStickReady = true;
            if (inGameplay && !Boat.IsDrivingLocally && (VRUIManager.Instance == null || !VRUIManager.Instance.IsConsumingRightStick))
            {
                HandleRightStickInventoryScroll(right.stickY);
            }
        }

        // Triggers: Suppress gameplay fire when clicking on a WorldSpace UI button with the VR laser!
        state.rightTrigger = uiConsumingLaser ? 0f : dom.trigger;
        state.leftTrigger = uiConsumingLaser ? 0f : off.trigger;

        uint buttons = 0;

        bool leftGripChordHeld = left.IsActive && left.squeeze > 0.55f && !Boat.IsDrivingLocally;

        // Conflict-free VR Menu Chords (never touches SteamVR's Left Menu button!):
        // - Left Grip + Right A -> Toggle Findings / Collection Journal
        // - Left Grip + Right B -> Toggle Pause Menu
        bool consumedRightPrimaryByChord = false;
        bool consumedRightSecondaryByChord = false;

        if (leftGripChordHeld && !MainMenuManager.IsInMenu && Player.LocalPlayer != null)
        {
            if (right.PrimaryButton && !_prevRightPrimary && !PauseManager.IsPaused && !ChatManager.IsTyping)
            {
                TriggerJournalToggle();
                OpenXRBridge.TriggerHaptic(true, 0.22f, 0.025f, 165f);
                OpenXRBridge.TriggerHaptic(false, 0.16f, 0.020f, 150f);
                consumedRightPrimaryByChord = true;
            }

            if (right.SecondaryButton && !_prevRightSecondary && !ChatManager.IsTyping)
            {
                if (PlayerThinking.IsThinking)
                {
                    TriggerJournalToggle();
                }
                else
                {
                    PauseManager.TogglePause(!PauseManager.IsPaused);
                }
                OpenXRBridge.TriggerHaptic(true, 0.24f, 0.025f, 165f);
                OpenXRBridge.TriggerHaptic(false, 0.16f, 0.020f, 150f);
                consumedRightSecondaryByChord = true;
            }
        }

        // Right Primary (A): Jump / UI Submit (suppressed if Left Grip chord held)
        if (right.PrimaryButton && !uiConsumingLaser && !leftGripChordHeld && !consumedRightPrimaryByChord)
        {
            buttons |= ResolveActionGamepadButtonMask("PlayerJump", GamepadButton.South);
        }

        // Right Secondary (B):
        // - In Findings / Collection (PlayerThinking.IsThinking): Immediately close Findings!
        // - In Pause Menu: Step back / unpause via MainPauseScreenOrClose()
        // - In Main Menu / NPC Dialogue: UI Cancel / Back (East)
        // - While Driving Boat: Exit Boat Driving (StopDrivingInput)
        // - In Gameplay: Direct Drop / Release-Hooked-Fish (tap) & Charge-Throw (hold + swing + release)!
        if (!leftGripChordHeld && !consumedRightSecondaryByChord)
        {
            HandleRightSecondaryDropOrCancel(right, inGameplay, inMenuOrDialogue, uiConsumingLaser, ref buttons);
        }
        else if (_dropHoldActive)
        {
            CancelActiveDropHoldSafe();
        }

        // Left Primary (X) & Empty-Handed Dominant Grip:
        // - In Automatic Mode: Reload firearm
        // - In Manual VR Mode: Pavlov/H3VR Magazine Release / Breech Open!
        // - Otherwise: PickUp / Interact
        bool holdingWeapon = inGameplay && Player.LocalPlayer.Holding != null &&
                             Player.LocalPlayer.Holding.HeldItem != null &&
                             Player.LocalPlayer.Holding.HeldItem.Weapon != null &&
                             !(Player.LocalPlayer.Holding.HeldItem.Weapon is Bow);
        bool hoveringWorldObject = inGameplay && VRPlayerAndHandsPatches.HasHoveredPickupOrInteractable(Player.LocalPlayer);

        if (left.PrimaryButton)
        {
            if (holdingWeapon && (!hoveringWorldObject || left.squeeze > 0.5f))
            {
                if (!_prevLeftPrimary)
                {
                    Item held = Player.LocalPlayer.Holding.HeldItem;
                    if (held != null && held.Weapon != null)
                    {
                        if (VRConfig.AutoReloadWeapons.Value)
                        {
                            if (held.Weapon.Ammo < held.Weapon.Attachments.AmmoPerMag)
                            {
                                held.ReloadInput(default);
                                OpenXRBridge.TriggerOffHandHaptic(0.24f, 0.025f, 165f);
                            }
                        }
                        else
                        {
                            VRCombatAndBoatPatches.TriggerManualMagReleaseOrBreech(held.Weapon);
                        }
                    }
                }
            }
            else
            {
                buttons |= ResolveActionGamepadButtonMask("PlayerPickUp", GamepadButton.West);
            }
        }

        // Natural VR Grab: squeezing Dominant Grip while empty-handed & hovering a grabbable world Item picks it up!
        if (inGameplay && !uiConsumingLaser &&
            Player.LocalPlayer.Holding != null && Player.LocalPlayer.Holding.HeldItem == null &&
            dom.squeeze > 0.65f && VRPlayerAndHandsPatches.HasHoveredGrabbableItem(Player.LocalPlayer))
        {
            buttons |= ResolveActionGamepadButtonMask("PlayerPickUp", GamepadButton.West);
        }

        // Left Secondary (Y):
        // - When Findings / Collection is open: also closes Findings so neither hand can ever get stuck!
        // - Hold Left Grip + Tap Y -> Cycle Bait (or Inspect Weapon if holding Weapon!)
        // - Hold Y (0.38s) -> Holster to empty hands (InventoryNone)
        // - Tap Y -> Next Inventory Slot
        if (PlayerThinking.IsThinking)
        {
            if (left.SecondaryButton && !_prevLeftSecondary)
            {
                TriggerJournalToggle();
                OpenXRBridge.TriggerHaptic(false, 0.20f, 0.02f, 160f);
            }
        }
        else
        {
            HandleLeftSecondaryButton(left, inGameplay);
        }

        // Optional direct Menu Button passthrough (for runtimes like Oculus Link where Menu isn't eaten by SteamVR):
        // Never requires LG + Menu!
        if (left.MenuButton && left.squeeze <= 0.5f)
        {
            buttons |= ResolveActionGamepadButtonMask("Pause", GamepadButton.Start);
            if (!MainMenuManager.IsInMenu && !Player.LocalPlayer)
            {
                buttons |= 1u << (int)GamepadButton.South;
            }
        }

        // Left Stick Click:
        // - Left Grip + Stick Click -> PushToTalk
        // - Normal Stick Click -> Sprint
        if (left.StickClick)
        {
            if (left.squeeze > 0.5f)
            {
                buttons |= ResolveActionGamepadButtonMask("PushToTalk", GamepadButton.Select);
            }
            else
            {
                buttons |= ResolveActionGamepadButtonMask("PlayerSprint", GamepadButton.LeftStick);
            }
        }

        // Right Stick Click (short press < 0.50s): Crouch (resolves exact bound Crouch button on Gamepad!)
        if (right.StickClick && _rightStickHoldTime < 0.5f && !_recenterTriggeredOnHold)
        {
            buttons |= ResolveActionGamepadButtonMask("PlayerCrouch", GamepadButton.RightStick);
        }

        // Boat Fallback Boost when physical boat controls disabled
        if (Boat.IsDrivingLocally && left.squeeze > 0.65f && !VRConfig.PhysicalBoatControls.Value)
        {
            buttons |= 1u << (int)GamepadButton.LeftShoulder;
        }

        state.buttons = buttons;
        InputSystem.QueueStateEvent(_vrGamepad, state);

        _prevLeftPrimary = left.PrimaryButton;
        _prevLeftSecondary = left.SecondaryButton;
        _prevRightPrimary = right.PrimaryButton;
        _prevRightSecondary = right.SecondaryButton;
        _prevLeftMenu = left.MenuButton;
        _prevRightStickClick = right.StickClick;
    }

    private void HandleRightSecondaryDropOrCancel(
        in ControllerStateData right,
        bool inGameplay,
        bool inMenuOrDialogue,
        bool uiConsumingLaser,
        ref uint buttons)
    {
        if (inMenuOrDialogue)
        {
            if (_dropHoldActive)
            {
                CancelActiveDropHoldSafe();
            }

            // Explicit close/back handlers for Findings (PlayerThinking) and in-game Pause so B ALWAYS works!
            if (right.SecondaryButton && !_prevRightSecondary)
            {
                if (PlayerThinking.IsThinking)
                {
                    TriggerJournalToggle();
                    OpenXRBridge.TriggerHaptic(true, 0.20f, 0.02f, 165f);
                    return;
                }
                if (PauseManager.IsPaused && !MainMenuManager.IsInMenu)
                {
                    PauseManager.MainPauseScreenOrClose();
                    OpenXRBridge.TriggerHaptic(true, 0.20f, 0.02f, 165f);
                    return;
                }
            }

            if (right.SecondaryButton)
            {
                buttons |= 1u << (int)GamepadButton.East;
            }
            return;
        }

        if (Boat.IsDrivingLocally)
        {
            if (_dropHoldActive)
            {
                CancelActiveDropHoldSafe();
            }
            if (right.SecondaryButton && !_prevRightSecondary)
            {
                if (!TryInvokeStopDrivingBoat())
                {
                    buttons |= 1u << (int)GamepadButton.East;
                }
                OpenXRBridge.TriggerHaptic(true, 0.22f, 0.025f, 150f);
            }
            return;
        }

        if (!inGameplay || Player.LocalPlayer == null || Player.LocalPlayer.Holding == null)
        {
            _dropHoldActive = false;
            if (right.SecondaryButton)
            {
                buttons |= 1u << (int)GamepadButton.East;
            }
            return;
        }

        // Gameplay Drop / Release Hooked Fish / Charge-Throw!
        if (right.SecondaryButton && !_prevRightSecondary && !uiConsumingLaser)
        {
            PlayerHolding holding = Player.LocalPlayer.Holding;
            if (holding.HeldItem != null && !Player.LocalPlayer.BlockInputs)
            {
                InvokeHoldingMethodSafe(DropInputMethod, holding);
                _dropHoldActive = true;
                OpenXRBridge.TriggerHaptic(true, 0.16f, 0.018f, 145f);
            }
        }
        else if (!right.SecondaryButton && _prevRightSecondary && _dropHoldActive)
        {
            _dropHoldActive = false;
            InvokeHoldingMethodSafe(DropInputCanceledMethod, Player.LocalPlayer.Holding);
        }
    }

    private void CancelActiveDropHoldSafe()
    {
        _dropHoldActive = false;
        if (Player.LocalPlayer != null && Player.LocalPlayer.Holding != null)
        {
            InvokeHoldingMethodSafe(DropInputCanceledMethod, Player.LocalPlayer.Holding);
        }
    }

    private static void InvokeHoldingMethodSafe(MethodInfo method, PlayerHolding holding)
    {
        if (method == null || holding == null) return;
        try
        {
            int paramCount = method.GetParameters().Length;
            if (paramCount == 0)
                method.Invoke(holding, null);
            else
                method.Invoke(holding, new object[] { default(InputAction.CallbackContext) });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogDebug($"InvokeHoldingMethodSafe({method.Name}) info: {ex.Message}");
        }
    }

    private static bool TryInvokeStopDrivingBoat()
    {
        try
        {
            Boat boat = BoatManager.Boat;
            if (boat == null || BoatStopDrivingInputMethod == null) return false;
            int paramCount = BoatStopDrivingInputMethod.GetParameters().Length;
            if (paramCount == 0)
                BoatStopDrivingInputMethod.Invoke(boat, null);
            else
                BoatStopDrivingInputMethod.Invoke(boat, new object[] { default(InputAction.CallbackContext) });
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogDebug($"TryInvokeStopDrivingBoat info: {ex.Message}");
            return false;
        }
    }

    public static void TriggerDropHeldItemImmediate()
    {
        if (Player.LocalPlayer == null || Player.LocalPlayer.Holding == null) return;
        if (Boat.IsDrivingLocally)
        {
            TryInvokeStopDrivingBoat();
            return;
        }
        if (Player.LocalPlayer.Holding.HeldItem == null) return;

        InvokeHoldingMethodSafe(DropInputMethod, Player.LocalPlayer.Holding);
        InvokeHoldingMethodSafe(DropInputCanceledMethod, Player.LocalPlayer.Holding);
        OpenXRBridge.TriggerDominantHaptic(0.24f, 0.025f, 145f);
    }

    public static void TriggerJournalToggle()
    {
        try
        {
            if (Player.LocalPlayer == null) return;
            var thinking = PlayerThinkingField?.GetValue(Player.LocalPlayer) as PlayerThinking;
            if (thinking == null || ThinkingInputMethod == null) return;

            int paramCount = ThinkingInputMethod.GetParameters().Length;
            if (paramCount == 0)
                ThinkingInputMethod.Invoke(thinking, null);
            else
                ThinkingInputMethod.Invoke(thinking, new object[] { default(InputAction.CallbackContext) });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogDebug($"TriggerJournalToggle info: {ex.Message}");
        }
    }

    private void HandleThinkingPageFlip(float stickX)
    {
        if (Mathf.Abs(stickX) > 0.62f)
        {
            if (_thinkingPageStickReady)
            {
                PlayerUI.ScrollThinkingPage(stickX > 0f ? 1 : -1);
                _thinkingPageStickReady = false;
                OpenXRBridge.TriggerHaptic(true, 0.14f, 0.015f, 155f);
            }
        }
        else if (Mathf.Abs(stickX) < 0.28f)
        {
            _thinkingPageStickReady = true;
        }
    }

    private void HandleRightStickInventoryScroll(float stickY)
    {
        if (Mathf.Abs(stickY) > 0.68f)
        {
            if (_rightStickSlotReady)
            {
                CycleInventorySlot(stickY > 0f ? -1 : 1);
                _rightStickSlotReady = false;
                OpenXRBridge.TriggerHaptic(true, 0.14f, 0.015f, 150f);
            }
        }
        else if (Mathf.Abs(stickY) < 0.3f)
        {
            _rightStickSlotReady = true;
        }
    }

    private void HandleLeftSecondaryButton(in ControllerStateData left, bool inGameplay)
    {
        if (!inGameplay || Player.LocalPlayer == null || Player.LocalPlayer.Inventory == null)
        {
            return;
        }

        if (left.SecondaryButton && !_prevLeftSecondary)
        {
            _leftSecondaryDownTime = Time.unscaledTime;
            _leftSecondaryHolsterTriggered = false;

            if (left.squeeze > 0.5f)
            {
                // Chord: Left Grip + Y -> Change Bait (if holding rod or empty) or Inspect Weapon / Change Skin!
                Item held = Player.LocalPlayer.Holding?.HeldItem;
                if (held != null && (held.Weapon != null || held.Melee != null))
                {
                    held.InspectInput(default);
                }
                else
                {
                    ChangeBaitInputMethod?.Invoke(Player.LocalPlayer.Inventory, new object[] { default(InputAction.CallbackContext) });
                }
                _leftSecondaryHolsterTriggered = true;
                OpenXRBridge.TriggerHaptic(false, 0.18f, 0.02f, 160f);
            }
        }
        else if (left.SecondaryButton && !_leftSecondaryHolsterTriggered)
        {
            if (Time.unscaledTime - _leftSecondaryDownTime >= 0.38f)
            {
                // Hold Y -> Holster to empty hands (slot -1) so player can punch or swim empty-handed!
                _leftSecondaryHolsterTriggered = true;
                SelectInventorySlot(-1);
                OpenXRBridge.TriggerHaptic(false, 0.25f, 0.03f, 120f);
            }
        }
        else if (!left.SecondaryButton && _prevLeftSecondary && !_leftSecondaryHolsterTriggered)
        {
            // Short tap Y -> Cycle to next inventory slot!
            CycleInventorySlot(1);
            OpenXRBridge.TriggerHaptic(false, 0.14f, 0.015f, 150f);
        }
    }

    public static void CycleInventorySlot(int delta)
    {
        if (Player.LocalPlayer == null || Player.LocalPlayer.Inventory == null) return;
        if (Player.LocalPlayer.BlockInputs || Boat.IsDrivingLocally || Boat.WantToDrive) return;

        var inv = Player.LocalPlayer.Inventory;
        int total = (int)(GetTotalSlotsMethod?.Invoke(inv, null) ?? 3);
        if (total <= 0) return;

        int cur = (int)(LocalCurSlotField?.GetValue(inv) ?? -1);
        int next = cur + delta;
        if (next < 0) next = total - 1;
        else if (next >= total) next = 0;

        SelectInventorySlot(next);
    }

    public static void SelectInventorySlot(int slotIndex)
    {
        if (Player.LocalPlayer == null || Player.LocalPlayer.Inventory == null) return;
        if (Player.LocalPlayer.BlockInputs || Boat.IsDrivingLocally || Boat.WantToDrive) return;
        LocalTrySelectSlotMethod?.Invoke(Player.LocalPlayer.Inventory, new object[] { slotIndex });
    }
}
