using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace Passenger.App;

/// <summary>Where every menu's popup stands (a context menu's, a menu flyout's): its frame is inset by the room its shadow
/// is drawn in (Shell.axaml's Passenger.MenuShadowRoom), since Avalonia draws nothing past a popup's edge, so the popup is
/// moved back by that room on the sides its placement aligns the frame to, and the frame opens where it would without it
/// (under a widget, beside a stripe's button, above the status bar).</summary>
internal static class MenuShadow
{
    public static void Install()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ContextMenu>((menu, _) => Fit(menu));
        ContextMenu.PlacementProperty.Changed.AddClassHandler<ContextMenu>((menu, _) => Fit(menu));
        PopupFlyoutBase.PlacementProperty.Changed.AddClassHandler<MenuFlyout>((flyout, _) => Fit(flyout));
    }

    /// <summary>The popup's offset for a placement: back by the room on the side the frame meets its target, by none
    /// across a side the frame is centred on.</summary>
    public static (double X, double Y) Offsets(PlacementMode placement, Thickness room) => placement switch
    {
        PlacementMode.TopEdgeAlignedLeft => (-room.Left, room.Bottom),
        PlacementMode.TopEdgeAlignedRight => (room.Right, room.Bottom),
        PlacementMode.BottomEdgeAlignedRight => (room.Right, -room.Top),
        PlacementMode.LeftEdgeAlignedTop => (room.Right, -room.Top),
        PlacementMode.LeftEdgeAlignedBottom => (room.Right, room.Bottom),
        PlacementMode.RightEdgeAlignedBottom => (-room.Left, room.Bottom),
        PlacementMode.Top => (0, room.Bottom),
        PlacementMode.Bottom => (0, -room.Top),
        PlacementMode.Left => (room.Right, 0),
        PlacementMode.Right => (-room.Left, 0),
        _ => (-room.Left, -room.Top),
    };

    private static void Fit(ContextMenu menu)
    {
        if (Room() is { } room)
        {
            (menu.HorizontalOffset, menu.VerticalOffset) = Offsets(menu.Placement, room);
        }
    }

    private static void Fit(MenuFlyout flyout)
    {
        if (Room() is { } room)
        {
            (flyout.HorizontalOffset, flyout.VerticalOffset) = Offsets(flyout.Placement, room);
        }
    }

    private static Thickness? Room() =>
        Application.Current?.TryGetResource(TokenKeys.MenuShadowRoom, null, out object? found) == true && found is Thickness room ? room : null;
}
