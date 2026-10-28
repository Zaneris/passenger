namespace Passenger.App;

/// <summary>One entry of a menu: an action (titled as it is registered), a submenu, a separator, or a submenu the window
/// lists as it opens (View's Tool Windows, File's Recent Solutions), or such a list in its menu's own place (Window's open
/// solution windows).</summary>
public sealed record MenuEntry(string? Action, string? Title = null, IReadOnlyList<MenuEntry>? Items = null)
{
    public bool IsSeparator => Action is null && Items is null && Title is null;

    /// <summary>A submenu of what the window has as it opens, named by its title.</summary>
    public bool IsListed => Action is null && Items is null && Title is not null;

    /// <summary>A list whose items stand in its menu itself rather than in a submenu (the open solution windows at the
    /// Window menu's end).</summary>
    public bool Inline { get; init; }
}

/// <summary>The main menu: File, Edit, View, Navigate, Code, Refactor, Build, Run, Tests, Tools, Git, Window and Help,
/// each made of the actions that name a place in it (<see cref="IdeAction.Menus"/>), in their order, a separator
/// between each run of them. On macOS it is the menu bar (with Settings and Quit in the application menu, as there);
/// elsewhere a menu behind the toolbar's button.</summary>
public static class MainMenu
{
    public const string ToolWindows = "Tool Windows";
    public const string RecentSolutions = "Recent Solutions";

    /// <summary>The Run menu's Avalonia renderer debug overlays, each a toggle.</summary>
    public const string AvaloniaOverlays = "Avalonia Debug Overlays";

    /// <summary>The Window menu's list of the solution windows open.</summary>
    public const string OpenSolutionWindows = "Open Solution Windows";

    /// <summary>The menus, in their order.</summary>
    public static IReadOnlyList<string> Titles { get; } = ["File", "Edit", "View", "Navigate", "Code", "Refactor", "Build", "Run", "Tests", "Tools", "Git", "Window", "Help"];

    /// <summary>The submenus the window lists as each opens.</summary>
    private static readonly string[] Listed = [ToolWindows, RecentSolutions, OpenSolutionWindows];

    /// <summary>The lists whose items stand in their menu's own place.</summary>
    private static readonly string[] Inlined = [OpenSolutionWindows];

    /// <summary>The submenus, each where its parent lists it.</summary>
    private static readonly MenuPlace[] Submenus =
    [
        new("View/" + ToolWindows, 0, 0),
        new("View/Active Editor", 3, 0),
        new("File/" + RecentSolutions, 0, 3),
        new("File/Line Separators", 2, 1),
        new("File/Local History", 2, 3),
        new("Edit/Find", 3, 0),
        new("Navigate/Bookmarks", 4, 0),
        new("Run/" + AvaloniaOverlays, 3, 1),
        new("Tools/Migrations", 1, 0),
        new("Code/Code Completion", 0, 0),
        new("Code/Folding", 3, 0),
        new("Window/Layout Settings", 0, 0),
        new("Window/Editor Tabs", 1, 0),
        new("Window/" + OpenSolutionWindows, 4, 0),
    ];

    private static readonly MenuEntry Separator = new(null);

    /// <summary>The menus the actions make.</summary>
    public static IReadOnlyList<MenuEntry> Build(IEnumerable<IdeAction> actions)
    {
        List<(MenuPlace Place, string Id)> placed = [.. actions.SelectMany(a => a.Menus.Select(place => (place, a.Id)))];
        return [.. Titles.Select(title => Menu(title, placed))];
    }

    private static MenuEntry Menu(string path, List<(MenuPlace Place, string Id)> placed)
    {
        IEnumerable<(int Group, int Order, MenuEntry Entry)> actions = placed.Where(p => p.Place.Path == path)
            .Select(p => (p.Place.Group, p.Place.Order, new MenuEntry(p.Id)));
        IEnumerable<(int Group, int Order, MenuEntry Entry)> submenus = Submenus.Where(s => s.Path[..Math.Max(0, s.Path.LastIndexOf('/'))] == path)
            .Select(s => (s.Group, s.Order, Listed.Contains(Name(s.Path)) ? new MenuEntry(null, Name(s.Path)) { Inline = Inlined.Contains(Name(s.Path)) } : Menu(s.Path, placed)));
        List<MenuEntry> items = [];
        int? group = null;
        foreach ((int Group, int Order, MenuEntry Entry) item in actions.Concat(submenus).OrderBy(i => i.Group).ThenBy(i => i.Order))
        {
            // A list in its menu's place brings its own separator, there only while it lists something.
            if (group is { } before && before != item.Group && !item.Entry.Inline)
            {
                items.Add(Separator);
            }
            items.Add(item.Entry);
            group = item.Group;
        }
        return new MenuEntry(null, Name(path), items);
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
