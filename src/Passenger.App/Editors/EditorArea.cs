using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.App.Docking;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Editors;

/// <summary>The editors, as Rider's: one group of tabs, or several split right and down, one of them active (the one
/// the person works in, whose editor <see cref="CodeEditor"/> is). A tab dragged onto a group's edge splits it there,
/// onto its middle or its tab strip moves into it, and out of the editors into a window of its own (<see
/// cref="EditorWindow"/>), whose groups are the area's too; a group whose last tab closes goes, and its neighbour takes
/// its room. A file shows in as many groups as it is open in, in any window, over one text.</summary>
public sealed partial class EditorArea : UserControl
{
    private readonly ObservableCollection<OpenDocument> documents = [];
    private readonly ContentControl emptyState = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly StackPanel folderHint;
    private readonly TextBlock emptyTitle;
    private readonly TextBlock emptyHint;
    private readonly List<Action<CodeEditor>> editorHooks = [];
    private readonly Dictionary<OpenDocument, int> held = [];

    /// <summary>The main window's editors, then each editor window's in the order they opened.</summary>
    private readonly List<EditorFrame> frames = [];
    private readonly EditorFrame main;
    private EditorGroup activeGroup;

    public EditorArea()
    {
        Commands = new CommandHistory(this);
        activeGroup = NewGroup();
        activeGroup.IsCurrent = true;
        emptyTitle = new TextBlock { Text = "Passenger", FontSize = 28, FontWeight = FontWeight.SemiBold };
        emptyTitle.Bind(TextBlock.ForegroundProperty, emptyTitle.GetResourceObservable("Passenger.Text"));
        emptyHint = new TextBlock { Classes = { "muted" }, Text = "Open a solution, project or folder to start." };
        folderHint = new StackPanel { Classes = { "emptyState" }, Spacing = 10, Children = { emptyTitle, emptyHint } };
        emptyState.Content = folderHint;
        main = NewFrame(new EditorFrame.Leaf(activeGroup), activeGroup, emptyState);
        Content = main.Surface;
        Rebuild(main);
        AttachedToVisualTree += (_, _) => FollowHost();
    }

    public event EventHandler? ActiveChanged;

    /// <summary>A tab's menu opening, for the window to fill with its tab actions, run on that tab.</summary>
    public event EventHandler<TabMenu>? TabMenuOpening;

    /// <summary>A file opened in a tab, before it is shown; and one closed, its last tab gone and nothing holding it.</summary>
    public event EventHandler<OpenDocument>? Opened;

    public event EventHandler<OpenDocument>? Closed;
    public event EventHandler? CaretMoved;

    /// <summary>A file that could not be written, and why: it stays open with its edits.</summary>
    public event EventHandler<string>? SaveFailed;

    /// <summary>A file not written because something else changed it on disk since it was last loaded or saved: which
    /// to keep is the person's question (File Cache Conflict), never settled by writing over the disk's.</summary>
    public event EventHandler<OpenDocument>? SaveConflicted;

    /// <summary>A file or folder moved through <see cref="MoveFile"/>, with the open files that went with it.</summary>
    public event EventHandler<FileMove>? Moved;

    /// <summary>The commands that changed several files at once or moved one (a rename), which Undo takes back whole.</summary>
    public CommandHistory Commands { get; }

    /// <summary>The active group's editor: the one keys and the IDE's actions go to.</summary>
    public CodeEditor CodeEditor => activeGroup.Editor;

    /// <summary>The file the active group shows.</summary>
    public OpenDocument? Active => activeGroup.Active;

    /// <summary>The file the main window's editors show (their group last worked in), whichever window is in front.</summary>
    public OpenDocument? ShownInMain => main.Current.Active;

    /// <summary>Every file open, in any group, once each.</summary>
    public IReadOnlyList<OpenDocument> Documents => documents;

    /// <summary>Every group of tabs: the main window's, then each editor window's.</summary>
    public IReadOnlyList<EditorGroup> Groups => [.. AllGroups];

    public EditorGroup ActiveGroup => activeGroup;

    public FindBar FindBar => activeGroup.Find;

    /// <summary>The layer over the active group's window of editors that completion and the code actions' popups sit on.</summary>
    public Canvas AssistLayer => ActiveFrame.Assist;

    private IEnumerable<EditorGroup> AllGroups => frames.SelectMany(f => f.Groups);

    private EditorFrame ActiveFrame => FrameOf(activeGroup) ?? main;

    private EditorFrame? FrameOf(EditorGroup group) => frames.FirstOrDefault(f => f.Groups.Contains(group));

    /// <summary>The drag of a group's tabs, which its frame's surface measures.</summary>
    internal DragGesture<(EditorGroup Group, OpenDocument Document)>? TabDragOf(EditorGroup group) => FrameOf(group)?.TabDrag;

    /// <summary>Runs for every editor there is and every one a split makes, as wiring for its events.</summary>
    public void ForEachEditor(Action<CodeEditor> hook)
    {
        editorHooks.Add(hook);
        foreach (EditorGroup group in AllGroups)
        {
            hook(group.Editor);
        }
    }

    /// <summary>What the area shows with no file open and no folder: the Welcome screen, before one opens and once it
    /// closes.</summary>
    public void ShowWelcome(Control welcome)
    {
        folderName = null;
        emptyState.Content = welcome;
        TitleWindows();
    }

    /// <summary>The empty area's words once a folder is open: it has files now, just none showing.</summary>
    public void ShowFolderHint(string folder)
    {
        folderName = Path.GetFileName(folder);
        emptyTitle.Text = folderName;
        emptyHint.Text = "Open a file from the Explorer.";
        emptyState.Content = folderHint;
        TitleWindows();
    }

    /// <summary>Opens a file in a tab of the active group (or shows its tab if open), returning why not if it can't be.</summary>
    public string? Open(string path)
    {
        string full = PathNames.Canonical(path);
        if (documents.FirstOrDefault(d => d.Path == full) is { } open)
        {
            Activate(open);
            return null;
        }
        FileText? file;
        try
        {
            file = FileText.Load(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
        if (file is null)
        {
            return $"{Path.GetFileName(full)} looks like a binary file.";
        }
        OpenDocument document = new(full, file);
        Opened?.Invoke(this, document);
        documents.Add(document);
        activeGroup.Add(document);
        Activate(document);
        return null;
    }

    /// <summary>Shows a file in the window in front: in the active group if it has its tab, else in the group there that
    /// does, else in a new tab of the active group, as Rider opens a file in the window it was asked for in.</summary>
    public void Activate(OpenDocument document)
    {
        EditorGroup group = activeGroup.Has(document) ? activeGroup : ActiveFrame.Groups.FirstOrDefault(g => g.Has(document)) ?? activeGroup;
        group.Show(document);
        SetActiveGroup(group);
        Refresh();
        group.Editor.Focus();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes a file's tabs everywhere, saved first as Rider never asks; false, the file left open, when it could
    /// not be written. A file something else holds (<see cref="Hold"/>) stays open without a tab until let go.</summary>
    public bool Close(OpenDocument document) => Close(document, forGood: false);

    /// <summary>Closes a file deleted from disk, its edits with it, even while held: what held it hears it closed.</summary>
    public void CloseDeleted(OpenDocument document)
    {
        document.Text.MarkSaved();
        Close(document, forGood: true);
    }

    /// <summary>Keeps an open file open while something beside its tabs edits it (a diff's right side): its tabs may close,
    /// and it is still saved as every open file is, then closed once let go with no tab. Closed for good meanwhile
    /// (deleted, another folder opened), <see cref="Closed"/> says so.</summary>
    public IDisposable Hold(OpenDocument document)
    {
        held[document] = held.GetValueOrDefault(document) + 1;
        return new FileHold(this, document);
    }

    /// <summary>A file's document for something beside the tabs to show and edit (Local History's diff): the open one, else
    /// one read from disk with no tab, held (<see cref="Hold"/>) until the hold is disposed. Null when it cannot be read as
    /// text.</summary>
    public (OpenDocument Document, IDisposable Hold)? Borrow(string path) => Load(path) is { } document ? (document, Hold(document)) : null;

    /// <summary>Names a change made across files through these editors (a refactoring's), for Local History: held while
    /// the change is made.</summary>
    public Func<IDisposable?>? Recording { get; set; }

    private bool Close(OpenDocument document, bool forGood)
    {
        if (document.IsModified && !TrySave(document))
        {
            return false;
        }
        foreach (EditorGroup group in AllGroups.Where(g => g.Has(document)).ToList())
        {
            group.Remove(document);
        }
        if (forGood)
        {
            held.Remove(document);
        }
        if (!held.ContainsKey(document))
        {
            documents.Remove(document);
            Closed?.Invoke(this, document);
            document.Close();
        }
        Collapse();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void LetGo(OpenDocument document)
    {
        if (!held.TryGetValue(document, out int count))
        {
            return;
        }
        if (count > 1)
        {
            held[document] = count - 1;
            return;
        }
        held.Remove(document);
        // A file that cannot be written now stays open, its edits shown in a tab again.
        if (documents.Contains(document) && !AllGroups.Any(g => g.Has(document)) && !Close(document))
        {
            Activate(document);
        }
    }

    private sealed class FileHold(EditorArea area, OpenDocument document) : IDisposable
    {
        private bool released;

        public void Dispose()
        {
            if (!released)
            {
                released = true;
                area.LetGo(document);
            }
        }
    }

    /// <summary>Closes a tab of one group: the file stays open if another group has it, in any window. Reopen Closed Tab
    /// brings it back where it stood.</summary>
    public bool CloseTab(EditorGroup group, OpenDocument document)
    {
        if (!group.Has(document))
        {
            return true;
        }
        ClosedTab closing = new(document.Path, new WeakReference<EditorGroup>(group), group.Tabs.Select(t => t.Document).ToList().IndexOf(document),
            document.Text.Snapshot.GetPosition(Math.Min(group.SelectionsOf(document).Primary.Caret, document.Text.Snapshot.Length)),
            group.ScrollOf(document), group.IsPinned(document));
        if (AllGroups.Count(g => g.Has(document)) > 1)
        {
            group.Remove(document);
            Collapse();
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!Close(document))
        {
            return false;
        }
        closedTabs.Add(closing);
        if (closedTabs.Count > ClosedTabsKept)
        {
            closedTabs.RemoveAt(0);
        }
        return true;
    }

    /// <summary>Rider's Close Other Tabs: every tab of the group but this one and the pinned ones.</summary>
    public void CloseOthers(EditorGroup group, OpenDocument keep) => CloseTabs(group, (tab, _) => tab.Document != keep);

    /// <summary>Rider's Close All Tabs: every tab of the group but the pinned ones.</summary>
    public void CloseAllTabs(EditorGroup group) => CloseTabs(group, (_, _) => true);

    /// <summary>Rider's Close Tabs to the Left or to the Right of a tab, the pinned ones left open.</summary>
    public void CloseToSide(EditorGroup group, OpenDocument of, bool left)
    {
        int at = group.Tabs.Select(t => t.Document).ToList().IndexOf(of);
        CloseTabs(group, (_, index) => left ? index < at : index > at);
    }

    /// <summary>Rider's Close All but Pinned: the group's unpinned tabs, or every group's in every window.</summary>
    public void CloseUnpinned(EditorGroup? group)
    {
        foreach (EditorGroup each in group is null ? AllGroups.ToList() : [group])
        {
            if (FrameOf(each) is not null && !CloseTabs(each, (_, _) => true))
            {
                return;
            }
        }
    }

    /// <summary>The unpinned tabs of a group that would close, by their place along its strip.</summary>
    public static IReadOnlyList<OpenDocument> Unpinned(EditorGroup group, Func<EditorTab, int, bool> which) =>
        [.. group.Tabs.Select((tab, index) => (tab, index)).Where(t => !t.tab.IsPinned && which(t.tab, t.index)).Select(t => t.tab.Document)];

    /// <summary>Closes the unpinned tabs chosen, each saved first; false, the rest left open, at one that could not be.</summary>
    private bool CloseTabs(EditorGroup group, Func<EditorTab, int, bool> which)
    {
        foreach (OpenDocument each in Unpinned(group, which))
        {
            if (!CloseTab(group, each))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>How many closed tabs Reopen Closed Tab can bring back.</summary>
    private const int ClosedTabsKept = 50;

    /// <summary>The tabs closed, the latest last: each file, the group it was in (held weakly, so a split closed since
    /// goes), its place there, its caret and scroll, and whether it was pinned.</summary>
    private readonly List<ClosedTab> closedTabs = [];

    private sealed record ClosedTab(string Path, WeakReference<EditorGroup> Group, int Index, TextPosition Caret, Vector Scroll, bool Pinned);

    /// <summary>Whether Reopen Closed Tab has a tab to bring back.</summary>
    public bool CanReopen => closedTabs.Count > 0;

    /// <summary>Rider's Reopen Closed Tab: the tab closed last opens again where it stood, in its group if that is still
    /// there (else the active one), pinned if it was, its caret and scroll as they were. Null once one is open, else why
    /// not: none was closed, or none of those closed can be read any more.</summary>
    public string? ReopenClosedTab()
    {
        while (closedTabs.Count > 0)
        {
            ClosedTab closed = closedTabs[^1];
            closedTabs.RemoveAt(closedTabs.Count - 1);
            if (Load(closed.Path) is not { } document)
            {
                continue;
            }
            EditorGroup group = closed.Group.TryGetTarget(out EditorGroup? stood) && FrameOf(stood) is not null ? stood : activeGroup;
            if (!group.Has(document))
            {
                group.Add(document, closed.Index, closed.Pinned);
                TextSnapshot text = document.Text.Snapshot;
                TextPosition at = new(Math.Clamp(closed.Caret.Line, 0, text.LineCount - 1), Math.Max(0, closed.Caret.Column));
                group.Remember(document, SelectionSet.Single(text.GetOffset(at)), closed.Scroll);
            }
            group.Show(document);
            SetActiveGroup(group);
            Refresh();
            group.Editor.Focus();
            ActiveChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        return "No closed tab can be opened again";
    }

    /// <summary>Opens a file in a new group beside one (right for horizontal, below for vertical), as Rider's Split
    /// Right and Split Down; with <paramref name="move"/> its tab leaves the first group.</summary>
    public void Split(EditorGroup beside, OpenDocument document, Orientation orientation, bool move, bool before = false)
    {
        EditorFrame frame = FrameOf(beside) ?? main;
        EditorGroup created = NewGroup();
        frame.Root = frame.Root.Replace(beside, leaf => new EditorFrame.SplitNode(orientation, before ? new EditorFrame.Leaf(created) : leaf, before ? leaf : new EditorFrame.Leaf(created)));
        created.Add(document, pinned: move && beside.IsPinned(document));
        created.Show(document);
        if (move)
        {
            beside.Remove(document);
        }
        Rebuild(frame);
        Collapse();
        SetActiveGroup(created);
        created.Editor.Focus();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves a tab to another group, in this window or another, at a place on its strip or after its active tab,
    /// pinned there if it was.</summary>
    public void Move(EditorGroup from, OpenDocument document, EditorGroup to, int? index = null)
    {
        if (from == to)
        {
            if (index is int at)
            {
                from.Reorder(document, at);
            }
            return;
        }
        to.Add(document, index, from.IsPinned(document));
        to.Show(document);
        from.Remove(document);
        Collapse();
        SetActiveGroup(to);
        to.Editor.Focus();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rider's Move to Opposite Group: the tab goes to the group beside its own in its window, if there is one.</summary>
    public void MoveToOpposite(EditorGroup group, OpenDocument document)
    {
        List<EditorGroup> all = [.. (FrameOf(group) ?? main).Groups];
        if (all.Count > 1)
        {
            Move(group, document, all[(all.IndexOf(group) + 1) % all.Count]);
        }
    }

    /// <summary>The group the person works in, from a click or the focus, and so the group last worked in in its window.</summary>
    public void SetActiveGroup(EditorGroup group)
    {
        if (group == activeGroup || FrameOf(group) is not { } frame)
        {
            return;
        }
        activeGroup.Keep();
        activeGroup.IsCurrent = false;
        activeGroup = group;
        frame.Current = group;
        group.IsCurrent = true;
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void OpenTabMenu(EditorGroup group, OpenDocument document, ContextMenu menu) => TabMenuOpening?.Invoke(this, new TabMenu(group, document, menu));

    /// <summary>A tab of a group was shown by a click on it.</summary>
    internal void Activated(EditorGroup group)
    {
        Refresh();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the find bar over the active file, with the replace row if asked.</summary>
    public void ShowFind(bool replace)
    {
        if (Active is not null)
        {
            activeGroup.Find.Open(replace);
        }
    }

    public void SaveActive()
    {
        if (Active is { } active)
        {
            TrySave(active);
        }
    }

    /// <summary>Saves every changed file, but those <paramref name="waiting"/> on a question; whether all were written.</summary>
    public bool SaveAll(IReadOnlySet<OpenDocument>? waiting = null)
    {
        bool all = true;
        foreach (OpenDocument document in documents.Where(d => d.IsModified && waiting?.Contains(d) != true).ToList())
        {
            all &= TrySave(document);
        }
        return all;
    }

    /// <summary>Writes a file even over a version on disk newer than its own, as Keep Memory Changes does.</summary>
    public bool SaveOver(OpenDocument document) => TrySave(document, overDisk: true);

    /// <summary>Writes a file, unless something else changed it on disk meanwhile; whether it was written.</summary>
    public bool Save(OpenDocument document) => TrySave(document);

    /// <summary>Moves a file or folder on disk with its open files, which keep their tabs in every split, their undo
    /// and carets: each is saved first, so what moves is what the editor shows. Null when it moved, else why not, with
    /// nothing moved.</summary>
    public string? MoveFile(string from, string to)
    {
        string source = PathNames.Canonical(from);
        List<OpenDocument> moving = [.. documents.Where(d => PathNames.Within(d.Path, source))];
        foreach (OpenDocument document in moving)
        {
            if (document.IsModified && !TrySave(document))
            {
                return $"{document.Name} could not be saved before it moved";
            }
        }
        try
        {
            if (Directory.Exists(from))
            {
                Directory.Move(from, to);
            }
            else
            {
                File.Move(from, to);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
        string target = PathNames.Canonical(to);
        foreach (OpenDocument document in moving)
        {
            document.MoveTo(PathNames.Moved(document.Path, source, target));
        }
        Moved?.Invoke(this, new FileMove(source, target, moving));
        TitleWindows();
        if (Active is { } shown && moving.Contains(shown))
        {
            // What names the file shown (the title, the status bar's path, the structure) reads it again.
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
        return null;
    }

    private bool TrySave(OpenDocument document, bool overDisk = false)
    {
        if (!overDisk && document.ChangedOnDisk())
        {
            SaveConflicted?.Invoke(this, document);
            return false;
        }
        try
        {
            // The carets of the editor showing it, whose lines keep the whitespace a save would trim.
            EditorGroup? showing = activeGroup.Has(document) ? activeGroup : AllGroups.FirstOrDefault(g => g.Has(document));
            document.Save(showing?.SelectionsOf(document));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SaveFailed?.Invoke(this, $"{document.Name} could not be saved: {error.Message}");
            return false;
        }
    }

    /// <summary>The main window's splits, each group's tabs with their carets and the one shown, for the folder to open
    /// on next time; the editor windows' are <see cref="CaptureWindows"/>.</summary>
    public EditorLayout Capture()
    {
        activeGroup.Keep();
        return Capture(main.Root, main.Current);
    }

    private static EditorLayout Capture(EditorFrame.Node node, EditorGroup current)
    {
        if (node is EditorFrame.SplitNode split)
        {
            return new EditorLayout
            {
                Split = split.Orientation == Orientation.Horizontal ? "right" : "down",
                Ratio = split.Ratio,
                First = Capture(split.First, current),
                Second = Capture(split.Second, current),
            };
        }
        EditorGroup group = ((EditorFrame.Leaf)node).Group;
        return new EditorLayout
        {
            Tabs = [.. group.Tabs.Select(t =>
            {
                TextPosition caret = t.Document.Text.Snapshot.GetPosition(Math.Min(group.SelectionsOf(t.Document).Primary.Caret, t.Document.Text.Snapshot.Length));
                return new KeptTab(t.Document.Path, caret.Line, caret.Column) { Pinned = t.IsPinned };
            })],
            Shown = group.Active is { } shown ? group.Tabs.Select(t => t.Document).ToList().IndexOf(shown) : 0,
            Current = group == current,
        };
    }

    /// <summary>Opens what a folder kept, into an area with nothing open: the main window's splits and the editor windows
    /// where they stood, each group's tabs with their carets and the one shown. A file gone from disk is left out, and a
    /// group left with none goes, a window with it.</summary>
    public void Restore(EditorLayout? layout, IReadOnlyList<KeptEditorWindow>? windows = null)
    {
        if (documents.Count > 0)
        {
            return;
        }
        bool restored = false;
        if (layout is not null && Restore(layout, out EditorGroup? current) is { } tree)
        {
            activeGroup.IsCurrent = false;
            main.Root = tree;
            activeGroup = current ?? main.Groups.First();
            main.Current = activeGroup;
            activeGroup.IsCurrent = true;
            Rebuild(main);
            activeGroup.Editor.Focus();
            restored = true;
        }
        restored |= RestoreWindows(windows ?? []);
        if (restored)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private EditorFrame.Node? Restore(EditorLayout kept, out EditorGroup? current)
    {
        current = null;
        if (kept.Split is { } direction && kept.First is { } firstKept && kept.Second is { } secondKept)
        {
            EditorFrame.Node? first = Restore(firstKept, out EditorGroup? inFirst);
            EditorFrame.Node? second = Restore(secondKept, out EditorGroup? inSecond);
            current = inFirst ?? inSecond;
            return first is null ? second : second is null ? first
                : new EditorFrame.SplitNode(direction == "down" ? Orientation.Vertical : Orientation.Horizontal, first, second) { Ratio = Math.Clamp(kept.Ratio, 0.05, 0.95) };
        }
        EditorGroup group = NewGroup();
        OpenDocument? shown = null;
        TextPosition shownAt = default;
        foreach ((KeptTab tab, int index) in (kept.Tabs ?? []).Select((t, i) => (t, i)))
        {
            if (Load(tab.Path) is not { } document)
            {
                continue;
            }
            group.Add(document, group.Tabs.Count, tab.Pinned);
            TextPosition at = new(Math.Clamp(tab.Line, 0, document.Text.Snapshot.LineCount - 1), Math.Max(0, tab.Column));
            group.Remember(document, SelectionSet.Single(document.Text.Snapshot.GetOffset(at)));
            if (index == kept.Shown || shown is null)
            {
                (shown, shownAt) = (document, at);
            }
        }
        if (shown is null)
        {
            return null;
        }
        group.Show(shown);
        group.Editor.GoTo(shownAt);
        current = kept.Current ? group : null;
        return new EditorFrame.Leaf(group);
    }

    /// <summary>A file's document, open already or read from disk now; null when it cannot be read as text.</summary>
    private OpenDocument? Load(string path)
    {
        string full = PathNames.Canonical(path);
        if (documents.FirstOrDefault(d => d.Path == full) is { } open)
        {
            return open;
        }
        try
        {
            if (FileText.Load(full) is not { } file)
            {
                return null;
            }
            OpenDocument document = new(full, file);
            Opened?.Invoke(this, document);
            documents.Add(document);
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Closes every file, those held too, each saved first; false when one could not be, and stays open. The
    /// editor windows go with their last tabs, and the tabs Reopen Closed Tab had, which were the folder's.</summary>
    public bool CloseAll()
    {
        foreach (OpenDocument document in documents.ToList())
        {
            if (!Close(document, forGood: true))
            {
                return false;
            }
        }
        closedTabs.Clear();
        return true;
    }

    /// <summary>Whether a group's window of editors is split, so Unsplit has something to join.</summary>
    public bool IsSplit(EditorGroup group) => (FrameOf(group)?.Groups.Count() ?? 0) > 1;

    /// <summary>Rider's Unsplit: the split a group is in goes, the tabs of the other side joining the group's after its
    /// own, each with its carets and pin; with <paramref name="all"/> (Unsplit All) every split of its window goes so.</summary>
    public void Unsplit(EditorGroup group, bool all)
    {
        if (FrameOf(group) is not { } frame || !IsSplit(group))
        {
            return;
        }
        EditorFrame.Node joined = all ? frame.Root : Sides(frame.Root, group) ?? frame.Root;
        foreach (EditorGroup other in joined.Groups.Where(g => g != group).ToList())
        {
            other.Keep();
            foreach (EditorTab tab in other.Tabs.ToList())
            {
                if (!group.Has(tab.Document))
                {
                    group.Add(tab.Document, group.Tabs.Count, tab.IsPinned);
                    group.Remember(tab.Document, other.SelectionsOf(tab.Document), other.ScrollOf(tab.Document));
                }
                other.Remove(tab.Document);
            }
        }
        frame.Root = frame.Root.Replace(joined, new EditorFrame.Leaf(group));
        frame.Current = group;
        Rebuild(frame);
        SetActiveGroup(group);
        Collapse();
        if (group.Active is not null)
        {
            group.Editor.Focus();
        }
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The split whose one side holds a group's leaf: what Unsplit joins.</summary>
    private static EditorFrame.Node? Sides(EditorFrame.Node node, EditorGroup group) => node is EditorFrame.SplitNode split
        ? split.First is EditorFrame.Leaf first && first.Group == group || split.Second is EditorFrame.Leaf second && second.Group == group
            ? split
            : Sides(split.First, group) ?? Sides(split.Second, group)
        : null;

    /// <summary>The next tab of the active group (or previous, for a negative step), wrapping at the ends.</summary>
    public void Cycle(int step)
    {
        activeGroup.Cycle(step);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    // Dropping a dragged tab.

    private void Drop(EditorFrame frame, EditorGroup from, OpenDocument document, Point at)
    {
        if (Target(frame, at) is not { } target)
        {
            // Out of every window's editors: a window of its own where it was let go, its tab under the pointer.
            if (TopLevel.GetTopLevel(frame.Surface) is not null)
            {
                Detach(from, document, frame.Surface.PointToScreen(at - TabGrip));
            }
            return;
        }
        switch (target.Place)
        {
            case DropPlace.Strip:
                Move(from, document, target.Group, target.Index);
                break;
            case DropPlace.Middle:
                Move(from, document, target.Group);
                break;
            default:
                // A tab dropped on its own group's edge, the group's only tab, has nothing to split from.
                if (target.Group == from && from.Tabs.Count == 1)
                {
                    return;
                }
                bool across = target.Place is DropPlace.Left or DropPlace.Right;
                bool before = target.Place is DropPlace.Left or DropPlace.Top;
                Split(target.Group, document, across ? Orientation.Horizontal : Orientation.Vertical, move: target.Group == from, before);
                if (target.Group != from)
                {
                    from.Remove(document);
                    Collapse();
                }
                break;
        }
        if (target.Frame != frame)
        {
            Raise(target.Frame);
        }
    }

    private enum DropPlace
    {
        Strip,
        Middle,
        Left,
        Right,
        Top,
        Bottom,
    }

    /// <summary>The lit place a tab dragged from a frame is over, in the overlay of the window it would drop in.</summary>
    private DropZone? ZoneAt(EditorFrame frame, Point at) => Target(frame, at) is { } target ? new DropZone(target.Zone, Overlay: target.Frame.Drop) : null;

    /// <summary>Where a tab dragged from a frame and let go at a point (in its surface's coordinates) goes: into the
    /// editors under the point, the window it came from first, then the editor windows, then the main window, which the
    /// others may stand over; null over none of them.</summary>
    private (EditorFrame Frame, EditorGroup Group, DropPlace Place, int Index, Rect Zone)? Target(EditorFrame from, Point at)
    {
        if (TopLevel.GetTopLevel(from.Surface) is null)
        {
            return null;
        }
        PixelPoint screen = from.Surface.PointToScreen(at);
        foreach (EditorFrame frame in frames.OrderBy(f => f == from ? 0 : f.Window is not null ? 1 : 2).ToList())
        {
            if (TopLevel.GetTopLevel(frame.Surface) is not { IsVisible: true })
            {
                continue;
            }
            Point local = frame == from ? at : frame.Surface.PointToClient(screen);
            if (TargetIn(frame, local) is { } found)
            {
                return (frame, found.Group, found.Place, found.Index, found.Zone);
            }
        }
        return null;
    }

    /// <summary>Where in a frame a tab dropped at a point goes: onto a group's strip at a place, its middle, or one of its
    /// edges (the outer quarter), with the rectangle that lights up for it, in the frame's surface's coordinates.</summary>
    private static (EditorGroup Group, DropPlace Place, int Index, Rect Zone)? TargetIn(EditorFrame frame, Point at)
    {
        foreach (EditorGroup group in frame.Groups)
        {
            if (group.TranslatePoint(default, frame.Surface) is not { } origin)
            {
                continue;
            }
            Rect bounds = new(origin, group.Bounds.Size);
            if (!bounds.Contains(at))
            {
                continue;
            }
            Rect strip = new(origin, new Size(group.Bounds.Width, group.TabStrip.Bounds.Height));
            if (strip.Contains(at))
            {
                return (group, DropPlace.Strip, group.IndexAt(frame.Surface.TranslatePoint(at, group.TabStrip) ?? at), strip);
            }
            Rect body = new(bounds.X, strip.Bottom, bounds.Width, bounds.Bottom - strip.Bottom);
            double x = (at.X - body.X) / body.Width;
            double y = (at.Y - body.Y) / body.Height;
            return x < 0.25 ? (group, DropPlace.Left, 0, body.WithWidth(body.Width / 2))
                : x > 0.75 ? (group, DropPlace.Right, 0, new Rect(body.X + (body.Width / 2), body.Y, body.Width / 2, body.Height))
                : y < 0.25 ? (group, DropPlace.Top, 0, body.WithHeight(body.Height / 2))
                : y > 0.75 ? (group, DropPlace.Bottom, 0, new Rect(body.X, body.Y + (body.Height / 2), body.Width, body.Height / 2))
                : (group, DropPlace.Middle, 0, body);
        }
        return null;
    }

    // The split trees.

    private EditorGroup NewGroup()
    {
        EditorGroup group = new(this) { IsCurrent = false };
        group.Editor.SelectionChanged += (_, _) =>
        {
            if (group == activeGroup)
            {
                CaretMoved?.Invoke(this, EventArgs.Empty);
            }
        };
        foreach (Action<CodeEditor> hook in editorHooks)
        {
            hook(group.Editor);
        }
        return group;
    }

    private EditorFrame NewFrame(EditorFrame.Node root, EditorGroup current, Control? empty = null)
    {
        EditorFrame frame = new(root, current, ZoneAt, Drop, empty);
        frames.Add(frame);
        return frame;
    }

    /// <summary>Groups left with no tab go (all but the main window's last), their neighbours taking their room, and an
    /// editor window left with none goes with them.</summary>
    private void Collapse()
    {
        EditorFrame worked = ActiveFrame;
        foreach (EditorFrame frame in frames.ToList())
        {
            if (frame.Window is { } window && frame.IsEmpty)
            {
                frames.Remove(frame);
                // Closed by the person, it is closing already.
                if (!frame.Closing)
                {
                    window.CloseQuietly();
                }
                continue;
            }
            bool changed = false;
            foreach (EditorGroup empty in frame.Groups.Where(g => g.Tabs.Count == 0).ToList())
            {
                if (frame.Groups.Count() > 1)
                {
                    frame.Root = frame.Root.Without(empty) ?? frame.Root;
                    changed = true;
                }
            }
            if (changed)
            {
                frame.Rebuild();
            }
            if (!frame.Groups.Contains(frame.Current))
            {
                frame.Current = frame.Groups.FirstOrDefault(g => g.Active is not null) ?? frame.Groups.First();
            }
        }
        if (FrameOf(activeGroup) is null)
        {
            // The active group went: the one last worked in in its window takes its place, the main window's if that went too.
            EditorFrame front = frames.Contains(worked) ? worked : main;
            activeGroup.IsCurrent = false;
            activeGroup = front.Current;
            activeGroup.IsCurrent = true;
            if (activeGroup.Active is not null)
            {
                activeGroup.Editor.Focus();
            }
        }
        Refresh();
    }

    private void Rebuild(EditorFrame frame)
    {
        frame.Rebuild();
        Refresh();
    }

    private void Refresh()
    {
        emptyState.IsVisible = main.IsEmpty;
        TitleWindows();
    }
}

/// <summary>A file or folder that moved on disk (both paths canonical), and the open files that went with it.</summary>
public sealed record FileMove(string From, string To, IReadOnlyList<OpenDocument> Documents);

/// <summary>A tab's menu as it opens: the tab (its group and file) and the menu to fill.</summary>
public sealed record TabMenu(EditorGroup Group, OpenDocument Document, ContextMenu Menu);
