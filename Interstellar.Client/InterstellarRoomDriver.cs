using UnityEngine;
using VoiceChatPlugin;
using VoiceChatPlugin.VoiceChat;
using Object = UnityEngine.Object;

namespace VoiceChatPlugin;

internal static class InterstellarRoomDriver
{
    private static bool _wasInIntro = false;
    private static bool _wasInEndGame = false;
    private static bool _splashShownThisGame;

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
            _splashShownThisGame = false;
            VoiceChatServerState.Reset();
            return;
        }

        // Nebula: if (Instance == null) StartVoiceChat(region, roomId)
        if (VoiceChatRoom.Current == null)
        {
            string region = AmongUsClient.Instance!.networkAddress;
            string roomId = AmongUsClient.Instance.GameId.ToString();
            VoiceChatRoom.Start(region, roomId);
            InterstellarHudState.ApplyMicState();
            InterstellarHudState.ApplySpeakerState();

            if (AmongUsClient.Instance.AmHost)
            {
                VoiceChatConfig.ApplyLocalHostSettingsToSynced();
                InterstellarHudState.MarkRoomSettingsDirty();
            }

            InterstellarPlugin.Logger.LogInfo($"[VC] Room started: region={region} room={roomId}");

            // Show join splash — once per game session, for all players including host
            if (!_splashShownThisGame)
            {
                _splashShownThisGame = true;
                JoinSplashScreen.Show();
            }
        }

        if (VoiceChatRoom.Current == null) return;

        // IntroCutscene ended → Rejoin to re-sync profiles
        bool inIntro = IntroCutscene.Instance != null;
        if (_wasInIntro && !inIntro)
        {
            foreach (var c in VoiceChatRoom.Current.AllClients)
                c.ResetMapping();
            VoiceChatRoom.Current.ForceUpdateLocalProfile();
            InterstellarPlugin.Logger.LogInfo("[VC] IntroCutscene ended: mappings reset, profile re-broadcast.");
        }
        _wasInIntro = inIntro;

        // EndGame started → Rejoin
        bool inEndGame = Object.FindObjectOfType<EndGameManager>() != null;
        if (inEndGame && !_wasInEndGame)
        {
            VoiceChatRoom.Current.Rejoin();
            VoiceChatRoom.Current.ForceUpdateLocalProfile();
            InterstellarPlugin.Logger.LogInfo("[VC] EndGame: room rejoined.");
        }
        _wasInEndGame = inEndGame;

        InterstellarHudState.TrySyncHostRoomSettings();

        try { VoiceChatRoom.Current.Update(); }
        catch (System.Exception ex)
        { InterstellarPlugin.Logger.LogError("[VC] Room update error: " + ex); }
    }
}
