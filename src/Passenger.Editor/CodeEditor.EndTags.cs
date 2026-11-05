using Passenger.Text;

namespace Passenger.Editor;

/// <summary>An end tag typed by hand where the editor wrote it already (<see cref="EndTagTyping"/>): held only while the text
/// and carets stay as the last key left them, so any other edit or move ends it.</summary>
public sealed partial class CodeEditor
{
    private (EndTagTyping Typing, TextSnapshot Text, SelectionSet Carets)? typingOver;

    /// <summary>A key passed the end tag it was typed over: the text changed in nothing, so nothing is to be listed for it.</summary>
    public event EventHandler? TypedOverEndTag;

    private EndTagTyping? TypingOverEndTag =>
        typingOver is { } over && ReferenceEquals(over.Text, document.Snapshot) && over.Carets.SameAs(selections) ? over.Typing : null;

    /// <summary>Makes an end tag's completion worked out away from the keys (<see cref="TextEditing.CloseTag"/>), a step of
    /// Undo of its own, typed over from here on as one the keys made.</summary>
    public void CloseEndTag(EditPlan closing)
    {
        ApplyEdits(closing.Edits, closing.Selections);
        KeepTypingOver(closing.EndTags);
    }

    private void KeepTypingOver(EndTagTyping? typing) => typingOver = typing is null ? null : (typing, document.Snapshot, selections);

    /// <summary>Whether what was typed passes the end tag being typed over; one that differs leaves the tag whole for the
    /// key to land as usual.</summary>
    private bool PassesEndTag(string typed)
    {
        if (TypingOverEndTag is not { } typing)
        {
            return false;
        }
        if (!typing.Passes(typed))
        {
            LeaveEndTag();
            return false;
        }
        SetSelections(typing.Past(selections, typed), reveal: true);
        KeepTypingOver(typing.After(typed));
        return true;
    }

    /// <summary>Backspace while an end tag is typed over takes back a key passed, or the "&lt;/" itself, and never a letter of
    /// the tag. Whether that was all it had to do.</summary>
    private bool StepsBackOverEndTag()
    {
        if (TypingOverEndTag is not { CanStepBack: true } typing)
        {
            LeaveEndTag();
            return false;
        }
        TextSnapshot text = document.Snapshot;
        EndTagTyping left = typing.StepBack(text, selections);
        SetSelections(typing.Back(text, selections), reveal: true);
        KeepTypingOver(left);
        return true;
    }

    /// <summary>Ends typing over an end tag for what is not its next key, the tag left whole.</summary>
    private void LeaveEndTag()
    {
        EndTagTyping? typing = TypingOverEndTag;
        typingOver = null;
        Apply(typing?.PutBack(selections));
    }
}
