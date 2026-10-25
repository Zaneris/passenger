using Avalonia;
using Avalonia.Controls;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's bars, one look in every window: along its top, its tabs, fields, buttons and words over a
/// rule; down its left, its icon buttons in a <see cref="ToolColumn"/> beside a rule. UiRulesTests fails a bar built by
/// hand.</summary>
public static class ToolBars
{
    /// <summary>The top bar's height: a control's (Passenger.ControlHeight, 28: a tab, a field, a button, a drop-down) with
    /// 2 above and below, the least that holds the tallest part a bar has without crowding it, as most bars were.</summary>
    public const double Height = 32;

    /// <summary>The side bar's width: an icon button's (28) with 4 each side.</summary>
    public const double Width = 36;

    /// <summary>A bar along a window's top, docked there, holding <paramref name="content"/> over its rule. A bar whose
    /// parts wrap (the Migrations window's, in a narrow window) grows a row at a time.</summary>
    public static Border Top(Control content)
    {
        Border bar = new() { [DockPanel.DockProperty] = Dock.Top, MinHeight = Height, BorderThickness = new Thickness(0, 0, 0, 1), Child = content };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable(TokenKeys.Divider));
        return bar;
    }

    /// <summary>A bar down a window's left, docked there, its buttons from the top and those with no room behind More
    /// (<see cref="ToolColumn"/>), its rule at its right.</summary>
    public static Border Side(params Control[] buttons)
    {
        ToolColumn column = new() { Spacing = 2, Margin = new Thickness(0, 6) };
        column.Children.AddRange(buttons);
        Border bar = new() { [DockPanel.DockProperty] = Dock.Left, Width = Width, BorderThickness = new Thickness(0, 0, 1, 0), Child = column };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable(TokenKeys.Divider));
        return bar;
    }

    /// <summary>The space between a side bar's groups of buttons.</summary>
    public static Control Gap() => new Border { Height = 8 };
}
