using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A file in conflict as the Conflicts dialog lists it: its name and folder, and what yours and theirs did to it.</summary>
public sealed record ConflictRow(GitConflict Conflict, string Folder, string Yours, string Theirs)
{
    /// <summary>Whether both sides have the file, so there is something to merge.</summary>
    public bool CanMerge => Conflict.CanMerge;
}

/// <summary>The Conflicts dialog: what is under way (Merging branch feature into branch main), the files in conflict
/// with what yours and theirs did to each (the branches named over the columns, yours the rebased branch in a rebase),
/// and Accept Yours, Accept Theirs and Merge... for the chosen ones; a double-click merges. It follows the working tree,
/// a file resolved leaving the list, and closes once none is left. Escape closes it.</summary>
public sealed class ConflictsDialog : Window
{
    private const double StatusWidth = 170;

    private readonly VcsController vcs;
    private readonly Func<GitConflict, Task> merge;
    private readonly ObservableCollection<ConflictRow> rows = [];
    private readonly ListBox list;
    private readonly TextBlock description;
    private readonly TextBlock yoursColumn;
    private readonly TextBlock theirsColumn;
    private readonly Button acceptYours;
    private readonly Button acceptTheirs;
    private readonly Button mergeButton;

    /// <param name="merge">Opens the merge window on a file, done when it closes.</param>
    public ConflictsDialog(VcsController vcs, Func<GitConflict, Task> merge)
    {
        this.vcs = vcs;
        this.merge = merge;
        Title = "Conflicts";
        Width = 760;
        Height = 420;
        MinWidth = 520;
        MinHeight = 260;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        description = new TextBlock { [DockPanel.DockProperty] = Avalonia.Controls.Dock.Top, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
        yoursColumn = Column(string.Empty, 1);
        theirsColumn = Column(string.Empty, 2);
        Grid header = new()
        {
            [DockPanel.DockProperty] = Avalonia.Controls.Dock.Top,
            ColumnDefinitions = new ColumnDefinitions($"*,{StatusWidth},{StatusWidth}"),
            Height = ListRow.Height,
            // Level with the rows, which the list and its items pad.
            Margin = new Thickness(10, 0),
            Children = { Column("Name", 0), yoursColumn, theirsColumn },
        };
        Border underHeader = new() { Child = header, BorderThickness = new Thickness(0, 0, 0, 1), [DockPanel.DockProperty] = Avalonia.Controls.Dock.Top };
        underHeader.Bind(Border.BorderBrushProperty, underHeader.GetResourceObservable(TokenKeys.Separator));
        list = new ListBox
        {
            Classes = { "tree" },
            ItemsSource = rows,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<ConflictRow>((row, _) => row is null ? new Panel() : Row(row), supportsRecycling: false),
        };
        list.SelectionChanged += (_, _) => UpdateButtons();
        list.DoubleTapped += (_, _) => IdeErrors.Observe(MergeAsync());
        list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                IdeErrors.Observe(MergeAsync());
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        Border table = new()
        {
            Classes = { "framed" },
            ClipToBounds = true,
            Child = new DockPanel { Children = { underHeader, list } },
        };

        acceptYours = ActionButton("Accept Yours", () => AcceptAsync(yours: true));
        acceptTheirs = ActionButton("Accept Theirs", () => AcceptAsync(yours: false));
        mergeButton = ActionButton("Merge…", MergeAsync);
        StackPanel actions = new()
        {
            [DockPanel.DockProperty] = Avalonia.Controls.Dock.Right,
            Spacing = 8,
            Margin = new Thickness(12, 0, 0, 0),
            Children = { acceptYours, acceptTheirs, mergeButton },
        };
        Button close = new() { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        Content = DialogFooter.Page(new DockPanel { Margin = new Thickness(16), Children = { description, actions, table } }, DialogFooter.Bar(DialogFooter.Listed(close)));

        EventHandler changed = (_, _) => Fill();
        EventHandler busy = (_, _) => UpdateButtons();
        vcs.Changed += changed;
        vcs.BusyChanged += busy;
        Closed += (_, _) =>
        {
            vcs.Changed -= changed;
            vcs.BusyChanged -= busy;
        };
        Opened += (_, _) => Dispatcher.UIThread.Post(FocusChosen, DispatcherPriority.Loaded);
        Fill();
    }

    /// <summary>The files in conflict, as listed.</summary>
    public IReadOnlyList<ConflictRow> Rows => rows;

    /// <summary>What the dialog says is under way, over the list.</summary>
    public string Description => description.Text ?? string.Empty;

    /// <summary>The columns' titles: Name, then yours and theirs with their branches.</summary>
    public IReadOnlyList<string> Columns => ["Name", yoursColumn.Text ?? string.Empty, theirsColumn.Text ?? string.Empty];

    /// <summary>The rows chosen, which the buttons act on.</summary>
    public IReadOnlyList<ConflictRow> Chosen
    {
        get => [.. list.SelectedItems?.OfType<ConflictRow>() ?? []];
        set
        {
            list.SelectedItems?.Clear();
            foreach (ConflictRow row in value)
            {
                list.SelectedItems?.Add(row);
            }
        }
    }

    /// <summary>Accept Yours or Accept Theirs for the files chosen.</summary>
    public async Task AcceptAsync(bool yours)
    {
        IReadOnlyList<ConflictRow> chosen = Chosen;
        if (chosen.Count > 0)
        {
            await vcs.AcceptAsync([.. chosen.Select(r => r.Conflict)], yours);
        }
    }

    /// <summary>Merge...: the merge window on the file chosen, done once it closes.</summary>
    public async Task MergeAsync()
    {
        if (Chosen is [{ CanMerge: true } row] && !vcs.IsBusy)
        {
            await merge(row.Conflict);
        }
    }

    /// <summary>Lists the conflicts as git has them now, the chosen files still chosen; none left, the dialog is done.</summary>
    private void Fill()
    {
        if (vcs.Conflicts.Count == 0)
        {
            Close();
            return;
        }
        ConflictSides sides = vcs.Sides;
        description.Text = sides.Description;
        yoursColumn.Text = sides.YoursColumn;
        theirsColumn.Text = sides.TheirsColumn;
        HashSet<string> chosen = [.. Chosen.Select(r => r.Conflict.Path)];
        rows.Clear();
        foreach (GitConflict conflict in vcs.Conflicts.OrderBy(c => c.Path, StringComparer.Ordinal))
        {
            string folder = vcs.Repository is { } repository ? Path.GetDirectoryName(repository.Relative(conflict.Path))?.Replace('\\', '/') ?? string.Empty : string.Empty;
            rows.Add(new ConflictRow(conflict, folder, sides.YoursHas(conflict) ? "Modified" : "Deleted", sides.TheirsHas(conflict) ? "Modified" : "Deleted"));
        }
        Chosen = [.. rows.Where(r => chosen.Contains(r.Conflict.Path))];
        if (Chosen.Count == 0)
        {
            list.SelectedIndex = 0;
        }
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        bool any = Chosen.Count > 0 && !vcs.IsBusy;
        acceptYours.IsEnabled = any;
        acceptTheirs.IsEnabled = any;
        mergeButton.IsEnabled = any && Chosen is [{ CanMerge: true }];
    }

    /// <summary>The keys to the chosen row, as a list in a dialog takes them.</summary>
    private void FocusChosen()
    {
        if (list.SelectedIndex >= 0)
        {
            list.ScrollIntoView(list.SelectedIndex);
            list.UpdateLayout();
            list.ContainerFromIndex(list.SelectedIndex)?.Focus();
        }
    }

    private static Control Row(ConflictRow row)
    {
        PathIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(row.Conflict.Path),
            Foreground = Explorer.FileIcons.BrushForFile(row.Conflict.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
        };
        TextBlock name = new() { Text = row.Conflict.Name, VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.ForegroundProperty, name.GetResourceObservable(TokenKeys.Vcs_File_Conflicted));
        TextBlock folder = new() { Text = row.Folder, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        folder.Classes.Add("muted");
        StackPanel file = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0), Children = { icon, name, folder } };
        Grid line = new() { ColumnDefinitions = new ColumnDefinitions($"*,{StatusWidth},{StatusWidth}"), Height = ListRow.Height };
        line.Children.Add(file);
        line.Children.Add(Status(row.Yours, 1));
        line.Children.Add(Status(row.Theirs, 2));
        return line;

        static TextBlock Status(string text, int column)
        {
            TextBlock status = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(status, column);
            return status;
        }
    }

    private static TextBlock Column(string text, int column)
    {
        TextBlock title = new() { Text = text, FontWeight = FontWeight.SemiBold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(column == 0 ? 6 : 8, 0, 0, 0) };
        Grid.SetColumn(title, column);
        return title;
    }

    private static Button ActionButton(string label, Func<Task> run)
    {
        // The column's buttons one width, the widest's.
        Button button = new() { Content = label, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Stretch };
        button.Click += (_, _) => IdeErrors.Observe(run());
        return button;
    }
}
