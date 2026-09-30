using System;
using System.Globalization;
using System.Text;

namespace Interstellar;

internal static class GameCode
{
    private const string CharSet = "QWXRTYLPESDFGHUJKZOCVBINMA";

    internal static string FromGameIdString(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int gameId))
            return raw; // already a letter code — nothing to convert

        if (gameId >= 0)
        {
            // v1: four ASCII bytes, little-endian ("ABCD" == 'A' | 'B'<<8 | ...).
            byte[] bytes = BitConverter.GetBytes(gameId);
            if (!BitConverter.IsLittleEndian) Array.Reverse(bytes);
            var sb = new StringBuilder(4);
            for (int i = 0; i < 4; i++)
            {
                if (bytes[i] < (byte)'A' || bytes[i] > (byte)'Z') return raw;
                sb.Append((char)bytes[i]);
            }
            return sb.ToString();
        }
        if (gameId == -1) return raw; // degenerate — not a minted id

        uint bits = (uint)gameId;
        int first = (int)(bits & 0x3FF);
        int rest = (int)((bits >> 10) & 0xFFFFF);
        if (first / 26 >= CharSet.Length) return raw; // not an id the game would mint

        char[] code = new char[6];
        code[0] = CharSet[first % 26];
        code[1] = CharSet[first / 26];
        code[2] = CharSet[rest % 26];
        rest /= 26;
        code[3] = CharSet[rest % 26];
        rest /= 26;
        code[4] = CharSet[rest % 26];
        code[5] = CharSet[(rest / 26) % 26];
        return new string(code);
    }
}
