using HarmonyLib;

namespace Interstellar.Voice;

[HarmonyPatch(typeof(PassiveButtonManager), nameof(PassiveButtonManager.Update))]
public static class VCInputBlockPatch
{
    public static bool IsAnyVoiceWindowOpen =>
        Interstellar.UI.VoiceSettingsPanel.IsOpen
        || (PublicLobbyWindow.Instance != null && PublicLobbyWindow.Instance.ShowWindow)
        || (PlayerVolumeWindow.Instance != null && PlayerVolumeWindow.Instance.ShowWindow);

    // Returning false skips the original method entirely for this frame. A cursor parked
    // on a menu chip also stands the game's buttons down, so the click that opens the
    // voice panel cannot leak to whatever sits underneath the chip (Perfect-Comms
    // VoiceModalInputBlocker blocks the same method for the same reason).
    static bool Prefix() => !IsAnyVoiceWindowOpen && !VoiceChipEntries.CursorOverChip;
}

[HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.CanMove), HarmonyLib.MethodType.Getter)]
public static class VCCanMoveBlockPatch
{
    static void Postfix(ref bool __result)
    {
        if (VCInputBlockPatch.IsAnyVoiceWindowOpen) __result = false;
    }
}
