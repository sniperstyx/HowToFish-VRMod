using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace HowToFishVR;

public static class VRFishingPatches
{
    private static readonly FieldInfo RodTipJointField = AccessTools.Field(typeof(FishingRod), "_tipJoint");
    private static readonly FieldInfo RodCrankField = AccessTools.Field(typeof(FishingRod), "_crank");
    private static readonly FieldInfo RodCrankHandHolderField = AccessTools.Field(typeof(FishingRod), "_crankHandleHandHolder");
    private static readonly FieldInfo RodIsPullingBackField = AccessTools.Field(typeof(FishingRod), "_isPullingBack");
    private static readonly FieldInfo RodIsReelingOutField = AccessTools.Field(typeof(FishingRod), "_isReelingOut");
    private static readonly FieldInfo RodIsReelingInField = AccessTools.Field(typeof(FishingRod), "_isReelingIn");
    private static readonly FieldInfo RodCurRodRotField = AccessTools.Field(typeof(FishingRod), "_curRodRot");
    private static readonly FieldInfo RodCurLineMultiField = AccessTools.Field(typeof(FishingRod), "_curLineLengthMulti");
    private static readonly FieldInfo RodReelSpeedMultiField = AccessTools.Field(typeof(FishingRod), "_curReelSpeedMulti");
    private static readonly FieldInfo RodMaxBendForceField = AccessTools.Field(typeof(FishingRod), "_baitJointForceForMaxBend");
    private static readonly MethodInfo DecreaseLineMethod = AccessTools.Method(typeof(FishingRod), "DecreaseLineLength");
    private static readonly MethodInfo SetPullBackAnimMethod = AccessTools.Method(typeof(FishingRod), "SetPullBackAnimation");

    private static readonly FieldInfo CastBailOpenField = AccessTools.Field(typeof(FishingRodCast), "_bailOpen");
    private static readonly MethodInfo CastStartCastMethod = AccessTools.Method(typeof(FishingRodCast), "StartCast");
    private static readonly MethodInfo CastStartReelMethod = AccessTools.Method(typeof(FishingRodCast), "StartReel");

    private static readonly MethodInfo CrabStopReelOutMethod = AccessTools.Method(typeof(FishingRodCrab), "StopReelOut");

    // Per-frame VR fishing state for local player's held rod
    private static FishingRod _activeRod;
    private static Vector3 _lastTipPos;
    private static Vector3 _smoothedTipVel;
    private static float _peakSwingSpeedDuringBail;
    private static Vector3 _peakSwingVelDir;

    private static bool _wasCranking;
    private static float _lastCrankAngleDeg;
    private static float _accumCrankDegrees;
    private static float _nextTensionHapticTime;

    public static bool IsCrankingReel =>
        _wasCranking &&
        _activeRod != null &&
        _activeRod.isActiveAndEnabled &&
        !_activeRod.IsInInventory &&
        Player.LocalPlayer != null &&
        Player.LocalPlayer.Holding != null &&
        Player.LocalPlayer.Holding.HeldItem == _activeRod;

    private static bool IsLocalVRRod(FishingRod rod)
    {
        return VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive &&
               rod != null && rod.Holder != null && rod.Holder.Owner != null && rod.Holder.Owner.IsLocalClient;
    }

    // =========================================================================
    // 1. Physical Whip Casting + Rod Pump + Tension Haptics
    // =========================================================================
    [HarmonyPatch(typeof(FishingRod), "Update")]
    public static class FishingRodUpdatePatch
    {
        public static void Postfix(FishingRod __instance)
        {
            if (!IsLocalVRRod(__instance)) return;

            var vr = VRRigManager.Instance;
            Rigidbody tipJoint = RodTipJointField?.GetValue(__instance) as Rigidbody;
            Vector3 tipPos = tipJoint != null ? tipJoint.position : (__instance.SwayTransform.position + __instance.SwayTransform.forward * 1.4f);

            if (_activeRod != __instance)
            {
                _activeRod = __instance;
                _lastTipPos = tipPos;
                _smoothedTipVel = Vector3.zero;
                _peakSwingSpeedDuringBail = 0f;
                _wasCranking = false;
                _accumCrankDegrees = 0f;
            }

            float dt = Mathf.Max(Time.deltaTime, 0.0005f);
            Vector3 rawTipVel = (tipPos - _lastTipPos) / dt;
            _lastTipPos = tipPos;
            _smoothedTipVel = Vector3.Lerp(_smoothedTipVel, rawTipVel, Mathf.Clamp01(22f * dt));

            // Track peak forward/upward whip velocity while bail/pullback is held
            bool isPullingBack = (bool)(RodIsPullingBackField?.GetValue(__instance) ?? false);
            bool isReelingOut = (bool)(RodIsReelingOutField?.GetValue(__instance) ?? false);
            int curLineMulti = (int)(RodCurLineMultiField?.GetValue(__instance) ?? 0);

            if (isPullingBack && VRConfig.PhysicalFishingCast.Value)
            {
                // Suppress the flatscreen pullback animation if the user is actively moving their VR arm so the rod stays 1:1 in their hand!
                float handSpeed = (vr.DominantHandVelocity - __instance.Holder.Movement.Velocity).magnitude;
                float tipSpeed = (_smoothedTipVel - __instance.Holder.Movement.Velocity).magnitude;

                if (tipSpeed > 1.3f || handSpeed > 0.45f)
                {
                    SetPullBackAnimMethod?.Invoke(__instance, new object[] { 0f });
                }

                Vector3 relTipVel = _smoothedTipVel - __instance.Holder.Movement.Velocity;
                if (relTipVel.magnitude > _peakSwingSpeedDuringBail)
                {
                    _peakSwingSpeedDuringBail = relTipVel.magnitude;
                    _peakSwingVelDir = relTipVel.normalized;
                }
            }

            // Upward physical rod pump while reeling in boosts reel speed & pulls hooked catch!
            if (!isReelingOut && curLineMulti > 1)
            {
                float relUpVel = _smoothedTipVel.y - __instance.Holder.Movement.Velocity.y;
                if (relUpVel > 1.25f)
                {
                    float curMulti = (float)(RodReelSpeedMultiField?.GetValue(__instance) ?? 1f);
                    curMulti = Mathf.Min(curMulti + relUpVel * 0.45f * dt, 3.2f);
                    RodReelSpeedMultiField?.SetValue(__instance, curMulti);
                }
            }

            // Physical Left-Hand Reel Cranking
            if (VRConfig.PhysicalFishingReel.Value)
            {
                UpdatePhysicalReelCranking(__instance, vr, isPullingBack, isReelingOut);
            }

            // Continuous Line Tension Haptics on Dominant Controller
            if (__instance.Bait != null && Time.unscaledTime >= _nextTensionHapticTime)
            {
                float maxForce = Mathf.Max(25f, (float)(RodMaxBendForceField?.GetValue(__instance) ?? 250f));
                float forceRatio = Mathf.Clamp01(__instance.Bait.JointForceMagnitude / maxForce);
                if (__instance.Bait.ItemOnBait != null)
                {
                    forceRatio = Mathf.Max(forceRatio, 0.22f);
                }

                if (forceRatio > 0.08f)
                {
                    OpenXRBridge.TriggerDominantHaptic(forceRatio * 0.65f, 0.045f, Mathf.Lerp(60f, 190f, forceRatio));
                    _nextTensionHapticTime = Time.unscaledTime + 0.035f;
                }
            }
        }

        private static void UpdatePhysicalReelCranking(FishingRod rod, VRRigManager vr, bool isPullingBack, bool isReelingOut)
        {
            Transform crank = RodCrankField?.GetValue(rod) as Transform;
            if (crank == null) return;

            var offHand = vr.OffHandState;
            Vector3 offPos = vr.OffHandGripWorldPos;
            float distToCrank = Vector3.Distance(offPos, crank.position);

            bool grabbingCrank = offHand.IsActive && offHand.squeeze > 0.5f && distToCrank < 0.28f && !isPullingBack;
            if (!grabbingCrank)
            {
                _wasCranking = false;
                return;
            }

            // Snap visual off-hand to crank handle holder!
            Transform crankHandHolder = RodCrankHandHolderField?.GetValue(rod) as Transform;
            if (crankHandHolder != null)
            {
                VRPlayerAndHandsPatches.OffHandSnapActive = true;
                VRPlayerAndHandsPatches.OffHandSnapPos = crankHandHolder.position;
                VRPlayerAndHandsPatches.OffHandSnapRot = crankHandHolder.rotation;
                VRPlayerAndHandsPatches.OffHandSnapIsRawBoneRot = vr.IsRightDominant;
            }

            // Project off-hand position into the rod's YZ plane around the crank axle
            Vector3 localCrank = rod.SwayTransform.InverseTransformPoint(crank.position);
            Vector3 localHand = rod.SwayTransform.InverseTransformPoint(offPos);
            Vector2 radial = new Vector2(localHand.z - localCrank.z, localHand.y - localCrank.y);

            if (radial.sqrMagnitude < 0.0004f) return;

            float angleDeg = Mathf.Atan2(radial.y, radial.x) * Mathf.Rad2Deg;
            if (!_wasCranking)
            {
                _wasCranking = true;
                _lastCrankAngleDeg = angleDeg;
                _accumCrankDegrees = 0f;

                // Grabbing the crank handle while line is flying out closes the bail cleanly!
                if (isReelingOut)
                {
                    if (rod is FishingRodCast castRod)
                        CastStartReelMethod?.Invoke(castRod, null);
                    else if (rod is FishingRodCrab crabRod)
                        CrabStopReelOutMethod?.Invoke(crabRod, null);

                    OpenXRBridge.TriggerOffHandHaptic(0.35f, 0.03f, 200f);
                }
                return;
            }

            float deltaDeg = Mathf.DeltaAngle(_lastCrankAngleDeg, angleDeg);
            _lastCrankAngleDeg = angleDeg;

            // Accept cranking in either circular direction when intentional (> 1.5 deg) so left/right handed players never fight polarity
            _accumCrankDegrees += Mathf.Abs(deltaDeg);

            const float DegreesPerReelStep = 36f;
            while (_accumCrankDegrees >= DegreesPerReelStep)
            {
                _accumCrankDegrees -= DegreesPerReelStep;
                DecreaseLineMethod?.Invoke(rod, new object[] { 1 });

                float curMulti = (float)(RodReelSpeedMultiField?.GetValue(rod) ?? 1f);
                RodReelSpeedMultiField?.SetValue(rod, Mathf.Min(curMulti + 0.12f, 2.8f));

                // Tactile mechanical reel click on the cranking hand!
                OpenXRBridge.TriggerOffHandHaptic(0.22f, 0.014f, 185f);
            }
        }
    }

    // =========================================================================
    // 2. Physical Whip Release on ReelOutCancelled
    // =========================================================================
    [HarmonyPatch(typeof(FishingRod), "ReelOutCancelled")]
    public static class FishingRodReelOutCancelledPatch
    {
        public static bool Prefix(FishingRod __instance)
        {
            if (!IsLocalVRRod(__instance) || !VRConfig.PhysicalFishingCast.Value) return true;

            bool isPullingBack = (bool)(RodIsPullingBackField?.GetValue(__instance) ?? false);
            if (!isPullingBack || __instance.Bait == null || __instance.Bait.ItemOnBait != null) return true;

            Vector3 playerVel = __instance.Holder.Movement != null ? __instance.Holder.Movement.Velocity : Vector3.zero;
            Vector3 curRelTipVel = _smoothedTipVel - playerVel;
            float releaseSpeed = Mathf.Max(curRelTipVel.magnitude, _peakSwingSpeedDuringBail * 0.85f);

            // If player barely moved their arm (< 1.5 m/s), fall back to standard button-cast animation!
            if (releaseSpeed < 1.5f)
            {
                _peakSwingSpeedDuringBail = 0f;
                return true;
            }

            // Physical 6DOF VR Cast!
            RodIsPullingBackField?.SetValue(__instance, false);
            RodCurRodRotField?.SetValue(__instance, 0f);
            SetPullBackAnimMethod?.Invoke(__instance, new object[] { 0f });

            Vector3 launchDir = curRelTipVel.sqrMagnitude > 2.0f ? curRelTipVel.normalized : _peakSwingVelDir;
            // Ensure slight upward arc if whipping horizontally so bait flies naturally over water
            if (launchDir.y < 0.12f)
            {
                launchDir = (launchDir + Vector3.up * 0.22f).normalized;
            }

            if (__instance is FishingRodCast castRod)
            {
                CastBailOpenField?.SetValue(castRod, true);
                __instance.Bait.ToggleDrag(enable: false);
                CastStartCastMethod?.Invoke(castRod, null);
            }
            else if (__instance is FishingRodCrab)
            {
                RodIsReelingOutField?.SetValue(__instance, true);
                __instance.Bait.ToggleDrag(enable: false);
            }

            Rigidbody tipJoint = RodTipJointField?.GetValue(__instance) as Rigidbody;
            if (__instance.Bait.Rig != null && !__instance.Bait.Rig.isKinematic)
            {
                if (tipJoint != null && Vector3.Distance(__instance.Bait.transform.position, tipJoint.position) < 2.2f)
                {
                    __instance.Bait.transform.position = tipJoint.position + launchDir * 0.35f;
                }
                float launchSpeed = Mathf.Clamp(releaseSpeed * VRConfig.CastVelocityMultiplier.Value * 1.45f, 5f, 36f);
                __instance.Bait.Rig.linearVelocity = playerVel + launchDir * launchSpeed;
            }

            AudioManager.PlayRandomPlayerClip("FishingrodCast_V", 1, 2, __instance.Holder, variation: false, AudioDistance.VeryShort, Mathf.Clamp01(releaseSpeed / 10f));
            OpenXRBridge.TriggerDominantHaptic(0.55f, 0.06f, 150f);

            _peakSwingSpeedDuringBail = 0f;
            return false;
        }
    }

    // =========================================================================
    // 3. ResetBait Raycast from VR Rod Tip instead of HMD Face
    // =========================================================================
    [HarmonyPatch(typeof(FishingRod), "ResetBait")]
    public static class FishingRodResetBaitPatch
    {
        public static void Postfix(FishingRod __instance)
        {
            if (!IsLocalVRRod(__instance) || __instance.Bait == null) return;
            Rigidbody tipJoint = RodTipJointField?.GetValue(__instance) as Rigidbody;
            if (tipJoint == null) return;

            Vector3 targetPos = tipJoint.position + Vector3.down * 0.35f;
            __instance.Bait.transform.position = targetPos;
            if (__instance.Bait.Rig != null && !__instance.Bait.Rig.isKinematic)
            {
                __instance.Bait.Rig.linearVelocity = Vector3.zero;
            }
        }
    }

    // =========================================================================
    // 4. Fish-Bite Strike Haptics on Bait.AddItemOnBait
    // =========================================================================
    [HarmonyPatch(typeof(Bait), "AddItemOnBait")]
    public static class BaitStrikeHapticPatch
    {
        public static void Postfix(Bait __instance, Item item, bool asServer)
        {
            if (asServer || item == null || __instance.FishingRod == null) return;
            if (!IsLocalVRRod(__instance.FishingRod)) return;

            // Strong exciting fish-strike pulse on both hands if cranking, or dominant rod hand!
            OpenXRBridge.TriggerDominantHaptic(0.95f, 0.14f, 110f);
            if (_wasCranking)
            {
                OpenXRBridge.TriggerOffHandHaptic(0.65f, 0.10f, 110f);
            }
        }
    }
}
