using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Vcs;
using Passenger.Editor;
using Passenger.Vcs;

namespace Passenger.App.ToolWindows;

/// <summary>A changed file in the Commit window, and whether it goes into the next commit.</summary>
public sealed class CommitRow(FileChange change, bool chosen) : Observable
{
    private bool isChosen = chosen;

    public FileChange Change { get; } = change;

    public bool IsChosen
    {
        get => isChosen;
        set => Set(ref isChosen, value);
    }
}

/// <summary>A group of the Commit window's files: Changes, or Unversioned Files, and whether every one of them goes in
/// (true), none (false) or some (null).</summary>
public sealed class CommitGroup(string name, IReadOnlyList<CommitRow> rows) : Observable
{
    private bool? isChosen = State(rows);

    public string Name { get; } = name;
    public IReadOnlyList<CommitRow> Rows { get; } = rows;

    public bool? IsChosen
    {
        get => isChosen;
        private set => Set(ref isChosen, value);
    }

    /// <summary>Works out <see cref="IsChosen"/> again, after a row's box changed.</summary>
    internal void Update() => IsChosen = State(Rows);

    private static bool? State(IReadOnlyList<CommitRow> rows)
    {
        int count = rows.Count(r => r.IsChosen);
        return count == rows.Count ? true : count == 0 ? false : null;
    }
}

/// <summary>The Commit window: the changed files under Changes and the new ones under Unversioned Files, each with a box
/// for whether it goes in (changes are in and new files out until chosen), and files in conflict above them under Merge
/// Conflicts, with its Resolve link and no box; Amend and the message below (a merge's own message while one is under
/// way); Commit and Commit and Push. Double-click opens a file's diff, or a conflicted file's merge; Rollback puts the
/// chosen ones back.</summary>
public sealed class CommitView : DockPanel, IOwnKeys
{
    private VcsController vcs;
    private readonly ObservableCollection<object> rows = [];
    private readonly Dictionary<string, bool> chosen = [];
    private readonly ListBox list;
    private readonly TextBox message;
    private readonly CheckBox amend;
    private readonly Button commit;
    private readonly Button commitAndPush;
    private readonly TextBlock placeholder;
    private string? beforeAmend;

    /// <summary>The merge's message the box was given, taken away again if the merge ends some other way.</summary>
    private string? prefilled;

    public CommitView(VcsController git)
    {
        vcs = git;
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            SelectionMode = SelectionMode.Multiple,
        };
        ReusedRows.Use(list, Row);
        ListMenus.Attach(list, () => list.SelectedItem is not CommitRow chosen ? [] : chosen.Change.Kind is ChangeKind.Conflicted ?
        [
            ListMenus.Item("Merge…", "Enter", () => MergeRequested?.Invoke(this, chosen.Change.Path)),
            ListMenus.Item("Resolve Conflicts…", null, () => ResolveRequested?.Invoke(this, EventArgs.Empty)),
            ListMenus.Item("Jump to Source", "F4", () => OpenRequested?.Invoke(this, chosen.Change.Path)),
            new Separator(),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Change.Path))),
        ] :
        [
            ListMenus.Item("Show Diff", "Enter", () => DiffRequested?.Invoke(this, chosen.Change.Path)),
            ListMenus.Item("Jump to Source", "F4", () => OpenRequested?.Invoke(this, chosen.Change.Path), chosen.Change.Kind is not ChangeKind.Deleted),
            new Separator(),
            ListMenus.Item("Rollback…", null, () => RollbackRequested?.Invoke(this, [.. ChosenChanges(chosen)])),
            ListMenus.Item("Delete…", null, () => DeleteRequested?.Invoke(this, chosen.Change.Path), chosen.Change.Kind is not ChangeKind.Deleted),
            ListMenus.Item("Add to .gitignore", null, () => IgnoreRequested?.Invoke(this, chosen.Change.Path), chosen.Change.Kind is ChangeKind.Unversioned),
            new Separator(),
            ListMenus.Item("Copy Path", null, () => IdeErrors.Observe(ListMenus.CopyAsync(this, chosen.Change.Path))),
        ]);
        // Double-click shows the diff and F4 the file.
        list.DoubleTapped += (_, _) =>
        {
            if (list.SelectedItem is CommitRow row)
            {
                Open(row.Change);
            }
        };
        list.KeyDown += (_, e) =>
        {
            if (list.SelectedItem is CommitRow shown)
            {
                if (e.Key == Key.F4 && shown.Change.Kind is not ChangeKind.Deleted)
                {
                    OpenRequested?.Invoke(this, shown.Change.Path);
                    e.Handled = true;
                    return;
                }
                if (e.Key == Key.Enter || AppActions.Presses(AppActions.ShowDiff, e))
                {
                    Open(shown.Change);
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
            {
                // Space flips the chosen rows' boxes.
                CommitRow[] selected = [.. list.SelectedItems?.OfType<CommitRow>().Where(r => r.Change.Kind is not ChangeKind.Conflicted) ?? []];
                bool to = !selected.All(r => r.IsChosen);
                foreach (CommitRow row in selected)
                {
                    row.IsChosen = to;
                }
                e.Handled = true;
            }
        };
        placeholder = new TextBlock { Classes = { "emptyText" } };

        amend = new CheckBox { Content = "Amend", Margin = new Thickness(10, 4, 0, 0) };
        amend.IsCheckedChanged += (_, _) => IdeErrors.Observe(AmendChangedAsync());
        message = new TextBox
        {
            Classes = { "search" },
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = "Commit Message",
            MinHeight = 90,
            MaxHeight = 220,
            Margin = new Thickness(10, 4, 10, 8),
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        message.TextChanged += (_, _) => UpdateButtons();
        // Ctrl+Enter (Cmd+Enter on the Mac) commits, seen as it tunnels: the box takes any Enter as a new line first.
        message.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == Keystroke.Command)
            {
                IdeErrors.Observe(CommitAsync(push: false));
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        commit = new Button { Content = "Commit", Classes = { "primary" } };
        commit.Click += (_, _) => IdeErrors.Observe(CommitAsync(push: false));
        commitAndPush = new Button { Content = "Commit and Push…" };
        commitAndPush.Click += (_, _) => IdeErrors.Observe(CommitAsync(push: true));
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(10, 0, 10, 10),
            Children = { commit, commitAndPush },
        };
        Border bottom = new()
        {
            [DockProperty] = Dock.Bottom,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = new StackPanel { Children = { amend, message, buttons } },
        };
        bottom.Bind(Border.BorderBrushProperty, bottom.GetResourceObservable(TokenKeys.Divider));

        StackPanel tools = new()
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Margin = new Thickness(6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ToolButtons.Of(AppActions.CommitRefresh, IconKeys.Rerun, "Refresh", action => ActionRequested?.Invoke(this, action)),
                ToolButtons.Of(IconKeys.Rollback, "Rollback", () => RollbackRequested?.Invoke(this, ChosenOrSelected())),
                ToolButtons.Of(AppActions.ShowDiff, IconKeys.Diff, "Show Diff", _ =>
                {
                    if (list.SelectedItem is CommitRow row)
                    {
                        DiffRequested?.Invoke(this, row.Change.Path);
                    }
                }),
            },
        };
        Border top = ToolBars.Top(tools);

        Children.Add(top);
        Children.Add(bottom);
        Children.Add(new Panel { Children = { list, placeholder } });
        vcs.Changed += OnChanged;
        vcs.BusyChanged += OnBusyChanged;
        Fill();
    }

    /// <summary>Lists another folder's changes from now on, its session's working tree in place of the last's.</summary>
    public void Bind(VcsController next)
    {
        vcs.Changed -= OnChanged;
        vcs.BusyChanged -= OnBusyChanged;
        vcs = next;
        vcs.Changed += OnChanged;
        vcs.BusyChanged += OnBusyChanged;
        Fill();
    }

    private void OnChanged(object? sender, EventArgs e) => Fill();

    /// <summary>Amend checked starts from the last commit's message, and unchecked gives back what was typed.</summary>
    private async Task AmendChangedAsync()
    {
        if (amend.IsChecked == true)
        {
            beforeAmend = message.Text;
            if (await vcs.LastMessageAsync() is { } last && (beforeAmend ?? string.Empty).Trim().Length == 0)
            {
                message.Text = last;
            }
        }
        else if (beforeAmend is not null)
        {
            message.Text = beforeAmend;
            beforeAmend = null;
        }
        UpdateButtons();
    }

    private void OnBusyChanged(object? sender, EventArgs e) => UpdateButtons();

    /// <summary>The Merge Conflicts group's Resolve link: the Conflicts dialog.</summary>
    public event EventHandler? ResolveRequested;

    /// <summary>Asks the person to agree (a title, the question, the button that agrees).</summary>
    public Func<string, string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>A conflicted file to merge, double-clicked.</summary>
    public event EventHandler<string>? MergeRequested;

    /// <summary>What a double-click or Enter opens: a conflicted file's merge, any other file's diff.</summary>
    /// <summary>Show Diff's keys, as the Commit window takes them (<see cref="AppActions.KeysIn"/>), ahead of what they run
    /// elsewhere while a file is chosen.</summary>
    public bool TakesKey(KeyEventArgs e) => list.IsKeyboardFocusWithin && list.SelectedItem is CommitRow && AppActions.Presses(AppActions.ShowDiff, e);

    private void Open(FileChange change)
    {
        if (change.Kind is ChangeKind.Conflicted)
        {
            MergeRequested?.Invoke(this, change.Path);
        }
        else
        {
            DiffRequested?.Invoke(this, change.Path);
        }
    }

    /// <summary>A file to open in the editor.</summary>
    public event EventHandler<string>? OpenRequested;

    /// <summary>One of the window's actions asked for by its button.</summary>
    public event EventHandler<string>? ActionRequested;

    /// <summary>A changed file to move to the Trash, from its menu.</summary>
    public event EventHandler<string>? DeleteRequested;

    /// <summary>An unversioned file to leave out of Git for good, by its path in the repository's .gitignore.</summary>
    public event EventHandler<string>? IgnoreRequested;

    /// <summary>The rows chosen with the one the menu is for: every one when it is among them, else it alone.</summary>
    private IEnumerable<FileChange> ChosenChanges(CommitRow row)
    {
        List<CommitRow> chosen = [.. list.SelectedItems?.OfType<CommitRow>() ?? []];
        return chosen.Contains(row) ? chosen.Select(r => r.Change) : [row.Change];
    }

    /// <summary>A file's diff against the last commit, to show.</summary>
    public event EventHandler<string>? DiffRequested;

    /// <summary>Rollback pressed: the files to put back, once the person has said so.</summary>
    public event EventHandler<IReadOnlyList<FileChange>>? RollbackRequested;

    /// <summary>The files and the groups over them, as the window lists them.</summary>
    public IReadOnlyList<object> Rows => rows;

    /// <summary>The files that go into the next commit.</summary>
    public IReadOnlyList<FileChange> Chosen => [.. rows.OfType<CommitRow>().Where(r => r.IsChosen).Select(r => r.Change)];

    public string Message
    {
        get => message.Text ?? string.Empty;
        set => message.Text = value;
    }

    public bool Amend
    {
        get => amend.IsChecked == true;
        set => amend.IsChecked = value;
    }

    /// <summary>Puts the keys in the message (the Commit shortcut).</summary>
    public void FocusMessage() => message.Focus();

    /// <summary>Commits the chosen files with the message (pushing after when asked), then empties the message. Whether the
    /// commit was made, whatever came of the push.</summary>
    public async Task<bool> CommitAsync(bool push)
    {
        // A merge's commit takes in every change but those left out, which stay changes, once the person agrees.
        bool excluding = vcs.Merge.CommitsWhole && !Amend && vcs.Conflicts.Count == 0
            && rows.OfType<CommitRow>().Any(r => !r.IsChosen && r.Change.Kind is not (ChangeKind.Unversioned or ChangeKind.Conflicted));
        if (excluding && Confirm is { } ask
            && !await ask("Changes Excluded from Merge Commit", "Are you sure you want to exclude these changed files from merge commit?", "Commit"))
        {
            return false;
        }
        if (!(await vcs.CommitAsync(Chosen, Message, Amend, push)).Commit.Succeeded)
        {
            return false;
        }
        beforeAmend = null;
        amend.IsChecked = false;
        message.Text = string.Empty;
        return true;
    }

    private IReadOnlyList<FileChange> ChosenOrSelected()
    {
        CommitRow[] selected = [.. list.SelectedItems?.OfType<CommitRow>() ?? []];
        return selected.Length > 0 ? [.. selected.Select(r => r.Change)] : Chosen;
    }

    /// <summary>Lists the status again in place: a file still changed as it was keeps its row, its box and whether it is
    /// selected, so a refresh with nothing new (the window coming to the front, a save) leaves the list as it was, and
    /// what Rollback acts on with it. A file changed another way (modified, now deleted) is selected again by path.</summary>
    private void Fill()
    {
        HashSet<string> selected = [.. list.SelectedItems?.OfType<CommitRow>().Select(r => r.Change.Path) ?? []];
        Dictionary<FileChange, CommitRow> kept = [];
        foreach (CommitRow row in rows.OfType<CommitRow>())
        {
            // A conflicted file has no box: resolved, it is chosen as any change is.
            if (row.Change.Kind is not ChangeKind.Conflicted)
            {
                chosen[row.Change.Path] = row.IsChosen;
            }
            kept.TryAdd(row.Change, row);
        }
        IReadOnlyList<FileChange> changes = vcs.Changes;
        List<object> wanted = [];
        Group(MergeConflicts, [.. changes.Where(c => c.Kind is ChangeKind.Conflicted).OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => Row(c, false))]);
        Group("Changes", [.. changes.Where(c => c.Kind is not (ChangeKind.Unversioned or ChangeKind.Conflicted)).OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => Row(c, true))]);
        Group("Unversioned Files", [.. changes.Where(c => c.Kind is ChangeKind.Unversioned).OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => Row(c, false))]);

        // What went is taken out and what came put in at its place; rows kept are in the same order in both lists.
        HashSet<object> keep = new(wanted, ReferenceEqualityComparer.Instance);
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(rows[i]))
            {
                rows.RemoveAt(i);
            }
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i == rows.Count || !ReferenceEquals(rows[i], wanted[i]))
            {
                rows.Insert(i, wanted[i]);
            }
        }
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i] is CommitRow row && selected.Contains(row.Change.Path) && !list.Selection.IsSelected(i))
            {
                list.Selection.Select(i);
            }
        }
        placeholder.Text = vcs.Repository is null ? Refusals.NoRepository : "No changes";
        placeholder.IsVisible = rows.Count == 0;
        // A merge under way gives the commit that finishes it the message git wrote for it, until it ends.
        string? merging = vcs.Merge.CommitsWhole ? vcs.Merge.Message : null;
        if (merging is not null && Message.Trim().Length == 0 && merging != prefilled)
        {
            Message = merging;
            prefilled = merging;
        }
        else if (merging is null && prefilled is not null)
        {
            if (Message == prefilled)
            {
                Message = string.Empty;
            }
            prefilled = null;
        }
        UpdateButtons();

        CommitRow Row(FileChange change, bool byDefault)
        {
            if (kept.Remove(change, out CommitRow? same))
            {
                return same;
            }
            // A file in conflict never goes into a commit as it is.
            CommitRow row = new(change, change.Kind is not ChangeKind.Conflicted && (chosen.TryGetValue(change.Path, out bool was) ? was : byDefault));
            row.PropertyChanged += (_, _) =>
            {
                foreach (CommitGroup group in rows.OfType<CommitGroup>())
                {
                    group.Update();
                }
                UpdateButtons();
            };
            return row;
        }

        void Group(string name, CommitRow[] members)
        {
            if (members.Length == 0)
            {
                return;
            }
            wanted.Add(rows.OfType<CommitGroup>().FirstOrDefault(g => g.Name == name && g.Rows.SequenceEqual(members)) ?? new CommitGroup(name, members));
            wanted.AddRange(members);
        }
    }

    private void UpdateButtons()
    {
        // Off while a commit (or a stash, a rollback) runs, so a second press cannot start another on git's index lock.
        bool ready = !vcs.IsBusy && vcs.Repository is not null && Message.Trim().Length > 0 && (Amend || vcs.Merge.CommitsWhole || rows.OfType<CommitRow>().Any(r => r.IsChosen));
        commit.IsEnabled = ready;
        commitAndPush.IsEnabled = ready;
    }

    private const string MergeConflicts = "Merge Conflicts";

    /// <summary>A row for whichever group or file the list hands its container: a group's box, name and count (Merge
    /// Conflicts' with its Resolve link and no box), or a file's box and line (a conflicted file's with no box: it is
    /// resolved first). A box follows its item while shown, and a click on it chooses the item it shows.</summary>
    private Control Row()
    {
        CheckBox groupBox = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 2, 0) };
        TextBlock groupName = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        TextBlock count = new()
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
        };
        count.Classes.Add("muted");
        Button resolve = new() { Classes = { "link" }, Content = "Resolve", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        resolve.Click += (_, _) => ResolveRequested?.Invoke(this, EventArgs.Empty);
        StackPanel groupLine = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { groupBox, groupName, count, resolve } };
        CheckBox fileBox = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(22, 0, 2, 0) };
        (DockPanel fileLine, RowIcon icon, TextBlock name, TextBlock folder) = FileLineParts();
        DockPanel.SetDock(fileBox, Dock.Left);
        fileLine.Children.Insert(0, fileBox);
        Panel row = new() { Children = { groupLine, fileLine } };
        bool setting = false;
        string? nameColour = null;
        string? groupColour = null;
        groupBox.IsCheckedChanged += (_, _) =>
        {
            if (setting || row.DataContext is not CommitGroup group)
            {
                return;
            }
            bool to = groupBox.IsChecked != false;
            foreach (CommitRow file in group.Rows)
            {
                file.IsChosen = to;
            }
        };
        fileBox.IsCheckedChanged += (_, _) =>
        {
            if (!setting && row.DataContext is CommitRow file)
            {
                file.IsChosen = fileBox.IsChecked == true;
            }
        };
        return row.Showing<object>(item =>
        {
            setting = true;
            groupLine.IsVisible = item is CommitGroup;
            fileLine.IsVisible = item is CommitRow;
            if (item is CommitGroup group)
            {
                bool conflicts = group.Name == MergeConflicts;
                groupBox.IsVisible = !conflicts;
                groupBox.IsChecked = group.IsChosen;
                groupName.Text = group.Name;
                groupName.Margin = new Thickness(conflicts ? 26 : 0, 0, 0, 0);
                groupName.Resource(TextBlock.ForegroundProperty, ref groupColour, conflicts ? TokenKeys.Vcs_File_Conflicted : null);
                count.Text = Core.Plural.Of(group.Rows.Count, "file");
                resolve.IsVisible = conflicts;
            }
            else if (item is CommitRow file)
            {
                bool conflicted = file.Change.Kind is ChangeKind.Conflicted;
                fileBox.IsVisible = !conflicted;
                fileBox.IsChecked = file.IsChosen;
                fileLine.Margin = new Thickness(conflicted ? 44 : 0, 0, 0, 0);
                ShowFile(fileLine, icon, name, folder, ref nameColour, file.Change, vcs.Repository);
            }
            setting = false;
        });
    }

    /// <summary>The files as this window lists them, without their boxes, for Rollback's question to name.</summary>
    internal static Control FileList(IReadOnlyList<FileChange> changes, Git? repository)
    {
        StackPanel lines = new() { Margin = new Thickness(8, 2) };
        foreach (FileChange change in changes.OrderBy(c => c.Kind is ChangeKind.Unversioned).ThenBy(c => c.Path, StringComparer.Ordinal))
        {
            lines.Children.Add(FileLine(change, repository));
        }
        return new Border
        {
            Classes = { "framed" },
            MaxHeight = (24 * 8) + 6,
            Child = new ScrollViewer { Content = lines, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
    }

    /// <summary>A changed file as the window shows it: its icon, its name in its change's colour (struck through when
    /// deleted) and its folder from the repository's top.</summary>
    private static DockPanel FileLine(FileChange change, Git? repository)
    {
        (DockPanel line, RowIcon icon, TextBlock name, TextBlock folder) = FileLineParts();
        string? colour = null;
        ShowFile(line, icon, name, folder, ref colour, change, repository);
        return line;
    }

    /// <summary>A file's line with nothing in it yet, for <see cref="ShowFile"/> to show a file in.</summary>
    private static (DockPanel Line, RowIcon Icon, TextBlock Name, TextBlock Folder) FileLineParts()
    {
        RowIcon icon = new()
        {
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
        };
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock folder = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        folder.Classes.Add("muted");
        // The folder takes what the row leaves, ending in an ellipsis in a narrow window rather than cut mid-letter.
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(name, Dock.Left);
        DockPanel line = new() { Height = ListRow.Height, Children = { icon, name, folder } };
        return (line, icon, name, folder);
    }

    /// <summary>Shows a changed file in a line's parts, its name's colour bound anew only for another kind of change.</summary>
    private static void ShowFile(DockPanel line, RowIcon icon, TextBlock name, TextBlock folder, ref string? colour, FileChange change, Git? repository)
    {
        icon.Data = Explorer.FileIcons.ForFile(change.Path);
        icon.Foreground = Explorer.FileIcons.BrushForFile(change.Path);
        name.Text = change.Name;
        name.Resource(TextBlock.ForegroundProperty, ref colour, KindToken(change.Kind));
        name.TextDecorations = change.Kind is ChangeKind.Deleted ? TextDecorations.Strikethrough : null;
        folder.Text = repository is not null ? Path.GetDirectoryName(repository.Relative(change.Path))?.Replace('\\', '/') ?? string.Empty : string.Empty;
        ToolTip.SetTip(line, change.From is { } from ? $"{change.Kind}: {from} -> {change.Path}" : $"{change.Kind}: {change.Path}");
    }

    /// <summary>A changed file's colour by how it changed (modified, added, deleted, new).</summary>
    internal static string KindToken(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => TokenKeys.Vcs_File_Added,
        ChangeKind.Deleted => TokenKeys.Vcs_File_Deleted,
        ChangeKind.Unversioned => TokenKeys.Vcs_File_Unversioned,
        ChangeKind.Conflicted => TokenKeys.Vcs_File_Conflicted,
        _ => TokenKeys.Vcs_File_Modified,
    };
}
