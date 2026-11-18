using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>Where every menu's popup stands (a context menu's, a menu flyout's): its frame is inset by the room its shadow
/// is drawn in (Shell.axaml's Passenger.MenuShadowRoom), since Avalonia draws nothing past a popup's edge, so the popup is
/// moved back by that room on the sides its placement aligns the frame to, and the frame opens where it would without it
/// (under a widget, beside a stripe's button, above the status bar). A submenu's popup is its frame and the room round it,
/// the positioner placing the frame, and opens beside its item (<see cref="Beside"/>).</summary>
internal static class MenuShadow
{
    public static void Install()
    {
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<ContextMenu>((menu, _) => Fit(menu));
        ContextMenu.PlacementProperty.Changed.AddClassHandler<ContextMenu>((menu, _) => Fit(menu));
        PopupFlyoutBase.PlacementProperty.Changed.AddClassHandler<MenuFlyout>((flyout, _) => Fit(flyout));
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, e) => Beside(item, e.NameScope.Find<Popup>("PART_Popup")));
    }

    /// <summary>A submenu's popup opens beside its item on whichever side it has room, and the room its shadow is drawn in
    /// is the submenu's own: a pointer resting there keeps it open, as one on the frame does, and the pointer coming in
    /// chooses its item again, so an item it crossed on the way (the one below, on a slant) neither opens nor closes it.</summary>
    private static void Beside(MenuItem item, Popup? popup)
    {
        if (popup is null)
        {
            return;
        }
        popup.Placement = PlacementMode.Custom;
        popup.CustomPopupPlacementCallback = Place;
        ContentPresenter? shown = null;
        void Entered(object? sender, PointerEventArgs e) => Choose(item);
        popup.Opened += (_, _) =>
        {
            if (popup.Child?.GetVisualParent() is ContentPresenter room)
            {
                shown = room;
                room.Background = Brushes.Transparent;
                room.PointerEntered += Entered;
            }
        };
        popup.Closed += (_, _) =>
        {
            if (shown is not null)
            {
                shown.PointerEntered -= Entered;
                shown = null;
            }
        };
    }

    /// <summary>An item chosen in its menu and each menu above it, as the pointer on it chooses it.</summary>
    private static void Choose(MenuItem item)
    {
        for (MenuItem? at = item; at?.Parent is SelectingItemsControl menu; at = menu as MenuItem)
        {
            menu.SelectedIndex = menu.IndexFromContainer(at);
        }
    }

    /// <summary>A submenu's place: right of its item, else left, its item's row widened by the room for the shadow on the
    /// side it opens to, so the popup, room and all, lies over none of the item, since a desktop gives the pointer to a
    /// popup across its whole rect and the item would lose it to the shadow. The positioner places the frame: the popup's
    /// child less its margin, the room.</summary>
    private static void Place(CustomPopupPlacement placed)
    {
        Rect row = placed.AnchorRectangle;
        Thickness room = placed.Deflate;
        placed.AnchorRectangle = new Rect(row.X - room.Right, row.Y, row.Width + room.Left + room.Right, row.Height);
        placed.Anchor = PopupAnchor.TopRight;
        placed.Gravity = PopupGravity.BottomRight;
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
