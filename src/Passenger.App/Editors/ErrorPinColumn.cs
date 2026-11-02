using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media.TextFormatting;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.Editors;

/// <summary>The errors a run raised at a line (from zero), pinned there after it ends: how many came, the last one's words,
/// whether every one was a warning, and what holds the rest (<see cref="Data"/>).</summary>
public readonly record struct ErrorPin(int Line, int Count, string Last, bool Warnings, object? Data = null);

/// <summary>The gutter's column of a run's errors pinned to their lines, beside the text: a badge on each pinned line, in
/// the error's colour (a warning's where all were warnings), saying how many came. It takes no room in a file with none. A
/// press on a badge is <see cref="Pressed"/>'s.</summary>
public sealed class ErrorPinColumn : IGutterColumn
{
    private OpenDocument? file;
    private readonly Dictionary<int, ErrorPin> shown = [];

    public event EventHandler? Changed;

    /// <summary>A press on a line's badge: whether it did something.</summary>
    public Func<GutterClick, bool>? Pressed { get; set; }

    public IReadOnlyList<ErrorPin> Pins => file?.ErrorPins ?? [];

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
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenDocument.ErrorPins))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>A count as its badge says it: up to 999, then in thousands.</summary>
    public static string Counted(int count) => count < 1000 ? count.ToString(CultureInfo.InvariantCulture)
        : count < 100_000 ? $"{count / 1000}k" : "99k+";

    public double Width(GutterFont font)
    {
        IReadOnlyList<ErrorPin> pins = Pins;
        if (pins.Count == 0)
        {
            return 0;
        }
        int widest = pins.Max(p => Counted(p.Count).Length);
        return (widest * font.CharWidth * 0.8) + 12;
    }

    public void Draw(GutterFrame frame)
    {
        shown.Clear();
        foreach (ErrorPin pin in Pins)
        {
            if (pin.Line >= frame.First && pin.Line <= frame.Last && !frame.IsHidden(pin.Line))
            {
                shown[pin.Line] = pin;
            }
        }
        foreach (GutterLine line in frame.Lines)
        {
            if (!shown.TryGetValue(line.Line, out ErrorPin pin))
            {
                continue;
            }
            TextLine label = frame.Label(Counted(pin.Count), TokenKeys.BadgeText, 0.8, bold: true);
            double height = Math.Min(frame.LineHeight - 4, label.Height + 2);
            Rect badge = new(frame.Left + 2, line.Top + ((frame.LineHeight - height) / 2), frame.Width - 4, height);
            frame.Context.FillRectangle(frame.Brush(pin.Warnings ? TokenKeys.Editor_Warning : TokenKeys.Editor_Error), badge, (float)(height / 2));
            label.Draw(frame.Context, new Point(badge.X + ((badge.Width - label.WidthIncludingTrailingWhitespace) / 2), badge.Y + ((badge.Height - label.Height) / 2)));
        }
    }

    public GutterAnswer Press(GutterPress press)
    {
        if (press.Click.Context || !press.OnStrip || !Pins.Any(p => p.Line == press.Click.Line))
        {
            return GutterAnswer.Passed;
        }
        return Pressed?.Invoke(press.Click) == true ? GutterAnswer.Taken : GutterAnswer.Passed;
    }
}
