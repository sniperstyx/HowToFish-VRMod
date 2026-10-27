# How To Fish — Full 6DOF VR Mod (`v1.0.0`)

A complete **OpenXR 1.0 + Direct3D 11** 6DOF VR conversion mod for **How to Fish** (Unity 6000.4 URP), featuring true stereoscopic rendering, calibrated 1:1 playermodel hands & finger curl, physical motion-controlled fishing, **Pavlov VR / H3VR-style multi-stage manual weapon reloading**, physical arm-swing melee & boxing, holographic wrist menus, a **Half-Life: Alyx-inspired gaze-fading Desktop Spectator UI** for streaming/recording, and **100% vanilla + VR-to-VR multiplayer crossplay**.

> **Current Release:** `v1.0.0` *(First Public Release Candidate)*

---

## Installation Guide (Choose Either Method!)

Every release `.zip` on the [**Releases**](../../releases) page supports **both** a 1-click automatic installer **and** a 100% script-free manual drag-and-drop installation.

### Option A: Easy One-Click Installation (`Install-HowToFishVR.bat`)

1. Download the latest `HowToFish-VRMod-vX.Y.Z.zip` from [**Releases**](../../releases).
2. **Right-click** the downloaded `.zip` file and select **Extract All...** to unpack it into normal folder.
3. Open the extracted folder and double-click **`Install-HowToFishVR.bat`**.
   - The script automatically finds your `"How to Fish"` Steam installation across all drives (or opens a standard folder picker if installed in a custom path) and copies the mod files into place.
4. Start your VR runtime (**SteamVR**, **Meta Quest Link / Air Link**, or **Virtual Desktop / VDXR**) and launch **How to Fish** normally from Steam!

---

### Option B: Step-by-Step Manual Installation (No Scripts Needed)

If you prefer **never** running `.bat` or installer scripts downloaded from the internet (which is completely understandable and good security practice!), you can install the mod manually in under 30 seconds without running any script at all:

1. Download `HowToFish-VRMod-vX.Y.Z.zip` from [**Releases**](../../releases) and open/extract it.
2. Open **Steam**, right-click **How to Fish** in your Library, and click **Manage -> Browse local files**.
3. Open the inner **`How to Fish`** subfolder so you can see **`How to Fish.exe`** (for example: `...\Steam\steamapps\common\How to Fish\How to Fish\`).
4. From the extracted `.zip`, **drag and drop ONLY these 3 items** directly into the folder next to `How to Fish.exe`:
   - **`BepInEx`** *(folder)*
   - **`doorstop_config.ini`** *(file)*
   - **`winhttp.dll`** *(file)*
   *(You can completely ignore or delete `Install-HowToFishVR.bat` and `Uninstall-HowToFishVR.bat`!)*
5. Verify your game folder looks like this:
   ```text
   How to Fish/
   ├── BepInEx/
   │   ├── core/                        (Official BepInEx 5.4.23.6 x64 Unity loader)
   │   └── plugins/
   │       └── HowToFishVR/
   │           ├── HowToFishVR.dll       (Managed C# VR mod plugin)
   │           ├── HowToFishVRNative.dll (Native C++17 OpenXR + D3D11 stereo bridge)
   │           └── openxr_loader.dll     (Official Khronos OpenXR loader)
   ├── How to Fish_Data/
   ├── doorstop_config.ini               (Tells Unity Doorstop to load BepInEx)
   ├── winhttp.dll                       (Standard Unity Doorstop proxy DLL)
   └── How to Fish.exe
   ```
6. Start your VR runtime (SteamVR / Quest Link / Virtual Desktop) and launch **How to Fish** from Steam!

> **Script Transparency Note:** Curious what `Install-HowToFishVR.bat` actually does? Right-click it and select **Edit in Notepad** — it is plain, un-obfuscated Windows Batch that makes **zero** internet connections, **zero** registry edits, and only copies `BepInEx\`, `winhttp.dll`, and `doorstop_config.ini` into your `How to Fish` folder.

### Uninstalling (or Temporarily Playing in Flatscreen Mode)
- **Temporary Flatscreen Toggle (No Uninstall Needed):** Open `How to Fish\BepInEx\config\com.howtofish.vrmod.cfg` and set `EnableVR = false` to boot in standard desktop mode anytime.
- **Manual Uninstall:** Delete `winhttp.dll`, `doorstop_config.ini`, and `BepInEx\plugins\HowToFishVR\` (or the `BepInEx\` folder) next to `How to Fish.exe`. Your save files are never modified.
- **One-Click Uninstall:** Double-click `Uninstall-HowToFishVR.bat`.

---

## Key Features

- **Native OpenXR 1.0 + D3D11 Stereo Rendering**:
  - Works with SteamVR, Meta Quest Link / Air Link, Virtual Desktop (VDXR), PICO, Valve Index, HTC Vive, and WMR headsets.
  - Asymmetric per-eye frustum projection, MSAA eye swapchains, zero-parallax stereo Red Dot sights, and 3D Picture-in-Picture (`RenderTexture`) zoomed Sniper Scope lenses.
- **Comfort Main Menu, Floating 3D Title (`v1.0.0` Badge) & Holographic Wrist Menu**:
  - Calm, slow-paced background boat & wave simulation on the Main Menu (`MenuSimulationPace = 0.20`) to prevent motion sickness before entering a game.
  - Floating 3D **"How to Fish"** title, clear lowered wrist-prompt subtitle, and **VR Mod version badge (`v1.0.0`)** in the world + Wrist Holo-Pad.
  - **Left-Hand Holographic Wrist Menu** (both on the **Main Menu** and **inside saves**): turn your left wrist toward your face and look at it to smoothly fade in instant controls for **Pause / Options**, **Findings / Journal**, **Combat & Reload Mode (`MANUAL VR` / `AUTOMATIC`)**, **Drop Item**, **Holster**, **Snap/Smooth Turn**, **Recenter VR**, and live weapon reload telemetry.
- **Half-Life: Alyx Desktop Spectator UI with Gaze-Based Fade**:
  - Renders a crisp, native-resolution static 2D spectator HUD (**Health**, **Fullness/Hunger**, **Poison/Fire alerts**, **Live 3D Hotbar & Bait icons**, **Digital Weapon Ammo + Chamber + Reload Stage**, **Money**, and **Boss HP**) exclusively on the desktop window for Discord screensharing, streaming, and recording — **never** inside the VR headset lenses.
  - **Gaze-Fade Twist**: Automatically fades **out** whenever the VR player looks directly at their in-game 3D UI (Wrist Menu, Wrist HUD, Modal Menus, or WorldSpace HUD) and smoothly fades **back in** when the player looks back out at the world!
- **Pavlov VR / H3VR Multi-Stage Manual Weapon Reloading & Physical Combat**:
  - **Manual VR Mode (Default)** tailored to each firearm's real action:
    - **Box-Magazine Pistols, SMGs & Rifles**: Eject mag (`Left X` or grab & pull magazine downward with off-hand; retains `+1` round in the chamber on tactical reloads!) -> grab fresh magazine from your **chest/hip ammo pouch** with Off-Hand Grip/Trigger -> insert into the glowing magwell -> **grab & rack the slide / charging handle** backward and forward to chamber a round!
    - **Bolt-Action Sniper Rifles**: Eject mag -> insert fresh mag from pouch -> **grab & cycle the bolt handle** back and forward after empty reloads.
    - **Pump / Tube Shotguns**: Grab **12-gauge shells** one-by-one from your ammo pouch and thumb-load them into the loading port -> **pump the forend** back and forward to chamber!
    - **Break-Action Shotguns / Launchers & Revolvers**: Pop open the breech/cylinder (`Left X` or off-hand pull) -> load shell / speedloader from your pouch -> **flick your firing wrist upward** (or push the barrel closed with your off-hand) to lock the action shut!
  - **Physical Arm-Swing Melee & Bare-Handed Boxing**:
    - In **Manual VR Mode**, knives/melee weapons and bare-handed punches require **physically swinging your arms in real life** to damage fish and creatures (trigger auto-swing is disabled).
    - Toggle **`Combat & Reload: AUTOMATIC`** anytime from your Left Wrist Menu if you prefer desktop-style button reloads and trigger melee attacks while in VR!
- **6DOF Fishing & Boat Driving**:
  - Hold Left Trigger to open the bail, whip-cast your fishing rod in 3D space, and release to launch your bait along your real swing arc; physically crank the spinning reel with your off-hand (or hold Right Trigger) and pump the rod upward with live bite/tension haptics.
  - Grab and steer the boat's 3D wheel and push/pull the throttle lever physically with your VR hands.
- **Calibrated Anatomical Hands, VR Controller Icons & Full Multiplayer**:
  - Orthogonalized metacarpal-plane hand & 15-joint finger curl calibration so fingers curl cleanly into a natural fist without sideways skew.
  - Color-coded **VR Hand Controller button icons** replacing flatscreen Xbox prompts.
  - **100% Vanilla Crossplay** + automatic **30 Hz VR-to-VR bimanual 6DOF hand & finger sync** over SteamNetworkingMessages channel `77`.

---

## VR Controls Reference

| VR Control | Action |
| :--- | :--- |
| **Turn Left Wrist & Look at It** | **Open Holographic Wrist Menu** *(Main Menu & In-Save)* |
| **Left Thumbstick** | Move (On Foot) / Steer Boat |
| **Left Stick Click** | Sprint *(`Left Grip + Left Stick Click`: Push-To-Talk)* |
| **Right Thumbstick Left / Right** | Snap Turn (`45°`) or Smooth Turn *(Flips pages while Findings/Journal is open!)* |
| **Right Thumbstick Up / Down** | Cycle Inventory Hotbar Slots *(Scrolls UI when pointing at menus)* |
| **Right Stick Click (Hold 0.6s)** / **F12** | **Recenter / Calibrate VR View & Height** *(`Short Tap`: Crouch)* |
| **Right Trigger (`RT`)** | Primary Action *(Fire Weapon / Reel In / Draw Bow / Eat / UI Laser Click; Melee/Punch in `AUTOMATIC` mode)* |
| **Left Trigger (`LT`)** | Secondary Action *(Open Bail & Cast / ADS / Boat Reverse)* |
| **Swing Arms Physically** | **Slash Knife / Melee Weapon or Punch Creatures** *(Required in `MANUAL` VR mode; always active)* |
| **Off-Hand Grip (`LG`) at Chest/Hip** | **Grab Fresh Magazine / Shotgun Shell / Speedloader** from Ammo Pouch |
| **Off-Hand Grip (`LG`) on Gun** | Pull Mag Out / **Rack Slide, Bolt, Charging Handle, or Pump** / Two-Hand Barrel Stabilize |
| **Dominant Grip (`RG`)** | Grab Hovered World Item *(when empty-handed)* / Grab Boat Wheel or Throttle |
| **Left X (`Primary`)** | Pick Up / Interact • **Eject Magazine / Open Breech** *(in Manual Mode when not hovering a pickup)* |
| **Left Y (`Secondary`)** | `Tap`: Next Inventory Slot • `Hold`: Holster (Empty Hands) • `LG + Y`: Cycle Bait / Inspect |
| **Right A (`Primary`)** | Jump / UI Submit |
| **Right B (`Secondary`)** | **Close Findings / Pause UI** • `Tap`: Drop Item / Release Fish • `Hold + Swing`: Throw • Exit Boat |
| **Left Grip + Right A (`LG + A`)** | **Toggle Findings / Collection Journal** *(Zero SteamVR Menu button conflict!)* |
| **Left Grip + Right B (`LG + B`)** | **Toggle Pause Menu** *(Alternative to Wrist Menu `Pause` button)* |

---

## Versioning & Release Policy

Every update to **How To Fish VR Mod** is published as its own standalone GitHub Release with a pre-packaged `.zip`:

- **Small / Hotfix Updates (`+0.0.1`)**: Increment the patch digit (`v1.0.0` -> `v1.0.1` -> `v1.0.2`).
- **Major / Feature Updates (`+0.1.0`)**: Increment the minor digit and reset patch (`v1.0.x` -> `v1.1.0` -> `v1.2.0`).
- **Digit Cap at `.9.9`**: Each digit caps at `9`, rolling cleanly from `v1.9.9` into `v2.0.0` (via `scripts\bump_version.ps1` and `scripts\publish_release.ps1`).

---

## Building from Source

1. Install **.NET SDK 8+** and **Visual Studio 2022 C++ Build Tools** (x64).
2. Ensure **How to Fish** is installed via Steam (update `<ManagedDir>` in `src/HowToFishVR/HowToFishVR.csproj` if your Steam library path differs).
3. Run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\build_all.ps1
   ```
   This compiles `HowToFishVRNative.dll` (C++17 OpenXR/D3D11) and `HowToFishVR.dll` (C# BepInEx plugin) and creates `dist\HowToFish-VRMod-v1.0.0.zip`.
