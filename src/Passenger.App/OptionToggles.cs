using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>A search's options, each an icon's toggle in its field or beside it, in the one look (Shell.axaml's
/// <c>ToggleButton.option</c>: the accent's ground while on): Match case, Words, Regex and Recursively, each with its key,
/// which its tip names as this system shows keys and whatever holds the field hears (<see cref="Hear"/>), so the editor's
/// find bar and Find in Files share the toggles and their keys.</summary>
public static class OptionToggles
{
    /// <summary>An option: its name, its icon (none for Regex, whose mark is <c>.*</c> in the code font) and its key.</summary>
    public sealed record Option(string Name, string? Icon, Keystroke Key);

    public static Option MatchCase { get; } = new("Match case", IconKeys.MatchCase, new Keystroke(Key.C, KeyModifiers.Alt));

    public static Option Words { get; } = new("Words", IconKeys.WholeWord, new Keystroke(Key.O, KeyModifiers.Alt));

    public static Option Regex { get; } = new("Regex", null, new Keystroke(Key.X, KeyModifiers.Alt));

    /// <summary>A directory's subdirectories searched too.</summary>
    public static Option Recursively { get; } = new("Recursively", IconKeys.Structure, new Keystroke(Key.Y, KeyModifiers.Alt));

    public static ToggleButton Of(Option option) => Give(new ToggleButton(), option);

    /// <summary>Makes a toggle declared elsewhere (the find bar's XAML names its own) the option's: its look, its mark,
    /// its tip naming its key, and its name for a screen reader.</summary>
    public static T Give<T>(T toggle, Option option)
        where T : ToggleButton
    {
        toggle.Classes.Add(StyleClasses.Option);
        if (option.Icon is { } icon)
        {
            PathIcon glyph = new();
            glyph.Bind(PathIcon.DataProperty, glyph.GetResourceObservable(icon));
            toggle.Content = glyph;
        }
        else
        {
            toggle.Content = new TextBlock { Classes = { StyleClasses.BadgeText }, Text = ".*" }.InCodeFont();
        }
        return ToolButtons.Tip(toggle, option.Name, KeyWords.Of(option.Key));
    }

    /// <summary>Turns over the toggle whose option's key a press is, the press handled; false for any other press.</summary>
    public static bool Hear(KeyEventArgs e, params ReadOnlySpan<(Option Option, ToggleButton Toggle)> options)
    {
        foreach ((Option option, ToggleButton toggle) in options)
        {
            if (option.Key == Keystroke.From(e) && toggle.IsEffectivelyEnabled)
            {
                toggle.IsChecked = toggle.IsChecked != true;
                e.Handled = true;
                return true;
            }
        }
        return false;
    }
}
