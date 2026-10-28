using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Places a document carries through its changes (<see cref="TextDocument.AnchorAt"/>, <see cref="TextDocument.RangeAt"/>,
/// <see cref="TextDocument.MarkAt"/>), as a breakpoint, a squiggle or a fold holds its place in the text.</summary>
public class AnchorTests
{
    [Theory]
    [InlineData(AnchorGravity.Before, 5)]
    [InlineData(AnchorGravity.After, 8)]
    public void Text_typed_at_an_anchor_goes_after_it_or_before_it_by_its_gravity(AnchorGravity gravity, int after)
    {
        TextDocument document = new("0123456789");
        Anchor anchor = document.AnchorAt(5, gravity);
        Edit(document, new TextEdit(5, 0, "abc"));
        Assert.Equal(after, anchor.Offset);
    }

    [Theory]
    [InlineData(3, AnchorGravity.Before, 3)]
    [InlineData(3, AnchorGravity.After, 3)]
    [InlineData(5, AnchorGravity.Before, 3)]
    [InlineData(5, AnchorGravity.After, 5)]
    [InlineData(7, AnchorGravity.Before, 5)]
    [InlineData(7, AnchorGravity.After, 5)]
    [InlineData(2, AnchorGravity.After, 2)]
    [InlineData(9, AnchorGravity.Before, 7)]
    public void A_replaced_stretch_keeps_an_anchor_at_its_start_takes_one_at_its_end_past_the_new_text_and_one_inside_by_gravity(int at, AnchorGravity gravity,
        int after)
    {
        TextDocument document = new("0123456789");
        Anchor anchor = document.AnchorAt(at, gravity);
        Edit(document, new TextEdit(3, 4, "ab"));
        Assert.Equal(after, anchor.Offset);
    }

    [Fact]
    public void Text_deleted_around_an_anchor_leaves_it_where_the_deletion_was_still_carried()
    {
        TextDocument document = new("line one\nline two\nline three");
        Anchor anchor = document.AnchorAt(14, AnchorGravity.After);
        Edit(document, new TextEdit(9, 9, string.Empty));
        Assert.True(anchor.IsLive);
        Assert.Equal(9, anchor.Offset);
        Edit(document, new TextEdit(0, 0, "// "));
        Assert.Equal(12, anchor.Offset);
    }

    [Fact]
    public void An_anchor_has_moved_when_a_handler_of_the_change_reads_it()
    {
        TextDocument document = new("abc");
        Anchor anchor = document.AnchorAt(2, AnchorGravity.Before);
        int seen = -1;
        document.Changed += (_, _) => seen = anchor.Offset;
        Edit(document, new TextEdit(0, 0, "xyz"));
        Assert.Equal(5, seen);
    }

    [Fact]
    public void Several_carets_edits_move_an_anchor_by_what_each_before_it_changed()
    {
        TextDocument document = new("a1b2c3d4");
        Anchor[] anchors = [.. Enumerable.Range(0, 9).Select(i => document.AnchorAt(i, AnchorGravity.Before))];
        Edit(document, new TextEdit(1, 1, "11"), new TextEdit(3, 1, string.Empty), new TextEdit(6, 0, "-"));
        Assert.Equal("a11bc3-d4", document.Snapshot.ToString());
        Assert.Equal([0, 1, 3, 4, 4, 5, 6, 8, 9], anchors.Select(a => a.Offset));
    }

    /// <summary>An insertion at the start of a removal, as one caret's typing over another's selection makes: what was inside the
    /// removal is pressed to its start and the text inserted there goes before it, whatever its gravity.</summary>
    [Fact]
    public void An_anchor_inside_a_removal_that_text_is_inserted_at_the_start_of_goes_after_that_text()
    {
        TextDocument document = new("abcXYZdef");
        Anchor inside = document.AnchorAt(4, AnchorGravity.Before);
        Anchor atStart = document.AnchorAt(3, AnchorGravity.Before);
        Edit(document, new TextEdit(3, 0, "new"), new TextEdit(3, 3, string.Empty));
        Assert.Equal("abcnewdef", document.Snapshot.ToString());
        Assert.Equal(6, inside.Offset);
        Assert.Equal(3, atStart.Offset);
    }

    [Fact]
    public void A_mark_keeps_out_what_is_typed_at_its_start_takes_in_what_is_typed_at_its_end_and_once_emptied_stays_empty()
    {
        TextDocument document = new("call(name);");
        AnchoredRange mark = document.MarkAt(TextRange.FromBounds(5, 9));
        Edit(document, new TextEdit(5, 0, "@"));
        Assert.Equal(TextRange.FromBounds(6, 10), mark.Range);
        Edit(document, new TextEdit(10, 0, "s"));
        Assert.Equal(TextRange.FromBounds(6, 11), mark.Range);
        Edit(document, new TextEdit(6, 5, string.Empty));
        Assert.Equal(new TextRange(6, 0), mark.Range);
        Edit(document, new TextEdit(6, 0, "other"));
        Assert.Equal(new TextRange(6, 0), mark.Range);
        Assert.Equal("call(@other);", document.Snapshot.ToString());
    }

    [Fact]
    public void A_range_never_ends_before_it_starts_and_a_removed_one_stays_where_it_was()
    {
        TextDocument document = new("0123456789");
        AnchoredRange range = document.RangeAt(new TextRange(5, 0), AnchorGravity.After, AnchorGravity.Before);
        Edit(document, new TextEdit(5, 0, "abc"));
        Assert.Equal(5, range.EndAnchor.Offset);
        Assert.Equal(new TextRange(8, 0), range.Range);
        int anchors = document.Anchors;
        range.Remove();
        Assert.False(range.IsLive);
        Assert.Equal(anchors - 2, document.Anchors);
        Edit(document, new TextEdit(0, 0, "zz"));
        Assert.Equal(new TextRange(8, 0), range.Range);
    }

    [Fact]
    public void An_anchor_moved_elsewhere_is_carried_from_there_and_one_removed_keeps_its_last_offset()
    {
        TextDocument document = new("0123456789");
        Anchor anchor = document.AnchorAt(2, AnchorGravity.Before);
        anchor.MoveTo(8);
        Edit(document, new TextEdit(0, 0, "ab"));
        Assert.Equal(10, anchor.Offset);
        anchor.Remove();
        Edit(document, new TextEdit(0, 0, "cd"));
        Assert.Equal(10, anchor.Offset);
        Assert.Equal(0, document.Anchors);
        Assert.Equal(14, document.AnchorAt(99, AnchorGravity.Before).Offset);
    }

    /// <summary>Hundreds of anchors of both gravities through a thousand seeded random changes of one to four edits each, every
    /// anchor's offset checked after each change against the rules worked out edit by edit from the last.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_seeded_run_of_random_changes_carries_every_anchor_as_the_rules_say(int seed)
    {
        Random random = new(seed);
        TextDocument document = new(Texts.Random(random, 4_000));
        List<(Anchor Anchor, int Expected)> anchors = [];
        for (int i = 0; i < 300; i++)
        {
            int at = random.Next(document.Snapshot.Length + 1);
            anchors.Add((document.AnchorAt(at, random.Next(2) == 0 ? AnchorGravity.Before : AnchorGravity.After), at));
        }
        for (int step = 0; step < 1_000; step++)
        {
            List<TextEdit> edits = RandomEdits(random, document.Snapshot.Length);
            for (int i = 0; i < anchors.Count; i++)
            {
                int expected = anchors[i].Expected;
                for (int e = edits.Count - 1; e >= 0; e--)
                {
                    expected = Carried(expected, anchors[i].Anchor.Gravity, edits[e]);
                }
                anchors[i] = (anchors[i].Anchor, expected);
            }
            Edit(document, [.. edits]);
            foreach ((Anchor anchor, int expected) in anchors)
            {
                if (expected != anchor.Offset)
                {
                    Assert.Fail($"Change {step} ({string.Join(", ", edits)}) left an anchor of gravity {anchor.Gravity} at {anchor.Offset}, where the rules put it at {expected}");
                }
            }
            if (step % 50 == 0)
            {
                int replaced = random.Next(anchors.Count);
                anchors[replaced].Anchor.Remove();
                int at = random.Next(document.Snapshot.Length + 1);
                anchors[replaced] = (document.AnchorAt(at, AnchorGravity.After), at);
            }
        }
        Assert.Equal(anchors.Count, document.Anchors);
    }

    /// <summary>One edit's rule: before it unmoved, past it shifted, at an insertion by gravity, at a stretch's start kept, at
    /// its end past the new text, inside it to its start or with After past the new text.</summary>
    private static int Carried(int offset, AnchorGravity gravity, TextEdit edit)
    {
        if (offset < edit.Start)
        {
            return offset;
        }
        if (offset > edit.End)
        {
            return offset + edit.Text.Length - edit.Length;
        }
        bool after = edit.Length == 0 || (offset > edit.Start && offset < edit.End) ? gravity == AnchorGravity.After : offset == edit.End;
        return after ? edit.Start + edit.Text.Length : edit.Start;
    }

    /// <summary>One to four edits in order, a character at least between each, so no anchor is pressed from one into the next.</summary>
    private static List<TextEdit> RandomEdits(Random random, int length)
    {
        List<TextEdit> edits = [];
        int from = 0;
        int count = random.Next(1, 5);
        for (int i = 0; i < count && from <= length; i++)
        {
            int start = from + random.Next(Math.Min(400, length - from) + 1);
            int removed = random.Next(Math.Min(random.Next(2) == 0 ? 3 : 60, length - start) + 1);
            string inserted = random.Next(3) == 0 ? string.Empty : Texts.Random(random, random.Next(1, 40));
            if (removed > 0 || inserted.Length > 0)
            {
                edits.Add(new TextEdit(start, removed, inserted));
            }
            from = start + removed + 1;
        }
        return edits.Count > 0 ? edits : [new TextEdit(0, 0, "x")];
    }

    private static void Edit(TextDocument document, params TextEdit[] edits) =>
        document.Edit(edits, SelectionSet.Single(0), SelectionSet.Single(0));
}
