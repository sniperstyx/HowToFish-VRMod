using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HowToFishVR;

/// <summary>
/// Renders a crisp, Half-Life: Alyx-inspired static 2D HUD exclusively onto the desktop
/// spectator/mirror window (for streaming, Discord screensharing, and recording) without
/// ever polluting the stereoscopic VR headset eye textures.
///
/// Twist: Smoothly fades out when the VR player looks directly at any in-game 3D UI
/// (Wrist Menu, Wrist HUD, Modal Menus, or floating WorldSpace HUD strips) and smoothly
/// fades back in as soon as the player looks back out into the world.
/// </summary>
public static class VRDesktopSpectatorHUD
{
    private static float _hudAlpha = 1f;
    private static float _lastUpdateFrameTime = -1f;

    // Reflection caches
    private static readonly FieldInfo InvLocalCurSlotField = typeof(PlayerInventory).GetField("_localCurSlot", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo InvStartingSlotsField = typeof(PlayerInventory).GetField("_startingSlots", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo InvItemSlotsField = typeof(PlayerInventory).GetField("_itemSlots", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo InvBaitSlotField = typeof(PlayerInventory).GetField("_baitSlot", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo SlotItemImageField = typeof(InventorySlot).GetField("_itemImage", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo ItemNameLocalizedField = typeof(Item).GetField("_nameLocalized", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly PropertyInfo BaitNameLocalizedProp = typeof(BaitInfo).GetProperty("NameLocalized", BindingFlags.Instance | BindingFlags.Public);
    private static readonly FieldInfo BaitInternalNameField = typeof(BaitInfo).GetField("_name", BindingFlags.Instance | BindingFlags.NonPublic);
    private static MethodInfo _localizedStringGetMethod;
    private static PropertyInfo _localizedStringIsEmptyProp;

    // Cached procedural textures & GUIStyles
    private static Texture2D _whiteTex;
    private static Texture2D _cardBgTex;
    private static GUIStyle _labelSmallStyle;
    private static GUIStyle _labelBoldStyle;
    private static GUIStyle _bigNumberStyle;
    private static GUIStyle _medNumberStyle;
    private static GUIStyle _slotIndexStyle;
    private static GUIStyle _centeredSmallStyle;
    private static float _lastStyledScale = -1f;

    // Zero-GC name cache
    private static readonly Dictionary<int, string> CachedDisplayNames = new(128);

    // Half-Life: Alyx inspired palette
    private static readonly Color AlyxPanelDark = new(0.055f, 0.072f, 0.092f, 0.80f);
    private static readonly Color AlyxPanelSlot = new(0.075f, 0.098f, 0.125f, 0.76f);
    private static readonly Color AlyxAmber = new(1.00f, 0.74f, 0.24f, 1.00f);
    private static readonly Color AlyxCyan = new(0.42f, 0.90f, 1.00f, 1.00f);
    private static readonly Color AlyxOffWhite = new(0.93f, 0.96f, 0.98f, 1.00f);
    private static readonly Color AlyxMuted = new(0.62f, 0.70f, 0.76f, 0.92f);
    private static readonly Color AlyxHealthHigh = new(0.92f, 0.96f, 0.98f, 0.96f);
    private static readonly Color AlyxHealthMid = new(1.00f, 0.70f, 0.22f, 0.98f);
    private static readonly Color AlyxHealthLow = new(1.00f, 0.25f, 0.24f, 1.00f);
    private static readonly Color AlyxHungerFill = new(0.96f, 0.68f, 0.24f, 0.92f);
    private static readonly Color AlyxPoisonFill = new(0.45f, 0.95f, 0.36f, 0.96f);
    private static readonly Color AlyxFireFill = new(1.00f, 0.42f, 0.16f, 0.98f);
    private static readonly Color AlyxMoneyGreen = new(0.56f, 0.96f, 0.64f, 1.00f);

    public static void DrawOnDesktopWindow()
    {
        if (!VRConfig.DesktopSpectatorHUD.Value) return;
        if (Event.current == null || Event.current.type != EventType.Repaint) return;
        if (MainMenuManager.IsInMenu) return;

        Player localPlayer = Player.LocalPlayer;
        if (localPlayer == null || localPlayer.Vitals == null) return;

        // Update smooth gaze-based fade once per rendered frame
        float now = Time.unscaledTime;
        if (!Mathf.Approximately(now, _lastUpdateFrameTime))
        {
            float dt = (_lastUpdateFrameTime < 0f) ? 0.016f : Mathf.Clamp(now - _lastUpdateFrameTime, 0f, 0.1f);
            _lastUpdateFrameTime = now;

            bool lookingAtInGameUI = VRUIManager.Instance != null && VRUIManager.Instance.IsPlayerLookingAtInGameUI();
            bool hideForCutsceneOrDeath = localPlayer.Vitals.Health <= 0 ||
                string.Equals(SceneManager.GetActiveScene().name, "Crash", StringComparison.OrdinalIgnoreCase);

            float targetAlpha = (lookingAtInGameUI || hideForCutsceneOrDeath) ? 0f : 1f;
            float fadeSpeed = (targetAlpha < _hudAlpha) ? 4.6f : 3.6f;
            _hudAlpha = Mathf.MoveTowards(_hudAlpha, targetAlpha, dt * fadeSpeed);
        }

        if (_hudAlpha <= 0.01f) return;

        float sw = Screen.width;
        float sh = Screen.height;
        if (sw < 320f || sh < 240f) return;

        float scale = Mathf.Clamp(sh / 1080f, 0.55f, 2.25f);
        EnsureResourcesAndStyles(scale);

        Color prevGuiColor = GUI.color;
        float easedAlpha = Mathf.SmoothStep(0f, 1f, _hudAlpha);

        try
        {
            DrawVitalsModule(localPlayer, sw, sh, scale, easedAlpha);
            DrawHotbarModule(localPlayer, sw, sh, scale, easedAlpha);
            DrawEquippedItemModule(localPlayer, sw, sh, scale, easedAlpha);
            DrawCurrencyModule(sw, sh, scale, easedAlpha);
            DrawBossModule(sw, sh, scale, easedAlpha);
        }
        finally
        {
            GUI.color = prevGuiColor;
        }
    }

    private static void DrawVitalsModule(Player player, float sw, float sh, float scale, float alpha)
    {
        PlayerVitals vitals = player.Vitals;
        int hp = Mathf.Clamp(vitals.Health, 0, 100);
        int fullness = Mathf.Clamp(vitals.Fullness, 0, 100);
        int poison = Mathf.Clamp(vitals._syncedPoison.Value, 0, 100);
        int fire = Mathf.Clamp(vitals._syncedFire.Value, 0, 100);

        float marginX = 34f * scale;
        float marginY = 30f * scale;
        float cardW = 282f * scale;
        float cardH = 104f * scale;
        Rect cardRect = new(marginX, sh - marginY - cardH, cardW, cardH);

        // Status warning pills above Vitals card (Poison / Fire)
        float pillY = cardRect.y - 30f * scale;
        if (poison > 0)
        {
            Rect pill = new(cardRect.x, pillY, 134f * scale, 24f * scale);
            DrawAlyxCard(pill, AlyxPanelDark, AlyxPoisonFill, alpha, leftAccent: true);
            DrawText(new Rect(pill.x + 10f * scale, pill.y + 3f * scale, pill.width - 14f * scale, pill.height),
                $"POISON  {poison}%", _labelBoldStyle, AlyxPoisonFill, alpha);
            pillY -= 28f * scale;
        }
        if (fire > 0)
        {
            Rect pill = new(cardRect.x, pillY, 134f * scale, 24f * scale);
            DrawAlyxCard(pill, AlyxPanelDark, AlyxFireFill, alpha, leftAccent: true);
            DrawText(new Rect(pill.x + 10f * scale, pill.y + 3f * scale, pill.width - 14f * scale, pill.height),
                $"BURNING  {fire}%", _labelBoldStyle, AlyxFireFill, alpha);
        }

        Color hpColor = hp <= 25
            ? Color.Lerp(AlyxHealthLow, AlyxAmber, 0.35f + 0.35f * Mathf.Sin(Time.unscaledTime * 8f))
            : (hp <= 55 ? AlyxHealthMid : AlyxHealthHigh);

        DrawAlyxCard(cardRect, AlyxPanelDark, hp <= 25 ? AlyxHealthLow : AlyxAmber, alpha, leftAccent: true);

        float padX = 15f * scale;
        // Health Label + Large HL:Alyx Number
        DrawText(new Rect(cardRect.x + padX, cardRect.y + 9f * scale, 90f * scale, 18f * scale),
            "HEALTH", _labelSmallStyle, AlyxMuted, alpha);

        DrawText(new Rect(cardRect.x + padX, cardRect.y + 21f * scale, 92f * scale, 44f * scale),
            hp.ToString(), _bigNumberStyle, hpColor, alpha);

        // Segmented HL:Alyx Health Bar (10 pips + smooth backing)
        float barX = cardRect.x + 96f * scale;
        float barW = cardRect.width - 110f * scale;
        float hpBarY = cardRect.y + 34f * scale;
        float hpBarH = 16f * scale;
        DrawSegmentedBar(new Rect(barX, hpBarY, barW, hpBarH), hp / 100f, 10, hpColor, alpha);

        // Divider line
        DrawSolidRect(new Rect(cardRect.x + padX, cardRect.y + 66f * scale, cardRect.width - padX * 2f, Mathf.Max(1f, 1f * scale)),
            new Color(1f, 1f, 1f, 0.10f * alpha));

        // Hunger / Fullness row
        DrawText(new Rect(cardRect.x + padX, cardRect.y + 73f * scale, 82f * scale, 20f * scale),
            $"FULLNESS {fullness}", _labelSmallStyle, fullness <= 15 ? AlyxHealthLow : AlyxMuted, alpha);

        Rect hungerBarRect = new(barX, cardRect.y + 78f * scale, barW, 9f * scale);
        DrawSolidRect(hungerBarRect, new Color(1f, 1f, 1f, 0.10f * alpha));
        if (fullness > 0)
        {
            Color fillCol = fullness <= 15 ? AlyxHealthLow : AlyxHungerFill;
            DrawSolidRect(new Rect(hungerBarRect.x, hungerBarRect.y, hungerBarRect.width * (fullness / 100f), hungerBarRect.height),
                WithAlpha(fillCol, fillCol.a * alpha));
        }
    }

    private static void DrawHotbarModule(Player player, float sw, float sh, float scale, float alpha)
    {
        PlayerInventory inv = player.Inventory;
        if (inv == null) return;

        int startingSlots = InvStartingSlotsField != null ? (int)InvStartingSlotsField.GetValue(inv) : 3;
        int totalSlots = Mathf.Clamp(startingSlots + inv.ExtraSlots, 1, 9);
        int curSlot = InvLocalCurSlotField != null ? (int)InvLocalCurSlotField.GetValue(inv) : inv._syncedCurSlot.Value;
        var slotWidgets = InvItemSlotsField?.GetValue(inv) as List<InventorySlot>;
        BaitSlot baitWidget = InvBaitSlotField?.GetValue(inv) as BaitSlot;

        float slotSize = 58f * scale;
        float gap = 6f * scale;
        byte curBaitIdx = inv.CurBait;
        bool showBait = curBaitIdx > 0 && GameInfo.AllBaits != null && curBaitIdx < GameInfo.AllBaits.Count;

        int totalBoxes = totalSlots + (showBait ? 1 : 0);
        float extraBaitSep = showBait ? 10f * scale : 0f;
        float totalW = totalBoxes * slotSize + Mathf.Max(0, totalBoxes - 1) * gap + extraBaitSep;
        float startX = (sw - totalW) * 0.5f;
        float posY = sh - 30f * scale - slotSize;

        for (int i = 0; i < totalSlots; i++)
        {
            bool selected = (i == curSlot);
            float lift = selected ? -5f * scale : 0f;
            Rect r = new(startX + i * (slotSize + gap), posY + lift, slotSize, slotSize);

            Item slotItem = inv._items.TryGetValue((byte)i, out Item found) ? found : null;
            Color accent = selected ? AlyxAmber : new Color(1f, 1f, 1f, 0.16f);
            DrawAlyxCard(r, selected ? AlyxPanelDark : AlyxPanelSlot, accent, alpha, leftAccent: false, bottomAccent: selected);

            // Render live 3D slot icon texture if available
            bool drewIcon = false;
            if (slotItem != null && slotWidgets != null && i < slotWidgets.Count && slotWidgets[i] != null)
            {
                RawImage rawImg = SlotItemImageField?.GetValue(slotWidgets[i]) as RawImage;
                if (rawImg != null && rawImg.texture != null)
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    float pad = 5f * scale;
                    GUI.DrawTexture(new Rect(r.x + pad, r.y + pad, r.width - pad * 2f, r.height - pad * 2f), rawImg.texture, ScaleMode.ScaleToFit, true);
                    drewIcon = true;
                }
            }

            if (slotItem != null && !drewIcon)
            {
                string shortName = TruncateLabel(GetItemDisplayName(slotItem), 9);
                DrawText(new Rect(r.x + 3f * scale, r.y + 18f * scale, r.width - 6f * scale, r.height - 22f * scale),
                    shortName, _centeredSmallStyle, AlyxOffWhite, alpha);
            }

            // Slot number badge in top-left corner
            DrawText(new Rect(r.x + 5f * scale, r.y + 3f * scale, 18f * scale, 16f * scale),
                (i + 1).ToString(), _slotIndexStyle, selected ? AlyxAmber : AlyxMuted, alpha);
        }

        // Active Bait card on the right of hotbar
        if (showBait)
        {
            float baitX = startX + totalSlots * (slotSize + gap) + extraBaitSep;
            Rect br = new(baitX, posY, slotSize, slotSize);
            DrawAlyxCard(br, AlyxPanelSlot, AlyxCyan, alpha, leftAccent: false, bottomAccent: true);

            BaitInfo baitInfo = GameInfo.AllBaits[curBaitIdx];
            bool drewBaitTex = false;
            if (baitWidget != null)
            {
                RawImage rawImg = SlotItemImageField?.GetValue(baitWidget) as RawImage;
                if (rawImg != null && rawImg.texture != null)
                {
                    GUI.color = new Color(1f, 1f, 1f, alpha);
                    float pad = 6f * scale;
                    GUI.DrawTexture(new Rect(br.x + pad, br.y + pad, br.width - pad * 2f, br.height - pad * 2f), rawImg.texture, ScaleMode.ScaleToFit, true);
                    drewBaitTex = true;
                }
            }

            if (!drewBaitTex && baitInfo != null)
            {
                DrawText(new Rect(br.x + 3f * scale, br.y + 16f * scale, br.width - 6f * scale, br.height - 22f * scale),
                    TruncateLabel(GetBaitDisplayName(baitInfo), 8), _centeredSmallStyle, AlyxCyan, alpha);
            }

            int ownedIdx = curBaitIdx - 1;
            int baitCount = (ownedIdx >= 0 && ownedIdx < inv._ownedBaits.Count) ? inv._ownedBaits[ownedIdx] : 0;
            DrawText(new Rect(br.x + 4f * scale, br.y + 2f * scale, br.width - 8f * scale, 15f * scale),
                "BAIT", _slotIndexStyle, AlyxCyan, alpha);
            DrawText(new Rect(br.x + 4f * scale, br.y + br.height - 18f * scale, br.width - 8f * scale, 16f * scale),
                $"x{baitCount}", _centeredSmallStyle, AlyxOffWhite, alpha);
        }
    }

    private static void DrawEquippedItemModule(Player player, float sw, float sh, float scale, float alpha)
    {
        Item held = player.Holding != null ? player.Holding.HeldItem : null;
        if (held == null) return;

        float marginX = 34f * scale;
        float marginY = 30f * scale;
        string itemName = GetItemDisplayName(held).ToUpperInvariant();

        // Firearm (Half-Life: Alyx digital ammo counter + chamber + reload prompt)
        if (held.Weapon != null && held.Weapon is not Bow &&
            VRCombatAndBoatPatches.TryGetManualWeaponStatusForHUD(
                held.Weapon,
                out int displayAmmo,
                out int maxAmmo,
                out bool hasChamberRound,
                out string stageHint))
        {
            bool hasStageHint = !string.IsNullOrEmpty(stageHint);
            float cardW = 296f * scale;
            float cardH = (hasStageHint ? 114f : 94f) * scale;
            Rect cardRect = new(sw - marginX - cardW, sh - marginY - cardH, cardW, cardH);

            bool isLow = displayAmmo <= 0;
            Color accent = isLow ? AlyxHealthLow : AlyxAmber;
            DrawAlyxCard(cardRect, AlyxPanelDark, accent, alpha, leftAccent: false, rightAccent: true);

            float padX = 15f * scale;
            DrawText(new Rect(cardRect.x + padX, cardRect.y + 8f * scale, cardW - padX * 2f, 18f * scale),
                TruncateLabel(itemName, 24), _labelBoldStyle, AlyxMuted, alpha);

            string modeTag = VRConfig.AutoReloadWeapons.Value ? "AUTO" : "MANUAL VR";
            DrawText(new Rect(cardRect.x + cardW - 102f * scale, cardRect.y + 8f * scale, 88f * scale, 18f * scale),
                modeTag, _labelSmallStyle, VRConfig.AutoReloadWeapons.Value ? AlyxMuted : AlyxCyan, alpha, TextAnchor.UpperRight);

            // Big ammo number
            Color ammoCol = displayAmmo <= 0 ? AlyxHealthLow : AlyxOffWhite;
            DrawText(new Rect(cardRect.x + padX, cardRect.y + 26f * scale, 110f * scale, 48f * scale),
                displayAmmo.ToString("00"), _bigNumberStyle, ammoCol, alpha);

            DrawText(new Rect(cardRect.x + padX + 82f * scale, cardRect.y + 40f * scale, 90f * scale, 28f * scale),
                $"/ {maxAmmo}", _medNumberStyle, AlyxMuted, alpha);

            if (hasChamberRound && !VRConfig.AutoReloadWeapons.Value)
            {
                DrawText(new Rect(cardRect.x + cardW - 118f * scale, cardRect.y + 46f * scale, 104f * scale, 20f * scale),
                    "+1 CHAMBER", _labelBoldStyle, AlyxAmber, alpha, TextAnchor.UpperRight);
            }

            if (hasStageHint)
            {
                DrawSolidRect(new Rect(cardRect.x + padX, cardRect.y + 78f * scale, cardW - padX * 2f, Mathf.Max(1f, 1f * scale)),
                    new Color(1f, 1f, 1f, 0.10f * alpha));
                DrawText(new Rect(cardRect.x + padX, cardRect.y + 85f * scale, cardW - padX * 2f, 20f * scale),
                    stageHint.ToUpperInvariant(), _labelBoldStyle, displayAmmo <= 0 ? AlyxAmber : AlyxCyan, alpha);
            }
            return;
        }

        // Non-firearm held tool / catch / melee / rod / bow
        float compactW = 264f * scale;
        float compactH = 74f * scale;
        Rect compactRect = new(sw - marginX - compactW, sh - marginY - compactH, compactW, compactH);
        DrawAlyxCard(compactRect, AlyxPanelDark, AlyxCyan, alpha, leftAccent: false, rightAccent: true);

        string subLine;
        Color subColor = AlyxMuted;
        if (held.FishingRod != null)
        {
            subLine = "FISHING ROD  •  6DOF CAST & REEL";
            subColor = AlyxCyan;
        }
        else if (held.Weapon is Bow)
        {
            subLine = "ARCHERY BOW  •  TWO-HAND DRAW";
            subColor = AlyxAmber;
        }
        else if (held.Melee != null)
        {
            subLine = VRConfig.AutoReloadWeapons.Value
                ? "MELEE  •  AUTOMATIC / TRIGGER"
                : "MELEE  •  PHYSICAL SWING ONLY";
            subColor = VRConfig.AutoReloadWeapons.Value ? AlyxMuted : AlyxAmber;
        }
        else if (held.TotalWorth > 0)
        {
            subLine = $"VALUE  ${held.TotalWorth:N0}";
            subColor = AlyxMoneyGreen;
        }
        else
        {
            subLine = "EQUIPPED ITEM";
        }

        DrawText(new Rect(compactRect.x + 14f * scale, compactRect.y + 12f * scale, compactW - 28f * scale, 24f * scale),
            TruncateLabel(itemName, 24), _medNumberStyle, AlyxOffWhite, alpha);
        DrawText(new Rect(compactRect.x + 14f * scale, compactRect.y + 42f * scale, compactW - 28f * scale, 20f * scale),
            subLine, _labelSmallStyle, subColor, alpha);
    }

    private static void DrawCurrencyModule(float sw, float sh, float scale, float alpha)
    {
        int dollars = MoneyManager.Money;
        int euros = MoneyManager.Euro;

        float marginX = 34f * scale;
        float marginY = 26f * scale;
        bool showEuro = euros > 0;

        float cardW = (showEuro ? 236f : 168f) * scale;
        float cardH = 44f * scale;
        Rect cardRect = new(sw - marginX - cardW, marginY, cardW, cardH);

        DrawAlyxCard(cardRect, AlyxPanelDark, AlyxMoneyGreen, alpha, leftAccent: false, rightAccent: true);

        string moneyText = showEuro
            ? $"${dollars:N0}   •   €{euros:N0}"
            : $"${dollars:N0}";

        DrawText(new Rect(cardRect.x + 12f * scale, cardRect.y + 8f * scale, cardW - 26f * scale, 28f * scale),
            moneyText, _medNumberStyle, AlyxMoneyGreen, alpha, TextAnchor.MiddleRight);
    }

    private static void DrawBossModule(float sw, float sh, float scale, float alpha)
    {
        Creature boss = BossManager.Boss;
        if (boss == null || !PlayerUI.BossCanvasActive || BossManager.BossMaxHp <= 0) return;

        float barW = 480f * scale;
        float barH = 54f * scale;
        Rect cardRect = new((sw - barW) * 0.5f, 24f * scale, barW, barH);

        DrawAlyxCard(cardRect, AlyxPanelDark, AlyxHealthLow, alpha, leftAccent: true, rightAccent: true);

        string bossName = GetItemDisplayName(boss).ToUpperInvariant();
        float hpPct = Mathf.Clamp01((float)boss.Hp / Mathf.Max(1, BossManager.BossMaxHp));

        DrawText(new Rect(cardRect.x + 16f * scale, cardRect.y + 6f * scale, barW - 32f * scale, 20f * scale),
            bossName, _labelBoldStyle, AlyxOffWhite, alpha, TextAnchor.UpperCenter);

        Rect hpTrack = new(cardRect.x + 16f * scale, cardRect.y + 30f * scale, barW - 32f * scale, 13f * scale);
        DrawSolidRect(hpTrack, new Color(1f, 1f, 1f, 0.12f * alpha));
        if (hpPct > 0f)
        {
            Color fill = BossManager.IsImmortal ? AlyxCyan : AlyxHealthLow;
            DrawSolidRect(new Rect(hpTrack.x, hpTrack.y, hpTrack.width * hpPct, hpTrack.height),
                WithAlpha(fill, alpha));
        }
    }

    // =========================================================================
    // Drawing Helpers & Style Initialization
    // =========================================================================

    private static void DrawAlyxCard(
        Rect r,
        Color bgColor,
        Color accentColor,
        float alpha,
        bool leftAccent = false,
        bool rightAccent = false,
        bool bottomAccent = false)
    {
        // Subtle outer border shadow
        DrawSolidRect(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, r.height + 2f),
            new Color(0f, 0f, 0f, 0.36f * alpha));

        // Main dark translucent body
        GUI.color = WithAlpha(bgColor, bgColor.a * alpha);
        GUI.DrawTexture(r, _cardBgTex != null ? _cardBgTex : _whiteTex, ScaleMode.StretchToFill, true);

        // Subtle top hairline
        DrawSolidRect(new Rect(r.x, r.y, r.width, 1f), new Color(1f, 1f, 1f, 0.08f * alpha));

        float barThick = Mathf.Max(2f, Mathf.Round(3f * (Screen.height / 1080f)));
        Color acc = WithAlpha(accentColor, accentColor.a * alpha);
        if (leftAccent)
        {
            DrawSolidRect(new Rect(r.x, r.y, barThick, r.height), acc);
        }
        if (rightAccent)
        {
            DrawSolidRect(new Rect(r.xMax - barThick, r.y, barThick, r.height), acc);
        }
        if (bottomAccent)
        {
            DrawSolidRect(new Rect(r.x, r.yMax - barThick, r.width, barThick), acc);
        }
    }

    private static void DrawSegmentedBar(Rect r, float pct, int segments, Color fillCol, float alpha)
    {
        pct = Mathf.Clamp01(pct);
        float gap = Mathf.Max(2f, 3f * (r.height / 16f));
        float segW = (r.width - gap * (segments - 1)) / segments;

        for (int i = 0; i < segments; i++)
        {
            Rect segRect = new(r.x + i * (segW + gap), r.y, segW, r.height);
            DrawSolidRect(segRect, new Color(1f, 1f, 1f, 0.11f * alpha));

            float segStart = (float)i / segments;
            float segEnd = (float)(i + 1) / segments;
            float segFill = Mathf.InverseLerp(segStart, segEnd, pct);
            if (segFill > 0.001f)
            {
                DrawSolidRect(new Rect(segRect.x, segRect.y, segRect.width * segFill, segRect.height),
                    WithAlpha(fillCol, fillCol.a * alpha));
            }
        }
    }

    private static void DrawSolidRect(Rect r, Color col)
    {
        if (col.a <= 0.001f) return;
        GUI.color = col;
        GUI.DrawTexture(r, _whiteTex);
    }

    private static void DrawText(Rect r, string text, GUIStyle style, Color col, float alpha, TextAnchor? overrideAlign = null)
    {
        if (string.IsNullOrEmpty(text) || style == null) return;
        TextAnchor prevAlign = style.alignment;
        if (overrideAlign.HasValue) style.alignment = overrideAlign.Value;

        // Crisp 1px drop shadow for readability over bright ocean water/sky
        GUI.color = new Color(0f, 0f, 0f, 0.72f * alpha);
        GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, style);

        GUI.color = WithAlpha(col, col.a * alpha);
        GUI.Label(r, text, style);

        style.alignment = prevAlign;
    }

    private static void EnsureResourcesAndStyles(float scale)
    {
        if (_whiteTex == null)
        {
            _whiteTex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp
            };
            Color[] px = { Color.white, Color.white, Color.white, Color.white };
            _whiteTex.SetPixels(px);
            _whiteTex.Apply();
        }

        if (_cardBgTex == null)
        {
            // Subtle vertical gradient texture for clean HL:Alyx glass depth
            _cardBgTex = new Texture2D(4, 32, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            for (int y = 0; y < 32; y++)
            {
                float t = y / 31f;
                float shade = Mathf.Lerp(0.88f, 1.08f, t);
                Color c = new(shade, shade, shade, 1f);
                for (int x = 0; x < 4; x++) _cardBgTex.SetPixel(x, y, c);
            }
            _cardBgTex.Apply();
        }

        if (_labelSmallStyle != null && Mathf.Abs(_lastStyledScale - scale) < 0.02f) return;
        _lastStyledScale = scale;

        _labelSmallStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.Max(10, Mathf.RoundToInt(12f * scale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft,
            wordWrap = false,
            clipping = TextClipping.Clip
        };
        _labelSmallStyle.normal.textColor = Color.white;

        _labelBoldStyle = new GUIStyle(_labelSmallStyle)
        {
            fontSize = Mathf.Max(11, Mathf.RoundToInt(13f * scale)),
            fontStyle = FontStyle.Bold
        };

        _bigNumberStyle = new GUIStyle(_labelSmallStyle)
        {
            fontSize = Mathf.Max(22, Mathf.RoundToInt(38f * scale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };

        _medNumberStyle = new GUIStyle(_labelSmallStyle)
        {
            fontSize = Mathf.Max(14, Mathf.RoundToInt(19f * scale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleLeft
        };

        _slotIndexStyle = new GUIStyle(_labelSmallStyle)
        {
            fontSize = Mathf.Max(9, Mathf.RoundToInt(11f * scale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.UpperLeft
        };

        _centeredSmallStyle = new GUIStyle(_labelSmallStyle)
        {
            fontSize = Mathf.Max(9, Mathf.RoundToInt(11f * scale)),
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
    }

    private static string ResolveLocalizedObject(object locObj)
    {
        if (locObj == null) return null;
        try
        {
            Type t = locObj.GetType();
            _localizedStringIsEmptyProp ??= t.GetProperty("IsEmpty", BindingFlags.Instance | BindingFlags.Public);
            if (_localizedStringIsEmptyProp != null && _localizedStringIsEmptyProp.GetValue(locObj) is bool isEmpty && isEmpty)
            {
                return null;
            }
            _localizedStringGetMethod ??= t.GetMethod("GetLocalizedString", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            return _localizedStringGetMethod?.Invoke(locObj, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static string GetItemDisplayName(Item item)
    {
        if (item == null) return string.Empty;
        int id = RuntimeHelpers.GetHashCode(item);
        if (CachedDisplayNames.TryGetValue(id, out string cached)) return cached;

        string resolved = ResolveLocalizedObject(ItemNameLocalizedField?.GetValue(item));
        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = CleanAssetName(item.name);
        }

        if (CachedDisplayNames.Count > 512) CachedDisplayNames.Clear();
        CachedDisplayNames[id] = resolved;
        return resolved;
    }

    private static string GetBaitDisplayName(BaitInfo bait)
    {
        if (bait == null) return "Bait";
        int id = RuntimeHelpers.GetHashCode(bait);
        if (CachedDisplayNames.TryGetValue(id, out string cached)) return cached;

        string resolved = ResolveLocalizedObject(BaitNameLocalizedProp?.GetValue(bait));
        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = BaitInternalNameField?.GetValue(bait) as string;
        }
        if (string.IsNullOrWhiteSpace(resolved))
        {
            resolved = CleanAssetName(bait.name);
        }

        CachedDisplayNames[id] = resolved;
        return resolved;
    }

    private static string CleanAssetName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "Item";
        string s = raw.Replace("(Clone)", "").Replace("_", " ").Trim();
        return string.IsNullOrEmpty(s) ? "Item" : s;
    }

    private static string TruncateLabel(string s, int maxChars)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= maxChars) return s;
        return s.Substring(0, Mathf.Max(1, maxChars - 1)) + "…";
    }

    private static Color WithAlpha(Color c, float a) => new(c.r, c.g, c.b, Mathf.Clamp01(a));
}
