using UnityEngine;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

internal static class VoiceChatRoomDriver
{
    private static bool _wasInIntro = false;
    private static bool _wasInEndGame = false;

    private static bool IsLocalServer()
    {
        var addr = AmongUsClient.Instance?.networkAddress;
        return addr is "127.0.0.1" or "localhost";
    }

    internal static void Update()
    {
        // Nebula: shouldNotUseVC = !option || IsLocalServer()
        bool shouldNotUseVC = AmongUsClient.Instance == null
            || (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined
                && AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Started)
            || IsLocalServer();

        if (shouldNotUseVC)
        {
            if (VoiceChatRoom.Current != null)
                VoiceChatRoom.CloseCurrentRoom();
            _wasInIntro = _wasInEndGame = false;
            return;
        }

        // Nebula: if (Instance == null) StartVoiceChat(region, roomId)
        if (VoiceChatRoom.Current == null)
        {
            string region = AmongUsClient.Instance!.networkAddress;
            string roomId = AmongUsClient.Instance.GameId.ToString();
            VoiceChatRoom.Start(region, roomId);
            VoiceChatHudState.ApplyMicState();
            VoiceChatHudState.ApplySpeakerState();

            if (AmongUsClient.Instance.AmHost)
            {
                VoiceChatConfig.ApplyLocalHostSettingsToSynced();
                VoiceChatHudState.MarkRoomSettingsDirty();
            }

            VoiceChatPluginMain.Logger.LogInfo($"[VC] Room started: region={region} room={roomId}");
        }

        if (VoiceChatRoom.Current == null) return;

        // ── IntroCutscene ended → Rejoin to re-sync profiles ───────
        // FIX: After game start, profiles need re-sync because client
        // mappings are stale. Rejoin() triggers RequestReload on the
        // Interstellar server so all clients re-share profiles.
        bool inIntro = IntroCutscene.Instance != null;
        if (_wasInIntro && !inIntro)
        {
            foreach (var c in VoiceChatRoom.Current.AllClients)
                c.ResetMapping();
            VoiceChatRoom.Current.ForceUpdateLocalProfile();
            VoiceChatPluginMain.Logger.LogInfo("[VC] IntroCutscene ended: mappings reset, profile re-broadcast.");
        }
        _wasInIntro = inIntro;

        // ── EndGame started → Rejoin ───────────────────────────────
        bool inEndGame = Object.FindObjectOfType<EndGameManager>() != null;
        if (inEndGame && !_wasInEndGame)
        {
            VoiceChatRoom.Current.Rejoin();
            VoiceChatRoom.Current.ForceUpdateLocalProfile();
            VoiceChatPluginMain.Logger.LogInfo("[VC] EndGame: room rejoined.");
        }
        _wasInEndGame = inEndGame;

        // ── Per-frame ─────────────────────────────────────────────────
        VoiceChatHudState.TrySyncHostRoomSettings();

        try { VoiceChatRoom.Current.Update(); }
        catch (System.Exception ex)
        { VoiceChatPluginMain.Logger.LogError("[VC] Room update error: " + ex); }
    }
}
