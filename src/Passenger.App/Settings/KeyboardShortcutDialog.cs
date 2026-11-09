using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>The Keyboard Shortcut dialog (Add Keyboard Shortcut): the action and where it is, the first keystroke's
/// field, Second stroke and its field, and below them, as the keys are pressed, the actions the shortcut is "Already
/// assigned to". OK (or Enter once the first field holds a keystroke) closes it with the shortcut; Cancel or Escape
/// with null.</summary>
public sealed class KeyboardShortcutDialog : ShortcutDialog
{
    public KeyboardShortcutDialog(string action, string where, Func<Shortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf)
        : base("Keyboard Shortcut")
    {
        Keys = new ShortcutPanel("Second stroke:");
        Keys.Changed += (_, _) => ShortcutChanged(Keys.Shortcut is { } shortcut ? conflictsOf(shortcut) : []);
        Frame(action, where, Keys);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => Keys.First.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The first and second keystrokes' fields and the box between them.</summary>
    public ShortcutPanel Keys { get; }

    protected override object? Chosen => Keys.Shortcut;
}
