using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Text a command wrote just before the caret that a hand used to typing it types again (a call's brackets a
/// completion wrote): held only while the text and carets stay as it or the last key taken left them.</summary>
public sealed partial class CodeEditor
{
    private (string Left, TextSnapshot Text, SelectionSet Carets)? typedAgain;

    /// <summary>The next keys that are characters of <paramref name="written"/>, which stands just before the caret, are
    /// taken as typed already, in its order, one passing over those before it, the text changing in nothing; any other key,
    /// edit or move ends it.</summary>
    public void TakeTypedAgain(string written) => typedAgain = written.Length == 0 ? null : (written, document.Snapshot, selections);

    /// <summary>Whether a key was one of the written characters still to come, now taken.</summary>
    private bool TakesTypedAgain(string typed)
    {
        if (typedAgain is not { } again)
        {
            return false;
        }
        typedAgain = null;
        int at = typed.Length == 1 && ReferenceEquals(again.Text, document.Snapshot) && again.Carets.SameAs(selections) ? again.Left.IndexOf(typed[0], StringComparison.Ordinal) : -1;
        if (at < 0)
        {
            return false;
        }
        TakeTypedAgain(again.Left[(at + 1)..]);
        return true;
    }
}
