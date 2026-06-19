using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Messages.Variation;

public class UpdateMuteStatusMessage : IMessage
{
    public bool Mute { get; }
    public bool IsImpostorRadio { get; }

    public UpdateMuteStatusMessage(bool mute, bool isImpostorRadio = false)
    {
        this.Mute = mute;
        this.IsImpostorRadio = isImpostorRadio;
    }

    int IMessage.Serialize(Span<byte> bytes)
    {
        int length = 0;
        length += IMessage.SerializeTag(ref bytes, MessageTag.UpdateMuteStatus);
        length += IMessage.SerializeBoolean(ref bytes, Mute);
        length += IMessage.SerializeBoolean(ref bytes, IsImpostorRadio);
        return length;
    }

    static public UpdateMuteStatusMessage DeserializeWithoutTag(ReadOnlySpan<byte> bytes, out int read)
    {
        read = 0;
        read += IMessage.DeserializeBoolean(ref bytes, out var mute);
        // Backward-compat: old clients don't send this field
        bool impRadio = false;
        if (bytes.Length > 0)
            read += IMessage.DeserializeBoolean(ref bytes, out impRadio);
        return new(mute, impRadio);
    }
}
