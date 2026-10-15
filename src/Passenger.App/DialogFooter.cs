using Avalonia.Controls;

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

    private static StackPanel Arrange(Button[] choices, Button? cancel, Button[] more)
    {
        Button?[] order = OperatingSystem.IsMacOS() ? [cancel, .. more, .. choices.Reverse()] : [.. choices, cancel, .. more];
        StackPanel footer = new() { Classes = { "footer" } };
        footer.Children.AddRange(order.OfType<Button>());
        return footer;
    }
}
