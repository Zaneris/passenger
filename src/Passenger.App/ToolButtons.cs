using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>Every icon button, made here: a toolbar's (a tool window's, a diff's, a dialog's list's, the title bar's), a
/// control-high square with its icon at the one size every icon has, or a glyph a step smaller (<see cref="Glyph"/>), its
/// tip, its name for a screen reader, and what a click does; and the smaller one inside a chip, a field or a tab
/// (<see cref="Small"/>).
/// A tool window's never takes the keys from what it acts on, so the list or editor beside it keeps them. One call per
/// button, so every bar's buttons are alike; one that runs one of the window's actions is made by the overload that
/// takes it, or bound by <see cref="ActionButtons"/>. UiRulesTests fails an icon button styled anywhere else.</summary>
public static class ToolButtons
{
    public static Button Of(string icon, string tip, Action? run = null) => Clicks(Made(new Button(), Icon(icon), tip), run);

    /// <summary>A toolbar button whose mark is drawn otherwise than one icon of the theme's (two over each other, one
    /// turned, a change's own arrow), at the size an icon is.</summary>
    public static Button Of(Control mark, string tip, Action? run = null) => Clicks(Made(new Button(), mark, tip), run);

    /// <summary>A toolbar button whose mark is a glyph, a step smaller than an icon: a × that closes or removes what it
    /// stands beside (a row's, the find bar's), a tool window header's own Options and Hide.</summary>
    public static Button Glyph(string glyph, string tip, Action? run = null) => Clicks(Made(new Button(), GlyphOf(glyph), tip), run);

    /// <summary>A glyph's button no larger than its square, inside what it acts on: a chip's × that clears it, a field's
    /// + that opens its menu, a tab's pin and ×. It keeps the keys' reach a plain button has; one inside a field that takes
    /// every key is made unfocusable there.</summary>
    public static Button Small(string glyph, string tip, Action? run = null)
    {
        Button button = new() { Classes = { StyleClasses.Small }, Content = RowIcon.Glyph(glyph) };
        return Clicks(Tip(button, tip), run);
    }

    /// <summary>Makes a button declared elsewhere (a window's XAML, one of a kind of its own that opens its menu) a
    /// toolbar's icon button, <paramref name="mark"/> a class its icon takes (the title bar's Run in its own colour).
    /// <paramref name="keys"/> keeps it where Tab reaches it, as a bar the keys go through has its buttons (the title
    /// bar's, the find bar's); a tool window's leaves the keys with what it acts on.</summary>
    public static T Give<T>(T button, string icon, string tip, bool keys = false, bool glyph = false, [IdentifierOf(typeof(StyleClasses))] string? mark = null)
        where T : Button
    {
        Control shown = glyph ? GlyphOf(icon) : Icon(icon);
        if (mark is not null)
        {
            shown.Classes.Add(mark);
        }
        return Give(button, shown, tip, keys);
    }

    /// <summary>Makes a button the toolbar's icon button with a mark drawn otherwise than one icon of the theme's, kept
    /// where Tab reaches it for <paramref name="keys"/> (a page's, which the keys go through).</summary>
    public static T Give<T>(T button, Control mark, string tip, bool keys = false)
        where T : Button
    {
        Made(button, mark, tip);
        button.Focusable = keys;
        return button;
    }

    /// <summary>A button that asks for one of the window's actions its view's way (<paramref name="request"/>, given the
    /// action): named by the action, its tip naming the action's keys as the keymap in force has them
    /// (<see cref="ActionTips"/>), and marked so the window shows the action's work on it (<see cref="ActionButtons.ShowMarked"/>).</summary>
    public static Button Of(string action, string icon, string tip, Action<string> request)
    {
        Button button = Of(icon, tip, () => request(action));
        button.Name = action;
        ActionTips.Set(button, action, tip);
        ActionButtons.Mark(button, action);
        return button;
    }

    /// <summary>A list's filter for one kind of its rows (a test's outcome, a problem's severity): the kind's icon in its
    /// colour and how many rows are of it, pressed while the kind is chosen; <paramref name="count"/> is the count's words.</summary>
    public static ToggleButton Counted(string icon, string brush, string tip, out TextBlock count)
    {
        PathIcon mark = Icon(icon);
        mark.Bind(PathIcon.ForegroundProperty, mark.GetResourceObservable(brush));
        count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Classes = { StyleClasses.Count } };
        ToggleButton button = new()
        {
            Classes = { StyleClasses.Outcome },
            Focusable = false,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gaps.Tight, Children = { mark, count } },
        };
        return Tip(button, tip);
    }

    /// <summary>A toolbar's button that stays pressed while what it turns on is on (Mute Breakpoints, Select Element).</summary>
    public static ToggleButton Toggle(string icon, string tip) => Made(new ToggleButton(), Icon(icon), tip);

    /// <summary>Gives a button with no words on it its tip, and those words as the name a screen reader says for it; with
    /// <paramref name="keys"/>, the keys that do the same, which the tip names after its words as every tip does.</summary>
    public static T Tip<T>(T button, string tip, string? keys = null)
        where T : Control
    {
        ToolTip.SetTip(button, ActionTips.Named(tip, keys));
        AutomationProperties.SetName(button, tip);
        return button;
    }

    /// <summary>Greys a button (or a choice that greys as one does) while it can't act, its tip saying why: after an icon's
    /// name, as a refusal follows what it stopped ("Delete: main is checked out"), or alone on one of words, which names
    /// itself; null gives it back its <paramref name="tip"/> while it can act, else its name alone. A screen reader names it
    /// as before. UiRulesTests fails a control greyed with why by hand.</summary>
    public static void Grey(Control button, string? why, string? tip = null)
    {
        string? name = AutomationProperties.GetName(button);
        button.IsEnabled = why is null;
        ToolTip.SetTip(button, why is null ? tip ?? name : name is null ? why : $"{name}: {why}");
    }

    private static T Made<T>(T button, Control mark, string tip)
        where T : Button
    {
        button.Classes.Add(StyleClasses.Tool);
        button.Content = mark;
        button.Focusable = false;
        return Tip(button, tip);
    }

    private static T Clicks<T>(T button, Action? run)
        where T : Button
    {
        if (run is not null)
        {
            button.Click += (_, _) => run();
        }
        return button;
    }

    private static PathIcon Icon(string key)
    {
        PathIcon icon = new();
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(key));
        return icon;
    }

    private static PathIcon GlyphOf(string key)
    {
        PathIcon glyph = Icon(key);
        glyph.Classes.Add(StyleClasses.Small);
        return glyph;
    }
}

/// <summary>Words of less weight beside what a row or a page says (a count, a hint, where something is): the muted
/// text's colour, centred in its row, ending in an ellipsis where it has no room. Its page sets what it needs beyond that
/// (a width, a margin, a caption's smaller size).</summary>
public static class MutedText
{
    public static TextBlock Of(string text) =>
        new() { Classes = { StyleClasses.Muted }, Text = text, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <summary>A muted note that wraps to its column, as a page's explanations do.</summary>
    public static TextBlock Wrapped(string text)
    {
        TextBlock note = Of(text);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }

    /// <summary>A muted note at the caption's step: a field's note, a row's detail.</summary>
    public static TextBlock Caption(string text)
    {
        TextBlock note = Of(text);
        note.Classes.Add(StyleClasses.Caption);
        return note;
    }

    /// <summary>A muted note at the caption's step that wraps to its column, as a field's note under it does.</summary>
    public static TextBlock CaptionWrapped(string text)
    {
        TextBlock note = Wrapped(text);
        note.Classes.Add(StyleClasses.Caption);
        return note;
    }
}
