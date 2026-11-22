using System.Text;
using Microsoft.CodeAnalysis.Text;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Lines as Roslyn counts them (a lone '\r', U+0085, U+2028 and U+2029 end a line as '\n' and "\r\n" do), held
/// to Roslyn's own <see cref="SourceText"/> over seeded random texts of every break and what stands beside one: a place's
/// line and column each way (<see cref="RoslynLines"/>, <see cref="ServerLines"/>, which every place to and from the
/// language server goes through), a snapshot's lines read whole, across the rope's chunks, and carried through random
/// edits. A difference is shrunk to its smallest text and said with its seed.</summary>
public sealed class RoslynLinesTests
{
    /// <summary>The differences kept on purpose, each with why: a server's column past its line's end is clamped to the
    /// end of the line's content, where <see cref="TextLineCollection.GetPosition"/> would count on into the break and the
    /// lines after it, so a place the server sends stale lands on its own line.</summary>
    private const string Kept = "a column past a line's end is that end";

    /// <summary>Each break Roslyn counts, a '\r' and a '\n' to stand apart or meet, and characters that are none.</summary>
    private const string Alphabet = "\n\r\r\n\u0085\u2028\u2029ab\t\u00C2\u2027";

    [Fact]
    public void Places_and_lines_are_counted_as_Roslyns_SourceText_counts_them_whole_across_chunks_and_through_edits()
    {
        for (int seed = 1; seed <= 3_000; seed++)
        {
            Random random = new(seed);
            string text = RandomText(random, random.Next(0, 24));
            if (Differs(text, ServerLines.Of(TextSnapshot.From(text)), whole: true) is { } differs)
            {
                string smallest = Shrink(text, candidate => Differs(candidate, ServerLines.Of(TextSnapshot.From(candidate)), whole: true) is not null);
                Assert.Fail($"Seed {seed}: in {Shown(smallest)}, {Differs(smallest, ServerLines.Of(TextSnapshot.From(smallest)), whole: true)} (in the text drawn, {differs}); kept apart on purpose: {Kept}");
            }

            (string after, TextEdit[] edits) = Edited(random, text);
            TextSnapshot before = TextSnapshot.From(text);
            Assert.NotNull(ServerLines.Of(before));
            TextSnapshot made = TextSnapshot.From(after);
            ServerLines carried = ServerLines.After(new DocumentChangedEventArgs(before, made, edits, 1, ChangeReason.Edit))!;
            Assert.True(Differs(after, carried, whole: true) is null,
                $"Seed {seed}: the lines of {Shown(text)} carried through the edits {string.Join(", ", edits.Select(e => $"{e.Start}+{e.Length}→{Shown(e.Text)}"))} "
                + $"to {Shown(after)}: {Differs(after, carried, whole: true)}, so a key would move the server's places off the text");
        }
        for (int seed = 1; seed <= 40; seed++)
        {
            Random random = new(seed);
            string text = LongText(random);
            Assert.True(Differs(text, ServerLines.Of(TextSnapshot.From(text)), whole: false) is null,
                $"Seed {seed}: a text of {text.Length} characters, read across the rope's chunks: {Differs(text, ServerLines.Of(TextSnapshot.From(text)), whole: false)}");
        }
    }

    /// <summary>What differs from Roslyn's count of a text, read through <paramref name="lines"/>, or null. Whole reads every
    /// line's every column; otherwise each offset and each line's ends, as for a long text.</summary>
    private static string? Differs(string text, ServerLines lines, bool whole)
    {
        TextLineCollection roslyn = SourceText.From(text).Lines;
        if (lines.LineCount != roslyn.Count)
        {
            return $"the server's lines number {lines.LineCount} where Roslyn's do {roslyn.Count}";
        }
        for (int offset = 0; offset <= text.Length; offset++)
        {
            LinePosition expected = roslyn.GetLinePosition(offset);
            if (RoslynLines.PlaceOf(text, offset) is var place && place != (expected.Line, expected.Character))
            {
                return $"RoslynLines places offset {offset} at {place} where Roslyn does at {expected}";
            }
            if (lines.PositionOf(offset) is var position && (position.Line, position.Character) != (expected.Line, expected.Character))
            {
                return $"the server's lines place offset {offset} at {position} where Roslyn does at {expected}";
            }
        }
        List<LspPosition> asked = [];
        List<int> expectedOffsets = [];
        foreach (TextLine line in roslyn)
        {
            int length = line.End - line.Start;
            IEnumerable<int> columns = whole ? Enumerable.Range(0, length + 3) : [0, length, length + 1];
            foreach (int column in columns)
            {
                int expected = column <= length ? line.Start + column : line.End;
                asked.Add(new LspPosition(line.LineNumber, column));
                expectedOffsets.Add(expected);
                if (lines.OffsetOf(line.LineNumber, column) is int offset && offset != expected)
                {
                    return $"the server's line {line.LineNumber}, column {column} is offset {offset} where Roslyn's is {expected}";
                }
            }
        }
        int[] offsets = lines.OffsetsOf(asked);
        int wrong = Enumerable.Range(0, offsets.Length).FirstOrDefault(i => offsets[i] != expectedOffsets[i], -1);
        return wrong < 0 ? null : $"of many places asked at once, {asked[wrong]} is offset {offsets[wrong]} where Roslyn's is {expectedOffsets[wrong]}";
    }

    private static string RandomText(Random random, int length)
    {
        StringBuilder text = new(length);
        for (int i = 0; i < length; i++)
        {
            text.Append(Alphabet[random.Next(Alphabet.Length)]);
        }
        return text.ToString();
    }

    /// <summary>A text of a few of the rope's chunks, its breaks crowded about each chunk's end so a "\r\n" may fall across
    /// two.</summary>
    private static string LongText(Random random)
    {
        StringBuilder text = new(new string('a', random.Next(3_000, 4_000)));
        foreach (int chunkEnd in new[] { 1_024, 2_048, 3_072 })
        {
            for (int at = chunkEnd - 4; at < chunkEnd + 4 && at < text.Length; at++)
            {
                text[at] = Alphabet[random.Next(Alphabet.Length)];
            }
        }
        for (int i = 0; i < 40; i++)
        {
            text[random.Next(text.Length)] = Alphabet[random.Next(Alphabet.Length)];
        }
        return text.ToString();
    }

    /// <summary>A text after a few random edits, apart and in order, each replacing a stretch with random text.</summary>
    private static (string After, TextEdit[] Edits) Edited(Random random, string text)
    {
        List<TextEdit> edits = [];
        int from = 0;
        while (from <= text.Length && edits.Count < 3 && random.Next(4) != 0)
        {
            int start = random.Next(from, text.Length + 1);
            int length = random.Next(0, Math.Min(4, text.Length - start) + 1);
            edits.Add(new TextEdit(start, length, RandomText(random, random.Next(0, 4))));
            from = start + length + 1;
        }
        StringBuilder after = new(text);
        foreach (TextEdit edit in Enumerable.Reverse(edits))
        {
            after.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Text);
        }
        return (after.ToString(), [.. edits]);
    }

    /// <summary>The smallest text still failing that taking out a character, or making one an 'a', reaches.</summary>
    private static string Shrink(string text, Func<string, bool> fails)
    {
        bool smaller = true;
        while (smaller)
        {
            smaller = false;
            for (int i = 0; i < text.Length; i++)
            {
                foreach (string? candidate in new[] { text.Remove(i, 1), text[i] == 'a' ? null : text[..i] + "a" + text[(i + 1)..] })
                {
                    if (candidate is not null && fails(candidate))
                    {
                        text = candidate;
                        smaller = true;
                        break;
                    }
                }
            }
        }
        return text;
    }

    private static string Shown(string text) => "\"" + string.Concat(text.Select(c => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        < ' ' or > '~' => $"\\u{(int)c:X4}",
        _ => c.ToString(),
    })) + "\"";
}
