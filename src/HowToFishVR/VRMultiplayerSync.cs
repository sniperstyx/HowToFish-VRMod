using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using Steamworks;
using UnityEngine;

namespace HowToFishVR;

/// <summary>
/// Full Multiplayer VR Synchronization:
/// - 100% Vanilla Crossplay compatible (zero changes to Application.version, FishNet RPCs, SyncVars, or Prefabs).
/// - Vanilla players natively see the VR player's 6DOF HMD pitch/yaw, 6DOF held tools/weapons/rods, IK arms, items, fishing line, and attacks.
/// - VR-to-VR peers additionally exchange compact 68-byte 6DOF dual-hand + finger-curl + head-roll packets at 30 Hz
///   over isolated SteamNetworkingMessages channel 77 (works even when the lobby host is an unmodded Vanilla player!).
/// </summary>
public class VRMultiplayerSync : MonoBehaviour
{
    public static VRMultiplayerSync Instance { get; private set; }

    private const uint PacketMagic = 0x48565231; // "HVR1"
    private const int VrSteamChannel = 77;
    private const int SteamSendFlagsUnreliableNoNagleAutoRestart = 1 | 4 | 32; // k_nSteamNetworkingSend_UnreliableNoNagle (5) | AutoRestartBrokenSession (32) = 37
    private const float SendInterval = 1f / 30f;
    private const float RemoteTimeoutSeconds = 2.5f;

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VrSyncPacket
    {
        public uint magic;
        public ulong senderSteamId;
        public short headRollCentideg; // roll in 0.01 deg units
        public float lx, ly, lz;
        public float lqx, lqy, lqz, lqw;
        public float rx, ry, rz;
        public float rqx, rqy, rqz, rqw;
        public byte leftTrigger;
        public byte leftGrip;
        public byte rightTrigger;
        public byte rightGrip;
        public byte flags; // bit0 = relativeToBoat, bit1 = holdingToolRight
    }

    private class RemoteVRState
    {
        public ulong SteamId;
        public float LastReceiveTime;

        public float TargetHeadRoll;
        public float CurHeadRoll;

        public Vector3 TargetLeftLocalPos;
        public Quaternion TargetLeftLocalRot = Quaternion.identity;
        public Vector3 TargetRightLocalPos;
        public Quaternion TargetRightLocalRot = Quaternion.identity;

        public Vector3 CurLeftWorldPos;
        public Quaternion CurLeftWorldRot = Quaternion.identity;
        public Vector3 CurRightWorldPos;
        public Quaternion CurRightWorldRot = Quaternion.identity;
        public bool InitializedWorld;

        public float LeftTrigger;
        public float LeftGrip;
        public float RightTrigger;
        public float RightGrip;
        public bool RelativeToBoat;
        public bool HoldingToolRight;
    }

    private readonly Dictionary<ulong, RemoteVRState> _remoteStates = new Dictionary<ulong, RemoteVRState>();
    private readonly Dictionary<Player, ulong> _playerSteamIdCache = new Dictionary<Player, ulong>();
    private float _nextCacheRefreshTime;
    private float _nextSendTime;
    private CSteamID _lastTaggedLobby = CSteamID.Nil;

    private Callback<SteamNetworkingMessagesSessionRequest_t> _sessionReqCallback;
    private Callback<SteamNetworkingMessagesSessionFailed_t> _sessionFailCallback;
    private readonly IntPtr[] _recvMsgPtrs = new IntPtr[32];

    // Cached reflection for PlayerHands & PlayerBody
    private static readonly FieldInfo BodyPlayerField = AccessTools.Field(typeof(PlayerBody), "_player");
    private static readonly FieldInfo BodyHeadField = AccessTools.Field(typeof(PlayerBody), "_head");

    private static readonly FieldInfo HandModelRightField = AccessTools.Field(typeof(PlayerHands), "_handModelRight");
    private static readonly FieldInfo HandModelLeftField = AccessTools.Field(typeof(PlayerHands), "_handModelLeft");
    private static readonly FieldInfo FingersRightField = AccessTools.Field(typeof(PlayerHands), "_fingerTransformsRight");
    private static readonly FieldInfo FingersLeftField = AccessTools.Field(typeof(PlayerHands), "_fingerTransformsLeft");
    private static readonly FieldInfo OrigRightField = AccessTools.Field(typeof(PlayerHands), "_handTransformsOrigRight");
    private static readonly FieldInfo OrigLeftField = AccessTools.Field(typeof(PlayerHands), "_handTransformsOrigLeft");

    private static readonly PropertyInfo PlayerSteamIdProp =
        typeof(Player).GetProperty("SteamID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
        typeof(Player).GetProperty("SteamId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo PlayerSteamIdField =
        typeof(Player).GetField("_steamID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
        typeof(Player).GetField("SteamID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

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

    private void Update()
    {
        if (!VRConfig.EnableVRMultiplayerSync.Value) return;

        TryEnsureSteamCallbacks();
        PollIncomingMessages();

        if (VRRigManager.Instance != null && VRRigManager.Instance.IsVRActive)
        {
            EnsureLobbyMemberTag();
            if (Time.unscaledTime >= _nextSendTime)
            {
                _nextSendTime = Time.unscaledTime + SendInterval;
                SendLocalVRPoseToPeers();
            }
        }
    }

    private void TryEnsureSteamCallbacks()
    {
        if (_sessionReqCallback != null) return;
        try
        {
            if (SteamManager.CurrentLobbyID != CSteamID.Nil || SteamAPI.IsSteamRunning())
            {
                _sessionReqCallback = Callback<SteamNetworkingMessagesSessionRequest_t>.Create(OnSessionRequest);
                _sessionFailCallback = Callback<SteamNetworkingMessagesSessionFailed_t>.Create(OnSessionFailed);
            }
        }
        catch
        {
            // Steamworks not initialized yet; will retry next frame
        }
    }

    private void OnSessionRequest(SteamNetworkingMessagesSessionRequest_t req)
    {
        CSteamID remoteId = req.m_identityRemote.GetSteamID();
        if (SteamManager.IsCurrentLobbyMember(remoteId))
        {
            SteamNetworkingMessages.AcceptSessionWithUser(ref req.m_identityRemote);
        }
    }

    private void OnSessionFailed(SteamNetworkingMessagesSessionFailed_t fail)
    {
        SteamNetworkingIdentity id = fail.m_info.m_identityRemote;
        SteamNetworkingMessages.CloseSessionWithUser(ref id);
    }

    private void EnsureLobbyMemberTag()
    {
        CSteamID lobby = SteamManager.CurrentLobbyID;
        if (lobby == CSteamID.Nil)
        {
            _lastTaggedLobby = CSteamID.Nil;
            return;
        }

        if (_lastTaggedLobby != lobby)
        {
            try
            {
                SteamMatchmaking.SetLobbyMemberData(lobby, "htf_vr", "1");
                _lastTaggedLobby = lobby;
                Plugin.Log.LogInfo($"Published VR lobby member tag ('htf_vr'='1') in lobby {lobby.m_SteamID}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogDebug($"SetLobbyMemberData info: {ex.Message}");
            }
        }
    }

    private unsafe void SendLocalVRPoseToPeers()
    {
        CSteamID lobby = SteamManager.CurrentLobbyID;
        if (lobby == CSteamID.Nil || Player.LocalPlayer == null) return;

        int memberCount;
        try
        {
            memberCount = SteamMatchmaking.GetNumLobbyMembers(lobby);
        }
        catch
        {
            return;
        }
        if (memberCount <= 1) return;

        CSteamID myId = SteamUser.GetSteamID();
        var vr = VRRigManager.Instance;
        Player lp = Player.LocalPlayer;

        bool onBoat = lp.Movement != null && lp.Movement.OnBoat && BoatManager.Boat != null && BoatManager.Boat.VisualBoat != null;
        Transform refSpace = onBoat ? BoatManager.Boat.VisualBoat : lp.Transform;

        // Compute calibrated playermodel hand bone rotations with natural anatomical orientation
        Quaternion boneWorldL = VRPlayerAndHandsPatches.GetCalibratedHandBoneRotation(
            lp.Hands, isRightHand: false, vr.LeftGripWorldRot, vr.LeftAimWorldRot);
        Quaternion boneWorldR = VRPlayerAndHandsPatches.GetCalibratedHandBoneRotation(
            lp.Hands, isRightHand: true, vr.RightGripWorldRot, vr.RightAimWorldRot);

        Vector3 localPosL = refSpace.InverseTransformPoint(vr.LeftGripWorldPos);
        Quaternion localRotL = Quaternion.Inverse(refSpace.rotation) * boneWorldL;
        Vector3 localPosR = refSpace.InverseTransformPoint(vr.RightGripWorldPos);
        Quaternion localRotR = Quaternion.Inverse(refSpace.rotation) * boneWorldR;

        float rawRoll = vr.HeadWorldRot.eulerAngles.z;
        if (rawRoll > 180f) rawRoll -= 360f;
        short packedRoll = (short)Mathf.Clamp(Mathf.RoundToInt(rawRoll * 100f), -18000, 18000);

        bool holdingToolRight = lp.Holding != null && lp.Holding.HeldItem != null && lp.Holding.HeldItem.Tool != null && vr.IsRightDominant;
        byte flags = 0;
        if (onBoat) flags |= 1;
        if (holdingToolRight) flags |= 2;

        VrSyncPacket pkt = new VrSyncPacket
        {
            magic = PacketMagic,
            senderSteamId = myId.m_SteamID,
            headRollCentideg = packedRoll,
            lx = localPosL.x, ly = localPosL.y, lz = localPosL.z,
            lqx = localRotL.x, lqy = localRotL.y, lqz = localRotL.z, lqw = localRotL.w,
            rx = localPosR.x, ry = localPosR.y, rz = localPosR.z,
            rqx = localRotR.x, rqy = localRotR.y, rqz = localRotR.z, rqw = localRotR.w,
            leftTrigger = (byte)Mathf.Clamp(Mathf.RoundToInt(vr.CurrentFrame.handLeft.trigger * 255f), 0, 255),
            leftGrip = (byte)Mathf.Clamp(Mathf.RoundToInt(vr.CurrentFrame.handLeft.squeeze * 255f), 0, 255),
            rightTrigger = (byte)Mathf.Clamp(Mathf.RoundToInt(vr.CurrentFrame.handRight.trigger * 255f), 0, 255),
            rightGrip = (byte)Mathf.Clamp(Mathf.RoundToInt(vr.CurrentFrame.handRight.squeeze * 255f), 0, 255),
            flags = flags
        };

        int pktSize = sizeof(VrSyncPacket);
        VrSyncPacket* pktPtr = &pkt;

        for (int i = 0; i < memberCount; i++)
        {
            CSteamID peerId = SteamMatchmaking.GetLobbyMemberByIndex(lobby, i);
            if (peerId == myId || peerId == CSteamID.Nil) continue;

            // Send only to peers advertising VR mod ("htf_vr" == "1") or from whom we've already received VR packets!
            string peerTag = SteamMatchmaking.GetLobbyMemberData(lobby, peerId, "htf_vr");
            if (peerTag != "1" && !_remoteStates.ContainsKey(peerId.m_SteamID)) continue;

            SteamNetworkingIdentity identity = default;
            identity.SetSteamID(peerId);
            SteamNetworkingMessages.SendMessageToUser(ref identity, (IntPtr)pktPtr, (uint)pktSize, SteamSendFlagsUnreliableNoNagleAutoRestart, VrSteamChannel);
        }
    }

    private unsafe void PollIncomingMessages()
    {
        if (_sessionReqCallback == null) return;

        int count;
        try
        {
            count = SteamNetworkingMessages.ReceiveMessagesOnChannel(VrSteamChannel, _recvMsgPtrs, _recvMsgPtrs.Length);
        }
        catch
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            IntPtr msgPtr = _recvMsgPtrs[i];
            if (msgPtr == IntPtr.Zero) continue;

            try
            {
                SteamNetworkingMessage_t msg = Marshal.PtrToStructure<SteamNetworkingMessage_t>(msgPtr);
                if (msg.m_cbSize >= sizeof(VrSyncPacket) && msg.m_pData != IntPtr.Zero)
                {
                    VrSyncPacket pkt = *(VrSyncPacket*)msg.m_pData;
                    if (pkt.magic == PacketMagic && pkt.senderSteamId != 0)
                    {
                        ApplyIncomingPacket(in pkt);
                    }
                }
            }
            finally
            {
                SteamNetworkingMessage_t.Release(msgPtr);
                _recvMsgPtrs[i] = IntPtr.Zero;
            }
        }
    }

    private void ApplyIncomingPacket(in VrSyncPacket pkt)
    {
        if (!_remoteStates.TryGetValue(pkt.senderSteamId, out RemoteVRState st))
        {
            st = new RemoteVRState { SteamId = pkt.senderSteamId };
            _remoteStates[pkt.senderSteamId] = st;
        }

        st.LastReceiveTime = Time.unscaledTime;
        st.TargetHeadRoll = pkt.headRollCentideg * 0.01f;
        st.TargetLeftLocalPos = new Vector3(pkt.lx, pkt.ly, pkt.lz);
        st.TargetLeftLocalRot = new Quaternion(pkt.lqx, pkt.lqy, pkt.lqz, pkt.lqw);
        st.TargetRightLocalPos = new Vector3(pkt.rx, pkt.ry, pkt.rz);
        st.TargetRightLocalRot = new Quaternion(pkt.rqx, pkt.rqy, pkt.rqz, pkt.rqw);
        st.LeftTrigger = pkt.leftTrigger / 255f;
        st.LeftGrip = pkt.leftGrip / 255f;
        st.RightTrigger = pkt.rightTrigger / 255f;
        st.RightGrip = pkt.rightGrip / 255f;
        st.RelativeToBoat = (pkt.flags & 1) != 0;
        st.HoldingToolRight = (pkt.flags & 2) != 0;
    }

    private bool TryGetRemoteVRState(Player player, out RemoteVRState state)
    {
        state = null;
        if (player == null || _remoteStates.Count == 0) return false;

        if (Time.unscaledTime >= _nextCacheRefreshTime)
        {
            _nextCacheRefreshTime = Time.unscaledTime + 1.0f;
            _playerSteamIdCache.Clear();
        }

        if (!_playerSteamIdCache.TryGetValue(player, out ulong steamId))
        {
            steamId = ExtractPlayerSteamId(player);
            if (steamId != 0) _playerSteamIdCache[player] = steamId;
        }

        if (steamId == 0 || !_remoteStates.TryGetValue(steamId, out state)) return false;
        if (Time.unscaledTime - state.LastReceiveTime > RemoteTimeoutSeconds) return false;
        return true;
    }

    private static ulong ExtractPlayerSteamId(Player player)
    {
        if (player == null) return 0;
        if (player._steamID != null && player._steamID.Value != 0)
        {
            return player._steamID.Value;
        }
        return 0;
    }

    public void ApplyRemotePlayerBodyHeadRoll(PlayerBody body)
    {
        if (!VRConfig.EnableVRMultiplayerSync.Value || body == null) return;
        Player player = BodyPlayerField?.GetValue(body) as Player;
        if (player == null || (player.Owner != null && player.Owner.IsLocalClient)) return;
        if (!TryGetRemoteVRState(player, out RemoteVRState st)) return;

        Transform head = BodyHeadField?.GetValue(body) as Transform;
        if (head == null) return;

        st.CurHeadRoll = Mathf.LerpAngle(st.CurHeadRoll, st.TargetHeadRoll, Mathf.Clamp01(Time.deltaTime * 18f));
        head.rotation *= Quaternion.Euler(0f, 0f, st.CurHeadRoll);
    }

    public void ApplyRemotePlayerHands(Player player, PlayerHands hands)
    {
        if (!VRConfig.EnableVRMultiplayerSync.Value || player == null || hands == null) return;
        if (!TryGetRemoteVRState(player, out RemoteVRState st)) return;
        if (player.Dying != null && player.Dying.IsDead) return;

        Transform refSpace = (st.RelativeToBoat && BoatManager.Boat != null && BoatManager.Boat.VisualBoat != null)
            ? BoatManager.Boat.VisualBoat
            : player.Transform;

        Vector3 targetLeftWPos = refSpace.TransformPoint(st.TargetLeftLocalPos);
        Quaternion targetLeftWRot = refSpace.rotation * st.TargetLeftLocalRot;
        Vector3 targetRightWPos = refSpace.TransformPoint(st.TargetRightLocalPos);
        Quaternion targetRightWRot = refSpace.rotation * st.TargetRightLocalRot;

        float lerpT = Mathf.Clamp01(Time.deltaTime * 20f);
        if (!st.InitializedWorld || Vector3.Distance(st.CurLeftWorldPos, targetLeftWPos) > 3f)
        {
            st.CurLeftWorldPos = targetLeftWPos;
            st.CurLeftWorldRot = targetLeftWRot;
            st.CurRightWorldPos = targetRightWPos;
            st.CurRightWorldRot = targetRightWRot;
            st.InitializedWorld = true;
        }
        else
        {
            st.CurLeftWorldPos = Vector3.Lerp(st.CurLeftWorldPos, targetLeftWPos, lerpT);
            st.CurLeftWorldRot = Quaternion.Slerp(st.CurLeftWorldRot, targetLeftWRot, lerpT);
            st.CurRightWorldPos = Vector3.Lerp(st.CurRightWorldPos, targetRightWPos, lerpT);
            st.CurRightWorldRot = Quaternion.Slerp(st.CurRightWorldRot, targetRightWRot, lerpT);
        }

        Item held = player.Holding != null ? player.Holding.HeldItem : null;
        if (held != null && held.Tool != null && held.Tool.HandsMesh != null)
        {
            held.Tool.HandsMesh.enabled = false;
        }

        if (HandModelRightField?.GetValue(hands) is Renderer rR) rR.enabled = true;
        if (HandModelLeftField?.GetValue(hands) is Renderer rL) rL.enabled = true;

        hands.HandBoneLeft.SetPositionAndRotation(st.CurLeftWorldPos, st.CurLeftWorldRot);

        if (st.HoldingToolRight && held != null && held.HandModelRight != null)
        {
            hands.HandBoneRight.SetPositionAndRotation(held.HandModelRight.position, held.HandModelRight.rotation);
        }
        else
        {
            hands.HandBoneRight.SetPositionAndRotation(st.CurRightWorldPos, st.CurRightWorldRot);
        }

        var fingersR = FingersRightField?.GetValue(hands) as Transform[];
        var fingersL = FingersLeftField?.GetValue(hands) as Transform[];
        var origR = OrigRightField?.GetValue(hands) as HandTransforms;
        var origL = OrigLeftField?.GetValue(hands) as HandTransforms;

        VRPlayerAndHandsPatches.ApplyDynamicFingerCurls(hands, isRightHand: false, fingersL, origL, st.LeftTrigger, st.LeftGrip);
        if (!st.HoldingToolRight)
        {
            VRPlayerAndHandsPatches.ApplyDynamicFingerCurls(hands, isRightHand: true, fingersR, origR, st.RightTrigger, st.RightGrip);
        }

        VRPlayerAndHandsPatches.ForceEnablePlayerArmIK(player);
    }
}

[HarmonyPatch(typeof(PlayerBody), "LateUpdate")]
public static class PlayerBodyLateUpdateVRPatch
{
    public static void Postfix(PlayerBody __instance)
    {
        VRMultiplayerSync.Instance?.ApplyRemotePlayerBodyHeadRoll(__instance);
    }
}
