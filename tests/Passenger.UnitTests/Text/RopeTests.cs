using System.Text;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>The rope under every document, read as a string holding the same text reads, its shape held after every edit.</summary>
public class RopeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(RopeNode.MaxLeaf)]
    [InlineData(RopeNode.MaxLeaf + 1)]
    [InlineData(RopeNode.MaxLeaf * RopeNode.MaxChildren + 1)]
    [InlineData(300_000)]
    public void Text_of_any_length_reads_back_whole_with_its_lines_where_a_string_has_them(int length)
    {
        string text = Texts.Random(new Random(length), length);
        TextSnapshot snapshot = TextSnapshot.From(text);
        RopeShape.Check(Rope.Build(text));
        Assert.Equal(text, snapshot.ToString());
        Model.Check(snapshot, text, new Random(7), probes: 200);
    }

    /// <summary>Two thousand edits of every size (typing, a line, a pasted block, the whole text) at seeded random places,
    /// each checked against the same edit of a string, every version kept on the way read again at the end.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_seeded_run_of_random_edits_reads_as_a_string_given_the_same_edits_reads(int seed)
    {
        Random random = new(seed);
        string model = Texts.Random(random, 40_000);
        TextSnapshot snapshot = TextSnapshot.From(model);
        RopeNode rope = Rope.Build(model);
        List<(TextSnapshot Version, string Text)> kept = [];
        for (int step = 0; step < 2_000; step++)
        {
            (int start, int length, string inserted) = Texts.RandomEdit(random, model);
            model = string.Concat(model.AsSpan(0, start), inserted, model.AsSpan(start + length));
            snapshot = snapshot.Replace(start, length, inserted);
            rope = Rope.Replace(rope, start, length, inserted);
            RopeShape.Check(rope);
            RopeShape.Balanced(rope);
            Assert.Equal(model.Length, snapshot.Length);
            Model.Check(snapshot, model, random, probes: 3);
            if (step % 100 == 0)
            {
                kept.Add((snapshot, model));
            }
        }
        Assert.Equal(model, snapshot.ToString());
        foreach ((TextSnapshot version, string text) in kept)
        {
            Assert.True(text == version.ToString(), "A version of the text read differently after later edits: an edit changed a snapshot taken before it");
        }
    }

    [Fact]
    public void A_leaf_filled_past_its_size_splits_into_leaves_that_fit_and_reads_the_same()
    {
        string text = new('a', RopeNode.MaxLeaf);
        RopeNode rope = Rope.Replace(Rope.Build(text), RopeNode.MaxLeaf / 2, 0, "b");
        RopeShape.Check(rope);
        Assert.Equal(2, RopeShape.LeafCount(rope));
        Assert.Equal(text.Insert(RopeNode.MaxLeaf / 2, "b"), ToText(rope));
    }

    [Fact]
    public void A_rope_grows_a_level_past_a_branch_of_leaves_and_comes_back_down_as_its_text_goes()
    {
        string text = Texts.Random(new Random(11), RopeNode.MaxLeaf * RopeNode.MaxChildren + 1);
        RopeNode rope = Rope.Build(text);
        Assert.Equal(2, rope.Height);
        rope = Rope.Replace(rope, 10, text.Length - 20, "middle");
        RopeShape.Check(rope);
        Assert.True(rope.Height < 2, $"26 characters left of a rope two levels deep are still {rope.Height} levels deep");
        Assert.Equal(string.Concat(text.AsSpan(0, 10), "middle", text.AsSpan(text.Length - 10)), ToText(rope));
        rope = Rope.Replace(rope, 0, rope.Length, string.Empty);
        RopeShape.Check(rope);
        Assert.Equal(0, rope.Length);
        Assert.Equal(1, TextSnapshot.From("x").Replace(0, 1, string.Empty).LineCount);
    }

    [Theory]
    [InlineData(RopeNode.MaxLeaf - 2)]
    [InlineData(RopeNode.MaxLeaf - 1)]
    [InlineData(RopeNode.MaxLeaf)]
    public void Text_built_into_leaves_never_has_a_surrogate_pair_cut_between_two(int at)
    {
        string text = new string('a', at) + "\U0001F600" + new string('b', RopeNode.MaxLeaf * 3);
        RopeShape.Check(Rope.Build(text));
        StringBuilder chunks = new();
        TextSnapshot.From(text).VisitChunks(0, text.Length, chunk =>
        {
            Assert.False(char.IsHighSurrogate(chunk.Span[^1]), $"A chunk of the text ends in half of the emoji at {at}");
            chunks.Append(chunk.Span);
        });
        Assert.Equal(text, chunks.ToString());
    }

    [Fact]
    public void A_line_ending_in_CRLF_holds_its_text_without_the_CR_and_a_place_inside_the_break_is_the_lines_end()
    {
        TextSnapshot text = TextSnapshot.From("one\r\ntwo\nthree\r\n");
        Assert.Equal(4, text.LineCount);
        Assert.Equal(new LineSpan(0, 0, 3, 2), text.GetLine(0));
        Assert.Equal(new LineSpan(1, 5, 3, 1), text.GetLine(1));
        Assert.Equal(new LineSpan(3, 16, 0, 0), text.GetLine(3));
        Assert.Equal("three", text.GetLineText(2));
        Assert.Equal(new TextPosition(0, 3), text.GetPosition(4));
        Assert.Equal(3, text.GetOffset(new TextPosition(0, 99)));
        Assert.Equal(text.Length, text.GetOffset(new TextPosition(99, 0)));
        Assert.Equal(0, text.GetLineFromOffset(4));
        Assert.Equal(1, text.GetLineFromOffset(5));
    }

    [Fact]
    public void A_needle_across_the_end_of_a_block_read_is_found_and_overlapping_ones_only_when_asked()
    {
        const int Block = 1 << 16;
        string text = new string('x', Block - 2) + "abab" + new string('x', 10) + "aaa";
        TextSnapshot snapshot = TextSnapshot.From(text);
        Assert.Equal([Block - 2], snapshot.Occurrences("abab"));
        Assert.Equal([Block - 2, Block], snapshot.Occurrences("ab"));
        Assert.Equal([text.Length - 3], snapshot.Occurrences("aa"));
        Assert.Equal([text.Length - 3, text.Length - 2], snapshot.Occurrences("aa", overlapping: true));
        Assert.Equal([Block], snapshot.Occurrences("ab", start: Block - 1));
        Assert.Empty(snapshot.Occurrences(string.Empty));
    }

    [Fact]
    public void Edits_applied_together_make_the_text_each_made_in_turn_from_the_last_makes()
    {
        TextSnapshot text = TextSnapshot.From("alpha beta\ngamma delta\n");
        TextEdit[] edits = [new(0, 5, "A"), new(6, 4, string.Empty), new(11, 0, "+"), new(text.Length, 0, "end")];
        Assert.Equal("A \n+gamma delta\nend", text.Apply(edits).ToString());
        Assert.Equal(new TextEdit(2, 3, "Z"), TextEdit.Between("abcdef", "abZf"));
        Assert.Equal(new TextEdit(3, 0, "c"), TextEdit.Between("abc", "abcc"));
    }

    private static string ToText(RopeNode rope)
    {
        StringBuilder builder = new();
        Rope.AppendTo(rope, builder);
        return builder.ToString();
    }
}

/// <summary>Text for the rope's and the editing commands' tests: words, both line breaks, accents and emoji.</summary>
internal static class Texts
{
    private static readonly string[] Pieces = ["word", " ", "the", "Café", "\n", "\r\n", "\t", "{", "}", "\U0001F600", "x", "    ", "line"];

    public static string Random(Random random, int length)
    {
        StringBuilder text = new(length + 2);
        while (text.Length < length)
        {
            text.Append(Pieces[random.Next(Pieces.Length)]);
        }
        text.Length = length;
        return Whole(text.ToString());
    }

    /// <summary>A place, a length from it and text to put there, of sizes from a key's to the whole text's, never inside a
    /// surrogate pair, the text kept between about a thousand and a hundred thousand characters.</summary>
    public static (int Start, int Length, string Text) RandomEdit(Random random, string model)
    {
        int kind = random.Next(100);
        int start = Boundary(model, kind < 5 ? 0 : kind < 10 ? model.Length : random.Next(model.Length + 1));
        int most = model.Length - start;
        int length = kind switch
        {
            < 40 => Math.Min(most, random.Next(2)),
            < 70 => Math.Min(most, random.Next(80)),
            < 97 => Math.Min(most, random.Next(model.Length > 100_000 ? 20_000 : 3_000)),
            _ => most,
        };
        int size = model.Length < 1_000 ? random.Next(5_000) : kind switch
        {
            < 40 => random.Next(3),
            < 70 => random.Next(120),
            _ => random.Next(4_000),
        };
        length = Boundary(model, start + length) - start;
        return (start, length, Random(random, size));
    }

    /// <summary>The offset, or the one after it where it falls inside a surrogate pair.</summary>
    private static int Boundary(string text, int offset) =>
        offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]) ? offset + 1 : offset;

    /// <summary>The text with a half of a pair cut off at its end taken off with it.</summary>
    private static string Whole(string text) => text.Length > 0 && char.IsHighSurrogate(text[^1]) ? text[..^1] + "y" : text;
}

/// <summary>A snapshot's reads checked against a string holding the same text.</summary>
internal static class Model
{
    public static void Check(TextSnapshot snapshot, string model, Random random, int probes)
    {
        List<int> starts = [0];
        for (int i = model.IndexOf('\n'); i >= 0; i = model.IndexOf('\n', i + 1))
        {
            starts.Add(i + 1);
        }
        Assert.Equal(starts.Count, snapshot.LineCount);
        for (int probe = 0; probe < probes; probe++)
        {
            int line = random.Next(starts.Count);
            int start = starts[line];
            int next = line + 1 < starts.Count ? starts[line + 1] : model.Length;
            int breakLength = line + 1 < starts.Count ? (next - 1 > start && model[next - 2] == '\r' ? 2 : 1) : 0;
            Assert.Equal(new LineSpan(line, start, next - start - breakLength, breakLength), snapshot.GetLine(line));

            int offset = random.Next(model.Length + 1);
            int found = starts.BinarySearch(offset);
            int onLine = found >= 0 ? found : ~found - 1;
            Assert.Equal(onLine, snapshot.GetLineFromOffset(offset));
            TextPosition position = snapshot.GetPosition(offset);
            Assert.Equal(Math.Min(offset, snapshot.GetLine(onLine).End), snapshot.GetOffset(position));

            int length = random.Next(Math.Min(5_000, model.Length - offset) + 1);
            Assert.Equal(model.Substring(offset, length), snapshot.GetText(offset, length));
            if (offset < model.Length)
            {
                Assert.Equal(model[offset], snapshot[offset]);
            }
        }
    }
}
