using Avalonia;
using Avalonia.Media.TextFormatting;
using Passenger.Editor;

namespace Passenger.App.Vcs;

/// <summary>The gutter's annotations column at its left edge (Git blame): each line's commit muted, on a ground of its own
/// with a rule after it, as wide as the longest; none while the file shows none. A click on a line is <see cref="Pressed"/>'s.</summary>
public sealed class BlameColumn : IGutterColumn
{
    private int longest;

    public event EventHandler? Changed;

    /// <summary>A click on a line's annotation (from zero).</summary>
    public event EventHandler<int>? Pressed;

    /// <summary>Text per line, null for a line with none; empty for no column.</summary>
    public IReadOnlyList<string?> Labels { get; private set; } = [];

    public void SetLabels(IReadOnlyList<string?> perLine)
    {
        Labels = perLine;
        longest = perLine.Count == 0 ? 0 : perLine.Max(l => l?.Length ?? 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public double Width(GutterFont font) => longest == 0 ? 0 : (longest * font.CharWidth) + 18;

    public void Draw(GutterFrame frame)
    {
        if (frame.Width <= 0)
        {
            return;
        }
        frame.Context.FillRectangle(frame.Brush(TokenKeys.Editor_CurrentLine), new Rect(frame.Left, 0, frame.Width - 6, frame.Height));
        foreach (GutterLine shown in frame.Lines)
        {
            if (shown.Line >= Labels.Count || Labels[shown.Line] is not { Length: > 0 } label)
            {
                continue;
            }
            TextLine text = frame.Label(label, TokenKeys.Editor_LineNumber);
            text.Draw(frame.Context, new Point(frame.Left + 8, shown.Top + (frame.LineHeight - text.Height) / 2));
        }
    }

    public GutterAnswer Press(GutterPress press)
    {
        if (press.Click.Context || !press.OnStrip || Pressed is null)
        {
            return GutterAnswer.Passed;
        }
        Pressed(this, press.Click.Line);
        return GutterAnswer.Taken;
    }
}
