using System.ComponentModel;
using Avalonia;
using Avalonia.Media.TextFormatting;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Vcs;

/// <summary>The gutter's annotations column at its left edge (Git blame): each line's commit muted, on a ground of its own
/// with a rule after it, as wide as the longest; none while the file shows none. Read from the file shown as it changes
/// them. A click on a line is <see cref="Pressed"/>'s.</summary>
public sealed class BlameColumn : IGutterColumn
{
    private OpenDocument? file;
    private IReadOnlyList<string?> measured = [];
    private int longest;

    public event EventHandler? Changed;

    /// <summary>A click on a line's annotation (from zero).</summary>
    public event EventHandler<int>? Pressed;

    /// <summary>Text per line, null for a line with none; empty for no column.</summary>
    public IReadOnlyList<string?> Labels => file?.GutterAnnotations ?? [];

    /// <summary>Shows a file's annotations, followed as it changes them; none for no file.</summary>
    public void Show(OpenDocument? document)
    {
        if (ReferenceEquals(document, file))
        {
            return;
        }
        if (file is not null)
        {
            file.PropertyChanged -= OnFileChanged;
        }
        file = document;
        if (file is not null)
        {
            file.PropertyChanged += OnFileChanged;
        }
        Measure();
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenDocument.GutterAnnotations))
        {
            Measure();
        }
    }

    /// <summary>Finds the longest label once for each list, and has the editor draw them.</summary>
    private void Measure()
    {
        IReadOnlyList<string?> labels = Labels;
        if (!ReferenceEquals(labels, measured))
        {
            measured = labels;
            longest = labels.Count == 0 ? 0 : labels.Max(l => l?.Length ?? 0);
        }
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
