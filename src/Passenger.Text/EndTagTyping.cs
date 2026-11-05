namespace Passenger.Text;

/// <summary>An end tag one caret types over: the tag whole; <paramref name="Ahead"/> where the caret stands inside it, what
/// is typed belonging before it, else where "&lt;/" just wrote it and the caret stands after it, what is typed belonging
/// there.</summary>
public readonly record struct TagTypedOver(string Tag, bool Ahead);

/// <summary>End tags the editor wrote that a person goes on to type by hand, one per caret, from their "&lt;/": a key that
/// comes next in every tag passes it, the text as it was, so the keys end after one tag however fast they come. The first
/// that doesn't leaves each tag whole, what was passed put back as typed, for that key to land as if nothing had been
/// passed: a mistyped or other name is what was typed, never a name run into the tag.</summary>
public sealed record EndTagTyping(IReadOnlyList<TagTypedOver> Tags)
{
    /// <summary>The length of the "&lt;/" every tag starts with, passed before anything else is.</summary>
    private const int Begun = 2;

    /// <summary>How much of each tag has been passed.</summary>
    private int Passed { get; init; } = Begun;

    /// <summary>Whether every caret stands inside its tag, as a tag written before the "&lt;/" was typed leaves it.</summary>
    public bool Ahead => Tags.All(t => t.Ahead);

    /// <summary>Whether what was typed comes next in every tag.</summary>
    public bool Passes(string typed) => Tags.All(t => t.Tag.AsSpan(Passed).StartsWith(typed, StringComparison.Ordinal));

    /// <summary>The carets past what was typed.</summary>
    public SelectionSet Past(SelectionSet carets, string typed) => Moved(carets, Passed + typed.Length);

    /// <summary>What is left to type once what was typed has passed; null at a tag's end.</summary>
    public EndTagTyping? After(string typed) => Passed + typed.Length < Tags[0].Tag.Length ? this with { Passed = Passed + typed.Length } : null;

    /// <summary>Whether Backspace has a key of the tag to take back, the text as it was.</summary>
    public bool CanStepBack => Passed > Begun;

    /// <summary>The carets a key back (<see cref="CanStepBack"/>), a letter whole; a tag "&lt;/" wrote has its caret after it
    /// again once nothing of it is passed.</summary>
    public SelectionSet Back(TextSnapshot text, SelectionSet carets) => Moved(carets, PassedBack(text, carets));

    /// <summary>What is left to type a key back (<see cref="CanStepBack"/>).</summary>
    public EndTagTyping StepBack(TextSnapshot text, SelectionSet carets) => this with { Passed = PassedBack(text, carets) };

    /// <summary>Each tag left whole, what was passed put back as typed: before a tag the caret stood inside, after one it
    /// stood behind; null where nothing was passed but a tag the caret already stands after.</summary>
    public EditPlan? PutBack(SelectionSet carets)
    {
        List<TextEdit> edits = [];
        Selection[] placed = new Selection[carets.Count];
        int shift = 0;
        for (int i = 0; i < carets.Count; i++)
        {
            int caret = carets.Items[i].Caret;
            TagTypedOver over = Tags[i];
            if (over.Ahead)
            {
                // What was passed stands before the caret already: written again after it, the tag is whole again.
                edits.Add(new TextEdit(caret, 0, over.Tag[..Passed]));
                placed[i] = Selection.At(caret + shift);
                shift += Passed;
            }
            else if (Passed > Begun)
            {
                string typed = over.Tag[Begun..Passed];
                int end = caret + over.Tag.Length - Passed;
                edits.Add(new TextEdit(end, 0, typed));
                shift += typed.Length;
                placed[i] = Selection.At(end + shift);
            }
            else
            {
                placed[i] = Selection.At(caret + shift);
            }
        }
        return edits.Count == 0 ? null : new EditPlan(edits, SelectionSet.Create(placed, carets.PrimaryIndex), EditKind.Typing);
    }

    private int PassedBack(TextSnapshot text, SelectionSet carets)
    {
        int caret = carets.Primary.Caret;
        return Math.Max(Begun, Passed - (caret - TextNavigation.PreviousGrapheme(text, caret)));
    }

    /// <summary>The carets where <paramref name="passed"/> of each tag has been passed.</summary>
    private SelectionSet Moved(SelectionSet carets, int passed)
    {
        Selection[] moved = new Selection[carets.Count];
        for (int i = 0; i < carets.Count; i++)
        {
            TagTypedOver over = Tags[i];
            int caret = carets.Items[i].Caret;
            int start = caret - (over.Ahead || Passed > Begun ? Passed : over.Tag.Length);
            moved[i] = Selection.At(start + (over.Ahead || passed > Begun ? passed : over.Tag.Length));
        }
        return SelectionSet.Create(moved, carets.PrimaryIndex);
    }
}
