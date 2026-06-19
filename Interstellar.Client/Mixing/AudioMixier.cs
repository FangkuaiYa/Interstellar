using NAudio.Wave;

namespace Interstellar.Mixing;

internal interface IMixingContext
{
}

// Placeholder for future mixing implementation
internal class AudioMixier
{
    internal ISampleProvider? OutputProvider { get; }

    internal void AddSamples(int clientId, float[] buffer, int length)
    {
    }
}
