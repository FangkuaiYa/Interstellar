using UnityEngine;

namespace Interstellar.Voice;

internal static class BlackmailVoiceGate
{
    private static volatile bool _active;

    private static bool _baselineActive;
    private static bool _baselineSeen;

    public static bool Active => _active;

    public static void Poll()
    {
        bool inMeeting = false;
        try { inMeeting = MeetingHud.Instance != null; } catch { }

        bool seen = false;
        bool buttonActive = false;
        try
        {
            var hud = HudManager.Instance;
            var chat = hud != null ? hud.Chat : null;
            var button = chat != null ? chat.quickChatButton : null;
            if (button != null)
            {
                buttonActive = button.gameObject.activeSelf;
                seen = true;
            }
        }
        catch { seen = false; }

        if (!inMeeting)
        {
            _active = false;
            if (seen)
            {
                _baselineActive = buttonActive;
                _baselineSeen = true;
            }
            return;
        }

        try
        {
            // Ghosts answer to the ghost-voice rules, not to a meeting gag.
            var local = PlayerControl.LocalPlayer;
            if (local == null || local.Data == null || local.Data.IsDead)
            {
                _active = false;
                return;
            }

            bool gated = seen && _baselineSeen && buttonActive != _baselineActive;
            if (gated == _active) return;
            _active = gated;
            InterstellarPlugin.Logger?.LogInfo(gated
                ? $"[VC] Blackmail gate: quick chat button flipped in meeting " +
                  $"(baseline={_baselineActive} now={buttonActive}), TX held"
                : "[VC] Blackmail gate: quick chat button back to baseline, TX resumed");
        }
        catch
        {
            _active = false; // fail open: no detection → normal voice
        }
    }
}
