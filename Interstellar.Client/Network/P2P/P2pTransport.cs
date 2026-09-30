using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Interstellar.Voice;

namespace Interstellar.Network.P2P;

/// <summary>
/// Pure-C# P2P media transport.
///
/// Design (no relay of any kind, by request):
///   • one UDP socket per voice session — all peers share it, so a single NAT
///     mapping carries every media flow;
///   • candidates = local unicast addresses + server-reflexive addresses learned
///     from a configurable pile of STUN servers (whichever answers wins);
///   • peer candidates + an ephemeral ECDH public key arrive over the voice
///     server's existing "signal" event (server only ever relays public keys);
///   • connectivity checks are plain STUN binding requests fired simultaneously
///     at every candidate from both sides — classic simultaneous-open hole
///     punching. The path is validated when a binding response comes back;
///   • every media frame is sealed with AES-256-GCM;
///   • if no path opens, this class sends nothing for that peer — routing is
///     the ServerConnection's job: Auto mode falls back to the server's
///     "signal" relay for peers without a validated path, P2P mode stays
///     silent, Relay mode never asks us to send media at all (it is only kept
///     alive so a strict-P2P peer can still reach us).
///
/// The receive loop is a dedicated long-running thread; all session state is
/// guarded by a single lock; callbacks (<see cref="_onAudio"/>) fire outside it.
/// </summary>
internal sealed class P2pTransport : IDisposable
{
    public delegate void AudioHandler(int clientId, byte[] opus, uint counter);
    public delegate void SignalHandler(string sid, string json);

    // ── timing ──────────────────────────────────────────────────────────────
    private const int TickIntervalMs = 100;
    private const int ProbeIntervalMs = 100;
    private const int PunchWindowMs = 7000;
    private const int PunchRetryMinBackoffMs = 3000;
    private const int PunchRetryMaxBackoffMs = 30000;
    private const int KeepAliveMs = 10000;
    private const int DeadMs = 45000;
    private const int HelloResendMs = 2000;
    // 64 × 2s ≈ 128s of HELLO coverage while a path is missing: the budget now
    // covers the whole not-connected phase (see the resend in Tick), not just
    // the wait for their first HELLO.
    private const int HelloMaxCount = 64;
    private const int PendingTtlMs = 6000;
    private const int SrflxTtlMs = 120000;
    private const int StunTimeoutMs = 3000;
    private const int StunAttempts = 2;

    // ── limits ──────────────────────────────────────────────────────────────
    private const int MaxTargets = 8;
    private const int MaxOrphans = 16;
    private const int MaxCandidates = 12;

    private readonly AudioHandler _onAudio;
    private readonly SignalHandler _signal;
    private readonly string[] _stunServers;

    private readonly object _lock = new();

    private UdpClient? _udp;
    private int _port;
    private volatile bool _running;
    private bool _disposed;
    private Timer? _tickTimer;
    private Task? _receiveTask;

    private readonly Dictionary<string, P2pSession> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, P2pSession> _byPeerDir = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, P2pSession> _byFlow = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>Outbound signalling, drained with the lock released. Calling the
    /// websocket emitter from inside the lock would be a re-entrancy hazard the
    /// moment that callback ever does anything synchronous.</summary>
    private readonly ConcurrentQueue<(string Sid, string Json)> _outSignals = new();

    /// <summary>Frames that arrived before their sender's HELLO (UDP beats the
    /// websocket signalling race). Bounded, flushed as soon as a peer registers.</summary>
    private readonly List<(byte[] Pkt, IPEndPoint From, long At)> _orphans = new();

    private readonly List<IPEndPoint> _local = new();
    private readonly List<IPEndPoint> _srflx = new();
    private readonly HashSet<string> _srflxKeys = new(StringComparer.Ordinal);
    private long _srflxAt;
    private long _localAt;
    private volatile bool _gathering;
    private int _errors;
    /// <summary>Inbound STUN binding requests seen on this socket, attributed or
    /// not — paired with the per-session rx in the punch log to tell "packets
    /// never arrive" from "arrive but fail attribution".</summary>
    private volatile int _stunRxAny;

    private sealed class Pending
    {
        public IPEndPoint? Dest;
        public TaskCompletionSource<IPEndPoint>? Tcs;
        public P2pSession? Session;
        public long SentAt;
    }

    private sealed class HelloMsg
    {
        public string? t { get; set; }
        public string? d { get; set; }
        public string? k { get; set; }
        public string[]? c { get; set; }
        public int g { get; set; }
    }

    public P2pTransport(AudioHandler onAudio, SignalHandler signal)
    {
        _onAudio = onAudio;
        _signal = signal;
        _stunServers = LoadStunServers();
    }

    // ── lifecycle ───────────────────────────────────────────────────────────

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _udp != null) return;
            try
            {
                _udp = new UdpClient(0);
                _udp.Client.Blocking = true;
                _udp.Client.ReceiveBufferSize = 1 << 20;
                _udp.Client.SendBufferSize = 1 << 20;
                try
                {
                    // Windows reports ICMP port-unreachable as a SocketException
                    // out of Receive(), which would otherwise burn one iteration
                    // of the receive loop per dead STUN server or departed peer.
                    const int SioUdpConnreset = -1744830452;
                    _udp.Client.IOControl(SioUdpConnreset, new byte[] { 0 }, null!);
                }
                catch { /* non-Windows / restricted socket — harmless */ }
                _port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
            }
            catch (Exception ex)
            {
                Logger("[P2P] UDP socket failed: " + ex.Message);
                _udp = null;
                return;
            }
            _running = true;
        }

        RefreshLocalCandidates();
        _localAt = Environment.TickCount64;
        _receiveTask = Task.Factory.StartNew(ReceiveLoop, TaskCreationOptions.LongRunning);
        _tickTimer = new Timer(_ => SafeTick(), null, TickIntervalMs, TickIntervalMs);
        _ = RefreshSrflxAsync();
        Logger("[P2P] started port=" + _port + " stun=" + _stunServers.Length);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }

        try { _tickTimer?.Dispose(); } catch { }
        _tickTimer = null;

        UdpClient? udp;
        lock (_lock)
        {
            udp = _udp;
            _udp = null;
        }
        try { udp?.Close(); } catch { }
        try { udp?.Dispose(); } catch { }

        P2pSession[] sessions;
        lock (_lock)
        {
            sessions = _sessions.Values.ToArray();
            _sessions.Clear();
            _byPeerDir.Clear();
            _byFlow.Clear();
            _pending.Clear();
            _orphans.Clear();
        }
        foreach (var s in sessions) { try { s.Dispose(); } catch { } }
    }

    // ── peer roster (driven from ServerConnection) ──────────────────────────

    public void AddPeer(string sid, int clientId)
    {
        if (_disposed || string.IsNullOrEmpty(sid)) return;
        try
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(sid, out var existing))
                {
                    if (existing.ClientId == clientId) return;
                    UnregisterLocked(existing);
                    existing.Dispose();
                }
                var s = new P2pSession(sid, clientId);
                _sessions[sid] = s;
                SendHelloLocked(s);
            }
        }
        finally { FlushSignals(); }
    }

    public void RemovePeer(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return;
        P2pSession? s;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(sid, out s)) return;
            _sessions.Remove(sid);
            UnregisterLocked(s);
        }
        s?.Dispose();
    }

    public void Clear()
    {
        P2pSession[] all;
        lock (_lock)
        {
            all = _sessions.Values.ToArray();
            _sessions.Clear();
            _byPeerDir.Clear();
            _byFlow.Clear();
            _orphans.Clear();
            _pending.Clear();
        }
        foreach (var s in all) { try { s.Dispose(); } catch { } }
    }

    /// <summary>Re-announce ourselves (websocket reconnect, fresh STUN results).</summary>
    public void ResendHellos()
    {
        try
        {
            lock (_lock)
            {
                foreach (var s in _sessions.Values)
                {
                    if (s.Disposed) continue;
                    s.HelloCount = 0;
                    SendHelloLocked(s);
                }
            }
        }
        finally { FlushSignals(); }
    }

    // ── signalling in ───────────────────────────────────────────────────────

    /// <summary>Called from the websocket receive path with the decoded JSON of a
    /// "P2P:" signal message.</summary>
    public void HandleSignal(string fromSid, string json)
    {
        if (_disposed || string.IsNullOrEmpty(fromSid)) return;
        HelloMsg? msg;
        try { msg = JsonSerializer.Deserialize<HelloMsg>(json); }
        catch { return; }
        if (msg == null || !string.Equals(msg.t, "hi", StringComparison.Ordinal)) return;

        var dirHex = msg.d;
        if (string.IsNullOrEmpty(dirHex) || string.IsNullOrEmpty(msg.k)) return;
        dirHex = dirHex!.ToLowerInvariant();
        byte[]? dir = FromHex(dirHex);
        byte[]? pub;
        try { pub = Convert.FromBase64String(msg.k!); }
        catch { return; }
        if (dir == null || dir.Length != P2pCrypto.DirIdLen || pub.Length != 64) return;

        try
        {
            lock (_lock)
            {
            if (_disposed) return;
            if (!_sessions.TryGetValue(fromSid, out var s) || s.Disposed) return;

            // Echo guard — our own HELLO reflected back at us.
            if (dirHex == ToHex(s.Crypto.DirId)) return;

            if (s.HasPeerHello && s.PeerDirHex == dirHex)
            {
                // Idempotent re-HELLO: only the candidate list may have changed.
                ApplyCandidatesLocked(s, msg.c);
                return;
            }

            if (s.HasPeerHello)
            {
                // Peer restarted with new key material — drop the old identity.
                UnregisterPeerLocked(s, s.PeerDirHex);
                s.ResetPeerIdentity();
            }

            if (!s.Crypto.TryDerive(pub, dir)) { Logger("[P2P] key derive failed for " + s.Sid); return; }

            s.PeerDirId = dir;
            s.PeerDirHex = dirHex;
            s.PeerPublicKey = pub;
            s.HasPeerHello = true;
            s.Generation = msg.g;

            _byPeerDir[dirHex] = s;
            _byFlow[ToFlowId(dir)] = s;

            ApplyCandidatesLocked(s, msg.c);
            FlushOrphansLocked(s);

            // Always answer, even if we already pushed one (the peer may have
            // dropped it while their socket was still connecting).
            SendHelloLocked(s);

            if (!s.Connected && s.PunchRoundEndsAt == 0) StartPunchLocked(s, reason: "hello");
            }
        }
        finally { FlushSignals(); }
    }

    // ── media out ───────────────────────────────────────────────────────────

    /// <summary>Seal one encoded Opus frame and ship it to every peer that has a
    /// validated path. A peer without a path receives nothing — by design.</summary>
    public void SendAll(byte[] opus)
    {
        if (opus == null || opus.Length == 0) return;
        lock (_lock)
        {
            if (_disposed) return;
            var udp = _udp;
            if (udp == null || _sessions.Count == 0) return;

            foreach (var s in _sessions.Values)
            {
                if (s.Disposed || !s.Connected || s.Remote == null) continue;
                var pkt = s.Crypto.Seal(opus);
                if (pkt == null) continue;
                try
                {
                    udp.Send(pkt, pkt.Length, s.Remote);
                    if (s.MediaTx++ == 0)
                        Logger("[P2P] media tx cid=" + s.ClientId + " sid=" + s.Sid + " (first frame)");
                }
                catch { /* transient — keepalives will notice a dead path */ }
            }
        }
    }

    /// <summary>True when a validated UDP path exists for this peer's signalling
    /// socket id. Used by the Auto transport mode to decide, per peer, whether the
    /// frame goes out directly or falls back to the server relay.</summary>
    public bool IsConnectedTo(string sid)
    {
        if (string.IsNullOrEmpty(sid)) return false;
        lock (_lock)
            return _sessions.TryGetValue(sid, out var s) && s != null && !s.Disposed && s.Connected;
    }

    /// <summary>Seal one frame and ship it to a single peer over the validated path.
    /// Returns false when there is no path (caller may then relay it instead);
    /// a socket-level send failure also reports false so the frame is not lost
    /// silently while the path recovers.</summary>
    public bool TrySendTo(string sid, byte[] opus)
    {
        if (string.IsNullOrEmpty(sid) || opus == null || opus.Length == 0) return false;
        lock (_lock)
        {
            if (_disposed) return false;
            var udp = _udp;
            if (udp == null) return false;
            if (!_sessions.TryGetValue(sid, out var s) || s == null || s.Disposed
                || !s.Connected || s.Remote == null) return false;
            var pkt = s.Crypto.Seal(opus);
            if (pkt == null) return false;
            try { udp.Send(pkt, pkt.Length, s.Remote); return true; }
            catch { return false; } // transient: caller relays this frame instead
        }
    }

    public int ConnectedCount()
    {
        lock (_lock) return _sessions.Values.Count(s => s.Connected);
    }

    public string Describe()
    {
        lock (_lock)
        {
            int conn = _sessions.Values.Count(s => s.Connected);
            return "p2p peers=" + _sessions.Count + " connected=" + conn +
                   " punching=" + (_sessions.Count - conn) +
                   " srflx=" + _srflx.Count + " local=" + _local.Count + " port=" + _port;
        }
    }

    // ── receive path ────────────────────────────────────────────────────────

    private void ReceiveLoop()
    {
        while (_running)
        {
            UdpClient? udp;
            lock (_lock) udp = _udp;
            if (udp == null) break;

            var from = new IPEndPoint(IPAddress.Any, 0);
            byte[] data;
            try { data = udp.Receive(ref from); }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { if (!_running) break; continue; }
            catch { if (!_running) break; continue; }

            try { HandleDatagram(data, from); }
            catch (Exception ex)
            {
                if (++_errors <= 5) Logger("[P2P] recv error: " + ex.Message);
            }
        }
    }

    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        if (StunMessage.IsStun(data)) { HandleStun(data, from); return; }
        if (data.Length >= P2pCrypto.HeaderLen + P2pCrypto.TagLen && data[0] == P2pCrypto.MediaMagic)
            HandleMedia(data, from);
    }

    private void HandleStun(byte[] data, IPEndPoint from)
    {
        ushort type = StunMessage.MessageType(data);

        if (type == StunMessage.BindingSuccess || type == StunMessage.BindingError)
        {
            if (!StunMessage.TryGetTxId(data, out var txId)) return;
            var key = ToHex(txId);

            Pending? p;
            lock (_lock)
            {
                if (!_pending.TryGetValue(key, out p)) return;
                _pending.Remove(key);
            }

            if (p.Tcs != null)
            {
                // STUN server query: XOR-MAPPED-ADDRESS is *our* address — this is
                // where the server-reflexive candidate comes from.
                if (type == StunMessage.BindingSuccess &&
                    StunMessage.TryParseBindingResponse(data, txId, out var mapped))
                    p.Tcs.TrySetResult(mapped);
                else
                    p.Tcs.TrySetResult(null!);
                return;
            }

            if (p.Session == null || p.Session.Disposed) return;
            // Punch response: the *source* of this packet is the peer's address
            // (XOR-MAPPED here would be our own address, so we ignore it).
            if (type == StunMessage.BindingSuccess) OnPathValidated(p.Session, from);
            return;
        }

        if (type != StunMessage.BindingRequest) return;
        _stunRxAny++;

        if (!StunMessage.TryGetTxId(data, out var reqTx)) return;
        if (!StunMessage.TryGetUsername(data, out var username)) return;

        // USERNAME = <sender dirId hex>:<their view of us> — lets us attribute the
        // probe to a session even though nothing has been validated yet.
        var senderHex = username.Split(':')[0].ToLowerInvariant();
        byte[]? senderDir = FromHex(senderHex);

        P2pSession? s;
        lock (_lock)
        {
            if (senderDir == null || !_byPeerDir.TryGetValue(senderHex, out s)) return;
            if (s.Disposed) return;
            s.LastInbound = Environment.TickCount64;
            if (s.StunRx++ == 0)
                Logger("[P2P] rx probe cid=" + s.ClientId + " sid=" + s.Sid +
                       " from=" + from + " (first)");
            if (!s.Connected)
            {
                // Peer-reflexive: their probe proves this address is live even when
                // it is not the one they advertised (their NAT picked a different
                // mapping for us than for the STUN server).
                if (!s.Learned.Contains(from)) s.Learned.Add(from);
                if (s.Targets.Count < MaxTargets) s.AddTarget(from);
            }
        }

        byte[] response;
        try { response = StunMessage.BuildBindingResponse(reqTx, from); }
        catch { return; }

        UdpClient? udp;
        lock (_lock) udp = _udp;
        try { udp?.Send(response, response.Length, from); }
        catch { /* path may not exist yet */ }
    }

    private void HandleMedia(byte[] data, IPEndPoint from)
    {
        uint flow = (uint)((data[2] << 24) | (data[3] << 16) | (data[4] << 8) | data[5]);
        P2pSession? s;
        long now = Environment.TickCount64;

        lock (_lock)
        {
            if (_disposed) return;
            if (!_byFlow.TryGetValue(flow, out s))
            {
                // Their HELLO is still in flight over the websocket. Hold briefly
                // and retry once it lands.
                if (_orphans.Count < MaxOrphans)
                    _orphans.Add((data, from, now));
                return;
            }
            if (s.Disposed) return;
        }

        if (!s.Crypto.TryOpen(data, out var payload, out uint counter)) return;

        // An authenticated frame proves the reverse path works — adopt it as the
        // media address immediately rather than waiting for our own probe reply.
        lock (_lock)
        {
            if (s.Disposed || _disposed) return;
            if (!s.Connected || !EqualsAddress(s.Remote, from))
            {
                AdoptPathLocked(s, from, reason: "authenticated frame");
            }
            s.LastInbound = now;
            if (s.MediaFrames++ == 0)
                Logger("[P2P] media rx cid=" + s.ClientId + " sid=" + s.Sid + " (first frame)");
        }

        try { _onAudio(s.ClientId, payload, counter); }
        catch { /* decoder must never take the receive loop down */ }
    }

    private void OnPathValidated(P2pSession s, IPEndPoint from)
    {
        lock (_lock)
        {
            if (s.Disposed || _disposed) return;
            AdoptPathLocked(s, from, reason: "binding response");
            s.LastInbound = Environment.TickCount64;
        }
    }

    private void AdoptPathLocked(P2pSession s, IPEndPoint from, string reason)
    {
        bool changed = !s.Connected || !EqualsAddress(s.Remote, from);
        s.Connected = true;
        s.Remote = from;
        s.LastOutbound = Environment.TickCount64;
        if (changed)
            Logger("[P2P] connected cid=" + s.ClientId + " sid=" + s.Sid + " via " + from + " (" + reason + ")");
    }

    // ── punch scheduling ────────────────────────────────────────────────────

    private void StartPunchLocked(P2pSession s, string reason)
    {
        if (s.Targets.Count == 0) return;
        long now = Environment.TickCount64;
        s.Connected = false;
        s.Remote = null;
        s.PunchRoundEndsAt = now + PunchWindowMs;
        s.NextProbeAt = now;
        if (s.PunchRound == 0 || reason == "hello")
            Logger("[P2P] punching cid=" + s.ClientId + " targets=" + s.Targets.Count +
                   " (" + reason + ") " + string.Join(" ", s.Targets) +
                   " | me=" + string.Join(" ", CollectCandidateStringsLocked()));
    }

    private void SafeTick()
    {
        try { Tick(); }
        catch (Exception ex)
        {
            if (++_errors <= 5) Logger("[P2P] tick error: " + ex.Message);
        }
    }

    private void Tick()
    {
        if (_disposed || !_running) return;
        long now = Environment.TickCount64;

        if (!_gathering && now - _srflxAt > SrflxTtlMs) _ = RefreshSrflxAsync();

        if (now - _localAt > SrflxTtlMs)
        {
            _localAt = now;
            if (RefreshLocalCandidates())
            {
                Logger("[P2P] local addresses changed (network change) — re-announcing");
                ResendHellos();
            }
        }

        UdpClient? udp;
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                udp = _udp;
                if (udp == null) return;

            PrunePendingLocked(now);

            foreach (var s in _sessions.Values)
            {
                if (s.Disposed) continue;

                if (s.Connected)
                {
                    if (now - s.LastInbound > DeadMs)
                    {
                        Logger("[P2P] path dead cid=" + s.ClientId +
                               " (no inbound for " + (now - s.LastInbound) + "ms) — re-punching");
                        StartPunchLocked(s, reason: "path dead");
                        continue;
                    }
                    if (now - s.LastOutbound >= KeepAliveMs)
                    {
                        s.LastOutbound = now;
                        if (s.Remote != null) ProbeLocked(s, s.Remote);
                    }
                    continue;
                }

                // Not connected yet.
                if (!s.HasPeerHello || s.Targets.Count == 0)
                {
                    if (s.HelloCount < HelloMaxCount && now - s.LastHelloAt >= HelloResendMs)
                        SendHelloLocked(s);
                    continue;
                }

                // Their HELLO is in, but the exchange can still be half-complete:
                // ours may have been dropped on their side (OnSignal discards
                // signals from a sid that is not in the roster yet — a roster race
                // during reconnect churn), and a repeated HELLO gets an idempotent
                // no-reply there. Without this resend the peer waits forever with
                // zero targets, never probes, and its NAT never admits us — the
                // classic "LAN works, every other network is silent". Keep ours
                // flowing until a path exists; the peer ignores duplicates.
                if (s.HelloCount < HelloMaxCount && now - s.LastHelloAt >= HelloResendMs)
                    SendHelloLocked(s);

                // Rounds only pace the log line — probing itself never pauses. A
                // simultaneous-open needs both ends' outbound to overlap, and the
                // old implementation went completely silent for 5–30s per round,
                // putting the two sides on disjoint schedules and dooming the
                // punch even between NATs that would otherwise open.
                if (now > s.PunchRoundEndsAt)
                {
                    s.PunchRound++;
                    int backoff = Math.Min(PunchRetryMaxBackoffMs,
                        PunchRetryMinBackoffMs + s.PunchRound * 2000);
                    s.PunchRoundEndsAt = now + PunchWindowMs + backoff;
                    Logger("[P2P] punch attempt " + s.PunchRound + " cid=" + s.ClientId +
                           " still trying (targets=" + s.Targets.Count +
                           " tx=" + s.Probes + " rx=" + s.StunRx + " all=" + _stunRxAny + ")");
                }

                if (now < s.NextProbeAt) continue;
                s.NextProbeAt = now + ProbeIntervalMs;
                for (int i = 0; i < s.Targets.Count && i < MaxTargets; i++)
                    ProbeLocked(s, s.Targets[i]);
            }
        }
        }
        finally { FlushSignals(); }
    }

    private void ProbeLocked(P2pSession s, IPEndPoint dest)
    {
        if (s.PeerDirHex == null) return;
        var udp = _udp;
        if (udp == null) return;

        var txId = StunMessage.NewTxId();
        var username = ToHex(s.Crypto.DirId) + ":" + s.PeerDirHex;
        byte[] req;
        try { req = StunMessage.BuildBindingRequest(txId, username); }
        catch { return; }

        s.Probes++;
        _pending[ToHex(txId)] = new Pending { Dest = dest, Session = s, SentAt = Environment.TickCount64 };
        try { udp.Send(req, req.Length, dest); }
        catch { _pending.Remove(ToHex(txId)); }
    }

    private void PrunePendingLocked(long now)
    {
        if (_pending.Count == 0) return;
        List<string>? stale = null;
        foreach (var kv in _pending)
        {
            if (now - kv.Value.SentAt > PendingTtlMs)
                (stale ??= new List<string>()).Add(kv.Key);
        }
        if (stale == null) return;
        foreach (var k in stale) _pending.Remove(k);
    }

    // ── signalling out ──────────────────────────────────────────────────────

    private void SendHelloLocked(P2pSession s)
    {
        if (s.Disposed) return;
        string[] cands = CollectCandidateStringsLocked();
        var payload = new HelloMsg
        {
            t = "hi",
            d = ToHex(s.Crypto.DirId),
            k = Convert.ToBase64String(s.Crypto.PublicKey),
            c = cands,
            g = s.Generation,
        };
        string json;
        try { json = JsonSerializer.Serialize(payload); }
        catch { return; }

        s.HelloCount++;
        s.LastHelloAt = Environment.TickCount64;
        _outSignals.Enqueue((s.Sid, json));
    }

    /// <summary>Drain the outbound signalling queue. Never called with the lock held.</summary>
    private void FlushSignals()
    {
        while (_outSignals.TryDequeue(out var item))
        {
            try { _signal(item.Sid, item.Json); }
            catch (Exception ex) { Logger("[P2P] signal out failed: " + ex.Message); }
        }
    }

    private string[] CollectCandidateStringsLocked()
    {
        var list = new List<string>(MaxCandidates);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in _local)
            if (seen.Add(ep.ToString())) list.Add(ep.ToString());
        foreach (var ep in _srflx)
            if (seen.Add(ep.ToString())) list.Add(ep.ToString());
        if (list.Count > MaxCandidates) list.RemoveRange(MaxCandidates, list.Count - MaxCandidates);
        return list.ToArray();
    }

    /// <summary>Replace a session's probe targets with the peer's latest candidate
    /// list. Replacing matters: addresses from a network the peer has left must not
    /// crowd out the ones they just moved onto.</summary>
    private bool ApplyCandidatesLocked(P2pSession s, string[]? candidates)
    {
        if (candidates != null) s.PeerCandidates = candidates;

        string before = s.Targets.Count == 0 ? "" : string.Join("|", s.Targets);
        s.RebuildTargets(ParseCandidate, MaxTargets);
        string after = s.Targets.Count == 0 ? "" : string.Join("|", s.Targets);
        bool changed = !string.Equals(before, after, StringComparison.Ordinal);
        if (!changed || s.Connected || s.Targets.Count == 0) return false;

        long now = Environment.TickCount64;
        if (s.PunchRoundEndsAt == 0)
        {
            // First list we have ever had — start punching.
            StartPunchLocked(s, reason: "candidates");
        }
        else
        {
            // They moved: the round in progress was aimed at stale addresses, so
            // give it a fresh window instead of letting it time out uselessly.
            s.PunchRoundEndsAt = now + PunchWindowMs;
            s.NextProbeAt = now;
        }
        return true;
    }

    /// <summary>Validate + filter one advertised candidate. Returns null to skip.</summary>
    private static IPEndPoint? ParseCandidate(string raw)
    {
        if (!IPEndPoint.TryParse(raw.Trim(), out var ep) || ep == null) return null;
        if (ep.Port <= 0 || ep.Address.Equals(IPAddress.Any) ||
            ep.Address.Equals(IPAddress.IPv6Any) || ep.Address.IsIPv6LinkLocal)
            return null;
        if (ep.Address.GetAddressBytes()[0] == 0) return null; // 0/8, ::…, unspecified
        return ep;
    }

    private void FlushOrphansLocked(P2pSession s)
    {
        if (s.PeerDirId == null || _orphans.Count == 0) return;
        uint flow = ToFlowId(s.PeerDirId);
        for (int i = _orphans.Count - 1; i >= 0; i--)
        {
            var (pkt, from, _) = _orphans[i];
            if (pkt.Length < 6) { _orphans.RemoveAt(i); continue; }
            uint f = (uint)((pkt[2] << 24) | (pkt[3] << 16) | (pkt[4] << 8) | pkt[5]);
            if (f != flow) continue;
            _orphans.RemoveAt(i);
            _byFlow[flow] = s;
            // Decrypt + deliver outside this lock via the normal media path.
            Task.Run(() => HandleOrphan(pkt, from));
        }
    }

    private void HandleOrphan(byte[] pkt, IPEndPoint from)
    {
        try
        {
            P2pSession? s;
            uint flow = (uint)((pkt[2] << 24) | (pkt[3] << 16) | (pkt[4] << 8) | pkt[5]);
            lock (_lock) _byFlow.TryGetValue(flow, out s);
            if (s == null || s.Disposed) return;
            if (!s.Crypto.TryOpen(pkt, out var payload, out uint counter)) return;
            lock (_lock)
            {
                if (s.Disposed) return;
                if (!s.Connected) AdoptPathLocked(s, from, reason: "buffered frame");
                s.LastInbound = Environment.TickCount64;
                if (s.MediaFrames++ == 0)
                    Logger("[P2P] media rx cid=" + s.ClientId + " sid=" + s.Sid + " (first frame, buffered)");
            }
            _onAudio(s.ClientId, payload, counter);
        }
        catch { }
    }

    private void UnregisterLocked(P2pSession s)
    {
        if (s.PeerDirHex != null)
        {
            if (_byPeerDir.TryGetValue(s.PeerDirHex, out var cur) && cur == s)
                _byPeerDir.Remove(s.PeerDirHex);
            if (s.PeerDirId != null && _byFlow.TryGetValue(ToFlowId(s.PeerDirId), out var cur2) && cur2 == s)
                _byFlow.Remove(ToFlowId(s.PeerDirId));
        }
        List<string>? mine = null;
        foreach (var kv in _pending)
            if (ReferenceEquals(kv.Value.Session, s))
                (mine ??= new List<string>()).Add(kv.Key);
        if (mine != null) foreach (var k in mine) _pending.Remove(k);
    }

    private void UnregisterPeerLocked(P2pSession s, string? dirHex)
    {
        if (dirHex == null) return;
        if (_byPeerDir.TryGetValue(dirHex, out var cur) && cur == s) _byPeerDir.Remove(dirHex);
        if (s.PeerDirId != null && _byFlow.TryGetValue(ToFlowId(s.PeerDirId), out var cur2) && cur2 == s)
            _byFlow.Remove(ToFlowId(s.PeerDirId));
    }

    // ── candidates ──────────────────────────────────────────────────────────

    /// <summary>Re-scan the machine's unicast addresses. Returns true when the set
    /// changed (wifi ⇄ ethernet, VPN up/down) so callers can re-announce.</summary>
    private bool RefreshLocalCandidates()
    {
        var list = new List<IPEndPoint>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                IPInterfaceProperties props;
                try { props = ni.GetIPProperties(); } catch { continue; }

                foreach (var ua in props.UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any)) continue;
                    if (IPAddress.IsLoopback(a)) continue;
                    if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv6LinkLocal) continue;
                    list.Add(new IPEndPoint(a, _port));
                }
            }
        }
        catch { /* platform quirk — host candidates still include loopback */ }

        // Same-host clients (two instances on one PC for testing) only see this.
        list.Add(new IPEndPoint(IPAddress.Loopback, _port));

        bool changed;
        lock (_lock)
        {
            changed = _local.Count != list.Count;
            if (!changed)
                foreach (var ep in list)
                    if (!_local.Contains(ep)) { changed = true; break; }

            _local.Clear();
            _local.AddRange(list);
        }
        return changed;
    }

    private async Task RefreshSrflxAsync()
    {
        if (_gathering || _disposed || _udp == null) return;
        _gathering = true;
        try
        {
            var servers = _stunServers;
            if (servers.Length == 0) return;

            var tasks = new Task<IPEndPoint?>[servers.Length];
            for (int i = 0; i < servers.Length; i++) tasks[i] = QuerySrflxAsync(servers[i]);
            IPEndPoint?[] results;
            try { results = await Task.WhenAll(tasks); }
            catch { return; }

            var found = new List<IPEndPoint>();
            foreach (var r in results)
                if (r != null && !found.Contains(r)) found.Add(r);

            int answered = 0;
            foreach (var r in results) if (r != null) answered++;
            Logger("[P2P] STUN answers " + answered + "/" + servers.Length);

            bool changed = false;
            lock (_lock)
            {
                _srflxAt = Environment.TickCount64;
                if (found.Count > 0)
                {
                    // Replace, don't accumulate: on mobile the public mapping can
                    // move mid-session, and a stale entry advertised ahead of the
                    // fresh one crowds it out of the peer's MaxTargets budget.
                    changed = _srflx.Count != found.Count;
                    if (!changed)
                        foreach (var ep in found)
                            if (!_srflx.Contains(ep)) { changed = true; break; }
                    _srflx.Clear();
                    _srflx.AddRange(found);
                    _srflxKeys.Clear();
                    foreach (var ep in _srflx) _srflxKeys.Add(ep.ToString());
                }
            }

            if (changed)
            {
                Logger("[P2P] srflx candidates: " + string.Join(", ", found.Select(f => f.ToString())));
                ResendHellos();
            }
        }
        finally { _gathering = false; }
    }

    /// <summary>Merge one freshly learned reflexive address and re-announce if it
    /// is new. Idempotent, so it is safe to call from every STUN query.</summary>
    private void PublishSrflx(IPEndPoint ep)
    {
        bool changed;
        lock (_lock)
        {
            if (_srflxKeys.Add(ep.ToString()))
            {
                _srflx.Add(ep);
                changed = true;
            }
            else changed = false;
        }
        if (!changed) return;
        Logger("[P2P] srflx candidate: " + ep);
        ResendHellos();
    }

    private async Task<IPEndPoint?> QuerySrflxAsync(string spec)
    {
        if (!TryParseStunSpec(spec, out var host, out var port)) return null;

        IPAddress? addr = null;
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            foreach (var a in addrs)
                if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
            if (addr == null)
                foreach (var a in addrs)
                    if (a.AddressFamily == AddressFamily.InterNetworkV6) { addr = a; break; }
        }
        catch { return null; }
        if (addr == null) return null;

        var dest = new IPEndPoint(addr, port);
        for (int attempt = 0; attempt < StunAttempts; attempt++)
        {
            if (_disposed) return null;
            var txId = StunMessage.NewTxId();
            var tcs = new TaskCompletionSource<IPEndPoint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var key = ToHex(txId);
            lock (_lock)
                _pending[key] = new Pending { Dest = dest, Tcs = tcs, SentAt = Environment.TickCount64 };

            try
            {
                var req = StunMessage.BuildBindingRequest(txId, null);
                UdpClient? udp;
                lock (_lock) udp = _udp;
                udp?.Send(req, req.Length, dest);
            }
            catch
            {
                lock (_lock) _pending.Remove(key);
                return null;
            }

            IPEndPoint? mapped = null;
            try { mapped = await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(StunTimeoutMs)); }
            catch (TimeoutException) { lock (_lock) _pending.Remove(key); }

            if (mapped != null && IsUsefulSrflx(mapped))
            {
                // Publish the moment one server answers instead of waiting for the
                // whole pile: several will be unreachable, and holding the
                // candidate until the laggards time out delays every punch.
                // Log which server answered with what — "is STUN lying / dead"
                // settles from this line instead of from guesswork.
                Logger("[P2P] STUN " + host + ":" + port + " → " + mapped);
                PublishSrflx(mapped);
                return mapped;
            }
            if (mapped == null) await Task.Delay(150);
        }
        return null;
    }

    /// <summary>A reflexive address that is just one of our own LAN addresses is
    /// useless (we already advertise it) — that happens when STUN runs on-LAN.</summary>
    private bool IsUsefulSrflx(IPEndPoint ep)
    {
        if (ep.Address.Equals(IPAddress.Any) || IPAddress.IsLoopback(ep.Address)) return false;
        if (ep.Address.GetAddressBytes()[0] == 0) return false;
        lock (_lock)
        {
            foreach (var l in _local)
                if (l.Address.Equals(ep.Address)) return false;
        }
        return true;
    }

    private static bool TryParseStunSpec(string spec, out string host, out int port)
    {
        host = string.Empty;
        port = 3478;
        if (string.IsNullOrWhiteSpace(spec)) return false;
        var s = spec.Trim();
        foreach (var prefix in new[] { "stun:", "STUN:", "stuns:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s.Substring(prefix.Length); break; }
        if (s.Length == 0) return false;

        // IPv6 literal "[2001:db8::1]:3478"
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close <= 1) return false;
            host = s.Substring(1, close - 1);
            if (close + 1 < s.Length && s[close + 1] == ':' &&
                int.TryParse(s.Substring(close + 2), out var p6) && p6 > 0 && p6 <= 65535) port = p6;
            return host.Length > 0;
        }

        int colon = s.LastIndexOf(':');
        if (colon > 0 && colon < s.Length - 1 && s.IndexOf(':') == colon)
        {
            if (int.TryParse(s.Substring(colon + 1), out var p) && p > 0 && p <= 65535)
            {
                host = s.Substring(0, colon);
                port = p;
                return host.Length > 0;
            }
            return false;
        }
        host = s;
        return true;
    }

    private static string[] LoadStunServers()
    {
        string raw;
        try { raw = VoiceConfig.P2PStunServers; }
        catch { raw = string.Empty; }

        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = part.Trim();
            if (s.Length == 0 || !seen.Add(s)) continue;
            list.Add(s);
        }
        return list.ToArray();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static bool EqualsAddress(IPEndPoint? a, IPEndPoint? b)
        => a != null && b != null && a.Equals(b);

    private static uint ToFlowId(byte[] dir)
        => (uint)((dir[0] << 24) | (dir[1] << 16) | (dir[2] << 8) | dir[3]);

    private static string ToHex(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static byte[]? FromHex(string hex)
    {
        if (hex == null || hex.Length == 0 || hex.Length % 2 != 0) return null;
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return null;
        }
        return bytes;
    }

    private static void Logger(string msg)
    {
        try { InterstellarPlugin.Logger?.LogInfo(msg); }
        catch { /* logger unavailable during teardown */ }
    }
}
