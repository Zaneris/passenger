using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>A tree's filter in its window's top bar (Unit Tests', the Explorer's): a search field with a magnifier before its
/// words, its placeholder naming Find's keys, which give it the keys while its window has them.</summary>
public static class FilterBox
{
    public static TextBox Of()
    {
        PathIcon magnifier = new() { Width = 14, Height = 14, Margin = new Thickness(8, 0, 0, 0) };
        magnifier.Bind(PathIcon.DataProperty, magnifier.GetResourceObservable(IconKeys.Search));
        return new TextBox
        {
            Classes = { StyleClasses.Search },
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = $"{(OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl+F")} to filter",
            InnerLeftContent = magnifier,
        };
    }
}
