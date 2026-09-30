using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Interstellar.Network.P2P;

/// <summary>
/// Minimal RFC 5389 STUN codec — just enough to
///   1. discover our server-reflexive address (a bunch of STUN servers are queried,
///      whichever answers gives us a public candidate), and
///   2. prove a UDP path works in both directions, which is the actual hole punch.
///
/// Deliberately NOT an ICE/TURN implementation: there is no relay fallback by
/// design — if no path opens, that peer simply has no audio.
///
/// No MESSAGE-INTEGRITY / FINGERPRINT attributes: this is a connectivity probe
/// between two Interstellar clients over an already authenticated signalling
/// channel (the peer's candidates arrive over the voice server's "signal" event),
/// so integrity is provided by the media packet's AEAD instead.
/// </summary>
internal static class StunMessage
{
    public const ushort BindingRequest = 0x0001;
    public const ushort BindingSuccess = 0x0101;
    public const ushort BindingError = 0x0111;

    public const int HeaderLen = 20;
    public const int TxIdLen = 12;

    private static readonly byte[] Magic = { 0x21, 0x12, 0xA4, 0x42 };

    private const ushort AttrUsername = 0x0006;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    /// <summary>True when the datagram looks like a STUN message (top two bits clear
    /// and the RFC 5389 magic cookie present). Media packets always set the high bit.</summary>
    public static bool IsStun(ReadOnlySpan<byte> d)
        => d.Length >= HeaderLen
           && (d[0] & 0xC0) == 0
           && d[4] == Magic[0] && d[5] == Magic[1] && d[6] == Magic[2] && d[7] == Magic[3];

    public static ushort MessageType(ReadOnlySpan<byte> d) => (ushort)((d[0] << 8) | d[1]);
    public static int MessageLength(ReadOnlySpan<byte> d) => (d[2] << 8) | d[3];

    public static byte[] NewTxId()
    {
        var id = new byte[TxIdLen];
        RandomNumberGenerator.Fill(id);
        return id;
    }

    /// <summary>Binding request carrying an optional USERNAME attribute whose value is
    /// the sender's 4-byte directory id (hex) so the receiver can attribute the probe
    /// to a session before any media has flowed.</summary>
    public static byte[] BuildBindingRequest(ReadOnlySpan<byte> txId, string? username)
    {
        int userBytes = string.IsNullOrEmpty(username) ? 0 : Encoding.UTF8.GetByteCount(username!);
        int attrLen = userBytes > 0 ? 4 + Pad4(userBytes) : 0;
        var m = new byte[HeaderLen + attrLen];
        WriteHeader(m, BindingRequest, attrLen, txId);
        if (userBytes > 0)
        {
            int off = HeaderLen;
            WriteUInt16(m, off, AttrUsername);
            WriteUInt16(m, off + 2, userBytes);
            Encoding.UTF8.GetBytes(username!, m.AsSpan(off + 4, userBytes));
        }
        return m;
    }

    /// <summary>Binding success carrying XOR-MAPPED-ADDRESS = the requester's address
    /// as we observed it (their peer-reflexive candidate).</summary>
    public static byte[] BuildBindingResponse(ReadOnlySpan<byte> txId, IPEndPoint mapped)
    {
        bool v4 = mapped.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
        int valueLen = v4 ? 8 : 20;
        var m = new byte[HeaderLen + 4 + valueLen];
        WriteHeader(m, BindingSuccess, 4 + valueLen, txId);

        int off = HeaderLen;
        WriteUInt16(m, off, AttrXorMappedAddress);
        WriteUInt16(m, off + 2, valueLen);
        int v = off + 4;
        m[v] = 0;
        m[v + 1] = v4 ? (byte)0x01 : (byte)0x02;

        int port = mapped.Port ^ ((Magic[0] << 8) | Magic[1]);
        WriteUInt16(m, v + 2, port);

        byte[] raw = mapped.Address.GetAddressBytes();
        if (v4)
        {
            for (int i = 0; i < 4; i++) m[v + 4 + i] = (byte)(raw[i] ^ Magic[i]);
        }
        else
        {
            for (int i = 0; i < 16; i++)
            {
                byte key = i < 4 ? Magic[i] : txId[i - 4];
                m[v + 4 + i] = (byte)(raw[i] ^ key);
            }
        }
        return m;
    }

    /// <summary>Parse a binding response. When <paramref name="expectedTxId"/> is given,
    /// only that transaction is accepted (the transaction table already guarantees it,
    /// this is a second check). Falls back to MAPPED-ADDRESS for old RFC 3489 servers.</summary>
    public static bool TryParseBindingResponse(
        ReadOnlySpan<byte> m, ReadOnlySpan<byte> expectedTxId, out IPEndPoint mapped)
    {
        mapped = null!;
        if (!IsStun(m)) return false;
        ushort type = MessageType(m);
        if (type != BindingSuccess) return false;
        if (expectedTxId.Length == TxIdLen)
        {
            for (int i = 0; i < TxIdLen; i++)
                if (m[8 + i] != expectedTxId[i]) return false;
        }
        ReadOnlySpan<byte> txId = m.Slice(8, TxIdLen);

        if (TryFindAttribute(m, AttrXorMappedAddress, out int off, out int len))
            return TryDecodeAddress(m, off, len, xor: true, txId, out mapped);
        if (TryFindAttribute(m, AttrMappedAddress, out off, out len))
            return TryDecodeAddress(m, off, len, xor: false, txId, out mapped);
        return false;
    }

    /// <summary>Read USERNAME off an inbound request (used to attribute the probe).</summary>
    public static bool TryGetUsername(ReadOnlySpan<byte> m, out string username)
    {
        username = string.Empty;
        if (!IsStun(m)) return false;
        if (!TryFindAttribute(m, AttrUsername, out int off, out int len) || len <= 0 || len > 256) return false;
        try
        {
            username = Encoding.UTF8.GetString(m.Slice(off, len));
            return username.Length > 0;
        }
        catch { return false; }
    }

    public static bool TryGetTxId(ReadOnlySpan<byte> m, out byte[] txId)
    {
        txId = Array.Empty<byte>();
        if (!IsStun(m)) return false;
        txId = m.Slice(8, TxIdLen).ToArray();
        return true;
    }

    // ── internals ────────────────────────────────────────────────────────────

    private static void WriteHeader(byte[] m, ushort type, int attrLen, ReadOnlySpan<byte> txId)
    {
        WriteUInt16(m, 0, type);
        WriteUInt16(m, 2, attrLen);
        Magic.CopyTo(m.AsSpan(4, 4));
        txId.Slice(0, Math.Min(TxIdLen, txId.Length)).CopyTo(m.AsSpan(8, TxIdLen));
    }

    private static bool TryFindAttribute(ReadOnlySpan<byte> m, ushort wanted, out int valueOffset, out int valueLength)
    {
        valueOffset = 0;
        valueLength = 0;
        int end = Math.Min(m.Length, HeaderLen + MessageLength(m));
        int off = HeaderLen;
        while (off + 4 <= end)
        {
            ushort type = (ushort)((m[off] << 8) | m[off + 1]);
            int len = (m[off + 2] << 8) | m[off + 3];
            int vOff = off + 4;
            if (len < 0 || vOff + len > end) return false;
            if (type == wanted)
            {
                valueOffset = vOff;
                valueLength = len;
                return true;
            }
            off = (vOff + len + 3) & ~3;
        }
        return false;
    }

    private static bool TryDecodeAddress(
        ReadOnlySpan<byte> m, int off, int len, bool xor, ReadOnlySpan<byte> txId, out IPEndPoint ep)
    {
        ep = null!;
        if (len < 8) return false;
        byte family = m[off + 1];
        int port = (m[off + 2] << 8) | m[off + 3];
        if (xor) port ^= (Magic[0] << 8) | Magic[1];

        if (family == 0x01)
        {
            if (len < 8) return false;
            var raw = new byte[4];
            for (int i = 0; i < 4; i++)
                raw[i] = xor ? (byte)(m[off + 4 + i] ^ Magic[i]) : m[off + 4 + i];
            if (IsUnspecified(raw)) return false;
            ep = new IPEndPoint(new IPAddress(raw), port);
            return true;
        }
        if (family == 0x02)
        {
            if (len < 20) return false;
            var raw = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                byte key = i < 4 ? Magic[i] : (i - 4 < txId.Length ? txId[i - 4] : (byte)0);
                raw[i] = xor ? (byte)(m[off + 4 + i] ^ key) : m[off + 4 + i];
            }
            if (IsUnspecified(raw)) return false;
            ep = new IPEndPoint(new IPAddress(raw), port);
            return true;
        }
        return false;
    }

    private static bool IsUnspecified(byte[] raw)
    {
        foreach (var b in raw) if (b != 0) return false;
        return true;
    }

    private static int Pad4(int n) => (n + 3) & ~3;

    private static void WriteUInt16(byte[] m, int off, int value)
    {
        m[off] = (byte)(value >> 8);
        m[off + 1] = (byte)value;
    }
}
