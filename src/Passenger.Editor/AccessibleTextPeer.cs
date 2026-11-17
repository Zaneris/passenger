using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>What a screen reader meets of a control drawing its own text (the code editor, the terminal): a document named
/// as its host names the control (by its file, by its tab), read-only where it is, whose plain value is the caret's line,
/// raised as changed as the caret goes to another line or the line changes, for a reader that reads only a value; a
/// system's own bridge reads the whole text from <see cref="Text"/>.</summary>
public sealed class AccessibleTextPeer : ControlAutomationPeer, IValueProvider
{
    /// <summary>The most of the caret's line given as the value, from its start, so a line of a megabyte costs a reader
    /// no more than a long one.</summary>
    public const int MostValue = 4096;

    /// <summary>The caret's line as last told, so a move along the line tells nothing.</summary>
    private int caretLine;

    public AccessibleTextPeer(Control owner, AccessibleText text) : base(owner)
    {
        Text = text;
        caretLine = text.LineOf(text.Caret);
        text.CaretMoved += (_, _) => FollowCaret(changed: false);
        text.Changed += (_, change) => FollowCaret(changed: Touches(change));
    }

    /// <summary>The text as a system's bridge reads it.</summary>
    public AccessibleText Text { get; }

    public bool IsReadOnly => Text.IsReadOnly;

    public string? Value
    {
        get
        {
            TextRange line = Text.LineAt(Text.Caret);
            return Text.Read(line.Start, Math.Min(line.Length, MostValue));
        }
    }

    /// <summary>Puts a value in the place of the caret's line, refused where the text is read-only.</summary>
    public void SetValue(string? value)
    {
        if (Text.IsReadOnly)
        {
            throw new InvalidOperationException("The text is read-only");
        }
        Text.Replace(Text.LineAt(Text.Caret), value ?? string.Empty);
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    /// <summary>Whether a change reaches the caret's line.</summary>
    private bool Touches(AccessibleTextChange change)
    {
        TextRange line = Text.LineAt(Text.Caret);
        return change.Start <= line.End && change.Start + change.Inserted >= line.Start;
    }

    /// <summary>Tells the value changed as the caret comes to another line, or its line changes.</summary>
    private void FollowCaret(bool changed)
    {
        int line = Text.LineOf(Text.Caret);
        if (line == caretLine && !changed)
        {
            return;
        }
        caretLine = line;
        RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, null, Value);
    }
}
