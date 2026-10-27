using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace HowToFishVR;

public static class VRPlayerAndHandsPatches
{
    // Cached reflection fields
    private static readonly FieldInfo CamPlayerField = AccessTools.Field(typeof(PlayerCamera), "_player");
    private static readonly FieldInfo CamRotField = AccessTools.Field(typeof(PlayerCamera), "_rot");
    private static readonly PropertyInfo CamPosNoEffectsProp = AccessTools.Property(typeof(PlayerCamera), "CamPosNoEffects");

    private static readonly FieldInfo MovePlayerField = AccessTools.Field(typeof(PlayerMovement), "_player");
    private static readonly PropertyInfo CurPlayerRotProp = AccessTools.Property(typeof(Player), "CurPlayerRot");

    private static readonly FieldInfo ToolMovePlayerField = AccessTools.Field(typeof(PlayerToolMovement), "_player");
    private static readonly FieldInfo ToolMoveToolField = AccessTools.Field(typeof(PlayerToolMovement), "_tool");
    private static readonly FieldInfo ToolMoveHoldPercentField = AccessTools.Field(typeof(PlayerToolMovement), "_holdPercent");
    private static readonly FieldInfo ToolMoveRecoilRigField = AccessTools.Field(typeof(PlayerToolMovement), "_toolRecoilRig");
    private static readonly FieldInfo ToolMoveHasNewToolField = AccessTools.Field(typeof(PlayerToolMovement), "_hasNewTool");
    private static readonly MethodInfo GlueToCameraMethod = AccessTools.Method(typeof(PlayerToolMovement), "GlueToCamera");
    private static readonly MethodInfo CalcFakeVelMethod = AccessTools.Method(typeof(PlayerToolMovement), "CalculateFakeVel");
    private static readonly MethodInfo ResetSwayRecoilMethod = AccessTools.Method(typeof(PlayerToolMovement), "ResetSwayRecoilPosRotVel");

    private static readonly FieldInfo HandsPlayerField = AccessTools.Field(typeof(PlayerHands), "_player");
    private static readonly FieldInfo HandModelRightField = AccessTools.Field(typeof(PlayerHands), "_handModelRight");
    private static readonly FieldInfo HandModelLeftField = AccessTools.Field(typeof(PlayerHands), "_handModelLeft");
    private static readonly FieldInfo FingersRightField = AccessTools.Field(typeof(PlayerHands), "_fingerTransformsRight");
    private static readonly FieldInfo FingersLeftField = AccessTools.Field(typeof(PlayerHands), "_fingerTransformsLeft");
    private static readonly FieldInfo OrigRightField = AccessTools.Field(typeof(PlayerHands), "_handTransformsOrigRight");
    private static readonly FieldInfo OrigLeftField = AccessTools.Field(typeof(PlayerHands), "_handTransformsOrigLeft");
    private static readonly FieldInfo DefaultHandAngleField = AccessTools.Field(typeof(PlayerHands), "_defaultHandAngle");
    private static readonly FieldInfo ToolAnimField = AccessTools.Field(typeof(Tool), "_anim");

    private static readonly FieldInfo ArmsIkRightField = AccessTools.Field(typeof(PlayerArms), "_ikRight");
    private static readonly FieldInfo ArmsIkLeftField = AccessTools.Field(typeof(PlayerArms), "_ikLeft");
    private static readonly FieldInfo IkStopResolvingField = AccessTools.Field(typeof(IK), "_stopResolving");

    private static readonly FieldInfo HoldPlayerField = AccessTools.Field(typeof(PlayerHolding), "_player");
    private static readonly FieldInfo HoldGrabRangeField = AccessTools.Field(typeof(PlayerHolding), "_grabRange");
    private static readonly FieldInfo HoldGrabRadiusField = AccessTools.Field(typeof(PlayerHolding), "_grabRadius");
    private static readonly FieldInfo HoldGrabbableField = AccessTools.Field(typeof(PlayerHolding), "_grabbableItem");
    private static readonly FieldInfo HoldInteractableField = AccessTools.Field(typeof(PlayerHolding), "_interactable");
    private static readonly FieldInfo HoldHeldItemField = AccessTools.Field(typeof(PlayerHolding), "_heldItem");
    private static readonly FieldInfo HoldDropForceOffsetField = AccessTools.Field(typeof(PlayerHolding), "_curDropForcePosOffset");
    private static readonly FieldInfo HoldCurHoldPercentField = AccessTools.Field(typeof(PlayerHolding), "_curHoldPercent");

    // External snap hooks for off-hand (set by VRFishingPatches / VRCombatAndBoatPatches when grabbing reel crank, foregrip, or boat controls)
    public static bool OffHandSnapActive;
    public static Vector3 OffHandSnapPos;
    public static Quaternion OffHandSnapRot = Quaternion.identity;
    public static bool OffHandSnapIsRawBoneRot;

    public static bool DominantHandSnapActive;
    public static Vector3 DominantHandSnapPos;
    public static Quaternion DominantHandSnapRot = Quaternion.identity;

    public static bool IsTwoHandingWeapon { get; private set; }

    // Ergonomic controller-to-anatomical-hand mapping:
    // Forward (+Z) = distal fingers (tilted 6 deg downward along controller aim/grip blend)
    // Up (+Y)      = pinky-to-thumb axis (tilted ~20 deg inward so palms naturally face each other around the grip)
    private static readonly Quaternion RightControllerToAnatomical =
        Quaternion.LookRotation(new Vector3(0f, -0.10f, 0.995f).normalized, new Vector3(-0.34f, 0.94f, 0f).normalized);
    private static readonly Quaternion LeftControllerToAnatomical =
        Quaternion.LookRotation(new Vector3(0f, -0.10f, 0.995f).normalized, new Vector3(0.34f, 0.94f, 0f).normalized);

    private sealed class HandSideCalibration
    {
        public Quaternion BoneOffset = Quaternion.identity;
        public Vector3[] LocalCurlAxes = Array.Empty<Vector3>();
        public byte[] FingerRoles = Array.Empty<byte>(); // 0 = middle/ring/pinky, 1 = thumb, 2 = index
        public byte[] FingerJointDepth = Array.Empty<byte>(); // 0 = proximal, 1 = intermediate, 2 = distal
        public bool Valid;
    }

    private sealed class HandRigCalibration
    {
        public PlayerHands HandsRef;
        public readonly HandSideCalibration Right = new HandSideCalibration();
        public readonly HandSideCalibration Left = new HandSideCalibration();
    }

    private static readonly Dictionary<int, HandRigCalibration> HandCalibrations = new Dictionary<int, HandRigCalibration>();
    private static bool _loggedHandCalibration;

    public static bool HasHoveredPickupOrInteractable(Player player)
    {
        if (player == null || player.Holding == null) return false;
        var item = HoldGrabbableField?.GetValue(player.Holding) as Item;
        var inter = HoldInteractableField?.GetValue(player.Holding) as Interactable;
        return item != null || inter != null;
    }

    public static bool HasHoveredGrabbableItem(Player player)
    {
        if (player == null || player.Holding == null) return false;
        return HoldGrabbableField?.GetValue(player.Holding) is Item;
    }

    public static void ForceEnablePlayerArmIK(Player player)
    {
        if (player == null || player.Arms == null) return;
        player.Arms.SetIKTarget(player.Hands.HandBoneRight, player.Hands.HandBoneLeft);

        if (ArmsIkRightField?.GetValue(player.Arms) is IK ikR)
            IkStopResolvingField?.SetValue(ikR, false);
        if (ArmsIkLeftField?.GetValue(player.Arms) is IK ikL)
            IkStopResolvingField?.SetValue(ikL, false);
    }

    private static HandRigCalibration EnsureCalibrated(PlayerHands hands)
    {
        if (hands == null) return null;
        int id = RuntimeHelpers.GetHashCode(hands);
        if (HandCalibrations.TryGetValue(id, out HandRigCalibration existing) &&
            existing != null && existing.HandsRef == hands &&
            existing.Right.Valid && existing.Left.Valid)
        {
            return existing;
        }

        if (HandCalibrations.Count > 32)
        {
            var staleKeys = new List<int>();
            foreach (var kv in HandCalibrations)
            {
                if (kv.Value == null || kv.Value.HandsRef == null)
                    staleKeys.Add(kv.Key);
            }
            for (int i = 0; i < staleKeys.Count; i++)
                HandCalibrations.Remove(staleKeys[i]);
        }

        var cal = existing ?? new HandRigCalibration { HandsRef = hands };
        cal.HandsRef = hands;

        var fingersR = FingersRightField?.GetValue(hands) as Transform[];
        var fingersL = FingersLeftField?.GetValue(hands) as Transform[];
        var origR = OrigRightField?.GetValue(hands) as HandTransforms;
        var origL = OrigLeftField?.GetValue(hands) as HandTransforms;

        CalibrateSingleHand(hands.HandBoneRight, fingersR, origR, isRightHand: true, cal.Right);
        CalibrateSingleHand(hands.HandBoneLeft, fingersL, origL, isRightHand: false, cal.Left);

        HandCalibrations[id] = cal;

        if (!_loggedHandCalibration && cal.Right.Valid && cal.Left.Valid)
        {
            _loggedHandCalibration = true;
            Plugin.Log.LogInfo(
                $"Calibrated playermodel VR hand rig offsets: RightEuler={cal.Right.BoneOffset.eulerAngles}, LeftEuler={cal.Left.BoneOffset.eulerAngles}");
        }

        return cal;
    }

    private static Transform GetDeepestFingerChild(Transform root)
    {
        Transform cur = root;
        int guard = 0;
        while (cur != null && cur.childCount > 0 && guard++ < 6)
        {
            cur = cur.GetChild(0);
        }
        return cur;
    }

    private static void CalibrateSingleHand(
        Transform handBone,
        Transform[] fingers,
        HandTransforms orig,
        bool isRightHand,
        HandSideCalibration outSide)
    {
        if (handBone == null) return;

        int fingerBoneCount = fingers != null ? fingers.Length : 0;
        Quaternion[] savedLocalRots = null;

        // Temporarily restore rest-pose finger rotations so anatomical basis & curl axes are 100% invariant
        if (fingers != null && orig != null && orig.FingerRots != null)
        {
            int n = Mathf.Min(fingers.Length, orig.FingerRots.Length);
            savedLocalRots = new Quaternion[n];
            for (int i = 0; i < n; i++)
            {
                if (fingers[i] != null)
                {
                    savedLocalRots[i] = fingers[i].localRotation;
                    fingers[i].localRotation = orig.FingerRots[i];
                }
            }
        }

        try
        {
            int childCount = handBone.childCount;
            if (childCount < 2)
            {
                // Fallback for simplified rigs without 5 finger chains: Blender Y-along-bone convention
                Quaternion fallbackBasis = Quaternion.LookRotation(
                    handBone.up,
                    isRightHand ? -handBone.forward : handBone.forward);
                outSide.BoneOffset = Quaternion.Inverse(fallbackBasis) * handBone.rotation;
                outSide.Valid = true;
                return;
            }

            Vector3 wristPos = handBone.position;
            Vector3[] childDirs = new Vector3[childCount];
            float[] wristDists = new float[childCount];
            float maxWristDist = 0.0001f;
            Vector3 sumDirs = Vector3.zero;

            for (int c = 0; c < childCount; c++)
            {
                Transform ch = handBone.GetChild(c);
                Transform tip = GetDeepestFingerChild(ch);
                Vector3 toTip = (tip != null ? tip.position : ch.position) - wristPos;
                childDirs[c] = toTip.sqrMagnitude > 0.000001f ? toTip.normalized : handBone.up;
                wristDists[c] = Vector3.Distance(ch.position, wristPos);
                if (wristDists[c] > maxWristDist) maxWristDist = wristDists[c];
                sumDirs += childDirs[c];
            }

            Vector3 meanDir = sumDirs.sqrMagnitude > 0.0001f ? sumDirs.normalized : handBone.up;

            // 1. Identify Thumb child chain (by bone name first, then geometric divergence + proximity to wrist)
            int thumbIdx = -1;
            for (int c = 0; c < childCount; c++)
            {
                string cName = handBone.GetChild(c).name;
                if (!string.IsNullOrEmpty(cName) && cName.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    thumbIdx = c;
                    break;
                }
            }

            if (thumbIdx < 0)
            {
                float lowestScore = float.MaxValue;
                for (int c = 0; c < childCount; c++)
                {
                    float dot = Vector3.Dot(childDirs[c], meanDir);
                    float distRatio = wristDists[c] / maxWristDist;
                    float score = dot + 0.65f * distRatio;
                    if (score < lowestScore)
                    {
                        lowestScore = score;
                        thumbIdx = c;
                    }
                }
            }

            // 2. Measure worldDistal (wrist -> 4 non-thumb knuckles) and the FLAT metacarpal knuckle-row axis
            //    (pinky knuckle -> index knuckle) so the thumb's ~40 deg palmar drop never skews palm roll or shifts fingers sideways!
            Vector3 nonThumbDirSum = Vector3.zero;
            Vector3 nonThumbKnuckleSum = Vector3.zero;
            var nonThumbIndices = new List<int>(childCount);

            for (int c = 0; c < childCount; c++)
            {
                if (c == thumbIdx) continue;
                Transform ch = handBone.GetChild(c);
                Vector3 vKnuckle = ch.position - wristPos;
                nonThumbDirSum += vKnuckle.sqrMagnitude > 0.000001f ? vKnuckle.normalized : childDirs[c];
                nonThumbKnuckleSum += ch.position;
                nonThumbIndices.Add(c);
            }

            int nonThumbCount = nonThumbIndices.Count;
            Vector3 worldDistal = nonThumbDirSum.sqrMagnitude > 0.0001f ? nonThumbDirSum.normalized : meanDir;
            Vector3 knucklesCentroid = nonThumbCount > 0 ? (nonThumbKnuckleSum / nonThumbCount) : wristPos;

            Transform thumbRoot = handBone.GetChild(thumbIdx);
            Vector3 thumbRefPos = thumbRoot.childCount > 0 ? thumbRoot.GetChild(0).position : thumbRoot.position;

            // 3. Identify Index & Pinky knuckles across the flat 4-knuckle metacarpal row
            int indexIdx = -1;
            int pinkyIdx = -1;
            for (int k = 0; k < nonThumbCount; k++)
            {
                int c = nonThumbIndices[k];
                string cName = handBone.GetChild(c).name;
                if (string.IsNullOrEmpty(cName)) continue;
                if (indexIdx < 0 && cName.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0)
                    indexIdx = c;
                if (pinkyIdx < 0 && (cName.IndexOf("pinky", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     cName.IndexOf("little", StringComparison.OrdinalIgnoreCase) >= 0))
                    pinkyIdx = c;
            }

            if ((indexIdx < 0 || pinkyIdx < 0) && nonThumbCount >= 2)
            {
                // Find the widest-separated knuckle pair perpendicular to worldDistal -> guaranteed (Index, Pinky)
                int pairA = nonThumbIndices[0];
                int pairB = nonThumbIndices[nonThumbCount - 1];
                float maxSpanSq = -1f;
                for (int i = 0; i < nonThumbCount; i++)
                {
                    Vector3 pi = Vector3.ProjectOnPlane(handBone.GetChild(nonThumbIndices[i]).position - knucklesCentroid, worldDistal);
                    for (int j = i + 1; j < nonThumbCount; j++)
                    {
                        Vector3 pj = Vector3.ProjectOnPlane(handBone.GetChild(nonThumbIndices[j]).position - knucklesCentroid, worldDistal);
                        float spanSq = (pi - pj).sqrMagnitude;
                        if (spanSq > maxSpanSq)
                        {
                            maxSpanSq = spanSq;
                            pairA = nonThumbIndices[i];
                            pairB = nonThumbIndices[j];
                        }
                    }
                }

                float distAToThumb = (handBone.GetChild(pairA).position - thumbRoot.position).sqrMagnitude;
                float distBToThumb = (handBone.GetChild(pairB).position - thumbRoot.position).sqrMagnitude;
                if (distAToThumb <= distBToThumb)
                {
                    if (indexIdx < 0) indexIdx = pairA;
                    if (pinkyIdx < 0) pinkyIdx = pairB;
                }
                else
                {
                    if (indexIdx < 0) indexIdx = pairB;
                    if (pinkyIdx < 0) pinkyIdx = pairA;
                }
            }

            Transform indexRoot = indexIdx >= 0 ? handBone.GetChild(indexIdx) : null;
            Transform pinkyRoot = pinkyIdx >= 0 ? handBone.GetChild(pinkyIdx) : null;

            Vector3 rawKnuckleThumbDir = Vector3.zero;
            if (indexRoot != null && pinkyRoot != null && indexRoot != pinkyRoot)
            {
                rawKnuckleThumbDir = Vector3.ProjectOnPlane(indexRoot.position - pinkyRoot.position, worldDistal);
            }
            if (rawKnuckleThumbDir.sqrMagnitude < 0.000001f)
            {
                rawKnuckleThumbDir = Vector3.ProjectOnPlane(thumbRefPos - knucklesCentroid, worldDistal);
            }
            if (rawKnuckleThumbDir.sqrMagnitude < 0.000001f)
            {
                rawKnuckleThumbDir = Vector3.ProjectOnPlane(thumbRoot.position - wristPos, worldDistal);
            }

            Vector3 worldKnuckleThumbDir = rawKnuckleThumbDir.sqrMagnitude > 0.000001f
                ? rawKnuckleThumbDir.normalized
                : handBone.right;

            Quaternion anatomicalWorldBasis = Quaternion.LookRotation(worldDistal, worldKnuckleThumbDir);
            outSide.BoneOffset = Quaternion.Inverse(anatomicalWorldBasis) * handBone.rotation;

            // 4. Calibrate pure anatomical hinge flexion axes for each phalanx (orthogonal to bone shaft, zero sideways shift/twist!)
            // In anatomicalWorldBasis: +Z = distal fingers, +Y = flat pinky->index knuckle row, -X (Right) / +X (Left) = perpendicular palm inward
            Vector3 worldPalmInward = anatomicalWorldBasis * (isRightHand ? Vector3.left : Vector3.right);
            float thumbPalmCheck = Vector3.Dot(thumbRefPos - knucklesCentroid, worldPalmInward);
            if (thumbPalmCheck < -0.005f)
            {
                worldPalmInward = -worldPalmInward;
            }

            Vector3 thumbOppositionTarget = (worldPalmInward * 0.78f - worldKnuckleThumbDir * 0.46f + worldDistal * 0.42f).normalized;

            outSide.LocalCurlAxes = new Vector3[fingerBoneCount];
            outSide.FingerRoles = new byte[fingerBoneCount];
            outSide.FingerJointDepth = new byte[fingerBoneCount];

            for (int i = 0; i < fingerBoneCount; i++)
            {
                Transform fb = fingers[i];
                if (fb == null)
                {
                    outSide.LocalCurlAxes[i] = Vector3.forward;
                    continue;
                }

                bool isThumbBone = fb == thumbRoot || fb.IsChildOf(thumbRoot);
                bool isIndexBone = !isThumbBone && indexRoot != null && (fb == indexRoot || fb.IsChildOf(indexRoot));
                outSide.FingerRoles[i] = isThumbBone ? (byte)1 : (isIndexBone ? (byte)2 : (byte)0);

                byte depth = 2;
                if (fb.parent == handBone) depth = 0;
                else if (fb.parent != null && fb.parent.parent == handBone) depth = 1;
                outSide.FingerJointDepth[i] = depth;

                Vector3 boneDirWorld;
                if (fb.childCount > 0)
                {
                    Vector3 d = fb.GetChild(0).position - fb.position;
                    boneDirWorld = d.sqrMagnitude > 0.000001f ? d.normalized : worldDistal;
                }
                else if (fb.parent != null && fb.parent != handBone)
                {
                    Vector3 d = fb.position - fb.parent.position;
                    boneDirWorld = d.sqrMagnitude > 0.000001f ? d.normalized : worldDistal;
                }
                else
                {
                    boneDirWorld = isThumbBone ? (worldDistal * 0.7f + worldKnuckleThumbDir * 0.7f).normalized : worldDistal;
                }

                Quaternion stepWorld;
                if (isThumbBone)
                {
                    Vector3 halfThumb = Vector3.Slerp(boneDirWorld, thumbOppositionTarget, 0.5f).normalized;
                    stepWorld = Quaternion.FromToRotation(boneDirWorld, halfThumb);
                }
                else
                {
                    // Project phalanx into the sagittal flexion plane perpendicular to the knuckle row so curl has ZERO lateral splay!
                    Vector3 sagittalDir = Vector3.ProjectOnPlane(boneDirWorld, worldKnuckleThumbDir);
                    Vector3 startDir = sagittalDir.sqrMagnitude > 0.001f ? sagittalDir.normalized : worldDistal;
                    Vector3 halfFlex = Vector3.Slerp(startDir, worldPalmInward, 0.5f).normalized;
                    stepWorld = Quaternion.FromToRotation(startDir, halfFlex);
                }

                stepWorld.ToAngleAxis(out float angleDeg, out Vector3 worldAxis);
                if (angleDeg > 180f) angleDeg -= 360f;
                if (angleDeg < 0f) worldAxis = -worldAxis;

                if (worldAxis.sqrMagnitude > 0.0001f)
                {
                    Vector3 localAxis = fb.InverseTransformDirection(worldAxis.normalized);
                    Vector3 localShaft = fb.InverseTransformDirection(boneDirWorld);
                    if (localShaft.sqrMagnitude > 0.0001f)
                    {
                        // Remove any axial bone-shaft component so phalanges never twist around their length when curling!
                        Vector3 pureHinge = Vector3.ProjectOnPlane(localAxis, localShaft.normalized);
                        if (pureHinge.sqrMagnitude > 0.0001f)
                        {
                            localAxis = pureHinge;
                        }
                    }
                    outSide.LocalCurlAxes[i] = localAxis.sqrMagnitude > 0.0001f ? localAxis.normalized : Vector3.forward;
                }
                else
                {
                    outSide.LocalCurlAxes[i] = Vector3.forward;
                }
            }

            outSide.Valid = true;
        }
        finally
        {
            if (savedLocalRots != null && fingers != null)
            {
                for (int i = 0; i < savedLocalRots.Length; i++)
                {
                    if (fingers[i] != null)
                    {
                        fingers[i].localRotation = savedLocalRots[i];
                    }
                }
            }
        }
    }

    public static Quaternion GetCalibratedHandBoneRotation(
        PlayerHands hands,
        bool isRightHand,
        Quaternion gripWorldRot,
        Quaternion aimWorldRot)
    {
        Quaternion baseFrame = Quaternion.Slerp(gripWorldRot, aimWorldRot, 0.55f);
        Quaternion targetAnatomical = baseFrame * (isRightHand ? RightControllerToAnatomical : LeftControllerToAnatomical);

        HandRigCalibration cal = EnsureCalibrated(hands);
        if (cal != null)
        {
            HandSideCalibration side = isRightHand ? cal.Right : cal.Left;
            if (side.Valid)
            {
                return targetAnatomical * side.BoneOffset;
            }
        }

        return targetAnatomical;
    }

    public static void ApplyDynamicFingerCurls(
        PlayerHands hands,
        bool isRightHand,
        Transform[] fingers,
        HandTransforms orig,
        float trigger,
        float squeeze)
    {
        if (fingers == null || orig == null || orig.FingerRots == null) return;

        HandRigCalibration cal = EnsureCalibrated(hands);
        HandSideCalibration side = cal != null ? (isRightHand ? cal.Right : cal.Left) : null;

        int count = Mathf.Min(fingers.Length, orig.FingerRots.Length);
        for (int i = 0; i < count; i++)
        {
            if (fingers[i] == null) continue;

            byte role = (side != null && i < side.FingerRoles.Length)
                ? side.FingerRoles[i]
                : (byte)((i / 3) == 0 ? 1 : ((i / 3) == 1 ? 2 : 0));
            byte jointDepth = (side != null && i < side.FingerJointDepth.Length)
                ? side.FingerJointDepth[i]
                : (byte)(i % 3);

            float curlAmount;
            float maxCurlDeg;
            if (role == 1) // Thumb
            {
                curlAmount = Mathf.Max(trigger * 0.35f, squeeze * 0.70f);
                maxCurlDeg = jointDepth == 0 ? 22f : (jointDepth == 1 ? 34f : 38f);
            }
            else if (role == 2) // Index finger (trigger finger)
            {
                curlAmount = Mathf.Max(trigger, squeeze * 0.55f);
                maxCurlDeg = jointDepth == 0 ? 50f : (jointDepth == 1 ? 70f : 46f);
            }
            else // Middle, Ring, Pinky
            {
                curlAmount = squeeze;
                maxCurlDeg = jointDepth == 0 ? 54f : (jointDepth == 1 ? 74f : 48f);
            }

            if (side != null && side.Valid && i < side.LocalCurlAxes.Length)
            {
                Quaternion curlRot = Quaternion.AngleAxis(curlAmount * maxCurlDeg, side.LocalCurlAxes[i]);
                fingers[i].localRotation = orig.FingerRots[i] * curlRot;
            }
            else
            {
                Quaternion curlRot = Quaternion.Euler(0f, 0f, -curlAmount * 48f);
                fingers[i].localRotation = orig.FingerRots[i] * curlRot;
            }
        }
    }

    // =========================================================================
    // 1. PlayerCamera Patches
    // =========================================================================
    [HarmonyPatch(typeof(PlayerCamera))]
    public static class PlayerCameraPatches
    {
        private static bool IsLocalVR(PlayerCamera instance, out Player player)
        {
            player = CamPlayerField?.GetValue(instance) as Player;
            return VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive &&
                   player != null && player.Owner != null && player.Owner.IsLocalClient;
        }

        [HarmonyPatch("ControllerRotation")]
        [HarmonyPrefix]
        public static bool ControllerRotationPrefix(PlayerCamera __instance)
        {
            return !IsLocalVR(__instance, out _);
        }

        [HarmonyPatch("ApplyAimAssist")]
        [HarmonyPrefix]
        public static bool ApplyAimAssistPrefix(PlayerCamera __instance)
        {
            return !IsLocalVR(__instance, out _);
        }

        [HarmonyPatch("HeadRot")]
        [HarmonyPrefix]
        public static bool HeadRotPrefix(PlayerCamera __instance)
        {
            return !IsLocalVR(__instance, out _);
        }

        [HarmonyPatch("MouseMovement")]
        [HarmonyPrefix]
        public static bool MouseMovementPrefix(PlayerCamera __instance)
        {
            return !IsLocalVR(__instance, out _);
        }

        [HarmonyPatch("SetFov")]
        [HarmonyPrefix]
        public static bool SetFovPrefix(PlayerCamera __instance)
        {
            return !IsLocalVR(__instance, out _);
        }

        [HarmonyPatch("SetCamPosRot")]
        [HarmonyPrefix]
        public static bool SetCamPosRotPrefix(PlayerCamera __instance)
        {
            if (!IsLocalVR(__instance, out _)) return true;

            var vr = VRRigManager.Instance;
            vr.UpdateRigAndPoses();

            __instance.CamTransform.SetPositionAndRotation(vr.HeadWorldPos, vr.HeadWorldRot);
            CamPosNoEffectsProp?.SetValue(__instance, vr.HeadWorldPos);

            Vector3 euler = vr.HeadWorldRot.eulerAngles;
            float pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            CamRotField?.SetValue(__instance, new Vector3(pitch, euler.y, 0f));
            return false;
        }

        [HarmonyPatch("SetRot")]
        [HarmonyPrefix]
        public static bool SetRotPrefix(PlayerCamera __instance, float angle)
        {
            if (!IsLocalVR(__instance, out _)) return true;

            VRRigManager.Instance.AlignYawTo(angle);
            VRRigManager.Instance.UpdateRigAndPoses();
            __instance.CamTransform.SetPositionAndRotation(VRRigManager.Instance.HeadWorldPos, VRRigManager.Instance.HeadWorldRot);
            return false;
        }
    }

    // =========================================================================
    // 2. PlayerMovement Patches (Controller-Oriented Locomotion Option)
    // =========================================================================
    [HarmonyPatch(typeof(PlayerMovement), "Move")]
    public static class PlayerMovementMovePatch
    {
        public static void Prefix(PlayerMovement __instance, out (Quaternion origRot, bool restore) __state)
        {
            __state = (Quaternion.identity, false);

            if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive) return;
            if (VRConfig.MoveOrientation.Value != VRMoveOrientation.LeftController) return;

            var player = MovePlayerField?.GetValue(__instance) as Player;
            if (player == null || player.Owner == null || !player.Owner.IsLocalClient) return;

            __state = (player.CurPlayerRot, true);
            float ctrlYaw = VRRigManager.Instance.LeftAimWorldRot.eulerAngles.y;
            CurPlayerRotProp?.SetValue(player, Quaternion.Euler(0f, ctrlYaw, 0f));
        }

        public static void Postfix(PlayerMovement __instance, (Quaternion origRot, bool restore) __state)
        {
            if (!__state.restore) return;
            var player = MovePlayerField?.GetValue(__instance) as Player;
            if (player != null)
            {
                CurPlayerRotProp?.SetValue(player, __state.origRot);
            }
        }
    }

    // =========================================================================
    // 3. PlayerToolMovement Patches (1:1 6DOF Tool & Two-Handed Weapon Pose)
    // =========================================================================
    [HarmonyPatch(typeof(PlayerToolMovement))]
    public static class PlayerToolMovementPatches
    {
        private static void ApplyVRToolPose(PlayerToolMovement instance, Player player, Tool tool)
        {
            var vr = VRRigManager.Instance;
            ToolMoveHoldPercentField?.SetValue(instance, 1f);
            GlueToCameraMethod?.Invoke(instance, null);

            Rigidbody recoilRig = ToolMoveRecoilRigField?.GetValue(instance) as Rigidbody;
            Vector3 recoilPos = recoilRig != null ? recoilRig.position : Vector3.zero;
            Quaternion recoilRot = recoilRig != null ? recoilRig.rotation : Quaternion.identity;

            Quaternion targetRot = vr.DominantAimWorldRot;
            IsTwoHandingWeapon = false;

            // Two-handed foregrip stabilization for firearms (exclude Bow & active manual reload manipulation)
            if (VRConfig.TwoHandedWeapons.Value && tool.Weapon != null && !(tool.Weapon is Bow) &&
                !VRCombatAndBoatPatches.IsManipulatingWeaponReload)
            {
                var offState = vr.OffHandState;
                Vector3 gripToOff = vr.OffHandGripWorldPos - vr.DominantGripWorldPos;
                float dist = gripToOff.magnitude;
                float forwardAlongBarrel = Vector3.Dot(gripToOff, vr.DominantAimWorldRot * Vector3.forward);
                if (offState.IsActive && offState.squeeze > 0.55f && dist > 0.12f && dist < 0.58f && forwardAlongBarrel > 0.11f)
                {
                    Vector3 aimDir = (vr.OffHandGripWorldPos - vr.DominantGripWorldPos).normalized;
                    if (aimDir.sqrMagnitude > 0.5f)
                    {
                        Vector3 upRef = vr.DominantAimWorldRot * Vector3.up;
                        targetRot = Quaternion.LookRotation(aimDir, upRef);
                        IsTwoHandingWeapon = true;

                        OffHandSnapActive = true;
                        OffHandSnapPos = vr.OffHandGripWorldPos;
                        OffHandSnapRot = vr.OffHandGripWorldRot;
                        OffHandSnapIsRawBoneRot = false;
                    }
                }
            }

            targetRot *= recoilRot;
            tool.SwayTransform.rotation = targetRot;

            Transform gripAnchor = (tool.HandModelRight != null && tool.HandModelRight.IsChildOf(tool.SwayTransform))
                ? tool.HandModelRight
                : tool.SwayRotAroundTransform;

            Vector3 anchorWorldOffset = gripAnchor != null
                ? (gripAnchor.position - tool.SwayTransform.position)
                : Vector3.zero;

            tool.SwayTransform.position = vr.DominantGripWorldPos - anchorWorldOffset + (targetRot * recoilPos);
        }

        [HarmonyPatch("LateUpdate")]
        [HarmonyPrefix]
        public static bool LateUpdatePrefix(PlayerToolMovement __instance)
        {
            if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive) return true;
            var player = ToolMovePlayerField?.GetValue(__instance) as Player;
            if (player == null || player.Owner == null || !player.Owner.IsLocalClient) return true;

            var tool = ToolMoveToolField?.GetValue(__instance) as Tool;
            if (tool == null) return true;

            ApplyVRToolPose(__instance, player, tool);
            return false;
        }

        [HarmonyPatch("FixedUpdate")]
        [HarmonyPrefix]
        public static bool FixedUpdatePrefix(PlayerToolMovement __instance)
        {
            if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive) return true;
            var player = ToolMovePlayerField?.GetValue(__instance) as Player;
            if (player == null || player.Owner == null || !player.Owner.IsLocalClient) return true;

            var tool = ToolMoveToolField?.GetValue(__instance) as Tool;
            if (tool == null) return true; // Let vanilla handle _droppedTool cleanup when _tool == null

            ApplyVRToolPose(__instance, player, tool);
            CalcFakeVelMethod?.Invoke(__instance, null);

            if (ToolMoveHasNewToolField != null && (bool)ToolMoveHasNewToolField.GetValue(__instance))
            {
                ToolMoveHasNewToolField.SetValue(__instance, false);
                ResetSwayRecoilMethod?.Invoke(__instance, null);
            }
            return false;
        }
    }

    // =========================================================================
    // 4. PlayerHands Patches (6DOF Local VR Hands, Curls & 2-Bone Arm IK)
    // =========================================================================
    [HarmonyPatch(typeof(PlayerHands), "Awake")]
    public static class PlayerHandsAwakePatch
    {
        public static void Postfix(PlayerHands __instance)
        {
            EnsureCalibrated(__instance);
        }
    }

    [HarmonyPatch(typeof(PlayerHands), "LateUpdate")]
    public static class PlayerHandsLateUpdatePatch
    {
        public static void Postfix(PlayerHands __instance)
        {
            var player = HandsPlayerField?.GetValue(__instance) as Player;
            if (player == null) return;

            // Remote VR players are handled by VRMultiplayerSync
            if (player.Owner == null || !player.Owner.IsLocalClient)
            {
                VRMultiplayerSync.Instance?.ApplyRemotePlayerHands(player, __instance);
                return;
            }

            if (VRRigManager.Instance == null || !VRRigManager.Instance.IsVRActive) return;
            var vr = VRRigManager.Instance;

            bool isDead = player.Dying != null && player.Dying.IsDead;
            Item heldItem = player.Holding != null ? player.Holding.HeldItem : null;

            // Check if a Tool is playing an explicit animated hand clip (Inspect, or Reload ONLY when Automatic mode is enabled!)
            bool letAnimatedHandsPlay = false;
            if (!isDead && heldItem != null && heldItem.Tool != null && heldItem.Tool.HandsMesh != null)
            {
                Animation anim = ToolAnimField?.GetValue(heldItem.Tool) as Animation;
                bool allowCannedReload = VRConfig.AutoReloadWeapons.Value || (heldItem.Weapon is Bow);
                if (anim != null &&
                    (anim.IsPlaying("Inspect") ||
                     (allowCannedReload && (anim.IsPlaying("Reload") || anim.IsPlaying("ReloadLast")))))
                {
                    letAnimatedHandsPlay = true;
                }
                else
                {
                    heldItem.Tool.HandsMesh.enabled = false;
                }
            }

            var rightRenderer = HandModelRightField?.GetValue(__instance) as Renderer;
            var leftRenderer = HandModelLeftField?.GetValue(__instance) as Renderer;

            if (letAnimatedHandsPlay)
            {
                if (rightRenderer != null) rightRenderer.enabled = false;
                if (leftRenderer != null) leftRenderer.enabled = false;
                OffHandSnapActive = false;
                OffHandSnapIsRawBoneRot = false;
                DominantHandSnapActive = false;
                return;
            }

            if (rightRenderer != null) rightRenderer.enabled = !isDead;
            if (leftRenderer != null) leftRenderer.enabled = !isDead;
            if (isDead)
            {
                OffHandSnapActive = false;
                OffHandSnapIsRawBoneRot = false;
                DominantHandSnapActive = false;
                return;
            }

            bool rightDom = vr.IsRightDominant;
            Transform domBone = rightDom ? __instance.HandBoneRight : __instance.HandBoneLeft;
            Transform offBone = rightDom ? __instance.HandBoneLeft : __instance.HandBoneRight;

            bool hasAuthoredToolRightGrip = heldItem != null && heldItem.Tool != null && rightDom &&
                                            heldItem.HandModelRight != null && !player.Punching.IsAnimatingRight;
            bool hasAuthoredItemRightGrip = heldItem != null && heldItem.Tool == null && rightDom &&
                                            heldItem.HandTransformsRight != null && heldItem.HandTransformsRight.Exists &&
                                            heldItem.HandTransformsRight.Parent != null && !player.Punching.IsAnimatingRight;

            // Dominant hand pose
            if (DominantHandSnapActive)
            {
                domBone.SetPositionAndRotation(DominantHandSnapPos, DominantHandSnapRot);
            }
            else if (hasAuthoredToolRightGrip)
            {
                domBone.SetPositionAndRotation(heldItem.HandModelRight.position, heldItem.HandModelRight.rotation);
            }
            else if (hasAuthoredItemRightGrip)
            {
                // Vanilla already positioned HandBoneRight + custom finger pose onto heldItem.HandTransformsRight
            }
            else if (!(rightDom ? player.Punching.IsAnimatingRight : player.Punching.IsAnimatingLeft))
            {
                Quaternion domBoneRot = GetCalibratedHandBoneRotation(
                    __instance, rightDom, vr.DominantGripWorldRot, vr.DominantAimWorldRot);
                domBone.SetPositionAndRotation(vr.DominantGripWorldPos, domBoneRot);
            }

            // Off-hand pose
            if (OffHandSnapActive)
            {
                Quaternion offSnapBoneRot = OffHandSnapIsRawBoneRot
                    ? OffHandSnapRot
                    : GetCalibratedHandBoneRotation(__instance, !rightDom, OffHandSnapRot, vr.OffHandAimWorldRot);
                offBone.SetPositionAndRotation(OffHandSnapPos, offSnapBoneRot);
            }
            else if (!(!rightDom ? player.Punching.IsAnimatingRight : player.Punching.IsAnimatingLeft))
            {
                Quaternion offBoneRot = GetCalibratedHandBoneRotation(
                    __instance, !rightDom, vr.OffHandGripWorldRot, vr.OffHandAimWorldRot);
                offBone.SetPositionAndRotation(vr.OffHandGripWorldPos, offBoneRot);
            }

            // Finger curls when hands aren't locked to an item's custom authored finger pose
            var fingersR = FingersRightField?.GetValue(__instance) as Transform[];
            var fingersL = FingersLeftField?.GetValue(__instance) as Transform[];
            var origR = OrigRightField?.GetValue(__instance) as HandTransforms;
            var origL = OrigLeftField?.GetValue(__instance) as HandTransforms;

            if (!hasAuthoredToolRightGrip && !hasAuthoredItemRightGrip)
            {
                float rSqueeze = (rightDom && heldItem != null)
                    ? Mathf.Max(vr.CurrentFrame.handRight.squeeze, 0.58f)
                    : vr.CurrentFrame.handRight.squeeze;
                ApplyDynamicFingerCurls(__instance, isRightHand: true, fingersR, origR, vr.CurrentFrame.handRight.trigger, rSqueeze);
            }

            float lSqueeze = (!rightDom && heldItem != null)
                ? Mathf.Max(vr.CurrentFrame.handLeft.squeeze, 0.58f)
                : vr.CurrentFrame.handLeft.squeeze;
            if (OffHandSnapActive && rightDom)
            {
                lSqueeze = Mathf.Max(lSqueeze, 0.65f);
            }
            ApplyDynamicFingerCurls(__instance, isRightHand: false, fingersL, origL, vr.CurrentFrame.handLeft.trigger, lSqueeze);

            ForceEnablePlayerArmIK(player);

            // Reset per-frame external snaps
            OffHandSnapActive = false;
            OffHandSnapIsRawBoneRot = false;
            DominantHandSnapActive = false;
        }
    }

    // =========================================================================
    // 5. PlayerHolding Patches (Controller Raycast, 6DOF Physics Hold & Throw)
    // =========================================================================
    [HarmonyPatch(typeof(PlayerHolding))]
    public static class PlayerHoldingPatches
    {
        private static bool IsLocalVR(PlayerHolding instance, out Player player)
        {
            player = HoldPlayerField?.GetValue(instance) as Player;
            return VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive &&
                   player != null && player.Owner != null && player.Owner.IsLocalClient;
        }

        [HarmonyPatch("ScanForGrabbableItem")]
        [HarmonyPrefix]
        public static bool ScanForGrabbableItemPrefix(PlayerHolding __instance)
        {
            if (!IsLocalVR(__instance, out Player player)) return true;

            Item prevGrabbable = HoldGrabbableField?.GetValue(__instance) as Item;
            if (player.BlockInputs || Boat.IsDrivingLocally || Boat.WantToDrive)
            {
                if (prevGrabbable != null) prevGrabbable.UnHover();
                HoldGrabbableField?.SetValue(__instance, null);
                return false;
            }

            var vr = VRRigManager.Instance;
            Vector3 rayOrigin = vr.DominantHandState.IsActive ? vr.DominantAimWorldPos : vr.HeadWorldPos;
            Vector3 rayDir = vr.DominantHandState.IsActive ? (vr.DominantAimWorldRot * Vector3.forward) : (vr.HeadWorldRot * Vector3.forward);
            float grabRange = (float)(HoldGrabRangeField?.GetValue(__instance) ?? 3f) * 1.15f;
            float grabRadius = (float)(HoldGrabRadiusField?.GetValue(__instance) ?? 0.25f);

            Collider[] cols = Physics.OverlapCapsule(rayOrigin, rayOrigin + rayDir * grabRange, grabRadius, GameInfo.ItemLayer);
            Item bestItem = null;
            float bestScore = -1f;

            foreach (Collider col in cols)
            {
                if (!col.CompareTag("Item")) continue;
                Item candidate = ItemManager.Get(col);
                if (candidate == null || !candidate.IsInteractable || !candidate.CanPickUp || candidate.SyncedHolder != null) continue;

                Vector3 toCom = candidate.Rig.worldCenterOfMass - rayOrigin;
                if (Physics.Raycast(rayOrigin, toCom, out RaycastHit hit, grabRange, (int)GameInfo.LevelLayer | (int)GameInfo.ItemLayer) &&
                    hit.transform.CompareTag("Level"))
                {
                    continue;
                }

                float score = Vector3.Dot((candidate.transform.position - rayOrigin).normalized, rayDir);
                // Also reward very close hand proximity (< 0.35m) for direct physical grabbing!
                float dist = Vector3.Distance(candidate.transform.position, vr.DominantGripWorldPos);
                if (dist < 0.35f) score += 0.6f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestItem = candidate;
                }
            }

            if (prevGrabbable != bestItem && prevGrabbable != null)
            {
                prevGrabbable.UnHover();
            }
            if (bestItem != null)
            {
                bestItem.Hover();
            }
            HoldGrabbableField?.SetValue(__instance, bestItem);
            return false;
        }

        [HarmonyPatch("ScanForInteractable")]
        [HarmonyPrefix]
        public static bool ScanForInteractablePrefix(PlayerHolding __instance)
        {
            if (!IsLocalVR(__instance, out Player player)) return true;

            Item curGrabbable = HoldGrabbableField?.GetValue(__instance) as Item;
            Interactable prevInter = HoldInteractableField?.GetValue(__instance) as Interactable;

            if (curGrabbable != null || player.BlockInputs || Boat.IsDrivingLocally || Boat.WantToDrive)
            {
                if (prevInter != null) prevInter.UnHover();
                HoldInteractableField?.SetValue(__instance, null);
                return false;
            }

            var vr = VRRigManager.Instance;
            Vector3 rayOrigin = vr.DominantHandState.IsActive ? vr.DominantAimWorldPos : vr.HeadWorldPos;
            Vector3 rayDir = vr.DominantHandState.IsActive ? (vr.DominantAimWorldRot * Vector3.forward) : (vr.HeadWorldRot * Vector3.forward);
            float grabRange = (float)(HoldGrabRangeField?.GetValue(__instance) ?? 3f) * 1.15f;
            float grabRadius = (float)(HoldGrabRadiusField?.GetValue(__instance) ?? 0.25f);

            Collider[] cols = Physics.OverlapCapsule(rayOrigin, rayOrigin + rayDir * grabRange, grabRadius, GameInfo.InteractableLayer);
            Interactable bestInter = null;
            float bestScore = -1f;

            foreach (Collider col in cols)
            {
                if (!col.CompareTag("Interactable")) continue;
                Interactable inter = Interactable.Get(col);
                if (inter == null) continue;

                Vector3 toInter = inter.transform.position - rayOrigin;
                if (Physics.Raycast(rayOrigin, toInter, out RaycastHit hit, grabRange, (int)GameInfo.LevelLayer | (int)GameInfo.InteractableLayer) &&
                    hit.transform.CompareTag("Level"))
                {
                    continue;
                }

                float score = Vector3.Dot(toInter.normalized, rayDir);
                float dist = Vector3.Distance(inter.transform.position, vr.DominantGripWorldPos);
                if (dist < 0.45f) score += 0.5f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestInter = inter;
                }
            }

            if (prevInter != bestInter && prevInter != null)
            {
                prevInter.UnHover();
            }
            if (bestInter != null)
            {
                bestInter.Hover();
            }
            HoldInteractableField?.SetValue(__instance, bestInter);
            return false;
        }

        [HarmonyPatch("CalculateHoldTargetPos")]
        [HarmonyPrefix]
        public static bool CalculateHoldTargetPosPrefix(PlayerHolding __instance, ref Vector3 __result)
        {
            if (!IsLocalVR(__instance, out Player player)) return true;
            Item held = HoldHeldItemField?.GetValue(__instance) as Item;
            if (held == null) return true;

            var vr = VRRigManager.Instance;
            Vector3 dropOffset = (Vector3)(HoldDropForceOffsetField?.GetValue(__instance) ?? Vector3.zero);

            // Place physics item right in front of the player's 6DOF dominant hand!
            Vector3 handHoldPos = vr.DominantGripWorldPos + (vr.DominantAimWorldRot * new Vector3(0f, 0.02f, 0.14f)) + (vr.DominantAimWorldRot * dropOffset);

            // If actively button-eating (not physical mouth proximity), blend smoothly toward mouth
            if (held.Creature != null && player.Eating != null && player.Eating.EatPercent > 0.01f && !VRCombatAndBoatPatches.IsPhysicalMouthEating)
            {
                float eatT = player.Eating.MoveCurve.Evaluate(player.Eating.EatPercent);
                Vector3 mouthTarget = player.CamObject.position + (player.CamObject.rotation * held.Creature.EatPos);
                handHoldPos = Vector3.Lerp(handHoldPos, mouthTarget, eatT);
            }

            __result = handHoldPos;
            return false;
        }

        [HarmonyPatch("CalculateHoldTargetRot")]
        [HarmonyPrefix]
        public static bool CalculateHoldTargetRotPrefix(PlayerHolding __instance, ref Quaternion __result)
        {
            if (!IsLocalVR(__instance, out Player player)) return true;
            Item held = HoldHeldItemField?.GetValue(__instance) as Item;
            if (held == null) return true;

            var vr = VRRigManager.Instance;
            Quaternion baseRot = vr.DominantAimWorldRot * Quaternion.Euler(held.HeldRot);

            if (held.Creature != null && player.Eating != null && player.Eating.EatPercent > 0.01f && !VRCombatAndBoatPatches.IsPhysicalMouthEating)
            {
                float eatT = player.Eating.MoveCurve.Evaluate(player.Eating.EatPercent);
                Quaternion eatRot = player.CamObject.rotation * Quaternion.Euler(held.Creature.EatRot);
                baseRot = Quaternion.Slerp(baseRot, eatRot, eatT);
            }

            __result = baseRot;
            return false;
        }

        [HarmonyPatch("ShouldUnstuckItem")]
        [HarmonyPrefix]
        public static bool ShouldUnstuckItemPrefix(PlayerHolding __instance, ref bool __result)
        {
            if (!IsLocalVR(__instance, out _)) return true;
            Item held = HoldHeldItemField?.GetValue(__instance) as Item;
            if (held == null) return true;

            // Measure unstuck distance from VR hand instead of chest/camera so reaching out with long arms never false-teleports the item!
            float sqrDist = (VRRigManager.Instance.DominantGripWorldPos - held.transform.position).sqrMagnitude;
            __result = sqrDist > 9.0f; // > 3.0m away from VR hand
            return false;
        }

        [HarmonyPatch("FinalDropForce")]
        [HarmonyPostfix]
        public static void FinalDropForcePostfix(PlayerHolding __instance, ref Vector3 __result)
        {
            if (!IsLocalVR(__instance, out Player player)) return;
            Item held = HoldHeldItemField?.GetValue(__instance) as Item;
            if (held == null) return;

            var vr = VRRigManager.Instance;
            // Re-orient any button-charged throw force along the VR hand's aim ray instead of HMD gaze
            float buttonMag = __result.magnitude;
            if (held.Tool != null)
            {
                // Vanilla already added FakeItemVel; replace flat CamTransform.forward component with VR hand aim + velocity boost
                Vector3 rawHandThrow = (vr.DominantHandVelocity - player.Movement.Velocity) * VRConfig.ThrowVelocityMultiplier.Value;
                float extraCharge = Mathf.Max(0f, (buttonMag - player.ToolMovement.FakeItemVel.magnitude));
                __result = (vr.DominantAimWorldRot * Vector3.forward) * extraCharge + player.Movement.Velocity + rawHandThrow;
            }
            else
            {
                // Physics Item: add real 6DOF hand swing velocity!
                Vector3 handSwingVel = (vr.DominantHandVelocity - player.Movement.Velocity) * VRConfig.ThrowVelocityMultiplier.Value;
                __result = (vr.DominantAimWorldRot * Vector3.forward) * buttonMag + handSwingVel;
            }

            if (__result.sqrMagnitude > 4f)
            {
                OpenXRBridge.TriggerDominantHaptic(0.35f, 0.04f, 140f);
            }
        }
    }
}
