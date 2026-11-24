using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What the host formats once a key has typed it (<see cref="FormatsTyped"/>), Enter that opens a block between a
/// '{' and its '}' among them (<see cref="OpenedBlock"/>): the key begins a step of Undo of its own, or goes on in Vim's
/// insert session, and the formatting joins that step, so one Undo takes the key and its formatting back together. Held
/// only until the next key. And a block a line break opened between a pair of brackets, which the host formats once the
/// caret leaves it (<see cref="LeftBlock"/>), as a step of its own.</summary>
public sealed partial class CodeEditor
{
    /// <summary>The most blocks followed at once (<see cref="splitBlocks"/>), one inside another.</summary>
    private const int MostSplitBlocks = 16;

    /// <summary>The blocks a line break split a pair of brackets for at one caret, by their brackets, followed until the
    /// caret leaves them.</summary>
    private readonly List<(Anchor Opener, Anchor Closer)> splitBlocks = [];

    /// <summary>The text's version <see cref="FollowSplitBlocks"/> last looked at, so a caret moved by an edit is told
    /// from one moved alone.</summary>
    private int splitBlocksSeen;

    /// <summary>Set while Undo or Redo puts carets back: a block they leave is let go untold, never formatted as it is undone.</summary>
    private bool splitBlocksQuiet;

    /// <summary>The caret left a block a line break opened between a pair of brackets (<see cref="EditPlan.Split"/>), by a
    /// move or an edit that was no key the host formats itself: the offset of the block's closer, the text otherwise as it
    /// stood, for the host to format what it closes.</summary>
    public event EventHandler<int>? LeftBlock;

    /// <summary>Enter opened a block between a '{' and its '}' at the one caret, where the host formats a line break typed
    /// (<see cref="FormatsTyped"/>): the host lays the block out, the caret on the line between (<see cref="FormatKeyAt"/>).</summary>
    public event EventHandler? OpenedBlock;

    /// <summary>Enter at the one caret opened no block, where the host formats a line break typed (<see cref="FormatsTyped"/>):
    /// the host writes what goes on from the line it broke, if anything, the caret where that puts it (<see cref="FormatKeyAt"/>).</summary>
    public event EventHandler? LineBroken;

    /// <summary>Enter, as <see cref="TextEditing.NewLine"/> plans it: at one caret, where the host formats a line break,
    /// it begins the step of Undo the host's layout joins and has the host lay out the block it opened between a '{' and
    /// its '}', or what goes on from the line it broke.</summary>
    private void NewLine(EditPlan plan)
    {
        bool opened = plan.Split is [SplitPair pair] && document.Snapshot[pair.Opener] == '{';
        formattedKey = plan.Selections.Count == 1 ? BeginFormattedKey("\n") : null;
        Apply(plan);
        if (formattedKey is not null)
        {
            (opened ? OpenedBlock : LineBroken)?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Follows the blocks an edit split a pair of brackets for, at one caret.</summary>
    private void KeepSplitBlocks(EditPlan plan)
    {
        if (plan.Split is not [SplitPair pair] || plan.Selections.Count != 1)
        {
            return;
        }
        if (splitBlocks.Count == MostSplitBlocks)
        {
            LetGoOfSplitBlock(0);
        }
        splitBlocks.Add((document.AnchorAt(pair.Opener, AnchorGravity.After), document.AnchorAt(pair.Closer, AnchorGravity.After)));
    }

    /// <summary>Each block followed that the caret now stands outside of, or that the text no longer holds split, let go:
    /// one left whole is told (<see cref="LeftBlock"/>), but where Undo or Redo, or a key the host formats itself, took the
    /// caret out, or there are several carets.</summary>
    private void FollowSplitBlocks()
    {
        bool edited = document.Version != splitBlocksSeen;
        splitBlocksSeen = document.Version;
        TextSnapshot text = document.Snapshot;
        int caret = selections.Primary.Caret;
        for (int i = splitBlocks.Count - 1; i >= 0; i--)
        {
            int opener = splitBlocks[i].Opener.Offset;
            int closer = splitBlocks[i].Closer.Offset;
            bool whole = opener < closer && closer < text.Length && IsBracket(text[opener], opener: true) && IsBracket(text[closer], opener: false)
                && text.GetLineFromOffset(opener) < text.GetLineFromOffset(closer);
            if (whole && !selections.HasMultiple && caret > opener && caret <= closer)
            {
                continue;
            }
            LetGoOfSplitBlock(i);
            if (whole && !selections.HasMultiple && !splitBlocksQuiet && !(edited && formattedKey is not null))
            {
                LeftBlock?.Invoke(this, closer);
            }
        }
    }

    /// <summary>The carets Undo or Redo puts back, letting go of the blocks they leave untold.</summary>
    private void SetSelectionsQuietly(SelectionSet carets)
    {
        splitBlocksQuiet = true;
        try
        {
            SetSelections(carets, reveal: true);
        }
        finally
        {
            splitBlocksQuiet = false;
        }
    }

    private void LetGoOfSplitBlock(int index)
    {
        splitBlocks[index].Opener.Remove();
        splitBlocks[index].Closer.Remove();
        splitBlocks.RemoveAt(index);
    }

    /// <summary>Lets go of every block followed, telling of none: another document has blocks of its own.</summary>
    private void ForgetSplitBlocks()
    {
        for (int i = splitBlocks.Count - 1; i >= 0; i--)
        {
            LetGoOfSplitBlock(i);
        }
    }

    /// <summary>Where the history stood before the last key, when the host formats what it typed; null once another key
    /// is pressed or an action runs.</summary>
    private long? formattedKey;

    /// <summary>Whether the host formats what a key's text types, asked before it is typed (<see cref="FormatTyped"/>).</summary>
    public Func<string, bool>? FormatsTyped { get; set; }

    /// <summary>The step of Undo a key's text begins where the host formats what it types: one of its own, but in Vim's insert
    /// session, which it goes on; null where the host formats nothing of it.</summary>
    private long? BeginFormattedKey(string typed)
    {
        if (FormatsTyped?.Invoke(typed) != true)
        {
            return null;
        }
        if (vim is not { Mode: VimMode.Insert })
        {
            document.BreakUndoGroup();
        }
        return document.UndoPlace;
    }

    /// <summary>The host's formatting of what the last key typed (<see cref="FormatsTyped"/>): <paramref name="edits"/>,
    /// sorted and apart and all before the carets, in <paramref name="asked"/>, the text the host worked from, made while
    /// the text still stands so and no key has been pressed since, joined to the key's step of Undo, the carets after what
    /// was typed.</summary>
    public void FormatTyped(TextSnapshot asked, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count > 0 && edits[^1].End <= selections.Items.Min(s => s.Start))
        {
            JoinFormatting(asked, edits, TextEditing.MapThrough(selections, edits));
        }
    }

    /// <summary>The host's formatting of what the last key typed at the one caret, wherever it writes (the block Enter
    /// opened laid out, <see cref="OpenedBlock"/>, or what goes on from a line, <see cref="LineBroken"/>): <paramref name="edits"/>,
    /// sorted and apart, in <paramref name="asked"/>, the text the host worked from, made while the text still stands so and no
    /// key has been pressed since, joined to the key's step of Undo, the caret at <paramref name="caret"/> in the text they make.</summary>
    public void FormatKeyAt(TextSnapshot asked, IReadOnlyList<TextEdit> edits, int caret)
    {
        if (edits.Count > 0 && !selections.HasMultiple && caret >= 0 && caret <= asked.Length + edits.Sum(e => e.Text.Length - e.Length))
        {
            JoinFormatting(asked, edits, SelectionSet.Single(caret));
        }
    }

    /// <summary>The host's formatting of what the last key typed, joined to its step of Undo or Vim's insert session, the
    /// carets <paramref name="after"/>; made only while the text stands as <paramref name="asked"/> and no key has been
    /// pressed since.</summary>
    private void JoinFormatting(TextSnapshot asked, IReadOnlyList<TextEdit> edits, SelectionSet after)
    {
        if (formattedKey is not { } place || !ReferenceEquals(document.Snapshot, asked) || IsReadOnly)
        {
            return;
        }
        formattedKey = null;
        if (vim is { Mode: VimMode.Insert })
        {
            vim.FollowEdits(edits, selections.Primary.Caret);
            Apply(new EditPlan(edits, after, EditKind.Joined));
            return;
        }
        ApplyEdits(edits, after);
        document.JoinUndoSince(place);
    }

    /// <summary>The host's formatting of a block the caret left (<see cref="LeftBlock"/>): <paramref name="edits"/>, sorted
    /// and apart, in <paramref name="asked"/>, made while the text still stands so, as a step of Undo of its own, or in
    /// Vim's insert session, as part of its one step.</summary>
    public void FormatLeftBlock(TextSnapshot asked, IReadOnlyList<TextEdit> edits)
    {
        if (!ReferenceEquals(document.Snapshot, asked) || IsReadOnly || edits.Count == 0)
        {
            return;
        }
        SelectionSet after = TextEditing.MapThrough(selections, edits);
        if (vim is { Mode: VimMode.Insert })
        {
            vim.FollowEdits(edits, selections.Primary.Caret);
            Apply(new EditPlan(edits, after, EditKind.Joined));
            return;
        }
        ApplyEdits(edits, after);
    }
}
