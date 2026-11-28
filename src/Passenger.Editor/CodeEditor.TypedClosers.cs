using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The closers the typing help wrote after an opener or a quote typed (<see cref="EditPlan.Closed"/>), which Tab
/// steps over while a single caret stays inside the pair on its line: followed by anchors, each let go once the caret
/// leaves its pair or the text no longer holds it, the oldest past <see cref="MostTypedClosers"/>.</summary>
public sealed partial class CodeEditor
{
    private const int MostTypedClosers = 64;

    private readonly List<(Anchor Opener, Anchor Closer)> typedClosers = [];

    /// <summary>Whether Tab steps over a closer the typing help wrote, where the caret stands right before it.</summary>
    public bool TabStepsOverClosers { get; set; } = true;

    /// <summary>Follows the closers a plan wrote, at one caret.</summary>
    private void KeepTypedClosers(EditPlan plan)
    {
        if (plan.Closed is not [int closer] || plan.Selections.Count != 1)
        {
            return;
        }
        if (typedClosers.Count == MostTypedClosers)
        {
            LetGoOfTypedCloser(0);
        }
        typedClosers.Add((document.AnchorAt(closer - 1, AnchorGravity.After), document.AnchorAt(closer, AnchorGravity.After)));
    }

    /// <summary>Each closer followed whose pair the caret now stands outside of, on another line, or that the text no
    /// longer holds, let go.</summary>
    private void FollowTypedClosers()
    {
        TextSnapshot text = document.Snapshot;
        int caret = selections.Primary.Caret;
        for (int i = typedClosers.Count - 1; i >= 0; i--)
        {
            int opener = typedClosers[i].Opener.Offset;
            int closer = typedClosers[i].Closer.Offset;
            bool held = !selections.HasMultiple && opener < caret && caret <= closer && closer < text.Length && Pairs(text[opener], text[closer])
                && text.GetLineFromOffset(opener) == text.GetLineFromOffset(closer);
            if (!held)
            {
                LetGoOfTypedCloser(i);
            }
        }

        static bool Pairs(char opener, char closer) => (opener, closer) is ('(', ')') or ('[', ']') or ('{', '}') or ('"', '"') or ('\'', '\'');
    }

    /// <summary>Tab at a caret right before a closer the typing help wrote: the caret put past it; false where none stands there.</summary>
    private bool StepsOverTypedCloser()
    {
        if (!TabStepsOverClosers || selections.HasMultiple || !selections.Primary.IsEmpty)
        {
            return false;
        }
        int caret = selections.Primary.Caret;
        int at = typedClosers.FindLastIndex(each => each.Closer.Offset == caret);
        if (at < 0)
        {
            return false;
        }
        LetGoOfTypedCloser(at);
        SetSelections(SelectionSet.Single(caret + 1), reveal: true);
        return true;
    }

    private void LetGoOfTypedCloser(int index)
    {
        typedClosers[index].Opener.Remove();
        typedClosers[index].Closer.Remove();
        typedClosers.RemoveAt(index);
    }

    /// <summary>Lets go of every closer followed: another document has closers of its own.</summary>
    private void ForgetTypedClosers()
    {
        for (int i = typedClosers.Count - 1; i >= 0; i--)
        {
            LetGoOfTypedCloser(i);
        }
    }
}
