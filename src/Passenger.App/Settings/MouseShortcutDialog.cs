using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>The Mouse Shortcut dialog (Add Mouse Shortcut): the action and where it is, a box the gesture is pressed into
/// (any button, the modifiers held, a double-click taken as one), the gesture as it is shown, and the actions it is
/// "Already assigned to". OK closes it with the gesture once one is pressed; Cancel or Escape with null.</summary>
public sealed class MouseShortcutDialog : ShortcutDialog
{
    private readonly Func<MouseShortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf;
    private readonly TextBlock shown = new() { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold };

    public MouseShortcutDialog(string action, string where, Func<MouseShortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf)
        : base("Mouse Shortcut")
    {
        this.conflictsOf = conflictsOf;
        TextBlock asked = new() { Text = "Click or double-click here with the modifiers held", HorizontalAlignment = HorizontalAlignment.Center };
        asked.Classes.Add(StyleClasses.Muted);
        // Transparent, so a press anywhere in the box is the box's.
        Pad = new Border
        {
            Classes = { StyleClasses.Box },
            Height = 88,
            Background = Brushes.Transparent,
            Child = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { asked, shown } },
        };
        Pad.PointerPressed += (_, e) =>
        {
            if (MouseShortcut.Of(e) is { Button: not MouseButton.None } pressed)
            {
                Take(pressed);
            }
            e.Handled = true;
        };
        Frame(action, where, Pad);
    }

    /// <summary>The box a gesture is pressed into.</summary>
    public Border Pad { get; }

    /// <summary>The gesture pressed last, or null before any.</summary>
    public MouseShortcut? Gesture { get; private set; }

    /// <summary>The gesture as the box shows it ("⌥Click"), empty before any.</summary>
    public string Shown => shown.Text ?? string.Empty;

    protected override object? Chosen => Gesture;

    /// <summary>A gesture pressed in the box: shown, its conflicts said, and OK let take it. A second click of a double-click
    /// comes as the double-click.</summary>
    private void Take(MouseShortcut pressed)
    {
        Gesture = pressed with { Clicks = Math.Min(2, pressed.Clicks) };
        shown.Text = Gesture.Value.Display();
        ShortcutChanged(conflictsOf(Gesture.Value));
    }
}
