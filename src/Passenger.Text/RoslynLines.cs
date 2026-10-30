using System.Buffers;

namespace Passenger.Text;

/// <summary>Lines as Roslyn's parser, compilers and language server count them: a lone '\r', U+0085, U+2028 and U+2029 end
/// a line as '\n' and "\r\n" do.</summary>
public static class RoslynLines
{
    /// <summary>What ends a line for Roslyn beside '\n': '\r' (with the '\n' after it, one break), U+0085, U+2028 and
    /// U+2029.</summary>
    public static SearchValues<char> Breaks { get; } = SearchValues.Create("\r\u0085\u2028\u2029");

    private static readonly SearchValues<char> AllBreaks = SearchValues.Create("\n\r\u0085\u2028\u2029");

    /// <summary>An offset's line and column from zero.</summary>
    public static (int Line, int Column) PlaceOf(ReadOnlySpan<char> text, int offset)
    {
        int line = 0;
        int lineStart = 0;
        int at = 0;
        while (at < offset && text[at..offset].IndexOfAny(AllBreaks) is int found and >= 0)
        {
            at += found;
            // "\r\n" is one break, ending at its '\n'.
            if (text[at] != '\r' || at + 1 == text.Length || text[at + 1] != '\n')
            {
                line++;
                lineStart = at + 1;
            }
            at++;
        }
        return (line, offset - lineStart);
    }
}
