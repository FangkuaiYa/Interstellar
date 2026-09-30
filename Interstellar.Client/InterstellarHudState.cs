using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Interstellar.Voice;
using Object = UnityEngine.Object;

namespace Interstellar.Voice;

public static class InterstellarHudState
{
    public static bool IsSpeakerMuted => VoiceButtons.IsSpeakerMuted;
    public static bool IsImpostorRadioOnly => VoiceButtons.IsImpostorRadioOnly;

    private static VoiceRoomSettings? _lastSentSettings;
    public static void MarkRoomSettingsDirty() => _lastSentSettings = null;

    private static TextMeshProUGUI? _serverInfoText;

    private static bool _lastPublicLobbyState;
    private static int _lastPublicLobbyPlayers;

    internal static void Init()
    {
        SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)((_, __) =>
        {
            DestroyServerInfoText();
        });
    }

    internal static void UpdateHud()
    {
        var hud = HudManager.Instance;
        if (hud == null) return;
        EnsureServerInfoText(hud);
        UpdateServerInfoText();
    }

    internal static void ApplyMicState() => VoiceButtons.ApplyMicState();
    internal static void ApplySpeakerState() => VoiceButtons.ApplySpeakerState();
    internal static void CycleMicPublic() => VoiceButtons.CycleMic();
    internal static void ToggleSpeakerPublic() => VoiceButtons.ToggleSpeaker();

    internal static void TrySyncHostRoomSettings()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        if (AmongUsClient.Instance.GameState != InnerNet.InnerNetClient.GameStates.Joined) return;

        var cur = VoiceConfig.SyncedRoomSettings;
        if (_lastSentSettings != null && cur.ContentEquals(_lastSentSettings)) return;

        VoiceRoom.Current?.SendHostSettings(cur);
        _lastSentSettings = new VoiceRoomSettings();
        _lastSentSettings.Apply(cur);
    }

    internal static void TrySyncPublicLobby()
    {
        if (AmongUsClient.Instance == null || !AmongUsClient.Instance.AmHost) return;
        var room = VoiceRoom.Current;
        if (room == null) return;

        bool wantPublic = VoiceConfig.PublicLobbyEnabled;
        int curPlayers = 0;
        foreach (var p in PlayerControl.AllPlayerControls) if (p != null) curPlayers++;

        if (wantPublic == _lastPublicLobbyState && curPlayers == _lastPublicLobbyPlayers) return;
        _lastPublicLobbyState = wantPublic;
        _lastPublicLobbyPlayers = curPlayers;

        if (wantPublic)
        {
            var code = AmongUsClient.Instance.GameId.ToString();
            _ = room.PublishLobbyAsync(code, new PublicLobbyManager.LobbyInfo
            {
                title = VoiceConfig.PublicLobbyTitle,
                host = PlayerControl.LocalPlayer?.name ?? "Host",
                current_players = curPlayers,
                max_players = GameOptionsManager.Instance?.currentNormalGameOptions?.MaxPlayers ?? 10,
                language = VoiceConfig.PublicLobbyLanguage,
                mods = "Vanilla",
                server = VoiceConfig.GetActiveServerURL(),
                gameState = 1,
            });
        }
        else
        {
            var code = AmongUsClient.Instance.GameId.ToString();
            _ = room.RemoveLobbyAsync(code);
        }
    }

    private static void DestroyServerInfoText()
    {
        if (_serverInfoText != null) { Object.Destroy(_serverInfoText.gameObject); _serverInfoText = null; }
    }

    private static void EnsureServerInfoText(HudManager hud)
    {
        if (_serverInfoText != null) return;
        var canvas = VCUiKit.EnsureCanvas();
        _serverInfoText = VCUiKit.CreateText(canvas.transform, "VC_ServerInfo", "",
            Vector2.zero, new Vector2(1400f, 36f), 18f, new Color(0.6f, 0.85f, 0.6f),
            align: TextAlignmentOptions.BottomLeft);
        Object.DontDestroyOnLoad(_serverInfoText.gameObject);
        var rt = _serverInfoText.rectTransform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(30f, 20f);
    }

    private static void UpdateServerInfoText()
    {
        if (_serverInfoText == null) return;

        bool inLobby = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "OnlineGame";
        if (!inLobby) { _serverInfoText.text = ""; return; }

        var room = VoiceRoom.Current;
        if (room == null) { _serverInfoText.text = ""; return; }

        string serverUrl = VoiceServerState.HasInfo ? VoiceServerState.VoiceServerUrl : VoiceConfig.GetActiveServerURL();
        if (string.IsNullOrEmpty(serverUrl)) { _serverInfoText.text = ""; return; }

        string serverName = ResolveServerName(serverUrl);
        int cur = 0;
        foreach (var _ in room.AllClients) cur++;
        int opt = VoiceServerState.OptimalPlayers;
        string transport = "  ·  " + TransportLabel();
        var ok = new Color(0.6f, 0.85f, 0.6f);
        var warn = new Color(1f, 0.65f, 0.2f);

        // P2P direct pushes no media through the server, so printing its name reads as
        // if voice still went through it: lead with the transport instead. The roster
        // count stays (still your room), and so does the capacity warning — the server
        // is still what admits new signups.
        if (VoiceConfig.TransportMode == "P2P")
        {
            _serverInfoText.text = TransportLabel() + (opt > 0 ? "  " + cur + "/" + opt : "  " + cur);
            _serverInfoText.color = opt > 0 && cur >= opt ? warn : ok;
            return;
        }

        if (opt > 0)
        {
            _serverInfoText.text = TranslationHelper.Get("vc.hud.serverPrefix", "Interstellar Server: ") + serverName + "  " + cur + "/" + opt + transport;
            _serverInfoText.color = cur >= opt ? warn : ok;
        }
        else
        {
            _serverInfoText.text = TranslationHelper.Get("vc.hud.serverPrefix", "Interstellar Server: ") + serverName + "  " + cur + transport;
            _serverInfoText.color = ok;
        }
    }

    /// <summary>Current media transport, in the panel's own words — so the HUD and the
    /// settings row can never drift apart.</summary>
    private static string TransportLabel() => VoiceConfig.TransportMode switch
    {
        "P2P" => TranslationHelper.Get("vc.settings.transport.p2p", "P2P direct"),
        "Relay" => TranslationHelper.Get("vc.settings.transport.relay", "Server relay"),
        _ => TranslationHelper.Get("vc.settings.transport.auto", "Auto (P2P + relay)"),
    };

    private static string ResolveServerName(string url)
    {
        var servers = ServerList.GetServers();
        foreach (var (name, serverUrl) in servers)
        {
            if (string.Equals(serverUrl.TrimEnd('/'), url.TrimEnd('/'), System.StringComparison.OrdinalIgnoreCase))
                return name;
        }
        return ShortenServerUrl(url);
    }

    static string ShortenServerUrl(string url)
    {
        var host = url.Replace("ws://", "").Replace("wss://", "").Replace("/vc", "");
        var colon = host.LastIndexOf(':');
        if (colon > 0) host = host.Substring(0, colon);
        return host;
    }

    static readonly Dictionary<string, Sprite> _spriteCache = new();

    public static Sprite LoadSprite(string path)
    {
        if (_spriteCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 900f);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[path] = spr;
            return spr;
        }
        catch { return null!; }
    }

    public static Sprite? LoadSpriteFromResources(string path, float pixelsPerUnit)
    {
        var key = path + "@" + pixelsPerUnit;
        if (_spriteCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(path);
            if (stream == null) return null;
            var tex = new Texture2D(0, 0, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            tex.LoadImage(ms.ToArray(), false);
            var spr = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
            spr.hideFlags |= HideFlags.HideAndDontSave | HideFlags.DontSaveInEditor;
            _spriteCache[key] = spr;
            return spr;
        }
        catch { return null; }
    }
}