using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

namespace HowToFishVR;

public static class VRCombatAndBoatPatches
{
    public static bool IsPhysicalMouthEating { get; private set; }

    public static bool IsManipulatingWeaponReload =>
        _holdingVirtualMag ||
        _isGrabbingGunMagazine ||
        _isGrabbingSlideOrAction;

    public static bool IsOffHandBusyWithCombatOrBoat =>
        _isPhysicalBowDrawing ||
        _wasGrabbingWheel ||
        _isGrabbingThrottle ||
        IsManipulatingWeaponReload ||
        VRPlayerAndHandsPatches.IsTwoHandingWeapon;

    // Weapon / Attachments / RedDotSight reflection
    private static readonly MethodInfo WeaponAmmoSetter = AccessTools.PropertySetter(typeof(Weapon), "Ammo");
    private static readonly FieldInfo WeaponAimPercentField = AccessTools.Field(typeof(Weapon), "_aimPercent");
    private static readonly FieldInfo WeaponIsReloadingField = AccessTools.Field(typeof(Weapon), "_isReloading");
    private static readonly FieldInfo WeaponQueueReloadField = AccessTools.Field(typeof(Weapon), "_queueReload");
    private static readonly FieldInfo WeaponHasCoolDownField = AccessTools.Field(typeof(Weapon), "_hasCoolDown");
    private static readonly FieldInfo WeaponTimeBetweenShotsField = AccessTools.Field(typeof(Weapon), "_timeBetweenShots");
    private static readonly FieldInfo WeaponProjCountField = AccessTools.Field(typeof(Weapon), "_projectileCountPerShot");
    private static readonly FieldInfo WeaponFullAutoField = AccessTools.Field(typeof(Weapon), "_fullAuto");
    private static readonly FieldInfo WeaponReloadSeqField = AccessTools.Field(typeof(Weapon), "_reloadAudioSeq");
    private static readonly FieldInfo WeaponReloadLastSeqField = AccessTools.Field(typeof(Weapon), "_reloadLastAudioSeq");

    private static readonly FieldInfo RedDotCanvasField = AccessTools.Field(typeof(RedDotSight), "_redDotCanvas");
    private static readonly FieldInfo RedDotImgField = AccessTools.Field(typeof(RedDotSight), "_redDot");
    private static readonly FieldInfo RedDotZeroDistField = AccessTools.Field(typeof(RedDotSight), "_zeroDistance");

    // Bow reflection
    private static readonly FieldInfo BowIsChargingField = AccessTools.Field(typeof(Bow), "_isCharging");
    private static readonly FieldInfo BowCurChargeField = AccessTools.Field(typeof(Bow), "_curCharge");
    private static readonly FieldInfo BowChargeAnimField = AccessTools.Field(typeof(Bow), "_chargeAnimation");
    private static readonly FieldInfo ToolAnimField = AccessTools.Field(typeof(Tool), "_anim");
    private static readonly MethodInfo BowBeginChargeMethod = AccessTools.Method(typeof(Bow), "BeginCharge");
    private static readonly MethodInfo BowSendChargeMethod = AccessTools.Method(typeof(Bow), "SendChargeState");

    // Melee & Punching reflection
    private static readonly FieldInfo MeleeHoldingPrimaryField = AccessTools.Field(typeof(Melee), "_holdingPrimaryInput");
    private static readonly FieldInfo MeleeQueuedAttackField = AccessTools.Field(typeof(Melee), "_queuedAttack");
    private static readonly MethodInfo MeleeCanAttackMethod = AccessTools.Method(typeof(Melee), "CanAttack");
    private static readonly MethodInfo MeleeFindTargetMethod = AccessTools.Method(typeof(Melee), "FindAttackTarget");

    private static readonly FieldInfo PunchPlayerField = AccessTools.Field(typeof(PlayerPunching), "_player");
    private static readonly FieldInfo PunchLastSideField = AccessTools.Field(typeof(PlayerPunching), "_lastPunchSide");
    private static readonly FieldInfo PunchHoldingInputField = AccessTools.Field(typeof(PlayerPunching), "_holdingPunchInput");
    private static readonly FieldInfo PunchQueuedField = AccessTools.Field(typeof(PlayerPunching), "_queuedPunch");
    private static readonly MethodInfo PunchCanPunchMethod = AccessTools.Method(typeof(PlayerPunching), "CanPunch");
    private static readonly MethodInfo PunchFindTargetMethod = AccessTools.Method(typeof(PlayerPunching), "FindPunchTarget");

    // PlayerEating reflection
    private static readonly FieldInfo EatPlayerField = AccessTools.Field(typeof(PlayerEating), "_player");
    private static readonly FieldInfo EatLocalIsEatingField = AccessTools.Field(typeof(PlayerEating), "_localIsEating");
    private static readonly MethodInfo StartEatingMethod = AccessTools.Method(typeof(PlayerEating), "StartEating");
    private static readonly MethodInfo StopEatingMethod = AccessTools.Method(typeof(PlayerEating), "StopEating");

    // Boat reflection
    private static readonly FieldInfo BoatSteeringWheelField = AccessTools.Field(typeof(Boat), "_steeringWheel");
    private static readonly FieldInfo BoatThrottleField = AccessTools.Field(typeof(Boat), "_throttle");
    private static readonly FieldInfo BoatLocalDriveInputField = AccessTools.Field(typeof(Boat), "_localDriveInput");

    // Active RedDotSights for per-eye stereo collimation
    private static readonly HashSet<RedDotSight> ActiveRedDots = new HashSet<RedDotSight>();
    private static bool _renderHookRegistered;

    // =========================================================================
    // Pavlov VR / H3VR Per-Firearm Manual Reload Architecture
    // =========================================================================
    public enum FirearmArchetype
    {
        BoxMagPistolOrAuto,
        BoltAction,
        TubeShotgun,
        BreakAction,
        Revolver
    }

    private sealed class ManualGunState
    {
        public Weapon WeaponRef;
        public FirearmArchetype Archetype;
        public bool Initialized;

        // Chamber + Magazine / Tube / Cylinder tracking
        public bool HasMagazineInserted = true;
        public bool IsBreechOrCylinderOpen;
        public bool ChamberLoaded = true;
        public int MagRounds;
        public bool NeedsActionCycle; // True when chamber is empty or bolt/pump needs cycling

        // Optional discovered mesh bones on the 3D weapon hierarchy for authentic visual feedback
        public Transform DiscoveredMagBone;
        public Vector3 OrigMagScale = Vector3.one;
        public bool MagBoneHidden;

        public Transform DiscoveredActionBone;
        public Vector3 OrigActionLocalPos;
        public bool ActionBoneMoved;
    }

    private static readonly Dictionary<int, ManualGunState> GunStates = new Dictionary<int, ManualGunState>();

    // Physical bow & manual weapon reload runtime state
    private static bool _isPhysicalBowDrawing;
    private static float _nextBowHapticTime;

    private static bool _holdingVirtualMag;
    private static FirearmArchetype _heldAmmoVisualArchetype;
    private static bool _isGrabbingGunMagazine;
    private static Vector3 _magGrabStartOffPos;
    private static bool _isGrabbingSlideOrAction;
    private static Vector3 _actionGrabStartLocalOnGun;
    private static bool _actionReachedBackStop;
    private static float _curActionPullMeters;
    private static bool _wasOffHandGrabHeld;
    private static float _nextDryFireClickTime;
    private static float _lastBreechOpenTime;

    private static GameObject _virtualMagRoot;
    private static GameObject _virtualBoxMagMesh;
    private static GameObject _virtualShellMesh;
    private static GameObject _virtualSpeedloaderMesh;

    private static GameObject _gunGuideRoot;
    private static GameObject _magwellRingGuide;
    private static GameObject _actionRailGuide;
    private static GameObject _actionHandleKnob;
    private static Material _guideCyanMat;
    private static Material _guideAmberMat;

    // Physical melee / punch state
    private static float _nextMeleeSwingTime;
    private static Vector3 _pendingVRMeleeSwingDir;
    private static float _nextPunchTimeLeft;
    private static float _nextPunchTimeRight;
    private static int _pendingVRPunchSide = -1;
    private static Vector3 _pendingVRPunchDir;

    // Physical boat state
    private static bool _wasGrabbingWheel;
    private static bool _isGrabbingThrottle;
    private static int _wheelHandIdx = -1; // 0=Left, 1=Right
    private static float _lastWheelHandAngle;
    private static float _accumWheelSteer;
    private static float _nextBoatHapticTime;

    private sealed class DroppedMagEffect : MonoBehaviour
    {
        public Vector3 LinearVel;
        public Vector3 AngularVel;
        private float _dieTime;

        private void OnEnable()
        {
            _dieTime = Time.unscaledTime + 2.0f;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _dieTime)
            {
                Destroy(gameObject);
                return;
            }
            float dt = Time.deltaTime;
            LinearVel += Physics.gravity * dt;
            transform.position += LinearVel * dt;
            transform.Rotate(AngularVel * dt, Space.Self);
        }
    }

    private static ManualGunState GetOrCreateGunState(Weapon weapon)
    {
        if (weapon == null) return null;
        int key = RuntimeHelpers.GetHashCode(weapon);
        if (GunStates.TryGetValue(key, out ManualGunState st) && st != null && st.WeaponRef == weapon && st.Initialized)
        {
            return st;
        }

        if (GunStates.Count > 48)
        {
            var deadKeys = new List<int>();
            foreach (var kv in GunStates)
            {
                if (kv.Value == null || kv.Value.WeaponRef == null)
                    deadKeys.Add(kv.Key);
            }
            for (int i = 0; i < deadKeys.Count; i++)
                GunStates.Remove(deadKeys[i]);
        }

        st = new ManualGunState
        {
            WeaponRef = weapon,
            Archetype = ClassifyFirearmArchetype(weapon),
            Initialized = true
        };

        int curAmmo = Mathf.Max(0, weapon.Ammo);
        st.HasMagazineInserted = true;
        st.IsBreechOrCylinderOpen = false;
        st.ChamberLoaded = curAmmo > 0;
        st.NeedsActionCycle = curAmmo <= 0;

        if (st.Archetype == FirearmArchetype.TubeShotgun ||
            st.Archetype == FirearmArchetype.BreakAction ||
            st.Archetype == FirearmArchetype.Revolver)
        {
            st.MagRounds = curAmmo;
        }
        else
        {
            st.MagRounds = Mathf.Max(0, curAmmo - (st.ChamberLoaded ? 1 : 0));
        }

        DiscoverWeaponSubMeshes(weapon, st);
        GunStates[key] = st;
        return st;
    }

    private static FirearmArchetype ClassifyFirearmArchetype(Weapon weapon)
    {
        string nm = (weapon.name ?? string.Empty).ToLowerInvariant();
        int projPerShot = (int)(WeaponProjCountField?.GetValue(weapon) ?? 1);
        bool fullAuto = (bool)(WeaponFullAutoField?.GetValue(weapon) ?? false);
        float fireInterval = (float)(WeaponTimeBetweenShotsField?.GetValue(weapon) ?? 0.15f);
        int maxMag = weapon.Attachments != null ? Mathf.Max(1, weapon.Attachments.AmmoPerMag) : 8;
        bool hasSniperScope = weapon.Attachments != null && weapon.Attachments.UseSniperUi;

        if (nm.Contains("revolver") || nm.Contains("magnum") || nm.Contains("python") || nm.Contains("colt"))
        {
            return FirearmArchetype.Revolver;
        }

        if (nm.Contains("double") || nm.Contains("blunder") || nm.Contains("flare") || nm.Contains("launcher") ||
            maxMag == 1 || (projPerShot > 1 && maxMag <= 2))
        {
            return FirearmArchetype.BreakAction;
        }

        if (projPerShot > 1 || nm.Contains("shotgun") || nm.Contains("pump") || nm.Contains("scatter") || nm.Contains("12g"))
        {
            return FirearmArchetype.TubeShotgun;
        }

        if (hasSniperScope || nm.Contains("sniper") || nm.Contains("bolt") || nm.Contains("hunting") || nm.Contains("kar") ||
            (!fullAuto && fireInterval >= 0.65f && maxMag <= 10))
        {
            return FirearmArchetype.BoltAction;
        }

        return FirearmArchetype.BoxMagPistolOrAuto;
    }

    private static void DiscoverWeaponSubMeshes(Weapon weapon, ManualGunState st)
    {
        Transform root = weapon.SwayTransform != null ? weapon.SwayTransform : weapon.transform;
        Transform firePt = weapon.Attachments != null ? weapon.Attachments.FirePoint : null;
        if (root == null) return;

        foreach (Transform tr in root.GetComponentsInChildren<Transform>(true))
        {
            if (tr == null || tr == root || tr == weapon.SwayRotAroundTransform || tr == firePt) continue;
            if (weapon.HandModelRight != null && (tr == weapon.HandModelRight || tr.IsChildOf(weapon.HandModelRight))) continue;
            if (weapon.HandModelLeft != null && (tr == weapon.HandModelLeft || tr.IsChildOf(weapon.HandModelLeft))) continue;
            if (weapon.HandsMesh != null && (tr == weapon.HandsMesh.transform || tr.IsChildOf(weapon.HandsMesh.transform))) continue;

            string n = tr.name.ToLowerInvariant();
            if (n.StartsWith("htfvr_")) continue;

            if (st.DiscoveredMagBone == null &&
                st.Archetype != FirearmArchetype.TubeShotgun &&
                (n.Contains("mag") || n.Contains("clip")))
            {
                st.DiscoveredMagBone = tr;
                st.OrigMagScale = tr.localScale;
            }
            else if (st.DiscoveredActionBone == null &&
                     (n.Contains("slide") || n.Contains("bolt") || n.Contains("pump") ||
                      n.Contains("charging") || n.Contains("cock") || n.Contains("forend")))
            {
                st.DiscoveredActionBone = tr;
                st.OrigActionLocalPos = tr.localPosition;
            }
        }
    }

    private static void RestoreGunSubMeshes(ManualGunState st)
    {
        if (st == null) return;
        if (st.MagBoneHidden && st.DiscoveredMagBone != null)
        {
            st.DiscoveredMagBone.localScale = st.OrigMagScale;
            st.MagBoneHidden = false;
        }
        if (st.ActionBoneMoved && st.DiscoveredActionBone != null)
        {
            st.DiscoveredActionBone.localPosition = st.OrigActionLocalPos;
            st.ActionBoneMoved = false;
        }
    }

    private static void SetWeaponAmmoDirect(Weapon weapon, int count)
    {
        if (weapon == null) return;
        int maxMag = weapon.Attachments != null ? Mathf.Max(1, weapon.Attachments.AmmoPerMag) : 30;
        int clamped = Mathf.Clamp(count, 0, maxMag);
        WeaponAmmoSetter?.Invoke(weapon, new object[] { clamped });
    }

    private static void PlayWeaponReloadStageClip(Weapon weapon, int stageIndex)
    {
        // stageIndex: 0 = Eject / Open, 1 = Insert Mag / Shell, 2 = Rack Slide / Bolt / Pump / Close Breech
        try
        {
            AudioSequence seq = (WeaponReloadLastSeqField?.GetValue(weapon) as AudioSequence)
                                ?? (WeaponReloadSeqField?.GetValue(weapon) as AudioSequence);
            if (seq != null && seq.Steps != null && seq.Steps.Length > 0)
            {
                int idx = stageIndex switch
                {
                    0 => 0,
                    1 => Mathf.Clamp(seq.Steps.Length / 2, 0, seq.Steps.Length - 1),
                    _ => seq.Steps.Length - 1
                };

                AudioClip clip = seq.Steps[idx]?.GetRandomClip();
                if (clip != null)
                {
                    float pitch = stageIndex == 0 ? 1.04f : (stageIndex == 1 ? 0.98f : 1.02f);
                    PlayOneShot3D(clip, weapon.transform.position, Mathf.Clamp(seq.Volume * 0.85f, 0.25f, 1.0f), pitch);
                    return;
                }
            }

            float fallbackPitch = stageIndex == 0 ? 0.82f : (stageIndex == 1 ? 1.05f : 1.24f);
            AudioManager.PlayGlobalClip("Click", variation: true, fallbackPitch, 0.24f);
        }
        catch
        {
            // Safe audio guard
        }
    }

    private static void PlayOneShot3D(AudioClip clip, Vector3 worldPos, float volume, float pitch)
    {
        if (clip == null) return;
        GameObject go = new GameObject("HTFVR_ReloadOneShotAudio");
        go.transform.position = worldPos;
        var src = go.AddComponent<AudioSource>();
        src.clip = clip;
        src.volume = volume;
        src.pitch = pitch;
        src.spatialBlend = 0.85f;
        src.minDistance = 0.5f;
        src.maxDistance = 12f;
        src.Play();
        UnityEngine.Object.Destroy(go, Mathf.Max(0.25f, clip.length / Mathf.Max(0.5f, pitch) + 0.1f));
    }

    private static void SpawnDroppedMagazineVisual(Vector3 worldPos, Quaternion worldRot, FirearmArchetype archetype)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");
        GameObject drop = new GameObject("HTFVR_DroppedMagazine");
        drop.transform.SetPositionAndRotation(worldPos, worldRot);

        if (archetype == FirearmArchetype.BreakAction || archetype == FirearmArchetype.Revolver)
        {
            for (int i = 0; i < 2; i++)
            {
                GameObject shell = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                shell.transform.SetParent(drop.transform, false);
                shell.transform.localPosition = new Vector3((i - 0.5f) * 0.018f, 0f, 0f);
                shell.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                shell.transform.localScale = new Vector3(0.016f, 0.028f, 0.016f);
                UnityEngine.Object.Destroy(shell.GetComponent<Collider>());
                var mr = shell.GetComponent<MeshRenderer>();
                mr.sharedMaterial = new Material(sh)
                {
                    color = archetype == FirearmArchetype.Revolver
                        ? new Color(0.85f, 0.68f, 0.24f)
                        : new Color(0.78f, 0.16f, 0.14f)
                };
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
        }
        else
        {
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.transform.SetParent(drop.transform, false);
            body.transform.localScale = archetype == FirearmArchetype.BoltAction
                ? new Vector3(0.026f, 0.085f, 0.068f)
                : new Vector3(0.024f, 0.115f, 0.052f);
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());
            var mr = body.GetComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(sh) { color = new Color(0.14f, 0.16f, 0.19f) };
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        var fx = drop.AddComponent<DroppedMagEffect>();
        fx.LinearVel = (worldRot * new Vector3(0f, -0.85f, -0.25f)) + UnityEngine.Random.insideUnitSphere * 0.2f;
        fx.AngularVel = new Vector3(240f, 95f, 140f);
    }

    public static string GetCurrentManualReloadPrompt(Weapon weapon, bool richText)
    {
        if (weapon == null || (weapon is Bow) || VRConfig.AutoReloadWeapons.Value) return null;
        ManualGunState st = GetOrCreateGunState(weapon);
        if (st == null) return null;

        int maxMag = weapon.Attachments != null ? Mathf.Max(1, weapon.Attachments.AmmoPerMag) : 8;

        switch (st.Archetype)
        {
            case FirearmArchetype.TubeShotgun:
                if (st.NeedsActionCycle && st.MagRounds > 0)
                    return richText ? "<color=#FFD15C>PUMP FOREND BACK & FWD</color>" : "PUMP FOREND";
                if (weapon.Ammo <= 0 && st.MagRounds <= 0)
                    return richText ? "<color=#62E3FF>GRAB SHELLS FROM POUCH -> LOAD</color>" : "LOAD SHELLS FROM POUCH";
                return null;

            case FirearmArchetype.BreakAction:
            case FirearmArchetype.Revolver:
                if (st.IsBreechOrCylinderOpen)
                {
                    if (st.MagRounds < maxMag)
                        return richText ? "<color=#62E3FF>GRAB AMMO FROM POUCH -> INSERT</color>" : "INSERT AMMO FROM POUCH";
                    return richText ? "<color=#FFD15C>FLICK WRIST UP TO CLOSE</color>" : "FLICK WRIST TO CLOSE";
                }
                if (weapon.Ammo <= 0)
                    return richText ? "<color=#FF9E4A>PRESS [X] TO OPEN BREECH</color>" : "PRESS [X] TO OPEN";
                return null;

            default:
                if (!st.HasMagazineInserted)
                    return richText
                        ? (st.ChamberLoaded
                            ? "<color=#62E3FF>1 IN CHAMBER • INSERT MAG</color>"
                            : "<color=#62E3FF>GRAB MAG FROM POUCH -> INSERT</color>")
                        : "INSERT FRESH MAG";
                if (!st.ChamberLoaded && st.MagRounds > 0)
                    return richText
                        ? (st.Archetype == FirearmArchetype.BoltAction
                            ? "<color=#FFD15C>PULL BOLT BACK & FWD</color>"
                            : "<color=#FFD15C>RACK SLIDE / CHARGING HANDLE</color>")
                        : "RACK SLIDE / BOLT";
                if (weapon.Ammo <= 0)
                    return richText ? "<color=#FF9E4A>EJECT MAG [X] OR PULL MAG OUT</color>" : "EJECT SPENT MAG [X]";
                return null;
        }
    }

    public static bool TryGetManualWeaponStatusForHUD(
        Weapon weapon,
        out int displayAmmo,
        out int maxAmmo,
        out bool hasChamberRound,
        out string stageHint)
    {
        displayAmmo = 0;
        maxAmmo = 0;
        hasChamberRound = false;
        stageHint = null;

        if (weapon == null || weapon.Attachments == null) return false;
        maxAmmo = Mathf.Max(1, weapon.Attachments.AmmoPerMag);
        displayAmmo = weapon.Ammo;

        if ((weapon is Bow) || VRConfig.AutoReloadWeapons.Value)
        {
            hasChamberRound = displayAmmo > 0;
            return true;
        }

        ManualGunState st = GetOrCreateGunState(weapon);
        if (st != null)
        {
            hasChamberRound = st.ChamberLoaded;
            stageHint = GetCurrentManualReloadPrompt(weapon, richText: false);
        }
        return true;
    }

    /// <summary>
    /// Triggered by Left X (Off-Hand Primary) or physical mag-pull in Manual VR Mode!
    /// Ejects box magazine (retaining 1 chambered round on tactical reload!) or opens break-action / revolver cylinder.
    /// </summary>
    public static void TriggerManualMagReleaseOrBreech(Weapon weapon)
    {
        if (weapon == null || (weapon is Bow) || VRConfig.AutoReloadWeapons.Value) return;
        if (weapon.Holder == null || weapon.Holder.BlockInputs || weapon.IsInInventory) return;

        ManualGunState st = GetOrCreateGunState(weapon);
        if (st == null) return;

        var vr = VRRigManager.Instance;
        Vector3 magwellWorld = vr != null
            ? (vr.DominantGripWorldPos + (vr.DominantAimWorldRot * new Vector3(0f, -0.04f, 0.05f)))
            : weapon.transform.position;
        Quaternion gunRot = vr != null ? vr.DominantAimWorldRot : weapon.transform.rotation;

        if (st.Archetype == FirearmArchetype.TubeShotgun)
        {
            // Tube shotguns don't drop a box mag; give a subtle tactile cue toward the bottom shell gate
            OpenXRBridge.TriggerDominantHaptic(0.22f, 0.02f, 150f);
            return;
        }

        if (st.Archetype == FirearmArchetype.BreakAction || st.Archetype == FirearmArchetype.Revolver)
        {
            if (!st.IsBreechOrCylinderOpen)
            {
                st.IsBreechOrCylinderOpen = true;
                st.ChamberLoaded = false;
                st.MagRounds = 0;
                st.NeedsActionCycle = true;
                _lastBreechOpenTime = Time.unscaledTime;
                SetWeaponAmmoDirect(weapon, 0);

                SpawnDroppedMagazineVisual(magwellWorld, gunRot, st.Archetype);
                PlayWeaponReloadStageClip(weapon, stageIndex: 0);
                OpenXRBridge.TriggerDominantHaptic(0.52f, 0.04f, 150f);
                OpenXRBridge.TriggerOffHandHaptic(0.28f, 0.025f, 145f);
            }
            return;
        }

        // Box-Magazine Pistol / SMG / Rifle / Bolt-Action Sniper
        if (!st.HasMagazineInserted) return;

        st.HasMagazineInserted = false;
        st.MagRounds = 0;

        // Tactical Reload: if gun had >= 1 round, 1 live round remains in the chamber!
        if (weapon.Ammo > 0 && st.ChamberLoaded)
        {
            st.ChamberLoaded = true;
            SetWeaponAmmoDirect(weapon, 1);
        }
        else
        {
            st.ChamberLoaded = false;
            st.NeedsActionCycle = true;
            SetWeaponAmmoDirect(weapon, 0);
        }

        if (st.DiscoveredMagBone != null)
        {
            st.DiscoveredMagBone.localScale = Vector3.zero;
            st.MagBoneHidden = true;
        }

        SpawnDroppedMagazineVisual(magwellWorld, gunRot, st.Archetype);
        PlayWeaponReloadStageClip(weapon, stageIndex: 0);
        OpenXRBridge.TriggerDominantHaptic(0.48f, 0.04f, 145f);
        OpenXRBridge.TriggerOffHandHaptic(0.26f, 0.025f, 150f);
    }

    public static void UpdateGlobalCombatVisuals()
    {
        Player lp = Player.LocalPlayer;
        Item held = lp != null && lp.Holding != null ? lp.Holding.HeldItem : null;
        bool validManualFirearm = VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive &&
                                  !VRConfig.AutoReloadWeapons.Value &&
                                  held != null && held.Weapon != null && !(held.Weapon is Bow) && !held.IsInInventory;

        if (!validManualFirearm)
        {
            _holdingVirtualMag = false;
            _isGrabbingGunMagazine = false;
            _isGrabbingSlideOrAction = false;
            _actionReachedBackStop = false;
            _curActionPullMeters = 0f;

            if (_virtualMagRoot != null && _virtualMagRoot.activeSelf)
                _virtualMagRoot.SetActive(false);
            if (_gunGuideRoot != null && _gunGuideRoot.activeSelf)
                _gunGuideRoot.SetActive(false);
        }
    }

    private static GameObject EnsureVirtualMagazineVisual(FirearmArchetype archetype)
    {
        if (_virtualMagRoot == null)
        {
            _virtualMagRoot = new GameObject("HTFVR_VirtualAmmoInHand");
            UnityEngine.Object.DontDestroyOnLoad(_virtualMagRoot);
            _virtualMagRoot.hideFlags = HideFlags.HideAndDontSave;

            Shader sh = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Sprites/Default");

            // 1. Box Magazine (Pistol / SMG / Rifle / Sniper)
            _virtualBoxMagMesh = new GameObject("BoxMagGroup");
            _virtualBoxMagMesh.transform.SetParent(_virtualMagRoot.transform, false);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Cube);
            body.transform.SetParent(_virtualBoxMagMesh.transform, false);
            body.transform.localScale = new Vector3(0.024f, 0.115f, 0.052f);
            body.transform.localRotation = Quaternion.Euler(14f, 0f, 0f);
            UnityEngine.Object.Destroy(body.GetComponent<Collider>());
            var bodyRenderer = body.GetComponent<MeshRenderer>();
            bodyRenderer.sharedMaterial = new Material(sh) { color = new Color(0.15f, 0.17f, 0.20f) };
            bodyRenderer.shadowCastingMode = ShadowCastingMode.Off;

            GameObject brass = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            brass.transform.SetParent(_virtualBoxMagMesh.transform, false);
            brass.transform.localPosition = new Vector3(0f, 0.058f, 0.008f);
            brass.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            brass.transform.localScale = new Vector3(0.011f, 0.022f, 0.011f);
            UnityEngine.Object.Destroy(brass.GetComponent<Collider>());
            var brassRenderer = brass.GetComponent<MeshRenderer>();
            brassRenderer.sharedMaterial = new Material(sh) { color = new Color(0.90f, 0.72f, 0.24f) };
            brassRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // 2. 12-Gauge Shotgun Shell (Red hull + brass rim)
            _virtualShellMesh = new GameObject("ShotgunShellGroup");
            _virtualShellMesh.transform.SetParent(_virtualMagRoot.transform, false);

            GameObject hull = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            hull.transform.SetParent(_virtualShellMesh.transform, false);
            hull.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            hull.transform.localScale = new Vector3(0.020f, 0.032f, 0.020f);
            UnityEngine.Object.Destroy(hull.GetComponent<Collider>());
            var hullRenderer = hull.GetComponent<MeshRenderer>();
            hullRenderer.sharedMaterial = new Material(sh) { color = new Color(0.82f, 0.15f, 0.13f) };
            hullRenderer.shadowCastingMode = ShadowCastingMode.Off;

            GameObject rim = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            rim.transform.SetParent(_virtualShellMesh.transform, false);
            rim.transform.localPosition = new Vector3(0f, 0f, -0.026f);
            rim.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            rim.transform.localScale = new Vector3(0.0215f, 0.009f, 0.0215f);
            UnityEngine.Object.Destroy(rim.GetComponent<Collider>());
            var rimRenderer = rim.GetComponent<MeshRenderer>();
            rimRenderer.sharedMaterial = new Material(sh) { color = new Color(0.90f, 0.72f, 0.24f) };
            rimRenderer.shadowCastingMode = ShadowCastingMode.Off;

            // 3. Revolver Speedloader (Cylinder knob + brass rounds)
            _virtualSpeedloaderMesh = new GameObject("SpeedloaderGroup");
            _virtualSpeedloaderMesh.transform.SetParent(_virtualMagRoot.transform, false);

            GameObject loaderHub = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            loaderHub.transform.SetParent(_virtualSpeedloaderMesh.transform, false);
            loaderHub.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            loaderHub.transform.localScale = new Vector3(0.038f, 0.012f, 0.038f);
            UnityEngine.Object.Destroy(loaderHub.GetComponent<Collider>());
            var hubRenderer = loaderHub.GetComponent<MeshRenderer>();
            hubRenderer.sharedMaterial = new Material(sh) { color = new Color(0.16f, 0.18f, 0.22f) };
            hubRenderer.shadowCastingMode = ShadowCastingMode.Off;

            for (int r = 0; r < 6; r++)
            {
                float rad = r * 60f * Mathf.Deg2Rad;
                GameObject cart = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                cart.transform.SetParent(_virtualSpeedloaderMesh.transform, false);
                cart.transform.localPosition = new Vector3(Mathf.Cos(rad) * 0.012f, Mathf.Sin(rad) * 0.012f, 0.016f);
                cart.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                cart.transform.localScale = new Vector3(0.0085f, 0.018f, 0.0085f);
                UnityEngine.Object.Destroy(cart.GetComponent<Collider>());
                var cr = cart.GetComponent<MeshRenderer>();
                cr.sharedMaterial = new Material(sh) { color = new Color(0.90f, 0.72f, 0.24f) };
                cr.shadowCastingMode = ShadowCastingMode.Off;
            }

            _virtualMagRoot.SetActive(false);
        }

        _heldAmmoVisualArchetype = archetype;
        bool useShell = archetype == FirearmArchetype.TubeShotgun || archetype == FirearmArchetype.BreakAction;
        bool useSpeedloader = archetype == FirearmArchetype.Revolver;
        bool useBoxMag = !useShell && !useSpeedloader;

        if (_virtualBoxMagMesh != null)
        {
            _virtualBoxMagMesh.SetActive(useBoxMag);
            _virtualBoxMagMesh.transform.localScale = archetype == FirearmArchetype.BoltAction
                ? new Vector3(1.05f, 0.78f, 1.25f)
                : Vector3.one;
        }
        if (_virtualShellMesh != null) _virtualShellMesh.SetActive(useShell);
        if (_virtualSpeedloaderMesh != null) _virtualSpeedloaderMesh.SetActive(useSpeedloader);

        return _virtualMagRoot;
    }

    private static void EnsureGunActionGuides()
    {
        if (_gunGuideRoot != null) return;

        _gunGuideRoot = new GameObject("HTFVR_GunReloadGuides");
        UnityEngine.Object.DontDestroyOnLoad(_gunGuideRoot);
        _gunGuideRoot.hideFlags = HideFlags.HideAndDontSave;

        Shader unlit = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
        _guideCyanMat = new Material(unlit) { color = new Color(0.28f, 0.90f, 1.0f, 0.68f), renderQueue = 4090 };
        _guideAmberMat = new Material(unlit) { color = new Color(1.0f, 0.80f, 0.25f, 0.78f), renderQueue = 4091 };

        // Subtle holographic magwell/loading-port ring
        _magwellRingGuide = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        _magwellRingGuide.name = "HTFVR_MagwellGuide";
        _magwellRingGuide.transform.SetParent(_gunGuideRoot.transform, false);
        _magwellRingGuide.transform.localScale = new Vector3(0.042f, 0.003f, 0.056f);
        UnityEngine.Object.Destroy(_magwellRingGuide.GetComponent<Collider>());
        var magR = _magwellRingGuide.GetComponent<MeshRenderer>();
        magR.sharedMaterial = _guideCyanMat;
        magR.shadowCastingMode = ShadowCastingMode.Off;

        // Subtle holographic slide/bolt/pump rail + moving handle indicator
        _actionRailGuide = new GameObject("HTFVR_ActionRailGuide");
        _actionRailGuide.transform.SetParent(_gunGuideRoot.transform, false);

        GameObject railTrack = GameObject.CreatePrimitive(PrimitiveType.Cube);
        railTrack.transform.SetParent(_actionRailGuide.transform, false);
        railTrack.transform.localPosition = new Vector3(0f, 0f, -0.035f);
        railTrack.transform.localScale = new Vector3(0.004f, 0.004f, 0.075f);
        UnityEngine.Object.Destroy(railTrack.GetComponent<Collider>());
        var railR = railTrack.GetComponent<MeshRenderer>();
        railR.sharedMaterial = _guideCyanMat;
        railR.shadowCastingMode = ShadowCastingMode.Off;

        _actionHandleKnob = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _actionHandleKnob.transform.SetParent(_actionRailGuide.transform, false);
        _actionHandleKnob.transform.localScale = new Vector3(0.020f, 0.014f, 0.020f);
        UnityEngine.Object.Destroy(_actionHandleKnob.GetComponent<Collider>());
        var knobR = _actionHandleKnob.GetComponent<MeshRenderer>();
        knobR.sharedMaterial = _guideAmberMat;
        knobR.shadowCastingMode = ShadowCastingMode.Off;

        _gunGuideRoot.SetActive(false);
    }

    public static void RegisterRenderHooks()
    {
        if (_renderHookRegistered) return;
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        _renderHookRegistered = true;
    }

    private static void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam)
    {
        if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive) return;
        if (cam != VRRigManager.Instance.LeftEyeCamera && cam != VRRigManager.Instance.RightEyeCamera) return;
        if (ActiveRedDots.Count == 0) return;

        Vector3 eyePos = cam.transform.position;
        ActiveRedDots.RemoveWhere(s => s == null);

        foreach (RedDotSight sight in ActiveRedDots)
        {
            if (!sight.isActiveAndEnabled) continue;
            RectTransform canvas = RedDotCanvasField?.GetValue(sight) as RectTransform;
            Image dot = RedDotImgField?.GetValue(sight) as Image;
            if (canvas == null || dot == null) continue;

            float zeroDist = (float)(RedDotZeroDistField?.GetValue(sight) ?? 5f);
            Vector3 zeroWorldPoint = sight.transform.position + sight.transform.up * zeroDist;
            Vector3 rayDir = (zeroWorldPoint - eyePos).normalized;

            Plane glassPlane = new Plane(canvas.forward, canvas.position);
            if (glassPlane.Raycast(new Ray(eyePos, rayDir), out float enter) && enter > 0.01f)
            {
                Vector3 hitWorld = eyePos + rayDir * enter;
                Vector3 localPos = canvas.InverseTransformPoint(hitWorld);
                localPos.z = 0f;

                Rect r = canvas.rect;
                bool insideGlass = r.width <= 0.0001f || r.Contains(new Vector2(localPos.x, localPos.y));
                dot.enabled = insideGlass;
                if (insideGlass)
                {
                    dot.rectTransform.localPosition = localPos;
                }
            }
        }
    }

    private static bool IsLocalVRItem(Item item)
    {
        return VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive &&
               item != null && item.Holder != null && item.Holder.Owner != null && item.Holder.Owner.IsLocalClient;
    }

    // =========================================================================
    // 1. Weapon, Pavlov/H3VR Manual Reload, 3D PiP Scope & Stereo RedDot Patches
    // =========================================================================
    [HarmonyPatch(typeof(RedDotSight), "LateUpdate")]
    public static class RedDotSightLateUpdatePatch
    {
        public static bool Prefix(RedDotSight __instance)
        {
            ActiveRedDots.Add(__instance);
            if (VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Weapon))]
    public static class WeaponPatches
    {
        [HarmonyPatch("ReloadInput")]
        [HarmonyPrefix]
        public static bool ReloadInputPrefix(Weapon __instance)
        {
            if (!IsLocalVRItem(__instance) || (__instance is Bow)) return true;
            if (VRConfig.AutoReloadWeapons.Value) return true;

            // Manual VR Mode: block flatscreen canned reload timer & animation!
            WeaponQueueReloadField?.SetValue(__instance, false);
            WeaponIsReloadingField?.SetValue(__instance, false);
            return false;
        }

        [HarmonyPatch("LocalReload")]
        [HarmonyPrefix]
        public static bool LocalReloadPrefix(Weapon __instance)
        {
            if (!IsLocalVRItem(__instance) || (__instance is Bow)) return true;
            if (VRConfig.AutoReloadWeapons.Value) return true;

            WeaponQueueReloadField?.SetValue(__instance, false);
            WeaponIsReloadingField?.SetValue(__instance, false);
            return false;
        }

        [HarmonyPatch("Shoot")]
        [HarmonyPrefix]
        public static void ShootPrefix(Weapon __instance, out (float aim, int prevAmmo, bool restore) __state)
        {
            __state = (0f, 0, false);
            if (!IsLocalVRItem(__instance)) return;

            int prevAmmo = __instance.Ammo;
            float curAim = 0f;
            if (WeaponAimPercentField != null)
            {
                curAim = (float)WeaponAimPercentField.GetValue(__instance);
                WeaponAimPercentField.SetValue(__instance, 0f);
            }
            __state = (curAim, prevAmmo, true);
        }

        [HarmonyPatch("Shoot")]
        [HarmonyPostfix]
        public static void ShootPostfix(Weapon __instance, (float aim, int prevAmmo, bool restore) __state)
        {
            if (!__state.restore) return;
            WeaponAimPercentField?.SetValue(__instance, __state.aim);

            bool didFireShot = __state.prevAmmo > 0 && __instance.Ammo < __state.prevAmmo;

            if (!(__instance is Bow) && !VRConfig.AutoReloadWeapons.Value)
            {
                WeaponQueueReloadField?.SetValue(__instance, false);

                ManualGunState st = GetOrCreateGunState(__instance);
                if (st != null && didFireShot)
                {
                    if (__instance.Ammo > 0)
                    {
                        st.ChamberLoaded = true;
                        st.NeedsActionCycle = false;
                        st.MagRounds = (st.Archetype == FirearmArchetype.TubeShotgun ||
                                        st.Archetype == FirearmArchetype.BreakAction ||
                                        st.Archetype == FirearmArchetype.Revolver)
                            ? __instance.Ammo
                            : Mathf.Max(0, __instance.Ammo - 1);
                    }
                    else
                    {
                        // Shot dry! Chamber is now empty -> requires slide/bolt/pump rack after loading!
                        st.ChamberLoaded = false;
                        st.MagRounds = 0;
                        st.NeedsActionCycle = true;
                    }
                }

                if (didFireShot && __instance.Ammo <= 0)
                {
                    WeaponHasCoolDownField?.SetValue(__instance, true);
                    float shotCooldown = Mathf.Max(0.02f, (float)(WeaponTimeBetweenShotsField?.GetValue(__instance) ?? 0.1f));
                    __instance.Invoke("CoolDown", shotCooldown);
                }
            }

            if (didFireShot)
            {
                OpenXRBridge.TriggerDominantHaptic(0.85f, 0.065f, 155f);
                if (VRPlayerAndHandsPatches.IsTwoHandingWeapon)
                {
                    OpenXRBridge.TriggerOffHandHaptic(0.55f, 0.055f, 145f);
                }
            }
        }

        [HarmonyPatch("OnDrop")]
        [HarmonyPostfix]
        public static void OnDropPostfix(Weapon __instance)
        {
            if (!(__instance is Bow) && VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive)
            {
                if (!VRConfig.AutoReloadWeapons.Value)
                {
                    WeaponQueueReloadField?.SetValue(__instance, false);
                }
                if (GunStates.TryGetValue(RuntimeHelpers.GetHashCode(__instance), out ManualGunState st))
                {
                    RestoreGunSubMeshes(st);
                }
            }

            _holdingVirtualMag = false;
            _isGrabbingGunMagazine = false;
            _isGrabbingSlideOrAction = false;
            if (_virtualMagRoot != null && _virtualMagRoot.activeSelf) _virtualMagRoot.SetActive(false);
            if (_gunGuideRoot != null && _gunGuideRoot.activeSelf) _gunGuideRoot.SetActive(false);
        }

        [HarmonyPatch("PrimaryInput")]
        [HarmonyPrefix]
        public static void PrimaryInputPrefix(Weapon __instance)
        {
            if (!IsLocalVRItem(__instance) || (__instance is Bow)) return;
            if (VRConfig.AutoReloadWeapons.Value || __instance.Ammo > 0) return;

            bool isReloading = (bool)(WeaponIsReloadingField?.GetValue(__instance) ?? false);
            if (isReloading || __instance.Holder == null || __instance.Holder.BlockInputs) return;

            if (Time.unscaledTime >= _nextDryFireClickTime)
            {
                _nextDryFireClickTime = Time.unscaledTime + 0.22f;
                try
                {
                    AudioManager.PlayGlobalClip("Click", variation: true, 0.72f, 0.16f);
                }
                catch
                {
                    // Safe guard
                }
                OpenXRBridge.TriggerDominantHaptic(0.28f, 0.02f, 210f);
            }
        }

        [HarmonyPatch("Update")]
        [HarmonyPostfix]
        public static void UpdatePostfix(Weapon __instance)
        {
            if (!IsLocalVRItem(__instance) || (__instance is Bow)) return;

            bool isReloading = (bool)(WeaponIsReloadingField?.GetValue(__instance) ?? false);
            bool hasCoolDown = (bool)(WeaponHasCoolDownField?.GetValue(__instance) ?? false);

            if (VRConfig.AutoReloadWeapons.Value)
            {
                // Automatic Mode: clean up any manual state and let vanilla auto/button reload operate!
                if (GunStates.TryGetValue(RuntimeHelpers.GetHashCode(__instance), out ManualGunState autoSt))
                {
                    RestoreGunSubMeshes(autoSt);
                    autoSt.HasMagazineInserted = true;
                    autoSt.IsBreechOrCylinderOpen = false;
                    autoSt.ChamberLoaded = __instance.Ammo > 0;
                }
                if (_gunGuideRoot != null && _gunGuideRoot.activeSelf) _gunGuideRoot.SetActive(false);
                if (_virtualMagRoot != null && _virtualMagRoot.activeSelf) _virtualMagRoot.SetActive(false);
                _holdingVirtualMag = false;
                _isGrabbingGunMagazine = false;
                _isGrabbingSlideOrAction = false;

                if (__instance.Ammo <= 0 && !isReloading && !hasCoolDown &&
                    !__instance.IsInInventory && __instance.Holder != null && !__instance.Holder.BlockInputs)
                {
                    WeaponQueueReloadField?.SetValue(__instance, true);
                }
                return;
            }

            // Manual VR Mode (Pavlov VR / H3VR style):
            WeaponQueueReloadField?.SetValue(__instance, false);
            UpdatePavlovH3VRManualReload(__instance);
        }

        private static void UpdatePavlovH3VRManualReload(Weapon weapon)
        {
            var vr = VRRigManager.Instance;
            if (vr == null || weapon.Attachments == null || weapon.IsInInventory ||
                weapon.Holder == null || weapon.Holder.BlockInputs)
            {
                if (_gunGuideRoot != null && _gunGuideRoot.activeSelf) _gunGuideRoot.SetActive(false);
                return;
            }

            ManualGunState st = GetOrCreateGunState(weapon);
            if (st == null) return;

            EnsureGunActionGuides();

            int maxMag = Mathf.Max(1, weapon.Attachments.AmmoPerMag);
            var offState = vr.OffHandState;
            Vector3 offPos = vr.OffHandGripWorldPos;
            bool offGrabHeld = offState.IsActive && (offState.squeeze > 0.52f || offState.trigger > 0.55f);
            bool offGrabJustPressed = offGrabHeld && !_wasOffHandGrabHeld;

            Transform gunBasis = weapon.SwayTransform != null ? weapon.SwayTransform : weapon.transform;
            Vector3 gunGripPos = vr.DominantGripWorldPos;
            Quaternion gunRot = gunBasis.rotation;

            // Compute ergonomic interaction anchors on the weapon in world space
            Vector3 magwellLocalOffset = st.Archetype switch
            {
                FirearmArchetype.TubeShotgun => new Vector3(0f, -0.025f, 0.13f),
                FirearmArchetype.BreakAction => new Vector3(0f, 0.015f, 0.10f),
                FirearmArchetype.Revolver => new Vector3(-0.025f, 0.02f, 0.07f),
                FirearmArchetype.BoltAction => new Vector3(0f, -0.045f, 0.12f),
                _ => maxMag > 18 ? new Vector3(0f, -0.055f, 0.11f) : new Vector3(0f, -0.045f, 0.02f)
            };
            Vector3 magwellWorld = st.DiscoveredMagBone != null
                ? st.DiscoveredMagBone.position
                : (gunGripPos + (gunRot * magwellLocalOffset));

            Vector3 actionLocalOffset = st.Archetype switch
            {
                FirearmArchetype.TubeShotgun => new Vector3(0f, -0.015f, 0.28f), // Forend pump
                FirearmArchetype.BoltAction => new Vector3(0.03f, 0.045f, 0.08f), // Bolt handle
                _ => maxMag > 18 ? new Vector3(0f, 0.055f, 0.12f) : new Vector3(0f, 0.052f, 0.02f) // Charging handle / Pistol slide
            };
            Vector3 actionAnchorWorld = st.DiscoveredActionBone != null
                ? st.DiscoveredActionBone.position
                : (gunGripPos + (gunRot * actionLocalOffset));

            float distToMagwell = Vector3.Distance(offPos, magwellWorld);
            float distToAction = Vector3.Distance(offPos, actionAnchorWorld);

            // Generous Pavlov/H3VR Chest-Rig + Hip/Belt Ammo Pouch Zone (works comfortably standing OR seated!)
            Vector2 horizFromBody = new Vector2(offPos.x - vr.HeadWorldPos.x, offPos.z - vr.HeadWorldPos.z);
            bool inAmmoPouchZone = offPos.y < (vr.HeadWorldPos.y - 0.16f) &&
                                   horizFromBody.magnitude < 0.64f &&
                                   distToMagwell > 0.21f &&
                                   distToAction > 0.21f;

            // Keep discovered mag bone visibility synced
            if (st.DiscoveredMagBone != null)
            {
                bool shouldHideMag = !st.HasMagazineInserted &&
                                     (st.Archetype == FirearmArchetype.BoxMagPistolOrAuto || st.Archetype == FirearmArchetype.BoltAction);
                if (shouldHideMag && !st.MagBoneHidden)
                {
                    st.DiscoveredMagBone.localScale = Vector3.zero;
                    st.MagBoneHidden = true;
                }
                else if (!shouldHideMag && st.MagBoneHidden)
                {
                    st.DiscoveredMagBone.localScale = st.OrigMagScale;
                    st.MagBoneHidden = false;
                }
            }

            // -----------------------------------------------------------------
            // ARCHETYPE A: Break-Action Shotgun/Launcher & Revolver
            // -----------------------------------------------------------------
            if (st.Archetype == FirearmArchetype.BreakAction || st.Archetype == FirearmArchetype.Revolver)
            {
                if (!st.IsBreechOrCylinderOpen)
                {
                    // Allow off-hand grab on breech/cylinder to open when empty or partial!
                    if (offGrabJustPressed && distToMagwell < 0.18f && weapon.Ammo < maxMag)
                    {
                        TriggerManualMagReleaseOrBreech(weapon);
                    }
                    UpdateDiegeticGuides(showMagwell: false, magwellWorld, gunRot, showAction: false, actionAnchorWorld, 0f);
                }
                else
                {
                    bool needsShells = st.MagRounds < maxMag;
                    UpdateDiegeticGuides(showMagwell: needsShells, magwellWorld, gunRot, showAction: !needsShells, magwellWorld, 0f);

                    if (!_holdingVirtualMag && needsShells && offGrabJustPressed && inAmmoPouchZone)
                    {
                        BeginHoldingAmmoInOffHand(st.Archetype, offPos, vr.OffHandAimWorldRot);
                    }
                    else if (_holdingVirtualMag)
                    {
                        UpdateHeldAmmoTransform(offGrabHeld, offPos, vr.OffHandAimWorldRot);
                        if (_holdingVirtualMag && distToMagwell < 0.19f && needsShells)
                        {
                            _holdingVirtualMag = false;
                            if (_virtualMagRoot != null) _virtualMagRoot.SetActive(false);

                            st.MagRounds = maxMag;
                            PlayWeaponReloadStageClip(weapon, stageIndex: 1);
                            OpenXRBridge.TriggerOffHandHaptic(0.60f, 0.045f, 170f);
                            OpenXRBridge.TriggerDominantHaptic(0.45f, 0.035f, 160f);
                        }
                    }

                    // Stage 3: Close breech/cylinder by flicking dominant wrist upward OR pushing breech shut with off-hand!
                    if (!needsShells && (Time.unscaledTime - _lastBreechOpenTime) > 0.22f)
                    {
                        float upwardVel = vr.DominantHandVelocity.y - (weapon.Holder.Movement != null ? weapon.Holder.Movement.Velocity.y : 0f);
                        bool wristFlickClose = vr.DominantHandAngularVelocity.magnitude > 3.2f || upwardVel > 1.25f;
                        bool offHandPushClose = offGrabJustPressed && distToMagwell < 0.19f;

                        if (wristFlickClose || offHandPushClose)
                        {
                            st.IsBreechOrCylinderOpen = false;
                            st.ChamberLoaded = true;
                            st.NeedsActionCycle = false;
                            SetWeaponAmmoDirect(weapon, maxMag);
                            PlayWeaponReloadStageClip(weapon, stageIndex: 2);
                            NotifyMultiplayerReloadSafe(weapon);
                            OpenXRBridge.TriggerDominantHaptic(0.75f, 0.055f, 175f);
                            OpenXRBridge.TriggerOffHandHaptic(0.45f, 0.04f, 165f);
                        }
                    }
                }

                _wasOffHandGrabHeld = offGrabHeld;
                return;
            }

            // -----------------------------------------------------------------
            // ARCHETYPE B: Tube-Fed Pump Shotgun (Shell-by-Shell + Forend Pump!)
            // -----------------------------------------------------------------
            if (st.Archetype == FirearmArchetype.TubeShotgun)
            {
                int currentTotal = st.ChamberLoaded ? weapon.Ammo : st.MagRounds;
                bool canInsertShell = currentTotal < maxMag && !_isGrabbingSlideOrAction;
                bool needsPump = (!st.ChamberLoaded && st.MagRounds > 0) || _isGrabbingSlideOrAction;

                // 1) Grab shell from pouch & insert into bottom loading gate one shell at a time!
                if (!_holdingVirtualMag && !_isGrabbingSlideOrAction && canInsertShell && offGrabJustPressed && inAmmoPouchZone)
                {
                    BeginHoldingAmmoInOffHand(FirearmArchetype.TubeShotgun, offPos, vr.OffHandAimWorldRot);
                }
                else if (_holdingVirtualMag)
                {
                    UpdateHeldAmmoTransform(offGrabHeld, offPos, vr.OffHandAimWorldRot);
                    if (_holdingVirtualMag && distToMagwell < 0.20f && canInsertShell)
                    {
                        _holdingVirtualMag = false;
                        if (_virtualMagRoot != null) _virtualMagRoot.SetActive(false);

                        if (st.ChamberLoaded)
                        {
                            int nextAmmo = Mathf.Min(maxMag, weapon.Ammo + 1);
                            st.MagRounds = nextAmmo;
                            SetWeaponAmmoDirect(weapon, nextAmmo);
                        }
                        else
                        {
                            st.MagRounds = Mathf.Min(maxMag, st.MagRounds + 1);
                            st.NeedsActionCycle = true;
                        }

                        PlayWeaponReloadStageClip(weapon, stageIndex: 1);
                        OpenXRBridge.TriggerOffHandHaptic(0.55f, 0.04f, 175f);
                        OpenXRBridge.TriggerDominantHaptic(0.38f, 0.03f, 160f);
                    }
                }

                // 2) Forend Pump Action (pull back >= 5.2cm and push forward / release!)
                HandleSlideBoltOrPumpAction(weapon, st, gunBasis, actionAnchorWorld, distToAction, offPos, offGrabHeld, offGrabJustPressed, maxMag);

                UpdateDiegeticGuides(
                    showMagwell: canInsertShell && (_holdingVirtualMag || weapon.Ammo <= 0),
                    magwellWorld, gunRot,
                    showAction: needsPump,
                    actionAnchorWorld, _curActionPullMeters);

                _wasOffHandGrabHeld = offGrabHeld;
                return;
            }

            // -----------------------------------------------------------------
            // ARCHETYPE C: Box-Magazine Pistols, SMGs, Rifles & Bolt-Action Snipers
            // -----------------------------------------------------------------
            // Stage 1 Alternative: Physical H3VR/Pavlov Magazine Grab & Rip from Magwell!
            if (st.HasMagazineInserted && !_holdingVirtualMag && !_isGrabbingSlideOrAction)
            {
                if (!_isGrabbingGunMagazine)
                {
                    if (offGrabJustPressed && distToMagwell < 0.17f && (weapon.Ammo < maxMag || !st.ChamberLoaded))
                    {
                        _isGrabbingGunMagazine = true;
                        _magGrabStartOffPos = offPos;
                        OpenXRBridge.TriggerOffHandHaptic(0.24f, 0.02f, 150f);
                    }
                }
                else
                {
                    if (!offGrabHeld)
                    {
                        _isGrabbingGunMagazine = false;
                    }
                    else
                    {
                        VRPlayerAndHandsPatches.OffHandSnapActive = true;
                        VRPlayerAndHandsPatches.OffHandSnapPos = offPos;
                        VRPlayerAndHandsPatches.OffHandSnapRot = vr.OffHandGripWorldRot;
                        VRPlayerAndHandsPatches.OffHandSnapIsRawBoneRot = false;

                        float pullDist = Vector3.Distance(offPos, _magGrabStartOffPos);
                        if (pullDist > 0.06f)
                        {
                            _isGrabbingGunMagazine = false;
                            TriggerManualMagReleaseOrBreech(weapon);
                        }
                    }
                }
            }
            else
            {
                _isGrabbingGunMagazine = false;
            }

            // Stage 2: Grab Fresh Magazine from Hip/Chest Pouch & Insert into Magwell!
            if (!st.HasMagazineInserted && !_isGrabbingSlideOrAction)
            {
                if (!_holdingVirtualMag)
                {
                    if (offGrabJustPressed && inAmmoPouchZone)
                    {
                        BeginHoldingAmmoInOffHand(st.Archetype, offPos, vr.OffHandAimWorldRot);
                    }
                }
                else
                {
                    UpdateHeldAmmoTransform(offGrabHeld, offPos, vr.OffHandAimWorldRot);
                    if (_holdingVirtualMag && distToMagwell < 0.19f)
                    {
                        _holdingVirtualMag = false;
                        if (_virtualMagRoot != null) _virtualMagRoot.SetActive(false);

                        st.HasMagazineInserted = true;
                        if (st.DiscoveredMagBone != null && st.MagBoneHidden)
                        {
                            st.DiscoveredMagBone.localScale = st.OrigMagScale;
                            st.MagBoneHidden = false;
                        }

                        if (st.ChamberLoaded)
                        {
                            // Tactical reload complete! Round was already in the chamber -> topped off immediately!
                            st.MagRounds = Mathf.Max(0, maxMag - 1);
                            st.NeedsActionCycle = false;
                            SetWeaponAmmoDirect(weapon, maxMag);
                            NotifyMultiplayerReloadSafe(weapon);
                        }
                        else
                        {
                            // Dry reload: magazine is seated with full rounds, now rack slide / charging handle / bolt to chamber!
                            st.MagRounds = maxMag;
                            st.NeedsActionCycle = true;
                            SetWeaponAmmoDirect(weapon, 0);
                        }

                        PlayWeaponReloadStageClip(weapon, stageIndex: 1);
                        OpenXRBridge.TriggerOffHandHaptic(0.68f, 0.05f, 175f);
                        OpenXRBridge.TriggerDominantHaptic(0.50f, 0.04f, 160f);
                    }
                }
            }
            else if (_holdingVirtualMag && !offGrabHeld)
            {
                _holdingVirtualMag = false;
                if (_virtualMagRoot != null) _virtualMagRoot.SetActive(false);
            }

            // Stage 3: Grab & Rack Slide / Charging Handle / Bolt!
            if (st.HasMagazineInserted && !_holdingVirtualMag && !_isGrabbingGunMagazine)
            {
                HandleSlideBoltOrPumpAction(weapon, st, gunBasis, actionAnchorWorld, distToAction, offPos, offGrabHeld, offGrabJustPressed, maxMag);
            }

            bool showMagGuide = !st.HasMagazineInserted;
            bool showActionGuide = st.HasMagazineInserted && (!st.ChamberLoaded && st.MagRounds > 0 || _isGrabbingSlideOrAction);
            UpdateDiegeticGuides(showMagGuide, magwellWorld, gunRot, showActionGuide, actionAnchorWorld, _curActionPullMeters);

            _wasOffHandGrabHeld = offGrabHeld;
        }

        private static void BeginHoldingAmmoInOffHand(FirearmArchetype archetype, Vector3 offPos, Quaternion offAimRot)
        {
            _holdingVirtualMag = true;
            GameObject magObj = EnsureVirtualMagazineVisual(archetype);
            magObj.SetActive(true);
            magObj.transform.SetPositionAndRotation(offPos + (offAimRot * new Vector3(0f, 0.01f, 0.04f)), offAimRot);
            OpenXRBridge.TriggerOffHandHaptic(0.38f, 0.03f, 155f);
        }

        private static void UpdateHeldAmmoTransform(bool offGrabHeld, Vector3 offPos, Quaternion offAimRot)
        {
            GameObject magObj = EnsureVirtualMagazineVisual(_heldAmmoVisualArchetype);
            if (!offGrabHeld)
            {
                _holdingVirtualMag = false;
                magObj.SetActive(false);
                return;
            }
            magObj.SetActive(true);
            magObj.transform.SetPositionAndRotation(offPos + (offAimRot * new Vector3(0f, 0.01f, 0.04f)), offAimRot);
        }

        private static void HandleSlideBoltOrPumpAction(
            Weapon weapon,
            ManualGunState st,
            Transform gunBasis,
            Vector3 actionAnchorWorld,
            float distToAction,
            Vector3 offPos,
            bool offGrabHeld,
            bool offGrabJustPressed,
            int maxMag)
        {
            var vr = VRRigManager.Instance;
            if (!_isGrabbingSlideOrAction)
            {
                _curActionPullMeters = 0f;
                if (st.ActionBoneMoved && st.DiscoveredActionBone != null)
                {
                    st.DiscoveredActionBone.localPosition = st.OrigActionLocalPos;
                    st.ActionBoneMoved = false;
                }

                // Allow grabbing slide/bolt/pump whenever chamber needs cycling (or anytime with mag/tube rounds to rack!)
                bool canRack = (!st.ChamberLoaded && st.MagRounds > 0) || weapon.Ammo > 0;
                if (canRack && offGrabJustPressed && distToAction < 0.21f)
                {
                    _isGrabbingSlideOrAction = true;
                    _actionReachedBackStop = false;
                    _actionGrabStartLocalOnGun = gunBasis.InverseTransformPoint(offPos);
                    OpenXRBridge.TriggerOffHandHaptic(0.28f, 0.025f, 160f);
                }
                return;
            }

            Vector3 curLocalOnGun = gunBasis.InverseTransformPoint(offPos);
            // Pulling backward toward shooter along weapon's -Z axis:
            float pullBack = Mathf.Clamp(_actionGrabStartLocalOnGun.z - curLocalOnGun.z, 0f, 0.092f);
            _curActionPullMeters = pullBack;

            // Snap off-hand visually onto the slide / charging handle / pump forend!
            Vector3 trackedHandleWorld = actionAnchorWorld - (gunBasis.forward * pullBack);
            VRPlayerAndHandsPatches.OffHandSnapActive = true;
            VRPlayerAndHandsPatches.OffHandSnapPos = trackedHandleWorld;
            VRPlayerAndHandsPatches.OffHandSnapRot = vr.OffHandGripWorldRot;
            VRPlayerAndHandsPatches.OffHandSnapIsRawBoneRot = false;

            if (st.DiscoveredActionBone != null && st.DiscoveredActionBone.parent != null)
            {
                Vector3 localBackDir = st.DiscoveredActionBone.parent.InverseTransformDirection(-gunBasis.forward);
                st.DiscoveredActionBone.localPosition = st.OrigActionLocalPos + localBackDir * pullBack;
                st.ActionBoneMoved = true;
            }

            if (!_actionReachedBackStop && pullBack >= 0.052f)
            {
                _actionReachedBackStop = true;
                OpenXRBridge.TriggerOffHandHaptic(0.55f, 0.035f, 185f);
                OpenXRBridge.TriggerDominantHaptic(0.35f, 0.025f, 170f);
            }

            bool releasedToSpringHome = !offGrabHeld;
            bool pushedForwardHome = _actionReachedBackStop && pullBack <= 0.020f;

            if (releasedToSpringHome || pushedForwardHome)
            {
                bool completedCycle = _actionReachedBackStop;
                _isGrabbingSlideOrAction = false;
                _actionReachedBackStop = false;
                _curActionPullMeters = 0f;

                if (st.ActionBoneMoved && st.DiscoveredActionBone != null)
                {
                    st.DiscoveredActionBone.localPosition = st.OrigActionLocalPos;
                    st.ActionBoneMoved = false;
                }

                if (completedCycle)
                {
                    if (!st.ChamberLoaded && st.MagRounds > 0)
                    {
                        st.ChamberLoaded = true;
                        st.NeedsActionCycle = false;
                        int liveTotal = st.Archetype == FirearmArchetype.TubeShotgun
                            ? Mathf.Clamp(st.MagRounds, 1, maxMag)
                            : Mathf.Clamp(st.MagRounds, 1, maxMag);
                        if (st.Archetype != FirearmArchetype.TubeShotgun)
                        {
                            st.MagRounds = Mathf.Max(0, liveTotal - 1);
                        }
                        SetWeaponAmmoDirect(weapon, liveTotal);
                        NotifyMultiplayerReloadSafe(weapon);
                    }
                    else if (st.ChamberLoaded && weapon.Ammo > 0)
                    {
                        // Manual live-round ejection if player racks an already-chambered weapon!
                        int remaining = Mathf.Max(0, weapon.Ammo - 1);
                        st.ChamberLoaded = remaining > 0;
                        st.NeedsActionCycle = !st.ChamberLoaded;
                        SetWeaponAmmoDirect(weapon, remaining);
                    }

                    PlayWeaponReloadStageClip(weapon, stageIndex: 2);
                    OpenXRBridge.TriggerOffHandHaptic(0.75f, 0.055f, 180f);
                    OpenXRBridge.TriggerDominantHaptic(0.65f, 0.050f, 165f);
                }
            }
        }

        private static void UpdateDiegeticGuides(
            bool showMagwell,
            Vector3 magwellWorld,
            Quaternion gunRot,
            bool showAction,
            Vector3 actionWorld,
            float pullMeters)
        {
            if (_gunGuideRoot == null) return;
            bool any = showMagwell || showAction;
            if (_gunGuideRoot.activeSelf != any)
            {
                _gunGuideRoot.SetActive(any);
            }
            if (!any) return;

            float pulse = 0.55f + 0.35f * Mathf.Sin(Time.unscaledTime * 5.2f);
            if (_guideCyanMat != null) _guideCyanMat.color = new Color(0.28f, 0.90f, 1.0f, pulse * 0.72f);
            if (_guideAmberMat != null) _guideAmberMat.color = new Color(1.0f, 0.80f, 0.25f, pulse * 0.88f);

            if (_magwellRingGuide != null)
            {
                if (_magwellRingGuide.activeSelf != showMagwell) _magwellRingGuide.SetActive(showMagwell);
                if (showMagwell)
                {
                    _magwellRingGuide.transform.SetPositionAndRotation(magwellWorld, gunRot);
                }
            }

            if (_actionRailGuide != null)
            {
                if (_actionRailGuide.activeSelf != showAction) _actionRailGuide.SetActive(showAction);
                if (showAction)
                {
                    _actionRailGuide.transform.SetPositionAndRotation(actionWorld, gunRot);
                    if (_actionHandleKnob != null)
                    {
                        _actionHandleKnob.transform.localPosition = new Vector3(0f, 0f, -pullMeters);
                    }
                }
            }
        }

        private static void NotifyMultiplayerReloadSafe(Weapon weapon)
        {
            try
            {
                if (Server.Instance != null && weapon != null)
                {
                    Server.Instance.ReloadWeapon(weapon);
                }
            }
            catch
            {
                // Safe guard
            }
        }

        [HarmonyPatch("GunClippingRay")]
        [HarmonyPrefix]
        public static bool GunClippingRayPrefix(Weapon __instance, ref RaycastHit __result)
        {
            if (!IsLocalVRItem(__instance)) return true;

            Attachments att = __instance.Attachments;
            if (att == null || att.FirePoint == null) return true;

            Vector3 origin = __instance.SwayRotAroundTransform != null
                ? __instance.SwayRotAroundTransform.position
                : VRRigManager.Instance.DominantGripWorldPos;
            Vector3 target = att.FirePoint.position + att.FirePoint.forward * 0.08f;
            Vector3 dir = target - origin;

            int mask = (int)GameInfo.LevelLayer | (int)GameInfo.PlayerLayer | (int)GameInfo.ItemLayer | (int)GameInfo.NpcLayer;
            Physics.Raycast(origin, dir, out __result, dir.magnitude, mask);
            return false;
        }

        [HarmonyPatch("HandleAiming")]
        [HarmonyPostfix]
        public static void HandleAimingPostfix(Weapon __instance)
        {
            if (!IsLocalVRItem(__instance) || __instance.Attachments == null) return;

            if (__instance.Attachments.UseSniperUi)
            {
                PlayerUI.ToggleSniperUI(to: false);
                __instance.Attachments.ToggleSightModels(to: true);

                Transform scopePos = __instance.Attachments.SniperUiPos;
                if (scopePos != null)
                {
                    var pip = scopePos.GetComponent<VRSniperScopePiP>();
                    if (pip == null)
                    {
                        pip = scopePos.gameObject.AddComponent<VRSniperScopePiP>();
                    }
                    pip.Configure(__instance);
                }
            }
        }
    }

    public class VRSniperScopePiP : MonoBehaviour
    {
        private Weapon _weapon;
        private Camera _scopeCam;
        private RenderTexture _scopeRT;
        private GameObject _lensDisc;
        private Material _lensMat;

        public void Configure(Weapon weapon)
        {
            _weapon = weapon;
            if (_scopeRT == null)
            {
                _scopeRT = new RenderTexture(512, 512, 24, RenderTextureFormat.ARGB32)
                {
                    name = "HTFVR_SniperScopeRT",
                    antiAliasing = 2,
                    filterMode = FilterMode.Bilinear
                };
                _scopeRT.Create();

                GameObject camGo = new GameObject("HTFVR_ScopeCamera");
                camGo.transform.SetParent(transform, false);
                _scopeCam = camGo.AddComponent<Camera>();
                _scopeCam.targetTexture = _scopeRT;
                _scopeCam.nearClipPlane = 0.15f;
                _scopeCam.farClipPlane = 1500f;
                _scopeCam.enabled = false;

                var urp = camGo.GetComponent<UniversalAdditionalCameraData>() ?? camGo.AddComponent<UniversalAdditionalCameraData>();
                urp.renderPostProcessing = false;
                urp.renderShadows = false;

                _lensDisc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                _lensDisc.name = "HTFVR_ScopeLens";
                _lensDisc.transform.SetParent(transform, false);
                Destroy(_lensDisc.GetComponent<Collider>());
                _lensDisc.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                _lensDisc.transform.localScale = new Vector3(0.044f, 0.001f, 0.044f);
                _lensDisc.transform.localPosition = new Vector3(0f, 0f, -0.004f);

                Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
                _lensMat = new Material(unlit) { mainTexture = _scopeRT };
                if (_lensMat.HasProperty("_BaseMap")) _lensMat.SetTexture("_BaseMap", _scopeRT);
                _lensDisc.GetComponent<MeshRenderer>().sharedMaterial = _lensMat;

                CreateCrosshairBar("HTFVR_CrosshairH", new Vector3(0.040f, 0.0007f, 0.0005f));
                CreateCrosshairBar("HTFVR_CrosshairV", new Vector3(0.0007f, 0.040f, 0.0005f));
            }
        }

        private void CreateCrosshairBar(string barName, Vector3 scale)
        {
            GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
            bar.name = barName;
            bar.transform.SetParent(transform, false);
            Destroy(bar.GetComponent<Collider>());
            bar.transform.localPosition = new Vector3(0f, 0f, -0.0055f);
            bar.transform.localRotation = Quaternion.identity;
            bar.transform.localScale = scale;
            Shader sh = Shader.Find("Sprites/Default") ?? Shader.Find("Universal Render Pipeline/Unlit");
            bar.GetComponent<MeshRenderer>().sharedMaterial = new Material(sh) { color = Color.black };
        }

        private void LateUpdate()
        {
            if (_weapon == null || !_weapon.Attachments || !_weapon.Attachments.UseSniperUi ||
                _weapon.Holder == null || !_weapon.Holder.Owner.IsLocalClient ||
                VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive)
            {
                if (_lensDisc != null && _lensDisc.activeSelf) _lensDisc.SetActive(false);
                return;
            }

            float eyeDist = Vector3.Distance(VRRigManager.Instance.HeadWorldPos, transform.position);
            bool active = eyeDist < 0.75f && !_weapon.IsInInventory;
            if (_lensDisc != null && _lensDisc.activeSelf != active)
            {
                _lensDisc.SetActive(active);
            }
            if (!active || _scopeCam == null) return;

            Transform fp = _weapon.Attachments.FirePoint;
            if (fp != null)
            {
                _scopeCam.transform.SetPositionAndRotation(fp.position + fp.forward * 0.05f, fp.rotation);
            }
            _scopeCam.fieldOfView = Mathf.Clamp(_weapon.Attachments.AdsFov, 5f, 30f);
            if (VRRigManager.Instance.ActiveBaseCamera != null)
            {
                _scopeCam.cullingMask = VRRigManager.Instance.ActiveBaseCamera.cullingMask;
                _scopeCam.clearFlags = VRRigManager.Instance.ActiveBaseCamera.clearFlags;
                _scopeCam.backgroundColor = VRRigManager.Instance.ActiveBaseCamera.backgroundColor;
            }
            _scopeCam.Render();
        }

        private void OnDestroy()
        {
            if (_scopeRT != null)
            {
                _scopeRT.Release();
                Destroy(_scopeRT);
            }
        }
    }

    // =========================================================================
    // 2. Two-Handed Physical Bow Draw
    // =========================================================================
    [HarmonyPatch(typeof(Bow), "Update")]
    public static class BowUpdatePatch
    {
        public static void Postfix(Bow __instance)
        {
            if (!IsLocalVRItem(__instance) || !VRConfig.TwoHandedWeapons.Value) return;

            var vr = VRRigManager.Instance;
            var offState = vr.OffHandState;
            bool offPulling = offState.IsActive && (offState.squeeze > 0.55f || offState.trigger > 0.55f);
            float handDist = Vector3.Distance(vr.DominantGripWorldPos, vr.OffHandGripWorldPos);

            if (!_isPhysicalBowDrawing)
            {
                bool alreadyCharging = (bool)(BowIsChargingField?.GetValue(__instance) ?? false);
                if (offPulling && handDist < 0.28f && !alreadyCharging && __instance.Ammo > 0 && !__instance.Holder.BlockInputs)
                {
                    _isPhysicalBowDrawing = true;
                    BowBeginChargeMethod?.Invoke(__instance, null);
                    BowSendChargeMethod?.Invoke(__instance, new object[] { true });
                    OpenXRBridge.TriggerOffHandHaptic(0.35f, 0.03f, 170f);
                }
            }
            else
            {
                if (!offPulling)
                {
                    _isPhysicalBowDrawing = false;
                    __instance.PrimaryInputCancel(default);
                    OpenXRBridge.TriggerDominantHaptic(0.7f, 0.05f, 180f);
                    OpenXRBridge.TriggerOffHandHaptic(0.5f, 0.04f, 160f);
                    return;
                }

                float charge = Mathf.Clamp01((handDist - 0.12f) / 0.45f);
                BowCurChargeField?.SetValue(__instance, charge);

                AnimationState chargeAnim = BowChargeAnimField?.GetValue(__instance) as AnimationState;
                Animation anim = ToolAnimField?.GetValue(__instance) as Animation;
                if (chargeAnim != null && anim != null)
                {
                    chargeAnim.normalizedTime = charge;
                    anim.Sample();
                }

                if (handDist > 0.15f)
                {
                    Vector3 arrowDir = (vr.DominantGripWorldPos - vr.OffHandGripWorldPos).normalized;
                    __instance.SwayTransform.rotation = Quaternion.LookRotation(arrowDir, vr.DominantAimWorldRot * Vector3.up);
                }

                if (Time.unscaledTime >= _nextBowHapticTime && charge > 0.05f)
                {
                    OpenXRBridge.TriggerOffHandHaptic(charge * 0.4f, 0.035f, Mathf.Lerp(70f, 180f, charge));
                    _nextBowHapticTime = Time.unscaledTime + 0.04f;
                }
            }
        }
    }

    // =========================================================================
    // 3. Physical Arm-Swing Melee (Manual Mode) vs Trigger-Attack (Automatic Mode)
    // =========================================================================
    [HarmonyPatch(typeof(Melee))]
    public static class MeleePatches
    {
        [HarmonyPatch("PrimaryInput")]
        [HarmonyPrefix]
        public static bool PrimaryInputPrefix(Melee __instance)
        {
            if (!IsLocalVRItem(__instance)) return true;

            // In Manual VR Mode (!AutoReloadWeapons), knives/melee MUST be swung physically in real life!
            // In Automatic Mode (AutoReloadWeapons == true), pulling Trigger attacks like desktop mode!
            if (!VRConfig.AutoReloadWeapons.Value && VRConfig.PhysicalMeleeAndPunch.Value)
            {
                MeleeHoldingPrimaryField?.SetValue(__instance, false);
                MeleeQueuedAttackField?.SetValue(__instance, false);
                return false;
            }
            return true;
        }

        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static void LateUpdatePrefix(Melee __instance)
        {
            if (!IsLocalVRItem(__instance)) return;

            if (!VRConfig.AutoReloadWeapons.Value && VRConfig.PhysicalMeleeAndPunch.Value)
            {
                MeleeHoldingPrimaryField?.SetValue(__instance, false);
                MeleeQueuedAttackField?.SetValue(__instance, false);
            }

            if (!VRConfig.PhysicalMeleeAndPunch.Value) return;
            if (__instance.Holder.BlockInputs || Time.unscaledTime < _nextMeleeSwingTime) return;

            var vr = VRRigManager.Instance;
            Vector3 playerVel = __instance.Holder.Movement != null ? __instance.Holder.Movement.Velocity : Vector3.zero;
            Vector3 handLinVel = vr.DominantHandVelocity - playerVel;
            Vector3 bladeTipOffset = (vr.DominantAimWorldRot * Vector3.forward) * 0.30f;
            Vector3 bladeTipVel = handLinVel + Vector3.Cross(vr.DominantHandAngularVelocity, bladeTipOffset);

            float effectiveSpeed = Mathf.Max(handLinVel.magnitude, bladeTipVel.magnitude * 0.85f);
            if (effectiveSpeed > 1.85f)
            {
                bool canAttack = (bool)(MeleeCanAttackMethod?.Invoke(__instance, null) ?? false);
                if (canAttack)
                {
                    _nextMeleeSwingTime = Time.unscaledTime + 0.25f;
                    _pendingVRMeleeSwingDir = bladeTipVel.sqrMagnitude > 0.1f ? bladeTipVel.normalized : (vr.DominantAimWorldRot * Vector3.forward);
                    MeleeFindTargetMethod?.Invoke(__instance, new object[] { false, 0 });
                    _pendingVRMeleeSwingDir = Vector3.zero;
                    OpenXRBridge.TriggerDominantHaptic(0.48f, 0.04f, 140f);
                }
            }
        }

        [HarmonyPatch("FindAttackTarget")]
        [HarmonyPrefix]
        public static void FindTargetPrefix(Melee __instance, out (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            __state = default;
            if (!IsLocalVRItem(__instance) || __instance.Holder.CamObject == null) return;

            Transform camObj = __instance.Holder.CamObject;
            __state = (camObj.position, camObj.rotation, true);

            var vr = VRRigManager.Instance;
            Vector3 aimFwd = vr.DominantAimWorldRot * Vector3.forward;
            Vector3 castDir = _pendingVRMeleeSwingDir.sqrMagnitude > 0.2f
                ? Vector3.Slerp(aimFwd, _pendingVRMeleeSwingDir, 0.38f).normalized
                : aimFwd;
            Quaternion castRot = castDir.sqrMagnitude > 0.1f
                ? Quaternion.LookRotation(castDir, vr.DominantAimWorldRot * Vector3.up)
                : vr.DominantAimWorldRot;

            // Pull SphereCast origin slightly behind the hand so fish/creatures touching the blade are NEVER missed by SphereCastAll!
            Vector3 origin = vr.DominantGripWorldPos - castDir * 0.26f;
            camObj.SetPositionAndRotation(origin, castRot);
        }

        [HarmonyPatch("FindAttackTarget")]
        [HarmonyPostfix]
        public static void FindTargetPostfix(Melee __instance, (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            if (__state.restore && __instance.Holder != null && __instance.Holder.CamObject != null)
            {
                __instance.Holder.CamObject.SetPositionAndRotation(__state.pos, __state.rot);
            }
        }

        [HarmonyPatch("HitTarget")]
        [HarmonyPrefix]
        public static void HitTargetPrefix(Melee __instance, out (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            __state = default;
            if (!IsLocalVRItem(__instance) || __instance.Holder.CamObject == null) return;

            Transform camObj = __instance.Holder.CamObject;
            __state = (camObj.position, camObj.rotation, true);

            var vr = VRRigManager.Instance;
            camObj.SetPositionAndRotation(vr.DominantGripWorldPos, vr.DominantAimWorldRot);
            OpenXRBridge.TriggerDominantHaptic(0.88f, 0.07f, 115f);
        }

        [HarmonyPatch("HitTarget")]
        [HarmonyPostfix]
        public static void HitTargetPostfix(Melee __instance, (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            if (__state.restore && __instance.Holder != null && __instance.Holder.CamObject != null)
            {
                __instance.Holder.CamObject.SetPositionAndRotation(__state.pos, __state.rot);
            }
        }
    }

    // =========================================================================
    // 4. Physical Left/Right Bare-Handed VR Punching (Manual vs Automatic Mode)
    // =========================================================================
    [HarmonyPatch(typeof(PlayerPunching))]
    public static class PlayerPunchingPatches
    {
        private static bool IsLocalVRPunch(PlayerPunching instance, out Player player)
        {
            player = PunchPlayerField?.GetValue(instance) as Player;
            return VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive &&
                   player != null && player.Owner != null && player.Owner.IsLocalClient;
        }

        [HarmonyPatch("PunchInput")]
        [HarmonyPrefix]
        public static bool PunchInputPrefix(PlayerPunching __instance)
        {
            if (!IsLocalVRPunch(__instance, out _)) return true;

            // In Manual VR Mode (!AutoReloadWeapons), punches require physically swinging your arms in real life!
            // In Automatic Mode (AutoReloadWeapons == true), pulling Trigger punches like desktop mode!
            if (!VRConfig.AutoReloadWeapons.Value && VRConfig.PhysicalMeleeAndPunch.Value)
            {
                PunchHoldingInputField?.SetValue(__instance, false);
                PunchQueuedField?.SetValue(__instance, false);
                return false;
            }
            return true;
        }

        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        public static void UpdatePrefix(PlayerPunching __instance)
        {
            if (!IsLocalVRPunch(__instance, out Player player)) return;

            if (!VRConfig.AutoReloadWeapons.Value && VRConfig.PhysicalMeleeAndPunch.Value)
            {
                PunchHoldingInputField?.SetValue(__instance, false);
                PunchQueuedField?.SetValue(__instance, false);
            }

            if (!VRConfig.PhysicalMeleeAndPunch.Value) return;
            if (player.BlockInputs || player.Holding.HeldItem != null || Boat.IsDrivingLocally || Boat.WantToDrive) return;

            var vr = VRRigManager.Instance;
            Vector3 baseVel = player.Movement != null ? player.Movement.Velocity : Vector3.zero;
            bool canPunch = (bool)(PunchCanPunchMethod?.Invoke(__instance, null) ?? false);
            if (!canPunch) return;

            // Check Right Fist (side = 0)
            Vector3 rightRelVel = vr.RightHandVelocity - baseVel;
            if (vr.CurrentFrame.handRight.IsActive && rightRelVel.magnitude > 1.95f && Time.unscaledTime >= _nextPunchTimeRight)
            {
                if (Vector3.Dot(rightRelVel.normalized, vr.HeadWorldRot * Vector3.forward) > 0.05f)
                {
                    _nextPunchTimeRight = Time.unscaledTime + 0.25f;
                    PunchLastSideField?.SetValue(__instance, 1);
                    _pendingVRPunchSide = 0;
                    _pendingVRPunchDir = rightRelVel.normalized;
                    PunchFindTargetMethod?.Invoke(__instance, new object[] { false, 0 });
                    _pendingVRPunchSide = -1;
                    _pendingVRPunchDir = Vector3.zero;
                    OpenXRBridge.TriggerHaptic(true, 0.48f, 0.04f, 140f);
                    return;
                }
            }

            // Check Left Fist (side = 1)
            Vector3 leftRelVel = vr.LeftHandVelocity - baseVel;
            if (vr.CurrentFrame.handLeft.IsActive && leftRelVel.magnitude > 1.95f && Time.unscaledTime >= _nextPunchTimeLeft)
            {
                if (Vector3.Dot(leftRelVel.normalized, vr.HeadWorldRot * Vector3.forward) > 0.05f)
                {
                    _nextPunchTimeLeft = Time.unscaledTime + 0.25f;
                    PunchLastSideField?.SetValue(__instance, 0);
                    _pendingVRPunchSide = 1;
                    _pendingVRPunchDir = leftRelVel.normalized;
                    PunchFindTargetMethod?.Invoke(__instance, new object[] { false, 1 });
                    _pendingVRPunchSide = -1;
                    _pendingVRPunchDir = Vector3.zero;
                    OpenXRBridge.TriggerHaptic(false, 0.48f, 0.04f, 140f);
                }
            }
        }

        [HarmonyPatch("FindPunchTarget")]
        [HarmonyPrefix]
        public static void FindPunchPrefix(PlayerPunching __instance, int side, out (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            __state = default;
            if (!IsLocalVRPunch(__instance, out Player player) || player.CamObject == null) return;

            Transform camObj = player.CamObject;
            __state = (camObj.position, camObj.rotation, true);

            var vr = VRRigManager.Instance;
            int actualSide = _pendingVRPunchSide >= 0 ? _pendingVRPunchSide : side;
            Vector3 fistPos = actualSide == 1 ? vr.LeftGripWorldPos : vr.RightGripWorldPos;
            Vector3 punchDir = _pendingVRPunchDir.sqrMagnitude > 0.2f
                ? _pendingVRPunchDir
                : ((actualSide == 1 ? vr.LeftAimWorldRot : vr.RightAimWorldRot) * Vector3.forward);
            Quaternion aimRot = Quaternion.LookRotation(punchDir, Vector3.up);

            // Offset SphereCast start 0.24m behind the fist so point-blank punches connect reliably!
            camObj.SetPositionAndRotation(fistPos - punchDir * 0.24f, aimRot);
        }

        [HarmonyPatch("FindPunchTarget")]
        [HarmonyPostfix]
        public static void FindPunchPostfix(PlayerPunching __instance, (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            if (__state.restore && IsLocalVRPunch(__instance, out Player player) && player.CamObject != null)
            {
                player.CamObject.SetPositionAndRotation(__state.pos, __state.rot);
            }
        }

        [HarmonyPatch("HitTarget")]
        [HarmonyPrefix]
        public static void HitTargetPrefix(PlayerPunching __instance, int side, out (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            __state = default;
            if (!IsLocalVRPunch(__instance, out Player player) || player.CamObject == null) return;

            Transform camObj = player.CamObject;
            __state = (camObj.position, camObj.rotation, true);

            var vr = VRRigManager.Instance;
            Vector3 origin = side == 1 ? vr.LeftGripWorldPos : vr.RightGripWorldPos;
            Quaternion aimRot = side == 1 ? vr.LeftAimWorldRot : vr.RightAimWorldRot;
            camObj.SetPositionAndRotation(origin, aimRot);

            OpenXRBridge.TriggerHaptic(side == 0, 0.85f, 0.065f, 115f);
        }

        [HarmonyPatch("HitTarget")]
        [HarmonyPostfix]
        public static void HitTargetPostfix(PlayerPunching __instance, (Vector3 pos, Quaternion rot, bool restore) __state)
        {
            if (__state.restore && IsLocalVRPunch(__instance, out Player player) && player.CamObject != null)
            {
                player.CamObject.SetPositionAndRotation(__state.pos, __state.rot);
            }
        }
    }

    // =========================================================================
    // 5. Physical Mouth Proximity Eating & Drinking
    // =========================================================================
    [HarmonyPatch(typeof(PlayerEating), "Update")]
    public static class PlayerEatingUpdatePatch
    {
        public static void Prefix(PlayerEating __instance)
        {
            if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive || !VRConfig.PhysicalMouthEating.Value)
            {
                IsPhysicalMouthEating = false;
                return;
            }

            Player player = EatPlayerField?.GetValue(__instance) as Player;
            if (player == null || player.Owner == null || !player.Owner.IsLocalClient) return;

            Item held = player.Holding != null ? player.Holding.HeldItem : null;
            if (held == null || held.Creature == null || player.BlockInputs)
            {
                if (IsPhysicalMouthEating)
                {
                    IsPhysicalMouthEating = false;
                    StopEatingMethod?.Invoke(__instance, null);
                }
                return;
            }

            Vector3 mouthPos = VRRigManager.Instance.HeadWorldPos + (VRRigManager.Instance.HeadWorldRot * new Vector3(0f, -0.09f, 0.08f));
            float distHand = Vector3.Distance(VRRigManager.Instance.DominantGripWorldPos, mouthPos);
            float distItem = Vector3.Distance(held.transform.position, mouthPos);
            float minDist = Mathf.Min(distHand, distItem);

            bool localEating = (bool)(EatLocalIsEatingField?.GetValue(__instance) ?? false);

            if (minDist < 0.25f)
            {
                if (!localEating)
                {
                    IsPhysicalMouthEating = true;
                    StartEatingMethod?.Invoke(__instance, null);
                }
                if (IsPhysicalMouthEating)
                {
                    OpenXRBridge.TriggerDominantHaptic(0.18f, 0.03f, 95f);
                }
            }
            else if (minDist > 0.34f && IsPhysicalMouthEating)
            {
                IsPhysicalMouthEating = false;
                StopEatingMethod?.Invoke(__instance, null);
            }
        }
    }

    // =========================================================================
    // 6. Physical Boat Steering Wheel & Throttle Lever
    // =========================================================================
    [HarmonyPatch(typeof(Boat), "TickUpdate")]
    public static class BoatTickUpdatePatch
    {
        public static void Prefix(Boat __instance)
        {
            if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive || !VRConfig.PhysicalBoatControls.Value)
            {
                _wasGrabbingWheel = false;
                _isGrabbingThrottle = false;
                return;
            }

            if (!Boat.IsDrivingLocally || __instance.Driver == null || !__instance.Driver.Owner.IsLocalClient)
            {
                _wasGrabbingWheel = false;
                _isGrabbingThrottle = false;
                return;
            }

            var vr = VRRigManager.Instance;
            Transform wheel = BoatSteeringWheelField?.GetValue(__instance) as Transform;
            Transform throttle = BoatThrottleField?.GetValue(__instance) as Transform;
            if (BoatLocalDriveInputField == null) return;

            half2 driveInput = (half2)BoatLocalDriveInputField.GetValue(__instance);
            float outSteer = (float)driveInput.x;
            float outThrottle = (float)driveInput.y;
            bool modified = false;

            // 1) Physical Steering Wheel
            if (wheel != null)
            {
                bool leftGrab = vr.CurrentFrame.handLeft.IsActive && vr.CurrentFrame.handLeft.squeeze > 0.5f &&
                                Vector3.Distance(vr.LeftGripWorldPos, wheel.position) < 0.38f;
                bool rightGrab = vr.CurrentFrame.handRight.IsActive && vr.CurrentFrame.handRight.squeeze > 0.5f &&
                                 Vector3.Distance(vr.RightGripWorldPos, wheel.position) < 0.38f;

                int activeHand = rightGrab ? 1 : (leftGrab ? 0 : -1);
                if (activeHand >= 0)
                {
                    Vector3 handPos = activeHand == 1 ? vr.RightGripWorldPos : vr.LeftGripWorldPos;
                    Vector3 localOnBoat = __instance.VisualBoat.InverseTransformPoint(handPos) - __instance.VisualBoat.InverseTransformPoint(wheel.position);
                    float angleDeg = Mathf.Atan2(localOnBoat.y, localOnBoat.x) * Mathf.Rad2Deg;

                    if (!_wasGrabbingWheel || _wheelHandIdx != activeHand)
                    {
                        _wasGrabbingWheel = true;
                        _wheelHandIdx = activeHand;
                        _lastWheelHandAngle = angleDeg;
                        _accumWheelSteer = outSteer;
                    }
                    else
                    {
                        float delta = -Mathf.DeltaAngle(_lastWheelHandAngle, angleDeg);
                        _lastWheelHandAngle = angleDeg;
                        _accumWheelSteer = Mathf.Clamp(_accumWheelSteer + delta / 95f, -1f, 1f);
                        outSteer = _accumWheelSteer;
                        modified = true;
                    }
                }
                else
                {
                    _wasGrabbingWheel = false;
                    _wheelHandIdx = -1;
                }
            }

            // 2) Physical Throttle Lever
            _isGrabbingThrottle = false;
            if (throttle != null)
            {
                bool leftThrottle = _wheelHandIdx != 0 && vr.CurrentFrame.handLeft.IsActive && vr.CurrentFrame.handLeft.squeeze > 0.5f &&
                                    Vector3.Distance(vr.LeftGripWorldPos, throttle.position) < 0.34f;
                bool rightThrottle = _wheelHandIdx != 1 && vr.CurrentFrame.handRight.IsActive && vr.CurrentFrame.handRight.squeeze > 0.5f &&
                                     Vector3.Distance(vr.RightGripWorldPos, throttle.position) < 0.34f;

                if (leftThrottle || rightThrottle)
                {
                    _isGrabbingThrottle = true;
                    Vector3 handPos = rightThrottle ? vr.RightGripWorldPos : vr.LeftGripWorldPos;
                    Vector3 offset = handPos - throttle.position;
                    float forwardPush = Vector3.Dot(offset, __instance.VisualBoat.forward);
                    outThrottle = Mathf.Clamp(forwardPush / 0.16f, -1f, 1f);
                    modified = true;

                    if (Time.unscaledTime >= _nextBoatHapticTime && Mathf.Abs(outThrottle) > 0.15f)
                    {
                        OpenXRBridge.TriggerHaptic(rightThrottle, Mathf.Abs(outThrottle) * 0.25f, 0.04f, 65f);
                        _nextBoatHapticTime = Time.unscaledTime + 0.05f;
                    }
                }
            }

            if (modified)
            {
                BoatLocalDriveInputField.SetValue(__instance, new half2((half)outSteer, (half)outThrottle));
            }
        }
    }
}
