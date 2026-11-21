using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's bars, one look in every window: along its top, its tabs, fields, buttons and words over a
/// rule; down its left, its icon buttons in a <see cref="ToolColumn"/> beside a rule; between a bar's groups, its
/// <see cref="Rule"/> or <see cref="Gap"/>. UiRulesTests fails a bar or a bar's rule built by hand.</summary>
public static class ToolBars
{
    /// <summary>The top bar's height: a control's (Passenger.ControlHeight, 28: a tab, a field, a button, a drop-down) with
    /// 2 above and below, the least that holds the tallest part a bar has without crowding it, as most bars were.</summary>
    public const double Height = 32;

    /// <summary>The side bar's width: an icon button's (Passenger.ControlHeight, 28) with Gaps.Tight each side.</summary>
    public const double Width = 36;

    /// <summary>A bar along a window's top, docked there, holding <paramref name="content"/> over its rule. A bar whose
    /// parts wrap (<see cref="ToolFlow"/>, the Migrations window's, in a narrow window) grows a row at a time.</summary>
    public static Border Top(Control content)
    {
        Border bar = new() { [DockPanel.DockProperty] = Dock.Top, MinHeight = Height, BorderThickness = Hairlines.Below, Child = content };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable(TokenKeys.Divider));
        return bar;
    }

    /// <summary>A top bar over an editor (a table's, a XAML preview's), on a tool window's ground, which tells it from the
    /// editor's by shade.</summary>
    public static Border OverEditor(Control content)
    {
        Border bar = Top(content);
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable(TokenKeys.Panel));
        return bar;
    }

    /// <summary>A bar down a window's left, docked there, its buttons from the top and those with no room behind More
    /// (<see cref="ToolColumn"/>), its rule at its right.</summary>
    public static Border Side(params Control[] buttons)
    {
        ToolColumn column = new() { Spacing = Gaps.Hair, Margin = new Thickness(0, Gaps.Small) };
        column.Children.AddRange(buttons);
        Border bar = new() { [DockPanel.DockProperty] = Dock.Left, Width = Width, BorderThickness = Hairlines.After, Child = column };
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable(TokenKeys.Divider));
        return bar;
    }

    /// <summary>The space between a side bar's groups of buttons.</summary>
    public static Control Gap() => new Border { Height = Gaps.Normal };

    /// <summary>The line between a bar's groups (a list's choices and its filters, a diff's actions and its steps): the
    /// title bar's rule (Shell.axaml's <c>toolbarRule</c>), in the divider's colour on a tool window's ground.</summary>
    public static Control Rule() => new Border { Classes = { StyleClasses.ToolbarRule }, VerticalAlignment = VerticalAlignment.Center };
}
