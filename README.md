# How To Fish — VR Mod (`v1.0.0`)

> **Disclaimer:** This mod has been created by **Google Gemini 4 Argon** via **Antigravity CLI**.
>
> **A note from me:** *"I am aware that using AI won't make me a developer or coder, but I am just a warehouse worker doing a 10pm-6am night shift job for a slightly-above minimum wage pay. Hating on me and/or the tools used will not make you a better person either. AI is unfortunately, as much as it also disappoints me to say this, here to stay and it might not go away for a long time. Don't waste your time leaving a hate comment, ignore this repo entirely or just download the mod and play in VR and enjoy it. For any issues, open up a ticket and I'll try to get the issue sorted as soon as possible."*

---

## About the Mod

This mod brings full 6DOF Virtual Reality support to **How to Fish** — playable from start to finish in singleplayer and online multiplayer. You can physically cast your fishing rod, crank the reel by hand, drive the boat by grabbing the wheel and throttle, reload your guns by hand, swing knives or throw punches with your real arms, and play right alongside unmodded flatscreen friends or other VR players.

**Current Version:** `v1.0.0` *(First Public Release Candidate)*

*(Note: All features, UI panels, and interactions in this mod are written from scratch in code on top of How to Fish itself — any other VR games mentioned below are strictly design inspirations for how certain mechanics or HUD layouts feel, with zero assets taken from any other game.)*

---

## How to Install

Every release `.zip` on the [**Releases**](../../releases) page lets you install the mod whichever way you feel most comfortable with: either with a one-click batch script, or completely manually by dragging and dropping three items yourself.

### Option A: One-Click Install (`Install-HowToFishVR.bat`)

If you just want to get into the game quickly without hunting down folders:

1. Download `HowToFish-VRMod-vX.Y.Z.zip` from [**Releases**](../../releases).
2. **Right-click** the `.zip` file and choose **Extract All...** so it's unpacked into a normal folder.
3. Open that folder and double-click **`Install-HowToFishVR.bat`**.
   - It automatically checks your Steam library folders to find *How to Fish* (or pops open a normal Windows folder picker if your game is stored somewhere custom) and copies the mod files in for you.
4. Start up **SteamVR**, **Quest Link / Air Link**, or **Virtual Desktop**, and launch **How to Fish** from Steam!

---

### Option B: Manual Step-by-Step Install (No Scripts Needed)

If you're skeptical about running `.bat` scripts from the internet — which is totally fair given what people run into online — you don't have to touch the installer script at all. Here is how to install it yourself in about 20 seconds:

1. Download `HowToFish-VRMod-vX.Y.Z.zip` from [**Releases**](../../releases) and open or extract it.
2. In **Steam**, right-click **How to Fish** in your library and click **Manage -> Browse local files**.
3. Open the inner **`How to Fish`** folder — the one that actually has **`How to Fish.exe`** inside it (for example: `...\Steam\steamapps\common\How to Fish\How to Fish\`).
4. From the `.zip`, drag and drop **only these 3 things** into that folder right next to `How to Fish.exe`:
   - **`BepInEx`** *(folder)*
   - **`doorstop_config.ini`** *(file)*
   - **`winhttp.dll`** *(file)*
   *(You can completely ignore or delete `Install-HowToFishVR.bat` and `Uninstall-HowToFishVR.bat`.)*
5. When you're done, your game folder should look like this:
   ```text
   How to Fish/
   ├── BepInEx/
   │   ├── core/                        (BepInEx 5.4.23.6 x64 mod loader)
   │   └── plugins/
   │       └── HowToFishVR/
   │           ├── HowToFishVR.dll       (The VR mod plugin)
   │           ├── HowToFishVRNative.dll (OpenXR + DirectX 11 VR bridge)
   │           └── openxr_loader.dll     (Official Khronos OpenXR loader)
   ├── How to Fish_Data/
   ├── doorstop_config.ini               (Tells BepInEx to load when the game starts)
   ├── winhttp.dll                       (Standard BepInEx loader file)
   └── How to Fish.exe
   ```
6. Start your VR headset runtime (SteamVR, Quest Link, Virtual Desktop, etc.) and launch **How to Fish** through Steam!

> **Want to verify what the script does?** You can right-click `Install-HowToFishVR.bat` and click **Edit in Notepad**. It's plain, readable text — it doesn't connect to the internet, doesn't change your Windows registry, and never touches your save files. All it does is copy `BepInEx\`, `doorstop_config.ini`, and `winhttp.dll` into your game folder.

### Playing in Flatscreen Mode or Uninstalling
- **Play on flatscreen temporarily without uninstalling:** Open `How to Fish\BepInEx\config\com.howtofish.vrmod.cfg` in Notepad and change `EnableVR = true` to `EnableVR = false`.
- **Uninstall manually:** Delete `winhttp.dll`, `doorstop_config.ini`, and the `BepInEx` folder from next to `How to Fish.exe`. Your save files are stored separately and won't be affected.
- **Uninstall in one click:** Run `Uninstall-HowToFishVR.bat`.

---

## What's in the Mod

- **Full OpenXR 6DOF Headset & Controller Support**:
  - Works with SteamVR, Meta Quest (Link, Air Link, and Virtual Desktop / VDXR), Valve Index, HTC Vive, PICO, and Windows Mixed Reality headsets.
  - Proper 3D stereo rendering, working red-dot sights, and a zoomed picture-in-picture lens on sniper scopes.
- **Calm Main Menu & Left-Wrist Menu (In the Menu + In-Game)**:
  - Slows down the boat and wave motion on the background Main Menu screen so you don't get motion sick before loading into a save, with a floating 3D title, mod version stamp, and a reset-view button if you get turned around.
  - **Wrist Menu**: Turn your **left wrist** toward your face and look at it (both on the Main Menu and while playing inside a save) to fade in a floating wrist menu with quick buttons for **Pause / Options**, **Findings / Journal**, **Manual vs. Automatic Combat/Reload**, **Drop Item**, **Holster**, **Snap/Smooth Turn**, and **Recenter VR**.
- **Physical Fishing & Boat Driving**:
  - Hold Left Trigger to open the bail, swing your arm forward to cast, and let go to send the line flying. You can physically crank the fishing reel with your off-hand (with little haptic clicks as you spin it) or hold Right Trigger to reel in, and pull the rod upward to help fight fish.
  - Grab the boat's steering wheel and throttle lever directly with your VR hands to drive around, or use the thumbstick and triggers if you prefer sitting back.
- **Manual Weapon Reloading & Physical Melee (with an Automatic Toggle)**:
  - **Manual VR Mode (On by default — inspired by VR shooters like *Pavlov VR* and *H3VR*)**:
    - **Mag-fed Pistols, SMGs & Rifles**: Eject the mag (`Left X` or grab and pull it out with your off-hand — keeping `+1` round in the chamber if you didn't shoot it dry), grab a fresh mag from your chest/waist pouch with Off-Hand Grip, slide it into the magwell, and physically grab and rack the slide or charging handle to chamber a round.
    - **Bolt-Action Snipers**: Swap the magazine and grab/cycle the bolt handle by hand.
    - **Pump / Tube Shotguns**: Grab shells one at a time from your pouch, feed them into the underside loading port, and pump the forend back and forward.
    - **Break-Action Guns & Revolvers**: Pop the action/cylinder open (`Left X` or off-hand pull), load a shell or speedloader from your pouch, and flick your wrist upward (or push the barrel closed) to snap it shut.
  - **Real Arm-Swing Melee & Punching**: In **Manual** mode, knives/melee weapons and bare-handed punches require physically swinging your arms in real life to hit fish and creatures.
  - **Prefer simpler controls?** Flip **`Combat & Reload: AUTOMATIC`** on your Left Wrist Menu anytime to go back to button/auto reloads and trigger-button melee swings just like desktop mode.
- **Desktop Spectator HUD for Streaming & Recording**:
  - Inspired by how *Half-Life: Alyx* presents clean 2D info for people watching on a monitor — with a custom twist: the desktop window shows a crisp static 2D overlay (health, hunger/fullness, poison/fire warnings, hotbar & bait icons, ammo counter, money, and boss health) for Discord screenshares or OBS recordings without cluttering your VR headset lenses.
  - **Gaze Fade Twist**: Whenever you look directly at your in-game 3D UI (like your Wrist Menu, pause/shop screens, or floating HUD), the desktop overlay smoothly **fades out** so viewers aren't seeing duplicate UI, and **fades right back in** once you look back out at the world.
- **Natural Hands, VR Controller Prompts & Full Multiplayer**:
  - Properly aligned playermodel hands and finger curling, plus custom VR controller button icons instead of Xbox controller prompts.
  - **100% Multiplayer Compatible**: Join or host regular unmodded lobbies with flatscreen players with no issues. If other players in the lobby also have the VR mod installed, you'll automatically see each other's full two-handed 6DOF arm and finger movements too.

---

## VR Controls

| VR Control | What It Does |
| :--- | :--- |
| **Turn Left Wrist & Look at It** | **Open Wrist Menu** *(works on the Main Menu and inside a save)* |
| **Left Thumbstick** | Walk / Move (On Foot) • Steer Boat |
| **Left Stick Click** | Sprint *(`Left Grip + Left Stick Click`: Push-To-Talk)* |
| **Right Thumbstick Left / Right** | Snap Turn (`45°`) or Smooth Turn *(flips pages when Findings/Journal is open)* |
| **Right Thumbstick Up / Down** | Cycle Hotbar Slots *(scrolls lists when pointing at a menu)* |
| **Right Stick Click (Hold 0.6s)** / **F12** | **Recenter VR View & Height** *(`Quick Tap`: Crouch)* |
| **Right Trigger (`RT`)** | Shoot / Reel In / Draw Bow / Eat / Click UI *(also swings melee in `AUTOMATIC` mode)* |
| **Left Trigger (`LT`)** | Open Bail & Cast Rod / Aim Down Sights / Boat Reverse |
| **Swing Your Arms** | **Slash Knife / Melee or Punch** *(required in `MANUAL` mode; always works)* |
| **Off-Hand Grip (`LG`) at Chest/Hip** | **Grab Ammo (Magazine / Shell / Speedloader)** from your pouch |
| **Off-Hand Grip (`LG`) on Gun** | Pull Mag Out • **Rack Slide, Bolt, Charging Handle, or Pump** • Two-Hand Barrel Grip |
| **Main-Hand Grip (`RG`)** | Grab Hovered Item *(when empty-handed)* • Grab Boat Wheel or Throttle |
| **Left X** | Pick Up / Interact • **Eject Mag / Open Breech** *(in Manual Mode when not aiming at a pickup)* |
| **Left Y** | `Tap`: Next Hotbar Slot • `Hold`: Holster (Empty Hands) • `LG + Y`: Change Bait / Inspect |
| **Right A** | Jump / Confirm UI |
| **Right B** | **Close Findings or Pause Menu** • `Tap`: Drop Item / Release Fish • `Hold + Swing`: Throw • Exit Boat |
| **Left Grip + Right A (`LG + A`)** | **Open / Close Findings (Collection Journal)** *(avoids conflicting with SteamVR's Menu button)* |
| **Left Grip + Right B (`LG + B`)** | **Open / Close Pause Menu** |

---

## Version Numbering & Updates

Each update gets its own separate release on GitHub:
- **Small / Hotfix updates** go up by `+0.0.1` (`v1.0.0` -> `v1.0.1`).
- **Bigger feature updates** go up by `+0.1.0` (`v1.0.x` -> `v1.1.0`).
- Digits cap out at `9`, so after `v1.9.9` it rolls over to `v2.0.0`.

If you run into a bug or something feels off in VR, please [**open an Issue**](../../issues) with a quick description of what happened (and what headset/controllers you're on) and I'll try to get it sorted out as soon as I can!

---

## Building from Source (Optional)

If you want to compile the mod yourself instead of downloading the pre-built release `.zip`:
1. Install the **.NET 8+ SDK** and **Visual Studio 2022 C++ Build Tools** (x64).
2. Make sure *How to Fish* is installed on Steam (if your game isn't on `D:\SteamLibrary`, update `<ManagedDir>` inside `src/HowToFishVR/HowToFishVR.csproj` to point to your `How to Fish_Data\Managed` folder).
3. Run:
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\build_all.ps1
   ```
   That builds both the C++ OpenXR bridge and the C# BepInEx plugin and packages everything into `dist\HowToFish-VRMod-v1.0.0.zip`.
