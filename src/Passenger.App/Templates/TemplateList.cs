using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Templates;

/// <summary>What the New Project and New Item dialogs share around their lists of templates: the list's width, a row's
/// words, the row as the list shows it, and the line saying why a Create, an install or the templates' read failed.</summary>
internal static class TemplateList
{
    /// <summary>How wide the list beside a dialog's page stands, room for a template's name and its mark.</summary>
    public const double Width = 280;

    /// <summary>How a dialog says its templates could not be read.</summary>
    public static string Unread(Exception error) => $"The templates could not be read: {error.Message}";

    /// <summary>A row's words: its name, trimmed to its room, and its detail at its right (where it comes from, "Not
    /// installed").</summary>
    public static (DockPanel Named, TextBlock Name, TextBlock Mark) Named()
    {
        TextBlock name = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        TextBlock mark = ListRow.DetailText();
        DockPanel both = new();
        both.Children.Add(mark);
        both.Children.Add(name);
        return (both, name, mark);
    }

    /// <summary>A row as the list shows it, around its words.</summary>
    public static Border Row(Control named) => new() { Padding = new Thickness(Gaps.Small), Child = named };

    /// <summary>Says why the dialog's work was refused or failed, or nothing.</summary>
    public static void Fail(TextBlock problem, string? why)
    {
        problem.Text = why;
        problem.IsVisible = why is not null;
    }

    /// <summary>Shows the dialog's work under way, its problem hidden meanwhile and shown again after where it says one.</summary>
    public static void Busy(TextBlock problem, Control working, bool busy)
    {
        working.IsVisible = busy;
        problem.IsVisible = !busy && problem.Text is not null;
    }
}
