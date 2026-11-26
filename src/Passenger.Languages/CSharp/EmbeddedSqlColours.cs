using Passenger.Editor;
using Passenger.Languages.TextMate;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>Projects the colours of a SQL file onto the characters of a C# string that make up its SQL.</summary>
internal static class EmbeddedSqlColours
{
    public static List<SyntaxSpan> Of(EmbeddedSql[] strings, TextRange range, CancellationToken cancel)
    {
        List<SyntaxSpan> result = [];
        foreach (EmbeddedSql sql in strings)
        {
            List<SyntaxSpan> colours = TextMateHighlighter.Fragment(Language.Sql.Grammar!, sql.Text, cancel);
            int token = 0;
            int part = 0;
            while (token < colours.Count && part < sql.Parts.Length)
            {
                SyntaxSpan colour = colours[token];
                EmbeddedSqlPart source = sql.Parts[part];
                int from = Math.Max(source.Offset, colour.Start);
                int to = Math.Min(source.End, colour.End);
                if (from < to)
                {
                    int start = Math.Max(range.Start, source.SourceOffset(from));
                    int end = Math.Min(range.End, source.SourceOffset(to));
                    if (start < end)
                    {
                        result.Add(new SyntaxSpan(start, end - start, colour.Kind));
                    }
                }
                if (colour.End <= source.End)
                {
                    token++;
                }
                else
                {
                    part++;
                }
            }
        }
        result.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return result;
    }
}
