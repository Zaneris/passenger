using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Passenger.App.ToolWindows;

namespace Passenger.App;

/// <summary>Every search field, made here alike: a tool window's filter, a dialog's or a page's search, the Welcome
/// screen's, each with a magnifier before its words, a name a screen reader says, its words at the dense step
/// (Shell.axaml's <c>TextBox.search</c>), and in a tool window's bar the room the bar gives it (<see cref="InBar"/>). A
/// popup that is itself a search (Go to File, Find in Files) takes its field from <see cref="InPopup"/>, with no
/// magnifier; a list's Sort and Filter goes inside at its end (<see cref="FilterMenu.Inside"/>).</summary>
public static class SearchBox
{
    /// <summary>The room a search takes in a tool window's bar (<see cref="ToolFlow"/>): this least beside the bar's other
    /// parts, up to this most of what its row leaves.</summary>
    public static readonly ToolLayout.Fills InBar = new(140, 260);

    /// <summary>A search field named <paramref name="name"/> ("Search packages", "Filter properties"), which its
    /// placeholder says too unless it is given words of its own.</summary>
    public static TextBox Of(string name, string? placeholder = null)
    {
        // Where a field's words start, so what lines up with a field's words lines up with a search's magnifier.
        PathIcon magnifier = new() { Width = 14, Height = 14, Margin = new Thickness(FieldInsets.Padding.Left, 0, 0, 0) };
        magnifier.Bind(PathIcon.DataProperty, magnifier.GetResourceObservable(IconKeys.Search));
        TextBox box = InPopup(name, placeholder ?? name);
        box.InnerLeftContent = magnifier;
        ToolFlow.SetFills(box, InBar);
        return box;
    }

    /// <summary>A tool window's filter, which Find's keys give the keys to while its window has them: its placeholder names
    /// those keys as the keymap in force gives them ("⌘F to filter"), or its name while no key runs Find.</summary>
    public static TextBox Finding(string name)
    {
        TextBox box = Of(name);
        ActionTips.SetWords(box, AppActions.Find, keys => keys is null ? name : $"{keys} to filter");
        return box;
    }

    /// <summary>The field of a popup that is a search, named <paramref name="name"/> (its title), its placeholder saying
    /// what it finds.</summary>
    public static TextBox InPopup(string name, string placeholder)
    {
        TextBox box = new() { Classes = { StyleClasses.Search }, PlaceholderText = placeholder };
        AutomationProperties.SetName(box, name);
        return box;
    }
}
