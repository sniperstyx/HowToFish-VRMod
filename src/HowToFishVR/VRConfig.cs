using BepInEx.Configuration;

namespace HowToFishVR;

public enum VRTurnMode
{
    Snap,
    Smooth
}

public enum VRMoveOrientation
{
    HMD,
    LeftController
}

public enum VRDominantHand
{
    Right,
    Left
}

public enum VRDesktopMirrorMode
{
    LeftEye,
    RightEye,
    Disabled
}

public static class VRConfig
{
    public static ConfigEntry<bool> EnableVR;
    public static ConfigEntry<float> RenderScale;
    public static ConfigEntry<int> MsaaSamples;
    public static ConfigEntry<VRDesktopMirrorMode> DesktopMirror;

    public static ConfigEntry<VRTurnMode> TurnMode;
    public static ConfigEntry<float> SnapTurnAngle;
    public static ConfigEntry<float> SmoothTurnSpeed;
    public static ConfigEntry<VRMoveOrientation> MoveOrientation;
    public static ConfigEntry<VRDominantHand> DominantHand;

    public static ConfigEntry<bool> SeatedMode;
    public static ConfigEntry<float> SeatedHeightOffset;
    public static ConfigEntry<bool> ComfortVignetteOnMove;
    public static ConfigEntry<float> MenuSimulationPace;

    public static ConfigEntry<bool> PhysicalFishingCast;
    public static ConfigEntry<float> CastVelocityMultiplier;
    public static ConfigEntry<bool> PhysicalFishingReel;
    public static ConfigEntry<bool> TwoHandedWeapons;
    public static ConfigEntry<bool> AutoReloadWeapons;
    public static ConfigEntry<bool> PhysicalMeleeAndPunch;
    public static ConfigEntry<bool> PhysicalMouthEating;
    public static ConfigEntry<bool> PhysicalBoatControls;
    public static ConfigEntry<float> ThrowVelocityMultiplier;
    public static ConfigEntry<float> HapticStrength;

    public static ConfigEntry<float> HudDistance;
    public static ConfigEntry<float> HudScale;
    public static ConfigEntry<bool> WristHud;
    public static ConfigEntry<bool> DesktopSpectatorHUD;

    public static ConfigEntry<bool> EnableVRMultiplayerSync;

    public static void Init(ConfigFile cfg)
    {
        EnableVR = cfg.Bind("1. General", "EnableVR", true,
            "Enable OpenXR VR mode on launch. Set to false to launch in standard flatscreen mode without uninstalling.");
        RenderScale = cfg.Bind("1. General", "RenderScale", 1.0f,
            new ConfigDescription("Per-eye OpenXR render resolution multiplier.", new AcceptableValueRange<float>(0.5f, 2.0f)));
        MsaaSamples = cfg.Bind("1. General", "MsaaSamples", 4,
            new ConfigDescription("Anti-aliasing sample count for VR eye textures (1, 2, 4, or 8).", new AcceptableValueRange<int>(1, 8)));
        DesktopMirror = cfg.Bind("1. General", "DesktopMirror", VRDesktopMirrorMode.LeftEye,
            "Which eye view to mirror to the desktop game window.");

        TurnMode = cfg.Bind("2. Locomotion & Comfort", "TurnMode", VRTurnMode.Snap,
            "Right thumbstick turning style (Snap or Smooth).");
        SnapTurnAngle = cfg.Bind("2. Locomotion & Comfort", "SnapTurnAngle", 45f,
            new ConfigDescription("Degrees rotated per snap turn.", new AcceptableValueRange<float>(15f, 90f)));
        SmoothTurnSpeed = cfg.Bind("2. Locomotion & Comfort", "SmoothTurnSpeed", 135f,
            new ConfigDescription("Degrees per second when TurnMode is Smooth.", new AcceptableValueRange<float>(45f, 360f)));
        MoveOrientation = cfg.Bind("2. Locomotion & Comfort", "MoveOrientation", VRMoveOrientation.HMD,
            "Reference forward direction for left-stick movement (HMD or LeftController).");
        DominantHand = cfg.Bind("2. Locomotion & Comfort", "DominantHand", VRDominantHand.Right,
            "Primary hand used to hold tools, rods, weapons, and items.");
        SeatedMode = cfg.Bind("2. Locomotion & Comfort", "SeatedMode", false,
            "Calibrate eye height automatically to standing character height when playing seated.");
        SeatedHeightOffset = cfg.Bind("2. Locomotion & Comfort", "SeatedHeightOffset", 0.0f,
            new ConfigDescription("Additional vertical camera height offset in meters.", new AcceptableValueRange<float>(-0.6f, 0.6f)));
        ComfortVignetteOnMove = cfg.Bind("2. Locomotion & Comfort", "ComfortVignetteOnMove", false,
            "Show a subtle peripheral comfort vignette during fast artificial locomotion.");
        MenuSimulationPace = cfg.Bind("2. Locomotion & Comfort", "MenuSimulationPace", 0.20f,
            new ConfigDescription("Background boat and ocean wave speed multiplier on the Main Menu before entering a game (lower = slower and less dizzying).",
                new AcceptableValueRange<float>(0.05f, 1.0f)));

        PhysicalFishingCast = cfg.Bind("3. VR Interactions", "PhysicalFishingCast", true,
            "Allow 6DOF physical rod-whip casting (hold Left Trigger to open bail, swing rod, release to cast) alongside button fallback.");
        CastVelocityMultiplier = cfg.Bind("3. VR Interactions", "CastVelocityMultiplier", 1.35f,
            new ConfigDescription("Multiplier applied to physical rod tip velocity when casting.", new AcceptableValueRange<float>(0.5f, 3.0f)));
        PhysicalFishingReel = cfg.Bind("3. VR Interactions", "PhysicalFishingReel", true,
            "Allow cranking the fishing reel physically with your off-hand (hold Grip near reel crank and rotate) alongside Right Trigger reeling.");
        TwoHandedWeapons = cfg.Bind("3. VR Interactions", "TwoHandedWeapons", true,
            "Allow stabilizing firearms with your off-hand grip and drawing bows physically with two hands.");
        AutoReloadWeapons = cfg.Bind("3. VR Interactions", "AutoReloadWeapons", false,
            "Combat & Reload Mode toggle (False = MANUAL VR Mode, True = AUTOMATIC Mode). In Manual VR Mode (default), firearms use full Pavlov/H3VR multi-stage manual reloading (eject mag/open breech, grab ammo from pouch & insert, rack slide/bolt/pump) and punches/melee knives require physically swinging your arms in real life. In Automatic Mode, weapons reload automatically/with a button and trigger attacks work like desktop mode. Can be toggled live on your VR Wrist Menu.");
        PhysicalMeleeAndPunch = cfg.Bind("3. VR Interactions", "PhysicalMeleeAndPunch", true,
            "Trigger melee swings and bare-handed punches by physically swinging/jabbing your VR controllers.");
        PhysicalMouthEating = cfg.Bind("3. VR Interactions", "PhysicalMouthEating", true,
            "Automatically eat/drink held creatures or beverages when brought near your mouth in VR.");
        PhysicalBoatControls = cfg.Bind("3. VR Interactions", "PhysicalBoatControls", true,
            "Allow grabbing the boat's physical steering wheel and throttle lever with your VR hands.");
        ThrowVelocityMultiplier = cfg.Bind("3. VR Interactions", "ThrowVelocityMultiplier", 1.35f,
            new ConfigDescription("Multiplier for physical 6DOF controller throwing velocity.", new AcceptableValueRange<float>(0.5f, 3.0f)));
        HapticStrength = cfg.Bind("3. VR Interactions", "HapticStrength", 1.0f,
            new ConfigDescription("Global controller haptic vibration multiplier.", new AcceptableValueRange<float>(0.0f, 2.0f)));

        HudDistance = cfg.Bind("4. UI & HUD", "HudDistance", 1.35f,
            new ConfigDescription("Distance in meters of the floating 3D WorldSpace UI canvas.", new AcceptableValueRange<float>(0.7f, 2.5f)));
        HudScale = cfg.Bind("4. UI & HUD", "HudScale", 1.0f,
            new ConfigDescription("Scale multiplier for the 3D WorldSpace UI canvas.", new AcceptableValueRange<float>(0.6f, 1.6f)));
        WristHud = cfg.Bind("4. UI & HUD", "WristHud", false,
            "Attach the in-game player HUD (health/hotbar) to your non-dominant wrist instead of floating ahead.");
        DesktopSpectatorHUD = cfg.Bind("4. UI & HUD", "DesktopSpectatorHUD", true,
            "Render a crisp Half-Life: Alyx style static 2D spectator UI on the desktop window (for streaming/recording) that fades out when you look at your in-game VR UI and fades back in when you look away.");

        EnableVRMultiplayerSync = cfg.Bind("5. Multiplayer", "EnableVRMultiplayerSync", true,
            "Sync full 6DOF dual-hand poses, finger curls, and head roll with other VR players over Steam P2P channel 77 (100% compatible with vanilla hosts and clients).");
    }
}
