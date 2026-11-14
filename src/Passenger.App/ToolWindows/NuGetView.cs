using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Packages;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Packages;
using Passenger.Workspace;

namespace Passenger.App.ToolWindows;

/// <summary>A package as the NuGet window lists it: what the feeds say of it, the feeds that have it, the versions
/// the solution's projects have of it (by project, each framework's), those they get only through another package or
/// project, the known vulnerabilities of those versions, and whether their owner deprecated one.</summary>
public sealed record PackageRow(string Id, string Version, string Description, string Authors, long Downloads,
    IReadOnlyDictionary<string, IReadOnlyList<InstalledVersion>> Installed)
{
    public bool IsInstalled => Installed.Count > 0;

    /// <summary>The versions in force in the projects, each once.</summary>
    public IEnumerable<string> InstalledVersions => Installed.Values.SelectMany(v => v).Select(v => v.Version).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>By project, the versions a project referencing none of it gets through the packages and projects it does
    /// reference, as its restore resolved them.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TransitiveUse>> Transitive { get; init; } = new Dictionary<string, IReadOnlyList<TransitiveUse>>();

    /// <summary>Whether projects get it only through others, none referencing it.</summary>
    public bool IsTransitive => !IsInstalled && Transitive.Count > 0;

    /// <summary>Every version a project has, referenced or brought in, each once: those the audit is asked of.</summary>
    public IEnumerable<string> AuditedVersions => InstalledVersions.Concat(Transitive.Values.SelectMany(v => v).Select(v => v.Version)).Distinct(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Feeds { get; init; } = [];

    /// <summary>The advisories that affect a version a project has, the gravest first.</summary>
    public IReadOnlyList<Advisory> Advisories { get; init; } = [];

    public bool IsVulnerable => Advisories.Count > 0;

    /// <summary>The newest version a project has that its owner deprecated, with what they say of it; null where none is,
    /// or none is known yet.</summary>
    public (string Version, Deprecation Said)? Deprecated { get; init; }

    public bool IsDeprecated => Deprecated is not null;

    public string? UpdateVersion { get; init; }

    public IReadOnlyList<string> AvailableVersions { get; init; } = [];
}

/// <summary>The NuGet window, its Packages and Sources tabs in its header. Packages: the projects' packages with nothing
/// typed, else what the feed chosen beside the query finds; the chosen package's versions and a row per project to Install,
/// Update or Remove it through the .NET command line, whose restore uses those feeds and their credentials. A version with
/// a known vulnerability (the audit's VulnerabilityInfo, as restore reads it), a project's or the one chosen to put in, is
/// marked with its advisories, and Update offers the least version that fixes it; one its owner deprecated (the feed's
/// registration, read while the window is shown) with why and what to use instead. Sources: the files and their feeds
/// (<see cref="NuGetSourcesView"/>). A view over the folder's <see cref="FolderPackages"/>, its session's.</summary>
public sealed partial class NuGetView : DockPanel
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private readonly StackPanel tabs = new() { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
    private readonly ContentControl body = new();
    private readonly DockPanel packages = new();
    private readonly TextBox search;
    private readonly ComboBox chooser;
    private readonly CheckBox prerelease;
    private readonly ListBox list;
    private readonly StackPanel details;
    private readonly TextBox log;
    private readonly TextBlock status;
    private readonly Debounce typing;
    private CancellationTokenSource? asking;
    private CancellationTokenSource? showing;
    private IReadOnlyList<PackageRow> rows = [];
    private IReadOnlyList<string> versions = [];
    private FolderPackages model;
    private bool choosing;

    /// <summary>What the list says of itself (what was found, why a feed did not answer) and its tip, shown in the status
    /// unless a change's words hold it.</summary>
    private (string Words, string? Tip) listed = (string.Empty, null);

    /// <summary>What a package change came to, or that one is under way: held in the status over the list's words until
    /// the person next asks for a list or another change begins.</summary>
    private string? outcome;

    /// <param name="folder">The folder's packages, which the window lists and changes.</param>
    public NuGetView(FolderPackages folder)
    {
        model = folder;
        search = new TextBox { Classes = { StyleClasses.Search }, PlaceholderText = "Search packages", VerticalAlignment = VerticalAlignment.Center };
        chooser = new ComboBox { Classes = { StyleClasses.Compact }, MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 8, 0) };
        ToolTip.SetTip(chooser, "The feeds searched");
        chooser.SelectionChanged += (_, _) =>
        {
            if (!choosing)
            {
                Hold(null);
                IdeErrors.Observe(RefreshAsync());
            }
        };
        prerelease = new CheckBox { Content = "Prerelease", VerticalAlignment = VerticalAlignment.Center };
        status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        status.Classes.Add(StyleClasses.Muted);
        Button refresh = ToolButtons.Of(AppActions.NuGetRefresh, IconKeys.Rerun, "Refresh", action => ActionRequested?.Invoke(this, action));
        refresh.Margin = new Thickness(6, 0, 0, 0);
        updateAll = new Button { Content = "Update All", Focusable = false, IsVisible = false, IsEnabled = false, Margin = new Thickness(8, 0, 0, 0) };
        updateAll.Click += (_, _) => ActionRequested?.Invoke(this, AppActions.NuGetUpdateAll);
        typing = new Debounce(Pause, SearchAsync);
        search.TextChanged += (_, _) =>
        {
            Hold(null);
            typing.Restart();
        };
        prerelease.IsCheckedChanged += (_, _) =>
        {
            Hold(null);
            IdeErrors.Observe(RefreshAsync());
        };
        filters = new FilterMenu("Sort and Filter Packages", FilterItems);
        (projectSearch, projectFilters, projectsHeader) = MakeProjectsHeader();
        // Inside the search, so the bar's parts keep their room at the window's least width.
        filters.Inside(search);
        ToolFlow.SetFills(search, new ToolLayout.Fills(140, 260));
        ToolFlow.SetFills(status, new ToolLayout.Fills(120));
        Border top = ToolBars.Top(new ToolFlow { Margin = new Thickness(8, 2), Children = { search, chooser, prerelease, refresh, updateAll, status } });

        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
        };
        ReusedRows.Use(list, Row);
        list.SelectionChanged += (_, _) => IdeErrors.Observe(ShowAsync());
        details = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
        log = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontSize = 11,
            Height = 90,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Brushes.Transparent,
            IsVisible = false,
            [DockProperty] = Dock.Bottom,
        }.InCodeFont();
        log.Bind(TextBox.BorderBrushProperty, log.GetResourceObservable(TokenKeys.Divider));
        DockPanel right = new();
        right.Children.Add(log);
        right.Children.Add(new ScrollViewer { Content = details });
        packages.Children.Add(top);
        packages.Children.Add(new PaneSplit(list, right, "2*,1,3*"));

        Sources = new NuGetSourcesView(this);
        Children.Add(body);
        ShowPackages();
        Follow(model);
        AttachedToVisualTree += (_, _) =>
        {
            IdeErrors.Observe(Shown());
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        };
    }

    /// <summary>The folder's packages the window shows, its session's.</summary>
    public FolderPackages Packages => model;

    /// <summary>Shows another folder's packages from now on, its session's in place of the last's: the last folder's rows
    /// and words cleared at once, and its own listed while the window is shown (again as its projects are evaluated), else
    /// as it is next shown; its sources read if the last folder's had been, so the feeds offered are its own.</summary>
    public void Bind(FolderPackages next)
    {
        if (next == model)
        {
            return;
        }
        bool read = model.Sources != PackageSourceSet.None;
        model.Line -= OnLine;
        model.SourcesChanged -= OnSourcesChanged;
        model.ChangeBegun -= OnChangeBegun;
        model.Changed -= OnChanged;
        model = next;
        // A project of the last folder's narrows nothing of this one's.
        usedBy = null;
        filters.Narrowed = IsNarrowed;
        updateRows = [];
        asking?.Cancel();
        List([], null);
        Tell(string.Empty);
        Hold(null);
        Follow(next);
        if (IsShown)
        {
            IdeErrors.Observe(Shown());
        }
        else if (next.Sources == PackageSourceSet.None && read)
        {
            IdeErrors.Observe(ReloadSourcesAsync(list: false));
        }
    }

    /// <summary>Whether the window is shown, so a list it reads is seen.</summary>
    public bool IsShown => TopLevel.GetTopLevel(this) is not null;

    /// <summary>Hears what the folder's packages say, and offers their feeds when they are read already.</summary>
    private void Follow(FolderPackages packages)
    {
        packages.Line += OnLine;
        packages.SourcesChanged += OnSourcesChanged;
        packages.ChangeBegun += OnChangeBegun;
        packages.Changed += OnChanged;
        if (packages.Sources != PackageSourceSet.None)
        {
            OnSourcesChanged(packages, EventArgs.Empty);
        }
    }

    private void OnLine(object? sender, string line)
    {
        log.IsVisible = true;
        log.Text = $"{log.Text}{line}\n";
        log.CaretIndex = log.Text.Length;
    }

    /// <summary>A package change begun, whoever asked for it: said, and the projects' actions disabled until it ends.</summary>
    private void OnChangeBegun(object? sender, string stage)
    {
        Hold("Working…");
        EnableActions();
    }

    /// <summary>A package change ended, whoever asked for it: said, and the list read again under its words; one refused
    /// changed nothing.</summary>
    private void OnChanged(object? sender, PackageOutcome ended)
    {
        EnableActions();
        if (updatingAll)
        {
            return;
        }
        if (ended.Refused is { } why)
        {
            Hold(why);
            return;
        }
        Hold(ended.Done ? "Done" : $"The change failed{(ended.Left is { } left ? $", {left}" : string.Empty)}; the log says why");
        IdeErrors.Observe(RefreshAsync());
    }

    /// <summary>A change's words held in the status over the list's, or, with null, the list's own shown again.</summary>
    private void Hold(string? words)
    {
        outcome = words;
        ShowStatus();
    }

    /// <summary>The list's words, shown in the status unless a change's are held there.</summary>
    private void Tell(string words, string? tip = null)
    {
        listed = (words, tip);
        ShowStatus();
    }

    /// <summary>The status: a change's words while held, whole on hover (a narrow window cuts them) with the list's after
    /// them, else the list's.</summary>
    private void ShowStatus()
    {
        string words = string.Join("; ", new[] { listed.Words, NarrowedWords() }.Where(w => w.Length > 0));
        status.Text = outcome ?? words;
        ToolTip.SetTip(status, outcome is null ? listed.Tip : string.Join("\n", new[] { outcome, words }.Where(w => w.Length > 0)));
    }

    /// <summary>The projects' Install, Update, Downgrade and Remove, enabled only while no package change is under way.</summary>
    private void EnableActions()
    {
        updateAll.IsEnabled = CanUpdateAll;
        ToolTip.SetTip(updateAll, $"Update all {updateRows.Count} packages across their projects, including results hidden by search");
        foreach (Button button in ProjectLines.SelectMany(line => line.Children.OfType<Button>()))
        {
            button.IsEnabled = !model.IsBusy;
        }
    }

    /// <summary>The chosen package's line per project, among its details; null while none is chosen.</summary>
    private StackPanel? ProjectsPanel => details.Children.OfType<StackPanel>().FirstOrDefault(p => p.Name == "projects");

    /// <summary>The chosen package's line per project, its header aside.</summary>
    private IEnumerable<Panel> ProjectLines => ProjectsPanel?.Children.OfType<Panel>().Where(line => line != projectsHeader) ?? [];

    /// <summary>One of the window's actions asked for by its button.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>An advisory's link clicked, for the browser.</summary>
    public event EventHandler<Uri>? LinkClicked;

    /// <summary>The Packages and Sources tabs, for the tool window's header.</summary>
    public Control Tabs => tabs;

    /// <summary>The Sources tab: the nuget.config files in effect and their feeds.</summary>
    public NuGetSourcesView Sources { get; }

    /// <summary>Whether the Sources tab is the one shown.</summary>
    public bool ShowsSources => body.Content == Sources;

    /// <summary>The folder's package sources as last read.</summary>
    public PackageSourceSet PackageSources => model.Sources;

    /// <summary>What the feed chooser offers: All Feeds, then each enabled feed by name.</summary>
    public IReadOnlyList<string> FeedChoices => [.. chooser.Items.OfType<string>()];

    /// <summary>The chooser's choice: All Feeds or a feed's name.</summary>
    public string ChosenFeed
    {
        get => chooser.SelectedItem as string ?? FeedSet.All;
        set => chooser.SelectedItem = FeedChoices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The NuGet window has no feed {value}; it offers {string.Join(", ", FeedChoices)}");
    }

    /// <summary>Where packages are searched: the chooser's feeds, unless one is set (the folder's packages' own).</summary>
    public IPackageFeed Feed
    {
        get => model.Given ?? model.Chosen(ChosenFeed);
        set => model.Given = value;
    }

    /// <summary>The packages listed, as the filters leave them.</summary>
    public IReadOnlyList<PackageRow> Rows => rows;

    /// <summary>The list the packages are chosen in.</summary>
    public ListBox PackageList => list;

    public PackageRow? Selected => list.SelectedItem as PackageRow;

    /// <summary>The versions offered for the chosen package, newest first.</summary>
    public IReadOnlyList<string> Versions => versions;

    /// <summary>The version the Install and Update buttons put in.</summary>
    public string? ChosenVersion { get; set; }

    public string Query
    {
        get => search.Text ?? string.Empty;
        set => search.Text = value;
    }

    /// <summary>What the status beside the search says: what the last package change came to, else what was found, or why
    /// a feed did not answer.</summary>
    public string Status => status.Text ?? string.Empty;

    public string Log => log.Text ?? string.Empty;

    public bool IsBusy => model.IsBusy;

    public void ShowPackages()
    {
        bool wasUpdates = showsUpdates || body.Content == Sources;
        showsUpdates = false;
        updateAll.IsVisible = false;
        Show(packages);
        if (wasUpdates)
        {
            asking?.Cancel();
            Hold(null);
            IdeErrors.Observe(RefreshAsync());
        }
    }

    /// <summary>The Sources tab, its files read again; the package list stays as it was until a feed changes.</summary>
    public void ShowSources()
    {
        showsUpdates = false;
        asking?.Cancel();
        updateAll.IsVisible = false;
        Show(Sources);
        IdeErrors.Observe(ReloadSourcesAsync(list: false));
    }

    /// <summary>Reads the folder's nuget.config files again (after one changed, here or on disk), keeping the feed chosen
    /// while it is still enabled, and lists again.</summary>
    public async Task ReloadSourcesAsync(bool list = true)
    {
        // Held to the list shown next, so a screenshot waits for it rather than for the sources alone.
        using IDisposable held = BackgroundWork.Scope();
        // Sources read with no folder open are never current, so listing then would read them again without end.
        if (await model.ReloadSourcesAsync() && list && model.IsCurrent)
        {
            await RefreshAsync();
        }
    }

    /// <summary>Refresh, as the person asks for it: the last change's words give way to the list's, and the sources are
    /// read again and listed.</summary>
    public Task RefreshAskedAsync()
    {
        Hold(null);
        return ReloadSourcesAsync();
    }

    /// <summary>Lists again: the installed packages with nothing typed, else the chosen feeds' search.</summary>
    public async Task RefreshAsync()
    {
        asking?.Cancel();
        CancellationTokenSource cancel = asking = new();
        string? chosen = Selected?.Id;
        // The feed's answer is the list shown next, so a screenshot waits for it rather than for "Searching".
        using IDisposable held = BackgroundWork.Scope();
        if (!model.IsCurrent && model.Given is null)
        {
            await ReloadSourcesAsync();
            return;
        }
        if (updatingAll)
        {
            return;
        }
        if (showsUpdates)
        {
            await CheckUpdatesAsync(cancel, chosen);
            return;
        }
        Dictionary<string, Dictionary<string, IReadOnlyList<InstalledVersion>>> installed = await model.InstalledAsync();
        Dictionary<string, Dictionary<string, IReadOnlyList<TransitiveUse>>> transitive = await model.TransitiveAsync();
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        IPackageFeed feed = Feed;
        try
        {
            IReadOnlyList<PackageRow> found;
            if (Query.Trim().Length == 0)
            {
                IEnumerable<PackageRow> referenced = installed.Select(p => new PackageRow(p.Key,
                    p.Value.Values.SelectMany(v => v).Select(v => v.Shown).FirstOrDefault(v => v is not null) ?? string.Empty, string.Empty, string.Empty, 0, p.Value));
                // A package some project references is listed as referenced, however the others get it.
                IEnumerable<PackageRow> brought = kinds == PackageKinds.Referenced ? [] : transitive.Where(p => !installed.ContainsKey(p.Key))
                    .Select(p => new PackageRow(p.Key, string.Join(", ", p.Value.Values.SelectMany(v => v).Select(v => v.Version).Distinct(StringComparer.OrdinalIgnoreCase)),
                        string.Empty, string.Empty, 0, new Dictionary<string, IReadOnlyList<InstalledVersion>>()));
                found = [.. referenced.Concat(brought).OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase).Select(r => Brought(r, transitive))];
            }
            else
            {
                Tell($"Searching {feed.Name}…");
                FeedSearch answer = feed is FeedSet set
                    ? await set.SearchEachAsync(Query.Trim(), prerelease.IsChecked == true, cancel.Token)
                    : new FeedSearch(await feed.SearchAsync(Query.Trim(), prerelease.IsChecked == true, cancel.Token), [], 0);
                found = [.. answer.Packages.Select(h => Brought(new PackageRow(h.Id, h.Version, h.Description, h.Authors, h.Downloads,
                    installed.TryGetValue(h.Id, out Dictionary<string, IReadOnlyList<InstalledVersion>>? have) ? have : new Dictionary<string, IReadOnlyList<InstalledVersion>>())
                { Feeds = h.Feeds }, transitive))];
                Tell(Said(answer, feed), answer.Failed.Count > 0 ? string.Join("\n", answer.Failed.Select(f => $"{f.Feed}: {f.Problem}")) : null);
            }
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            Task<VulnerabilityData>? auditing = found.Any(r => r.IsInstalled || r.IsTransitive) ? model.Audit() : null;
            VulnerabilityData? audited = auditing is { IsCompletedSuccessfully: true } ? auditing.Result : null;
            found = Marked(found, audited ?? VulnerabilityData.None);
            if (Query.Trim().Length == 0)
            {
                Tell(Counted(found, audited));
            }
            List(found, chosen);
            Task deprecating = DeprecatedAsync(found, cancel);
            if (auditing is { IsCompleted: false })
            {
                await MarkAsync(auditing, cancel);
            }
            await deprecating;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A newer search took its place.
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            if (!cancel.IsCancellationRequested)
            {
                Tell($"{feed.Name} did not answer: {failed.Message}");
            }
        }
    }

    /// <summary>Shows rows as the filters leave them, the package chosen before chosen again, else the first.</summary>
    private void List(IReadOnlyList<PackageRow> listed, string? chosen)
    {
        all = listed;
        rows = Filtered(listed);
        list.ItemsSource = rows;
        if (chosen is not null && rows.FirstOrDefault(r => r.Id.Equals(chosen, StringComparison.OrdinalIgnoreCase)) is { } again)
        {
            list.SelectedIndex = IndexOf(again);
        }
        else if (rows.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }

    /// <summary>The vulnerabilities as far as they are known: none until they are read, or when they could not be.</summary>
    private VulnerabilityData Known => model.Known;

    /// <summary>Marks the rows shown once the vulnerabilities are known, unless another list took their place.</summary>
    private async Task MarkAsync(Task<VulnerabilityData> auditing, CancellationTokenSource cancel)
    {
        VulnerabilityData known;
        try
        {
            known = await auditing;
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            known = VulnerabilityData.None;
        }
        if (cancel.IsCancellationRequested || asking != cancel)
        {
            return;
        }
        List(Marked(all, known), Selected?.Id);
        if (Query.Trim().Length == 0)
        {
            Tell(Counted(all, known));
        }
    }

    /// <summary>Rows with the advisories that affect the versions their projects have, referenced or brought in, and the
    /// newest of those versions deprecated, as far as known.</summary>
    private IReadOnlyList<PackageRow> Marked(IReadOnlyList<PackageRow> found, VulnerabilityData known) =>
        [.. found.Select(r => r with
        {
            Advisories = [.. r.AuditedVersions.SelectMany(v => known.Of(r.Id, v)).Distinct().OrderByDescending(a => a.Severity)],
            Deprecated = DeprecatedOf(r.Id, r.AuditedVersions),
        })];

    /// <summary>The newest of a package's versions its owner deprecated, with what they say of it, as far as known.</summary>
    private (string Version, Deprecation Said)? DeprecatedOf(string id, IEnumerable<string> versions) =>
        versions.OrderByDescending(v => v, VersionOrder.Instance).Select(v => (Version: v, Said: model.DeprecationOf(id, v))).FirstOrDefault(d => d.Said is not null)
            is { Version: { } version, Said: { } said } ? (version, said) : null;

    /// <summary>Reads, while the window is shown, whether the versions the rows' projects have are deprecated, and once they
    /// are known marks the rows the list holds, unless another list took their place or nothing changed.</summary>
    private async Task DeprecatedAsync(IReadOnlyList<PackageRow> found, CancellationTokenSource cancel)
    {
        if (!IsShown || Asked(found) is not { Length: > 0 } wanted || model.DeprecationsAsync(wanted) is not { IsCompleted: false } reading)
        {
            return;
        }
        await reading;
        if (cancel.IsCancellationRequested || asking != cancel)
        {
            return;
        }
        IReadOnlyList<PackageRow> held = showsUpdates ? updateRows : all;
        IReadOnlyList<PackageRow> marked = Marked(held, Known);
        if (marked.Select(r => r.Deprecated?.Version).SequenceEqual(held.Select(r => r.Deprecated?.Version)) && DeprecationsUnknown(held).Count == 0)
        {
            return;
        }
        if (showsUpdates)
        {
            updateRows = marked;
            FilterUpdates();
            return;
        }
        List(marked, Selected?.Id);
        if (Query.Trim().Length == 0)
        {
            Tell(Counted(all, Known));
        }
    }

    /// <summary>Each package and version a project has among the rows, referenced or brought in.</summary>
    private static (string Id, string Version)[] Asked(IReadOnlyList<PackageRow> found) => [.. found.SelectMany(r => r.AuditedVersions.Select(v => (r.Id, v)))];

    /// <summary>The feeds that did not say whether a version the rows' projects have is deprecated.</summary>
    private IReadOnlyList<string> DeprecationsUnknown(IReadOnlyList<PackageRow> found) => model.DeprecationsUnknown(Asked(found));

    /// <summary>A row with the projects that get its package through others, those referencing it aside.</summary>
    private static PackageRow Brought(PackageRow row, Dictionary<string, Dictionary<string, IReadOnlyList<TransitiveUse>>> transitive) =>
        transitive.TryGetValue(row.Id, out Dictionary<string, IReadOnlyList<TransitiveUse>>? byProject)
            ? row with { Transitive = byProject.Where(p => !row.Installed.ContainsKey(p.Key)).ToDictionary(p => p.Key, p => p.Value, Passenger.Core.PathNames.Comparer) }
            : row;

    /// <summary>The status over the installed packages: how many, how many of them are transitive, how many have a known
    /// vulnerability, how many a deprecated version, and the feeds that did not say.</summary>
    private string Counted(IReadOnlyList<PackageRow> found, VulnerabilityData? known)
    {
        string text = $"{Plural.Of(found.Count, "package")} installed";
        if (found.Count(r => r.IsTransitive) is int brought and > 0)
        {
            text += $", {brought} transitive";
        }
        if (found.Count(r => r.IsVulnerable) is int vulnerable and > 0)
        {
            text += $", {vulnerable} vulnerable";
        }
        if (found.Count(r => r.IsDeprecated) is int deprecated and > 0)
        {
            text += $", {deprecated} deprecated";
        }
        if (known?.Failed is { Count: > 0 } failed)
        {
            text += $"; vulnerabilities unknown: {string.Join(", ", failed.Select(f => f.Feed))} did not answer";
        }
        if (DeprecationsUnknown(found) is { Count: > 0 } unread)
        {
            text += $"; deprecations unknown: {string.Join(", ", unread)} did not answer";
        }
        return text;
    }

    /// <summary>The status after a search: the feeds that did not answer, else what was found.</summary>
    private static string Said(FeedSearch answer, IPackageFeed feed)
    {
        List<string> said = [];
        if (answer.Failed.Count > 0)
        {
            string names = string.Join(", ", answer.Failed.Select(f => f.Feed));
            said.Add($"Unable to fetch packages: {names} {Plural.Agreeing(answer.Failed.Count, "is", "are")} invalid{(answer.Failed.Count == 1 ? $" ({answer.Failed[0].Problem})" : string.Empty)}");
        }
        else if (answer.Packages.Count == 0)
        {
            said.Add(feed.Name == FeedSet.All ? "Nothing found" : $"Nothing found in {feed.Name}");
        }
        if (answer.Unmapped > 0)
        {
            said.Add($"{Plural.Of(answer.Unmapped, "package")} left out by package source mapping");
        }
        return string.Join("; ", said);
    }

    /// <summary>Adds the chosen package to a project at the chosen version, or moves it there.</summary>
    public async Task<bool> InstallAsync(ProjectModel project) =>
        Selected is { } row && (ChosenVersion ?? row.Version) is { Length: > 0 } version
        && (BeforeInstall is null || await BeforeInstall(project, row.Id, version, null, CancellationToken.None))
        && Said(await model.InstallAsync(project, row.Id, version));

    /// <summary>Asked before the window's own Install or Update of a package in a project, with its id and version: whether
    /// it goes on as asked (false when the person cancelled, or it was made another way, as EF Core's packages moved
    /// together).</summary>
    public Func<ProjectModel, string, string, Passenger.App.Editors.CommandHistory.Recording?, CancellationToken, Task<bool>>? BeforeInstall { get; set; }

    /// <summary>Takes the chosen package out of a project (Remove).</summary>
    public async Task<bool> RemoveAsync(ProjectModel project) => Selected is { } row && Said(await model.RemoveAsync(project, row.Id));

    /// <summary>Whether a change the window asked for was done, its status saying why when it was refused (another change
    /// under way, which no change's end then tells).</summary>
    private bool Said(PackageOutcome ended)
    {
        if (ended.Refused is { } why)
        {
            Hold(why);
        }
        return ended.Done;
    }

    /// <summary>Reads the sources the first time the window is shown, or lists again once they are read.</summary>
    private Task Shown() => !model.IsCurrent && model.Given is null ? ReloadSourcesAsync() : RefreshAsync();

    /// <summary>Sources just read: the chooser's choices, keeping its choice while it is still there, and the Sources tab.</summary>
    private void OnSourcesChanged(object? sender, EventArgs e)
    {
        PackageSourceSet loaded = model.Sources;
        string was = ChosenFeed;
        choosing = true;
        try
        {
            chooser.ItemsSource = new[] { FeedSet.All }.Concat(loaded.Enabled.Select(f => f.Name)).ToList();
            chooser.SelectedItem = FeedChoices.FirstOrDefault(c => c.Equals(was, StringComparison.OrdinalIgnoreCase)) ?? FeedSet.All;
        }
        finally
        {
            choosing = false;
        }
        Sources.Show(loaded);
    }

    private void Show(Control content)
    {
        body.Content = content;
        tabs.Children.Clear();
        ToggleButton packagesTab = Tab("Packages", packages);
        packagesTab.IsChecked = body.Content == packages && !showsUpdates;
        tabs.Children.Add(packagesTab);
        ToggleButton updatesTab = new() { Classes = { StyleClasses.PanelTab }, IsChecked = showsUpdates, Content = "Updates" };
        updatesTab.Click += (_, _) => ShowUpdates();
        tabs.Children.Add(updatesTab);
        tabs.Children.Add(Tab("Sources", Sources));
    }

    private ToggleButton Tab(string title, Control content)
    {
        ToggleButton tab = new() { Classes = { StyleClasses.PanelTab }, IsChecked = body.Content == content, Content = title };
        tab.Click += (_, _) =>
        {
            if (content == Sources)
            {
                ShowSources();
            }
            else
            {
                ShowPackages();
            }
        };
        return tab;
    }

    private int IndexOf(PackageRow row)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i], row))
            {
                return i;
            }
        }
        return -1;
    }

    private async Task ShowAsync()
    {
        // Only the newest choice is shown: an earlier one's versions arriving after it would show its details again.
        showing?.Cancel();
        CancellationTokenSource cancel = showing = new();
        details.Children.Clear();
        versions = [];
        if (Selected is not { } row)
        {
            return;
        }
        IReadOnlyList<string> found;
        using IDisposable held = BackgroundWork.Scope();
        try
        {
            found = showsUpdates ? row.AvailableVersions : await Feed.VersionsAsync(row.Id, prerelease.IsChecked == true, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return;
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            found = row.Version.Length > 0 ? [row.Version] : [];
        }
        if (cancel.IsCancellationRequested)
        {
            return;
        }
        versions = found;
        VulnerabilityData known = Known;
        string? vulnerable = row.InstalledVersions.Where(v => known.Of(row.Id, v).Count > 0).OrderByDescending(v => v, VersionOrder.Instance).FirstOrDefault();
        string? fix = vulnerable is null ? null : known.FixedVersion(row.Id, vulnerable, versions);
        ChosenVersion = row.UpdateVersion ?? fix ?? versions.FirstOrDefault() ?? row.Version;
        ComboBox picker = new() { Name = "Versions", ItemsSource = versions, SelectedItem = ChosenVersion, MinWidth = 140 };
        StackPanel vulnerabilities = new() { Spacing = 8 };
        StackPanel deprecated = new() { Spacing = 8 };
        picker.SelectionChanged += (_, _) =>
        {
            ChosenVersion = picker.SelectedItem as string;
            ShowVulnerable(row, vulnerabilities);
            ShowDeprecated(row, deprecated);
            ShowProjects(row);
            IdeErrors.Observe(ChosenVulnerabilityAsync(row, vulnerabilities, cancel));
            IdeErrors.Observe(ChosenDeprecationAsync(row, deprecated, cancel));
        };
        details.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { new TextBlock { Text = row.Id, FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, picker },
        });
        if (row.Description.Length > 0)
        {
            details.Children.Add(new TextBlock { Text = row.Description, TextWrapping = TextWrapping.Wrap });
        }
        if (row.Authors.Length > 0 || row.Downloads > 0)
        {
            TextBlock by = new() { Text = row.Downloads > 0 ? $"{row.Authors}, {row.Downloads:N0} downloads" : row.Authors, FontSize = 12 };
            by.Classes.Add(StyleClasses.Muted);
            details.Children.Add(by);
        }
        if (row.Feeds.Count > 0)
        {
            TextBlock from = new() { Text = $"In {string.Join(", ", row.Feeds)}", FontSize = 12 };
            from.Classes.Add(StyleClasses.Muted);
            details.Children.Add(from);
        }
        details.Children.Add(vulnerabilities);
        ShowVulnerable(row, vulnerabilities);
        details.Children.Add(deprecated);
        ShowDeprecated(row, deprecated);
        details.Children.Add(new StackPanel { Name = "projects", Spacing = 4, Margin = new Thickness(0, 8, 0, 0) });
        ShowProjects(row);
        await Task.WhenAll(ChosenVulnerabilityAsync(row, vulnerabilities, cancel), ChosenDeprecationAsync(row, deprecated, cancel));
    }

    /// <summary>Reads, while the window is shown, the advisories the audit's feeds know, so the version chosen in the details
    /// is said vulnerable once they are, unless another package or version was chosen meanwhile.</summary>
    private async Task ChosenVulnerabilityAsync(PackageRow row, Panel said, CancellationTokenSource cancel)
    {
        if (!IsShown || ChosenVersion is not { Length: > 0 } chosen || model.Audit() is not { IsCompleted: false } reading)
        {
            return;
        }
        try
        {
            await reading;
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            // The audit unread, no version is said vulnerable.
            return;
        }
        if (!cancel.IsCancellationRequested && Selected == row && ChosenVersion == chosen)
        {
            ShowVulnerable(row, said);
        }
    }

    /// <summary>The details' word on vulnerable versions: the newest a project has, and the version chosen to install or
    /// move to, one word for both where the same advisories affect each.</summary>
    private void ShowVulnerable(PackageRow row, Panel said)
    {
        VulnerabilityData known = Known;
        List<(List<string> Versions, IReadOnlyList<Advisory> Advisories)> told = [];
        if (row.InstalledVersions.Where(v => known.Of(row.Id, v).Count > 0).OrderByDescending(v => v, VersionOrder.Instance).FirstOrDefault() is { } held)
        {
            told.Add(([held], known.Of(row.Id, held)));
        }
        if (ChosenVersion is { } chosen && known.Of(row.Id, chosen) is { Count: > 0 } advisories
            && !told.Any(t => t.Versions.Contains(chosen, StringComparer.OrdinalIgnoreCase)))
        {
            if (told.FirstOrDefault(t => t.Advisories.SequenceEqual(advisories)).Versions is { } same)
            {
                same.Add(chosen);
            }
            else
            {
                told.Add(([chosen], advisories));
            }
        }
        said.Children.Clear();
        for (int i = 0; i < told.Count; i++)
        {
            // Versions the same advisories affect have the same least version above them that none affects.
            string newest = told[i].Versions.OrderByDescending(v => v, VersionOrder.Instance).First();
            said.Children.Add(Vulnerability(told[i].Advisories, told[i].Versions, known.FixedVersion(row.Id, newest, versions), first: i == 0));
        }
        said.IsVisible = told.Count > 0;
    }

    /// <summary>Reads, while the window is shown, whether the version chosen in the details is deprecated, and says it once
    /// known, unless another package or version was chosen meanwhile.</summary>
    private async Task ChosenDeprecationAsync(PackageRow row, Panel said, CancellationTokenSource cancel)
    {
        if (!IsShown || ChosenVersion is not { Length: > 0 } chosen || model.DeprecationsAsync([(row.Id, chosen)]) is not { IsCompleted: false } reading)
        {
            return;
        }
        await reading;
        if (!cancel.IsCancellationRequested && Selected == row && ChosenVersion == chosen)
        {
            ShowDeprecated(row, said);
        }
    }

    /// <summary>The details' word on deprecated versions: the newest a project has, and the version chosen to install or
    /// move to, one word for both where their owner says the same of each.</summary>
    private void ShowDeprecated(PackageRow row, Panel said)
    {
        List<(List<string> Versions, Deprecation Said)> told = [];
        if (row.Deprecated is { } held)
        {
            told.Add(([held.Version], held.Said));
        }
        if (ChosenVersion is { } chosen && model.DeprecationOf(row.Id, chosen) is { } deprecation
            && !told.Any(t => t.Versions.Contains(chosen, StringComparer.OrdinalIgnoreCase)))
        {
            if (told.FirstOrDefault(t => Alike(t.Said, deprecation)).Versions is { } same)
            {
                same.Add(chosen);
            }
            else
            {
                told.Add(([chosen], deprecation));
            }
        }
        said.Children.Clear();
        for (int i = 0; i < told.Count; i++)
        {
            said.Children.Add(Deprecated(told[i].Versions, told[i].Said, first: i == 0));
        }
        said.IsVisible = told.Count > 0;

        static bool Alike(Deprecation one, Deprecation other) => one.Why == other.Why && one.Message == other.Message && one.Instead == other.Instead;
    }

    /// <summary>The word on vulnerable versions, their advisories each with its severity, the gravest first, and the
    /// version that fixes them, which the version picker and Update offer; the first of the details' named for a test to
    /// find.</summary>
    private Border Vulnerability(IReadOnlyList<Advisory> advisories, IReadOnlyList<string> versions, string? fix, bool first)
    {
        PathIcon warning = new() { Width = 14, Height = 14, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, [DockProperty] = Dock.Left };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable(IconKeys.Problems));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable(TokenKeys.Warning));
        StackPanel said = new() { Spacing = 4 };
        said.Children.Add(new TextBlock
        {
            Text = $"Known security vulnerability in {Plural.Listed(versions)}. It could be exploited in certain scenarios. See the advisories below for details.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (Advisory advisory in advisories)
        {
            Control link = new TextBlock { Text = advisory.Address, VerticalAlignment = VerticalAlignment.Center };
            if (advisory.Link is { } page)
            {
                Button button = new() { Classes = { StyleClasses.Link }, Content = advisory.Address, VerticalAlignment = VerticalAlignment.Center };
                button.Click += (_, _) => LinkClicked?.Invoke(this, page);
                link = button;
            }
            said.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "Advisory:", VerticalAlignment = VerticalAlignment.Center },
                    link,
                    new TextBlock { Text = $"(Severity: {advisory.Severity})", VerticalAlignment = VerticalAlignment.Center },
                },
            });
        }
        said.Children.Add(new TextBlock { Text = fix is null ? "No version without a known vulnerability is offered" : $"Fixed in {fix}", FontWeight = FontWeight.SemiBold });
        Border block = new()
        {
            Name = first ? "Vulnerability" : "ChosenVulnerability",
            Classes = { StyleClasses.Box },
            Padding = new Thickness(10, 8),
            Child = new DockPanel { Children = { warning, said } },
        };
        block.Bind(Border.BackgroundProperty, block.GetResourceObservable(TokenKeys.Banner));
        return block;
    }

    /// <summary>The word on deprecated versions: why, their owner's own words, and the package they name to use instead,
    /// which a click searches the feeds for; the first of the details' named for a test to find.</summary>
    private Border Deprecated(IReadOnlyList<string> versions, Deprecation deprecation, bool first)
    {
        PathIcon warning = new() { Width = 14, Height = 14, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, [DockProperty] = Dock.Left };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable(IconKeys.Problems));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable(TokenKeys.Warning));
        StackPanel said = new() { Spacing = 4 };
        said.Children.Add(new TextBlock { Text = $"{Plural.Listed(versions)} {Plural.Agreeing(versions.Count, "is", "are")} deprecated: {deprecation.Why}.", TextWrapping = TextWrapping.Wrap });
        if (deprecation.Message.Length > 0)
        {
            said.Children.Add(new TextBlock { Text = deprecation.Message, TextWrapping = TextWrapping.Wrap });
        }
        if (deprecation.Alternative is { } alternative)
        {
            Button search = new() { Name = first ? "Alternative" : "ChosenAlternative", Classes = { StyleClasses.Link }, Content = alternative, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(search, $"Search for {alternative}");
            search.Click += (_, _) => SearchFor(alternative);
            StackPanel instead = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { new TextBlock { Text = "Use instead:", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, search },
            };
            if (deprecation.AlternativeVersions is { } range)
            {
                instead.Children.Add(new TextBlock { Text = range, VerticalAlignment = VerticalAlignment.Center });
            }
            said.Children.Add(instead);
        }
        Border block = new()
        {
            Name = first ? "Deprecation" : "ChosenDeprecation",
            Classes = { StyleClasses.Box },
            Padding = new Thickness(10, 8),
            Child = new DockPanel { Children = { warning, said } },
        };
        block.Bind(Border.BackgroundProperty, block.GetResourceObservable(TokenKeys.Banner));
        return block;
    }

    /// <summary>The feeds searched for a package, in the Packages tab.</summary>
    private void SearchFor(string id)
    {
        if (showsUpdates || body.Content != packages)
        {
            ShowPackages();
        }
        Query = id;
    }

    /// <summary>The chosen package's line per project as the pane's filters leave them: its version (a version brought in
    /// by another package or project tagged so), a mark where it is vulnerable, a tag where it is deprecated, and Install,
    /// Update, Downgrade or Remove.</summary>
    private void ShowProjects(PackageRow row)
    {
        if (ProjectsPanel is not { } panel)
        {
            return;
        }
        projectsOf = row;
        panel.Children.Clear();
        // The one header goes with the chosen package's projects, from the last package's.
        (projectsHeader.Parent as Panel)?.Children.Remove(projectsHeader);
        panel.Children.Add(projectsHeader);
        IReadOnlyList<(ProjectModel Project, IReadOnlyList<InstalledVersion> At, IReadOnlyList<TransitiveUse> Brought)> listed = ProjectsListed(row, out int all);
        projectCount.Text = listed.Count == all ? string.Empty : $"{listed.Count} of {all}";
        foreach ((ProjectModel project, IReadOnlyList<InstalledVersion> at, IReadOnlyList<TransitiveUse> brought) in listed)
        {
            bool has = at.Count > 0;
            InstalledVersion? vulnerable = at.FirstOrDefault(v => v.Version is { } known && Known.Of(row.Id, known).Count > 0);
            TransitiveUse? broughtVulnerable = has ? null : brought.FirstOrDefault(b => Known.Of(row.Id, b.Version).Count > 0);
            (string Shown, string Told)? through = !has && brought.Count > 0 ? Brought(brought, ChosenVersion) : null;
            TextBlock version = new() { Text = through?.Shown ?? Shown(at), Width = 110, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(version, through?.Told ?? Told(at));
            version.Bind(TextBlock.ForegroundProperty, version.GetResourceObservable(vulnerable is not null || broughtVulnerable is not null ? TokenKeys.Warning : TokenKeys.TextMuted));
            // A slot of its own, so the buttons stand in one column whichever lines have the mark.
            Panel mark = new() { Width = 14, VerticalAlignment = VerticalAlignment.Center };
            if (vulnerable is { Version: { } affected })
            {
                Advisory gravest = Known.Of(row.Id, affected)[0];
                string where = at.Count > 1 ? $" for {vulnerable.Framework}" : string.Empty;
                mark.Children.Add(Warning($"Known security vulnerability in {affected}{where}: {gravest.Severity} severity ({gravest.Address})"));
            }
            else if (broughtVulnerable is { } bad)
            {
                Advisory gravest = Known.Of(row.Id, bad.Version)[0];
                mark.Children.Add(Warning($"Known security vulnerability in {bad.Version}, brought in by {string.Join(", ", bad.BroughtBy)}: {gravest.Severity} severity ({gravest.Address})"));
            }
            TextBlock name = new() { Text = project.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolFlow.SetFills(name, new ToolLayout.Fills(100, 220));
            ToolFlow line = new()
            {
                Spacing = 8,
                Children = { name, version, mark },
            };
            if (!has)
            {
                line.Children.Add(Action("Install", () => InstallAsync(project)));
            }
            else
            {
                if (ChosenVersion is { } chosen && InstalledVersion.Move(chosen, at) is PackageMove move and not PackageMove.None)
                {
                    Button moving = Action(Moved(move, chosen, at), () => InstallAsync(project));
                    ToolTip.SetTip(moving, move == PackageMove.UpdateAndDowngrade
                        ? $"Moves every framework to {chosen}: " + string.Join(", ", at.Where(v => v.Version is not null).Select(v => $"{v.Framework} from {v.Version}"))
                        : null);
                    line.Children.Add(moving);
                }
                line.Children.Add(Action("Remove", () => RemoveAsync(project)));
            }
            // After the buttons, so they stand in one column whichever lines have the tags.
            if (through is not null)
            {
                Border tag = Tagged("transitive");
                tag.Margin = new Thickness(0);
                ToolTip.SetTip(tag, through.Value.Told);
                line.Children.Add(tag);
            }
            if (DeprecatedOf(row.Id, has ? at.Select(v => v.Version).OfType<string>() : brought.Select(b => b.Version)) is { } deprecated)
            {
                Border tag = Tagged("deprecated");
                tag.Margin = new Thickness(0);
                ToolTip.SetTip(tag, DeprecationTip(deprecated.Version, deprecated.Said));
                line.Children.Add(tag);
            }
            panel.Children.Add(line);
        }
    }

    /// <summary>What a project's button for the chosen version says: Update or Downgrade, a downgrade naming the frameworks
    /// that go down where another does not, as every framework's reference moves together.</summary>
    private static string Moved(PackageMove move, string version, IReadOnlyList<InstalledVersion> at)
    {
        IReadOnlyList<InstalledVersion> lowered = InstalledVersion.Lowered(version, at);
        return move == PackageMove.Update || (move == PackageMove.Downgrade && lowered.Count == at.Count)
            ? move.ToString()
            : $"Downgrade {string.Join(", ", lowered.Select(v => v.Framework ?? "it"))}";
    }

    /// <summary>A project's versions of a package as its line says them: each once, in its frameworks' order.</summary>
    private static string Shown(IReadOnlyList<InstalledVersion> at) =>
        string.Join(", ", at.Select(v => v.Shown ?? "installed").Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>What a project's line tells of its versions on hover: each framework's, and the range the project gives
    /// where it shows another version; null where the line says it all.</summary>
    private static string? Told(IReadOnlyList<InstalledVersion> at)
    {
        bool ranged = at.Any(v => v.Given is { } given && given != v.Shown);
        return at.Count > 1 || ranged
            ? string.Join("\n", at.Select(v => (at.Count > 1 ? $"{v.Framework}: " : string.Empty) + (v.Shown ?? "installed")
                + (v.Given is { } given && given != v.Shown ? $", given as {given}" : string.Empty)))
            : null;
    }

    private Button Action(string label, Func<Task<bool>> run)
    {
        Button button = new() { Content = label, IsEnabled = !model.IsBusy };
        button.Click += (_, _) => IdeErrors.Observe(run());
        return button;
    }

    /// <summary>A package's row: its icon, its id (strong where a project references it), its version muted, a tag on one
    /// projects get only through others and on one a project has a deprecated version of, its description or what brings
    /// it in on hover, and a warning saying the gravest advisory where a version a project has is vulnerable.</summary>
    private Control Row()
    {
        TextBlock version = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        version.Classes.Add(StyleClasses.Muted);
        RowIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(6, 0, 6, 0) };
        icon.DataKey = IconKeys.Package;
        TextBlock id = new() { VerticalAlignment = VerticalAlignment.Center };
        Border transitive = Tagged("transitive");
        Border deprecated = Tagged("deprecated");
        RowIcon warning = Warning(string.Empty);
        warning.Margin = new Thickness(6, 0, 0, 0);
        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal,
            Height = ListRow.Height,
            Children = { icon, id, version, transitive, deprecated, warning },
        };
        return line.Showing<PackageRow>(row =>
        {
            id.Text = row.Id;
            id.FontWeight = row.IsInstalled ? FontWeight.SemiBold : FontWeight.Normal;
            version.Text = row.UpdateVersion is { } latest ? $"{row.Version} → {latest}" : row.Version;
            transitive.IsVisible = row.IsTransitive;
            deprecated.IsVisible = row.IsDeprecated;
            ToolTip.SetTip(deprecated, row.Deprecated is { } said ? DeprecationTip(said.Version, said.Said) : null);
            ToolTip.SetTip(line, row.IsTransitive ? $"Brought in by {BroughtBy(row.Transitive.Values.SelectMany(v => v))}"
                : row.Description.Length > 0 ? row.Description : null);
            warning.IsVisible = row.IsVulnerable;
            ToolTip.SetTip(warning, row.IsVulnerable ? $"Known security vulnerability: {row.Advisories[0].Severity} severity ({row.Advisories[0].Address})" : null);
        });
    }

    /// <summary>A small tag after a row's words ("transitive").</summary>
    private static Border Tagged(string words)
    {
        TextBlock text = new() { Text = words, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        text.Classes.Add(StyleClasses.Muted);
        return new Border { Classes = { StyleClasses.Tag }, Padding = new Thickness(4, 0), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Child = text };
    }

    /// <summary>What a deprecated version's tag tells on hover: why, what to use instead, and its owner's own words.</summary>
    private static string DeprecationTip(string version, Deprecation deprecation) =>
        $"{version} is deprecated: {deprecation.Why}" + (deprecation.Instead is { } instead ? $"\nUse {instead} instead" : string.Empty)
            + (deprecation.Message.Length > 0 ? $"\n{deprecation.Message}" : string.Empty);

    /// <summary>What brings a package in, each once, in order.</summary>
    private static string BroughtBy(IEnumerable<TransitiveUse> uses) =>
        string.Join(", ", uses.SelectMany(u => u.BroughtBy).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>The warning a vulnerable version is marked with, saying what it is.</summary>
    private static RowIcon Warning(string tip)
    {
        RowIcon warning = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        warning.DataKey = IconKeys.Problems;
        warning.ForegroundKey = TokenKeys.Warning;
        ToolTip.SetTip(warning, tip);
        return warning;
    }
}
