using HarmonyLib;

namespace Interstellar.Voice;

[HarmonyPatch]
public static class VoiceChatPatches
{
    // Windows keyboard shortcuts (Nebula uses VirtualInput, we use KeyboardJoystick)
    [HarmonyPostfix, HarmonyPatch(typeof(KeyboardJoystick), nameof(KeyboardJoystick.Update))]
    static void KeyboardUpdate_Post()
    {
        // A key capture in the settings panel owns the keyboard: the press that is
        // being bound must not also fire the action it is about to replace.
        if (Interstellar.UI.VoiceUiKit.SuppressGlobalHotkeys) return;
        if (VoiceConfig.ChordDown(VoiceChord.CycleMic))
            InterstellarHudState.CycleMicPublic();
        if (VoiceConfig.ChordDown(VoiceChord.ToggleSpeaker))
            InterstellarHudState.ToggleSpeakerPublic();
    }
}
