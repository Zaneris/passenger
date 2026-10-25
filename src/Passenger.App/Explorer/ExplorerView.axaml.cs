using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    public ExplorerView()
    {
        InitializeComponent();
        Rows.ItemsSource = Tree.Rows;
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
                menu.Open(Rows);
                e.Handled = true;
            }
        };
    }

    /// <summary>The row's right-click menu while it is open.</summary>
    public ContextMenu? OpenMenu { get; private set; }

    /// <summary>A file chosen with a double-click or Enter.</summary>
    public event EventHandler<string>? FileOpened;

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

    /// <summary>Reads the folder again, keeping the folders that were open open and the chosen row chosen.</summary>
    public void Refresh()
    {
        static string Key(ExplorerNode row) => (row.Kind == NodeKind.SolutionFolder ? row.SolutionPath : row.Path) ?? $"{row.Depth}/{row.Kind}/{row.Name}";
        List<(int Depth, string Key)> open = [.. Tree.Rows.Where(r => r.IsExpanded).Select(r => (r.Depth, Key(r)))];
        string? chosen = Selected is { } selected ? Key(selected) : null;
        Show(Tree.IsSolutionView);
        // Outermost first, so each opens inside one already open.
        foreach ((_, string key) in open.OrderBy(o => o.Depth))
        {
            if (Tree.Rows.FirstOrDefault(r => Key(r) == key) is { HasChildren: true, IsExpanded: false } row)
            {
                Tree.Expand(row);
            }
        }
        if (chosen is not null && Tree.Rows.FirstOrDefault(r => Key(r) == chosen) is { } again)
        {
            Rows.SelectedItem = again;
        }
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

    /// <summary>The project opened alone, which the Solution view shows at its top.</summary>
    private ProjectModel? project;

    /// <summary>Shows a workspace: its solution's view if it has one (or the project it was opened as), its folder's files
    /// otherwise.</summary>
    public void Load(WorkspaceModel workspace)
    {
        folder = workspace.Folder;
        Take(workspace);
        Show(solutionView: ViewSwitch.IsVisible);
        Hint.IsVisible = false;
        Rows.IsVisible = true;
    }

    /// <summary>Shows nothing, its hint in its place, as it did before a folder opened: the solution was closed.</summary>
    public void Unload()
    {
        folder = null;
        solution = null;
        project = null;
        ViewSwitch.IsVisible = false;
        Tree.Clear();
        Rows.IsVisible = false;
        Hint.IsVisible = true;
    }

    /// <summary>Reads again only the folders that changed on disk, the rows open and chosen kept.</summary>
    public void RefreshFolders(IEnumerable<string> folders) => Tree.RefreshFolders(folders);

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
        project = workspace.ProjectFile is null ? null : workspace.Projects.FirstOrDefault();
        ViewSwitch.IsVisible = solution is not null || project is not null;
    }

    private void Show(bool solutionView)
    {
        if (solutionView && solution is not null)
        {
            Tree.LoadSolution(solution);
            ViewName.Text = "Solution";
        }
        else if (solutionView && project is not null)
        {
            Tree.LoadProject(project);
            ViewName.Text = "Solution";
        }
        else if (folder is not null)
        {
            Tree.LoadFileSystem(folder);
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

    private void OnViewSwitchClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Show(!Tree.IsSolutionView);

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

    /// <summary>The Explorer's own items atop its header's options: Appearance, with File Nesting... while the Solution
    /// view shows.</summary>
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
            Tree.Toggle(node);
            e.Handled = true;
        }
    }

    /// <summary>A left press on a file or folder on disk may begin a drag of it, or of the rows chosen with it.</summary>
    private void OnRowsPressed(object? sender, PointerPressedEventArgs e)
    {
        if (RowAt(e.GetPosition(Rows)) is { IsMovable: true } row)
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
        // Once a drag, the pointer is the rows' until the release, wherever it goes.
        if (drag.Continue(e) && e.Pointer.Captured != Rows)
        {
            e.Pointer.Capture(Rows);
        }
    }

    private void OnRowsReleased(object? sender, PointerReleasedEventArgs e)
    {
        copyHeld = CopyHeld(e.KeyModifiers);
        if (drag.End(e))
        {
            e.Handled = true;
        }
        if (e.Pointer.Captured == Rows)
        {
            e.Pointer.Capture(null);
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
    /// it would not move (all of it there already) or a folder would go inside itself.</summary>
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
            || !copyHeld && items.All(i => PathNames.Same(Path.GetDirectoryName(i.Path!)!, into)))
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
