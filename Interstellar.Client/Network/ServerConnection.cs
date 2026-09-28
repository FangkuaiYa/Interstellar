using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Interstellar.Voice;

namespace Interstellar.Network;

internal interface IConnectionContext
{
    void OnAudioFrameReceived(int clientId, float[] samples, int length);
    void OnClientConnected(int clientId);
    void OnClientDisconnected(int clientId);
    void OnClientProfileUpdated(int clientId, string playerName, byte playerId);
    void OnReceiveMuteStatus(int clientId, bool isMute, bool isImpostorRadio);
    void OnCustomMessageReceived(byte[] message);
    void OnHostSettingsReceived(byte[] rawSettings);
    void OnServerInfoReceived(int optimalPlayers, int totalClients, string serverUrl);
}

internal class ServerConnection : IConnectionContext, IDisposable
{
    private readonly string _roomCode;
    private readonly IConnectionContext _context;
    private readonly string _wsUrl;
    private readonly string _httpOrigin;
    private WebSocket? _sws;
    private CancellationTokenSource? _cts;
    private bool _disposed, _connected;
    private int _connecting;
    private string? _sid;
    private int _localPlayerId, _localClientId;
    private bool _hasIds;
    private bool _joinIssued;
    private bool _joinPending;
    private bool _socketReady;

    private readonly ConcurrentDictionary<string, (int playerId, int clientId)> _peers = new();
    // Per-peer liveness: the server never tells us a peer died, so we prove the
    // link is alive with an "HB:" marker every 5s (independent of speech/VAD).
    // If a peer we have been sending to stops answering, our peer map is stale
    // and re-requesting setClients is the fix — this is what repairs the
    // occasional "neither side can hear the other" state without a refresh.
    private readonly ConcurrentDictionary<int, (int firstSeen, int lastBeat)> _peerBeat = new();
    // Wall-clock of the last packet we actually RECEIVED from each peer. Kept
    // apart from _peerBeat because AddPeer seeds lastBeat with "now" as a
    // registration grace — using that to answer "have I heard from them" would
    // make every freshly announced peer look alive, and the Android watchdog
    // that guards against a broken relay would never fire.
    private readonly ConcurrentDictionary<int, int> _lastRecv = new();
    private const int HeartbeatPeriodMs = 5000;
    private const int HeartbeatTimeoutMs = 15000;
    public bool HasPeers => !_peers.IsEmpty;
    private readonly HashSet<string> _unknownSignalLogged = new();
    private int _parseErrors;
    // TX side: microphones do not always deliver valid Opus frame lengths.
    private readonly float[] _txAccum = new float[2880]; // up to 60ms @48k
    private int _txAccumLen;
    private int _txFrames, _txEncodeErrors, _lastResyncTick;
    private int _resyncBackoffMs = 5000;
    private const int ResyncMinMs = 5000;
    private const int ResyncMaxMs = 30000;
    public int TxFrames => _txFrames;
    public int TxEncodeErrors => _txEncodeErrors;
    private readonly ConcurrentDictionary<int, Concentus.IOpusDecoder> _decoders = new();
    private readonly HashSet<int> _decodeErrors = new();

    // Per-peer voice state (synced via server events):
    // mute is tracked from VAD broadcasts, radio from our RADIO: signal marker.
    private readonly ConcurrentDictionary<int, bool> _clientMuted = new();
    private readonly ConcurrentDictionary<int, bool> _clientRadio = new();
    private bool _localRadio;

    private Timer? _pingTimer;
    private int _pingInterval = 25000;
    private int _lastReceiveTick = Environment.TickCount;
    // Engine.IO v3 (socket.io 2.4.x): the *client* pings every pingInterval and
    // the server answers pong (verified in engine.io@3.4.2 lib/socket.js) — the
    // server never pings on its own. So traffic only exists if we ask for it:
    // ping every 5s, and treat 15s of silence (3 missed pongs) as a dead socket.
    private const int PingPeriodMs = 5000;
    private const int NoDataAbortMs = 15000;
    private const int RetryDelayMs = 1000;
    // Local mic state, kept here so it can be re-announced after a reconnect
    // (the room restarts with a fresh connection object).
    private bool _lastMute;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    public int MyClientId => 0;

    public ServerConnection(IConnectionContext context, string roomCode, string region, string url)
    {
        _context = context; _roomCode = roomCode;
        var u = new Uri(url);
        _httpOrigin = u.Scheme + "://" + u.Host;
        // Always use EIO=3 (Engine.IO v3), matching the official BetterCrewLink
        // client (socket.io-client 2.4.0) which works on both the Cloudflare
        // official server and the EdgeOne servers. EIO=4 breaks the Cloudflare
        // server when we don't actively send the "40" connect packet.
        _wsUrl = (u.Scheme == "https" ? "wss" : "ws") + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + "/socket.io/?EIO=3&transport=websocket";
        StartConnectLoop();
    }

    void StartConnectLoop()
    {
        if (Interlocked.Exchange(ref _connecting, 1) == 1) return;
        _ = RunLoop();
    }

    async Task RunLoop()
    {
        try
        {
            while (!_disposed)
            {
                _cts?.Cancel();
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                var sws = new WebSocket(_wsUrl, _httpOrigin, token);
                _sws = sws;

                try
                {
                    InterstellarPlugin.Logger.LogInfo("[Srv] Connecting " + _wsUrl);
                    await sws.ConnectAsync();
                    _lastReceiveTick = Environment.TickCount;
                    _connected = false;
                    // Watchdog starts with the socket, not with the handshake: a
                    // server that accepts the connection and then never sends the
                    // Engine.IO open packet would otherwise hang ReceiveAsync
                    // forever. Pings still only go out once `_connected` (after
                    // the socket.io "40"), so the abort below doubles as a
                    // "handshake never completed" detector.
                    _pingTimer?.Dispose();
                    _pingTimer = new Timer(_ =>
                    {
                        if (_connected) SendRaw("2");
                        if (_connected)
                        {
                            SendHeartbeats();
                            CheckHeartbeats();
                        }
                        int silent = unchecked(Environment.TickCount - _lastReceiveTick);
                        if (silent > NoDataAbortMs)
                        {
                            InterstellarPlugin.Logger.LogWarning(
                                $"[Srv] No data for {silent}ms — forcing reconnect.");
                            try { _sws?.Abort(); } catch { }
                        }
                    }, null, PingPeriodMs, PingPeriodMs);
                    await ReadLoopSimple(sws, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    InterstellarPlugin.Logger.LogWarning("[Srv] Err: " + ex.Message);
                }
                finally
                {
                    try { sws.Dispose(); } catch { }
                    _connected = false;
                    _socketReady = false;
                    _joinIssued = false;
                    _joinPending = false;
                    _pingTimer?.Dispose();
                    // The server never broadcasts "leave"/disconnect, so peers
                    // learned on a previous socket would stay half-alive across a
                    // reconnect: VCPlayers and decoders still exist, icons look
                    // connected, but no audio flows in either direction.
                    DropPeers();
                }

                if (_disposed) break;
                try { await Task.Delay(RetryDelayMs, token); } catch { break; }
            }
        }
        finally
        {
            _connected = false;
            _pingTimer?.Dispose();
            DropPeers();
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>
    /// Forget every peer learned on the previous socket (see RunLoop's finally).
    /// Mirrors what a real "leave" broadcast from the server would have done.
    /// </summary>
    void DropPeers()
    {
        foreach (var v in _peers.Values)
        {
            _decoders.TryRemove(v.clientId, out _);
            _context.OnClientDisconnected(v.clientId);
        }
        _peers.Clear();
        _peerBeat.Clear();
        _lastRecv.Clear();
        _txAccumLen = 0;
        _unknownSignalLogged.Clear();
        _clientMuted.Clear();
        _clientRadio.Clear();
        _decodeErrors.Clear();
    }

    async Task ReadLoopSimple(WebSocket sws, CancellationToken token)
    {
        var sb = new StringBuilder();
        while (sws.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var (type, data) = await sws.ReceiveAsync();
            if (type == WebSocketMessageType.Close)
            {
                break;
            }
            sb.Append(Encoding.UTF8.GetString(data));
            HandleEngineIOMessage(sb.ToString());
            sb.Clear();
        }
    }

    void HandleEngineIOMessage(string data)
    {
        _lastReceiveTick = Environment.TickCount;
        if (string.IsNullOrEmpty(data)) return;
        switch (data[0])
        {
            case '0': HandleOpen(data.Substring(1)); break;
            case '2': SendRaw("3"); break;
            case '4': HandleSio(data.Substring(1)); break;
        }
    }

    void HandleOpen(string payload)
    {
        try
        {
            using var d = JsonDocument.Parse(payload);
            var r = d.RootElement;
            _sid = r.TryGetProperty("sid", out var s) ? s.GetString() : "";
            _pingInterval = r.TryGetProperty("pingInterval", out var pi) ? pi.GetInt32() : 25000;
            InterstellarPlugin.Logger.LogInfo($"[Srv] Open sid={_sid} pingInterval={_pingInterval}ms");
            // NOTE: Do NOT actively send the socket.io connect packet ("40") here.
            // The official socket.io-client does not send it for the default namespace;
            // it simply waits for the server to send "40". Actively sending it makes
            // the Cloudflare-hosted (socket.io v4) server close the socket right after join.
            // The server's "40" is handled in HandleSio (sioType == '0') which triggers join.
        }
        catch { }
    }

    void HandleSio(string payload)
    {
        if (payload.Length < 1) return;
        // socket.io packet type: 0=connect, 1=disconnect, 2=event, 3=ack, 4=error
        var sioType = payload[0];
        if (sioType == '0')
        {
            _connected = true;
            _socketReady = true;
            _joinIssued = false;
            _joinPending = false;
            if (_hasIds) _ = ScheduleJoinAsync();
            return;
        }
        if (sioType == '1')
        {
            _connected = false;
            return;
        }
        if (sioType == '4')
        {
            InterstellarPlugin.Logger.LogWarning("[Srv] Socket.io error packet");
            return;
        }
        // socket.io type 2 = event: 2["eventName",args...]
        if (sioType != '2' || payload.Length < 3) return;
        try
        {
            using var d = JsonDocument.Parse(payload.Substring(1));
            var arr = d.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return;
            switch (arr[0].GetString() ?? "")
            {
                case "setClient": OnSetClient(arr); break;
                case "setClients": OnSetClients(arr); break;
                case "join": OnJoin(arr); break;
                case "VAD": OnVAD(arr); break;
                case "signal": OnSignal(arr); break;
                case "new_lobbies": PublicLobbyManager.OnNewLobbies(arr[1].GetRawText()); break;
                case "update_lobby": PublicLobbyManager.OnUpdateLobby(arr[1].GetRawText()); break;
                case "remove_lobby": PublicLobbyManager.OnRemoveLobby(arr[1].GetInt32()); break;
            }
        }
        catch (Exception ex)
        {
            // A silent drop here hid broken setClients payloads entirely.
            // Cap logging — malformed signal packets would arrive at 50/s.
            if (_parseErrors < 10)
            {
                _parseErrors++;
                InterstellarPlugin.Logger.LogWarning("[Srv] Event parse failed: " + ex.Message + " raw=" +
                    (payload.Length > 200 ? payload.Substring(0, 200) : payload));
            }
        }
    }

    void OnSetClient(JsonElement arr)
    {
        if (arr.GetArrayLength() < 3) return;
        var sid = arr[1].GetString(); if (sid == null || sid == _sid) return;
        var c = arr[2];
        int pid = c.TryGetProperty("playerId", out var p) ? p.GetInt32() : 0;
        int cid = c.TryGetProperty("clientId", out var ci) ? ci.GetInt32() : 0;
        if (IsOwnClient(cid))
        {
            InterstellarPlugin.Logger.LogWarning($"[Srv] setClient sid={sid} is our own cid={cid} (stale socket) — ignoring.");
            return;
        }
        AddPeer(sid, pid, cid);
        // The joiner learns pre-existing peers only through setClient(s) — the
        // server never sends a "join" event for members already in the room.
        // Create the audio instance here as well, otherwise a peer who has not
        // spoken yet has no VCPlayer and shows the NoConnect icon until their
        // first audio frame arrives.
        _context.OnClientConnected(cid);
        _context.OnClientProfileUpdated(cid, "P" + pid, (byte)pid);
        SendRadioTo(sid);
    }

    void OnSetClients(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2) return;
        var clients = arr[1];
        if (clients.ValueKind != JsonValueKind.Object)
        {
            InterstellarPlugin.Logger.LogWarning("[Srv] setClients payload not an object: " + clients.GetRawText());
            return;
        }
        InterstellarPlugin.Logger.LogInfo("[Srv] setClients: " + clients.GetRawText());
        var seen = new HashSet<string>();
        foreach (var kv in clients.EnumerateObject())
        {
            if (kv.Name == _sid) continue;
            var c = kv.Value;
            if (c.ValueKind != JsonValueKind.Object) continue;
            int pid = c.TryGetProperty("playerId", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            int cid = c.TryGetProperty("clientId", out var ci) && ci.ValueKind == JsonValueKind.Number ? ci.GetInt32() : 0;
            if (IsOwnClient(cid)) continue;
            seen.Add(kv.Name);
            AddPeer(kv.Name, pid, cid);
            _context.OnClientConnected(cid);
            _context.OnClientProfileUpdated(cid, "P" + pid, (byte)pid);
            SendRadioTo(kv.Name);
        }
        // The roster is authoritative for the room as it exists right now.
        // Anything we still hold that is not in it came from a socket the server
        // has already discarded — dropping it stops us sending half our frames
        // into a dead sid and lets the live one be re-learned cleanly.
        foreach (var kv in _peers)
            if (!seen.Contains(kv.Key))
                RemovePeer(kv.Key);
    }

    void RemovePeer(string sid)
    {
        if (!_peers.TryRemove(sid, out var v)) return;
        InterstellarPlugin.Logger.LogInfo($"[Srv] Dropping stale peer sid={sid} cid={v.clientId} (not in roster).");
        foreach (var kv in _peers)
            if (kv.Value.clientId == v.clientId) return; // same client, another sid
        _peerBeat.TryRemove(v.clientId, out _);
        _lastRecv.TryRemove(v.clientId, out _);
        _decoders.TryRemove(v.clientId, out _);
        _context.OnClientDisconnected(v.clientId);
    }

    void OnJoin(JsonElement arr)
    {
        if (arr.GetArrayLength() < 3) return;
        var sid = arr[1].GetString(); if (sid == null || sid == _sid) return;
        var c = arr[2];
        int pid = c.TryGetProperty("playerId", out var p) ? p.GetInt32() : 0;
        int cid = c.TryGetProperty("clientId", out var ci) ? ci.GetInt32() : 0;
        if (IsOwnClient(cid))
        {
            InterstellarPlugin.Logger.LogWarning($"[Srv] join sid={sid} is our own cid={cid} (stale socket) — ignoring.");
            return;
        }
        AddPeer(sid, pid, cid);
        _context.OnClientConnected(cid);
        _context.OnClientProfileUpdated(cid, "P" + pid, (byte)pid);
        // A member who joins now has never heard our VAD — the server only
        // relays VAD packets emitted after they arrived, so announce it here
        // (the broadcast reaches the whole room, which just re-syncs everyone).
        Emit("VAD", !_lastMute);
        SendRadioTo(sid);
    }

    void OnVAD(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2) return;
        var j = arr[1];
        bool a = j.TryGetProperty("activity", out var av) && av.GetBoolean();
        int cid = 0;
        if (j.TryGetProperty("client", out var cl) && cl.TryGetProperty("clientId", out var ci)) cid = ci.GetInt32();
        _clientMuted[cid] = !a;
        _context.OnReceiveMuteStatus(cid, !a, _clientRadio.TryGetValue(cid, out var r) && r);
    }

    void OnSignal(JsonElement arr)
    {
        if (arr.GetArrayLength() < 2) return;
        var j = arr[1];
        if (!j.TryGetProperty("from", out var f)) return;
        var from = f.GetString(); if (from == null || from == _sid) return;
        // Resolve sender via the sid-keyed peer map. The old reverse search
        // through _clientToSocket used 0 as a "not found" sentinel, which
        // silently discarded every frame from a peer whose Among Us clientId
        // is 0 (one-way audio: peer created the room, we joined later).
        if (!_peers.TryGetValue(from, out var peer))
        {
            if (_unknownSignalLogged.Add(from))
                InterstellarPlugin.Logger.LogWarning(
                    $"[Srv] Signal from unknown sid={from} (peers={_peers.Count}) — dropping.");
            TryResync($"unknown sid={from}");
            return;
        }
        int cid = peer.clientId;
        NotePeerAlive(cid);
        if (!j.TryGetProperty("data", out var raw) || raw.ValueKind != JsonValueKind.String) return;
        var dataStr = raw.GetString();
        if (dataStr == null) return;
        // Liveness marker (no audio, no VAD — proves only that this sid relays).
        if (dataStr.StartsWith("HB:", StringComparison.Ordinal)) return;
        // Radio-state marker: "RADIO:1" / "RADIO:0" (not Opus audio)
        if (dataStr.StartsWith("RADIO:", StringComparison.Ordinal))
        {
            bool radio = dataStr.EndsWith("1", StringComparison.Ordinal);
            _clientRadio[cid] = radio;
            _context.OnReceiveMuteStatus(cid, _clientMuted.TryGetValue(cid, out var m) && m, radio);
            return;
        }
        // Host settings marker: "HOST:" + base64
        if (dataStr.StartsWith("HOST:", StringComparison.Ordinal))
        {
            try { _context.OnHostSettingsReceived(Convert.FromBase64String(dataStr.Substring(5))); } catch { }
            return;
        }
        // Custom message marker: "MSG:" + base64
        if (dataStr.StartsWith("MSG:", StringComparison.Ordinal))
        {
            try { _context.OnCustomMessageReceived(Convert.FromBase64String(dataStr.Substring(4))); } catch { }
            return;
        }
        try { DecodeOpus(cid, Convert.FromBase64String(dataStr)); } catch { }
    }

    void DecodeOpus(int clientId, byte[] opus)
    {
        try
        {
            if (!_decoders.TryGetValue(clientId, out var dec))
            { dec = Concentus.OpusCodecFactory.CreateDecoder(48000, 1); _decoders[clientId] = dec; }
            var buf = new float[2048];
            int n = dec.Decode(opus, buf, buf.Length);
            _context.OnAudioFrameReceived(clientId, buf, n);
        }
        catch { if (_decodeErrors.Add(clientId)) InterstellarPlugin.Logger.LogWarning("[Srv] Dec " + clientId); }
    }

    async void SendRaw(string data)
    {
        var sws = _sws; if (sws?.State != WebSocketState.Open) return;
        try
        {
            await _sendLock.WaitAsync();
            await sws.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text);
        }
        catch { }
        finally { try { _sendLock.Release(); } catch { } }
    }

    void Emit(string ev, params object[] args)
    {
        var sb = new StringBuilder();
        sb.Append("42[\"").Append(ev).Append('"');
        foreach (var a in args)
        {
            sb.Append(',');
            if (a is string s) sb.Append(JsonSerializer.Serialize(s));
            else if (a is bool b) sb.Append(b ? "true" : "false");
            else if (a is int i) sb.Append(i);
            else if (a is float f) sb.Append(f.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (a == null) sb.Append("null");
            else sb.Append(JsonSerializer.Serialize(a));
        }
        sb.Append(']');
        SendRaw(sb.ToString());
    }

    async Task ScheduleJoinAsync()
    {
        if (_disposed || _joinIssued || _joinPending || !_connected || !_socketReady || !_hasIds || string.IsNullOrWhiteSpace(_roomCode)) return;
        _joinPending = true;
        try
        {
            await Task.Delay(300);
            if (_disposed || _joinIssued || !_connected || !_socketReady || !_hasIds || string.IsNullOrWhiteSpace(_roomCode)) return;
            DoJoin();
        }
        finally
        {
            _joinPending = false;
        }
    }

    void DoJoin()
    {
        if (_disposed || _joinIssued || !_connected || !_socketReady || !_hasIds || string.IsNullOrWhiteSpace(_roomCode)) return;
        _joinIssued = true;
        InterstellarPlugin.Logger.LogInfo($"[Srv] join room={_roomCode} pid={_localPlayerId} cid={_localClientId}");
        Emit("id", _localPlayerId, _localClientId);
        Emit("join", _roomCode, _localPlayerId, _localClientId, false);
        // Re-announce local state. VAD/RADIO packets emitted while the socket was
        // down are dropped (SendRaw no-ops on a closed socket), and room members
        // never hear a VAD that was sent before they joined — so a refresh used
        // to leave the mic state out of sync on both ends.
        Emit("VAD", !_lastMute);
        if (_localRadio) BroadcastRadio(true);
    }

    void BroadcastRadio(bool radio)
    {
        var marker = radio ? "RADIO:1" : "RADIO:0";
        foreach (var (sid, _) in _peers)
            Emit("signal", new { to = sid, data = marker });
    }

    void SendRadioTo(string sid)
    {
        if (!_localRadio) return;
        Emit("signal", new { to = sid, data = "RADIO:1" });
    }

    /// <summary>True when this entry is a stale copy of ourselves (the server keeps
    /// a dead socket's record until it notices the disconnect, so setClients can
    /// hand back our previous sid).</summary>
    bool IsOwnClient(int cid) => _hasIds && cid == _localClientId;

    /// <summary>Register a peer, forgetting any previous sid that carries the same
    /// clientId. The server never broadcasts leave, so a client that reconnects
    /// would otherwise stay registered under its dead socket as well — we would
    /// keep sending half our frames into a socket nobody reads.</summary>
    void AddPeer(string sid, int pid, int cid)
    {
        foreach (var kv in _peers)
            if (kv.Key != sid && kv.Value.clientId == cid)
                _peers.TryRemove(kv.Key, out _);
        _peers[sid] = (pid, cid);
        // Keep an existing beat timer: a peer that reconnected under a new sid
        // is the same live client, and resetting would delay staleness detection.
        _peerBeat.TryAdd(cid, (Environment.TickCount, Environment.TickCount));
        // …but reception must restart: a roster entry proves we know *of* them,
        // not that a single packet has come back from them.
        _lastRecv.TryRemove(cid, out _);
    }

    /// <summary>Refresh a peer's liveness stamp — any packet from them (audio,
    /// VAD, radio marker, heartbeat) proves the relay path works.</summary>
    void NotePeerAlive(int cid)
    {
        int now = Environment.TickCount;
        _peerBeat.AddOrUpdate(cid,
            (now, now),
            (_, old) => (old.firstSeen, now));
        _lastRecv[cid] = now;
        _resyncBackoffMs = ResyncMinMs;
    }

    /// <summary>True once at least one packet (audio, VAD, radio marker,
    /// heartbeat) has ever arrived from this peer on the current connection.
    /// A peer who is simply not talking sends no audio but still heartbeats —
    /// so this, not "is audio playing", is what tells a dead relay from a
    /// quiet one.</summary>
    public bool PeerHasReceived(int clientId) => _lastRecv.ContainsKey(clientId);

    void SendHeartbeats()
    {
        if (_peers.IsEmpty) return;
        foreach (var (sid, _) in _peers)
            Emit("signal", new { to = sid, data = "HB:" });
    }

    /// <summary>If a peer we are sending to has not produced a single packet in
    /// 15s (they are alive — they would be talking or heartbeating), our copy of
    /// their sid is stale. Re-request the roster instead of staying silent.</summary>
    void CheckHeartbeats()
    {
        if (_peers.IsEmpty) return;
        int now = Environment.TickCount;
        foreach (var kv in _peers)
        {
            int cid = kv.Value.clientId;
            if (!_peerBeat.TryGetValue(cid, out var b))
            {
                _peerBeat[cid] = (now, now);
                continue;
            }
            if (unchecked(now - b.lastBeat) > HeartbeatTimeoutMs)
            {
                TryResync($"no heartbeat from cid={cid} sid={kv.Key}");
                return;
            }
        }
    }

    /// <summary>Re-join the room to get a fresh setClients. The server sends each
    /// join/setClient broadcast exactly once, so a missed one otherwise leaves
    /// that stream dropped forever. Rate-limited with a backoff, reset as soon
    /// as any peer answers.</summary>
    void TryResync(string reason)
    {
        if (!_hasIds || !_connected) return;
        int now = Environment.TickCount;
        if (unchecked(now - _lastResyncTick) < _resyncBackoffMs) return;
        _lastResyncTick = now;
        InterstellarPlugin.Logger.LogWarning(
            $"[Srv] Resyncing ({reason}) — peers={_peers.Count}.");
        Emit("id", _localPlayerId, _localClientId);
        Emit("join", _roomCode, _localPlayerId, _localClientId, false);
        _resyncBackoffMs = Math.Min(_resyncBackoffMs * 2, ResyncMaxMs);
    }

    /// <summary>Forced version used as the cheap first response to "no audio is
    /// coming through" — re-request the peer list before tearing the room down.</summary>
    public void ResyncPeers(string reason)
    {
        _resyncBackoffMs = ResyncMinMs;
        TryResync(reason);
    }

    public void UpdateProfile(string name, byte pid, int clientId)
    {
        _localPlayerId = pid;
        _localClientId = clientId;
        _hasIds = true;
        Emit("id", pid, clientId);
        if (_connected && _socketReady) _ = ScheduleJoinAsync();
    }
    public void UpdateMuteStatus(bool mute, bool radio = false)
    {
        _lastMute = mute;
        Emit("VAD", !mute);
        // Broadcast impostor-radio state to all peers via the signal channel.
        // (Official BCL does this over WebRTC data channels; here we mirror it
        // through the server relay so radio mode works cross-player.)
        if (_localRadio != radio)
        {
            _localRadio = radio;
            BroadcastRadio(radio);
        }
    }

    /// <summary>
    /// Opus only accepts specific frame lengths at 48kHz — 480 (10ms), 960
    /// (20ms), 1920 (40ms) or 2880 (60ms). Microphone backends sometimes hand
    /// us anything else (NAudio buffers merging into 80ms under load, etc.) and
    /// Concentus then throws; the old `catch {}` swallowed that as a completely
    /// silent dropped frame, which is one way to lose syllables on the wire.
    /// Input is now normalized into valid frames with the remainder carried
    /// over to the next call, so standard sizes still go out immediately.
    /// </summary>
    public void SendAudio(float[] buf, int len, double ms)
    {
        if (!_connected || _peers.IsEmpty || len <= 0) return;
        try
        {
            EnsureEncoder();
            int i = 0;
            while (i < len)
            {
                int space = _txAccum.Length - _txAccumLen;
                if (space == 0) { EmitTxFrames(); space = _txAccum.Length - _txAccumLen; }
                int n = Math.Min(space, len - i);
                Array.Copy(buf, i, _txAccum, _txAccumLen, n);
                _txAccumLen += n;
                i += n;
                EmitTxFrames();
            }
        }
        catch (Exception ex)
        {
            if (++_txEncodeErrors <= 5)
                InterstellarPlugin.Logger.LogWarning("[Srv] TX failed: " + ex.Message);
        }
    }

    void EmitTxFrames()
    {
        while (_txAccumLen >= 480)
        {
            int frame = _txAccumLen >= 2880 ? 2880
                      : _txAccumLen >= 1920 ? 1920
                      : _txAccumLen >= 960 ? 960
                      : 480;
            try
            {
                int n = _encoder!.Encode(_txAccum, frame, _encBuf, _encBuf.Length);
                if (n > 2)
                {
                    var opus = new byte[n];
                    Buffer.BlockCopy(_encBuf, 0, opus, 0, n);
                    var b64 = Convert.ToBase64String(opus);
                    foreach (var (sid, _) in _peers)
                        Emit("signal", new { to = sid, data = b64 });
                    Interlocked.Increment(ref _txFrames);
                }
            }
            catch (Exception ex)
            {
                if (++_txEncodeErrors <= 5)
                    InterstellarPlugin.Logger.LogWarning(
                        $"[Srv] Opus encode failed ({frame} samples): {ex.Message}");
            }
            // Always advance, even after a failure — a wedged accumulator would
            // stop all further voice permanently.
            int rem = _txAccumLen - frame;
            if (rem > 0) Array.Copy(_txAccum, frame, _txAccum, 0, rem);
            _txAccumLen = rem;
        }
    }

    Concentus.IOpusEncoder? _encoder; byte[] _encBuf = new byte[2048];
    void EnsureEncoder()
    {
        if (_encoder != null) return;
        _encoder = Concentus.OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        _encoder.Bitrate = 64000; _encoder.UseVBR = true; _encoder.UseInbandFEC = true;
    }

    public async Task PublishLobby(string code, PublicLobbyManager.LobbyInfo info)
    { Emit("lobby", code, new { title = info.title, host = info.host, current_players = info.current_players, max_players = info.max_players, language = info.language, mods = info.mods, isPublic = true, isPublic2 = true, server = info.server, gameState = info.gameState }); await Task.CompletedTask; }
    public async Task RemoveLobby(string code) { Emit("remove_lobby", code); await Task.CompletedTask; }
    public async Task JoinLobby(int id, Action<int, string, string> cb) { cb(1, "", ""); await Task.CompletedTask; }
    public async Task WatchLobbyBrowser(bool w)
    { if (w) { Emit("lobbybrowser", true); PublicLobbyManager.StartWatching(); } else { Emit("lobbybrowser", false); PublicLobbyManager.StopWatching(); } await Task.CompletedTask; }

    void IConnectionContext.OnAudioFrameReceived(int c, float[] s, int n) => _context.OnAudioFrameReceived(c, s, n);
    void IConnectionContext.OnClientConnected(int c) => _context.OnClientConnected(c);
    void IConnectionContext.OnClientDisconnected(int c) { _decoders.TryRemove(c, out _); _context.OnClientDisconnected(c); }
    void IConnectionContext.OnClientProfileUpdated(int c, string n, byte p) => _context.OnClientProfileUpdated(c, n, p);
    void IConnectionContext.OnReceiveMuteStatus(int c, bool m, bool r) => _context.OnReceiveMuteStatus(c, m, r);
    void IConnectionContext.OnCustomMessageReceived(byte[] m) => _context.OnCustomMessageReceived(m);
    void IConnectionContext.OnHostSettingsReceived(byte[] s) => _context.OnHostSettingsReceived(s);
    void IConnectionContext.OnServerInfoReceived(int o, int t, string u) => _context.OnServerInfoReceived(o, t, u);

    // Relay host settings / custom messages to every peer via the signal channel,
    // mirroring how official BCL sends them over WebRTC data channels.
    void BroadcastMarker(string prefix, byte[] payload)
    {
        if (!_connected || _peers.IsEmpty || payload == null || payload.Length == 0) return;
        var b64 = Convert.ToBase64String(payload);
        var data = prefix + b64;
        foreach (var (sid, _) in _peers)
            Emit("signal", new { to = sid, data });
    }

    public void SendCustomMessage(byte[] m) => BroadcastMarker("MSG:", m);
    public void SendHostSettings(byte[] s) => BroadcastMarker("HOST:", s);

    public void Disconnect()
    {
        _disposed = true;
        _pingTimer?.Dispose();
        _cts?.Cancel();
        var sws = _sws; _sws = null;
        if (sws == null) return;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (sws.State == WebSocketState.Open)
                    sws.SendAsync(Encoding.UTF8.GetBytes("42[\"leave\"]"), WebSocketMessageType.Text).Wait(2000);
            }
            catch { }
            try { sws.CloseAsync().Wait(3000); } catch { }
            try { sws.Dispose(); } catch { }
        });
        InterstellarPlugin.Logger.LogInfo("[Srv] Disconnected");
    }
    public void Dispose() { Disconnect(); }
}