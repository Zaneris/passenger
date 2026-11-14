using Avalonia;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Passenger.Editor;

namespace Passenger.Terminal;

/// <summary>Text an input method composes (Chinese by Pinyin, Japanese, Korean, an accent's dead key): drawn over the cells
/// from the cursor in the columns the program will count for it, underlined, the input method's cursor in it, and sent
/// only as the input method commits it, which comes as text input. The input method's window opens at the cursor and
/// follows it as output moves it.</summary>
public sealed partial class TerminalView
{
    private TerminalInputMethod? inputMethod;

    /// <summary>The composition as drawn, kept until it, the cursor's column, the screen's width or the look changes.</summary>
    private Laid? composed;

    private TerminalInputMethod InputMethod => inputMethod ??= new TerminalInputMethod(this);

    private bool Composing => inputMethod is { Composition.Length: > 0 };

    /// <summary>The composition changed: the view shows the screen's foot, where it is drawn at the cursor.</summary>
    private void Compose()
    {
        ForgetComposed();
        if (Composing)
        {
            heldTop = null;
        }
        RestartBlink();
        FollowInputMethod();
    }

    private void ForgetComposed()
    {
        composed?.Dispose();
        composed = null;
    }

    /// <summary>Tells a focused view's input method where the cursor is drawn now.</summary>
    private void FollowInputMethod()
    {
        if (inputMethod is not null && IsFocused)
        {
            inputMethod.Follow();
        }
    }

    /// <summary>The composition's characters laid from a column on rows as wide as the screen's, each shaped to fit its
    /// cells: the one kept while nothing it was made from changed.</summary>
    private Laid ComposedAt(int column, int columns)
    {
        string text = inputMethod?.Composition ?? string.Empty;
        int? at = inputMethod?.CompositionCursor;
        if (composed is { } kept && kept.Text == text && kept.Cursor == at && kept.Column == column && kept.Columns == columns)
        {
            return kept;
        }
        ForgetComposed();
        List<ComposedCell> cells = ComposedCells.Lay(text, column, columns);
        Typeface plain = new(FontFamily);
        List<(ComposedCell Cell, TextLine? Text, Point At)> parts = new(cells.Count);
        foreach (ComposedCell cell in cells)
        {
            (TextLine? shaped, Point offset) = Fit(text.AsMemory(cell.Start, cell.Length), new Rect(0, 0, cell.Width * cellWidth, cellHeight), plain, foreground);
            parts.Add((cell, shaped, offset));
        }
        return composed = new Laid(text, at, column, columns, parts, ComposedCells.CursorOf(cells, at, column));
    }

    /// <summary>The composition over its cells from the cursor's, the text under it hidden and each of its rows
    /// underlined whole until the input method commits it, and the input method's cursor in it as a bar.</summary>
    private void DrawComposition(DrawingContext context, TerminalScreen screen)
    {
        Laid shown = ComposedAt(Math.Min(screen.CursorColumn, screen.Columns - 1), screen.Columns);
        double top = Pad + (screen.CursorRow * cellHeight);
        for (int first = 0; first < shown.Parts.Count;)
        {
            int row = shown.Parts[first].Cell.Row;
            int end = first + 1;
            while (end < shown.Parts.Count && shown.Parts[end].Cell.Row == row)
            {
                end++;
            }
            ComposedCell last = shown.Parts[end - 1].Cell;
            double x = Pad + (shown.Parts[first].Cell.Column * cellWidth);
            Rect span = new(x, top + (row * cellHeight), Pad + ((last.Column + last.Width) * cellWidth) - x, cellHeight);
            context.FillRectangle(background, span);
            for (int i = first; i < end; i++)
            {
                (ComposedCell cell, TextLine? text, Point at) = shown.Parts[i];
                if (text is not null)
                {
                    Rect area = new(Pad + (cell.Column * cellWidth), span.Y, cell.Width * cellWidth, cellHeight);
                    using (context.PushClip(area))
                    {
                        text.Draw(context, area.TopLeft + at);
                    }
                }
            }
            context.FillRectangle(foreground, new Rect(span.X, span.Bottom - 2, span.Width, 1));
            first = end;
        }
        if (IsFocused && cursorOn)
        {
            context.FillRectangle(cursor, new Rect(Pad + (shown.CursorAt.Column * cellWidth), top + (shown.CursorAt.Row * cellHeight), 2, cellHeight));
        }
    }

    /// <summary>Where the input method's cursor is drawn: in the composition, at the input method's cursor; else at the
    /// terminal's cursor, shown or not.</summary>
    private Rect InputCursor()
    {
        if (session is null)
        {
            return default;
        }
        TerminalScreen screen = session.Screen;
        int column = Math.Min(screen.CursorColumn, screen.Columns - 1);
        int row = screen.CursorRow;
        if (Composing)
        {
            (int below, int at) = ComposedAt(column, screen.Columns).CursorAt;
            row += below;
            column = at;
        }
        return new Rect(Pad + (column * cellWidth), Pad + ((screen.ScreenTop + row - TopLine) * cellHeight), 2, cellHeight);
    }

    /// <summary>A composition laid and shaped: what it was made from (the text, the input method's cursor in it, the
    /// cursor's column and the screen's width), each character's cells, text and the offset its text is drawn at in them,
    /// and the input method's cursor's cell.</summary>
    private sealed record Laid(string Text, int? Cursor, int Column, int Columns,
        List<(ComposedCell Cell, TextLine? Text, Point At)> Parts, (int Row, int Column) CursorAt) : IDisposable
    {
        public void Dispose()
        {
            foreach ((_, TextLine? text, _) in Parts)
            {
                text?.Dispose();
            }
        }
    }

    /// <summary>What the platform's input method reads of the terminal: where its cursor is drawn, and no text around it,
    /// as the line a program edits is the program's own.</summary>
    private sealed class TerminalInputMethod(TerminalView view) : ViewInputMethod(view)
    {
        /// <summary>Told as empty rather than unsupported, so the platform's input method lets go of this client as the
        /// keys go elsewhere.</summary>
        public override bool SupportsSurroundingText => true;

        public override string SurroundingText => string.Empty;

        public override Rect CursorRectangle => view.InputCursor();

        /// <summary>Always none: there is no text here to select.</summary>
        public override TextSelection Selection
        {
            get => default;
            set { }
        }

        protected override void Composed() => view.Compose();
    }
}
