using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Godot;

/// <summary>A line's pinned errors, opened from its badge: each time one came, newest first, when (in the game's time) and
/// what it said, and the chosen one's stack, each frame numbered and a link to its line. Clear Pin and Clear All Pins take
/// them off by hand; Escape or Close leaves them.</summary>
public sealed class GodotPinPopup : UserControl
{
    private readonly ListBox list;
    private readonly StackPanel stack;
    private readonly Func<string, string> local;

    /// <param name="local">A frame's file (a <c>res://</c> path) as a path on this machine.</param>
    public GodotPinPopup(GodotPin pin, Func<string, string> local)
    {
        this.local = local;
        Pin = pin;
        Width = PopupFrame.Medium;
        TextBlock title = new() { Classes = { StyleClasses.PopupTitle }, Text = pin.Last.Said, TextWrapping = TextWrapping.Wrap };
        string earlier = pin.Earlier > 0 ? $", the {Plural.Of(pin.Earlier, "earlier one")} counted alone" : string.Empty;
        TextBlock counted = new()
        {
            Classes = { StyleClasses.Muted },
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = $"{(pin.Warnings ? "Warned" : "Raised")} {Plural.Of(pin.Count, "time")} at {Path.GetFileName(pin.Path)}:{pin.Line + 1} in the last run{earlier}",
        };
        list = new ListBox { Classes = { StyleClasses.Tree }, Height = ListRow.Height * 6, ItemsSource = pin.Occurrences.Reverse().ToList() };
        ReusedRows.Use(list, Row);
        stack = new StackPanel { Spacing = 1 };
        list.SelectionChanged += (_, _) => ShowStack(list.SelectedItem as GodotOccurrence);
        Button clear = new() { Content = "Clear Pin" };
        clear.Click += (_, _) => Cleared?.Invoke(this, false);
        Button all = new() { Content = "Clear All Pins" };
        all.Click += (_, _) => Cleared?.Invoke(this, true);
        Button close = new() { Content = "Close", Classes = { StyleClasses.Primary }, IsDefault = true };
        close.Click += (_, _) => Done?.Invoke(this, EventArgs.Empty);
        StackPanel answers = DialogFooter.Listed(clear, all, close);
        answers.HorizontalAlignment = HorizontalAlignment.Right;
        Content = new Border
        {
            Classes = { StyleClasses.Popup },
            Padding = new Thickness(14, 12),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    title,
                    counted,
                    new Border { Classes = { StyleClasses.Framed }, Child = list },
                    new ScrollViewer { MaxHeight = ListRow.Height * 8, Content = stack },
                    answers,
                },
            },
        };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Done?.Invoke(this, EventArgs.Empty);
            }
        }, RoutingStrategies.Tunnel);
        list.SelectedIndex = 0;
    }

    public GodotPin Pin { get; }

    /// <summary>The occurrences listed, newest first.</summary>
    public IReadOnlyList<GodotOccurrence> Listed => list.ItemsSource as IReadOnlyList<GodotOccurrence> ?? [];

    /// <summary>The chosen occurrence's frames as shown, "#0 _process  main.gd:7".</summary>
    public IReadOnlyList<string> Frames => [.. stack.Children.OfType<Button>().Select(b => (b.Content as TextBlock)?.Text ?? string.Empty)];

    /// <summary>Close pressed, or Escape.</summary>
    public event EventHandler? Done;

    /// <summary>Clear Pin (false) or Clear All Pins (true) pressed.</summary>
    public event EventHandler<bool>? Cleared;

    /// <summary>A frame's link followed: its file and line (from one).</summary>
    public event EventHandler<(string Path, int Line)>? FrameChosen;

    /// <summary>A time in the game's own clock, as minutes, seconds and milliseconds since it started.</summary>
    public static string Time(long milliseconds) =>
        string.Create(CultureInfo.InvariantCulture, $"{milliseconds / 60000}:{milliseconds / 1000 % 60:00}.{milliseconds % 1000:000}");

    private void ShowStack(GodotOccurrence? occurrence)
    {
        stack.Children.Clear();
        if (occurrence is null)
        {
            return;
        }
        if (occurrence.Frames.Count == 0)
        {
            stack.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 12, Text = "Godot sent no stack with it" });
            return;
        }
        for (int i = 0; i < occurrence.Frames.Count; i++)
        {
            GodotFrame frame = occurrence.Frames[i];
            string path = local(frame.File);
            Button link = new()
            {
                Classes = { StyleClasses.Link },
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Text = $"#{i} {frame.Function}  {Path.GetFileName(frame.File)}:{frame.Line}" }.InCodeFont(),
            };
            ToolTip.SetTip(link, $"Go to {path}:{frame.Line}");
            link.Click += (_, _) => FrameChosen?.Invoke(this, (path, frame.Line));
            stack.Children.Add(link);
        }
    }

    private static Control Row()
    {
        TextBlock when = new TextBlock { Classes = { StyleClasses.Muted }, FontSize = 11.5, Width = 80, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        TextBlock said = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(when, Dock.Left);
        DockPanel line = new() { Height = ListRow.Height, Margin = new Thickness(8, 0), Children = { when, said } };
        return line.Showing<GodotOccurrence>(occurrence =>
        {
            when.Text = Time(occurrence.Milliseconds);
            said.Text = occurrence.Said;
        });
    }
}
