using System;

namespace Interstellar.Audio;

/// <summary>
/// Byte-level helpers for sample-provider output buffers.
///
/// NAudio's <c>SampleToWaveProvider</c> (the wrapper WasapiOut ends up pulling
/// from) passes its own byte[] playback buffer straight through to
/// <c>ISampleProvider.Read</c> reinterpreted as float[]. The runtime type is
/// therefore byte[] while the static type is float[], which means:
///   • Array.Copy(float[], …) throws ArrayTypeMismatchException, and
///   • Array.Clear(buffer, offset, count) clears count BYTES, i.e. only a quarter
///     of the frame, leaving the previous audio in place (a stuck loop instead of
///     silence).
/// Buffer.BlockCopy is byte-level and only needs primitive arrays, so the same
/// offsets are correct for a real float[] and for the reinterpreted byte[].
/// </summary>
internal static class SampleFill
{
    private static float[] _zero = new float[512];

    /// <summary>Zero <paramref name="count"/> samples at <paramref name="offset"/>,
    /// regardless of which runtime array type the buffer actually is.</summary>
    public static void Clear(float[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        var zero = _zero;
        if (zero.Length < count)
        {
            zero = new float[count];
            _zero = zero;
        }
        Buffer.BlockCopy(zero, 0, buffer, offset * 4, count * 4);
    }

    /// <summary>Copy <paramref name="count"/> samples from <paramref name="source"/>
    /// into the buffer, byte-level (see the class comment).</summary>
    public static void Copy(float[] source, int sourceOffset, float[] buffer, int offset, int count)
    {
        if (count <= 0) return;
        Buffer.BlockCopy(source, sourceOffset * 4, buffer, offset * 4, count * 4);
    }
}
