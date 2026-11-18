using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Passenger.App.Docking;

namespace Passenger.App;

/// <summary>The window's regions, which Next Region and Previous Region take the keys round in one order, as they stand on
/// the screen: the toolbar, the left edge's tabs of hidden tool windows, the tool windows docked on the left (the upper
/// first), the editors, those on the right, the right edge's tabs, those along the bottom (the left first), the bottom
/// edge's tabs, the status bar, and round to the toolbar again. A region where nothing can take the
/// keys (no file open, a status bar with nothing to press) is passed over. Each takes them as by Tab, so what has them
/// draws the ring round it, and is told as it is entered, through the window's announcer ("Explorer, tool window").</summary>
public partial class MainWindow
{
    private static readonly ToolAnchor[] LeftPlaces = [ToolAnchor.LeftTop, ToolAnchor.LeftBottom];
    private static readonly ToolAnchor[] RightPlaces = [ToolAnchor.RightTop, ToolAnchor.RightBottom];
    private static readonly ToolAnchor[] BottomPlaces = [ToolAnchor.BottomLeft, ToolAnchor.BottomRight];

    /// <summary>A region: what it is told as, the control holding it, and how it takes the keys (false where it could not).</summary>
    private sealed record Region(string Said, Control Holder, Func<bool> TakeKeys);

    /// <summary>What the regions are told about, so keys pressed in quick turn are heard as the region they came to.</summary>
    private readonly object regionTold = new();

    private void AddRegions()
    {
        actions!.Add(new IdeAction(AppActions.NextRegion, "Next Region", "Window", _ => GoRound(1))
        {
            WhyNot = c => c.ByAgent ? Refusals.PersonsKeys : null,
            Menus = [new MenuPlace("Window", 3, 0)],
            FromTerminal = true,
            Words = ["Next Pane", "Next Area", "Focus Next"],
        });
        actions.Add(new IdeAction(AppActions.PreviousRegion, "Previous Region", "Window", _ => GoRound(-1))
        {
            WhyNot = c => c.ByAgent ? Refusals.PersonsKeys : null,
            Menus = [new MenuPlace("Window", 3, 1)],
            FromTerminal = true,
            Words = ["Previous Pane", "Previous Area", "Focus Previous"],
        });
        // A tool window opened over the editors from its edge tab, or put away from there, is told as a region entered is.
        Dock.PeekChanged += (_, e) => announcer.Tell(e.Over ? $"{e.Window.Title}, tool window over the editors"
            : e.Window.IsShown ? $"{e.Window.Title}, tool window" : $"{e.Window.Title} hidden", Urgency.Polite, regionTold);
    }

    /// <summary>The regions shown now, in their order.</summary>
    private List<Region> Regions()
    {
        List<Region> regions = [new("Toolbar", Toolbar, () => FirstTaking(Toolbar))];
        regions.AddRange(Edge(ToolAnchor.LeftTop));
        regions.AddRange(Dock.ShownAt(LeftPlaces).Where(shown => Laid(shown.Place)).Select(ToolRegion));
        if (Editors.Active is { } file && Laid(Editors))
        {
            regions.Add(new($"{file.Title}, editor", Editors, () => Editors.ActiveGroup.FocusShown(NavigationMethod.Tab)));
        }
        regions.AddRange(Dock.ShownAt(RightPlaces).Where(shown => Laid(shown.Place)).Select(ToolRegion));
        regions.AddRange(Edge(ToolAnchor.RightTop));
        regions.AddRange(Dock.ShownAt(BottomPlaces).Where(shown => Laid(shown.Place)).Select(ToolRegion));
        regions.AddRange(Edge(ToolAnchor.BottomLeft));
        regions.Add(new("Status bar", StatusBarFrame, () => FirstTaking(StatusBarFrame)));
        return regions;

        static Region ToolRegion((ToolWindow Window, Control Place) shown) =>
            new($"{shown.Window.Title}, tool window", shown.Place, () => shown.Window.TakeKeys(NavigationMethod.Tab));

        Region[] Edge(ToolAnchor place) => Dock.EdgeOf(place) is { } edge && Laid(edge) ? [new(edge.Said, edge, edge.TakeKeys)] : [];

        // A maximised tool window leaves the others, and the editors, no room.
        static bool Laid(Control region) => region.IsEffectivelyVisible && region.Bounds.Width > 0 && region.Bounds.Height > 0;
    }

    /// <summary>Takes the keys <paramref name="step"/> regions on from the one holding them, or from the first (the last,
    /// going back) where none does, passing over a region that cannot take them, and tells the region entered.</summary>
    private void GoRound(int step)
    {
        UiThread.Check();
        List<Region> regions = Regions();
        Visual? focused = FocusManager?.GetFocusedElement() as Visual;
        int at = focused is null ? -1 : regions.FindIndex(r => r.Holder == focused || r.Holder.IsVisualAncestorOf(focused));
        if (!IsActive)
        {
            Activate();
        }
        for (int tried = 1; tried <= regions.Count; tried++)
        {
            int next = at < 0
                ? (step > 0 ? tried - 1 : regions.Count - tried)
                : (((at + (step * tried)) % regions.Count) + regions.Count) % regions.Count;
            Region region = regions[next];
            if (region.TakeKeys())
            {
                announcer.Tell(region.Said, Urgency.Polite, regionTold);
                return;
            }
        }
    }

    /// <summary>Gives the keys, as by Tab, to the first control along a bar (<paramref name="area"/>) that takes them, from
    /// its left.</summary>
    private static bool FirstTaking(Control area) => area.GetVisualDescendants().OfType<Control>()
        .Where(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && KeyboardNavigation.GetIsTabStop(c))
        .OrderBy(c => c.TranslatePoint(default, area)?.X ?? double.MaxValue)
        .Any(c => c.Focus(NavigationMethod.Tab));
}
