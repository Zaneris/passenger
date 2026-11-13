using Avalonia.Controls;
using Passenger.Workspace;

namespace Passenger.App.ToolWindows;

/// <summary>The order the NuGet window lists packages in: a search's best matches first (the installed packages by name),
/// or by name either way.</summary>
public enum PackageOrder
{
    BestMatch,
    NameAscending,
    NameDescending,
}

/// <summary>Which of the projects' packages the NuGet window lists: those they reference, those too that they get only
/// through another package or project, or those alone.</summary>
public enum PackageKinds
{
    Referenced,
    WithTransitive,
    TransitiveOnly,
}

/// <summary>The package list's Sort and Filter: its order, the kinds of package it lists, the vulnerable alone, and one
/// project's alone; each applied at once to the rows the list holds, never asking a feed again, and kept as the person
/// moves between packages, tabs and searches.</summary>
public sealed partial class NuGetView
{
    private readonly FilterMenu filters;
    private PackageOrder order;
    private PackageKinds kinds;
    private bool vulnerableOnly;
    private string? usedBy;

    /// <summary>Every row the last list found, before the filters narrowed it to <see cref="Rows"/>.</summary>
    private IReadOnlyList<PackageRow> all = [];

    /// <summary>The package list's Sort and Filter button and its menu.</summary>
    public FilterMenu Filters => filters;

    public PackageOrder Order
    {
        get => order;
        set => Narrow(() => order = value);
    }

    /// <summary>Which packages are listed; the installed packages are listed again as transitive ones come or go, a search's
    /// rows narrowed as they are.</summary>
    public PackageKinds Kinds
    {
        get => kinds;
        set
        {
            bool reads = (kinds == PackageKinds.Referenced) != (value == PackageKinds.Referenced);
            Narrow(() => kinds = value);
            if (reads && !showsUpdates && body.Content == packages && Query.Trim().Length == 0)
            {
                IdeErrors.Observe(RefreshAsync());
            }
        }
    }

    public bool VulnerableOnly
    {
        get => vulnerableOnly;
        set => Narrow(() => vulnerableOnly = value);
    }

    /// <summary>The project whose packages alone are listed (its path), or null for every project's.</summary>
    public string? UsedBy
    {
        get => usedBy;
        set => Narrow(() => usedBy = value);
    }

    /// <summary>Whether the list is narrowed or ordered otherwise than at first.</summary>
    private bool IsNarrowed => order != PackageOrder.BestMatch || kinds != PackageKinds.Referenced || vulnerableOnly || usedBy is not null;

    /// <summary>Manage NuGet Packages for Solution: the Packages tab, over every project.</summary>
    public void ShowSolution()
    {
        if (showsUpdates || body.Content != packages)
        {
            ShowPackages();
        }
        UsedBy = null;
    }

    /// <summary>A filter changed: the rows the list holds narrowed again, the chosen one kept where it is still listed.</summary>
    private void Narrow(Action change)
    {
        change();
        filters.Narrowed = IsNarrowed;
        List(all, Selected?.Id);
        ShowStatus();
    }

    /// <summary>The rows a list found as the filters show them: a project's alone (those it gets through another too while
    /// they are listed), the vulnerable alone, the transitive alone, in the order chosen.</summary>
    private IReadOnlyList<PackageRow> Filtered(IReadOnlyList<PackageRow> rows)
    {
        IEnumerable<PackageRow> shown = rows;
        if (usedBy is { } project)
        {
            shown = shown.Where(r => r.Installed.ContainsKey(project) || (kinds != PackageKinds.Referenced && r.Transitive.ContainsKey(project)));
        }
        if (vulnerableOnly)
        {
            shown = shown.Where(r => r.IsVulnerable);
        }
        if (kinds == PackageKinds.TransitiveOnly && !showsUpdates)
        {
            shown = shown.Where(r => r.IsTransitive);
        }
        return order switch
        {
            PackageOrder.NameAscending => [.. shown.OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)],
            PackageOrder.NameDescending => [.. shown.OrderByDescending(r => r.Id, StringComparer.OrdinalIgnoreCase)],
            _ => [.. shown],
        };
    }

    /// <summary>What the filters leave of the list, after its own words: how many rows of how many are shown, and what
    /// narrowed them; nothing while every row is shown.</summary>
    private string NarrowedWords()
    {
        if (rows.Count == all.Count)
        {
            return string.Empty;
        }
        List<string> by = [];
        if (usedBy is { } project)
        {
            by.Add($"used by {model.Projects.FirstOrDefault(p => Passenger.Core.PathNames.Same(p.Path, project))?.Name ?? Path.GetFileNameWithoutExtension(project)}");
        }
        if (vulnerableOnly)
        {
            by.Add("vulnerable");
        }
        if (kinds == PackageKinds.TransitiveOnly && !showsUpdates)
        {
            by.Add("transitive");
        }
        return $"{rows.Count} of {all.Count} shown{(by.Count > 0 ? $": {string.Join(", ", by)}" : string.Empty)}";
    }

    /// <summary>The Sort and Filter menu as the list now stands: the order, the kinds (the Updates tab's being the
    /// referenced packages alone), the vulnerable alone, a project's alone, and Clear Filters.</summary>
    private IReadOnlyList<Control> FilterItems()
    {
        List<Control> items =
        [
            MenuChoice.Of("Sort by Best Match", order == PackageOrder.BestMatch, () => Order = PackageOrder.BestMatch),
            MenuChoice.Of("Sort by Name, A to Z", order == PackageOrder.NameAscending, () => Order = PackageOrder.NameAscending),
            MenuChoice.Of("Sort by Name, Z to A", order == PackageOrder.NameDescending, () => Order = PackageOrder.NameDescending),
            new Separator(),
        ];
        foreach ((PackageKinds kind, string header) in (ReadOnlySpan<(PackageKinds, string)>)[(PackageKinds.Referenced, "Referenced Packages"),
            (PackageKinds.WithTransitive, "Referenced and Transitive Packages"), (PackageKinds.TransitiveOnly, "Transitive Packages Only")])
        {
            MenuItem item = MenuChoice.Of(header, kinds == kind, () => Kinds = kind);
            if (showsUpdates)
            {
                item.IsEnabled = false;
                ToolTip.SetTip(item, "Updates are of the packages the projects reference");
            }
            items.Add(item);
        }
        items.Add(new Separator());
        items.Add(FilterMenu.Toggle("Vulnerable Only", vulnerableOnly, on => VulnerableOnly = on));
        List<Control> projects = [MenuChoice.Of("All Projects", usedBy is null, () => UsedBy = null), new Separator()];
        foreach (ProjectModel project in model.Projects.Where(p => p.Exists).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            projects.Add(MenuChoice.Of(project.Name, usedBy is { } chosen && Passenger.Core.PathNames.Same(chosen, project.Path), () => UsedBy = project.Path));
        }
        items.Add(new MenuItem { Header = "Used By", ItemsSource = projects });
        items.Add(new Separator());
        MenuItem clear = new() { Header = "Clear Filters", IsEnabled = filters.Narrowed };
        clear.Click += (_, _) => Narrow(() => (order, vulnerableOnly, usedBy) = (PackageOrder.BestMatch, false, null));
        items.Add(clear);
        if (kinds != PackageKinds.Referenced)
        {
            clear.Click += (_, _) => Kinds = PackageKinds.Referenced;
        }
        return items;
    }
}
