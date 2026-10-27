using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.TextCore;

namespace HowToFishVR;

/// <summary>
/// Generates a high-DPI vector-anti-aliased TMP_SpriteAsset of VR Hand Controller button glyphs
/// and patches SpriteManager so all in-game prompts, HUD shortcuts, tutorials, and controller menus
/// display actual Left/Right VR controller icons instead of Xbox/XInput icons.
/// </summary>
public static class VRControllerIcons
{
    public static TMP_SpriteAsset SpriteAsset { get; private set; }
    public static Material OverlaySpriteMaterial { get; private set; }

    private static Texture2D _atlasTex;
    private static readonly HashSet<TMP_SpriteAsset> _registeredParents = new HashSet<TMP_SpriteAsset>();
    private static bool _initialized;

    private static readonly FieldInfo SpriteAssetVersionField = AccessTools.Field(typeof(TMP_SpriteAsset), "m_Version");
    private static readonly FieldInfo SpriteAssetFaceInfoField = AccessTools.Field(typeof(TMP_SpriteAsset), "m_FaceInfo");

    private static readonly MethodInfo BaitSlotRefreshMethod = AccessTools.Method(typeof(BaitSlot), "RefreshChangeBaitShortcut");
    private static readonly MethodInfo ItemUIRefreshMethod = AccessTools.Method(typeof(ItemUI), "RefreshSpecificItemInfo");
    private static readonly FieldInfo ItemUICurInfoField = AccessTools.Field(typeof(ItemUI), "_curInfo");
    private static readonly MethodInfo ThinkingUIRefreshMethod = AccessTools.Method(typeof(ThinkingUI), "SetupCloseInputText");
    private static readonly MethodInfo DeathUIRefreshMethod = AccessTools.Method(typeof(DeathUI), "RefreshText");
    private static readonly MethodInfo TutorialRefreshMethod = AccessTools.Method(typeof(TutorialBox), "RefreshText");

    private enum IconKind
    {
        FaceButton,
        Trigger,
        Grip,
        Stick,
        StickClick,
        StickUpDown,
        MenuButton
    }

    private readonly struct IconSpec
    {
        public readonly string Name;
        public readonly IconKind Kind;
        public readonly bool IsRightHand;
        public readonly string Label;
        public readonly Color Accent;

        public IconSpec(string name, IconKind kind, bool isRight, string label, Color accent)
        {
            Name = name;
            Kind = kind;
            IsRightHand = isRight;
            Label = label;
            Accent = accent;
        }
    }

    private static readonly Color CyanLeft = new Color(0.24f, 0.88f, 1.00f, 1f);
    private static readonly Color SkyLeft = new Color(0.42f, 0.78f, 1.00f, 1f);
    private static readonly Color GoldRight = new Color(1.00f, 0.76f, 0.24f, 1f);
    private static readonly Color CoralRight = new Color(1.00f, 0.48f, 0.34f, 1f);

    private static readonly IconSpec[] Specs =
    {
        new IconSpec("vr_a",            IconKind.FaceButton,  true,  "A",  GoldRight),
        new IconSpec("vr_b",            IconKind.FaceButton,  true,  "B",  CoralRight),
        new IconSpec("vr_x",            IconKind.FaceButton,  false, "X",  CyanLeft),
        new IconSpec("vr_y",            IconKind.FaceButton,  false, "Y",  SkyLeft),
        new IconSpec("vr_rt",           IconKind.Trigger,     true,  "RT", GoldRight),
        new IconSpec("vr_lt",           IconKind.Trigger,     false, "LT", CyanLeft),
        new IconSpec("vr_rgrip",        IconKind.Grip,        true,  "RG", GoldRight),
        new IconSpec("vr_lgrip",        IconKind.Grip,        false, "LG", CyanLeft),
        new IconSpec("vr_rstick",       IconKind.Stick,       true,  "R",  GoldRight),
        new IconSpec("vr_lstick",       IconKind.Stick,       false, "L",  CyanLeft),
        new IconSpec("vr_rstick_click", IconKind.StickClick,  true,  "R",  GoldRight),
        new IconSpec("vr_lstick_click", IconKind.StickClick,  false, "L",  CyanLeft),
        new IconSpec("vr_rstick_ud",    IconKind.StickUpDown, true,  "R",  GoldRight),
        new IconSpec("vr_lstick_ud",    IconKind.StickUpDown, false, "L",  CyanLeft),
        new IconSpec("vr_menu",         IconKind.MenuButton,  false, "=",  CyanLeft),
    };

    public static void EnsureInitialized()
    {
        if (_initialized && SpriteAsset != null)
        {
            TryRegisterWithDefaultTmpSettings();
            return;
        }

        try
        {
            const int cols = 4;
            const int rows = 4;
            const int cellSize = 128;
            const int atlasW = cols * cellSize;
            const int atlasH = rows * cellSize;

            _atlasTex = new Texture2D(atlasW, atlasH, TextureFormat.RGBA32, mipChain: false)
            {
                name = "HTFVR_ControllerIcons_Atlas",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };

            Color[] pixels = new Color[atlasW * atlasH];
            for (int i = 0; i < Specs.Length; i++)
            {
                int col = i % cols;
                int row = (rows - 1) - (i / cols);
                RenderIconCell(pixels, atlasW, col * cellSize, row * cellSize, cellSize, Specs[i]);
            }

            _atlasTex.SetPixels(pixels);
            _atlasTex.Apply(updateMipmaps: false, makeNoLongerReadable: true);

            Shader spriteShader = Shader.Find("TextMeshPro/Sprite");
            if (spriteShader == null && TMP_Settings.defaultSpriteAsset != null && TMP_Settings.defaultSpriteAsset.material != null)
            {
                spriteShader = TMP_Settings.defaultSpriteAsset.material.shader;
            }
            if (spriteShader == null)
            {
                spriteShader = Shader.Find("UI/Default") ?? Shader.Find("Sprites/Default");
            }

            OverlaySpriteMaterial = new Material(spriteShader)
            {
                name = "HTFVR_ControllerIcons_Mat",
                mainTexture = _atlasTex,
                renderQueue = 4105,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (OverlaySpriteMaterial.HasProperty("_MainTex"))
                OverlaySpriteMaterial.SetTexture("_MainTex", _atlasTex);
            if (OverlaySpriteMaterial.HasProperty("unity_GUIZTestMode"))
                OverlaySpriteMaterial.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);

            SpriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
            SpriteAsset.name = "HTFVR_ControllerIcons";
            SpriteAsset.hideFlags = HideFlags.HideAndDontSave;

            // Set m_Version = "1.1.0" before UpdateLookupTables so TMP does not wipe tables via legacy UpgradeSpriteAsset!
            SpriteAssetVersionField?.SetValue(SpriteAsset, "1.1.0");
            SpriteAsset.spriteSheet = _atlasTex;
            SpriteAsset.material = OverlaySpriteMaterial;

            FaceInfo face = new FaceInfo
            {
                familyName = "HTFVR_ControllerIcons",
                styleName = "Regular",
                pointSize = 96,
                scale = 1f,
                lineHeight = 112f,
                ascentLine = 94f,
                capLine = 84f,
                meanLine = 60f,
                baseline = 0f,
                descentLine = -18f
            };
            SpriteAssetFaceInfoField?.SetValue(SpriteAsset, face);

            SpriteAsset.spriteGlyphTable.Clear();
            SpriteAsset.spriteCharacterTable.Clear();

            const int pad = 6;
            const int glyphSize = cellSize - pad * 2; // 116x116

            for (int i = 0; i < Specs.Length; i++)
            {
                int col = i % cols;
                int row = (rows - 1) - (i / cols);
                int gx = col * cellSize + pad;
                int gy = row * cellSize + pad;

                GlyphRect rect = new GlyphRect(gx, gy, glyphSize, glyphSize);
                GlyphMetrics metrics = new GlyphMetrics(
                    width: glyphSize,
                    height: glyphSize,
                    bearingX: 2f,
                    bearingY: 94f,
                    advance: 120f);

                var glyph = new TMP_SpriteGlyph((uint)i, metrics, rect, 1.0f, 0);
                SpriteAsset.spriteGlyphTable.Add(glyph);

                var character = new TMP_SpriteCharacter(0xE100u + (uint)i, SpriteAsset, glyph)
                {
                    name = Specs[i].Name,
                    scale = 1.08f
                };
                SpriteAsset.spriteCharacterTable.Add(character);
            }

            SpriteAsset.UpdateLookupTables();
            _initialized = true;

            TryRegisterWithDefaultTmpSettings();
            Plugin.Log.LogInfo($"Generated custom VR controller TMP_SpriteAsset '{SpriteAsset.name}' with {Specs.Length} icons.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"Failed to initialize VRControllerIcons: {ex}");
        }
    }

    public static void TryRegisterWithDefaultTmpSettings()
    {
        if (SpriteAsset == null) return;
        try
        {
            TMP_SpriteAsset def = TMP_Settings.defaultSpriteAsset;
            if (def != null)
            {
                RegisterAsFallbackOn(def);
            }
        }
        catch
        {
            // Safe guard if TMP_Settings hasn't loaded yet
        }
    }

    public static void EnsureRegisteredOnText(TMP_Text tmp)
    {
        if (tmp == null) return;
        EnsureInitialized();
        if (SpriteAsset == null) return;

        if (tmp.spriteAsset != null && tmp.spriteAsset != SpriteAsset)
        {
            RegisterAsFallbackOn(tmp.spriteAsset);
        }
        else if (tmp.spriteAsset == null && TMP_Settings.defaultSpriteAsset == null)
        {
            tmp.spriteAsset = SpriteAsset;
        }
    }

    private static void RegisterAsFallbackOn(TMP_SpriteAsset parentAsset)
    {
        if (parentAsset == null || parentAsset == SpriteAsset) return;
        if (_registeredParents.Contains(parentAsset)) return;

        parentAsset.fallbackSpriteAssets ??= new List<TMP_SpriteAsset>();
        if (!parentAsset.fallbackSpriteAssets.Contains(SpriteAsset))
        {
            parentAsset.fallbackSpriteAssets.Insert(0, SpriteAsset);
        }
        _registeredParents.Add(parentAsset);
    }

    public static void RefreshAllPromptUIs()
    {
        EnsureInitialized();
        try
        {
            foreach (var baitSlot in UnityEngine.Object.FindObjectsByType<BaitSlot>(FindObjectsInactive.Include))
            {
                BaitSlotRefreshMethod?.Invoke(baitSlot, null);
            }

            foreach (var itemUi in UnityEngine.Object.FindObjectsByType<ItemUI>(FindObjectsInactive.Include))
            {
                if (ItemUICurInfoField?.GetValue(itemUi) != null)
                {
                    ItemUIRefreshMethod?.Invoke(itemUi, null);
                }
            }

            foreach (var thinkingUi in UnityEngine.Object.FindObjectsByType<ThinkingUI>(FindObjectsInactive.Include))
            {
                ThinkingUIRefreshMethod?.Invoke(thinkingUi, null);
            }

            foreach (var deathUi in UnityEngine.Object.FindObjectsByType<DeathUI>(FindObjectsInactive.Include))
            {
                DeathUIRefreshMethod?.Invoke(deathUi, null);
            }

            foreach (var tut in UnityEngine.Object.FindObjectsByType<TutorialBox>(FindObjectsInactive.Include))
            {
                TutorialRefreshMethod?.Invoke(tut, null);
            }

            foreach (var rebind in UnityEngine.Object.FindObjectsByType<UnityEngine.InputSystem.Samples.RebindUI.RebindActionUI>(FindObjectsInactive.Include))
            {
                rebind.UpdateBindingDisplay();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogDebug($"RefreshAllPromptUIs info: {ex.Message}");
        }
    }

    public static string Tag(string iconName) => $"<sprite name=\"{iconName}\">";

    public static bool TryGetVRPromptForAction(InputAction action, int bindingIndex, out string spriteTag)
    {
        spriteTag = null;
        if (action == null) return false;

        bool rightDom = VRConfig.DominantHand.Value == VRDominantHand.Right;
        string domTrig = rightDom ? Tag("vr_rt") : Tag("vr_lt");
        string offTrig = rightDom ? Tag("vr_lt") : Tag("vr_rt");
        string domGrip = rightDom ? Tag("vr_rgrip") : Tag("vr_lgrip");
        string offGrip = rightDom ? Tag("vr_lgrip") : Tag("vr_rgrip");
        string domPri  = rightDom ? Tag("vr_a") : Tag("vr_x");
        string domSec  = rightDom ? Tag("vr_b") : Tag("vr_y");
        string offPri  = rightDom ? Tag("vr_x") : Tag("vr_a");
        string offSec  = rightDom ? Tag("vr_y") : Tag("vr_b");

        switch (action.name)
        {
            case "PlayerPickUp":
                spriteTag = offPri;
                return true;
            case "PlayerDrop":
                spriteTag = domSec;
                return true;
            case "PlayerLeftClick":
                spriteTag = domTrig;
                return true;
            case "PlayerRightClick":
                spriteTag = offTrig;
                return true;
            case "WeaponReload":
                spriteTag = VRConfig.AutoReloadWeapons.Value ? offPri : $"{offPri}+{offGrip}";
                return true;
            case "WeaponInspect":
            case "ChangeBait":
            case "PlayerChangeSkin":
                spriteTag = $"{offGrip}+{offSec}";
                return true;
            case "InventoryScroll":
                spriteTag = $"{offSec}/{Tag("vr_rstick_ud")}";
                return true;
            case "InventoryNone":
                spriteTag = offSec;
                return true;
            case "PlayerJump":
                spriteTag = domPri;
                return true;
            case "PlayerMove":
                spriteTag = Tag("vr_lstick");
                return true;
            case "PlayerLook":
                spriteTag = Tag("vr_rstick");
                return true;
            case "PlayerSprint":
                spriteTag = Tag("vr_lstick_click");
                return true;
            case "PlayerCrouch":
                spriteTag = Tag("vr_rstick_click");
                return true;
            case "PushToTalk":
                spriteTag = $"{Tag("vr_lgrip")}+{Tag("vr_lstick_click")}";
                return true;
            case "PlayerTab":
                spriteTag = PlayerThinking.IsThinking ? Tag("vr_b") : $"{Tag("vr_lgrip")}+{Tag("vr_a")}";
                return true;
            case "Pause":
                spriteTag = PlayerThinking.IsThinking ? Tag("vr_b") : $"{Tag("vr_lgrip")}+{Tag("vr_b")}";
                return true;
            case "Skip":
                spriteTag = Tag("vr_a");
                return true;
        }

        // Fallback for any custom/specific gamepad binding index
        if (bindingIndex >= 0 && bindingIndex < action.bindings.Count)
        {
            if (TryMapGamepadBindingPath(action.bindings[bindingIndex].effectivePath, out spriteTag))
                return true;
        }

        foreach (InputBinding b in action.bindings)
        {
            if (TryMapGamepadBindingPath(b.effectivePath, out spriteTag))
                return true;
        }

        return false;
    }

    private static bool TryMapGamepadBindingPath(string path, out string tag)
    {
        tag = path switch
        {
            "<Gamepad>/buttonSouth"     => Tag("vr_a"),
            "<Gamepad>/buttonEast"      => Tag("vr_b"),
            "<Gamepad>/buttonWest"      => Tag("vr_x"),
            "<Gamepad>/buttonNorth"     => Tag("vr_y"),
            "<Gamepad>/rightTrigger"    => Tag("vr_rt"),
            "<Gamepad>/leftTrigger"     => Tag("vr_lt"),
            "<Gamepad>/rightShoulder"   => Tag("vr_rgrip"),
            "<Gamepad>/leftShoulder"    => Tag("vr_lgrip"),
            "<Gamepad>/rightStick"      => Tag("vr_rstick"),
            "<Gamepad>/leftStick"       => Tag("vr_lstick"),
            "<Gamepad>/rightStickPress" => Tag("vr_rstick_click"),
            "<Gamepad>/leftStickPress"  => Tag("vr_lstick_click"),
            "<Gamepad>/start"           => Tag("vr_menu"),
            "<Gamepad>/select"          => Tag("vr_menu"),
            "<Gamepad>/dpad/up"         => Tag("vr_rstick_ud"),
            "<Gamepad>/dpad/down"       => Tag("vr_b"),
            "<Gamepad>/dpad/left"       => Tag("vr_x"),
            "<Gamepad>/dpad/right"      => Tag("vr_y"),
            _ => null
        };
        return tag != null;
    }

    // =========================================================================
    // Vector-SDF Procedural VR Controller Icon Renderer (128x128 per icon)
    // =========================================================================
    private static void RenderIconCell(Color[] atlas, int atlasW, int ox, int oy, int size, in IconSpec spec)
    {
        Vector2 c = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
        Color darkBg = new Color(0.07f, 0.10f, 0.15f, 0.96f);
        Color innerShade = new Color(0.12f, 0.17f, 0.24f, 0.98f);
        Color glyphWhite = new Color(0.98f, 0.99f, 1.00f, 1.00f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Vector2 p = new Vector2(x, y);
                Color outCol = Color.clear;

                switch (spec.Kind)
                {
                    case IconKind.FaceButton:
                    case IconKind.MenuButton:
                    {
                        Vector2 mainC = c + new Vector2(spec.IsRightHand ? -4f : 4f, -3f);
                        float r = (p - mainC).magnitude;
                        float bodyA = SmoothStepAA(44.5f - r);
                        float ringA = SmoothStepAA(44.5f - r) * SmoothStepAA(r - 38.0f);
                        float innerA = SmoothStepAA(36.0f - r);

                        outCol = BlendOver(outCol, WithAlpha(darkBg, bodyA));
                        outCol = BlendOver(outCol, WithAlpha(innerShade, innerA * 0.6f));
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, ringA));

                        if (spec.Kind == IconKind.MenuButton)
                        {
                            float bars = Mathf.Max(
                                StrokeSeg(p, mainC + new Vector2(-15f, 11f), mainC + new Vector2(15f, 11f), 3.4f),
                                Mathf.Max(
                                    StrokeSeg(p, mainC + new Vector2(-15f, 0f), mainC + new Vector2(15f, 0f), 3.4f),
                                    StrokeSeg(p, mainC + new Vector2(-15f, -11f), mainC + new Vector2(15f, -11f), 3.4f)));
                            outCol = BlendOver(outCol, WithAlpha(glyphWhite, bars));
                        }
                        else
                        {
                            float ch = SampleGlyphChar(spec.Label[0], p, mainC, 21f, 4.4f);
                            outCol = BlendOver(outCol, WithAlpha(glyphWhite, ch));
                        }

                        // Corner Left/Right controller hand badge
                        Vector2 badgeC = c + new Vector2(spec.IsRightHand ? 35f : -35f, 34f);
                        outCol = DrawHandBadge(outCol, p, badgeC, spec.IsRightHand, spec.Accent);
                        break;
                    }

                    case IconKind.Trigger:
                    {
                        // Curved ergonomic VR index trigger silhouette
                        float bend = Mathf.Clamp((p.y - c.y) / 44f, -1f, 1f);
                        float curveX = (spec.IsRightHand ? -1f : 1f) * (1f - bend * bend) * 5.5f;
                        float halfW = Mathf.Lerp(26f, 36f, (bend + 1f) * 0.5f);
                        float sd = SignedDistRoundBox(p - (c + new Vector2(curveX, -2f)), new Vector2(halfW, 40f), 16f);

                        float bodyA = SmoothStepAA(-sd);
                        float ringA = SmoothStepAA(-sd) * SmoothStepAA(sd + 5.8f);
                        // Top cap highlight bar indicating index trigger pull surface
                        float topCap = SmoothStepAA(-sd) * SmoothStepAA((p.y - (c.y + 24f)));

                        outCol = BlendOver(outCol, WithAlpha(darkBg, bodyA));
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, topCap * 0.28f));
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, ringA));

                        float ch0 = SampleGlyphChar(spec.Label[0], p, c + new Vector2(-13f, -3f), 15.5f, 3.6f);
                        float ch1 = SampleGlyphChar(spec.Label[1], p, c + new Vector2(13f, -3f), 15.5f, 3.6f);
                        outCol = BlendOver(outCol, WithAlpha(glyphWhite, Mathf.Max(ch0, ch1)));
                        break;
                    }

                    case IconKind.Grip:
                    {
                        // Side squeeze grip button silhouette with tactile grip grooves
                        float sd = SignedDistRoundBox(p - c, new Vector2(36f, 44f), 18f);
                        float bodyA = SmoothStepAA(-sd);
                        float ringA = SmoothStepAA(-sd) * SmoothStepAA(sd + 5.6f);

                        outCol = BlendOver(outCol, WithAlpha(darkBg, bodyA));
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, ringA));

                        // Subtle side squeeze chevrons
                        float sideX = spec.IsRightHand ? -26f : 26f;
                        float groove = Mathf.Max(
                            StrokeSeg(p, c + new Vector2(sideX, 14f), c + new Vector2(sideX, -14f), 2.2f),
                            0f);
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, groove * 0.65f));

                        float ch0 = SampleGlyphChar(spec.Label[0], p, c + new Vector2(-13f, 0f), 15.0f, 3.5f);
                        float ch1 = SampleGlyphChar(spec.Label[1], p, c + new Vector2(13f, 0f), 15.0f, 3.5f);
                        outCol = BlendOver(outCol, WithAlpha(glyphWhite, Mathf.Max(ch0, ch1)));
                        break;
                    }

                    case IconKind.Stick:
                    case IconKind.StickClick:
                    case IconKind.StickUpDown:
                    {
                        Vector2 stickC = spec.Kind == IconKind.StickClick ? (c + new Vector2(0f, -6f)) : c;
                        float rOuter = (p - stickC).magnitude;
                        float outerRing = SmoothStepAA(46f - rOuter) * SmoothStepAA(rOuter - 41f);
                        float capBody = SmoothStepAA(36.5f - rOuter);
                        float capRim = SmoothStepAA(36.5f - rOuter) * SmoothStepAA(rOuter - 31.5f);

                        outCol = BlendOver(outCol, WithAlpha(darkBg, SmoothStepAA(46f - rOuter) * 0.88f));
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, outerRing * 0.55f));
                        outCol = BlendOver(outCol, WithAlpha(innerShade, capBody));
                        outCol = BlendOver(outCol, WithAlpha(spec.Accent, capRim));

                        if (spec.Kind == IconKind.StickClick)
                        {
                            // Crisp downward press arrow at top of thumbstick
                            Vector2 top = c + new Vector2(0f, 49f);
                            Vector2 tip = c + new Vector2(0f, 31f);
                            float arr = Mathf.Max(
                                StrokeSeg(p, top, tip, 3.2f),
                                Mathf.Max(
                                    StrokeSeg(p, tip, tip + new Vector2(-9f, 9f), 3.0f),
                                    StrokeSeg(p, tip, tip + new Vector2(9f, 9f), 3.0f)));
                            outCol = BlendOver(outCol, WithAlpha(glyphWhite, arr));
                        }
                        else if (spec.Kind == IconKind.StickUpDown)
                        {
                            // Up & Down chevrons
                            Vector2 upTip = c + new Vector2(0f, 51f);
                            Vector2 dnTip = c + new Vector2(0f, -51f);
                            float chev = Mathf.Max(
                                Mathf.Max(StrokeSeg(p, upTip, upTip + new Vector2(-10f, -9f), 3.0f),
                                          StrokeSeg(p, upTip, upTip + new Vector2(10f, -9f), 3.0f)),
                                Mathf.Max(StrokeSeg(p, dnTip, dnTip + new Vector2(-10f, 9f), 3.0f),
                                          StrokeSeg(p, dnTip, dnTip + new Vector2(10f, 9f), 3.0f)));
                            outCol = BlendOver(outCol, WithAlpha(glyphWhite, chev));
                        }

                        float ch = SampleGlyphChar(spec.Label[0], p, stickC, 15.5f, 3.8f);
                        outCol = BlendOver(outCol, WithAlpha(glyphWhite, ch));
                        break;
                    }
                }

                atlas[(oy + y) * atlasW + (ox + x)] = outCol;
            }
        }
    }

    private static Color DrawHandBadge(Color dst, Vector2 p, Vector2 badgeC, bool isRight, Color accent)
    {
        float r = (p - badgeC).magnitude;
        float bgA = SmoothStepAA(16.5f - r);
        if (bgA <= 0.001f) return dst;

        Color badgeBg = Color.Lerp(new Color(0.04f, 0.06f, 0.10f, 1f), accent, 0.88f);
        dst = BlendOver(dst, WithAlpha(badgeBg, bgA));

        float ch = SampleGlyphChar(isRight ? 'R' : 'L', p, badgeC, 8.8f, 2.35f);
        dst = BlendOver(dst, WithAlpha(new Color(0.04f, 0.06f, 0.10f, 1f), ch));
        return dst;
    }

    private static float SampleGlyphChar(char c, Vector2 p, Vector2 center, float halfH, float radius)
    {
        float w = halfH * 0.72f;
        float h = halfH;
        Vector2 q = p - center;
        float a = 0f;

        switch (c)
        {
            case 'A':
                a = Mathf.Max(
                    StrokeSeg(q, new Vector2(-w, -h), new Vector2(0f, h), radius),
                    Mathf.Max(
                        StrokeSeg(q, new Vector2(w, -h), new Vector2(0f, h), radius),
                        StrokeSeg(q, new Vector2(-w * 0.58f, -h * 0.18f), new Vector2(w * 0.58f, -h * 0.18f), radius * 0.92f)));
                break;

            case 'B':
            {
                float spine = StrokeSeg(q, new Vector2(-w * 0.78f, -h), new Vector2(-w * 0.78f, h), radius);
                float topB = StrokeSeg(q, new Vector2(-w * 0.78f, h), new Vector2(w * 0.18f, h), radius);
                float midB = StrokeSeg(q, new Vector2(-w * 0.78f, 0.04f * h), new Vector2(w * 0.25f, 0.04f * h), radius * 0.92f);
                float botB = StrokeSeg(q, new Vector2(-w * 0.78f, -h), new Vector2(w * 0.25f, -h), radius);
                float upperLoop = RingArcRight(q - new Vector2(w * 0.15f, h * 0.52f), h * 0.48f, radius);
                float lowerLoop = RingArcRight(q - new Vector2(w * 0.20f, -h * 0.48f), h * 0.52f, radius);
                a = Mathf.Max(Mathf.Max(spine, midB), Mathf.Max(Mathf.Max(topB, botB), Mathf.Max(upperLoop, lowerLoop)));
                break;
            }

            case 'X':
                a = Mathf.Max(
                    StrokeSeg(q, new Vector2(-w * 0.88f, -h), new Vector2(w * 0.88f, h), radius),
                    StrokeSeg(q, new Vector2(-w * 0.88f, h), new Vector2(w * 0.88f, -h), radius));
                break;

            case 'Y':
                a = Mathf.Max(
                    StrokeSeg(q, new Vector2(0f, -h), new Vector2(0f, -h * 0.05f), radius),
                    Mathf.Max(
                        StrokeSeg(q, new Vector2(0f, -h * 0.05f), new Vector2(-w * 0.92f, h), radius),
                        StrokeSeg(q, new Vector2(0f, -h * 0.05f), new Vector2(w * 0.92f, h), radius)));
                break;

            case 'L':
                a = Mathf.Max(
                    StrokeSeg(q, new Vector2(-w * 0.72f, h), new Vector2(-w * 0.72f, -h), radius),
                    StrokeSeg(q, new Vector2(-w * 0.72f, -h), new Vector2(w * 0.78f, -h), radius));
                break;

            case 'R':
            {
                float spine = StrokeSeg(q, new Vector2(-w * 0.78f, -h), new Vector2(-w * 0.78f, h), radius);
                float topR = StrokeSeg(q, new Vector2(-w * 0.78f, h), new Vector2(w * 0.18f, h), radius);
                float midR = StrokeSeg(q, new Vector2(-w * 0.78f, 0.04f * h), new Vector2(w * 0.18f, 0.04f * h), radius * 0.92f);
                float loop = RingArcRight(q - new Vector2(w * 0.16f, h * 0.52f), h * 0.48f, radius);
                float leg = StrokeSeg(q, new Vector2(w * 0.05f, 0.02f * h), new Vector2(w * 0.82f, -h), radius);
                a = Mathf.Max(Mathf.Max(spine, leg), Mathf.Max(Mathf.Max(topR, midR), loop));
                break;
            }

            case 'T':
                a = Mathf.Max(
                    StrokeSeg(q, new Vector2(-w * 0.92f, h), new Vector2(w * 0.92f, h), radius),
                    StrokeSeg(q, new Vector2(0f, -h), new Vector2(0f, h), radius));
                break;

            case 'G':
            {
                // Clean C-arc + inward horizontal bar
                float rDist = Mathf.Abs(q.magnitude - h * 0.92f);
                float ang = Mathf.Atan2(q.y, q.x) * Mathf.Rad2Deg;
                bool inSpan = !(ang > 8f && ang < 50f);
                float arc = inSpan ? SmoothStepAA(radius - rDist) : 0f;
                float bar = StrokeSeg(q, new Vector2(0.05f * w, -0.05f * h), new Vector2(h * 0.90f, -0.05f * h), radius * 0.92f);
                float drop = StrokeSeg(q, new Vector2(h * 0.90f, -0.05f * h), new Vector2(h * 0.90f, -0.58f * h), radius * 0.92f);
                a = Mathf.Max(arc, Mathf.Max(bar, drop));
                break;
            }
        }

        return a;
    }

    private static float RingArcRight(Vector2 d, float arcR, float strokeR)
    {
        if (d.x < -0.5f) return 0f;
        float dist = Mathf.Abs(d.magnitude - arcR);
        return SmoothStepAA(strokeR - dist);
    }

    private static float StrokeSeg(Vector2 p, Vector2 a, Vector2 b, float radius)
    {
        Vector2 pa = p - a;
        Vector2 ba = b - a;
        float denom = Vector2.Dot(ba, ba);
        float t = denom > 0.0001f ? Mathf.Clamp01(Vector2.Dot(pa, ba) / denom) : 0f;
        float dist = (pa - ba * t).magnitude;
        return SmoothStepAA(radius - dist);
    }

    private static float SignedDistRoundBox(Vector2 p, Vector2 halfExtents, float cornerRadius)
    {
        Vector2 q = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y)) - (halfExtents - new Vector2(cornerRadius, cornerRadius));
        return Vector2.Max(q, Vector2.zero).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0f) - cornerRadius;
    }

    private static float SmoothStepAA(float edgeDelta)
    {
        return Mathf.Clamp01(edgeDelta + 0.65f);
    }

    private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, c.a * Mathf.Clamp01(a));

    private static Color BlendOver(Color dst, Color src)
    {
        float sa = src.a;
        if (sa <= 0.001f) return dst;
        float outA = sa + dst.a * (1f - sa);
        if (outA <= 0.001f) return Color.clear;
        Vector3 rgb = (new Vector3(src.r, src.g, src.b) * sa + new Vector3(dst.r, dst.g, dst.b) * dst.a * (1f - sa)) / outA;
        return new Color(rgb.x, rgb.y, rgb.z, outA);
    }
}

// =============================================================================
// Harmony Patch on SpriteManager so VR Hand Controller Icons Replace XInput Icons
// =============================================================================
[HarmonyPatch(typeof(SpriteManager), nameof(SpriteManager.InputToSpriteName))]
public static class SpriteManagerVRControllerIconsPatch
{
    public static bool Prefix(InputAction input, InputSchemeSelection inputScheme, int bindingIndex, ref string __result)
    {
        if (!VRConfig.EnableVR.Value || input == null)
        {
            return true;
        }

        // Preserve Keyboard/Mouse column in the Controls settings table while replacing Controller/Current icons with VR icons!
        if (inputScheme == InputSchemeSelection.ForceKeyboard)
        {
            return true;
        }

        VRControllerIcons.EnsureInitialized();
        if (VRControllerIcons.SpriteAsset != null &&
            VRControllerIcons.TryGetVRPromptForAction(input, bindingIndex, out string vrSpriteTag))
        {
            __result = vrSpriteTag;
            return false;
        }

        return true;
    }
}

[HarmonyPatch(typeof(ThinkingUI), "ToggleThinking")]
public static class ThinkingUITogglePromptRefreshPatch
{
    private static readonly MethodInfo SetupCloseMethod = AccessTools.Method(typeof(ThinkingUI), "SetupCloseInputText");

    public static void Postfix(ThinkingUI __instance)
    {
        if (!VRConfig.EnableVR.Value || __instance == null) return;
        SetupCloseMethod?.Invoke(__instance, null);
    }
}
