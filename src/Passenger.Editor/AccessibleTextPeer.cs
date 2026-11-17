using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Passenger.Editor;

/// <summary>What a screen reader meets of a control drawing its own text (the code editor, the terminal): a document named
/// as its host names the control (by its file, by its tab), read-only where it is, whose plain value is the caret's line,
/// raised as changed as the caret goes to another line or the line changes, for a reader that reads only a value; a
/// system's own bridge reads the whole text from <see cref="Text"/>.</summary>
public sealed class AccessibleTextPeer : ControlAutomationPeer, IValueProvider
{
    public AccessibleTextPeer(Control owner, AccessibleText text) : base(owner)
    {
        Text = text;
        text.CaretLineChanged += (_, _) => RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, null, Value);
    }

    /// <summary>The text as a system's bridge reads it.</summary>
    public AccessibleText Text { get; }

    public bool IsReadOnly => Text.IsReadOnly;

    public string? Value => Text.CaretLine;

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
}
