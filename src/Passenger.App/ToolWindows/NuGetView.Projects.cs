using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.ToolWindows;

/// <summary>The order of the chosen package's projects: by name either way, those referencing it first or last, or by
/// the version they have, the newest first.</summary>
public enum ProjectOrder
{
    NameAscending,
    NameDescending,
    InstalledFirst,
    NotInstalledFirst,
    NewestVersionFirst,
}

/// <summary>Which of the chosen package's projects are listed: every one, those referencing it, those not (whether or not
/// another brings it in), or those whose version a newer one offered would update.</summary>
public enum ProjectsShown
{
    All,
    Installed,
    NotInstalled,
    WithUpdate,
}

/// <summary>The chosen package's projects: a line per project to Install, Update or Remove it, narrowed by their names
/// typed in the pane's filter and by its Sort and Filter, each kept as the person moves between packages.</summary>
public sealed partial class NuGetView
{
    private readonly TextBox projectSearch;
    private readonly FilterMenu projectFilters;
    private readonly TextBlock projectCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
    private readonly ToolFlow projectsHeader;
    private ProjectOrder projectOrder;
    private ProjectsShown projectsShown;
    private PackageRow? projectsOf;

    /// <summary>The buttons on the chosen package's lines, each with whether it puts the chosen version in (Install, Update,
    /// Downgrade) and its tip while it can act.</summary>
    private readonly List<(Button Button, bool PutsIn, string? Tip)> lineActions = [];

    /// <summary>The projects pane's Sort and Filter button and its menu.</summary>
    public FilterMenu ProjectFilters => projectFilters;

    /// <summary>The words narrowing the chosen package's projects to those whose names hold them.</summary>
    public string ProjectQuery
    {
        get => projectSearch.Text ?? string.Empty;
        set => projectSearch.Text = value;
    }

    public ProjectOrder ProjectOrder
    {
        get => projectOrder;
        set => NarrowProjects(() => projectOrder = value);
    }

    public ProjectsShown ProjectsShown
    {
        get => projectsShown;
        set => NarrowProjects(() => projectsShown = value);
    }

    /// <summary>The names of the chosen package's projects as the pane lists them, in its order.</summary>
    public IReadOnlyList<string> ProjectNames => [.. ProjectLines.Select(l => l.Children.OfType<ProjectLabel>().FirstOrDefault()?.Text).OfType<string>()];

    private (TextBox Search, FilterMenu Filters, ToolFlow Header) MakeProjectsHeader()
    {
        TextBox typed = SearchBox.Of("Filter projects");
        typed.Margin = new Thickness(Gaps.Wide, 0, Gaps.Tight, 0);
        typed.TextChanged += (_, _) => ShowProjectsAgain();
        FilterMenu menu = new("Sort and Filter Projects", ProjectFilterItems);
        menu.Inside(typed);
        projectCount.Classes.Add(StyleClasses.Muted);
        ToolFlow header = new()
        {
            Children = { new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "Projects", VerticalAlignment = VerticalAlignment.Center }, projectCount, typed },
        };
        return (typed, menu, header);
    }

    private void NarrowProjects(Action change)
    {
        change();
        projectFilters.Narrowed = projectOrder != ProjectOrder.NameAscending || projectsShown != ProjectsShown.All;
        ShowProjectsAgain();
    }

    private void ShowProjectsAgain()
    {
        if (projectsOf is { } row && Selected == row)
        {
            ShowProjects(row);
        }
    }

    /// <summary>The projects pane's Sort and Filter menu as it now stands.</summary>
    private IReadOnlyList<Control> ProjectFilterItems() =>
    [
        MenuChoice.Of("Sort by Name, A to Z", projectOrder == ProjectOrder.NameAscending, () => ProjectOrder = ProjectOrder.NameAscending),
        MenuChoice.Of("Sort by Name, Z to A", projectOrder == ProjectOrder.NameDescending, () => ProjectOrder = ProjectOrder.NameDescending),
        MenuChoice.Of("Sort Installed First", projectOrder == ProjectOrder.InstalledFirst, () => ProjectOrder = ProjectOrder.InstalledFirst),
        MenuChoice.Of("Sort Not Installed First", projectOrder == ProjectOrder.NotInstalledFirst, () => ProjectOrder = ProjectOrder.NotInstalledFirst),
        MenuChoice.Of("Sort by Version, Newest First", projectOrder == ProjectOrder.NewestVersionFirst, () => ProjectOrder = ProjectOrder.NewestVersionFirst),
        new Separator(),
        MenuChoice.Of("All Projects", projectsShown == ProjectsShown.All, () => ProjectsShown = ProjectsShown.All),
        MenuChoice.Of("Installed", projectsShown == ProjectsShown.Installed, () => ProjectsShown = ProjectsShown.Installed),
        MenuChoice.Of("Not Installed", projectsShown == ProjectsShown.NotInstalled, () => ProjectsShown = ProjectsShown.NotInstalled),
        MenuChoice.Of("With an Update", projectsShown == ProjectsShown.WithUpdate, () => ProjectsShown = ProjectsShown.WithUpdate),
    ];

    /// <summary>The chosen package's projects as the pane's filters leave them, in its order, with what each has of it.</summary>
    private IReadOnlyList<(ProjectModel Project, IReadOnlyList<InstalledVersion> At, IReadOnlyList<TransitiveUse> Brought)> ProjectsListed(PackageRow row, out int all)
    {
        (ProjectModel Project, IReadOnlyList<InstalledVersion> At, IReadOnlyList<TransitiveUse> Brought)[] each = [.. model.Projects.Where(p => p.Exists)
            .Select(p => (p, row.Installed.GetValueOrDefault(p.Path) ?? [], row.Transitive.GetValueOrDefault(p.Path) ?? []))];
        all = each.Length;
        string typed = ProjectQuery.Trim();
        string? newest = versions.FirstOrDefault();
        IEnumerable<(ProjectModel Project, IReadOnlyList<InstalledVersion> At, IReadOnlyList<TransitiveUse> Brought)> shown = each
            .Where(p => typed.Length == 0 || p.Project.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .Where(p => projectsShown switch
            {
                ProjectsShown.Installed => p.At.Count > 0,
                ProjectsShown.NotInstalled => p.At.Count == 0,
                ProjectsShown.WithUpdate => p.At.Count > 0 && newest is not null && InstalledVersion.Move(newest, p.At) is PackageMove.Update or PackageMove.UpdateAndDowngrade,
                _ => true,
            });
        IOrderedEnumerable<(ProjectModel Project, IReadOnlyList<InstalledVersion> At, IReadOnlyList<TransitiveUse> Brought)> ordered = projectOrder switch
        {
            ProjectOrder.NameDescending => shown.OrderByDescending(p => p.Project.Name, StringComparer.OrdinalIgnoreCase),
            ProjectOrder.InstalledFirst => shown.OrderBy(p => p.At.Count > 0 ? 0 : p.Brought.Count > 0 ? 1 : 2),
            ProjectOrder.NotInstalledFirst => shown.OrderBy(p => p.At.Count > 0 ? 2 : p.Brought.Count > 0 ? 1 : 0),
            ProjectOrder.NewestVersionFirst => shown.OrderByDescending(p => Newest(p.At, p.Brought) ?? string.Empty, VersionOrder.Instance),
            _ => shown.OrderBy(p => p.Project.Name, StringComparer.OrdinalIgnoreCase),
        };
        return [.. ordered.ThenBy(p => p.Project.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The newest version a project has of a package, referenced or brought in; null where it has none, which
    /// sorts last.</summary>
    private static string? Newest(IReadOnlyList<InstalledVersion> at, IReadOnlyList<TransitiveUse> brought) =>
        at.Select(v => v.Version).OfType<string>().Concat(brought.Select(b => b.Version)).Order(VersionOrder.Instance).LastOrDefault();

    /// <summary>The words on a project's line for its versions of the package: each once, those it references first, and on
    /// hover each framework's (named where it has several), with the range given where it shows another version and what
    /// brings in one it gets through others; nothing on hover where the line says it all.</summary>
    private static (string Shown, string? Told) LineVersions(IReadOnlyList<InstalledVersion> at, IReadOnlyList<TransitiveUse> uses)
    {
        bool named = at.Count + uses.Count > 1;
        string shown = string.Join(", ", at.Select(v => v.Shown ?? "installed").Concat(uses.Select(u => u.Version)).Distinct(StringComparer.OrdinalIgnoreCase));
        bool ranged = at.Any(v => v.Given is { } given && given != v.Shown);
        return (shown, named || ranged || uses.Count > 0
            ? string.Join("\n", at.Select(v => (named ? $"{v.Framework}: " : string.Empty) + (v.Shown ?? "installed")
                    + (v.Given is { } given && given != v.Shown ? $", given as {given}" : string.Empty))
                .Concat(uses.Select(u => (named ? $"{u.Framework}: " : string.Empty) + $"{u.Version}, brought in by {string.Join(", ", u.BroughtBy)}")))
            : null);
    }
}
