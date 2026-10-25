using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.History;

/// <summary>What the window lends a history: its editors, whose document of a file the diff's right side is (borrowed
/// when the file is not open, so a change put back there is the file's), the revert of files to versions, made under
/// an activity's name, and how its diffs are made.</summary>
public sealed record HistoryHost(EditorArea Editors, Func<IReadOnlyList<(string Path, string? Version)>, string, Task> Revert, DiffPanes Diffs);

/// <summary>One of the Local History window's tabs: what a file or folder went through, newest first, each change set
/// with its name and time and the labels among them, narrowed by content (a file's) or by file name (a folder's). A
/// file's chosen row shows the file before it beside the file now, whose arrows put a change back; a folder's lists its
/// files that are not now what they were then, each shown so as it is chosen. Revert Selected and Later Changes puts back
/// what the chosen row's version was; a folder's Revert Selection, only the files chosen.</summary>
public sealed class HistoryTab : DockPanel
{
    private const double RowHeight = ListRow.Height;

    private readonly LocalHistory store;
    private readonly HistoryHost host;
    private readonly ListBox list;
    private readonly TextBlock placeholder;
    private readonly TextBox filter;
    private readonly ContentControl diffHost = new();
    private readonly TextBlock diffPlaceholder;
    private readonly ListBox? fileList;
    private readonly ConcurrentDictionary<string, string> texts = new(StringComparer.Ordinal);
    private readonly EventHandler onStoreChanged;
    private IReadOnlyList<HistoryEvent> events = [];
    private IReadOnlyDictionary<string, string?> known = new Dictionary<string, string?>();
    private IReadOnlyList<HistoryRow> all = [];
    private IReadOnlyList<HistoryRow> rows = [];
    private IReadOnlyList<HistoryDifference> files = [];
    private (string Path, OpenDocument Document, IDisposable Hold)? borrowed;
    private (long Set, string Path, string? Version)? shownDiff;
    private int loads;
    private int filtering;
    private int shows;
    private bool closed;
    private bool away;

    public HistoryTab(LocalHistory store, HistoryHost host, string path, bool isFolder)
    {
        this.store = store;
        this.host = host;
        Path = path;
        IsFolder = isFolder;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemTemplate = new FuncDataTemplate<HistoryRow>((row, _) => row is null ? new TextBlock() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => IdeErrors.Observe(ShowAsync());
        ListMenus.Attach(list, () => Selected is null ? [] :
        [
            ListMenus.Item("Revert Selected and Later Changes", null, () => IdeErrors.Observe(RevertAsync())),
        ]);
        placeholder = MutedText.Of(string.Empty);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        placeholder.TextWrapping = TextWrapping.Wrap;
        diffPlaceholder = MutedText.Of("Select activity to view changes");
        diffPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
        diffPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        diffHost.Content = diffPlaceholder;

        filter = new TextBox { Classes = { "search" }, PlaceholderText = isFolder ? "Search by file name" : "Search by content", Width = 240, Margin = new Thickness(8, 0) };
        filter.TextChanged += (_, _) => IdeErrors.Observe(FilterAsync());
        Button revert = ToolButtons.Of(IconKeys.Rollback, "Revert Selected and Later Changes", () => IdeErrors.Observe(RevertAsync()));
        Border top = ToolBars.Top(new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { filter, revert } });

        Control right = diffHost;
        if (isFolder)
        {
            fileList = new ListBox
            {
                Classes = { "tree" },
                SelectionMode = SelectionMode.Multiple,
                ItemTemplate = new FuncDataTemplate<HistoryDifference>((file, _) => file is null ? new TextBlock() : FileRow(file), supportsRecycling: false),
            };
            fileList.SelectionChanged += (_, _) => IdeErrors.Observe(ShowFileAsync());
            ListMenus.Attach(fileList, () => fileList.SelectedItems?.Count > 0 ? [ListMenus.Item("Revert Selection", null, () => IdeErrors.Observe(RevertSelectionAsync()))] : []);
            Border fileBar = new()
            {
                [DockProperty] = Dock.Top,
                Height = 30,
                Child = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0), Children = { ToolButtons.Of(IconKeys.Rollback, "Revert Selection", () => IdeErrors.Observe(RevertSelectionAsync())) } },
            };
            Grid split = new() { RowDefinitions = new RowDefinitions("*,1,2*") };
            DockPanel changes = new() { Children = { fileBar, fileList } };
            HairlineDivider between = new(GridResizeDirection.Rows) { [Grid.RowProperty] = 1 };
            Grid.SetRow(diffHost, 2);
            split.Children.Add(changes);
            split.Children.Add(between);
            split.Children.Add(diffHost);
            right = split;
        }
        Grid body = new() { ColumnDefinitions = new ColumnDefinitions("2*,1,5*") };
        HairlineDivider divider = new(GridResizeDirection.Columns) { [Grid.ColumnProperty] = 1 };
        Grid.SetColumn(right, 2);
        body.Children.Add(new Panel { Children = { list, placeholder } });
        body.Children.Add(divider);
        body.Children.Add(right);
        Children.Add(top);
        Children.Add(body);

        onStoreChanged = (_, _) => IdeErrors.Observe(LoadAsync());
        store.Changed += onStoreChanged;
        host.Editors.Closed += OnFileClosed;
        // A diff that left the window (another tab shown, the tool window hidden or moved) let go of its panes: back, it is shown anew.
        DetachedFromVisualTree += (_, _) => away = true;
        AttachedToVisualTree += (_, _) =>
        {
            if (away && !closed)
            {
                away = false;
                shownDiff = null;
                IdeErrors.Observe(ShowAsync());
            }
        };
        IdeErrors.Observe(LoadAsync());
    }

    /// <summary>The file or folder the history is of, as it is named now.</summary>
    public string Path { get; }

    public bool IsFolder { get; }

    /// <summary>The rows listed, after the search.</summary>
    public IReadOnlyList<HistoryRow> Rows => rows;

    public HistoryRow? Selected => list.SelectedItem as HistoryRow;

    /// <summary>A folder's files that are not now what they were before the chosen row.</summary>
    public IReadOnlyList<HistoryDifference> Files => files;

    /// <summary>The diff shown, of the file or of the folder's file chosen.</summary>
    public DiffView? Diff => diffHost.Content as DiffView;

    /// <summary>What the list says when it lists nothing.</summary>
    public string Placeholder => placeholder.IsVisible ? placeholder.Text ?? string.Empty : string.Empty;

    public string FilterText
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>Chooses a row, as a click on it does.</summary>
    public void Select(HistoryRow row) => list.SelectedItem = rows.FirstOrDefault(r => r.Set == row.Set);

    /// <summary>Chooses the row of a change set, once the history has it.</summary>
    public void SelectSet(long set) => list.SelectedItem = rows.FirstOrDefault(r => r.Set == set);

    /// <summary>The tab closed: it stops following the history, and lets go of the file it borrowed.</summary>
    public void Close()
    {
        closed = true;
        shows++;
        store.Changed -= onStoreChanged;
        host.Editors.Closed -= OnFileClosed;
        diffHost.Content = null;
        Forget();
    }

    /// <summary>Reads the history again, keeping the row chosen while it is still there; a new one opens on the newest.</summary>
    public async Task LoadAsync()
    {
        int load = ++loads;
        (IReadOnlyList<HistoryEvent> readEvents, IReadOnlyDictionary<string, string?> readKnown) = store.Snapshot();
        IReadOnlyList<HistoryRow> read = await BackgroundWork.Run(() => IsFolder
            ? HistoryRows.OfFolder(readEvents, Path)
            : HistoryRows.OfFile(readEvents, readKnown, Path));
        if (load != loads || closed)
        {
            return;
        }
        events = readEvents;
        known = readKnown;
        all = read;
        await FilterAsync();
        // What the chosen row's version is against may have changed (a revert, a file deleted): it is shown again.
        shownDiff = null;
        await ShowAsync();
    }

    /// <summary>Revert Selected and Later Changes: the file, or every file of the folder that differs, back as it was before
    /// the chosen row's change set.</summary>
    public Task RevertAsync()
    {
        if (Selected is not { } row)
        {
            return Task.CompletedTask;
        }
        string name = RevertName(row);
        return IsFolder ? host.Revert([.. files.Select(f => (f.Path, f.Then))], name) : host.Revert([(Path, row.Version)], name);
    }

    /// <summary>What Revert Selected and Later Changes names its activity: the row's label or time.</summary>
    public static string RevertName(HistoryRow row)
    {
        string when = GitLogView.When(row.Time);
        return row.Name is { } named ? $"Revert to '{named}' at {when}" : $"Revert to {when}";
    }

    /// <summary>Revert Selection: only the folder's files chosen, back as they were before the chosen row.</summary>
    public Task RevertSelectionAsync()
    {
        if (Selected is not { } row || fileList?.SelectedItems is not { Count: > 0 } chosen)
        {
            return Task.CompletedTask;
        }
        string when = GitLogView.When(row.Time);
        string name = row.Name is { } named ? $"Revert '{named}' at {when}" : $"Revert {when}";
        return host.Revert([.. chosen.OfType<HistoryDifference>().Select(f => (f.Path, f.Then))], name);
    }

    /// <summary>Lists the rows the search keeps: a file's whose version holds the text, a folder's that touched a file
    /// whose name has it; the row chosen stays chosen while listed.</summary>
    private async Task FilterAsync()
    {
        int filter = ++filtering;
        string text = FilterText.Trim();
        IReadOnlyList<HistoryRow> from = all;
        IReadOnlyList<HistoryRow> kept = text.Length == 0 ? from
            : IsFolder ? [.. from.Where(r => r.Files.Any(f => System.IO.Path.GetFileName(f).Contains(text, StringComparison.OrdinalIgnoreCase)))]
            : await BackgroundWork.Run(() => (IReadOnlyList<HistoryRow>)[.. from.Where(r => r.Version is { } version && TextOf(version).Contains(text, StringComparison.OrdinalIgnoreCase))]);
        if (filter != filtering || closed)
        {
            return;
        }
        long? chosen = Selected?.Set;
        rows = kept;
        list.ItemsSource = rows;
        string name = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
        placeholder.Text = text.Length > 0 ? $"No matching activity in {name} detected" : $"No activity in {name} detected";
        placeholder.IsVisible = rows.Count == 0;
        HistoryRow? again = chosen is { } set ? rows.FirstOrDefault(r => r.Set == set) : null;
        list.SelectedItem = again ?? (rows.Count > 0 ? rows[0] : null);
    }

    /// <summary>What the chosen row shows: a file's version beside the file now, or the folder's files that differ since.</summary>
    private async Task ShowAsync()
    {
        if (closed)
        {
            return;
        }
        if (Selected is not { } row)
        {
            files = [];
            if (fileList is not null)
            {
                fileList.ItemsSource = files;
            }
            ShowNothing("Select activity to view changes");
            return;
        }
        if (!IsFolder)
        {
            await ShowDiffAsync(row.Set, Path, row.Version, Before(row));
            return;
        }
        int show = ++shows;
        IReadOnlyList<HistoryEvent> readEvents = events;
        IReadOnlyDictionary<string, string?> readKnown = known;
        IReadOnlyList<HistoryDifference> found = await BackgroundWork.Run(() => HistoryRows.Since(readEvents, readKnown, row.Set, Path));
        if (show != shows || closed)
        {
            return;
        }
        string? chosen = (fileList!.SelectedItem as HistoryDifference)?.Path;
        files = found;
        fileList.ItemsSource = files;
        if (files.Count == 0)
        {
            ShowNothing("No changes detected");
            return;
        }
        fileList.SelectedItem = files.FirstOrDefault(f => chosen is not null && PathNames.Same(f.Path, chosen)) ?? files[0];
        await ShowFileAsync();
    }

    private async Task ShowFileAsync()
    {
        if (Selected is { } row && fileList?.SelectedItem is HistoryDifference file)
        {
            await ShowDiffAsync(row.Set, file.Path, file.Then, Before(row));
        }
    }

    /// <summary>A row's version, as the diff's left side names it.</summary>
    private static string Before(HistoryRow row) =>
        $"{(row.IsLabel ? row.Title : $"Before {row.Title}")} · {GitLogView.When(row.Time)}";

    /// <summary>A version of a file beside the file now: its document, borrowed when it is not open, so an arrow puts a
    /// change back in it; read-only and empty where the file is gone.</summary>
    private async Task ShowDiffAsync(long set, string path, string? version, string title)
    {
        if (shownDiff == (set, path, version) && Diff is not null)
        {
            return;
        }
        int show = ++shows;
        string before = version is null ? string.Empty : await BackgroundWork.Run(() => TextOf(version));
        if (show != shows || closed)
        {
            return;
        }
        (OpenDocument Document, IDisposable Hold)? lent = borrowed is { } held && PathNames.Same(held.Path, path) ? null
            : File.Exists(path) || host.Editors.Documents.Any(d => PathNames.Same(d.Path, path)) ? host.Editors.Borrow(path) : null;
        OpenDocument? document = lent?.Document ?? (borrowed is { } still && PathNames.Same(still.Path, path) ? still.Document : null);
        TextDocument mine = document?.Text ?? new TextDocument(string.Empty);
        TextSnapshot text = mine.Snapshot;
        FileDiff diff = await BackgroundWork.Run(() => FileDiff.Compare(before, text));
        // Out of the window, a diff is made as it comes back.
        if (show != shows || closed || away || document is not null && !host.Editors.Documents.Contains(document))
        {
            lent?.Hold.Dispose();
            return;
        }
        string right = document is not null ? "Current" : "File does not exist";
        DiffView view = host.Diffs.ForFile(System.IO.Path.GetFileName(path), diff, mine, document, (version is null ? "File does not exist" : title, right), path, path);
        diffHost.Content = view;
        shownDiff = (set, path, version);
        if (diff.Hunks.Count > 0)
        {
            view.Right.GoTo(new TextPosition(Math.Min(diff.Hunks[0].NewStart, text.LineCount - 1), 0));
        }
        if (lent is { } taken)
        {
            borrowed?.Hold.Dispose();
            borrowed = (path, taken.Document, taken.Hold);
        }
        else if (document is null)
        {
            borrowed?.Hold.Dispose();
            borrowed = null;
        }
    }

    private void ShowNothing(string why)
    {
        shows++;
        shownDiff = null;
        diffPlaceholder.Text = why;
        diffHost.Content = diffPlaceholder;
        Forget();
    }

    /// <summary>Lets go of the file the diff borrowed, which closes unless a tab has it.</summary>
    private void Forget()
    {
        borrowed?.Hold.Dispose();
        borrowed = null;
    }

    /// <summary>The file the diff shows closed for good (deleted, the folder left): the diff goes with it.</summary>
    private void OnFileClosed(object? sender, OpenDocument document)
    {
        if (borrowed is { } held && held.Document == document)
        {
            borrowed = null;
            shownDiff = null;
            diffHost.Content = diffPlaceholder;
            diffPlaceholder.Text = "File does not exist";
        }
    }

    /// <summary>A version's text, read once.</summary>
    private string TextOf(string hash) =>
        texts.GetOrAdd(hash, h => store.Content(h) is { } bytes ? FileText.FromBytes(bytes)?.Text ?? string.Empty : string.Empty);

    private Control Row(HistoryRow row)
    {
        DockPanel line = new() { Height = RowHeight, LastChildFill = true };
        TextBlock time = MutedText.Of(GitLogView.When(row.Time));
        time[DockProperty] = Dock.Right;
        time.Margin = new Thickness(8, 0, 4, 0);
        line.Children.Add(time);
        if (row.IsLabel)
        {
            // The IDE's own labels (Tests Passed) muted beside the person's.
            PathIcon tag = new() { Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), [DockProperty] = Dock.Left };
            tag.Bind(PathIcon.DataProperty, tag.GetResourceObservable(IconKeys.Label));
            tag.Bind(PathIcon.ForegroundProperty, tag.GetResourceObservable(row.System ? TokenKeys.TextMuted : TokenKeys.Accent));
            line.Children.Add(tag);
        }
        TextBlock title = new() { Text = row.Title, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (row.IsLabel)
        {
            title.FontWeight = FontWeight.SemiBold;
        }
        line.Children.Add(title);
        return line;
    }

    private Control FileRow(HistoryDifference file)
    {
        PathIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(file.Path),
            Foreground = Explorer.FileIcons.BrushForFile(file.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(4, 0, 6, 0),
        };
        TextBlock name = new() { Text = file.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable(CommitView.KindToken(file.Kind)));
        if (file.Kind is ChangeKind.Deleted)
        {
            name.TextDecorations = TextDecorations.Strikethrough;
        }
        string where = System.IO.Path.GetDirectoryName(PathNames.Shown(file.Path, Path))?.Replace('\\', '/') ?? string.Empty;
        TextBlock folder = MutedText.Of(where);
        folder.FontSize = 12;
        folder.Margin = new Thickness(8, 0, 0, 0);
        return new StackPanel { Orientation = Orientation.Horizontal, Height = RowHeight, Children = { icon, name, folder } };
    }

}
