using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.App.Packages;
using Passenger.App.Workspace;
using Passenger.Editor;

namespace Passenger.App.ToolWindows;

/// <summary>A package as the NuGet window lists it: what the feeds say of it, the feeds that have it, the versions
/// the solution's projects have of it, and the known vulnerabilities of those versions.</summary>
public sealed record PackageRow(string Id, string Version, string Description, string Authors, long Downloads, IReadOnlyDictionary<string, string?> Installed)
{
    public bool IsInstalled => Installed.Count > 0;

    public IReadOnlyList<string> Feeds { get; init; } = [];

    /// <summary>The advisories that affect a version a project has, the gravest first.</summary>
    public IReadOnlyList<Advisory> Advisories { get; init; } = [];

    public bool IsVulnerable => Advisories.Count > 0;
}

/// <summary>Rider's NuGet window, its Packages and Sources tabs in its header. Packages: the projects' packages with nothing
/// typed, else what the feed chosen beside the query finds; the chosen package's versions and a row per project to Install,
/// Update or Remove it through the .NET command line, whose restore uses those feeds and their credentials. A version with
/// a known vulnerability (the audit's VulnerabilityInfo, as restore reads it) is marked with its advisories, and Update
/// offers the least version that fixes it. Sources: the files and their feeds (<see cref="NuGetSourcesView"/>).</summary>
public sealed class NuGetView : DockPanel
{
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(300);

    private readonly Func<IReadOnlyList<ProjectModel>> projects;
    private readonly Func<string?> root;
    private readonly PackageManager manager = new();
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
    private PackageSourceSet sources = PackageSourceSet.None;
    private Dictionary<string, IPackageFeed> feeds = new(StringComparer.OrdinalIgnoreCase);
    private IPackageFeed? given;
    private Task? reading;

    /// <summary>The audit's known vulnerabilities, read once the sources are and a listed package is installed.</summary>
    private Task<VulnerabilityData>? audit;
    private bool choosing;
    private bool busy;

    /// <param name="projects">The workspace's projects, whose packages the window lists and changes.</param>
    /// <param name="root">The folder NuGet's configuration is read from: the solution's, else the one open.</param>
    public NuGetView(Func<IReadOnlyList<ProjectModel>> projects, Func<string?> root)
    {
        this.projects = projects;
        this.root = root;
        search = new TextBox { Classes = { "search" }, PlaceholderText = "Search packages", Width = 260, Margin = new Thickness(8, 0) };
        chooser = new ComboBox { Classes = { "compact" }, Width = 180, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        ToolTip.SetTip(chooser, "The feeds searched");
        chooser.SelectionChanged += (_, _) =>
        {
            ToolTip.SetTip(chooser, chooser.SelectedItem ?? "The feeds searched");
            if (!choosing)
            {
                _ = RefreshAsync();
            }
        };
        prerelease = new CheckBox { Content = "Prerelease", VerticalAlignment = VerticalAlignment.Center };
        status = new TextBlock { Name = "NuGetPackageStatus", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        status.Bind(TextBlock.ForegroundProperty, status.GetResourceObservable("Passenger.TextMuted"));
        status.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty)
            {
                ToolTip.SetTip(status, status.Text);
            }
        };
        Button refresh = new() { Classes = { "tool" }, Content = new PathIcon(), Margin = new Thickness(6, 0, 0, 0) };
        ((PathIcon)refresh.Content).Bind(PathIcon.DataProperty, refresh.GetResourceObservable("Icon.Rerun"));
        ToolTip.SetTip(refresh, "Refresh");
        refresh.Click += (_, _) => _ = ReloadSourcesAsync();
        typing = new Debounce(Pause, RefreshAsync);
        search.TextChanged += (_, _) => typing.Restart();
        prerelease.IsCheckedChanged += (_, _) => _ = RefreshAsync();
        Border top = new()
        {
            [DockProperty] = Dock.Top,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new WrapPanel { Name = "NuGetPackageTools", Margin = new Thickness(0, 4), Children = { search, chooser, prerelease, refresh, status } },
        };
        top.SizeChanged += (_, change) =>
        {
            search.Width = Math.Min(260, Math.Max(0, change.NewSize.Width - search.Margin.Left - search.Margin.Right));
            chooser.Width = Math.Min(180, Math.Max(0, change.NewSize.Width - chooser.Margin.Left - chooser.Margin.Right));
            status.MaxWidth = Math.Max(0, change.NewSize.Width - status.Margin.Left - status.Margin.Right);
        };
        top.Bind(Border.BorderBrushProperty, top.GetResourceObservable("Passenger.Divider"));

        list = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<PackageRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => _ = ShowAsync();
        details = new StackPanel { Spacing = 8, Margin = new Thickness(14, 10) };
        log = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = CSharp.Fonts.Mono,
            FontSize = 11,
            Height = 90,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = Brushes.Transparent,
            IsVisible = false,
            [DockProperty] = Dock.Bottom,
        };
        log.Bind(TextBox.BorderBrushProperty, log.GetResourceObservable("Passenger.Divider"));
        manager.Line += (_, line) =>
        {
            log.IsVisible = true;
            log.Text = $"{log.Text}{line}\n";
            log.CaretIndex = log.Text.Length;
        };
        DockPanel right = new();
        right.Children.Add(log);
        right.Children.Add(new ScrollViewer { Content = details });
        Grid split = new() { ColumnDefinitions = new ColumnDefinitions("2*,1,3*") };
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        Grid.SetColumn(right, 2);
        split.Children.Add(list);
        split.Children.Add(divider);
        split.Children.Add(right);
        packages.Children.Add(top);
        packages.Children.Add(split);

        Sources = new NuGetSourcesView(this);
        Children.Add(body);
        ShowPackages();
        AttachedToVisualTree += (_, _) =>
        {
            _ = Shown();
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        };
    }

    /// <summary>Raised after a project's packages changed, so the solution is read again.</summary>
    public event EventHandler? Changed;

    /// <summary>An advisory's link clicked, for the browser.</summary>
    public event EventHandler<Uri>? LinkClicked;

    /// <summary>The Packages and Sources tabs, for the tool window's header.</summary>
    public Control Tabs => tabs;

    /// <summary>The Sources tab: the nuget.config files in effect and their feeds.</summary>
    public NuGetSourcesView Sources { get; }

    /// <summary>Whether the Sources tab is the one shown.</summary>
    public bool ShowsSources => body.Content == Sources;

    /// <summary>The folder's package sources as last read.</summary>
    public PackageSourceSet PackageSources => sources;

    /// <summary>What the feed chooser offers: All Feeds, then each enabled feed by name.</summary>
    public IReadOnlyList<string> FeedChoices => [.. chooser.Items.OfType<string>()];

    /// <summary>The chooser's choice: All Feeds or a feed's name.</summary>
    public string ChosenFeed
    {
        get => chooser.SelectedItem as string ?? FeedSet.All;
        set => chooser.SelectedItem = FeedChoices.FirstOrDefault(c => c.Equals(value, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"The NuGet window has no feed {value}; it offers {string.Join(", ", FeedChoices)}");
    }

    /// <summary>Where packages are searched: the chooser's feeds, unless a test sets a feed of its own.</summary>
    public IPackageFeed Feed
    {
        get => given ?? Chosen(ChosenFeed);
        set => given = value;
    }

    /// <summary>A feed by its name, or All Feeds when none is given, once the folder's sources are read: an agent may ask
    /// before the window was ever shown.</summary>
    public async Task<IPackageFeed> FeedAsync(string? name)
    {
        if (given is not null)
        {
            return given;
        }
        if (sources == PackageSourceSet.None || sources.Root != (root() ?? string.Empty))
        {
            await ReloadSourcesAsync();
        }
        return Chosen(name ?? FeedSet.All);
    }

    /// <summary>A feed by its name in the folder's configuration, or All Feeds.</summary>
    public IPackageFeed Chosen(string name)
    {
        IPackageFeed[] enabled = [.. sources.Enabled.Select(f => feeds[f.Name])];
        if (name.Equals(FeedSet.All, StringComparison.OrdinalIgnoreCase))
        {
            return new FeedSet(FeedSet.All, enabled, sources.Allows);
        }
        return enabled.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } one
            ? new FeedSet(one.Name, [one], sources.Allows)
            : throw new ArgumentException($"No enabled feed is named {name}; the folder's nuget.config files enable {string.Join(", ", enabled.Select(f => f.Name))}");
    }

    public IReadOnlyList<PackageRow> Rows => rows;

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

    /// <summary>What the status beside the search says: what was found, or why a feed did not answer.</summary>
    public string Status => status.Text ?? string.Empty;

    public string Log => log.Text ?? string.Empty;

    public bool IsBusy => busy;

    public void ShowPackages() => Show(packages);

    /// <summary>The Sources tab, its files read again; the package list stays as it was until a feed changes.</summary>
    public void ShowSources()
    {
        Show(Sources);
        _ = ReloadSourcesAsync(list: false);
    }

    /// <summary>Reads the folder's nuget.config files again (after one changed, here or on disk), keeping the feed chosen
    /// while it is still enabled, and lists again.</summary>
    public async Task ReloadSourcesAsync(bool list = true)
    {
        string folder = root() ?? string.Empty;
        using IDisposable held = BackgroundWork.Scope();
        Task<PackageSourceSet> read = BackgroundWork.Run(() => folder.Length == 0 ? PackageSourceSet.None : PackageSourceSet.Load(folder));
        reading = read;
        PackageSourceSet loaded = await read;
        if (reading != read)
        {
            return;
        }
        Take(loaded);
        if (list)
        {
            await RefreshAsync();
        }
    }

    /// <summary>Lists again: the installed packages with nothing typed, else the chosen feeds' search.</summary>
    public async Task RefreshAsync()
    {
        asking?.Cancel();
        CancellationTokenSource cancel = asking = new();
        string? chosen = Selected?.Id;
        Dictionary<string, Dictionary<string, string?>> installed = Installed();
        // The feed's answer is the list shown next, so a screenshot waits for it rather than for "Searching".
        using IDisposable held = BackgroundWork.Scope();
        if (sources.Root != (root() ?? string.Empty) && given is null)
        {
            await ReloadSourcesAsync();
            return;
        }
        IPackageFeed feed = Feed;
        try
        {
            IReadOnlyList<PackageRow> found;
            ToolTip.SetTip(status, status.Text);
            if (Query.Trim().Length == 0)
            {
                found = [.. installed.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PackageRow(p.Key, p.Value.Values.FirstOrDefault(v => v is not null) ?? string.Empty, string.Empty, string.Empty, 0, p.Value))];
                status.Text = Counted(found, null);
            }
            else
            {
                status.Text = $"Searching {feed.Name}...";
                FeedSearch answer = feed is FeedSet set
                    ? await set.SearchEachAsync(Query.Trim(), prerelease.IsChecked == true, cancel.Token)
                    : new FeedSearch(await feed.SearchAsync(Query.Trim(), prerelease.IsChecked == true, cancel.Token), [], 0);
                found = [.. answer.Packages.Select(h => new PackageRow(h.Id, h.Version, h.Description, h.Authors, h.Downloads,
                    installed.TryGetValue(h.Id, out Dictionary<string, string?>? have) ? have : new Dictionary<string, string?>()) { Feeds = h.Feeds })];
                status.Text = Said(answer, feed);
                if (answer.Failed.Count > 0)
                {
                    ToolTip.SetTip(status, string.Join("\n", answer.Failed.Select(f => $"{f.Feed}: {f.Problem}")));
                }
            }
            if (cancel.IsCancellationRequested)
            {
                return;
            }
            Task<VulnerabilityData>? auditing = found.Any(r => r.IsInstalled) ? Audit() : null;
            if (auditing is { IsCompletedSuccessfully: true })
            {
                found = Marked(found, auditing.Result);
                if (Query.Trim().Length == 0)
                {
                    status.Text = Counted(found, auditing.Result);
                }
            }
            List(found, chosen);
            if (auditing is { IsCompleted: false })
            {
                await MarkAsync(auditing, cancel);
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A newer search took its place.
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            if (!cancel.IsCancellationRequested)
            {
                status.Text = $"{feed.Name} did not answer: {failed.Message}";
            }
        }
    }

    /// <summary>Shows rows, the package chosen before chosen again, else the first.</summary>
    private void List(IReadOnlyList<PackageRow> found, string? chosen)
    {
        rows = found;
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

    /// <summary>The known vulnerabilities, read from the audit's feeds the first time a listed package is installed after
    /// the sources were read.</summary>
    private Task<VulnerabilityData> Audit()
    {
        IReadOnlyList<FeedSource> audited = sources.Audited;
        return audit ??= BackgroundWork.Run(() => VulnerabilityData.ReadAsync(audited, CancellationToken.None));
    }

    /// <summary>The known vulnerabilities of the folder's audit feeds, read once its sources are (an agent or a project
    /// file's code help may ask before the window was ever shown); none when no feed could say.</summary>
    public async Task<VulnerabilityData> VulnerabilitiesAsync()
    {
        if (sources == PackageSourceSet.None || sources.Root != (root() ?? string.Empty))
        {
            await ReloadSourcesAsync(list: false);
        }
        try
        {
            return await Audit();
        }
        catch (Exception failed) when (FeedSet.IsFeedFailure(failed))
        {
            return VulnerabilityData.None;
        }
    }

    /// <summary>The vulnerabilities as far as they are known: none until they are read, or when they could not be.</summary>
    private VulnerabilityData Known => audit is { IsCompletedSuccessfully: true } read ? read.Result : VulnerabilityData.None;

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
        List(Marked(rows, known), Selected?.Id);
        if (Query.Trim().Length == 0)
        {
            status.Text = Counted(rows, known);
        }
    }

    /// <summary>Rows with the advisories that affect the versions their projects have.</summary>
    private static IReadOnlyList<PackageRow> Marked(IReadOnlyList<PackageRow> found, VulnerabilityData known) =>
        [.. found.Select(r => r with { Advisories = [.. r.Installed.Values.OfType<string>().Distinct().SelectMany(v => known.Of(r.Id, v)).Distinct().OrderByDescending(a => a.Severity)] })];

    /// <summary>The status over the installed packages: how many, how many have a known vulnerability, and the audit's
    /// feeds that did not say.</summary>
    private static string Counted(IReadOnlyList<PackageRow> found, VulnerabilityData? known)
    {
        string text = found.Count == 1 ? "1 package installed" : $"{found.Count} packages installed";
        if (found.Count(r => r.IsVulnerable) is int vulnerable and > 0)
        {
            text += vulnerable == 1 ? ", 1 vulnerable" : $", {vulnerable} vulnerable";
        }
        if (known?.Failed is { Count: > 0 } failed)
        {
            text += $"; vulnerabilities unknown: {string.Join(", ", failed.Select(f => f.Feed))} did not answer";
        }
        return text;
    }

    /// <summary>The status after a search: Rider's words for feeds that did not answer, else what was found.</summary>
    private static string Said(FeedSearch answer, IPackageFeed feed)
    {
        List<string> said = [];
        if (answer.Failed.Count > 0)
        {
            string names = string.Join(", ", answer.Failed.Select(f => f.Feed));
            said.Add(answer.Failed.Count == 1 ? $"Unable to fetch packages: {names} is invalid ({answer.Failed[0].Problem})" : $"Unable to fetch packages: {names} are invalid");
        }
        else if (answer.Packages.Count == 0)
        {
            said.Add(feed.Name == FeedSet.All ? "Nothing found" : $"Nothing found in {feed.Name}");
        }
        if (answer.Unmapped > 0)
        {
            said.Add(answer.Unmapped == 1 ? "1 package left out by package source mapping" : $"{answer.Unmapped} packages left out by package source mapping");
        }
        return string.Join("; ", said);
    }

    /// <summary>Adds the chosen package to a project at the chosen version, or moves it there.</summary>
    public Task<bool> InstallAsync(ProjectModel project) =>
        Selected is { } row && (ChosenVersion ?? row.Version) is { Length: > 0 } version ? ChangeAsync(() => manager.InstallAsync(project.Path, row.Id, version)) : Task.FromResult(false);

    public Task<bool> RemoveAsync(ProjectModel project) =>
        Selected is { } row ? ChangeAsync(() => manager.RemoveAsync(project.Path, row.Id)) : Task.FromResult(false);

    /// <summary>Ends a package change still under way, as the IDE closes.</summary>
    public void StopChange() => manager.Stop();

    /// <summary>Reads the sources the first time the window is shown, or lists again once they are read.</summary>
    private Task Shown() => sources == PackageSourceSet.None && given is null ? ReloadSourcesAsync() : RefreshAsync();

    /// <summary>Takes sources just read: their feeds, and the chooser's choices, keeping its choice while it is still there.</summary>
    private void Take(PackageSourceSet loaded)
    {
        Dictionary<string, IPackageFeed> made = new(StringComparer.OrdinalIgnoreCase);
        foreach (FeedSource source in loaded.Feeds)
        {
            // A feed kept when nothing about it changed, so its service index is not asked for again.
            made[source.Name] = feeds.TryGetValue(source.Name, out IPackageFeed? kept) && sources.Feeds.Contains(source) ? kept : PackageFeeds.For(source);
        }
        string was = ChosenFeed;
        sources = loaded;
        feeds = made;
        audit = null;
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
        tabs.Children.Add(Tab("Packages", packages));
        tabs.Children.Add(Tab("Sources", Sources));
    }

    private ToggleButton Tab(string title, Control content)
    {
        ToggleButton tab = new() { Classes = { "panelTab" }, IsChecked = body.Content == content, Content = title };
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

    private async Task<bool> ChangeAsync(Func<Task<bool>> change)
    {
        if (busy)
        {
            return false;
        }
        busy = true;
        status.Text = "Working...";
        using IDisposable held = BackgroundWork.Scope();
        bool done;
        try
        {
            done = await change();
        }
        finally
        {
            busy = false;
        }
        status.Text = done ? "Done" : "The change failed; the log says why";
        Changed?.Invoke(this, EventArgs.Empty);
        await RefreshAsync();
        return done;
    }

    /// <summary>The packages the projects reference: by package, each project's version (null where only central
    /// management would know and it does not say).</summary>
    private Dictionary<string, Dictionary<string, string?>> Installed()
    {
        Dictionary<string, Dictionary<string, string?>> installed = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectModel project in projects())
        {
            foreach ((string name, string? version) in project.Packages)
            {
                if (!installed.TryGetValue(name, out Dictionary<string, string?>? byProject))
                {
                    installed[name] = byProject = [];
                }
                byProject[project.Path] = version;
            }
        }
        return installed;
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
            found = await Feed.VersionsAsync(row.Id, prerelease.IsChecked == true, cancel.Token);
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
        string? vulnerable = row.Installed.Values.OfType<string>().Where(v => known.Of(row.Id, v).Count > 0).OrderByDescending(v => v, VersionOrder.Instance).FirstOrDefault();
        string? fix = vulnerable is null ? null : known.FixedVersion(row.Id, vulnerable, versions);
        ChosenVersion = fix ?? versions.FirstOrDefault() ?? row.Version;
        ComboBox picker = new() { Name = "Versions", ItemsSource = versions, SelectedItem = ChosenVersion, MinWidth = 140 };
        picker.SelectionChanged += (_, _) =>
        {
            ChosenVersion = picker.SelectedItem as string;
            ShowProjects(row);
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
            by.Bind(TextBlock.ForegroundProperty, by.GetResourceObservable("Passenger.TextMuted"));
            details.Children.Add(by);
        }
        if (row.Feeds.Count > 0)
        {
            TextBlock from = new() { Text = $"In {string.Join(", ", row.Feeds)}", FontSize = 12 };
            from.Bind(TextBlock.ForegroundProperty, from.GetResourceObservable("Passenger.TextMuted"));
            details.Children.Add(from);
        }
        if (vulnerable is not null)
        {
            details.Children.Add(Vulnerability(known.Of(row.Id, vulnerable), vulnerable, fix));
        }
        details.Children.Add(new StackPanel { Name = "projects", Spacing = 4, Margin = new Thickness(0, 8, 0, 0) });
        ShowProjects(row);
    }

    /// <summary>Rider's word on a vulnerable version, its advisories each with its severity, and the version that fixes
    /// them, which the version picker and Update offer.</summary>
    private Border Vulnerability(IReadOnlyList<Advisory> advisories, string version, string? fix)
    {
        PathIcon warning = new() { Width = 14, Height = 14, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, [DockProperty] = Dock.Left };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable("Icon.Problems"));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable("Passenger.Editor.Warning"));
        StackPanel said = new() { Spacing = 4 };
        said.Children.Add(new TextBlock
        {
            Text = $"Known security vulnerability in {version}. It could be exploited in certain scenarios. See the advisories below for details.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (Advisory advisory in advisories)
        {
            Button link = new() { Classes = { "link" }, Content = advisory.Url.OriginalString, VerticalAlignment = VerticalAlignment.Center };
            link.Click += (_, _) => LinkClicked?.Invoke(this, advisory.Url);
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
            Name = "Vulnerability",
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = new DockPanel { Children = { warning, said } },
        };
        block.Bind(Border.BackgroundProperty, block.GetResourceObservable("Passenger.Banner"));
        return block;
    }

    private void ShowProjects(PackageRow row)
    {
        if (details.Children.OfType<StackPanel>().FirstOrDefault(p => p.Name == "projects") is not { } panel)
        {
            return;
        }
        panel.Children.Clear();
        panel.Children.Add(new TextBlock { Text = "Projects", FontWeight = FontWeight.SemiBold });
        foreach (ProjectModel project in projects().Where(p => p.Exists).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool has = row.Installed.TryGetValue(project.Path, out string? at);
            IReadOnlyList<Advisory> affecting = at is null ? [] : Known.Of(row.Id, at);
            TextBlock version = new() { Text = has ? at ?? "installed" : string.Empty, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            version.Bind(TextBlock.ForegroundProperty, version.GetResourceObservable(affecting.Count > 0 ? "Passenger.Editor.Warning" : "Passenger.TextMuted"));
            // A slot of its own, so the buttons stand in one column whichever lines have the mark.
            Panel mark = new() { Width = 14, VerticalAlignment = VerticalAlignment.Center };
            if (affecting.Count > 0)
            {
                mark.Children.Add(Warning($"Known security vulnerability in {at}: {affecting[0].Severity} severity ({affecting[0].Url.OriginalString})"));
            }
            StackPanel line = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { new TextBlock { Text = project.Name, Width = 220, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, version, mark },
            };
            if (!has)
            {
                line.Children.Add(Action("Install", () => InstallAsync(project)));
            }
            else
            {
                if (ChosenVersion is { } chosen && at is not null && VersionOrder.Instance.Compare(chosen, at) is int order && order != 0)
                {
                    line.Children.Add(Action(order > 0 ? "Update" : "Downgrade", () => InstallAsync(project)));
                }
                line.Children.Add(Action("Remove", () => RemoveAsync(project)));
            }
            panel.Children.Add(line);
        }
    }

    private Button Action(string label, Func<Task<bool>> run)
    {
        Button button = new() { Content = label, IsEnabled = !busy };
        button.Click += (_, _) => _ = run();
        return button;
    }

    private Control Row(PackageRow row)
    {
        TextBlock version = new() { Text = row.Version, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        version.Bind(TextBlock.ForegroundProperty, version.GetResourceObservable("Passenger.TextMuted"));
        PathIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(6, 0, 6, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable("Icon.Package"));
        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal,
            Height = 26,
            Children = { icon, new TextBlock { Text = row.Id, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.IsInstalled ? FontWeight.SemiBold : FontWeight.Normal }, version },
        };
        if (row.Description.Length > 0)
        {
            ToolTip.SetTip(line, row.Description);
        }
        if (row.IsVulnerable)
        {
            PathIcon warning = Warning($"Known security vulnerability: {row.Advisories[0].Severity} severity ({row.Advisories[0].Url.OriginalString})");
            warning.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(warning);
        }
        return line;
    }

    /// <summary>The warning a vulnerable version is marked with, saying what it is.</summary>
    private static PathIcon Warning(string tip)
    {
        PathIcon warning = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable("Icon.Problems"));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable("Passenger.Editor.Warning"));
        ToolTip.SetTip(warning, tip);
        return warning;
    }
}
