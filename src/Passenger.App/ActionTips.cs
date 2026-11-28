using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>An action button's tip (the toolbar's, a tool window's) that names its action's shortcut ("Step Over (F10)") as
/// the keymap in force has it, or words that name it (a tool window's empty text, a box's placeholder): said again as the
/// window follows a changed keymap (another keymap chosen, a shortcut set) or language and as the control is shown again,
/// so it never names keys that no longer run it. A place that takes another action's keys for its own names those
/// (<see cref="AppActions.KeysIn"/>).</summary>
public static class ActionTips
{
    private static readonly AttachedProperty<Tip?> TipProperty = AvaloniaProperty.RegisterAttached<Control, Tip?>("Tip", typeof(ActionTips));

    /// <summary>Gives a control the tip of an action named in words, and those words, without the keys, as the name a
    /// screen reader says for it.</summary>
    public static void Set(Control control, string action, Words words)
    {
        ShownWords.Set(control, AutomationProperties.NameProperty, words);
        if (control.GetValue(TipProperty) is null)
        {
            // A hidden tool window's buttons are out of the tree when the keymap changes.
            control.AttachedToVisualTree += (_, _) => Show(control);
        }
        control.SetValue(TipProperty, new Tip(action, words, null));
        Show(control);
    }

    /// <summary>Gives a block, a box's placeholder or a link's words naming an action's keys, made from the keys' display,
    /// null while nothing runs it.</summary>
    public static void SetWords(Control control, string action, Func<string?, string> words) => Say(control, action, keys => words(keys(action)));

    /// <summary>Gives a block words naming several actions' keys (a tool window's empty text naming each way to fill it),
    /// made from each one's keys' display as <c>keys(action)</c> gives it, null while nothing runs that one.</summary>
    public static void SetWords(Control control, Func<Func<string, string?>, string> words) => Say(control, null, words);

    /// <summary>An action's name with its keys after it, as every tip and empty text names them, or the name alone while
    /// nothing runs it.</summary>
    public static string Named(string name, string? keys) => keys is null ? name : $"{name} ({keys})";

    /// <summary>The action whose keys a control's tip names, or null for one with no such tip.</summary>
    public static string? ActionOf(Control control) => control.GetValue(TipProperty)?.Action;

    private static void Say(Control control, string? action, Func<Func<string, string?>, string> words)
    {
        if (control.GetValue(TipProperty) is null)
        {
            control.AttachedToVisualTree += (_, _) => Show(control);
        }
        control.SetValue(TipProperty, new Tip(action, string.Empty, words));
        Show(control);
    }

    /// <summary>The control's tip as the keymap in force has its action's keys now, or null for one with no such tip.</summary>
    public static string? Of(Control control) => control.GetValue(TipProperty) is { Text: null, Action: { } action } tip ? Named(tip.Words.Shown, Keys(action)) : null;

    /// <summary>An action's keys in words as the keymap in force has them now (<see cref="KeyWords"/>), those of the action
    /// whose keys it takes where it takes another's (<see cref="AppActions.KeysIn"/>), or null while nothing runs it: for
    /// words said once (a status), which no later keymap changes.</summary>
    public static string? Keys(string action) => KeyWords.Of(AppActions.Keymap(), AppActions.KeysIn(action));

    /// <summary>Every such tip shown under a window said again, after the keymap or the language in force changed.</summary>
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
        if (control.GetValue(TipProperty) is { Text: { } words })
        {
            if (control is TextBox box)
            {
                box.PlaceholderText = words(Keys);
            }
            else if (control is TextBlock block)
            {
                block.Text = words(Keys);
            }
            else if (control is ContentControl content)
            {
                content.Content = words(Keys);
            }
        }
        else if (!ActionButtons.IsWorking(control))
        {
            ToolTip.SetTip(control, Of(control));
        }
    }

    private sealed record Tip(string? Action, Words Words, Func<Func<string, string?>, string>? Text);
}
