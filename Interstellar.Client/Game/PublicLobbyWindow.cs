#pragma warning disable CS8602, CS8603, CS8618
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using UnityEngine;
using Interstellar.UI;
using Interstellar.Voice;

namespace Interstellar;

/// <summary>
/// Public lobby browser host: the websocket, the status line the settings panel prints
/// and the join_lobby copy-code ack flow. The surface moved into the settings panel —
/// F2 now opens it on the PUBLIC LOBBY rail entry — so nothing here draws any more:
/// the panel calls <see cref="SetBrowsing"/> while that category is on screen and reads
/// <see cref="StatusText"/> / <see cref="LastCopiedId"/> when it paints its rows.
/// </summary>
public class PublicLobbyWindow : MonoBehaviour
{
    public PublicLobbyWindow(System.IntPtr ptr) : base(ptr) { }

    public static PublicLobbyWindow? Instance { get; private set; }

    /// <summary>True while the panel is subscribed to the lobby list. VCInputBlockPatch
    /// reads this as "voice UI is up", the same contract the F2 window used to serve.</summary>
    public bool ShowWindow { get; private set; }

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _status = "";

    /// <summary>Socket status exactly as written ("Connecting...", "Getting code...",
    /// "Copied: XXX", errors). The panel's status row prints it unchanged, the way the
    /// old window's status line did.</summary>
    internal string StatusText => _status;

    // ── State (unchanged data path) ─────────────────────────────────────────
    private int _ackSeq;
    private int _pendingCopyAck = -1;
    private int _pendingCopyLobbyId = -1;
    private int _lastCopiedId = -1;

    /// <summary>Lobby whose code already landed on the clipboard — its row reads "Copied".</summary>
    internal int LastCopiedId => _lastCopiedId;

    void Awake() => Instance = this;

    void OnDestroy()
    {
        StopLobbyConnection();
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        // Fifth and last hotkey reader on the chord path (the other four: settings,
        // player volume, cycle mic, toggle speaker). F2 lands on the panel's own
        // category now instead of a private window, so it fires whether or not that
        // panel is already open; the standard guard keeps a rebinding row (and the
        // release of the key it just bound) in charge of the keyboard.
        if (!VoiceUiKit.SuppressGlobalHotkeys && VoiceConfig.ChordDown(VoiceChord.PublicLobby))
            Toggle();
    }

    public void Toggle()
    {
        var win = VoiceSettingsWindow.Instance;
        if (win == null) return;
        if (win.ShowWindow && VoiceSettingsPanel.IsOnCategory(VoiceSettingsPanel.CatLobby))
            win.Close();
        else
            win.OpenCategory(VoiceSettingsPanel.CatLobby);
    }

    /// <summary>Panel-driven lifecycle: the connect-on-open / disconnect-on-close of the
    /// old F2 window, keyed off whether the PUBLIC LOBBY category is on screen.</summary>
    public void SetBrowsing(bool browsing)
    {
        if (browsing == ShowWindow) return;
        ShowWindow = browsing;
        if (browsing) StartLobbyConnection();
        else StopLobbyConnection();
    }

    /// <summary>Refresh button in the panel: drop the socket and pull the list again.</summary>
    internal void Refresh()
    {
        if (!ShowWindow) return;
        StopLobbyConnection();
        StartLobbyConnection();
    }

    // ── Lobby socket (unchanged) ────────────────────────────────────────────

    async void StartLobbyConnection()
    {
        StopLobbyConnection();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _status = Get("vc.lobby.connecting", "Connecting...");
        PublicLobbyManager.IsLoading = true;
        PublicLobbyManager.LobbyMap.Clear();

        _ = Task.Delay(10000, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested) { PublicLobbyManager.IsLoading = false; if (string.IsNullOrEmpty(_status)) _status = Get("vc.lobby.noReceived", "No lobbies received."); }
        }, TaskScheduler.Default);

        try
        {
            var url = VoiceConfig.GetActiveServerURL();
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http"))
                throw new Exception(Get("vc.lobby.badUrl", "Invalid server URL: ") + url);
            var u = new Uri(url);
            var wsUrl = (u.Scheme == "https" ? "wss" : "ws") + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + "/socket.io/?EIO=3&transport=websocket";

            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(wsUrl), token);
            _status = Get("vc.lobby.connectedWaiting", "Connected, waiting...");

            var buf = new byte[8192];
            var sb = new StringBuilder();
            bool gotOpen = false;

            while (_ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(buf, token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, result.Count));
                if (!result.EndOfMessage) continue;

                var data = sb.ToString(); sb.Clear();
                if (string.IsNullOrEmpty(data)) continue;

                switch (data[0])
                {
                    case '0': // engine.io open
                        if (!gotOpen)
                        {
                            gotOpen = true;
                        }
                        break;
                    case '2': // ping
                        SendRaw("3");
                        break;
                    case '4': // socket.io message
                        var payload = data.Substring(1);
                        if (payload.StartsWith("0")) // socket.io connected
                        {
                            SendRaw("42[\"lobbybrowser\",true]");
                            _status = Get("vc.lobby.loadingLobbies", "Loading lobbies...");
                        }
                        else if (payload.StartsWith("2")) // event
                        {
                            try
                            {
                                using var d = JsonDocument.Parse(payload.Substring(1));
                                var arr = d.RootElement;
                                if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                                {
                                    var ev = arr[0].GetString();
                                    if (ev == "new_lobbies") { PublicLobbyManager.OnNewLobbies(arr[1].GetRawText()); _status = ""; PublicLobbyManager.IsLoading = false; }
                                    else if (ev == "update_lobby") PublicLobbyManager.OnUpdateLobby(arr[1].GetRawText());
                                    else if (ev == "remove_lobby" && arr.GetArrayLength() > 1) PublicLobbyManager.OnRemoveLobby(arr[1].GetInt32());
                                }
                            }
                            catch { }
                        }
                        else if (payload.StartsWith("3")) // ack (join_lobby response)
                        {
                            var rest = payload.Substring(1);
                            int bracket = rest.IndexOf('[');
                            if (bracket > 0 && int.TryParse(rest.Substring(0, bracket), out int ackId))
                                HandleLobbyAck(ackId, rest.Substring(bracket));
                            else
                                InterstellarPlugin.Logger?.LogWarning($"[VC] Ack parse failed: {payload}");
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status = Get("vc.lobby.errorPrefix", "Error: ") + ex.Message; PublicLobbyManager.IsLoading = false; }

        if (PublicLobbyManager.IsLoading && _status != Get("vc.lobby.loadingLobbies", "Loading lobbies..."))
            PublicLobbyManager.IsLoading = false;
    }

    async void SendRaw(string data)
    {
        if (_ws?.State == WebSocketState.Open)
            try { await _ws.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
    }

    void StopLobbyConnection()
    {
        _cts?.Cancel();
        try { _ws?.Dispose(); } catch { }
        _ws = null;
        PublicLobbyManager.IsLoading = false;
        PublicLobbyManager.StopWatching();
    }

    // ── Copy-code ack flow (unchanged) ──────────────────────────────────────

    /// <summary>Request the lobby's real room code from the server (join_lobby ack),
    /// then copy it. Takes the id rather than the LobbyInfo: this is an instance method
    /// on an IL2CPP-registered MonoBehaviour, so its parameter types must be ones the
    /// class injector understands (a managed LobbyInfo is not).</summary>
    internal void RequestLobbyCode(int lobbyId)
    {
        try
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                _status = Get("vc.lobby.copyFailed", "Copy failed");
                return;
            }
            _ackSeq = Math.Max(1, _ackSeq + 1);
            _pendingCopyAck = _ackSeq;
            _pendingCopyLobbyId = lobbyId;
            _status = Get("vc.lobby.copying", "Getting code...");
            SendRaw($"42{_ackSeq}[\"join_lobby\",{lobbyId}]");
        }
        catch (Exception e)
        {
            _status = Get("vc.lobby.copyFailed", "Copy failed");
            InterstellarPlugin.Logger?.LogError($"[VC] Request lobby code failed: {e}");
        }
    }

    private void HandleLobbyAck(int ackId, string argsJson)
    {
        if (ackId != _pendingCopyAck) return;
        _pendingCopyAck = -1;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 2)
            {
                int state = arr[0].GetInt32();
                string value = arr[1].GetString() ?? "";
                if (state == 0 && !string.IsNullOrEmpty(value))
                {
                    GUIUtility.systemCopyBuffer = value;
                    _lastCopiedId = _pendingCopyLobbyId;
                    _status = string.Format(Get("vc.lobby.copied", "Copied: {0}"), value);
                    return;
                }
            }
            _status = Get("vc.lobby.copyFailed", "Copy failed");
        }
        catch (Exception e)
        {
            _status = Get("vc.lobby.copyFailed", "Copy failed");
            InterstellarPlugin.Logger?.LogError($"[VC] Handle lobby ack failed: {e}");
        }
    }

    // ── Row text (shared with the settings panel) ───────────────────────────

    /// <summary>The dimmed line under a lobby's title: state, host, fill, mods, language.</summary>
    internal static string BuildDetails(PublicLobbyManager.LobbyInfo lobby)
    {
        var parts = new List<string>
        {
            PublicLobbyManager.GetGameStateName(lobby.gameState),
            string.Format(Get("vc.lobby.by", "by {0}"), Truncate(lobby.host, 14)),
            lobby.current_players + "/" + lobby.max_players,
            string.IsNullOrEmpty(lobby.mods) ? Get("vc.lobby.vanilla", "Vanilla") : Truncate(lobby.mods, 16),
        };
        if (!string.IsNullOrWhiteSpace(lobby.language)) parts.Add(Truncate(lobby.language, 12));
        return string.Join("  •  ", parts);
    }

    internal static string Truncate(string? value, int max)
    {
        value = string.IsNullOrWhiteSpace(value) ? "?" : value.Trim();
        return value.Length <= max ? value : value.Substring(0, Math.Max(0, max - 1)) + "…";
    }

    private static string Get(string key, string fallback) => TranslationHelper.Get(key, fallback);
}
