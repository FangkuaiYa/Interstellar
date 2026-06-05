#pragma warning disable CS8618, CS8602, CS8603, CS8604
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Interstellar.Mixing;

internal interface IMixingContext
{
}
internal class AudioMixier
{
    internal ISampleProvider OutputProvider { get; }

    /// <summary>
    /// Adds audio samples.
    /// </summary>
    /// <param name="clientId">The sender client ID.</param>
    /// <param name="buffer">Array containing audio data.</param>
    /// <param name="length">Length of the audio data.</param>
    internal void AddSamples(int clientId, float[] buffer, int length)
    {
    }
}
