using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App;

/// <summary>A dialog's buttons at its foot, at its right as Rider's dialogs place them: the default rightmost with Cancel
/// beside it on macOS, the default first and Cancel after it on Windows and Linux (Rider's OK, Cancel, Apply), any other
/// (Apply) after Cancel. Shell.axaml's <c>StackPanel.footer</c> spaces them and gives each one minimum width.</summary>
public static class DialogFooter
{
    public static StackPanel Of(Button chosen, Button? cancel, params Button[] more)
        => Arrange([chosen], cancel, more);

    /// <summary>Competing answers beside the default (Open Solution's This Window, Reload or Convert's Reload),
    /// together before Cancel on Windows and Linux, and after Cancel with the default last on macOS.</summary>
    public static StackPanel OfChoices(Button chosen, Button cancel, params Button[] choices)
        => Arrange([chosen, .. choices], cancel, []);

    /// <summary>A footer's option at its left, on the same row as its buttons, as Rider puts Don't ask again.</summary>
    public static Grid WithLeading(Control option, StackPanel buttons)
    {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        option.HorizontalAlignment = HorizontalAlignment.Left;
        option.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(buttons, 1);
        row.Children.Add(option);
        row.Children.Add(buttons);
        return row;
    }

    private static StackPanel Arrange(Button[] choices, Button? cancel, Button[] more)
    {
        Button?[] order = OperatingSystem.IsMacOS() ? [cancel, .. more, .. choices.Reverse()] : [.. choices, cancel, .. more];
        StackPanel footer = new() { Classes = { "footer" } };
        footer.Children.AddRange(order.OfType<Button>());
        return footer;
    }
}
