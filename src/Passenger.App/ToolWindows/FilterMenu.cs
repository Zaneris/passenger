using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Passenger.App.ToolWindows;

/// <summary>A list's Sort and Filter button (the NuGet window's packages and projects, Attach to Process's programs): a
/// funnel at the end of the list's search, pressed while the list is narrowed or ordered otherwise than at first, its click
/// opening a menu of the list's choices made as it opens, each choice applied at once to what the list holds.</summary>
public sealed class FilterMenu
{
    private readonly Func<IReadOnlyList<Control>> items;

    /// <param name="tip">What the button's tip and accessible name say.</param>
    /// <param name="items">The menu's items as the list now stands: <see cref="MenuChoice"/>s and <see cref="ListMenus.Toggle"/>s.</param>
    public FilterMenu(string tip, Func<IReadOnlyList<Control>> items)
    {
        this.items = items;
        Button = ToolButtons.Toggle(IconKeys.Filter, tip);
        Button.Click += (_, _) =>
        {
            // A click toggles the button itself; what it shows is whether the list is narrowed.
            Button.IsChecked = narrowed;
            Open();
        };
    }

    private bool narrowed;

    public ToggleButton Button { get; }

    /// <summary>The menu while it is open, for a test to choose in.</summary>
    public ContextMenu? OpenMenu { get; private set; }

    /// <summary>Whether the list is narrowed or ordered otherwise than at first, which the button shows pressed.</summary>
    public bool Narrowed
    {
        get => narrowed;
        set => Button.IsChecked = narrowed = value;
    }

    /// <summary>The button put at the end of a list's search field, inside it.</summary>
    public void Inside(TextBox search)
    {
        Button.Margin = new Thickness(0, 0, Gaps.Hair, 0);
        Button.Classes.Add(StyleClasses.InField);
        search.InnerRightContent = Button;
    }

    /// <summary>The menu opened below the button.</summary>
    public void Open()
    {
        OpenMenu?.Close();
        ContextMenu menu = new() { ItemsSource = items(), Placement = PlacementMode.BottomEdgeAlignedLeft };
        OpenMenu = menu;
        menu.Closed += (_, _) => OpenMenu = OpenMenu == menu ? null : OpenMenu;
        menu.Open(Button);
    }
}
