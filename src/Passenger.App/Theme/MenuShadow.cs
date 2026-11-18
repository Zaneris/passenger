using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Passenger.App;

/// <summary>Where a submenu's popup stands: beside its item (<see cref="Beside"/>). Every menu's popup is its frame and
/// the room round it its shadow is drawn in (Shell.axaml's Passenger.MenuShadowRoom), the margin of what the popup holds,
/// so the positioner places the frame, at the pointer, under a widget or beside an item, and the room comes with it on
/// whichever side the menu opens.</summary>
internal static class MenuShadow
{
    public static void Install() =>
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<MenuItem>((item, e) => Beside(item, e.NameScope.Find<Popup>("PART_Popup")));

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
}
