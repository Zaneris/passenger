using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Vcs;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>A row of the Commit window's Stashes: a stash, which opens to the files it holds, or one of those files.</summary>
public sealed class StashNode(StashInfo stash, FileChange? file = null) : Observable, ITreeRow
{
    private bool isExpanded;

    public StashInfo Stash { get; } = stash;

    /// <summary>The file it is, or null for the stash itself.</summary>
    public FileChange? File { get; } = file;

    /// <summary>What the stash's subject says: its message and the branch it was made on.</summary>
    public StashSaid Said { get; } = StashSaid.Of(stash.Message);

    /// <summary>The files a stash holds, once read; null until then.</summary>
    public IReadOnlyList<StashNode>? Files { get; set; }

    public int Depth => File is null ? 0 : 1;

    public bool Expands => File is null;

    public bool IsExpanded
    {
        get => isExpanded;
        set => Set(ref isExpanded, value);
    }
}

/// <summary>The Commit window's Stashes: every stash, the newest first, by its message, the branch it was made on and when,
/// each opening to the files it holds (its new files among them), a file's diff opened by a double click or Enter; and for
/// the stash chosen, what Pop, Apply and Drop do, in words and in git's, with their buttons. Stash Changes… puts the changes
/// away; the list follows the repository as it changes while shown.</summary>
public sealed class StashesView : DockPanel
{
    private VcsController vcs;
    private readonly TreeList list;
    private readonly TreeRows<StashNode> rows = [];
    private readonly EmptyState empty = new();
    private readonly StackPanel said = new() { Spacing = Gaps.Tight };
    private readonly TextBlock gitWords = new() { Classes = { StyleClasses.Code, StyleClasses.Muted }, TextWrapping = TextWrapping.Wrap };
    private readonly Button pop;
    private readonly Button apply;
    private readonly Button drop;
    private readonly Border actions;
    private List<StashNode> roots = [];
    private bool stale = true;
    private bool running;
    private int loads;

    public StashesView(VcsController git)
    {
        vcs = git;
        list = new TreeList { Classes = { StyleClasses.Tree }, ItemsSource = rows };
        ReusedRows.Use(list, Row);
        TreeKeys.Attach<StashNode>(list, Toggle, node =>
        {
            if (node.File is not { } file)
            {
                return false;
            }
            DiffRequested?.Invoke(this, (node.Stash, file));
            return true;
        }, new ListActs { Remove = () => Act(DropChosen) });
        list.SelectionChanged += (_, _) => Lay();
        ListMenus.Attach(list, Menu);
        pop = new Button { Content = "Pop", Classes = { StyleClasses.Primary } };
        pop.Click += (_, _) => Act(() => Chosen is { } stash ? UnstashAsync(stash, pop: true) : Task.CompletedTask);
        apply = new Button { Content = "Apply" };
        apply.Click += (_, _) => Act(() => Chosen is { } stash ? UnstashAsync(stash, pop: false) : Task.CompletedTask);
        drop = new Button { Content = "Drop…" };
        drop.Click += (_, _) => Act(DropChosen);
        actions = new Border
        {
            [DockProperty] = Dock.Bottom,
            BorderThickness = Hairlines.Above,
            Padding = new Thickness(ListRow.TextStart, Gaps.Normal, ListRow.TextStart, ListRow.TextStart),
            Child = new StackPanel
            {
                Spacing = Gaps.Normal,
                Children =
                {
                    said,
                    gitWords,
                    new WrapPanel { ItemSpacing = Gaps.Normal, LineSpacing = Gaps.Normal, Children = { pop, apply, drop } },
                },
            },
        };
        actions.Bind(Border.BorderBrushProperty, actions.GetResourceObservable(TokenKeys.Divider));
        Tools = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Gaps.Hair,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ToolButtons.Of(AppActions.Stash, IconKeys.Add, "Stash Changes…", action => ActionRequested?.Invoke(this, action)),
                ToolButtons.Of(IconKeys.Rerun, "Refresh", () => IdeErrors.Observe(LoadAsync())),
                ToolButtons.Of(AppActions.ShowDiff, IconKeys.Diff, "Show Diff", _ =>
                {
                    if (list.SelectedItem is StashNode { File: { } file } node)
                    {
                        DiffRequested?.Invoke(this, (node.Stash, file));
                    }
                }),
            },
        };
        Children.Add(actions);
        Children.Add(new Panel { Children = { list, empty } });
        vcs.Changed += OnChanged;
        AttachedToVisualTree += (_, _) =>
        {
            if (stale)
            {
                IdeErrors.Observe(LoadAsync());
            }
        };
        Lay();
    }

    /// <summary>The buttons the Commit window's toolbar shows while the stashes are: Stash Changes…, Refresh, Show Diff.</summary>
    public StackPanel Tools { get; }

    /// <summary>The rows shown, top to bottom.</summary>
    public IReadOnlyList<StashNode> Rows => rows;

    /// <summary>The stashes as last read, the newest first.</summary>
    public IReadOnlyList<StashInfo> Stashes => [.. roots.Select(r => r.Stash)];

    /// <summary>The stash chosen, or the one whose file is chosen.</summary>
    public StashInfo? Chosen => (list.SelectedItem as StashNode)?.Stash;

    public Button PopButton => pop;

    public Button ApplyButton => apply;

    public Button DropButton => drop;

    /// <summary>What Pop, Apply and Drop do with the stash chosen, a sentence each.</summary>
    public IReadOnlyList<string> WhatItDoes => [.. said.Children.OfType<TextBlock>().Select(t => t.Text ?? string.Empty)];

    /// <summary>The same in git's words.</summary>
    public string GitWords => gitWords.Text ?? string.Empty;

    /// <summary>What it says while nothing is listed.</summary>
    public string? Placeholder => empty.IsVisible ? empty.Text : null;

    /// <summary>A file of a stash to show beside what it was before the stash.</summary>
    public event EventHandler<(StashInfo Stash, FileChange File)>? DiffRequested;

    /// <summary>Drops a stash once the person agrees, done when it is dropped or kept; the window's.</summary>
    public Func<StashInfo, Task>? Drop { get; set; }

    /// <summary>One of the window's actions asked for by its button (Stash Changes…).</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>The menu of the row chosen: Pop, Apply and Drop… of its stash, and a file's Show Diff.</summary>
    private IReadOnlyList<Control> Menu()
    {
        if (Chosen is not { } stash)
        {
            return [];
        }
        List<Control> items =
        [
            ListMenus.Item("Pop", () => Act(() => UnstashAsync(stash, pop: true)), WhyNotUnstash),
            ListMenus.Item("Apply", () => Act(() => UnstashAsync(stash, pop: false)), WhyNotUnstash),
            ListMenus.Item("Drop…", () => Act(DropChosen), WhyNotUnstash),
        ];
        if (list.SelectedItem is StashNode { File: { } file })
        {
            items.Add(new Separator());
            items.Add(ListMenus.Open("Show Diff", () => DiffRequested?.Invoke(this, (stash, file))));
        }
        return items;
    }

    /// <summary>Lists another folder's stashes from now on.</summary>
    public void Bind(VcsController next)
    {
        vcs.Changed -= OnChanged;
        vcs = next;
        vcs.Changed += OnChanged;
        roots = [];
        rows.Show([]);
        OnChanged(this, EventArgs.Empty);
    }

    /// <summary>Gives the chosen row the keys, as a click on it does.</summary>
    public void FocusRows() => ListKeys.Hold(list);

    /// <summary>Chooses a stash by its place in the list, or one of its files by name, opening the stash.</summary>
    public async Task ChooseAsync(int index, string? file = null)
    {
        if (index >= roots.Count)
        {
            return;
        }
        StashNode stash = roots[index];
        if (file is null)
        {
            list.SelectedItem = stash;
            return;
        }
        if (!stash.IsExpanded)
        {
            await OpenAsync(stash);
        }
        list.SelectedItem = stash.Files?.FirstOrDefault(f => f.File!.Name == file);
    }

    /// <summary>Reads the stashes again, the one chosen and those open kept by their commits, an open one's files read again.</summary>
    public async Task LoadAsync()
    {
        int load = ++loads;
        stale = false;
        IReadOnlyList<StashInfo> stashes = await vcs.StashesAsync();
        HashSet<string> open = [.. roots.Where(r => r.IsExpanded).Select(r => r.Stash.Commit)];
        List<StashNode> made = [.. stashes.Select(s => new StashNode(s) { IsExpanded = open.Contains(s.Commit) })];
        foreach (StashNode stash in made.Where(s => s.IsExpanded))
        {
            stash.Files = [.. (await vcs.StashFilesAsync(stash.Stash)).Select(f => new StashNode(stash.Stash, f))];
        }
        if (load != loads)
        {
            return;
        }
        (string? Commit, string? File) chosen = list.SelectedItem is StashNode node ? (node.Stash.Commit, node.File?.Path) : (null, null);
        roots = made;
        rows.Show([.. TreeRows<StashNode>.Shown(roots, r => r.IsExpanded ? r.Files : null)]);
        list.SelectedItem = rows.FirstOrDefault(r => r.Stash.Commit == chosen.Commit && r.File?.Path == chosen.File)
            ?? rows.FirstOrDefault(r => r.Stash.Commit == chosen.Commit) ?? rows.FirstOrDefault();
        empty.Text = vcs.Repository is null ? vcs.NoRepository : "Nothing is stashed: Stash Changes… puts your changes away, to be popped back later";
        empty.IsVisible = roots.Count == 0;
        Lay();
    }

    /// <summary>Opens or closes a stash, its files read as it first opens.</summary>
    public void Toggle(StashNode node)
    {
        if (node.File is not null)
        {
            return;
        }
        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            rows.Close(node);
            list.SelectedItem = node;
            return;
        }
        IdeErrors.Observe(OpenAsync(node));
    }

    private async Task OpenAsync(StashNode node)
    {
        node.Files ??= [.. (await vcs.StashFilesAsync(node.Stash)).Select(f => new StashNode(node.Stash, f))];
        if (node.IsExpanded || !rows.Contains(node))
        {
            return;
        }
        node.IsExpanded = true;
        rows.Open(node, node.Files);
        list.SelectedItem = node;
        Lay();
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        stale = true;
        if (VisualRoot is not null)
        {
            IdeErrors.Observe(LoadAsync());
        }
    }

    /// <summary>Runs one of the stash's actions unless one runs, the buttons greyed meanwhile, then reads the list again.</summary>
    private void Act(Func<Task> action) => IdeErrors.Observe(ActingAsync(action));

    private async Task ActingAsync(Func<Task> action)
    {
        if (running)
        {
            return;
        }
        running = true;
        Lay();
        try
        {
            await action();
        }
        finally
        {
            running = false;
        }
        await LoadAsync();
        // The keys come back to the stashes acted in, so Undo there takes back what was done, though its button has gone.
        if (!IsKeyboardFocusWithin && !ListKeys.Hold(list))
        {
            list.Focus();
        }
    }

    private Task UnstashAsync(StashInfo stash, bool pop) => vcs.UnstashAsync(stash, pop);

    private Task DropChosen() => Chosen is { } stash && Drop is { } drop ? drop(stash) : Task.CompletedTask;

    private string? WhyNotUnstash => vcs.IsBusy || running ? VcsController.Busy : null;

    /// <summary>What the chosen stash's buttons do, said again, the buttons greyed with why where none is chosen or Git is busy.</summary>
    private void Lay()
    {
        StashNode? stash = (list.SelectedItem as StashNode) is { } node ? roots.FirstOrDefault(r => r.Stash.Commit == node.Stash.Commit) : null;
        string? why = stash is null ? "Choose a stash first" : WhyNotUnstash;
        foreach (Button button in (Button[])[pop, apply, drop])
        {
            ToolButtons.Grey(button, why);
        }
        actions.IsVisible = roots.Count > 0;
        said.Children.Clear();
        foreach (string sentence in Sentences(stash))
        {
            said.Children.Add(new TextBlock { Text = sentence, TextWrapping = TextWrapping.Wrap });
        }
        string place = stash is null ? string.Empty : $"stash@{{{stash.Stash.Index}}}";
        gitWords.Text = stash is null ? null : $"git stash pop {place}\ngit stash apply {place}\ngit stash drop {place}";
        gitWords.IsVisible = stash is not null;
    }

    /// <summary>Pop's, Apply's and Drop's words for a stash: what it puts back, as far as its files are read.</summary>
    private static IEnumerable<string> Sentences(StashNode? stash)
    {
        if (stash is null)
        {
            yield return "Choose a stash to see what it holds and what Pop, Apply and Drop do with it.";
            yield break;
        }
        string what = stash.Files is { } files ? Held(files) : "its changes";
        yield return $"Pop puts {what} back into your files and drops the stash; Apply puts them back and keeps it.";
        yield return "Where your files changed the same lines since, git stops for the conflicts and the stash stays.";
        yield return "Drop deletes it without putting anything back; Undo brings it back while git keeps its commit.";
    }

    /// <summary>A stash's files as Pop names them: the changes to some, the new files apart.</summary>
    private static string Held(IReadOnlyList<StashNode> files)
    {
        string[] changed = [.. files.Where(f => f.File!.Kind is not ChangeKind.Unversioned).Select(f => f.File!.Name)];
        string[] added = [.. files.Where(f => f.File!.Kind is ChangeKind.Unversioned).Select(f => f.File!.Name)];
        string Named(string[] names) => names.Length <= 3 ? Plural.Listed(names) : $"{string.Join(", ", names[..2])} and {Plural.Of(names.Length - 2, "more")}";
        string changes = changed.Length == 0 ? string.Empty : $"the changes to {Named(changed)}";
        string news = added.Length == 0 ? string.Empty : $"the new {Plural.Noun(added.Length, "file")} {Named(added)}";
        return changes.Length > 0 && news.Length > 0 ? $"{changes} and {news}" : changes.Length > 0 ? changes : news.Length > 0 ? news : "nothing";
    }

    /// <summary>A row for whichever stash or file the list hands its container: a stash's chevron, its message, and the
    /// branch it was made on and when, muted; a file's line as every list of changed files shows one.</summary>
    private Control Row()
    {
        RowExpander chevron = RowExpander.Of<StashNode>(Toggle);
        TextBlock message = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new() { Classes = { StyleClasses.Caption, StyleClasses.Muted }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Normal, 0, 0, 0) };
        ShrinkingRow stashLine = new() { Height = ListRow.Height, Children = { chevron, message, detail } };
        ChangedFileRow file = new();
        Panel row = new() { Children = { stashLine, file.Line } };
        return row.Showing<StashNode>(node =>
        {
            row.Margin = new Thickness(node.Depth * Gaps.Indent, 0, 0, 0);
            stashLine.IsVisible = node.File is null;
            file.Line.IsVisible = node.File is not null;
            if (node.File is { } changed)
            {
                file.Show(changed, vcs.Repository);
                return;
            }
            chevron.Expands = true;
            chevron.IsOpen = node.IsExpanded;
            message.Text = node.Said.Message;
            detail.Text = $"{(node.Said.Branch is { } branch ? $"on {branch}, " : string.Empty)}{GitLogView.When(node.Stash.Time)}";
        });
    }
}
