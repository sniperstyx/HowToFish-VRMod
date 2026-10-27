using System;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace HowToFishVR;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.howtofish.vrmod";
    public const string PluginName = "How To Fish VR";
    public const string PluginVersion = "1.0.0";

    public static ManualLogSource Log { get; private set; }
    private Harmony _harmony;

    private void Awake()
    {
        Log = Logger;
        Log.LogInfo($"Initializing {PluginName} v{PluginVersion}...");

        VRConfig.Init(Config);

        string pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
        if (VRConfig.EnableVR.Value)
        {
            if (!OpenXRBridge.PreloadNativeLibraries(pluginDir))
            {
                Log.LogError("Failed to preload OpenXR native bridge. VR rendering will remain inactive until resolved.");
            }
        }
        else
        {
            Log.LogInfo("EnableVR is false in config; starting in standard flatscreen mode.");
        }

        _harmony = new Harmony(PluginGuid);
        ApplyAllPatchesSafely();

        VRControllerIcons.EnsureInitialized();
        VRCombatAndBoatPatches.RegisterRenderHooks();

        // Create persistent VR subsystem host object
        GameObject vrHost = new GameObject("HowToFishVR_Host");
        DontDestroyOnLoad(vrHost);
        vrHost.hideFlags = HideFlags.HideAndDontSave;

        vrHost.AddComponent<VRRigManager>();
        vrHost.AddComponent<VRInputManager>();
        vrHost.AddComponent<VRUIManager>();
        vrHost.AddComponent<VRMultiplayerSync>();

        Log.LogInfo($"{PluginName} v{PluginVersion} initialized successfully!");
    }

    private void ApplyAllPatchesSafely()
    {
        Type[] patchTypes =
        {
            typeof(VRPlayerAndHandsPatches.PlayerCameraPatches),
            typeof(VRPlayerAndHandsPatches.PlayerMovementMovePatch),
            typeof(VRPlayerAndHandsPatches.PlayerToolMovementPatches),
            typeof(VRPlayerAndHandsPatches.PlayerHandsAwakePatch),
            typeof(VRPlayerAndHandsPatches.PlayerHandsLateUpdatePatch),
            typeof(VRPlayerAndHandsPatches.PlayerHoldingPatches),

            typeof(VRFishingPatches.FishingRodUpdatePatch),
            typeof(VRFishingPatches.FishingRodReelOutCancelledPatch),
            typeof(VRFishingPatches.FishingRodResetBaitPatch),
            typeof(VRFishingPatches.BaitStrikeHapticPatch),

            typeof(VRCombatAndBoatPatches.RedDotSightLateUpdatePatch),
            typeof(VRCombatAndBoatPatches.WeaponPatches),
            typeof(VRCombatAndBoatPatches.BowUpdatePatch),
            typeof(VRCombatAndBoatPatches.MeleePatches),
            typeof(VRCombatAndBoatPatches.PlayerPunchingPatches),
            typeof(VRCombatAndBoatPatches.PlayerEatingUpdatePatch),
            typeof(VRCombatAndBoatPatches.BoatTickUpdatePatch),

            typeof(VRWorldToScreenUIPatches.ItemUILateUpdatePatch),
            typeof(VRWorldToScreenUIPatches.CloseItemsUILateUpdatePatch),
            typeof(VRWorldToScreenUIPatches.OnHitUIPatches),
            typeof(VRWorldToScreenUIPatches.FishingUIPatches),
            typeof(VRWorldToScreenUIPatches.NpcUIPatches),
            typeof(VRWorldToScreenUIPatches.SwapOutfitButtonsUpdatePatch),
            typeof(VRWorldToScreenUIPatches.WeaponUIToggleSniperPatch),

            typeof(VRMainMenuComfortPatches.SelfDrivingBoatComfortPatches),
            typeof(VRMainMenuComfortPatches.WaterManagerComfortPatch),

            typeof(SpriteManagerVRControllerIconsPatch),
            typeof(ThinkingUITogglePromptRefreshPatch),
            typeof(PlayerBodyLateUpdateVRPatch)
        };

        int applied = 0;
        foreach (Type t in patchTypes)
        {
            try
            {
                _harmony.CreateClassProcessor(t).Patch();
                applied++;
            }
            catch (Exception ex)
            {
                Log.LogError($"Failed applying Harmony patch class {t.FullName}: {ex}");
            }
        }
        Log.LogInfo($"Applied {applied}/{patchTypes.Length} Harmony patch groups cleanly.");
    }

    private void OnApplicationQuit()
    {
        OpenXRBridge.Shutdown();
    }
}
