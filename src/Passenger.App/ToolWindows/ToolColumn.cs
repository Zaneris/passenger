using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Passenger.App.ToolWindows;

/// <summary>A tool window's toolbar down its left (the Debug window's): its buttons from the top while they fit, and in a
/// window too short for them all the last ones behind More at its foot. Worked out in the layout from the buttons' own
/// sizes, so hiding none asks for another pass; a button its window hides (Detach while launched) takes no room and is
/// never listed.</summary>
public sealed class ToolColumn : Panel
{
    private readonly Button more;
    private readonly List<Control> cut = [];

    /// <summary>Where a button with no room is put: drawn nowhere, the column clipping to itself what a button draws past
    /// its own empty box (its icon), and never hit.</summary>
    private static readonly Rect Nowhere = new(0, -10_000, 0, 0);

    public ToolColumn()
    {
        ClipToBounds = true;
        more = ToolButtons.Of(IconKeys.More, "More", ShowMore);
        Children.Add(more);
    }

    public double Spacing { get; init; }

    /// <summary>The buttons the column has no room for, which More lists; none while all fit.</summary>
    public IReadOnlyList<Button> Overflowing => [.. cut.OfType<Button>()];

    /// <summary>More, at the column's foot while some buttons have no room; drawn nowhere otherwise.</summary>
    public Button More => more;

    private IEnumerable<Control> Tools => Children.Where(c => c != more && c.IsVisible);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = 0;
        foreach (Control child in Children)
        {
            child.Measure(Size.Infinity);
            width = Math.Max(width, child.DesiredSize.Width);
        }
        Control[] tools = [.. Tools];
        double total = Math.Max(0, tools.Sum(t => t.DesiredSize.Height + Spacing) - Spacing);
        cut.Clear();
        if (total > availableSize.Height)
        {
            double room = availableSize.Height - more.DesiredSize.Height;
            double used = 0;
            int fits = 0;
            while (fits < tools.Length && used + tools[fits].DesiredSize.Height + Spacing <= room)
            {
                used += tools[fits].DesiredSize.Height + Spacing;
                fits++;
            }
            cut.AddRange(tools.Skip(fits));
        }
        return new Size(width, Math.Min(total, availableSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (Control tool in Tools)
        {
            if (cut.Contains(tool))
            {
                tool.Arrange(Nowhere);
                continue;
            }
            tool.Arrange(Centred(tool, y, finalSize.Width));
            y += tool.DesiredSize.Height + Spacing;
        }
        more.Arrange(cut.OfType<Button>().Any() ? Centred(more, y, finalSize.Width) : Nowhere);
        return finalSize;
    }

    private static Rect Centred(Control child, double y, double width) =>
        new((width - child.DesiredSize.Width) / 2, y, child.DesiredSize.Width, child.DesiredSize.Height);

    /// <summary>More's menu: each button the column has no room for, by its tip, doing what a press of it does.</summary>
    private void ShowMore()
    {
        ContextMenu menu = new() { Placement = PlacementMode.RightEdgeAlignedTop };
        foreach (Button button in Overflowing)
        {
            PathIcon glyph = new() { Data = (button.Content as PathIcon)?.Data };
            MenuItem item = new() { Header = ToolTip.GetTip(button) as string, Icon = glyph, IsEnabled = button.IsEffectivelyEnabled };
            if (button is ToggleButton toggle)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = toggle.IsChecked == true;
            }
            item.Click += (_, _) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            menu.Items.Add(item);
        }
        menu.Open(more);
    }
}
