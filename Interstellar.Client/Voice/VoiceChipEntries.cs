using System;
using HarmonyLib;
using Interstellar.UI;
using UnityEngine;

namespace Interstellar.Voice;

/// <summary>
/// Perfect-Comms menu entries: parks a <see cref="CommsChipButton"/> on the two original
/// chip slots — the options menu's left edge (14px) and the host settings menu's top
/// centre (-48px) — plus a third one glued to the main menu's top-right corner. Any of
/// them opens the voice settings.
/// </summary>
[HarmonyPatch]
public static class VoiceChipEntries
{
    private static OptionsMenuBehaviour? _optionsMenu;
    private static GameSettingMenu? _hostMenu;
    private static readonly CommsChipButton OptionsChip = new();
    private static readonly CommsChipButton HostChip = new();
    private static readonly CommsChipButton MenuChip = new();

    /// <summary>Cursor parked on any chip: the game's own buttons under it must not fire.</summary>
    public static bool CursorOverChip => OptionsChip.CursorOver || HostChip.CursorOver || MenuChip.CursorOver;

    public static void TickAll()
    {
        // Re-show self-heal: the menus are re-activated, not recreated, when reopened, so
        // their Start postfix (and with it ShowWithPop) never fires a second time — the
        // chip would otherwise stay hidden from the second open onward. Destroyed menus
        // read as null and fail the gate; the host chip also waits for HostGate() so a
        // non-host reopening the menu never sees it flicker.
        try
        {
            if (_optionsMenu != null && _optionsMenu.gameObject != null
                && _optionsMenu.gameObject.activeInHierarchy && !OptionsChip.Visible)
                OptionsChip.ShowWithPop();
            if (_hostMenu != null && _hostMenu.gameObject != null
                && _hostMenu.gameObject.activeInHierarchy && HostGate() && !HostChip.Visible)
                HostChip.ShowWithPop();
        }
        catch (Exception e) { Warn("chip self-heal", e); }
        try { OptionsChip.Tick(); }
        catch (Exception e) { Warn("options tick", e); }
        try { HostChip.Tick(); }
        catch (Exception e) { Warn("host tick", e); }
        try { MenuChip.Tick(); }
        catch (Exception e) { Warn("main menu tick", e); }
    }

    // ── Options menu: chip on the left edge ───────────────────────────

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Start))]
    [HarmonyPostfix]
    static void OnOptionsOpen(OptionsMenuBehaviour __instance)
    {
        try
        {
            _optionsMenu = __instance;
            EnsureOptionsChip();
            OptionsChip.ShowWithPop();
        }
        catch (Exception e) { Warn("options build", e); }
    }

    // The voice panel owns the screen: while it is up the menu's own close button stays
    // dead, so the menu underneath never vanishes mid-edit (Perfect-Comms behaviour).
    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
    [HarmonyPrefix]
    static bool BlockOptionsClose() => !VoiceUiKit.AnyPanelOpen;

    [HarmonyPatch(typeof(OptionsMenuBehaviour), nameof(OptionsMenuBehaviour.Close))]
    [HarmonyPostfix]
    static void OnOptionsClose(OptionsMenuBehaviour __instance)
    {
        // Harmony still runs a postfix when a prefix cancelled the method, so only let
        // go of the menu once it is actually gone — a blocked close keeps the chip up.
        if (__instance != null && __instance.gameObject != null && __instance.gameObject.activeInHierarchy) return;
        // Keep the reference: reopening re-activates this same menu without re-running
        // Start, and TickAll's self-heal needs it to raise the chip again. Destroyed
        // menus read as null and simply fail the gate.
        OptionsChip.Hide();
    }

    private static void EnsureOptionsChip()
    {
        if (OptionsChip.Built) return;
        // Android's camera cutout/status bar eats into the screen edge, and in landscape
        // the notch sits on the left — a fixed 14px inset then lands outside the visible
        // area ("off-screen"). Measure the same inset from the safe-area edge instead;
        // on PC the safe area is the whole screen, so the position is unchanged there.
        float x = 14f;
        try
        {
            float scale = Mathf.Max(0.01f, VoiceUiKit.Canvas.transform.localScale.x);
            x = Mathf.Max(14f, Screen.safeArea.xMin / scale + 14f);
        }
        catch { }
        OptionsChip.Build("INTERSTELLAR", TranslationHelper.Get("vc.chip.voiceSettings", "VOICE SETTINGS"),
            new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f),
            new Vector2(x, 0f),
            ToggleSettings,
            static () => _optionsMenu != null && _optionsMenu.gameObject != null && _optionsMenu.gameObject.activeInHierarchy,
            static () => true, 1.0f);
    }

    // ── Host settings menu: chip top centre ───────────────────────────

    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Start))]
    [HarmonyPostfix]
    static void OnHostOpen(GameSettingMenu __instance)
    {
        try
        {
            if (__instance == null) return;
            _hostMenu = __instance;
            EnsureHostChip();
            if (HostGate()) HostChip.ShowWithPop();
        }
        catch (Exception e) { Warn("host build", e); }
    }

    [HarmonyPatch(typeof(GameSettingMenu), nameof(GameSettingMenu.Close))]
    [HarmonyPostfix]
    static void OnHostClose()
    {
        // _hostMenu is kept: reopening re-runs neither Start nor OnHostOpen for a reused
        // menu, so TickAll's gate-checked self-heal is what raises the chip again.
        HostChip.Hide();
        // Perfect-Comms closes its host-only panel here. Ours is the shared settings
        // panel (it can also be opened from F11 or the menu chip), so it stays put.
    }

    private static void EnsureHostChip()
    {
        if (HostChip.Built) return;
        HostChip.Build("INTERSTELLAR", TranslationHelper.Get("vc.chip.voiceSettings", "VOICE SETTINGS"),
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
            new Vector2(0f, -48f),
            ToggleSettings,
            static () => _hostMenu != null && _hostMenu.gameObject != null && _hostMenu.gameObject.activeInHierarchy,
            HostGate);
    }

    // ── Main menu: chip pinned to the top-right corner ────────────────────

    [HarmonyPatch(typeof(MainMenuManager), nameof(MainMenuManager.Start))]
    [HarmonyPostfix]
    static void OnMainMenuOpen()
    {
        try
        {
            EnsureMenuChip();
            MenuChip.ShowWithPop();
        }
        catch (Exception e) { Warn("main menu build", e); }
    }

    private static void EnsureMenuChip()
    {
        if (MenuChip.Built) return;
        // The same 420x92 chip, anchored into the overlay canvas's top-right corner —
        // the screen-corner pinning the in-game bottom-left server text and hot-button
        // plate use (screen-space overlay, CanvasScaler 1920x1080 ScaleWithScreenSize),
        // so the slot and the chip's size hold at every resolution. Its menuAlive gate
        // parks it again the moment the menu scene goes away, and MainMenuManager.Start
        // raises it again on the way back.
        MenuChip.Build("INTERSTELLAR", TranslationHelper.Get("vc.chip.voiceSettings", "VOICE SETTINGS"),
            new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f),
            new Vector2(-40f, -40f),
            ToggleSettings,
            static () => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainMenu",
            static () => true);
    }

    private static bool HostGate() =>
        AmongUsClient.Instance != null && AmongUsClient.Instance.AmHost;

    private static void ToggleSettings()
    {
        try
        {
            var inst = VoiceSettingsWindow.Instance;
            if (inst != null) { inst.Toggle(); return; }
            if (VoiceSettingsPanel.IsOpen) VoiceSettingsPanel.Hide(); else VoiceSettingsPanel.Show();
        }
        catch (Exception e) { Warn("click", e); }
    }

    private static void Warn(string what, Exception e)
        => InterstellarPlugin.Logger?.LogWarning("[VC] Chip " + what + " failed: " + e.Message);
}
