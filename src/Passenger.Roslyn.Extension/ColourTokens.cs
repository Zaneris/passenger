namespace Passenger.Roslyn.Extension;

/// <summary>Semantic tokens as the protocol encodes them, five numbers each (its line counted from the last token's, its
/// start from the last's on the same line or else from its line's, its length, its type and its modifiers), written again
/// with each token's type taken from a table, or the token left out, those after it placed from the last token kept.</summary>
internal static class ColourTokens
{
    /// <summary>A table's word that tokens of that type are left out.</summary>
    public const int LeftOut = -1;

    /// <summary>The tokens with <paramref name="types"/>[type] for their type, a token of a type past the table's end or
    /// marked <see cref="LeftOut"/> left out.</summary>
    public static int[] Retyped(int[] data, int[] types)
    {
        int[] written = new int[data.Length - data.Length % 5];
        int count = 0;
        int line = 0;
        int start = 0;
        int keptLine = 0;
        int keptStart = 0;
        for (int i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            start = data[i] == 0 ? start + data[i + 1] : data[i + 1];
            int type = data[i + 3] >= 0 && data[i + 3] < types.Length ? types[data[i + 3]] : LeftOut;
            if (type == LeftOut)
            {
                continue;
            }
            written[count] = line - keptLine;
            written[count + 1] = line == keptLine ? start - keptStart : start;
            written[count + 2] = data[i + 2];
            written[count + 3] = type;
            written[count + 4] = data[i + 4];
            count += 5;
            keptLine = line;
            keptStart = start;
        }
        return count == written.Length ? written : written[..count];
    }
}
