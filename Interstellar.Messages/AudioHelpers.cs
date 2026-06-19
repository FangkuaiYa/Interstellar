using Concentus;
using SIPSorcery.Media;
using SIPSorceryMedia.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Messages;

static public class AudioHelpers
{
    public const int ClockRate = 48000;
    static public AudioFormat GetOpusFormat(int id) => new AudioFormat(AudioCodecsEnum.OPUS, id, ClockRate);
    static public IOpusEncoder GetOpusEncoder()
    {
        var encoder = OpusCodecFactory.CreateEncoder(48000, 1, Concentus.Enums.OpusApplication.OPUS_APPLICATION_VOIP);
        // Aggressive bandwidth optimization: 8 kbps is excellent for clear voice with Opus
        // (Discord uses 8-64 kbps; Teamspeak defaults to ~10 kbps; 8 kbps saves ~67% vs old 24 kbps)
        encoder.Bitrate = 8000;
        encoder.UseVBR = true;
        // DTX: stops transmitting entirely during silence (saves ~50%+ bandwidth when idle)
        // Comfort-noise packets (if any) are filtered server-side and client-side
        encoder.UseDTX = true;
        // In-band FEC: reduces packet loss impact without extra bandwidth for retransmits
        encoder.UseInbandFEC = true;
        encoder.SignalType = Concentus.Enums.OpusSignal.OPUS_SIGNAL_VOICE;
        return encoder;
    }
    static public IOpusDecoder GetOpusDecoder() => OpusCodecFactory.CreateDecoder(48000, 1);

    public const int MaxTracks = 63;
}
