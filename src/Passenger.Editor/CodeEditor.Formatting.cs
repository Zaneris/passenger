using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What the host formats once a key has typed it (<see cref="FormatsTyped"/>): the key begins a step of Undo of its
/// own, or goes on in Vim's insert session, and the formatting joins that step, so one Undo takes the key and its
/// formatting back together. Held only until the next key.</summary>
public sealed partial class CodeEditor
{
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
        if (formattedKey is not { } place || !ReferenceEquals(document.Snapshot, asked) || IsReadOnly || edits.Count == 0
            || edits[^1].End > selections.Items.Min(s => s.Start))
        {
            return;
        }
        formattedKey = null;
        SelectionSet after = TextEditing.MapThrough(selections, edits);
        if (vim is { Mode: VimMode.Insert })
        {
            vim.FollowEdits(edits);
            Apply(new EditPlan(edits, after, EditKind.Joined));
            return;
        }
        ApplyEdits(edits, after);
        document.JoinUndoSince(place);
    }
}
