using System;
using System.Security.Cryptography;

namespace Interstellar.Network.P2P;

/// <summary>
/// Per-peer transport crypto for the P2P media path.
///
/// A fresh ephemeral P-256 ECDH pair is generated per session; the public keys are
/// exchanged over the existing voice-server "signal" channel (which only ever sees
/// public keys, so the server cannot read the audio — a privacy improvement over the
/// old design where the server relayed the Opus stream itself).
///
///   shared  = ECDH(localPriv, peerPub)                       (SHA-256)
///   sendKey = HKDF-SHA256(shared, "…send-v1" | myDirId | peerDirId)
///   recvKey = HKDF-SHA256(shared, "…send-v1" | peerDirId | myDirId)
///
/// Each direction therefore has its OWN key, so the 12-byte GCM nonce (dirId |
/// counter | zeros) can never repeat under one key — no replay/nonce-reuse hazard
/// even across a counter restart, because a restart always means a new ECDH.
///
/// Frame layout (10-byte header, AEAD-sealed payload):
///   [0]     0x81  magic: high bit set (so it is never mistaken for STUN) + media
///   [1]     flags (bit0 = encrypted)
///   [2..5]  sender dirId (4 bytes)  — routes the frame to a session in O(1)
///   [6..9]  counter (uint32 BE)     — the GCM nonce and the replay window index
///   [10..]  ciphertext | 16-byte tag, header bound as AAD
/// </summary>
internal sealed class P2pCrypto : IDisposable
{
    public const int DirIdLen = 4;
    public const int HeaderLen = 10;
    public const int TagLen = 16;
    public const byte MediaMagic = 0x81;
    public const byte FlagEncrypted = 0x01;

    private static readonly byte[] InfoPrefix = "interstellar-p2p-send-v1"u8.ToArray();

    private readonly ECDiffieHellman _ec;
    private readonly byte[] _dirId;
    private AesGcm? _send;
    private AesGcm? _recv;
    private uint _sendCounter;

    // Replay window: highest counter seen + a 64-packet bitmap behind it.
    private uint _highest;
    private ulong _seen;

    private bool _disposed;

    public byte[] DirId => _dirId;
    /// <summary>Uncompressed P-256 public key, X || Y (64 bytes).</summary>
    public byte[] PublicKey { get; }
    public bool Ready => _send != null && _recv != null;

    public P2pCrypto()
    {
        _ec = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _dirId = new byte[DirIdLen];
        RandomNumberGenerator.Fill(_dirId);
        var p = _ec.ExportParameters(false);
        PublicKey = new byte[64];
        Buffer.BlockCopy(p.Q.X!, 0, PublicKey, 0, 32);
        Buffer.BlockCopy(p.Q.Y!, 0, PublicKey, 32, 32);
    }

    /// <summary>Derive the direction-split keys from the peer's public key + dirId.
    /// Returns false (leaving the session unusable) on malformed input.</summary>
    public bool TryDerive(byte[] peerPublicKey, byte[] peerDirId)
    {
        if (_disposed || Ready) return Ready;
        if (peerPublicKey == null || peerPublicKey.Length != 64) return false;
        if (peerDirId == null || peerDirId.Length != DirIdLen) return false;

        try
        {
            var ep = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint
                {
                    X = Slice(peerPublicKey, 0, 32),
                    Y = Slice(peerPublicKey, 32, 32),
                },
            };
            using var peerEc = ECDiffieHellman.Create(ep);
            byte[] shared = _ec.DeriveKeyFromHash(peerEc.PublicKey, HashAlgorithmName.SHA256);
            try
            {
                byte[] sendInfo = BuildInfo(_dirId, peerDirId);
                byte[] recvInfo = BuildInfo(peerDirId, _dirId);
                byte[] sendKey = Hkdf(shared, sendInfo, 32);
                byte[] recvKey = Hkdf(shared, recvInfo, 32);
                var send = new AesGcm(sendKey);
                var recv = new AesGcm(recvKey);
                CryptographicOperations.ZeroMemory(sendKey);
                CryptographicOperations.ZeroMemory(recvKey);
                _send = send;
                _recv = recv;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(shared); }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Seal one media frame. Returns null when the keys are not derived yet.</summary>
    public byte[]? Seal(byte[] payload)
    {
        var send = _send;
        if (send == null || payload == null || _disposed) return null;

        uint counter = ++_sendCounter;
        var pkt = new byte[HeaderLen + payload.Length + TagLen];
        pkt[0] = MediaMagic;
        pkt[1] = FlagEncrypted;
        _dirId.CopyTo(pkt.AsSpan(2, DirIdLen));
        WriteBe(pkt, 6, counter);

        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(pkt, counter, nonce);
        try
        {
            send.Encrypt(
                nonce,
                payload,
                pkt.AsSpan(HeaderLen, payload.Length),
                pkt.AsSpan(HeaderLen + payload.Length, TagLen),
                pkt.AsSpan(0, HeaderLen));
        }
        catch { return null; }
        return pkt;
    }

    /// <summary>Open a media frame addressed to this session (the caller has already
    /// routed it here by sender dirId). False = tampered, replayed or wrong key.
    /// <paramref name="counter"/> returns the frame's sequence number so the caller
    /// can tell a dropped frame from a quiet stretch and conceal the loss.</summary>
    public bool TryOpen(byte[] pkt, out byte[] payload, out uint counter)
    {
        payload = Array.Empty<byte>();
        counter = 0;
        var recv = _recv;
        if (recv == null || pkt == null || _disposed) return false;
        if (pkt.Length < HeaderLen + TagLen) return false;
        if (pkt[0] != MediaMagic) return false;
        if ((pkt[1] & FlagEncrypted) == 0) return false;

        counter = ReadBe(pkt, 6);
        if (!WindowAccepts(counter)) return false;

        int cipherLen = pkt.Length - HeaderLen - TagLen;
        if (cipherLen < 0) return false;

        Span<byte> nonce = stackalloc byte[12];
        BuildNonce(pkt, counter, nonce);

        var plain = new byte[cipherLen];
        try
        {
            recv.Decrypt(
                nonce,
                pkt.AsSpan(HeaderLen, cipherLen),
                pkt.AsSpan(HeaderLen + cipherLen, TagLen),
                plain,
                pkt.AsSpan(0, HeaderLen));
        }
        catch (CryptographicException) { return false; }
        catch { return false; }

        // Only an authenticated frame may move the replay window. The counter sits
        // in cleartext, so committing before the tag verifies would let anyone who
        // has ever seen a single packet forge a huge counter, push the window past
        // every real frame, and mute this peer for good.
        CommitCounter(counter);

        payload = plain;
        return true;
    }

    /// <summary>Read-only window check: would this counter be accepted if the
    /// frame authenticates? Nothing here mutates state.</summary>
    private bool WindowAccepts(uint counter)
    {
        if (_highest == 0 && _seen == 0) return true; // nothing seen yet
        if (counter > _highest) return true;
        uint back = _highest - counter;
        if (back >= 64) return false;
        return (_seen & (1UL << (int)back)) == 0;
    }

    /// <summary>Record an authenticated counter: strict-ish replay protection with
    /// out-of-order tolerance up to 64 frames deep.</summary>
    private void CommitCounter(uint counter)
    {
        if (_highest == 0 && _seen == 0)
        {
            _highest = counter;
            _seen = 1;
            return;
        }
        if (counter > _highest)
        {
            uint shift = counter - _highest;
            _highest = counter;
            _seen = shift >= 64 ? 1UL : (_seen << (int)shift) | 1UL;
            return;
        }
        uint behind = _highest - counter;
        if (behind >= 64) return;
        _seen |= 1UL << (int)behind;
    }

    private static void BuildNonce(byte[] pkt, uint counter, Span<byte> nonce)
    {
        nonce[0] = pkt[2]; nonce[1] = pkt[3]; nonce[2] = pkt[4]; nonce[3] = pkt[5];
        nonce[4] = (byte)(counter >> 24); nonce[5] = (byte)(counter >> 16);
        nonce[6] = (byte)(counter >> 8); nonce[7] = (byte)counter;
        nonce[8] = 0; nonce[9] = 0; nonce[10] = 0; nonce[11] = 0;
    }

    private static byte[] BuildInfo(byte[] from, byte[] to)
    {
        var info = new byte[InfoPrefix.Length + DirIdLen + DirIdLen];
        InfoPrefix.CopyTo(info.AsSpan(0));
        from.CopyTo(info.AsSpan(InfoPrefix.Length));
        to.CopyTo(info.AsSpan(InfoPrefix.Length + DirIdLen));
        return info;
    }

    /// <summary>HKDF-SHA256 (RFC 5869), extract+expand, empty salt.</summary>
    private static byte[] Hkdf(byte[] ikm, byte[] info, int length)
    {
        byte[] prk;
        using (var extract = new HMACSHA256(new byte[32]))
            prk = extract.ComputeHash(ikm);

        var okm = new byte[length];
        using var expand = new HMACSHA256(prk);
        var prev = Array.Empty<byte>();
        int pos = 0;
        byte counter = 1;
        while (pos < length)
        {
            var input = new byte[prev.Length + info.Length + 1];
            Buffer.BlockCopy(prev, 0, input, 0, prev.Length);
            Buffer.BlockCopy(info, 0, input, prev.Length, info.Length);
            input[^1] = counter++;
            prev = expand.ComputeHash(input);
            int n = Math.Min(prev.Length, length - pos);
            Buffer.BlockCopy(prev, 0, okm, pos, n);
            pos += n;
        }
        CryptographicOperations.ZeroMemory(prk);
        return okm;
    }

    private static byte[] Slice(byte[] src, int offset, int length)
    {
        var dst = new byte[length];
        Buffer.BlockCopy(src, offset, dst, 0, length);
        return dst;
    }

    private static void WriteBe(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24);
        b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8);
        b[off + 3] = (byte)v;
    }

    private static uint ReadBe(byte[] b, int off)
        => (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _send?.Dispose(); } catch { }
        try { _recv?.Dispose(); } catch { }
        try { _ec.Dispose(); } catch { }
        _send = null;
        _recv = null;
    }
}
