using System;

namespace Interstellar.Messages.Variation;

public class HostSettingsMessage : IMessage
{
    public float MaxChatDistance { get; }
    public bool WallsBlockSound => (Flags & 1) != 0;
    public bool OnlyHearInSight => (Flags & 2) != 0;
    public bool ImpostorHearGhosts => (Flags & 4) != 0;
    public bool OnlyGhostsCanTalk => (Flags & 8) != 0;
    public bool HearInVent => (Flags & 16) != 0;
    public bool VentPrivateChat => (Flags & 32) != 0;
    public bool CommsSabDisables => (Flags & 64) != 0;
    public bool CameraCanHear => (Flags & 128) != 0;
    public bool ImpostorPrivateRadio => (Flags & 256) != 0;
    public bool OnlyMeetingOrLobby => (Flags & 512) != 0;
    public bool HearVentPlayers => (Flags & 1024) != 0;

    /// <summary>Packed bitmask of all 12 boolean settings (saves 10 bytes vs individual booleans).</summary>
    public ushort Flags { get; }

    public HostSettingsMessage(
        float maxChatDistance, bool wallsBlockSound, bool onlyHearInSight,
        bool impostorHearGhosts, bool onlyGhostsCanTalk, bool hearInVent,
        bool hearVentPlayers, bool ventPrivateChat, bool commsSabDisables,
        bool cameraCanHear, bool impostorPrivateRadio, bool onlyMeetingOrLobby)
    {
        MaxChatDistance = maxChatDistance;
        ushort flags = 0;
        if (wallsBlockSound) flags |= 1;
        if (onlyHearInSight) flags |= 2;
        if (impostorHearGhosts) flags |= 4;
        if (onlyGhostsCanTalk) flags |= 8;
        if (hearInVent) flags |= 16;
        if (ventPrivateChat) flags |= 32;
        if (commsSabDisables) flags |= 64;
        if (cameraCanHear) flags |= 128;
        if (impostorPrivateRadio) flags |= 256;
        if (onlyMeetingOrLobby) flags |= 512;
        if (hearVentPlayers) flags |= 1024;
        Flags = flags;
    }

    int IMessage.Serialize(Span<byte> bytes)
    {
        int length = 0;
        length += IMessage.SerializeTag(ref bytes, MessageTag.HostSettings);
        length += IMessage.SerializeFloat(ref bytes, MaxChatDistance);
        length += IMessage.SerializeByte(ref bytes, (byte)(Flags & 0xFF));
        length += IMessage.SerializeByte(ref bytes, (byte)((Flags >> 8) & 0xFF));
        return length;
    }

    static public HostSettingsMessage DeserializeWithoutTag(ReadOnlySpan<byte> bytes, out int read)
    {
        read = 0;
        read += IMessage.DeserializeFloat(ref bytes, out var maxChatDistance);
        read += IMessage.DeserializeByte(ref bytes, out var flagsLow);
        read += IMessage.DeserializeByte(ref bytes, out var flagsHigh);
        ushort flags = (ushort)(flagsLow | (flagsHigh << 8));
        return new(maxChatDistance,
            (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0,
            (flags & 8) != 0, (flags & 16) != 0, (flags & 32) != 0,
            (flags & 64) != 0, (flags & 128) != 0, (flags & 256) != 0,
            (flags & 512) != 0, (flags & 1024) != 0);
    }
}
