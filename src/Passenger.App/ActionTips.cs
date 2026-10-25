using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>A tool window's button tip that names its action's shortcut ("Step Over (F10)") as the keymap in force has
/// it, or words that name it (a tool window's empty text, a box's placeholder): said again as the window follows a changed
/// keymap (another keymap chosen, a shortcut set) and as the control is shown again, so it never names keys that no longer
/// run it. A place that takes another action's keys for its own names those (<see cref="AppActions.KeysIn"/>).</summary>
public static class ActionTips
{
    private static readonly AttachedProperty<Tip?> TipProperty = AvaloniaProperty.RegisterAttached<Control, Tip?>("Tip", typeof(ActionTips));

    /// <summary>Gives a control the tip of an action named in words.</summary>
    public static void Set(Control control, string action, string words)
    {
        if (control.GetValue(TipProperty) is null)
        {
            // A hidden tool window's buttons are out of the tree when the keymap changes.
            control.AttachedToVisualTree += (_, _) => Show(control);
        }
        control.SetValue(TipProperty, new Tip(action, words, null));
        Show(control);
    }

    /// <summary>Gives a block, or a box's placeholder, words naming an action's keys, made from the keys' display, null
    /// while nothing runs it.</summary>
    public static void SetWords(Control control, string action, Func<string?, string> words)
    {
        if (control.GetValue(TipProperty) is null)
        {
            control.AttachedToVisualTree += (_, _) => Show(control);
        }
        control.SetValue(TipProperty, new Tip(action, string.Empty, words));
        Show(control);
    }

    /// <summary>The control's tip as the keymap in force has its action's keys now, or null for one with no such tip.</summary>
    public static string? Of(Control control) => control.GetValue(TipProperty) is { Text: null } tip
        ? KeysOf(tip.Action) is { } keys ? $"{tip.Words} ({keys})" : tip.Words
        : null;

    private static string? KeysOf(string action) => AppActions.Keymap().ShortcutsFor(AppActions.KeysIn(action)).FirstOrDefault()?.Display();

    /// <summary>Every such tip shown under a window said again, after the keymap in force changed.</summary>
    public static void Follow(Visual within)
    {
        foreach (Control control in within.GetVisualDescendants().OfType<Control>().Where(c => c.GetValue(TipProperty) is not null))
        {
            Show(control);
        }
    }

    /// <summary>The tip in force, unless the button is showing its work's stage, which <see cref="ActionButtons"/> puts back.</summary>
    private static void Show(Control control)
    {
        if (control.GetValue(TipProperty) is { Text: { } words } tip)
        {
            if (control is TextBox box)
            {
                box.PlaceholderText = words(KeysOf(tip.Action));
            }
            else if (control is TextBlock block)
            {
                block.Text = words(KeysOf(tip.Action));
            }
        }
        else if (!control.Classes.Contains("working"))
        {
            ToolTip.SetTip(control, Of(control));
        }
    }

    private sealed record Tip(string Action, string Words, Func<string?, string>? Text);
}
