using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
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
/// the solution's projects have of it, and the known vulnerabilities of those versions.</summary>
public sealed record PackageRow(string Id, string Version, string Description, string Authors, long Downloads, IReadOnlyDictionary<string, string?> Installed)
{
    public bool IsInstalled => Installed.Count > 0;

    public IReadOnlyList<string> Feeds { get; init; } = [];

    /// <summary>The advisories that affect a version a project has, the gravest first.</summary>
    public IReadOnlyList<Advisory> Advisories { get; init; } = [];

    public bool IsVulnerable => Advisories.Count > 0;
}

/// <summary>The NuGet window, its Packages and Sources tabs in its header. Packages: the projects' packages with nothing
/// typed, else what the feed chosen beside the query finds; the chosen package's versions and a row per project to Install,
/// Update or Remove it through the .NET command line, whose restore uses those feeds and their credentials. A version with
/// a known vulnerability (the audit's VulnerabilityInfo, as restore reads it) is marked with its advisories, and Update
/// offers the least version that fixes it. Sources: the files and their feeds (<see cref="NuGetSourcesView"/>). A view over
/// the folder's <see cref="FolderPackages"/>, its session's.</summary>
public sealed class NuGetView : DockPanel
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

    /// <param name="folder">The folder's packages, which the window lists and changes.</param>
    public NuGetView(FolderPackages folder)
    {
        model = folder;
        search = new TextBox { Classes = { "search" }, PlaceholderText = "Search packages", Margin = new Thickness(8, 0) };
        chooser = new ComboBox { Classes = { "compact" }, MinWidth = 130, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        ToolTip.SetTip(chooser, "The feeds searched");
        chooser.SelectionChanged += (_, _) =>
        {
            if (!choosing)
            {
                IdeErrors.Observe(RefreshAsync());
            }
        };
        prerelease = new CheckBox { Content = "Prerelease", VerticalAlignment = VerticalAlignment.Center };
        status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
        status.Classes.Add("muted");
        Button refresh = ToolButtons.Of(IconKeys.Rerun, "Refresh", () => IdeErrors.Observe(ReloadSourcesAsync()));
        refresh.Margin = new Thickness(6, 0, 0, 0);
        typing = new Debounce(Pause, RefreshAsync);
        search.TextChanged += (_, _) => typing.Restart();
        prerelease.IsCheckedChanged += (_, _) => IdeErrors.Observe(RefreshAsync());
        // In a narrow window the search and the count give way, so the feed chooser, Prerelease and Refresh keep their room.
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("3*,Auto,Auto,Auto,2*"), VerticalAlignment = VerticalAlignment.Center };
        row.ColumnDefinitions[0].MaxWidth = 276;
        Control[] parts = [search, chooser, prerelease, refresh, status];
        for (int i = 0; i < parts.Length; i++)
        {
            Grid.SetColumn(parts[i], i);
            row.Children.Add(parts[i]);
        }
        Border top = ToolBars.Top(row);

        list = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<PackageRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => IdeErrors.Observe(ShowAsync());
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
        log.Bind(TextBox.BorderBrushProperty, log.GetResourceObservable(TokenKeys.Divider));
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
        Follow(model);
        AttachedToVisualTree += (_, _) =>
        {
            IdeErrors.Observe(Shown());
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
        };
    }

    /// <summary>The folder's packages the window shows, its session's.</summary>
    public FolderPackages Packages => model;

    /// <summary>Shows another folder's packages from now on, its session's in place of the last's; its sources read if the
    /// last folder's had been, so the feeds offered are its own.</summary>
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
        Follow(next);
        if (next.Sources == PackageSourceSet.None && read)
        {
            IdeErrors.Observe(ReloadSourcesAsync(list: false));
        }
    }

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

    private void OnChangeBegun(object? sender, string stage) => status.Text = "Working…";

    /// <summary>A package change ended, whoever asked for it: said, and the list read again.</summary>
    private void OnChanged(object? sender, PackageOutcome outcome)
    {
        status.Text = outcome.Done ? "Done" : "The change failed; the log says why";
        IdeErrors.Observe(RefreshAsync());
    }

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

    public bool IsBusy => model.IsBusy;

    public void ShowPackages() => Show(packages);

    /// <summary>The Sources tab, its files read again; the package list stays as it was until a feed changes.</summary>
    public void ShowSources()
    {
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

    /// <summary>Lists again: the installed packages with nothing typed, else the chosen feeds' search.</summary>
    public async Task RefreshAsync()
    {
        asking?.Cancel();
        CancellationTokenSource cancel = asking = new();
        string? chosen = Selected?.Id;
        Dictionary<string, Dictionary<string, string?>> installed = model.Installed();
        // The feed's answer is the list shown next, so a screenshot waits for it rather than for "Searching".
        using IDisposable held = BackgroundWork.Scope();
        if (!model.IsCurrent && model.Given is null)
        {
            await ReloadSourcesAsync();
            return;
        }
        IPackageFeed feed = Feed;
        try
        {
            IReadOnlyList<PackageRow> found;
            ToolTip.SetTip(status, null);
            if (Query.Trim().Length == 0)
            {
                found = [.. installed.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new PackageRow(p.Key, p.Value.Values.FirstOrDefault(v => v is not null) ?? string.Empty, string.Empty, string.Empty, 0, p.Value))];
                status.Text = Counted(found, null);
            }
            else
            {
                status.Text = $"Searching {feed.Name}…";
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
            Task<VulnerabilityData>? auditing = found.Any(r => r.IsInstalled) ? model.Audit() : null;
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
        string text = $"{Plural.Of(found.Count, "package")} installed";
        if (found.Count(r => r.IsVulnerable) is int vulnerable and > 0)
        {
            text += $", {vulnerable} vulnerable";
        }
        if (known?.Failed is { Count: > 0 } failed)
        {
            text += $"; vulnerabilities unknown: {string.Join(", ", failed.Select(f => f.Feed))} did not answer";
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
            said.Add(answer.Failed.Count == 1 ? $"Unable to fetch packages: {names} is invalid ({answer.Failed[0].Problem})" : $"Unable to fetch packages: {names} are invalid");
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
        && (BeforeInstall is null || await BeforeInstall(project, row.Id, version))
        && (await model.InstallAsync(project, row.Id, version)).Done;

    /// <summary>Asked before the window's own Install or Update of a package in a project, with its id and version: whether
    /// it goes on as asked (false when the person cancelled, or had it made another way, as EF Core's packages moved with
    /// their provider).</summary>
    public Func<ProjectModel, string, string, Task<bool>>? BeforeInstall { get; set; }

    /// <summary>Takes the chosen package out of a project (Remove).</summary>
    public async Task<bool> RemoveAsync(ProjectModel project) => Selected is { } row && (await model.RemoveAsync(project, row.Id)).Done;

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
            by.Classes.Add("muted");
            details.Children.Add(by);
        }
        if (row.Feeds.Count > 0)
        {
            TextBlock from = new() { Text = $"In {string.Join(", ", row.Feeds)}", FontSize = 12 };
            from.Classes.Add("muted");
            details.Children.Add(from);
        }
        if (vulnerable is not null)
        {
            details.Children.Add(Vulnerability(known.Of(row.Id, vulnerable), vulnerable, fix));
        }
        details.Children.Add(new StackPanel { Name = "projects", Spacing = 4, Margin = new Thickness(0, 8, 0, 0) });
        ShowProjects(row);
    }

    /// <summary>The word on a vulnerable version, its advisories each with its severity, and the version that fixes
    /// them, which the version picker and Update offer.</summary>
    private Border Vulnerability(IReadOnlyList<Advisory> advisories, string version, string? fix)
    {
        PathIcon warning = new() { Width = 14, Height = 14, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Top, [DockProperty] = Dock.Left };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable(IconKeys.Problems));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable(TokenKeys.Warning));
        StackPanel said = new() { Spacing = 4 };
        said.Children.Add(new TextBlock
        {
            Text = $"Known security vulnerability in {version}. It could be exploited in certain scenarios. See the advisories below for details.",
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (Advisory advisory in advisories)
        {
            Control link = new TextBlock { Text = advisory.Address, VerticalAlignment = VerticalAlignment.Center };
            if (advisory.Link is { } page)
            {
                Button button = new() { Classes = { "link" }, Content = advisory.Address, VerticalAlignment = VerticalAlignment.Center };
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
            Name = "Vulnerability",
            Classes = { "box" },
            Padding = new Thickness(10, 8),
            Child = new DockPanel { Children = { warning, said } },
        };
        block.Bind(Border.BackgroundProperty, block.GetResourceObservable(TokenKeys.Banner));
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
        foreach (ProjectModel project in model.Projects.Where(p => p.Exists).OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool has = row.Installed.TryGetValue(project.Path, out string? at);
            IReadOnlyList<Advisory> affecting = at is null ? [] : Known.Of(row.Id, at);
            TextBlock version = new() { Text = has ? at ?? "installed" : string.Empty, Width = 110, VerticalAlignment = VerticalAlignment.Center };
            version.Bind(TextBlock.ForegroundProperty, version.GetResourceObservable(affecting.Count > 0 ? TokenKeys.Warning : TokenKeys.TextMuted));
            // A slot of its own, so the buttons stand in one column whichever lines have the mark.
            Panel mark = new() { Width = 14, VerticalAlignment = VerticalAlignment.Center };
            if (affecting.Count > 0)
            {
                mark.Children.Add(Warning($"Known security vulnerability in {at}: {affecting[0].Severity} severity ({affecting[0].Address})"));
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
        Button button = new() { Content = label, IsEnabled = !model.IsBusy };
        button.Click += (_, _) => IdeErrors.Observe(run());
        return button;
    }

    private Control Row(PackageRow row)
    {
        TextBlock version = new() { Text = row.Version, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        version.Classes.Add("muted");
        PathIcon icon = new() { Width = 14, Height = 14, Margin = new Thickness(6, 0, 6, 0) };
        icon.Bind(PathIcon.DataProperty, icon.GetResourceObservable(IconKeys.Package));
        StackPanel line = new()
        {
            Orientation = Orientation.Horizontal,
            Height = ListRow.Height,
            Children = { icon, new TextBlock { Text = row.Id, VerticalAlignment = VerticalAlignment.Center, FontWeight = row.IsInstalled ? FontWeight.SemiBold : FontWeight.Normal }, version },
        };
        if (row.Description.Length > 0)
        {
            ToolTip.SetTip(line, row.Description);
        }
        if (row.IsVulnerable)
        {
            PathIcon warning = Warning($"Known security vulnerability: {row.Advisories[0].Severity} severity ({row.Advisories[0].Address})");
            warning.Margin = new Thickness(6, 0, 0, 0);
            line.Children.Add(warning);
        }
        return line;
    }

    /// <summary>The warning a vulnerable version is marked with, saying what it is.</summary>
    private static PathIcon Warning(string tip)
    {
        PathIcon warning = new() { Width = 12, Height = 12, VerticalAlignment = VerticalAlignment.Center };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable(IconKeys.Problems));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable(TokenKeys.Warning));
        ToolTip.SetTip(warning, tip);
        return warning;
    }
}
