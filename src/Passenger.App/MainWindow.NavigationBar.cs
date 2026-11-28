using Passenger.App.Editors;
using Passenger.App.Navigation;
using Passenger.Core;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The navigation bar over each editor (<see cref="NavigationBar"/>): where its files' outlines come from (a C#
/// file's own parse, any other's as Structure reads it), the list a part opens, under it, narrowed as typed, the row chosen
/// gone to in the bar's group, and Jump to Navigation Bar, which gives the bar the keys.</summary>
public partial class MainWindow
{
    private void AddNavigationBar()
    {
        Editors.NavigationOutlineOf = document => document.Syntax is { } syntax ? new CSharp.CSharpOutline(syntax, document.Text) : session.OutlineOf(document);
        Editors.NavigationListRequested += (_, list) => ShowNavigationList(list);
        Add(A.JumpToNavigationBar, ActionTitles.JumpToNavigationBar, ActionGroups.Navigate, _ => IdeErrors.Observe(JumpToNavigationBarAsync()),
            c => c.ByAgent ? Refusals.PersonsKeys : Refusals.NeedsFile(c) ?? (Editors.Active is { } file && Editors.NavigationOutlineOf(file) is null ? Refusals.NoOutline(file.Name) : null),
            ("Navigate", 1, 6), words: ["Breadcrumbs", "Navigation Bar", "Focus Breadcrumbs"]);
    }

    /// <summary>The keys to the active group's navigation bar, told as a region entered is; with the bar hidden, the list
    /// its last part opens; in a file that declares nothing, why nothing opens.</summary>
    private async Task JumpToNavigationBarAsync()
    {
        NavigationBar bar = Editors.ActiveGroup.Navigation;
        if (!await bar.JumpToAsync())
        {
            Refuse(Refusals.DeclaresNothing(Editors.Active?.Name ?? string.Empty));
        }
        else if (bar.Bar.IsKeyboardFocusWithin)
        {
            announcer.Tell(NavigationBar.Said, Urgency.Polite, regionTold);
        }
    }

    /// <summary>A navigation bar's list under the part that opened it: narrowed as typed as File Structure is, the row
    /// chosen gone to in the bar's group with the keys given to its editor.</summary>
    private void ShowNavigationList(NavigationList list)
    {
        SymbolPopup popup = new(list.Title, Navigator.OutlineFilter, async (query, cancel) =>
        {
            string filter = query.Trim();
            return filter.Length == 0 ? list.Rows : await BackgroundWork.Run(() => Navigator.FilterOutline(list.Rows, filter), cancel);
        }, initial: _ => list.Chosen, width: PopupFrame.Medium);
        Avalonia.Point? under = popups.PointIn(list.Under, new Avalonia.Point(0, list.Under == list.Group.Editor ? 0 : list.Under.Bounds.Height));
        popups.Show(popup, chosen =>
        {
            if (chosen is not null)
            {
                Editors.SetActiveGroup(list.Group);
                navigator.GoTo(chosen);
                list.Group.FocusShown();
            }
        }, under);
    }
}
