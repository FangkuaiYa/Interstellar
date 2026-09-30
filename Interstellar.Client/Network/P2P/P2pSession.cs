using System;
using System.Collections.Generic;
using System.Net;

namespace Interstellar.Network.P2P;

/// <summary>
/// One P2P link with one remote client. Lives inside <see cref="P2pTransport"/>'s
/// lock — the transport is the only writer, and every field here is guarded by it.
///
/// A session walks: AddPeer → HELLO exchange → Punching → Connected ⇄ (dead) → Punching.
/// There is deliberately no "relay"/"fallback" state: if no UDP path opens, the
/// session simply stays in Punching and that peer has no audio.
/// </summary>
internal sealed class P2pSession : IDisposable
{
    public readonly string Sid;
    public readonly int ClientId;
    public readonly P2pCrypto Crypto = new();
    public volatile bool Disposed;

    // ── peer identity, learned from their "hi" ──────────────────────────────
    public bool HasPeerHello;
    public byte[]? PeerDirId;
    public string? PeerDirHex;
    public byte[]? PeerPublicKey;
    public int Generation;

    /// <summary>Remote candidate addresses to fire connectivity checks at.</summary>
    public readonly List<IPEndPoint> Targets = new();
    private readonly HashSet<string> _targetKeys = new(StringComparer.Ordinal);

    /// <summary>The candidate list exactly as the peer advertised it — kept raw so
    /// the target set can be *rebuilt* from it rather than accumulating stale
    /// addresses across a network change.</summary>
    public string[]? PeerCandidates;

    /// <summary>Addresses we saw an authenticated probe arrive from: the peer's
    /// reflexive candidate when it differs from what they advertised.</summary>
    public readonly List<IPEndPoint> Learned = new();

    // ── path state ──────────────────────────────────────────────────────────
    public bool Connected;
    /// <summary>Validated (or adopted from an authenticated frame) media address.</summary>
    public IPEndPoint? Remote;

    // ── punch scheduling (all absolute Environment.TickCount64 stamps) ──────
    public long PunchRoundEndsAt;
    public long NextProbeAt;
    public int PunchRound;

    // ── liveness ────────────────────────────────────────────────────────────
    public long LastInbound;
    public long LastOutbound;
    public long LastHelloAt;
    public int HelloCount;

    public long CreatedAt = Environment.TickCount64;
    /// <summary>Frames received over this session's validated UDP path — the first
    /// one is logged once, so "did P2P audio actually flow" settles from a log
    /// instead of from perception.</summary>
    public int MediaFrames;
    /// <summary>Frames sent over the validated path; likewise logged once.</summary>
    public int MediaTx;
    /// <summary>STUN probes this peer sent that we attributed to this session —
    /// rx=0 after several rounds means their packets never arrive (NAT filter or
    /// firewall), not an app-level drop.</summary>
    public int StunRx;
    /// <summary>Probes this side has sent toward this session's targets — the
    /// tx= counter in the punch log proves outbound checks are actually firing.</summary>
    public int Probes;

    public P2pSession(string sid, int clientId)
    {
        Sid = sid;
        ClientId = clientId;
    }

    public bool AddTarget(IPEndPoint ep)
    {
        var key = ep.ToString();
        if (!_targetKeys.Add(key)) return false;
        Targets.Add(ep);
        return true;
    }

    public void ClearTargets()
    {
        Targets.Clear();
        _targetKeys.Clear();
    }

    /// <summary>Peer restarted under a new ephemeral key: forget everything about
    /// them but keep our own key material (re-deriving on the next HELLO).</summary>
    public void ResetPeerIdentity()
    {
        HasPeerHello = false;
        PeerDirId = null;
        PeerDirHex = null;
        PeerPublicKey = null;
        PeerCandidates = null;
        Learned.Clear();
        ClearTargets();
        Connected = false;
        Remote = null;
    }

    /// <summary>Rebuild the probe target list from the peer's latest advertisement
    /// plus anything we learned from inbound probes. Replacing (rather than
    /// appending) matters: addresses from a network the client has left must not
    /// crowd out the ones it just moved onto.</summary>
    public void RebuildTargets(Func<string, IPEndPoint?> parse, int maxTargets)
    {
        ClearTargets();
        if (PeerCandidates != null)
        {
            foreach (var raw in PeerCandidates)
            {
                if (Targets.Count >= maxTargets) break;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var ep = parse(raw);
                if (ep != null) AddTarget(ep);
            }
        }
        foreach (var ep in Learned)
        {
            if (Targets.Count >= maxTargets) break;
            AddTarget(ep);
        }
    }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        try { Crypto.Dispose(); } catch { /* already gone */ }
    }
}
