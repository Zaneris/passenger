using Avalonia.Controls;
using Avalonia.Layout;
using Passenger.App.Editors;
using Passenger.App.Settings;
using Passenger.App.Workspace;
using A = Passenger.App.AppActions;

namespace Passenger.App;

/// <summary>The editor tab actions, in Window > Editor Tabs and each tab's own menu: the next and previous tab, Pin
/// Tab, the closes (the tab, the others, all, all but pinned, those to one side), Reopen Closed Tab, the splits and
/// Unsplit, and a tab moved to the opposite group or a window of its own. From a tab's menu they act on that tab, else
/// on the one the active group shows; Restore Default Layout is beside them, in Window > Layout Settings. A tab's menu
/// also copies its file's path and shows it in the file manager or a terminal; a double-click on a tab hides the tool
/// windows, or brings them back.</summary>
public partial class MainWindow
{
    /// <summary>The tabs a group's strip hides, listed under its button, the one chosen gone to.</summary>
    private void ShowHiddenTabs(EditorGroup group)
    {
        List<OpenDocument> hidden = [.. group.HiddenTabs.Select(tab => tab.Document)];
        if (hidden.Count == 0)
        {
            return;
        }
        Button button = group.HiddenTabsButton;
        // Its right edge under the button's, at the strip's end.
        Avalonia.Point? under = popups.PointIn(button, new Avalonia.Point(button.Bounds.Width - PopupFrame.Medium, button.Bounds.Height));
        popups.Show(new HiddenTabsPopup(hidden, Folder), chosen =>
        {
            if (chosen is not null && group.Has(chosen.Document))
            {
                group.GoTo(chosen.Document);
            }
        }, under);
    }

    private const string Tabs = "Editor Tabs";
    private const string TabsMenu = "Window/Editor Tabs";

    /// <summary>A tab's menu items, in their order: the file's path and Open In as the Explorer's row has them, an HTML
    /// page's browser too.</summary>
    private static MenuEntry[] TabMenuItems(OpenDocument file) =>
    [
        .. Items(A.CloseTab, A.CloseOtherTabs, A.CloseAllTabs, A.CloseUnpinned, A.CloseTabsLeft, A.CloseTabsRight, null,
            A.SplitRight, A.SplitDown, A.MoveToOppositeGroup, A.Unsplit, A.UnsplitAll, null,
            A.PinTab, A.OpenInNewWindow, null,
            A.CopyPath),
        new MenuEntry(null, "Open In", Languages.Language.ForPath(file.Path) == Languages.Language.Html
            ? Items(A.RevealInFileManager, A.OpenInTerminal, A.OpenInBrowser)
            : Items(A.RevealInFileManager, A.OpenInTerminal)),
        .. Items(null, A.ReopenClosedTab),
    ];

    private void AddTabActions()
    {
        Editors.TabMenuOpening += (_, opening) => FillTabMenu(opening);
        Editors.MaximizeRequested += (_, _) => Request(A.HideAllToolWindows);
        Editors.HiddenTabsRequested += (_, group) => ShowHiddenTabs(group);
        Tab(A.NextTab, "Next Tab", _ => Editors.Cycle(1), Refusals.NeedsFile, (0, 0));
        Tab(A.PreviousTab, "Previous Tab", _ => Editors.Cycle(-1), Refusals.NeedsFile, (0, 1));
        actions!.Add(new IdeAction(A.PinTab, "Pin Tab", Tabs, c => TabOf(c)!.Value.Group.Pin(TabOf(c)!.Value.File, !Pinned(c)))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace(TabsMenu, 0, 2)],
            TitleIn = c => Pinned(c) ? "Unpin Tab" : "Pin Tab",
        });

        // The split worked in loses its tab; the file stays open in any other.
        actions.Add(new IdeAction(A.CloseTab, "Close Tab", "IDE", c => Editors.CloseTab(TabOf(c)!.Value.Group, TabOf(c)!.Value.File))
        {
            WhyNot = Refusals.NeedsFile,
            Menus = [new MenuPlace("File", 1, 2), new MenuPlace(TabsMenu, 1, 0)],
            TitleIn = c => c.Tab is null ? "Close Tab" : "Close",
        });
        Tab(A.CloseOtherTabs, "Close Other Tabs", c => Editors.CloseOthers(TabOf(c)!.Value.Group, TabOf(c)!.Value.File),
            c => Closes(c, (tab, _) => tab.Document != TabOf(c)!.Value.File, "No other unpinned tab is open"), (1, 1));
        Tab(A.CloseAllTabs, "Close All Tabs", c => Editors.CloseAllTabs(TabOf(c)!.Value.Group), c => Closes(c, (_, _) => true, "Every tab is pinned"), (1, 2));
        Tab(A.CloseUnpinned, "Close All but Pinned", c => Editors.CloseUnpinned(c.Tab?.Group),
            c => Refusals.NeedsFile(c) ?? ((c.Tab is { } tab ? [tab.Group] : Editors.Groups).Any(g => EditorArea.Unpinned(g, (_, _) => true).Count > 0) ? null : "Every tab is pinned"), (1, 3));
        Tab(A.CloseTabsLeft, "Close Tabs to the Left", c => Editors.CloseToSide(TabOf(c)!.Value.Group, TabOf(c)!.Value.File, left: true),
            c => Closes(c, (_, index) => index < IndexOf(c), "No unpinned tab is to the left"), (1, 4));
        Tab(A.CloseTabsRight, "Close Tabs to the Right", c => Editors.CloseToSide(TabOf(c)!.Value.Group, TabOf(c)!.Value.File, left: false),
            c => Closes(c, (_, index) => index > IndexOf(c), "No unpinned tab is to the right"), (1, 5));

        Tab(A.ReopenClosedTab, "Reopen Closed Tab", _ =>
        {
            if (Editors.ReopenClosedTab() is { } why)
            {
                status.Text = why;
            }
        }, _ => Editors.CanReopen ? null : "No tab has been closed", (2, 0));
        // Found by older names too.
        Tab(A.SplitRight, "Split Right", c => Editors.Split(TabOf(c)!.Value.Group, TabOf(c)!.Value.File, Orientation.Horizontal, move: false), Refusals.NeedsFile, (2, 1), ["Split Vertically"]);
        Tab(A.SplitDown, "Split Down", c => Editors.Split(TabOf(c)!.Value.Group, TabOf(c)!.Value.File, Orientation.Vertical, move: false), Refusals.NeedsFile, (2, 2), ["Split Horizontally"]);
        Tab(A.Unsplit, "Unsplit", c => Editors.Unsplit(GroupOf(c), all: false), NotSplit, (2, 3));
        Tab(A.UnsplitAll, "Unsplit All", c => Editors.Unsplit(GroupOf(c), all: true), NotSplit, (2, 4));

        // The Window menu keeps these two after its Editor Tabs.
        Add(A.MoveToOppositeGroup, "Move to Opposite Group", Tabs, c => Editors.MoveToOpposite(TabOf(c)!.Value.Group, TabOf(c)!.Value.File), c => Refusals.NeedsFile(c) ?? NotSplit(c), ("Window", 2, 0));
        Add(A.OpenInNewWindow, "Open Tab in New Window", Tabs, c => Editors.OpenInNewWindow(TabOf(c)!.Value.Group, TabOf(c)!.Value.File), Refusals.NeedsFile, ("Window", 2, 1));

        actions.Add(new IdeAction(A.RestoreDefaultLayout, "Restore Default Layout", "Tool Windows", _ => Dock.RestoreDefault())
        {
            Menus = [new MenuPlace("Window/Layout Settings", 0, 0)],
            FromTerminal = true,
        });

        string? Closes(ActionContext c, Func<EditorTab, int, bool> which, string none) =>
            Refusals.NeedsFile(c) ?? (EditorArea.Unpinned(TabOf(c)!.Value.Group, which).Count > 0 ? null : none);

        int IndexOf(ActionContext c) => TabOf(c)!.Value.Group.Tabs.Select(t => t.Document).ToList().IndexOf(TabOf(c)!.Value.File);

        string? NotSplit(ActionContext c) => Editors.IsSplit(GroupOf(c)) ? null : "The editor is not split";
    }

    /// <summary>The theme's default layout as the dock's default, once every tool window is added: what the window opens on
    /// where no layout is kept, and what Restore Default Layout goes back to; and the arrangement it gives where the person
    /// set none.</summary>
    private void FollowThemeLayout()
    {
        EventHandler follow = (_, _) =>
        {
            Dock.UseDefault(Themes.Layout);
            FollowArrangement(settingsStore.Effective);
        };
        follow(null, EventArgs.Empty);
        Themes.LayoutChanged += follow;
        Closed += (_, _) => Themes.LayoutChanged -= follow;
    }

    /// <summary>The window's arrangement in force, the person's where they set it, else the theme's: where the editor's tabs
    /// stand.</summary>
    private void FollowArrangement(AppSettings next) => Editors.TabsPlace = next.TabsPlace();

    /// <summary>The tab an action acts on: the one whose menu it was chosen from, else the one the active group shows.</summary>
    private (EditorGroup Group, OpenDocument File)? TabOf(ActionContext context) =>
        context.Tab ?? (context.File is { } file ? (Editors.ActiveGroup, file) : null);

    private EditorGroup GroupOf(ActionContext context) => context.Tab?.Group ?? Editors.ActiveGroup;

    private bool Pinned(ActionContext context) => TabOf(context) is { } tab && tab.Group.IsPinned(tab.File);

    /// <summary>A tab action listed in Window > Editor Tabs at a place.</summary>
    private void Tab(string id, string title, Action<ActionContext> run, Func<ActionContext, string?> whyNot, (int Group, int Order) at, IReadOnlyList<string>? words = null) =>
        Add(id, title, Tabs, run, whyNot, (TabsMenu, at.Group, at.Order), words);

    /// <summary>The tab menu, its items run on the tab it opened on.</summary>
    private void FillTabMenu(TabMenu opening)
    {
        ActionContext context = new(opening.Document, opening.Group.Editor, null, null, session.Debug.IsDebugging ? session.Debug.Session?.State : null)
        {
            Tab = (opening.Group, opening.Document),
        };
        opening.Menu.ItemsSource = TabMenuItems(opening.Document).Select(entry => FlyoutEntry(entry, context)).ToList();
    }

    private static MenuEntry[] Items(params string?[] ids) => [.. ids.Select(id => new MenuEntry(id))];
}
