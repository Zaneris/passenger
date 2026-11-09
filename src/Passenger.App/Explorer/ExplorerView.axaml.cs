using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Passenger.App.Docking;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.App.Explorer;

/// <summary>Files and folders dragged onto a row: moved into the folder it stands for, or copied with the platform's
/// copy key held (Option on the Mac, Ctrl elsewhere).</summary>
public sealed record ExplorerDrop(IReadOnlyList<ExplorerNode> Items, ExplorerNode Target, bool Copy);

public partial class ExplorerView : UserControl
{
    private readonly DropOverlay drop = new();
    private readonly DragGesture<IReadOnlyList<ExplorerNode>> drag;
    private bool copyHeld;

    /// <summary>A plain left press on a file's row while one click opens files, which opens it once let go there.</summary>
    private (ExplorerNode Row, Point At)? clicked;

    /// <summary>How long typing in the filter pauses before the rows follow it.</summary>
    private static readonly TimeSpan FilterPause = TimeSpan.FromMilliseconds(150);

    private readonly TextBox filter = ToolWindows.FilterBox.Of();
    private readonly Border filterBar;
    private readonly Debounce filtering;

    /// <summary>Under the rows, how many more files a filter matched than the tree shows.</summary>
    private readonly TextBlock more = new() { Classes = { StyleClasses.EmptyText }, IsVisible = false };

    /// <summary>Filters asked for, numbering each: only the newest one's answer lands.</summary>
    private int asked;

    /// <summary>The filter whose answer landed last, by its number.</summary>
    private int answered;

    /// <summary>The newest answer landed, its files by full path, which the rows are narrowed to again as they are made
    /// anew; null while the filter is empty.</summary>
    private ExplorerFilter.Matches? matched;

    /// <summary>Down or Enter pressed in the filter before its answer landed, which then chooses the first file shown.</summary>
    private bool chooseFirst;

    public ExplorerView()
    {
        InitializeComponent();
        filter.Margin = new Thickness(6, 0, 4, 0);
        filterBar = ToolWindows.ToolBars.Top(filter);
        filterBar.IsVisible = false;
        Body.Children.Add(filterBar);
        more.Margin = new Thickness(16, 6);
        Grid.SetRow(more, 2);
        Body.Children.Add(more);
        filtering = new Debounce(FilterPause, MatchAsync);
        filter.TextChanged += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(filter.Text))
            {
                filtering.Stop();
                IdeErrors.Observe(MatchAsync());
            }
            else
            {
                filtering.Restart();
            }
        };
        filter.KeyDown += OnFilterKey;
        Rows.ItemsSource = Tree.Rows;
        ReusedRows.Use(Rows, Row);
        // Every key the keymap gives an action (Rename, Delete, the clipboard's) is the window's, run on the chosen row.
        TreeKeys.Attach<ExplorerNode>(Rows, Tree.Toggle, node =>
        {
            if (!node.IsFile || node.Path is not { } path)
            {
                return false;
            }
            FileOpened?.Invoke(this, path);
            return true;
        });
        Surface.Children.Add(drop);
        drag = new DragGesture<IReadOnlyList<ExplorerNode>>(Surface, drop, DropZoneAt, (items, at) =>
        {
            if (TargetAt(items, at) is { } target)
            {
                Dropped?.Invoke(this, new ExplorerDrop(items, target, copyHeld));
            }
        });
        // Seen before a row takes the press for its selection, which the drag leaves as it is.
        Rows.AddHandler(PointerPressedEvent, OnRowsPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerMovedEvent, OnRowsMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.AddHandler(PointerReleasedEvent, OnRowsReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Rows.ContextRequested += (_, e) =>
        {
            if (Rows.SelectedItem is ExplorerNode node && MenuFor?.Invoke(node) is { Count: > 0 } items)
            {
                ContextMenu menu = new();
                foreach (Control item in items)
                {
                    menu.Items.Add(item);
                }
                OpenMenu?.Close();
                OpenMenu = menu;
                menu.Closed += (_, _) => OpenMenu = OpenMenu == menu ? null : OpenMenu;
                if (!e.TryGetPosition(Rows, out Avalonia.Point _) && Rows.ContainerFromItem(node) is { } row)
                {
                    ToolWindows.ListMenus.PlaceForKeys(menu, row);
                }
                menu.Open(Rows);
                e.Handled = true;
            }
        };
    }

    /// <summary>The row's right-click menu while it is open.</summary>
    public ContextMenu? OpenMenu { get; private set; }

    /// <summary>A file chosen with a double-click or Enter.</summary>
    public event EventHandler<string>? FileOpened;

    /// <summary>A file clicked once while <see cref="OpensOnClick"/>: to be shown, the keys left on its row.</summary>
    public event EventHandler<string>? FileClicked;

    /// <summary>Open Files with Single Click: a plain click on a file's row opens it, as a double-click does.</summary>
    public bool OpensOnClick { get; set; }

    /// <summary>A row's right-click menu, which the window makes of its actions run on that row; none for a row with
    /// nothing to do.</summary>
    public Func<ExplorerNode, IReadOnlyList<Control>?>? MenuFor { get; set; }

    /// <summary>Files and folders dragged onto a folder, a project or a file's folder.</summary>
    public event EventHandler<ExplorerDrop>? Dropped;

    /// <summary>The row chosen, if any.</summary>
    public ExplorerNode? Selected => Rows.SelectedItem as ExplorerNode;

    /// <summary>Every row chosen: several with Shift or Cmd (Ctrl).</summary>
    public IReadOnlyList<ExplorerNode> SelectedNodes => [.. Rows.SelectedItems?.OfType<ExplorerNode>() ?? []];

    /// <summary>Chooses rows, as clicks with Cmd (Ctrl) held would.</summary>
    public void Choose(params ExplorerNode[] nodes)
    {
        Rows.SelectedItems?.Clear();
        foreach (ExplorerNode node in nodes)
        {
            Rows.SelectedItems?.Add(node);
        }
    }

    /// <summary>Reads the folder again, keeping the folders that were open open and the chosen row chosen, and the filter
    /// as it was.</summary>
    public void Refresh()
    {
        static string Key(ExplorerNode row) => (row.Kind == NodeKind.SolutionFolder ? row.SolutionPath : row.Path) ?? $"{row.Owner}/{row.Depth}/{row.Kind}/{row.Name}";
        // The rows open are those open before the filter, which it opens again over them.
        Tree.Unfilter();
        HashSet<string> open = new(Tree.Rows.Where(r => r.IsExpanded).Select(Key), StringComparer.Ordinal);
        string? chosen = Selected is { } selected ? Key(selected) : null;
        ExplorerNode? again = null;
        Show(Tree.IsSolutionView);
        // Down the rows as they open: a folder's rows come in after it, so each folder is reached once its holder is open.
        for (int at = 0; at < Tree.Rows.Count; at++)
        {
            ExplorerNode row = Tree.Rows[at];
            string key = Key(row);
            if (row is { HasChildren: true, IsExpanded: false } && open.Remove(key))
            {
                Tree.Expand(row);
            }
            if (again is null && key == chosen)
            {
                again = row;
            }
        }
        if (again is not null)
        {
            Rows.SelectedItem = again;
        }
        Narrow();
        if (revealing is { } waiting)
        {
            Reveal(waiting);
        }
    }

    /// <summary>Opens the solution folders down to one (by its path in the solution) and chooses it, or what it holds at a
    /// path, opening it for that, or when asked to.</summary>
    public void RevealSolutionFolder(string path, bool open = false, string? child = null)
    {
        if (Tree.RevealSolutionFolder(path) is not { } node)
        {
            return;
        }
        if (open || child is not null)
        {
            Tree.Expand(node);
        }
        ExplorerNode chosen = child is not null && node.Children?.FirstOrDefault(c => c.Path == child) is { } row ? row : node;
        Rows.SelectedItem = chosen;
        Rows.ScrollIntoView(chosen);
    }

    public ExplorerTree Tree { get; } = new();

    private string? folder;
    private SolutionModel? solution;

    /// <summary>The folder's projects, by which the File System view knows the folders the SDK reads.</summary>
    private IReadOnlyList<ProjectModel> projects = [];

    /// <summary>The file-based programs beside the solution, which the Solution view lists after its own.</summary>
    private IReadOnlyList<ProjectModel> programs = [];

    /// <summary>The project opened alone, which the Solution view shows at its top.</summary>
    private ProjectModel? project;

    /// <summary>Shows a workspace: its solution's view if it has one (or the project it was opened as), its folder's files
    /// otherwise.</summary>
    public void Load(WorkspaceModel workspace)
    {
        folder = workspace.Folder;
        Take(workspace);
        Show(solutionView: ViewSwitch.IsVisible);
        Rows.IsVisible = true;
        filterBar.IsVisible = true;
        ShowHint();
        Refilter();
    }

    /// <summary>Shows nothing, its hint in its place, as it did before a folder opened: the solution was closed.</summary>
    public void Unload()
    {
        folder = null;
        solution = null;
        programs = [];
        projects = [];
        project = null;
        ViewSwitch.IsVisible = false;
        Tree.Clear();
        Rows.IsVisible = false;
        filterBar.IsVisible = false;
        answered = ++asked;
        matched = null;
        more.IsVisible = false;
        ShowHint();
    }

    /// <summary>Reads again only the folders that changed on disk, the rows open and chosen kept.</summary>
    public void RefreshFolders(IEnumerable<string> folders)
    {
        Tree.RefreshFolders(folders);
        Narrow();
    }

    /// <summary>A project unloaded or loaded again: its row made anew, chosen again if it was, and every other row as it
    /// was.</summary>
    public void ShowProjectAgain(string project)
    {
        bool chosen = Selected is { Kind: NodeKind.Project, Path: { } path } && path == project;
        if (!Tree.ShowProjectsAgain([project]))
        {
            Refresh();
            return;
        }
        Narrow();
        if (chosen && Tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.Project && r.Path == project) is { } again)
        {
            Rows.SelectedItem = again;
        }
    }

    /// <summary>The folder's solution read again (a project added or changed on disk), the rows open kept.</summary>
    public void Reload(WorkspaceModel workspace)
    {
        Take(workspace);
        Refresh();
    }

    /// <summary>Projects MSBuild evaluated again: their rows show them anew, every other row as it was (open, chosen,
    /// focused), and a file waiting to be chosen is chosen once it shows.</summary>
    public void UpdateProjects(WorkspaceModel workspace, IEnumerable<ProjectModel> updated)
    {
        // A row made again takes the keys with it: they stay the Explorer's, on the row chosen, for the next Paste.
        bool keys = Rows.IsKeyboardFocusWithin;
        Take(workspace);
        Tree.UpdateProjects(updated);
        Narrow();
        if (revealing is { } waiting)
        {
            Reveal(waiting);
        }
        if (keys && !Rows.IsKeyboardFocusWithin)
        {
            FocusRows();
        }
    }

    private void Take(WorkspaceModel workspace)
    {
        solution = workspace.Solution;
        programs = [.. workspace.Programs];
        projects = workspace.Projects;
        project = workspace.ProjectFile is null ? null : workspace.Projects.FirstOrDefault();
        ViewSwitch.IsVisible = solution is not null || project is not null;
    }

    private void Show(bool solutionView)
    {
        if (solutionView && solution is not null)
        {
            Tree.LoadSolution(solution, programs);
            ViewName.Text = "Solution";
        }
        else if (solutionView && project is not null)
        {
            Tree.LoadProject(project);
            ViewName.Text = "Solution";
        }
        else if (folder is not null)
        {
            Tree.LoadFileSystem(folder, projects);
            ViewName.Text = "Files";
        }
    }

    /// <summary>Its view switch, for a tool window's header to carry, and its own title row gone: in a tool window the
    /// header names it.</summary>
    public Control TakeHeaderTools()
    {
        OwnHeader.Children.Remove(ViewSwitch);
        OwnHeader.IsVisible = false;
        return ViewSwitch;
    }

    private void OnViewSwitchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Show(!Tree.IsSolutionView);
        Narrow();
    }

    /// <summary>"Show files with the same names as nested": the Solution view nests by name as well as by
    /// DependentUpon; the rows are made again as it changes, those open kept open.</summary>
    public bool NestFiles
    {
        get => Tree.NestFiles;
        set
        {
            if (Tree.NestFiles == value)
            {
                return;
            }
            Tree.NestFiles = value;
            if (Tree.Root is not null)
            {
                Refresh();
            }
        }
    }

    /// <summary>File Nesting... chosen in the Explorer's options: the window asks how files nest.</summary>
    public event EventHandler? NestingAsked;

    /// <summary>Its view's items among its header's options: Appearance, with File Nesting... while the Solution view
    /// shows.</summary>
    public IReadOnlyList<Control> Options()
    {
        if (!Tree.IsSolutionView)
        {
            return [];
        }
        MenuItem nesting = new() { Header = "File Nesting…" };
        nesting.Click += (_, _) => NestingAsked?.Invoke(this, EventArgs.Empty);
        return [new MenuItem { Header = "Appearance", Items = { nesting } }];
    }

    /// <summary>Gives the tree the keys on its chosen row (the first when none is): the view itself cannot take the
    /// focus, only a row.</summary>
    public void FocusRows()
    {
        if (!Rows.IsVisible || Tree.Rows.Count == 0)
        {
            return;
        }
        if (Rows.SelectedIndex < 0)
        {
            Rows.SelectedIndex = 0;
        }
        int index = Rows.SelectedIndex;
        // A row scrolled out of sight, or in a window just shown, has no container until it is laid out in view.
        Rows.ScrollIntoView(index);
        Rows.UpdateLayout();
        Rows.ContainerFromIndex(index)?.Focus();
    }

    /// <summary>The folder's indexed files by their paths from its root, as the window's index has them now, which the
    /// filter reads off the window's thread: the index leaves out build output and what the walks pass over.</summary>
    public Func<(string Root, IReadOnlyList<string> Files)?>? IndexedFiles { get; set; }

    /// <summary>The filter's text: the rows narrowed to the files whose names hold it, with their folders.</summary>
    public string FilterText
    {
        get => filter.Text ?? string.Empty;
        set => filter.Text = value;
    }

    /// <summary>What the line under the rows says of the files the filter matched beyond those the tree shows; null while
    /// it shows them all.</summary>
    public string? MoreMatched => more.IsVisible ? more.Text : null;

    /// <summary>Gives the filter the keys, its text chosen: Find's keys while the Explorer has them.</summary>
    public void FocusFilter()
    {
        filter.Focus();
        filter.SelectAll();
    }

    /// <summary>The filter matched again among the folder's files as they now are (walked, or files came and went), while
    /// it has text.</summary>
    public void Refilter()
    {
        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            IdeErrors.Observe(MatchAsync());
        }
    }

    /// <summary>The files the filter's text names found off the window's thread, then the rows narrowed to them, unless a
    /// newer filter was asked for meanwhile; its text gone, every row again as it was open before, the row chosen meanwhile
    /// shown where it is.</summary>
    private async Task MatchAsync()
    {
        int ask = ++asked;
        string text = FilterText.Trim();
        if (text.Length == 0)
        {
            answered = ask;
            matched = null;
            chooseFirst = false;
            Unnarrow();
            return;
        }
        ExplorerFilter.Matches found = new([], 0);
        if (IndexedFiles?.Invoke() is ({ } root, { } files))
        {
            found = await BackgroundWork.Run(() =>
            {
                ExplorerFilter.Matches named = ExplorerFilter.Of(files, text);
                return named with { Files = [.. named.Files.Select(f => PathNames.Canonical(Path.Combine(root, f)))] };
            });
        }
        if (ask != asked)
        {
            return;
        }
        answered = ask;
        matched = found;
        Narrow();
        if (chooseFirst)
        {
            chooseFirst = false;
            ChooseFirstFile();
        }
    }

    /// <summary>The rows narrowed to the files of the answer landed, as the tree now is; nothing while none has.</summary>
    private void Narrow()
    {
        if (matched is not { } found)
        {
            return;
        }
        Tree.Filter(found.Files);
        more.Text = $"{found.More:N0} more {Plural.Noun(found.More, "file")} {Plural.Agreeing(found.More, "matches", "match")}: a longer filter narrows them";
        more.IsVisible = found.More > 0;
        ShowHint();
    }

    /// <summary>Every row again, as it was open before the filter, the row chosen meanwhile kept in sight.</summary>
    private void Unnarrow()
    {
        more.IsVisible = false;
        if (!Tree.IsFiltered)
        {
            return;
        }
        string? chosen = Selected?.Path;
        Tree.Unfilter();
        if (chosen is not null && Selected is null)
        {
            Reveal(chosen);
        }
        else if (Selected is { } still)
        {
            Rows.ScrollIntoView(still);
        }
        ShowHint();
    }

    /// <summary>The first file shown chosen, where no row is.</summary>
    private void ChooseFirstFile()
    {
        if (Selected is null && Tree.Rows.FirstOrDefault(r => r.IsFile) is { } first)
        {
            Rows.SelectedItem = first;
        }
    }

    /// <summary>The hint over the rows: nothing open, or a filter that leaves no file.</summary>
    private void ShowHint()
    {
        bool none = Rows.IsVisible && Tree.IsFiltered && !Tree.Rows.Any(r => r.IsFile);
        Hint.Text = none ? "No file matches" : "Nothing open";
        Hint.IsVisible = none || !Rows.IsVisible;
    }

    /// <summary>The filter's keys: Down or Enter takes the rows' keys, on the row chosen or the first file shown; Escape
    /// empties it and gives the rows their keys back.</summary>
    private void OnFilterKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || e.Key is not (Key.Down or Key.Enter or Key.Escape))
        {
            return;
        }
        if (e.Key == Key.Escape)
        {
            filter.Text = string.Empty;
        }
        else if (filtering.IsWaiting || answered != asked)
        {
            // The answer is yet to land: the first file it shows is chosen once it does.
            chooseFirst = true;
            if (filtering.IsWaiting)
            {
                filtering.Stop();
                IdeErrors.Observe(MatchAsync());
            }
        }
        else
        {
            ChooseFirstFile();
        }
        FocusRows();
        e.Handled = true;
    }

    /// <summary>Opens the folders down to a file and selects its row. A file the Solution view does not show yet (one just
    /// made, moved or copied into a project, before MSBuild has evaluated the project again) is chosen once it does.</summary>
    public void Reveal(string path)
    {
        if (Tree.Reveal(path) is { } node)
        {
            revealing = null;
            Rows.SelectedItem = node;
            Rows.ScrollIntoView(node);
        }
        else
        {
            revealing = Tree.IsSolutionView && (File.Exists(path) || Directory.Exists(path)) ? path : null;
        }
    }

    /// <summary>A file or folder to choose once the Solution view shows it.</summary>
    private string? revealing;

    /// <summary>Chooses a file's or folder's row and scrolls to it, as Select Opened File asks: in the Solution view, else in
    /// the Files view, switched to, where only the folder holds it; false, the view left as it was, where neither shows it.</summary>
    public bool Locate(string path)
    {
        string full = PathNames.Canonical(path);
        ExplorerNode? node = Tree.Reveal(full);
        if (node is null && Tree.IsSolutionView && folder is not null && PathNames.IsUnder(full, folder) && (File.Exists(full) || Directory.Exists(full)))
        {
            Show(solutionView: false);
            node = Tree.Reveal(full);
        }
        if (node is null)
        {
            return false;
        }
        revealing = null;
        Rows.SelectedItem = node;
        Rows.ScrollIntoView(node);
        return true;
    }

    private void Activate(ExplorerNode node)
    {
        if (node.IsFile && node.Path is { } path)
        {
            FileOpened?.Invoke(this, path);
        }
        else if (node.HasChildren)
        {
            Tree.Toggle(node);
        }
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Rows.SelectedItem is ExplorerNode node)
        {
            Activate(node);
            e.Handled = true;
        }
    }

    private void OnChevronPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ExplorerNode { HasChildren: true } node })
        {
            clicked = null;
            Tree.Toggle(node);
            e.Handled = true;
        }
    }

    /// <summary>A row for whichever file or folder the list hands its container: its chevron, icon, name and detail.</summary>
    private Control Row()
    {
        RowGlyphs glyphs = new();
        glyphs.ChevronPressed += OnChevronPressed;
        TextBlock name = new() { VerticalAlignment = VerticalAlignment.Center };
        TextBlock detail = new() { Classes = { StyleClasses.Muted }, FontSize = 12, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        StackPanel row = new() { Orientation = Orientation.Horizontal, Height = ListRow.Height, Children = { glyphs, name, detail } };
        return row.Showing<ExplorerNode>(node =>
        {
            row.Margin = node.Indent;
            row.Opacity = node.Opacity;
            ToolTip.SetTip(row, node.Tip);
            glyphs.HasChevron = node.HasChildren;
            glyphs.Turned = node.IsExpanded;
            glyphs.Icon = node.Icon;
            glyphs.IconBrush = node.IconBrush;
            name.Text = node.Name;
            name.Classes.Set(StyleClasses.VcsModified, node.IsVcsModified);
            name.Classes.Set(StyleClasses.VcsAdded, node.IsVcsAdded);
            name.Classes.Set(StyleClasses.VcsUnversioned, node.IsVcsUnversioned);
            name.Classes.Set(StyleClasses.VcsConflicted, node.IsVcsConflicted);
            detail.Text = node.Detail;
        });
    }

    /// <summary>A left press on a file or folder on disk may begin a drag of it, or of the rows chosen with it, and a plain
    /// one on a file a click that opens it (<see cref="OpensOnClick"/>). The row is the one pressed on, never one the
    /// scrollbar lies over, so a drag of the bar's thumb stays the bar's.</summary>
    private void OnRowsPressed(object? sender, PointerPressedEventArgs e)
    {
        ExplorerNode? pressed = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ExplorerNode;
        PointerPoint point = e.GetCurrentPoint(Rows);
        // Shift and Cmd (Ctrl) choose rows, and a second press is a double-click's.
        clicked = OpensOnClick && pressed is { IsFile: true, Path: not null } && point.Properties.IsLeftButtonPressed
            && e.KeyModifiers == KeyModifiers.None && e.ClickCount == 1 ? (pressed, point.Position) : null;
        if (pressed is { IsMovable: true } row)
        {
            // The rows chosen as it was pressed: the list then chooses the row alone.
            IReadOnlyList<ExplorerNode> chosen = SelectedNodes;
            drag.Begin(chosen.Contains(row) ? [.. chosen.Where(n => n.IsMovable)] : [row], e);
        }
    }

    private void OnRowsMoved(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(Rows).Properties.IsLeftButtonPressed)
        {
            // Released outside the rows before it became a drag, which they never heard.
            drag.Cancel();
            return;
        }
        copyHeld = CopyHeld(e.KeyModifiers);
        if (clicked is { } press && ((Vector)(e.GetPosition(Rows) - press.At)).Length >= DragGesture<IReadOnlyList<ExplorerNode>>.Threshold)
        {
            clicked = null;
        }
        // Once a drag, the pointer is the rows' until the release, wherever it goes.
        if (drag.Continue(e) && e.Pointer.Captured != Rows)
        {
            e.Pointer.Capture(Rows);
        }
    }

    private void OnRowsReleased(object? sender, PointerReleasedEventArgs e)
    {
        copyHeld = CopyHeld(e.KeyModifiers);
        bool dragged = drag.End(e);
        if (dragged)
        {
            e.Handled = true;
        }
        if (e.Pointer.Captured == Rows)
        {
            e.Pointer.Capture(null);
        }
        if (clicked is { } press)
        {
            clicked = null;
            if (!dragged && e.InitialPressMouseButton == MouseButton.Left && RowAt(e.GetPosition(Rows)) == press.Row && press.Row.Path is { } path)
            {
                FileClicked?.Invoke(this, path);
            }
        }
    }

    /// <summary>The platform's key for a copy rather than a move as files are dropped: Option on the Mac, Ctrl elsewhere.</summary>
    private static bool CopyHeld(KeyModifiers modifiers) => modifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Alt : KeyModifiers.Control);

    /// <summary>The row drawn under a point of the list.</summary>
    private ExplorerNode? RowAt(Point inRows)
    {
        foreach (Control container in Rows.GetRealizedContainers())
        {
            if (Rows.TranslatePoint(inRows, container) is { } at && new Rect(container.Bounds.Size).Contains(at))
            {
                return container.DataContext as ExplorerNode;
            }
        }
        return null;
    }

    /// <summary>The folder or project what is dragged would go into at a point: the one under it, or a file's; none where
    /// it would not move (all of it there already), a folder would go inside itself, or a folder the SDK reads would move.</summary>
    private ExplorerNode? TargetAt(IReadOnlyList<ExplorerNode> items, Point at)
    {
        if (Surface.TranslatePoint(at, Rows) is not { } inRows || RowAt(inRows) is not { } row)
        {
            return null;
        }
        // A file's folder holds what is dropped on it, past the file a nested one is shown under.
        ExplorerNode? target = row;
        while (target is { Kind: NodeKind.File })
        {
            target = Tree.Rows.Holder(target);
        }
        if (target?.Into is not { } into)
        {
            return null;
        }
        if (items.Any(i => i.Kind == NodeKind.Folder && PathNames.Within(into, i.Path!))
            || !copyHeld && (items.All(i => PathNames.Same(Path.GetDirectoryName(i.Path!)!, into)) || items.Any(i => i.Kept is not null)))
        {
            return null;
        }
        return target;
    }

    /// <summary>Where a drop lights up: the folder's row with the rows under it that show, as one place.</summary>
    private DropZone? DropZoneAt(IReadOnlyList<ExplorerNode> items, Point at)
    {
        if (TargetAt(items, at) is not { } target)
        {
            return null;
        }
        int first = Tree.Rows.IndexOf(target);
        int last = first;
        while (last + 1 < Tree.Rows.Count && Tree.Rows[last + 1].Depth > target.Depth)
        {
            last++;
        }
        Rect? place = null;
        foreach (Control container in Rows.GetRealizedContainers())
        {
            int index = Rows.IndexFromContainer(container);
            if (index >= first && index <= last && container.TranslatePoint(default, Surface) is { } corner)
            {
                Rect bounds = new(corner, container.Bounds.Size);
                place = place is { } so ? so.Union(bounds) : bounds;
            }
        }
        return place is { } lit ? new DropZone(lit.Intersect(new Rect(Surface.Bounds.Size))) : null;
    }
}
