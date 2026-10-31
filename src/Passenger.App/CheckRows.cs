using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A choice a list of check rows offers (<see cref="CheckRows.Of"/>): its name, what it is, whether it is
/// checked, and whether it can be.</summary>
public interface ICheckChoice
{
    string Name { get; }

    string Detail { get; }

    bool CanChoose { get; }

    bool IsChosen { get; set; }
}

/// <summary>A row a person checks or clears (Add Reference's projects, Add Docker Compose's services, a nuget.config's
/// feeds), as tall as every list's row (<see cref="ListRow.Height"/>): its box at its start, centred in it, then what
/// it names.</summary>
public static class CheckRows
{
    /// <summary>A row's box: centred in the row, as wide as its glyph.</summary>
    public static CheckBox Box() => new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };

    /// <summary>A list's row for whichever choice the list hands its container: its box, which follows the choice while
    /// shown and checks the one it shows, never taking the keys from the list; its name; and what it is, muted. One that
    /// cannot be chosen is greyed, its box with it.</summary>
    public static Control Of()
    {
        CheckBox check = Box();
        check.Focusable = false;
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new() { Classes = { StyleClasses.Muted }, FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        StackPanel row = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Margin = new Thickness(6, 0), Children = { check, name, detail } };
        bool setting = false;
        check.IsCheckedChanged += (_, _) =>
        {
            if (!setting && row.DataContext is ICheckChoice choice)
            {
                choice.IsChosen = check.IsChecked == true;
            }
        };
        return row.Showing<ICheckChoice>(choice =>
        {
            setting = true;
            check.IsChecked = choice.IsChosen;
            check.IsEnabled = choice.CanChoose;
            setting = false;
            name.Text = choice.Name;
            detail.Text = choice.Detail;
            row.Opacity = choice.CanChoose ? 1 : 0.55;
        });
    }
}
