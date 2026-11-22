using Avalonia;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>A screen reader's search of a control's text (<see cref="AccessibleText.Find"/>), read a piece at a time: it
/// finds what a search of the whole text at once finds, forward and backward, ignoring case or not, a match across two
/// pieces too, and never reads more than a piece of the text at once.</summary>
public class AccessibleTextSearchTests
{
    [Fact]
    public void A_screen_readers_search_read_a_piece_at_a_time_finds_what_a_whole_search_does() => Cases.All(
        ("A search read in pieces finds what one of the whole text finds and reads no more than a piece at once", A_search_read_in_pieces_finds_what_one_of_the_whole_text_finds_and_reads_no_more_than_a_piece_at_once),
        ("A match across every place a piece may end is found", A_match_across_every_place_a_piece_may_end_is_found));

    private void A_search_read_in_pieces_finds_what_one_of_the_whole_text_finds_and_reads_no_more_than_a_piece_at_once()
    {
        Random random = new(1846);
        const string Letters = "abcdeABCDE \n";
        string whole = string.Create(100_000, random, static (into, chosen) =>
        {
            for (int i = 0; i < into.Length; i++)
            {
                into[i] = Letters[chosen.Next(Letters.Length)];
            }
        });
        Pieces text = new(whole);
        for (int round = 0; round < 400; round++)
        {
            int from = random.Next(whole.Length);
            int to = from + random.Next(whole.Length - from + 1);
            int at = random.Next(whole.Length - 40);
            string sought = whole.Substring(at, 1 + random.Next(40));
            bool backward = random.Next(2) == 0;
            bool ignoreCase = random.Next(2) == 0;
            if (ignoreCase)
            {
                sought = sought.ToUpperInvariant();
            }
            StringComparison comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            string within = whole[from..to];
            int expected = backward ? within.LastIndexOf(sought, comparison) : within.IndexOf(sought, comparison);
            TextRange? found = text.Find(TextRange.FromBounds(from, to), sought, backward, ignoreCase);
            Assert.True(found == (expected < 0 ? null : new TextRange(from + expected, sought.Length)),
                $"Searching {from}..{to} {(backward ? "backward" : "forward")} for \"{sought}\"{(ignoreCase ? " ignoring case" : string.Empty)} found {found}, "
                + $"where a search of the whole range at once finds {(expected < 0 ? "none" : from + expected)}");
        }
        Assert.True(text.Widest is > 0 and < 20_000, $"The search read {text.Widest:N0} characters at once of {whole.Length:N0}");
    }

    private void A_match_across_every_place_a_piece_may_end_is_found() => Cases.Each<bool>(
    [
        false,
        true,
    ], backward =>
    {
        const string Sought = "needle";
        char[] filler = new string('.', 70_000).ToCharArray();
        for (int at = 1; at + Sought.Length < filler.Length; at += 997)
        {
            filler.AsSpan(0).Fill('.');
            Sought.AsSpan().CopyTo(filler.AsSpan(at));
            Pieces text = new(new string(filler));
            Assert.True(text.Find(new TextRange(0, filler.Length), "NEEDLE", backward, ignoreCase: true) == new TextRange(at, Sought.Length),
                $"\"{Sought}\" at {at} was found {(backward ? "backward" : "forward")} at {text.Find(new TextRange(0, filler.Length), "NEEDLE", backward, ignoreCase: true)}");
        }
    });

    /// <summary>A text held as a string, keeping the most it was asked to read at once.</summary>
    private sealed class Pieces(string whole) : AccessibleText
    {
        public int Widest { get; private set; }

        public override int Length => whole.Length;

        public override int LineCount => 1;

        public override LineSpan Line(int line) => new(0, 0, whole.Length, 0);

        public override int LineOf(int offset) => 0;

        public override string Read(int start, int length)
        {
            Widest = Math.Max(Widest, length);
            return whole.Substring(start, length);
        }

        public override int Caret => 0;

        public override IReadOnlyList<TextRange> Selected => [];

        public override bool IsReadOnly => true;

        public override void Replace(TextRange range, string text) => throw new InvalidOperationException("Read-only");

        public override IReadOnlyList<Rect> BoundsOf(TextRange range) => [];

        public override int OffsetAt(Point point) => 0;

        public override TextRange Shown => new(0, whole.Length);

        public override void Select(TextRange range)
        {
        }

        public override void Show(TextRange range)
        {
        }
    }
}
