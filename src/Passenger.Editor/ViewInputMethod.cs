using Avalonia;
using Avalonia.Input.TextInput;

namespace Passenger.Editor;

/// <summary>The platform's input method (accents, Chinese, Japanese and Korean composition) as a view drawing its own text
/// takes it: the text being composed, which the view draws at its caret until the input method commits it as text input,
/// and the caret's place, the surrounding text and the selection, each told once as it changes.</summary>
public abstract class ViewInputMethod(Visual view) : TextInputMethodClient
{
    /// <summary>What the input method was last told, so it hears of each change once.</summary>
    private (string Text, TextSelection Selection, Rect Cursor)? told;

    public override Visual TextViewVisual => view;
    public override bool SupportsPreedit => true;

    /// <summary>The text being composed; empty when nothing is.</summary>
    public string Composition { get; private set; } = string.Empty;

    /// <summary>Where in the composition the input method's cursor is; null at its end.</summary>
    public int? CompositionCursor { get; private set; }

    public sealed override void SetPreeditText(string? preeditText) => SetPreeditText(preeditText, null);

    public sealed override void SetPreeditText(string? preeditText, int? cursorPos)
    {
        Composition = preeditText ?? string.Empty;
        CompositionCursor = cursorPos;
        Composed();
    }

    /// <summary>The composition changed: the view draws it and tells where its caret is now.</summary>
    protected abstract void Composed();

    /// <summary>Forgets the composition as its text comes in as typed.</summary>
    public void Committed()
    {
        Composition = string.Empty;
        CompositionCursor = null;
    }

    /// <summary>Drops a composition under way, the platform's input method's too, so it is never finished where it was
    /// not begun.</summary>
    public void Discard()
    {
        if (Composition.Length == 0)
        {
            return;
        }
        Committed();
        RequestReset();
    }

    /// <summary>Raises what changed since the input method was last told: the surrounding text, the selection in it,
    /// the caret's rectangle.</summary>
    public void Follow()
    {
        (string Text, TextSelection Selection, Rect Cursor) now = (SurroundingText, Selection, CursorRectangle);
        (string Text, TextSelection Selection, Rect Cursor)? before = told;
        told = now;
        if (before?.Text != now.Text)
        {
            RaiseSurroundingTextChanged();
        }
        if (before?.Selection != now.Selection)
        {
            RaiseSelectionChanged();
        }
        if (before?.Cursor != now.Cursor)
        {
            RaiseCursorRectangleChanged();
        }
    }
}
