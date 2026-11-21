using Avalonia.Input;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>Keys named in words as this system shows them (⇧⌘L on macOS, Ctrl+Shift+L elsewhere, as every menu shows them),
/// the one way the window turns a key into words: a key no action owns that a tip, a placeholder, a status or a note names
/// (a list's F4, a field's Shift+Enter, a picture's zoom, a modifier a click is held with), and a keymap's shortcuts where
/// Settings and the menus show them. An action's keys in the keymap in force come through <see cref="ActionTips"/>, which
/// names them here.</summary>
public static class KeyWords
{
    public static string Of(Keystroke key) => key.Display();

    public static string Of(Key key, KeyModifiers modifiers = KeyModifiers.None) => Of(new Keystroke(key, modifiers));

    /// <summary>A chord ("⌘K, ⌘C") or a double tap ("⇧⇧") as one key's are.</summary>
    public static string Of(Shortcut shortcut) => shortcut.Display();

    /// <summary>A click with its modifiers ("⌥Click", "Ctrl+Click").</summary>
    public static string Of(MouseShortcut gesture) => gesture.Display();

    /// <summary>An action's first shortcut in a keymap, or null while it has none.</summary>
    public static string? Of(Keymap keymap, string action) => keymap.ShortcutsFor(action).FirstOrDefault() is { } shortcut ? Of(shortcut) : null;

    /// <summary>Keys that each do the same ("F7, F8").</summary>
    public static string Either(params IEnumerable<Keystroke> keys) => string.Join(", ", keys.Select(Of));

    /// <summary>Modifiers held with what no key names (the mouse's wheel), or held alone: "⌘", "Ctrl", "⌘Mouse Wheel",
    /// "Ctrl+Mouse Wheel".</summary>
    public static string Held(KeyModifiers modifiers, string with = "") => Keystroke.Shown(modifiers, with);
}
