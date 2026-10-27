#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

#define XR_USE_PLATFORM_WIN32
#define XR_USE_GRAPHICS_API_D3D11
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>

#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <condition_variable>
#include <string>
#include <vector>

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace {

constexpr int EVENT_SUBMIT_FRAME = 0x48544601; // 'HTF\x01'

struct EyeSwapchain {
    XrSwapchain handle = XR_NULL_HANDLE;
    int32_t width = 0;
    int32_t height = 0;
    std::vector<XrSwapchainImageD3D11KHR> images;
};

#pragma pack(push, 4)
struct PoseData {
    float px, py, pz;
    float qx, qy, qz, qw;
    float vx, vy, vz;
    float wx, wy, wz;
    int32_t valid;
};

struct EyeViewData {
    PoseData pose;
    float angleLeft;
    float angleRight;
    float angleUp;
    float angleDown;
};

struct ControllerStateData {
    PoseData gripPose;
    PoseData aimPose;
    float stickX;
    float stickY;
    float trigger;
    float squeeze;
    uint32_t buttons; // bit0=Primary(A/X), bit1=Secondary(B/Y), bit2=StickClick, bit3=Menu, bit4=TriggerClick, bit5=GripClick
    int32_t active;
};

struct VrFrameData {
    int32_t sessionState;
    int32_t sessionRunning;
    int32_t shouldRender;
    int32_t viewsValid;
    PoseData headPose;
    EyeViewData eyes[2];
    ControllerStateData hands[2]; // 0 = Left, 1 = Right
};
#pragma pack(pop)

// Global state
std::mutex g_logMutex;
std::string g_lastError;

void SetError(const char* fmt, ...) {
    char buf[1024];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);
    std::lock_guard<std::mutex> lk(g_logMutex);
    g_lastError = buf;
}

XrInstance g_instance = XR_NULL_HANDLE;
XrSystemId g_systemId = XR_NULL_SYSTEM_ID;
XrSession g_session = XR_NULL_HANDLE;
XrSessionState g_sessionState = XR_SESSION_STATE_UNKNOWN;
bool g_sessionRunning = false;

XrSpace g_stageSpace = XR_NULL_HANDLE;
XrSpace g_localSpace = XR_NULL_HANDLE;
XrSpace g_viewSpace = XR_NULL_HANDLE;
bool g_hasStageSpace = false;
bool g_preferStageSpace = true;

int32_t g_recWidth = 2048;
int32_t g_recHeight = 2048;
int64_t g_swapchainFormat = DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
XrGraphicsRequirementsD3D11KHR g_d3d11Reqs{ XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };

ID3D11Device* g_d3d11Device = nullptr;
ID3D11DeviceContext* g_d3d11Context = nullptr;
EyeSwapchain g_swapchains[2];

// Actions
XrActionSet g_actionSet = XR_NULL_HANDLE;
XrPath g_handPaths[2] = { XR_NULL_PATH, XR_NULL_PATH };
XrAction g_actGripPose = XR_NULL_HANDLE;
XrAction g_actAimPose = XR_NULL_HANDLE;
XrAction g_actThumbstick = XR_NULL_HANDLE;
XrAction g_actTrigger = XR_NULL_HANDLE;
XrAction g_actSqueeze = XR_NULL_HANDLE;
XrAction g_actPrimaryBtn = XR_NULL_HANDLE;
XrAction g_actSecondaryBtn = XR_NULL_HANDLE;
XrAction g_actStickClick = XR_NULL_HANDLE;
XrAction g_actMenuBtn = XR_NULL_HANDLE;
XrAction g_actHaptic = XR_NULL_HANDLE;
XrSpace g_gripSpaces[2] = { XR_NULL_HANDLE, XR_NULL_HANDLE };
XrSpace g_aimSpaces[2] = { XR_NULL_HANDLE, XR_NULL_HANDLE };

// Frame handoff between Unity simulation thread and Unity render thread
std::mutex g_frameMutex;
std::condition_variable g_frameCv;
bool g_submitInFlight = false;

struct PendingRenderFrame {
    XrTime predictedDisplayTime = 0;
    XrBool32 shouldRender = XR_FALSE;
    bool viewsValid = false;
    XrSpace playSpace = XR_NULL_HANDLE;
    XrView views[2];
    ID3D11Texture2D* srcTextures[2] = { nullptr, nullptr };
} g_pendingFrame;

XrSpace GetActivePlaySpace() {
    if (g_preferStageSpace && g_hasStageSpace && g_stageSpace != XR_NULL_HANDLE) {
        return g_stageSpace;
    }
    return g_localSpace;
}

void ConvertPoseToUnity(const XrSpaceLocation& loc, const XrSpaceVelocity& vel, PoseData& out) {
    memset(&out, 0, sizeof(PoseData));
    const XrSpaceLocationFlags reqFlags = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
    if ((loc.locationFlags & reqFlags) == reqFlags) {
        out.px = loc.pose.position.x;
        out.py = loc.pose.position.y;
        out.pz = -loc.pose.position.z;

        out.qx = -loc.pose.orientation.x;
        out.qy = -loc.pose.orientation.y;
        out.qz = loc.pose.orientation.z;
        out.qw = loc.pose.orientation.w;
        out.valid = 1;
    } else {
        out.qw = 1.0f;
        out.valid = 0;
    }

    if (vel.velocityFlags & XR_SPACE_VELOCITY_LINEAR_VALID_BIT) {
        out.vx = vel.linearVelocity.x;
        out.vy = vel.linearVelocity.y;
        out.vz = -vel.linearVelocity.z;
    }
    if (vel.velocityFlags & XR_SPACE_VELOCITY_ANGULAR_VALID_BIT) {
        out.wx = -vel.angularVelocity.x;
        out.wy = -vel.angularVelocity.y;
        out.wz = vel.angularVelocity.z;
    }
}

XrPath GetXrPath(const char* pathStr) {
    XrPath p = XR_NULL_PATH;
    xrStringToPath(g_instance, pathStr, &p);
    return p;
}

void TrySuggestProfile(const char* profilePath, const std::vector<XrActionSuggestedBinding>& bindings) {
    if (bindings.empty()) return;
    XrPath prof = GetXrPath(profilePath);
    if (prof == XR_NULL_PATH) return;
    XrInteractionProfileSuggestedBinding suggested{ XR_TYPE_INTERACTION_PROFILE_SUGGESTED_BINDING };
    suggested.interactionProfile = prof;
    suggested.suggestedBindings = bindings.data();
    suggested.countSuggestedBindings = (uint32_t)bindings.size();
    xrSuggestInteractionProfileBindings(g_instance, &suggested);
}

bool CreateActionsAndSpaces() {
    XrActionSetCreateInfo asci{ XR_TYPE_ACTION_SET_CREATE_INFO };
    strcpy_s(asci.actionSetName, "gameplay");
    strcpy_s(asci.localizedActionSetName, "Gameplay");
    asci.priority = 0;
    XrResult r = xrCreateActionSet(g_instance, &asci, &g_actionSet);
    if (XR_FAILED(r)) {
        SetError("xrCreateActionSet failed: %d", (int)r);
        return false;
    }

    g_handPaths[0] = GetXrPath("/user/hand/left");
    g_handPaths[1] = GetXrPath("/user/hand/right");

    auto makeAction = [&](XrActionType type, const char* name, const char* locName, XrAction* outAct) -> bool {
        XrActionCreateInfo aci{ XR_TYPE_ACTION_CREATE_INFO };
        aci.actionType = type;
        strcpy_s(aci.actionName, name);
        strcpy_s(aci.localizedActionName, locName);
        aci.countSubactionPaths = 2;
        aci.subactionPaths = g_handPaths;
        XrResult res = xrCreateAction(g_actionSet, &aci, outAct);
        if (XR_FAILED(res)) {
            SetError("xrCreateAction(%s) failed: %d", name, (int)res);
            return false;
        }
        return true;
    };

    if (!makeAction(XR_ACTION_TYPE_POSE_INPUT, "grip_pose", "Grip Pose", &g_actGripPose)) return false;
    if (!makeAction(XR_ACTION_TYPE_POSE_INPUT, "aim_pose", "Aim Pose", &g_actAimPose)) return false;
    if (!makeAction(XR_ACTION_TYPE_VECTOR2F_INPUT, "thumbstick", "Thumbstick", &g_actThumbstick)) return false;
    if (!makeAction(XR_ACTION_TYPE_FLOAT_INPUT, "trigger", "Trigger", &g_actTrigger)) return false;
    if (!makeAction(XR_ACTION_TYPE_FLOAT_INPUT, "squeeze", "Grip Squeeze", &g_actSqueeze)) return false;
    if (!makeAction(XR_ACTION_TYPE_BOOLEAN_INPUT, "primary_button", "Primary Button", &g_actPrimaryBtn)) return false;
    if (!makeAction(XR_ACTION_TYPE_BOOLEAN_INPUT, "secondary_button", "Secondary Button", &g_actSecondaryBtn)) return false;
    if (!makeAction(XR_ACTION_TYPE_BOOLEAN_INPUT, "stick_click", "Thumbstick Click", &g_actStickClick)) return false;
    if (!makeAction(XR_ACTION_TYPE_BOOLEAN_INPUT, "menu_button", "Menu Button", &g_actMenuBtn)) return false;
    if (!makeAction(XR_ACTION_TYPE_VIBRATION_OUTPUT, "haptic", "Haptic Vibration", &g_actHaptic)) return false;

    auto b = [&](XrAction act, const char* path) -> XrActionSuggestedBinding {
        return XrActionSuggestedBinding{ act, GetXrPath(path) };
    };

    // 1. Oculus Touch (Quest 2 / 3 / 3S / Pro / Rift S / VDXR / SteamVR Touch)
    TrySuggestProfile("/interaction_profiles/oculus/touch_controller", {
        b(g_actGripPose, "/user/hand/left/input/grip/pose"),
        b(g_actGripPose, "/user/hand/right/input/grip/pose"),
        b(g_actAimPose, "/user/hand/left/input/aim/pose"),
        b(g_actAimPose, "/user/hand/right/input/aim/pose"),
        b(g_actThumbstick, "/user/hand/left/input/thumbstick"),
        b(g_actThumbstick, "/user/hand/right/input/thumbstick"),
        b(g_actTrigger, "/user/hand/left/input/trigger/value"),
        b(g_actTrigger, "/user/hand/right/input/trigger/value"),
        b(g_actSqueeze, "/user/hand/left/input/squeeze/value"),
        b(g_actSqueeze, "/user/hand/right/input/squeeze/value"),
        b(g_actPrimaryBtn, "/user/hand/left/input/x/click"),
        b(g_actPrimaryBtn, "/user/hand/right/input/a/click"),
        b(g_actSecondaryBtn, "/user/hand/left/input/y/click"),
        b(g_actSecondaryBtn, "/user/hand/right/input/b/click"),
        b(g_actStickClick, "/user/hand/left/input/thumbstick/click"),
        b(g_actStickClick, "/user/hand/right/input/thumbstick/click"),
        b(g_actMenuBtn, "/user/hand/left/input/menu/click"),
        b(g_actHaptic, "/user/hand/left/output/haptic"),
        b(g_actHaptic, "/user/hand/right/output/haptic"),
    });

    // 2. Valve Index Controller
    TrySuggestProfile("/interaction_profiles/valve/index_controller", {
        b(g_actGripPose, "/user/hand/left/input/grip/pose"),
        b(g_actGripPose, "/user/hand/right/input/grip/pose"),
        b(g_actAimPose, "/user/hand/left/input/aim/pose"),
        b(g_actAimPose, "/user/hand/right/input/aim/pose"),
        b(g_actThumbstick, "/user/hand/left/input/thumbstick"),
        b(g_actThumbstick, "/user/hand/right/input/thumbstick"),
        b(g_actTrigger, "/user/hand/left/input/trigger/value"),
        b(g_actTrigger, "/user/hand/right/input/trigger/value"),
        b(g_actSqueeze, "/user/hand/left/input/squeeze/value"),
        b(g_actSqueeze, "/user/hand/right/input/squeeze/value"),
        b(g_actPrimaryBtn, "/user/hand/left/input/a/click"),
        b(g_actPrimaryBtn, "/user/hand/right/input/a/click"),
        b(g_actSecondaryBtn, "/user/hand/left/input/b/click"),
        b(g_actSecondaryBtn, "/user/hand/right/input/b/click"),
        b(g_actStickClick, "/user/hand/left/input/thumbstick/click"),
        b(g_actStickClick, "/user/hand/right/input/thumbstick/click"),
        b(g_actMenuBtn, "/user/hand/left/input/trackpad/force"),
        b(g_actHaptic, "/user/hand/left/output/haptic"),
        b(g_actHaptic, "/user/hand/right/output/haptic"),
    });

    // 3. HTC Vive Controller
    TrySuggestProfile("/interaction_profiles/htc/vive_controller", {
        b(g_actGripPose, "/user/hand/left/input/grip/pose"),
        b(g_actGripPose, "/user/hand/right/input/grip/pose"),
        b(g_actAimPose, "/user/hand/left/input/aim/pose"),
        b(g_actAimPose, "/user/hand/right/input/aim/pose"),
        b(g_actThumbstick, "/user/hand/left/input/trackpad"),
        b(g_actThumbstick, "/user/hand/right/input/trackpad"),
        b(g_actTrigger, "/user/hand/left/input/trigger/value"),
        b(g_actTrigger, "/user/hand/right/input/trigger/value"),
        b(g_actSqueeze, "/user/hand/left/input/squeeze/click"),
        b(g_actSqueeze, "/user/hand/right/input/squeeze/click"),
        b(g_actPrimaryBtn, "/user/hand/left/input/trackpad/click"),
        b(g_actPrimaryBtn, "/user/hand/right/input/trackpad/click"),
        b(g_actMenuBtn, "/user/hand/left/input/menu/click"),
        b(g_actSecondaryBtn, "/user/hand/right/input/menu/click"),
        b(g_actHaptic, "/user/hand/left/output/haptic"),
        b(g_actHaptic, "/user/hand/right/output/haptic"),
    });

    // 4. Microsoft Mixed Reality Motion Controller
    TrySuggestProfile("/interaction_profiles/microsoft/motion_controller", {
        b(g_actGripPose, "/user/hand/left/input/grip/pose"),
        b(g_actGripPose, "/user/hand/right/input/grip/pose"),
        b(g_actAimPose, "/user/hand/left/input/aim/pose"),
        b(g_actAimPose, "/user/hand/right/input/aim/pose"),
        b(g_actThumbstick, "/user/hand/left/input/thumbstick"),
        b(g_actThumbstick, "/user/hand/right/input/thumbstick"),
        b(g_actTrigger, "/user/hand/left/input/trigger/value"),
        b(g_actTrigger, "/user/hand/right/input/trigger/value"),
        b(g_actSqueeze, "/user/hand/left/input/squeeze/click"),
        b(g_actSqueeze, "/user/hand/right/input/squeeze/click"),
        b(g_actPrimaryBtn, "/user/hand/left/input/trackpad/click"),
        b(g_actPrimaryBtn, "/user/hand/right/input/trackpad/click"),
        b(g_actStickClick, "/user/hand/left/input/thumbstick/click"),
        b(g_actStickClick, "/user/hand/right/input/thumbstick/click"),
        b(g_actMenuBtn, "/user/hand/left/input/menu/click"),
        b(g_actSecondaryBtn, "/user/hand/right/input/menu/click"),
        b(g_actHaptic, "/user/hand/left/output/haptic"),
        b(g_actHaptic, "/user/hand/right/output/haptic"),
    });

    // 5. KHR Simple Controller fallback
    TrySuggestProfile("/interaction_profiles/khr/simple_controller", {
        b(g_actGripPose, "/user/hand/left/input/grip/pose"),
        b(g_actGripPose, "/user/hand/right/input/grip/pose"),
        b(g_actAimPose, "/user/hand/left/input/aim/pose"),
        b(g_actAimPose, "/user/hand/right/input/aim/pose"),
        b(g_actPrimaryBtn, "/user/hand/left/input/select/click"),
        b(g_actPrimaryBtn, "/user/hand/right/input/select/click"),
        b(g_actMenuBtn, "/user/hand/left/input/menu/click"),
        b(g_actSecondaryBtn, "/user/hand/right/input/menu/click"),
        b(g_actHaptic, "/user/hand/left/output/haptic"),
        b(g_actHaptic, "/user/hand/right/output/haptic"),
    });

    for (int i = 0; i < 2; ++i) {
        XrActionSpaceCreateInfo spci{ XR_TYPE_ACTION_SPACE_CREATE_INFO };
        spci.action = g_actGripPose;
        spci.subactionPath = g_handPaths[i];
        spci.poseInActionSpace.orientation.w = 1.0f;
        if (XR_FAILED(xrCreateActionSpace(g_session, &spci, &g_gripSpaces[i]))) {
            SetError("xrCreateActionSpace(grip %d) failed", i);
            return false;
        }
        spci.action = g_actAimPose;
        if (XR_FAILED(xrCreateActionSpace(g_session, &spci, &g_aimSpaces[i]))) {
            SetError("xrCreateActionSpace(aim %d) failed", i);
            return false;
        }
    }

    XrSessionActionSetsAttachInfo attachInfo{ XR_TYPE_SESSION_ACTION_SETS_ATTACH_INFO };
    attachInfo.countActionSets = 1;
    attachInfo.actionSets = &g_actionSet;
    r = xrAttachSessionActionSets(g_session, &attachInfo);
    if (XR_FAILED(r)) {
        SetError("xrAttachSessionActionSets failed: %d", (int)r);
        return false;
    }

    return true;
}

void PollOpenXREvents() {
    if (g_instance == XR_NULL_HANDLE) return;
    XrEventDataBuffer ev{ XR_TYPE_EVENT_DATA_BUFFER };
    while (xrPollEvent(g_instance, &ev) == XR_SUCCESS) {
        if (ev.type == XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            auto* sc = reinterpret_cast<XrEventDataSessionStateChanged*>(&ev);
            g_sessionState = sc->state;
            if (g_session != XR_NULL_HANDLE) {
                if (g_sessionState == XR_SESSION_STATE_READY) {
                    XrSessionBeginInfo bi{ XR_TYPE_SESSION_BEGIN_INFO };
                    bi.primaryViewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                    if (XR_SUCCEEDED(xrBeginSession(g_session, &bi))) {
                        g_sessionRunning = true;
                    }
                } else if (g_sessionState == XR_SESSION_STATE_STOPPING) {
                    xrEndSession(g_session);
                    g_sessionRunning = false;
                } else if (g_sessionState == XR_SESSION_STATE_EXITING || g_sessionState == XR_SESSION_STATE_LOSS_PENDING) {
                    g_sessionRunning = false;
                }
            }
        }
        ev = XrEventDataBuffer{ XR_TYPE_EVENT_DATA_BUFFER };
    }
}

void SubmitFrameOnRenderThread() {
    PendingRenderFrame frame;
    {
        std::lock_guard<std::mutex> lk(g_frameMutex);
        if (!g_submitInFlight) return;
        frame = g_pendingFrame;
    }

    if (g_session != XR_NULL_HANDLE && g_sessionRunning && frame.predictedDisplayTime > 0) {
        XrFrameBeginInfo fbi{ XR_TYPE_FRAME_BEGIN_INFO };
        XrResult beginRes = xrBeginFrame(g_session, &fbi);
        if (XR_SUCCEEDED(beginRes)) {
            XrCompositionLayerProjection layer{ XR_TYPE_COMPOSITION_LAYER_PROJECTION };
            XrCompositionLayerProjectionView projViews[2]{
                { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW },
                { XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW }
            };
            const XrCompositionLayerBaseHeader* layers[1] = {
                reinterpret_cast<const XrCompositionLayerBaseHeader*>(&layer)
            };

            bool copiedBoth = false;
            if (frame.shouldRender && frame.viewsValid &&
                frame.srcTextures[0] != nullptr && frame.srcTextures[1] != nullptr &&
                g_d3d11Context != nullptr &&
                g_swapchains[0].handle != XR_NULL_HANDLE && g_swapchains[1].handle != XR_NULL_HANDLE) {

                copiedBoth = true;
                for (int eye = 0; eye < 2; ++eye) {
                    XrSwapchainImageAcquireInfo acq{ XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO };
                    uint32_t imgIdx = 0;
                    if (XR_FAILED(xrAcquireSwapchainImage(g_swapchains[eye].handle, &acq, &imgIdx))) {
                        copiedBoth = false;
                        break;
                    }
                    XrSwapchainImageWaitInfo wi{ XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO };
                    wi.timeout = 100000000LL; // 100ms
                    if (XR_FAILED(xrWaitSwapchainImage(g_swapchains[eye].handle, &wi))) {
                        XrSwapchainImageReleaseInfo rel{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
                        xrReleaseSwapchainImage(g_swapchains[eye].handle, &rel);
                        copiedBoth = false;
                        break;
                    }

                    ID3D11Texture2D* dstTex = g_swapchains[eye].images[imgIdx].texture;
                    if (dstTex != nullptr && frame.srcTextures[eye] != nullptr) {
                        g_d3d11Context->CopyResource(dstTex, frame.srcTextures[eye]);
                    }

                    XrSwapchainImageReleaseInfo rel{ XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO };
                    xrReleaseSwapchainImage(g_swapchains[eye].handle, &rel);

                    projViews[eye].pose = frame.views[eye].pose;
                    projViews[eye].fov = frame.views[eye].fov;
                    projViews[eye].subImage.swapchain = g_swapchains[eye].handle;
                    projViews[eye].subImage.imageRect.offset = { 0, 0 };
                    projViews[eye].subImage.imageRect.extent = { g_swapchains[eye].width, g_swapchains[eye].height };
                    projViews[eye].subImage.imageArrayIndex = 0;
                }
            }

            XrFrameEndInfo fei{ XR_TYPE_FRAME_END_INFO };
            fei.displayTime = frame.predictedDisplayTime;
            fei.environmentBlendMode = XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
            if (copiedBoth && frame.playSpace != XR_NULL_HANDLE) {
                layer.space = frame.playSpace;
                layer.viewCount = 2;
                layer.views = projViews;
                fei.layerCount = 1;
                fei.layers = layers;
            } else {
                fei.layerCount = 0;
                fei.layers = nullptr;
            }
            xrEndFrame(g_session, &fei);
        }
    }

    {
        std::lock_guard<std::mutex> lk(g_frameMutex);
        g_submitInFlight = false;
    }
    g_frameCv.notify_all();
}

void __stdcall OnRenderEvent(int eventId) {
    if (eventId == EVENT_SUBMIT_FRAME) {
        SubmitFrameOnRenderThread();
    }
}

} // namespace

extern "C" {

__declspec(dllexport) int32_t HtfVr_GetLastError(char* buffer, int32_t bufferSize) {
    if (!buffer || bufferSize <= 0) return 0;
    std::lock_guard<std::mutex> lk(g_logMutex);
    int32_t len = (int32_t)g_lastError.size();
    if (len >= bufferSize) len = bufferSize - 1;
    memcpy(buffer, g_lastError.data(), len);
    buffer[len] = '\0';
    return len;
}

__declspec(dllexport) int32_t HtfVr_InitInstance() {
    if (g_instance != XR_NULL_HANDLE && g_systemId != XR_NULL_SYSTEM_ID) {
        return 1;
    }

    if (g_instance == XR_NULL_HANDLE) {
        uint32_t extCount = 0;
        XrResult r = xrEnumerateInstanceExtensionProperties(nullptr, 0, &extCount, nullptr);
        if (XR_FAILED(r) || extCount == 0) {
            SetError("xrEnumerateInstanceExtensionProperties waiting for OpenXR runtime (%d)", (int)r);
            return -1; // Retryable once user starts SteamVR / Quest Link / Virtual Desktop!
        }
        std::vector<XrExtensionProperties> exts(extCount, { XR_TYPE_EXTENSION_PROPERTIES });
        xrEnumerateInstanceExtensionProperties(nullptr, extCount, &extCount, exts.data());

        bool hasD3D11 = false;
        for (const auto& e : exts) {
            if (strcmp(e.extensionName, XR_KHR_D3D11_ENABLE_EXTENSION_NAME) == 0) {
                hasD3D11 = true;
                break;
            }
        }
        if (!hasD3D11) {
            SetError("Active OpenXR runtime does not support XR_KHR_D3D11_enable");
            return 0;
        }

        const char* enabledExts[] = { XR_KHR_D3D11_ENABLE_EXTENSION_NAME };
        XrInstanceCreateInfo ici{ XR_TYPE_INSTANCE_CREATE_INFO };
        strcpy_s(ici.applicationInfo.applicationName, "How to Fish VR");
        ici.applicationInfo.applicationVersion = 1;
        strcpy_s(ici.applicationInfo.engineName, "Unity");
        ici.applicationInfo.engineVersion = 6000;
        ici.applicationInfo.apiVersion = XR_MAKE_VERSION(1, 0, 0);
        ici.enabledExtensionCount = 1;
        ici.enabledExtensionNames = enabledExts;

        r = xrCreateInstance(&ici, &g_instance);
        if (XR_FAILED(r)) {
            SetError("xrCreateInstance waiting for OpenXR runtime/headset (%d)", (int)r);
            g_instance = XR_NULL_HANDLE;
            return -1; // Retryable when SteamVR / Oculus / VDXR runtime finishes starting!
        }
    }

    XrSystemGetInfo sgi{ XR_TYPE_SYSTEM_GET_INFO };
    sgi.formFactor = XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    XrResult r = xrGetSystem(g_instance, &sgi, &g_systemId);
    if (XR_FAILED(r)) {
        SetError("xrGetSystem failed (is VR headset connected/awake?): %d", (int)r);
        g_systemId = XR_NULL_SYSTEM_ID;
        return -1; // HMD not ready yet; can retry later!
    }

    uint32_t viewCount = 0;
    r = xrEnumerateViewConfigurationViews(g_instance, g_systemId, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, 0, &viewCount, nullptr);
    if (XR_FAILED(r) || viewCount < 2) {
        SetError("xrEnumerateViewConfigurationViews failed: %d (count=%u)", (int)r, viewCount);
        return 0;
    }
    std::vector<XrViewConfigurationView> cfgViews(viewCount, { XR_TYPE_VIEW_CONFIGURATION_VIEW });
    xrEnumerateViewConfigurationViews(g_instance, g_systemId, XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO, viewCount, &viewCount, cfgViews.data());
    g_recWidth = (int32_t)cfgViews[0].recommendedImageRectWidth;
    g_recHeight = (int32_t)cfgViews[0].recommendedImageRectHeight;

    PFN_xrGetD3D11GraphicsRequirementsKHR pfnGetReqs = nullptr;
    r = xrGetInstanceProcAddr(g_instance, "xrGetD3D11GraphicsRequirementsKHR", reinterpret_cast<PFN_xrVoidFunction*>(&pfnGetReqs));
    if (XR_FAILED(r) || !pfnGetReqs) {
        SetError("xrGetInstanceProcAddr(xrGetD3D11GraphicsRequirementsKHR) failed: %d", (int)r);
        return 0;
    }
    g_d3d11Reqs = { XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR };
    r = pfnGetReqs(g_instance, g_systemId, &g_d3d11Reqs);
    if (XR_FAILED(r)) {
        SetError("xrGetD3D11GraphicsRequirementsKHR failed: %d", (int)r);
        return 0;
    }

    return 1;
}

__declspec(dllexport) void HtfVr_GetRecommendedResolution(int32_t* outWidth, int32_t* outHeight) {
    if (outWidth) *outWidth = g_recWidth;
    if (outHeight) *outHeight = g_recHeight;
}

__declspec(dllexport) int32_t HtfVr_CreateSession(void* sampleD3D11Texture, int32_t width, int32_t height) {
    if (g_session != XR_NULL_HANDLE) return 1;
    if (g_instance == XR_NULL_HANDLE || g_systemId == XR_NULL_SYSTEM_ID) {
        SetError("HtfVr_CreateSession called before HtfVr_InitInstance");
        return 0;
    }
    if (!sampleD3D11Texture) {
        SetError("sampleD3D11Texture is null");
        return 0;
    }

    auto* tex = static_cast<ID3D11Texture2D*>(sampleD3D11Texture);
    tex->GetDevice(&g_d3d11Device);
    if (!g_d3d11Device) {
        SetError("Failed to retrieve ID3D11Device from RenderTexture");
        return 0;
    }
    g_d3d11Device->GetImmediateContext(&g_d3d11Context);

    XrGraphicsBindingD3D11KHR d3dBinding{ XR_TYPE_GRAPHICS_BINDING_D3D11_KHR };
    d3dBinding.device = g_d3d11Device;

    XrSessionCreateInfo sci{ XR_TYPE_SESSION_CREATE_INFO };
    sci.next = &d3dBinding;
    sci.systemId = g_systemId;

    XrResult r = xrCreateSession(g_instance, &sci, &g_session);
    if (XR_FAILED(r)) {
        SetError("xrCreateSession failed: %d", (int)r);
        g_session = XR_NULL_HANDLE;
        return 0;
    }

    // Reference spaces
    uint32_t spaceCount = 0;
    xrEnumerateReferenceSpaces(g_session, 0, &spaceCount, nullptr);
    std::vector<XrReferenceSpaceType> spaceTypes(spaceCount);
    if (spaceCount > 0) {
        xrEnumerateReferenceSpaces(g_session, spaceCount, &spaceCount, spaceTypes.data());
    }
    g_hasStageSpace = false;
    for (auto st : spaceTypes) {
        if (st == XR_REFERENCE_SPACE_TYPE_STAGE) g_hasStageSpace = true;
    }

    XrReferenceSpaceCreateInfo rsci{ XR_TYPE_REFERENCE_SPACE_CREATE_INFO };
    rsci.poseInReferenceSpace.orientation.w = 1.0f;

    if (g_hasStageSpace) {
        rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_STAGE;
        if (XR_FAILED(xrCreateReferenceSpace(g_session, &rsci, &g_stageSpace))) {
            g_hasStageSpace = false;
        }
    }

    rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_LOCAL;
    if (XR_FAILED(xrCreateReferenceSpace(g_session, &rsci, &g_localSpace))) {
        SetError("xrCreateReferenceSpace(LOCAL) failed");
        return 0;
    }

    rsci.referenceSpaceType = XR_REFERENCE_SPACE_TYPE_VIEW;
    if (XR_FAILED(xrCreateReferenceSpace(g_session, &rsci, &g_viewSpace))) {
        SetError("xrCreateReferenceSpace(VIEW) failed");
        return 0;
    }

    // Choose swapchain format
    uint32_t fmtCount = 0;
    xrEnumerateSwapchainFormats(g_session, 0, &fmtCount, nullptr);
    std::vector<int64_t> fmts(fmtCount);
    xrEnumerateSwapchainFormats(g_session, fmtCount, &fmtCount, fmts.data());

    const int64_t preferred[] = {
        DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
        DXGI_FORMAT_R8G8B8A8_UNORM,
        DXGI_FORMAT_B8G8R8A8_UNORM
    };
    g_swapchainFormat = fmts.empty() ? DXGI_FORMAT_R8G8B8A8_UNORM_SRGB : fmts[0];
    bool foundFmt = false;
    for (int64_t p : preferred) {
        for (int64_t f : fmts) {
            if (f == p) {
                g_swapchainFormat = p;
                foundFmt = true;
                break;
            }
        }
        if (foundFmt) break;
    }

    for (int eye = 0; eye < 2; ++eye) {
        XrSwapchainCreateInfo swci{ XR_TYPE_SWAPCHAIN_CREATE_INFO };
        swci.usageFlags = XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT | XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT | XR_SWAPCHAIN_USAGE_SAMPLED_BIT;
        swci.format = g_swapchainFormat;
        swci.sampleCount = 1;
        swci.width = (uint32_t)width;
        swci.height = (uint32_t)height;
        swci.faceCount = 1;
        swci.arraySize = 1;
        swci.mipCount = 1;

        r = xrCreateSwapchain(g_session, &swci, &g_swapchains[eye].handle);
        if (XR_FAILED(r)) {
            SetError("xrCreateSwapchain(eye %d) failed: %d", eye, (int)r);
            return 0;
        }
        g_swapchains[eye].width = width;
        g_swapchains[eye].height = height;

        uint32_t imgCount = 0;
        xrEnumerateSwapchainImages(g_swapchains[eye].handle, 0, &imgCount, nullptr);
        g_swapchains[eye].images.resize(imgCount, { XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR });
        xrEnumerateSwapchainImages(
            g_swapchains[eye].handle,
            imgCount,
            &imgCount,
            reinterpret_cast<XrSwapchainImageBaseHeader*>(g_swapchains[eye].images.data()));
    }

    if (!CreateActionsAndSpaces()) {
        return 0;
    }

    PollOpenXREvents();
    return 1;
}

__declspec(dllexport) int32_t HtfVr_GetSwapchainDxgiFormat() {
    return (int32_t)g_swapchainFormat;
}

__declspec(dllexport) int32_t HtfVr_HasStageSpace() {
    return g_hasStageSpace ? 1 : 0;
}

__declspec(dllexport) void HtfVr_SetPreferStageSpace(int32_t preferStage) {
    g_preferStageSpace = (preferStage != 0);
}

__declspec(dllexport) int32_t HtfVr_WaitAndUpdateFrame(VrFrameData* outFrame) {
    if (!outFrame) return 0;
    memset(outFrame, 0, sizeof(VrFrameData));
    outFrame->headPose.qw = 1.0f;
    outFrame->eyes[0].pose.qw = 1.0f;
    outFrame->eyes[1].pose.qw = 1.0f;
    outFrame->hands[0].gripPose.qw = 1.0f;
    outFrame->hands[0].aimPose.qw = 1.0f;
    outFrame->hands[1].gripPose.qw = 1.0f;
    outFrame->hands[1].aimPose.qw = 1.0f;

    if (g_session == XR_NULL_HANDLE) return 0;

    // Ensure previous render thread submit finished before calling xrWaitFrame
    {
        std::unique_lock<std::mutex> lk(g_frameMutex);
        g_frameCv.wait_for(lk, std::chrono::milliseconds(150), [] { return !g_submitInFlight; });
        g_submitInFlight = false;
    }

    PollOpenXREvents();
    outFrame->sessionState = (int32_t)g_sessionState;
    outFrame->sessionRunning = g_sessionRunning ? 1 : 0;

    if (!g_sessionRunning) {
        return 1;
    }

    XrFrameWaitInfo fwi{ XR_TYPE_FRAME_WAIT_INFO };
    XrFrameState fs{ XR_TYPE_FRAME_STATE };
    XrResult r = xrWaitFrame(g_session, &fwi, &fs);
    if (XR_FAILED(r)) {
        SetError("xrWaitFrame failed: %d", (int)r);
        return 0;
    }

    outFrame->shouldRender = fs.shouldRender ? 1 : 0;
    XrSpace playSpace = GetActivePlaySpace();

    // Locate views
    XrView views[2]{ { XR_TYPE_VIEW }, { XR_TYPE_VIEW } };
    XrViewState viewState{ XR_TYPE_VIEW_STATE };
    XrViewLocateInfo vli{ XR_TYPE_VIEW_LOCATE_INFO };
    vli.viewConfigurationType = XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    vli.displayTime = fs.predictedDisplayTime;
    vli.space = playSpace;

    uint32_t viewCount = 0;
    r = xrLocateViews(g_session, &vli, &viewState, 2, &viewCount, views);
    const XrViewStateFlags reqViewFlags = XR_VIEW_STATE_POSITION_VALID_BIT | XR_VIEW_STATE_ORIENTATION_VALID_BIT;
    bool viewsValid = XR_SUCCEEDED(r) && viewCount == 2 && ((viewState.viewStateFlags & reqViewFlags) == reqViewFlags);
    outFrame->viewsValid = viewsValid ? 1 : 0;

    if (viewsValid) {
        for (int eye = 0; eye < 2; ++eye) {
            XrSpaceLocation eyeLoc{ XR_TYPE_SPACE_LOCATION };
            eyeLoc.locationFlags = XR_SPACE_LOCATION_POSITION_VALID_BIT | XR_SPACE_LOCATION_ORIENTATION_VALID_BIT;
            eyeLoc.pose = views[eye].pose;
            XrSpaceVelocity dummyVel{ XR_TYPE_SPACE_VELOCITY };
            ConvertPoseToUnity(eyeLoc, dummyVel, outFrame->eyes[eye].pose);
            outFrame->eyes[eye].angleLeft = views[eye].fov.angleLeft;
            outFrame->eyes[eye].angleRight = views[eye].fov.angleRight;
            outFrame->eyes[eye].angleUp = views[eye].fov.angleUp;
            outFrame->eyes[eye].angleDown = views[eye].fov.angleDown;
        }
    }

    // Locate HMD center view space
    {
        XrSpaceVelocity hmdVel{ XR_TYPE_SPACE_VELOCITY };
        XrSpaceLocation hmdLoc{ XR_TYPE_SPACE_LOCATION };
        hmdLoc.next = &hmdVel;
        if (XR_SUCCEEDED(xrLocateSpace(g_viewSpace, playSpace, fs.predictedDisplayTime, &hmdLoc))) {
            ConvertPoseToUnity(hmdLoc, hmdVel, outFrame->headPose);
        }
    }

    // Sync actions & read controllers
    XrActiveActionSet activeSet{};
    activeSet.actionSet = g_actionSet;
    activeSet.subactionPath = XR_NULL_PATH;
    XrActionsSyncInfo syncInfo{ XR_TYPE_ACTIONS_SYNC_INFO };
    syncInfo.countActiveActionSets = 1;
    syncInfo.activeActionSets = &activeSet;
    xrSyncActions(g_session, &syncInfo);

    for (int h = 0; h < 2; ++h) {
        ControllerStateData& hs = outFrame->hands[h];

        XrSpaceVelocity gVel{ XR_TYPE_SPACE_VELOCITY };
        XrSpaceLocation gLoc{ XR_TYPE_SPACE_LOCATION };
        gLoc.next = &gVel;
        if (XR_SUCCEEDED(xrLocateSpace(g_gripSpaces[h], playSpace, fs.predictedDisplayTime, &gLoc))) {
            ConvertPoseToUnity(gLoc, gVel, hs.gripPose);
        }

        XrSpaceVelocity aVel{ XR_TYPE_SPACE_VELOCITY };
        XrSpaceLocation aLoc{ XR_TYPE_SPACE_LOCATION };
        aLoc.next = &aVel;
        if (XR_SUCCEEDED(xrLocateSpace(g_aimSpaces[h], playSpace, fs.predictedDisplayTime, &aLoc))) {
            ConvertPoseToUnity(aLoc, aVel, hs.aimPose);
        }

        hs.active = (hs.gripPose.valid || hs.aimPose.valid) ? 1 : 0;

        XrActionStateGetInfo gi{ XR_TYPE_ACTION_STATE_GET_INFO };
        gi.subactionPath = g_handPaths[h];

        XrActionStateVector2f v2{ XR_TYPE_ACTION_STATE_VECTOR2F };
        gi.action = g_actThumbstick;
        if (XR_SUCCEEDED(xrGetActionStateVector2f(g_session, &gi, &v2)) && v2.isActive) {
            hs.stickX = v2.currentState.x;
            hs.stickY = v2.currentState.y;
            hs.active = 1;
        }

        XrActionStateFloat flt{ XR_TYPE_ACTION_STATE_FLOAT };
        gi.action = g_actTrigger;
        if (XR_SUCCEEDED(xrGetActionStateFloat(g_session, &gi, &flt)) && flt.isActive) {
            hs.trigger = flt.currentState;
            if (hs.trigger > 0.65f) hs.buttons |= (1u << 4);
            hs.active = 1;
        }

        flt = { XR_TYPE_ACTION_STATE_FLOAT };
        gi.action = g_actSqueeze;
        if (XR_SUCCEEDED(xrGetActionStateFloat(g_session, &gi, &flt)) && flt.isActive) {
            hs.squeeze = flt.currentState;
            if (hs.squeeze > 0.65f) hs.buttons |= (1u << 5);
        }

        auto getBtn = [&](XrAction act, uint32_t bit) {
            XrActionStateBoolean bState{ XR_TYPE_ACTION_STATE_BOOLEAN };
            gi.action = act;
            if (XR_SUCCEEDED(xrGetActionStateBoolean(g_session, &gi, &bState)) && bState.isActive && bState.currentState) {
                hs.buttons |= (1u << bit);
            }
        };
        getBtn(g_actPrimaryBtn, 0);
        getBtn(g_actSecondaryBtn, 1);
        getBtn(g_actStickClick, 2);
        getBtn(g_actMenuBtn, 3);
    }

    // Store pending frame data for render thread submission
    {
        std::lock_guard<std::mutex> lk(g_frameMutex);
        g_pendingFrame.predictedDisplayTime = fs.predictedDisplayTime;
        g_pendingFrame.shouldRender = fs.shouldRender;
        g_pendingFrame.viewsValid = viewsValid;
        g_pendingFrame.playSpace = playSpace;
        g_pendingFrame.views[0] = views[0];
        g_pendingFrame.views[1] = views[1];
    }

    return 1;
}

__declspec(dllexport) void HtfVr_PrepareSubmit(void* leftEyeD3D11Tex, void* rightEyeD3D11Tex) {
    std::lock_guard<std::mutex> lk(g_frameMutex);
    g_pendingFrame.srcTextures[0] = static_cast<ID3D11Texture2D*>(leftEyeD3D11Tex);
    g_pendingFrame.srcTextures[1] = static_cast<ID3D11Texture2D*>(rightEyeD3D11Tex);
    g_submitInFlight = true;
}

__declspec(dllexport) void* HtfVr_GetRenderEventFunc() {
    return reinterpret_cast<void*>(&OnRenderEvent);
}

__declspec(dllexport) int32_t HtfVr_GetSubmitEventId() {
    return EVENT_SUBMIT_FRAME;
}

__declspec(dllexport) void HtfVr_TriggerHaptic(int32_t handIndex, float amplitude, float durationSeconds, float frequencyHz) {
    if (g_session == XR_NULL_HANDLE || !g_sessionRunning) return;
    if (handIndex < 0 || handIndex > 1) return;

    XrHapticActionInfo hai{ XR_TYPE_HAPTIC_ACTION_INFO };
    hai.action = g_actHaptic;
    hai.subactionPath = g_handPaths[handIndex];

    XrHapticVibration vib{ XR_TYPE_HAPTIC_VIBRATION };
    vib.amplitude = (amplitude < 0.0f) ? 0.0f : ((amplitude > 1.0f) ? 1.0f : amplitude);
    int64_t durNs = (int64_t)(durationSeconds * 1e9f);
    vib.duration = (durNs <= 0) ? XR_MIN_HAPTIC_DURATION : durNs;
    vib.frequency = (frequencyHz <= 0.0f) ? XR_FREQUENCY_UNSPECIFIED : frequencyHz;

    xrApplyHapticFeedback(g_session, &hai, reinterpret_cast<const XrHapticBaseHeader*>(&vib));
}

__declspec(dllexport) void HtfVr_Shutdown() {
    {
        std::unique_lock<std::mutex> lk(g_frameMutex);
        g_frameCv.wait_for(lk, std::chrono::milliseconds(200), [] { return !g_submitInFlight; });
        g_submitInFlight = false;
    }

    for (int i = 0; i < 2; ++i) {
        if (g_gripSpaces[i] != XR_NULL_HANDLE) { xrDestroySpace(g_gripSpaces[i]); g_gripSpaces[i] = XR_NULL_HANDLE; }
        if (g_aimSpaces[i] != XR_NULL_HANDLE) { xrDestroySpace(g_aimSpaces[i]); g_aimSpaces[i] = XR_NULL_HANDLE; }
        if (g_swapchains[i].handle != XR_NULL_HANDLE) { xrDestroySwapchain(g_swapchains[i].handle); g_swapchains[i].handle = XR_NULL_HANDLE; }
        g_swapchains[i].images.clear();
    }
    if (g_actionSet != XR_NULL_HANDLE) { xrDestroyActionSet(g_actionSet); g_actionSet = XR_NULL_HANDLE; }
    if (g_stageSpace != XR_NULL_HANDLE) { xrDestroySpace(g_stageSpace); g_stageSpace = XR_NULL_HANDLE; }
    if (g_localSpace != XR_NULL_HANDLE) { xrDestroySpace(g_localSpace); g_localSpace = XR_NULL_HANDLE; }
    if (g_viewSpace != XR_NULL_HANDLE) { xrDestroySpace(g_viewSpace); g_viewSpace = XR_NULL_HANDLE; }
    if (g_session != XR_NULL_HANDLE) {
        if (g_sessionRunning) xrEndSession(g_session);
        xrDestroySession(g_session);
        g_session = XR_NULL_HANDLE;
    }
    g_sessionRunning = false;
    if (g_d3d11Context) { g_d3d11Context->Release(); g_d3d11Context = nullptr; }
    if (g_d3d11Device) { g_d3d11Device->Release(); g_d3d11Device = nullptr; }
    if (g_instance != XR_NULL_HANDLE) { xrDestroyInstance(g_instance); g_instance = XR_NULL_HANDLE; }
    g_systemId = XR_NULL_SYSTEM_ID;
}

} // extern "C"
