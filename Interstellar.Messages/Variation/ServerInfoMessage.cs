using System;

namespace Interstellar.Messages.Variation;

/// <summary>
/// Sent by the server to a client after joining a room.
/// Contains server capacity and address info for UI display.
/// </summary>
public class ServerInfoMessage : IMessage
{
    public int OptimalPlayers { get; }
    public int CurrentTotalPlayers { get; }
    public string VoiceServerUrl { get; }

    public ServerInfoMessage(int optimalPlayers, int currentTotalPlayers, string voiceServerUrl)
    {
        OptimalPlayers = optimalPlayers;
        CurrentTotalPlayers = currentTotalPlayers;
        VoiceServerUrl = voiceServerUrl;
    }

    int IMessage.Serialize(Span<byte> bytes)
    {
        int length = 0;
        length += IMessage.SerializeTag(ref bytes, MessageTag.ServerInfo);
        length += IMessage.SerializeInt32(ref bytes, OptimalPlayers);
        length += IMessage.SerializeInt32(ref bytes, CurrentTotalPlayers);
        length += IMessage.SerializeString(ref bytes, VoiceServerUrl);
        return length;
    }

    public static ServerInfoMessage DeserializeWithoutTag(ReadOnlySpan<byte> bytes, out int read)
    {
        read = 0;
        read += IMessage.DeserializeInt32(ref bytes, out var optimalPlayers);
        read += IMessage.DeserializeInt32(ref bytes, out var currentTotalPlayers);
        read += IMessage.DeserializeString(ref bytes, out var voiceServerUrl);
        return new ServerInfoMessage(optimalPlayers, currentTotalPlayers, voiceServerUrl);
    }
}
