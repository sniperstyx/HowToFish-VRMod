using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace HowToFishVR;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PoseData
{
    public float px, py, pz;
    public float qx, qy, qz, qw;
    public float vx, vy, vz;
    public float wx, wy, wz;
    public int valid;

    public Vector3 Position => new Vector3(px, py, pz);
    public Quaternion Rotation => (qw == 0f && qx == 0f && qy == 0f && qz == 0f)
        ? Quaternion.identity
        : new Quaternion(qx, qy, qz, qw);
    public Vector3 Velocity => new Vector3(vx, vy, vz);
    public Vector3 AngularVelocity => new Vector3(wx, wy, wz);
    public bool IsValid => valid != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct EyeViewData
{
    public PoseData pose;
    public float angleLeft;
    public float angleRight;
    public float angleUp;
    public float angleDown;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ControllerStateData
{
    public PoseData gripPose;
    public PoseData aimPose;
    public float stickX;
    public float stickY;
    public float trigger;
    public float squeeze;
    public uint buttons; // bit0=Primary(A/X), bit1=Secondary(B/Y), bit2=StickClick, bit3=Menu, bit4=TriggerClick, bit5=GripClick
    public int active;

    public Vector2 Stick => new Vector2(stickX, stickY);
    public bool IsActive => active != 0;
    public bool PrimaryButton => (buttons & (1u << 0)) != 0;
    public bool SecondaryButton => (buttons & (1u << 1)) != 0;
    public bool StickClick => (buttons & (1u << 2)) != 0;
    public bool MenuButton => (buttons & (1u << 3)) != 0;
    public bool TriggerClick => (buttons & (1u << 4)) != 0;
    public bool GripClick => (buttons & (1u << 5)) != 0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct VrFrameData
{
    public int sessionState;
    public int sessionRunning;
    public int shouldRender;
    public int viewsValid;
    public PoseData headPose;
    public EyeViewData eyeLeft;
    public EyeViewData eyeRight;
    public ControllerStateData handLeft;
    public ControllerStateData handRight;

    public bool IsSessionRunning => sessionRunning != 0;
    public bool ShouldRender => shouldRender != 0;
    public bool AreViewsValid => viewsValid != 0;
}

public static class OpenXRBridge
{
    private const string NativeDllName = "HowToFishVRNative";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string lpPathName);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_GetLastError(byte[] buffer, int bufferSize);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_InitInstance();

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HtfVr_GetRecommendedResolution(out int outWidth, out int outHeight);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_CreateSession(IntPtr sampleD3D11Texture, int width, int height);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_GetSwapchainDxgiFormat();

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_HasStageSpace();

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HtfVr_SetPreferStageSpace(int preferStage);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_WaitAndUpdateFrame(out VrFrameData outFrame);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HtfVr_PrepareSubmit(IntPtr leftEyeD3D11Tex, IntPtr rightEyeD3D11Tex);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr HtfVr_GetRenderEventFunc();

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int HtfVr_GetSubmitEventId();

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HtfVr_TriggerHaptic(int handIndex, float amplitude, float durationSeconds, float frequencyHz);

    [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void HtfVr_Shutdown();

    public static bool NativeLoaded { get; private set; }
    public static bool InstanceReady { get; private set; }
    public static bool SessionReady { get; private set; }
    public static IntPtr RenderEventFunc { get; private set; }
    public static int SubmitEventId { get; private set; }
    public static int SwapchainDxgiFormat { get; private set; } = 29;
    public static int EyeWidth { get; private set; } = 2048;
    public static int EyeHeight { get; private set; } = 2048;
    public static bool HasStageSpace { get; private set; }

    public static bool PreloadNativeLibraries(string pluginFolder)
    {
        if (NativeLoaded) return true;

        try
        {
            SetDllDirectoryW(pluginFolder);

            string loaderPath = Path.Combine(pluginFolder, "openxr_loader.dll");
            if (File.Exists(loaderPath))
            {
                IntPtr loaderHandle = LoadLibraryW(loaderPath);
                if (loaderHandle == IntPtr.Zero)
                {
                    Plugin.Log.LogWarning($"LoadLibraryW failed for openxr_loader.dll (Win32 err={Marshal.GetLastWin32Error()})");
                }
            }

            string nativePath = Path.Combine(pluginFolder, "HowToFishVRNative.dll");
            if (!File.Exists(nativePath))
            {
                Plugin.Log.LogError($"Missing native bridge DLL at: {nativePath}");
                return false;
            }

            IntPtr nativeHandle = LoadLibraryW(nativePath);
            if (nativeHandle == IntPtr.Zero)
            {
                Plugin.Log.LogError($"LoadLibraryW failed for HowToFishVRNative.dll (Win32 err={Marshal.GetLastWin32Error()})");
                return false;
            }

            RenderEventFunc = HtfVr_GetRenderEventFunc();
            SubmitEventId = HtfVr_GetSubmitEventId();
            NativeLoaded = true;
            Plugin.Log.LogInfo($"Loaded HowToFishVRNative.dll (SubmitEventId=0x{SubmitEventId:X8})");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Exception loading OpenXR native libraries: {ex}");
            return false;
        }
    }

    public static string GetLastNativeError()
    {
        if (!NativeLoaded) return "Native DLL not loaded";
        byte[] buf = new byte[1024];
        int len = HtfVr_GetLastError(buf, buf.Length);
        return len > 0 ? Encoding.UTF8.GetString(buf, 0, len) : string.Empty;
    }

    /// <summary>
    /// Returns 1 on success, -1 if HMD not connected/awake yet (can retry), 0 on fatal error.
    /// </summary>
    public static int TryInitOpenXRInstance()
    {
        if (!NativeLoaded) return 0;
        if (InstanceReady) return 1;

        int res = HtfVr_InitInstance();
        if (res == 1)
        {
            InstanceReady = true;
            HtfVr_GetRecommendedResolution(out int recW, out int recH);
            float scale = Mathf.Clamp(VRConfig.RenderScale.Value, 0.5f, 2.0f);
            EyeWidth = Mathf.Clamp(Mathf.RoundToInt(recW * scale), 512, 6144);
            EyeHeight = Mathf.Clamp(Mathf.RoundToInt(recH * scale), 512, 6144);
            Plugin.Log.LogInfo($"OpenXR Instance & HMD ready! Recommended={recW}x{recH}, Scaled={EyeWidth}x{EyeHeight}");
        }
        return res;
    }

    public static bool TryCreateSession(IntPtr sampleD3D11Tex)
    {
        if (!InstanceReady || sampleD3D11Tex == IntPtr.Zero) return false;
        if (SessionReady) return true;

        HtfVr_SetPreferStageSpace(VRConfig.SeatedMode.Value ? 0 : 1);
        int res = HtfVr_CreateSession(sampleD3D11Tex, EyeWidth, EyeHeight);
        if (res != 1)
        {
            Plugin.Log.LogError($"OpenXR CreateSession failed: {GetLastNativeError()}");
            return false;
        }

        SwapchainDxgiFormat = HtfVr_GetSwapchainDxgiFormat();
        HasStageSpace = HtfVr_HasStageSpace() != 0;
        SessionReady = true;
        Plugin.Log.LogInfo($"OpenXR Session created! Swapchain DXGI Format={SwapchainDxgiFormat}, HasStageSpace={HasStageSpace}");
        return true;
    }

    public static void SetPreferStageSpace(bool preferStage)
    {
        if (NativeLoaded)
        {
            HtfVr_SetPreferStageSpace(preferStage ? 1 : 0);
        }
    }

    public static bool WaitAndUpdateFrame(out VrFrameData frame)
    {
        if (!SessionReady)
        {
            frame = default;
            return false;
        }
        return HtfVr_WaitAndUpdateFrame(out frame) != 0;
    }

    public static void SubmitStereoFrame(IntPtr leftTexPtr, IntPtr rightTexPtr)
    {
        if (!SessionReady || RenderEventFunc == IntPtr.Zero) return;
        HtfVr_PrepareSubmit(leftTexPtr, rightTexPtr);
        GL.IssuePluginEvent(RenderEventFunc, SubmitEventId);
    }

    public static void TriggerHaptic(bool rightHand, float amplitude, float durationSeconds = 0.03f, float frequencyHz = 0f)
    {
        if (!SessionReady) return;
        float scaled = Mathf.Clamp01(amplitude * VRConfig.HapticStrength.Value);
        if (scaled <= 0.001f) return;
        HtfVr_TriggerHaptic(rightHand ? 1 : 0, scaled, durationSeconds, frequencyHz);
    }

    public static void TriggerDominantHaptic(float amplitude, float durationSeconds = 0.03f, float frequencyHz = 0f)
    {
        bool right = VRConfig.DominantHand.Value == VRDominantHand.Right;
        TriggerHaptic(right, amplitude, durationSeconds, frequencyHz);
    }

    public static void TriggerOffHandHaptic(float amplitude, float durationSeconds = 0.03f, float frequencyHz = 0f)
    {
        bool right = VRConfig.DominantHand.Value != VRDominantHand.Right;
        TriggerHaptic(right, amplitude, durationSeconds, frequencyHz);
    }

    public static void Shutdown()
    {
        if (!NativeLoaded) return;
        try
        {
            HtfVr_Shutdown();
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"OpenXR Shutdown warning: {ex.Message}");
        }
        SessionReady = false;
        InstanceReady = false;
    }
}
