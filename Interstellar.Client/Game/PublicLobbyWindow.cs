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

        TickReconnect();
    }

    /// <summary>The receive loop exits for good when the socket dies (server restart,
    /// network blip) while the panel keeps showing the stale list — every Copy press
    /// then fails instantly. While the category is on screen, retry a dead socket
    /// every few seconds; StartLobbyConnection re-pulls the full snapshot.</summary>
    private void TickReconnect()
    {
        if (!ShowWindow)
        {
            _reconnectAt = 0f;
            return;
        }
        bool dead = _ws == null
            || _ws.State == WebSocketState.Closed
            || _ws.State == WebSocketState.Aborted;
        if (!dead)
        {
            _reconnectAt = 0f;
            return;
        }
        if (_reconnectAt <= 0f)
        {
            _reconnectAt = Time.realtimeSinceStartup + 3f;
            return;
        }
        if (Time.realtimeSinceStartup >= _reconnectAt)
        {
            _reconnectAt = 0f;
            InterstellarPlugin.Logger?.LogInfo("[VC] Lobby browser connection lost — reconnecting.");
            StartLobbyConnection();
        }
    }

    private float _reconnectAt;

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
                                    if (ev == "new_lobbies")
                                    {
                                        int count = arr.GetArrayLength() > 1 && arr[1].ValueKind == JsonValueKind.Array
                                            ? arr[1].GetArrayLength() : -1;
                                        InterstellarPlugin.Logger?.LogInfo($"[VC] Lobby evt new count={count}");
                                        PublicLobbyManager.OnNewLobbies(arr[1].GetRawText());
                                        _status = ""; PublicLobbyManager.IsLoading = false;
                                    }
                                    else if (ev == "update_lobby")
                                    {
                                        int id = -1;
                                        if (arr.GetArrayLength() > 1 && arr[1].ValueKind == JsonValueKind.Object
                                            && arr[1].TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out int parsedId))
                                            id = parsedId;
                                        InterstellarPlugin.Logger?.LogInfo($"[VC] Lobby evt update id={id}");
                                        PublicLobbyManager.OnUpdateLobby(arr[1].GetRawText());
                                    }
                                    else if (ev == "remove_lobby" && arr.GetArrayLength() > 1)
                                    {
                                        InterstellarPlugin.Logger?.LogInfo($"[VC] Lobby evt remove");
                                        PublicLobbyManager.OnRemoveLobby(arr[1].GetInt32());
                                    }
                                }
                            }
                            catch { }
                        }
                        else if (payload.StartsWith("3")) // ack (join_lobby response)
                        {
                            TryHandleAck(payload);
                        }
                        break;
                    case '3': // bare socket.io ack: legacy servers send the packet
                        // without the leading engine.io MESSAGE '4' — accept it too.
                        TryHandleAck(data);
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

    /// <summary>Copy the room code straight out of the cached list entry: the lobby
    /// broadcast carries it ("code"), so no join_lobby round trip is needed and a
    /// server refusing that ack can no longer break the button. Falls back to the
    /// ack flow only when the entry carries no code (older servers strip it).</summary>
    internal void CopyLobbyCode(int lobbyId)
    {
        if (PublicLobbyManager.LobbyMap.TryGetValue(lobbyId, out var lobby)
            && !string.IsNullOrEmpty(lobby.code))
        {
            // The broadcast carries GameId.ToString() — that string doubles as the
            // voice-room key — while players type the six-letter join code.
            string code = GameCode.FromGameIdString(lobby.code);
            GUIUtility.systemCopyBuffer = code;
            _lastCopiedId = lobbyId;
            _status = string.Format(Get("vc.lobby.copied", "Copied: {0}"), code);
            return;
        }
        // BCL's list never carries the code (its PublicLobby has no such field) —
        // ask the server; join_lobby answers [0, code, server, lobby].
        RequestLobbyCode(lobbyId);
    }

    // ── Copy-code ack flow (fallback for servers that strip code) ───────────

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
                // The stale list looks alive after a disconnect — say why the press
                // died (socket state only; no URL, no payload).
                InterstellarPlugin.Logger?.LogWarning(
                    $"[VC] Copy rejected: socket={(_ws == null ? "null" : _ws.State.ToString())}");
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

    /// <summary>Parse a socket.io ack packet — "3<ackId>[args]" — either as it sits
    /// inside an engine MESSAGE frame or as a bare legacy frame (old servers omit the
    /// leading '4'). The copied value never goes to the log.</summary>
    private void TryHandleAck(string packet)
    {
        var rest = packet.Substring(1); // drop the '3' packet type
        int bracket = rest.IndexOf('[');
        if (bracket > 0 && int.TryParse(rest.Substring(0, bracket), out int ackId))
        {
            HandleLobbyAck(ackId, rest.Substring(bracket));
            return;
        }
        // A bare "3" (engine PONG) is not ours to complain about; anything else is a
        // mangled ack — say so without printing the frame (it can carry a room code).
        if (rest.Length > 0)
            InterstellarPlugin.Logger?.LogWarning($"[VC] Ack parse failed (len={packet.Length})");
    }

    // ── Background code fetch (fills the rows' code segment) ───────────────

    /// <summary>join_lobby is a pure lookup on BCL — callback only, no
    /// socket.join, no state change — so the browser asks it for every joinable
    /// row's code when a list lands: the row can show the real join code with
    /// no button press. Fetch acks live in their own id space (>= 1,000,000),
    /// so they can never collide with the Copy flow's ids, and they never
    /// touch the clipboard or the status line. A pending entry older than ten
    /// seconds (socket died mid-ask) expires so the row can be re-asked.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int lobbyId, long at)> _pendingFetches = new();
    private int _fetchSeq = 999_999; // Interlocked.Increment → first ack id is 1,000,000

    internal void FetchMissingCodes()
    {
        try
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            long now = Environment.TickCount64;
            foreach (var kv in PublicLobbyManager.LobbyMap)
            {
                if (kv.Value.gameState != 0 || !string.IsNullOrEmpty(kv.Value.code)) continue;
                bool inflight = false;
                foreach (var p in _pendingFetches)
                {
                    if (p.Value.lobbyId != kv.Key) continue;
                    if (now - p.Value.at < 10_000) inflight = true;
                    else _pendingFetches.TryRemove(p.Key, out _);
                }
                if (inflight) continue;
                int ack = System.Threading.Interlocked.Increment(ref _fetchSeq);
                _pendingFetches[ack] = (kv.Key, now);
                SendRaw($"42{ack}[\"join_lobby\",{kv.Key}]");
            }
        }
        catch { }
    }

    private void HandleLobbyAck(int ackId, string argsJson)
    {
        if (ackId >= 1_000_000)
        {
            // A background code fetch (or the expired twin of one): same
            // [state, code, …] shape as a copy ack, but it only ever feeds the
            // row's code segment.
            if (_pendingFetches.TryRemove(ackId, out var fetch))
            {
                try
                {
                    using var d = JsonDocument.Parse(argsJson);
                    var a = d.RootElement;
                    if (a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 2)
                    {
                        // Accept on shape, not on state: the deployed server puts
                        // the code behind a non-zero state, and a prose error
                        // ("Lobby not found :C") never passes the shape test.
                        var v = a[1].GetString() ?? "";
                        bool shaped = v.Length >= 4 && v.Length <= 64;
                        if (shaped)
                            for (int i = 0; i < v.Length; i++)
                            {
                                char ch = v[i];
                                bool ok = (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z')
                                          || (ch >= 'a' && ch <= 'z') || ch == '-' || ch == '_';
                                if (!ok) { shaped = false; break; }
                            }
                        if (shaped
                            && PublicLobbyManager.LobbyMap.TryGetValue(fetch.lobbyId, out var fl))
                            fl.code = GameCode.FromGameIdString(v);
                    }
                }
                catch { }
            }
            return; // never a Copy ack, never an id-mismatch error
        }
        if (ackId != _pendingCopyAck)
        {
            if (_pendingCopyAck >= 0)
                InterstellarPlugin.Logger?.LogWarning(
                    $"[VC] Copy ack id mismatch: want={_pendingCopyAck} got={ackId}");
            return;
        }
        _pendingCopyAck = -1;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            var arr = doc.RootElement;
            if (arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() >= 2)
            {
                int state = arr[0].GetInt32();
                string value = arr[1].GetString() ?? "";
                // The deployed server answers [1, <value>] where this source answers
                // [0, code] — take any code-shaped value regardless of state (ASCII
                // alnum/-/_ only, 4..64, so a prose or CJK error message never gets
                // copied as a room code).
                bool codeShaped = value.Length >= 4 && value.Length <= 64;
                if (codeShaped)
                    for (int i = 0; i < value.Length; i++)
                    {
                        char ch = value[i];
                        bool ok = (ch >= '0' && ch <= '9') || (ch >= 'A' && ch <= 'Z')
                                  || (ch >= 'a' && ch <= 'z') || ch == '-' || ch == '_';
                        if (!ok) { codeShaped = false; break; }
                    }
                if (!string.IsNullOrEmpty(value) && (state == 0 || codeShaped))
                {
                    // BCL echoes the published GameId string back; convert it to the
                    // six-letter join code (a foreign letter code passes through).
                    string code = GameCode.FromGameIdString(value);
                    GUIUtility.systemCopyBuffer = code;
                    _lastCopiedId = _pendingCopyLobbyId;
                    _status = string.Format(Get("vc.lobby.copied", "Copied: {0}"), code);
                    // The row keeps showing the code after the status line moves on.
                    if (PublicLobbyManager.LobbyMap.TryGetValue(_pendingCopyLobbyId, out var learned))
                        learned.code = code;
                    if (state != 0)
                        InterstellarPlugin.Logger?.LogWarning(
                            $"[VC] Copy ack took value despite state={state} (len={value.Length})");
                    return;
                }
                // Log the shape only — unless the value contains a space: no room
                // code, password or address ever does, so a spaced value is the
                // server's prose reply and seeing it identifies the server build.
                string shown = value.Contains(' ') ? " msg=\"" + value + "\"" : "";
                InterstellarPlugin.Logger?.LogWarning(
                    $"[VC] Copy ack refused: state={state} valueLen={value.Length} " +
                    $"codeShaped={codeShaped}{shown}");
            }
            else
            {
                InterstellarPlugin.Logger?.LogWarning(
                    $"[VC] Copy ack bad args: kind={arr.ValueKind} count={(arr.ValueKind == JsonValueKind.Array ? arr.GetArrayLength() : -1)}");
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
            // The join code is what the row is for: show it as soon as one is
            // known (background fetch or a Copy press), converted from the raw
            // GameId if that is what the server handed back. The mods tag is
            // only the fallback while no code exists yet — mid-game rows have
            // none — and our own rows never claim Vanilla.
            !string.IsNullOrEmpty(lobby.code)
                ? GameCode.FromGameIdString(lobby.code)
                : string.IsNullOrEmpty(lobby.mods)
                    ? Get("vc.lobby.vanilla", "Vanilla")
                    : Truncate(lobby.mods, 16),
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
