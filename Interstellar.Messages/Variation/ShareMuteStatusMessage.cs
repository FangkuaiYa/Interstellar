using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Messages.Variation;

public class ShareMuteStatusMessage : IMessage
{
    public byte ClientId { get; }
    public bool IsMute { get; }
    public bool IsImpostorRadio { get; }

    public ShareMuteStatusMessage(byte clientId, bool isMute, bool isImpostorRadio = false)
    {
        this.ClientId = clientId;
        this.IsMute = isMute;
        this.IsImpostorRadio = isImpostorRadio;
    }

    int IMessage.Serialize(Span<byte> bytes)
    {
        int length = 0;
        length += IMessage.SerializeTag(ref bytes, MessageTag.ShareMuteStatus);
        length += IMessage.SerializeByte(ref bytes, ClientId);
        length += IMessage.SerializeBoolean(ref bytes, IsMute);
        length += IMessage.SerializeBoolean(ref bytes, IsImpostorRadio);
        return length;
    }

    static public ShareMuteStatusMessage DeserializeWithoutTag(ReadOnlySpan<byte> bytes, out int read)
    {
        read = 0;
        read += IMessage.DeserializeByte(ref bytes, out var clientId);
        read += IMessage.DeserializeBoolean(ref bytes, out var isMute);
        // Backward-compat: old clients don't send this field
        bool impRadio = false;
        if (bytes.Length > 0)
            read += IMessage.DeserializeBoolean(ref bytes, out impRadio);
        return new(clientId, isMute, impRadio);
    }
}
