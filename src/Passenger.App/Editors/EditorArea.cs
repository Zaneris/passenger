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

/// <summary>The editors: one group of tabs, or several split right and down, one of them active (the one the person
/// works in, whose editor <see cref="CodeEditor"/> is). A tab dragged onto a group's edge splits it there, onto its
/// middle or its tab strip moves into it, and out of the editors into a window of its own (<see cref="EditorWindow"/>),
/// whose groups are the area's too; a group whose last tab closes goes, and its neighbour takes its room. A file shows
/// in as many groups as it is open in, in any window, over one text.</summary>
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
        emptyTitle = new TextBlock { Text = "Passenger", FontSize = 28, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
        emptyTitle.Bind(TextBlock.ForegroundProperty, emptyTitle.GetResourceObservable(TokenKeys.Text));
        emptyHint = new TextBlock { Classes = { "muted" }, Text = "Open a solution, project or folder to start.", HorizontalAlignment = HorizontalAlignment.Center };
        folderHint = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Children = { emptyTitle, emptyHint } };
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

    /// <summary>An image or font whose first decode, off the UI thread, failed, and why: its tabs have closed, as a file
    /// <see cref="Open"/> could not read never opens.</summary>
    public event EventHandler<string>? OpenFailed;

    /// <summary>A file not written because something else changed it on disk since it was last loaded or saved: which
    /// to keep is the person's question (File Cache Conflict), never settled by writing over the disk's.</summary>
    public event EventHandler<OpenDocument>? SaveConflicted;

    /// <summary>A file or folder moved through <see cref="MoveFile"/>, with the open files that went with it.</summary>
    public event EventHandler<FileMove>? Moved;

    /// <summary>A folder another program moved, which open files followed (<see cref="FollowMove"/>).</summary>
    public event EventHandler<FileMove>? Followed;

    /// <summary>A link clicked in a rendered page (release notes), for the window to open the one way the IDE opens a browser.</summary>
    public event EventHandler<Uri>? BrowseRequested;

    internal void Browse(Uri address) => BrowseRequested?.Invoke(this, address);

    /// <summary>A link to a file clicked in a Markdown file's preview, for the window to open it in the editor.</summary>
    public event EventHandler<string>? FileLinkClicked;

    internal void OpenLinked(string path) => FileLinkClicked?.Invoke(this, path);

    /// <summary>The open folder, whose files alone a Markdown preview draws the images of; null for none.</summary>
    public Func<string?> Root { get; set; } = () => null;

    private bool showsMarkdownPreview = true;

    /// <summary>Whether a Markdown file shows its preview beside its editor, in every group showing one.</summary>
    public bool ShowsMarkdownPreview
    {
        get => showsMarkdownPreview;
        set
        {
            showsMarkdownPreview = value;
            foreach (EditorGroup group in AllGroups)
            {
                group.FollowMarkdown();
            }
        }
    }

    /// <summary>A group's preview toggle pressed, for the window to run the preview's action: nothing outside the group
    /// holds the toggle, so a closed split goes with it.</summary>
    public event EventHandler<EditorGroup>? PreviewToggleClicked;

    internal void PreviewToggled(EditorGroup group) => PreviewToggleClicked?.Invoke(this, group);

    /// <summary>Whether the window's previewer draws an open XAML file.</summary>
    public Func<OpenDocument, bool> HasXamlPreview { get; set; } = _ => false;

    private bool showsXamlPreview;

    /// <summary>Whether XAML files show their preview beside their editor, as View, XAML Preview has it.</summary>
    public bool ShowsXamlPreview
    {
        get => showsXamlPreview;
        set
        {
            showsXamlPreview = value;
            foreach (EditorGroup group in AllGroups)
            {
                group.FollowPreviewToggle();
            }
        }
    }

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

    /// <summary>Opens text, a read-only asset, or a binary file with no view of its own as its bytes' text, read-only
    /// (<see cref="FileText.Bytes"/>), in the active group (or shows its tab if open), returning why not when the file
    /// cannot be read; an asset whose decode fails once its tab shows it loading says so through <see cref="OpenFailed"/>.</summary>
    public string? Open(string path)
    {
        string full = PathNames.Canonical(path);
        if (Find(full) is { } open)
        {
            Activate(open);
            return null;
        }
        OpenDocument document;
        try
        {
            document = OpenDocument.Load(full);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
        document.PreviewFailed += OnPreviewFailed;
        Opened?.Invoke(this, document);
        documents.Add(document);
        activeGroup.Add(document);
        Activate(document);
        return null;
    }

    /// <summary>Shows a file in the window in front: in the active group if it has its tab, else in the group there
    /// that does, else in a new tab of the active group.</summary>
    public void Activate(OpenDocument document)
    {
        EditorGroup group = activeGroup.Has(document) ? activeGroup : ActiveFrame.Groups.FirstOrDefault(g => g.Has(document)) ?? activeGroup;
        group.Show(document);
        SetActiveGroup(group);
        Refresh();
        group.FocusShown();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes a file's tabs everywhere, saved first without asking; false, the file left open, when it could
    /// not be written. A file something else holds (<see cref="Hold"/>) stays open without a tab until let
    /// go.</summary>
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
    /// text. An asset, or a binary file's bytes, is never borrowed as editable text, even if its file has since been
    /// renamed.</summary>
    public (OpenDocument Document, IDisposable Hold)? Borrow(string path)
    {
        string full = PathNames.Canonical(path);
        if (AssetPreview.Supports(full))
        {
            return null;
        }
        return Load(full, asBytes: false) is { Asset: null, IsBinary: false } document ? (document, Hold(document)) : null;
    }

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

    /// <summary>Close Other Tabs: every tab of the group but this one and the pinned ones.</summary>
    public void CloseOthers(EditorGroup group, OpenDocument keep) => CloseTabs(group, (tab, _) => tab.Document != keep);

    /// <summary>Close All Tabs: every tab of the group but the pinned ones.</summary>
    public void CloseAllTabs(EditorGroup group) => CloseTabs(group, (_, _) => true);

    /// <summary>Close Tabs to the Left or to the Right of a tab, the pinned ones left open.</summary>
    public void CloseToSide(EditorGroup group, OpenDocument of, bool left)
    {
        int at = group.Tabs.Select(t => t.Document).ToList().IndexOf(of);
        CloseTabs(group, (_, index) => left ? index < at : index > at);
    }

    /// <summary>Close All but Pinned: the group's unpinned tabs, or every group's in every window.</summary>
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

    /// <summary>Reopen Closed Tab: the tab closed last opens again where it stood, in its group if that is still there
    /// (else the active one), pinned if it was, its caret and scroll as they were. Null once one is open, else why not:
    /// none was closed, or none of those closed can be read any more.</summary>
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

    /// <summary>Opens a file in a new group beside one (right for horizontal, below for vertical); with
    /// <paramref name="move"/> its tab leaves the first group.</summary>
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

    /// <summary>Move to Opposite Group: the tab goes to the group beside its own in its window, if there is
    /// one.</summary>
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

    /// <summary>Opens the find bar over the active text file, with the replace row if asked; never over an asset preview.</summary>
    public void ShowFind(bool replace)
    {
        if (Active is { Asset: null })
        {
            activeGroup.Find.Open(replace);
        }
    }

    /// <summary>File, Save: the file shown when it was pressed, whichever is shown by now, its folder made again if another
    /// program moved or deleted it, as asked; nothing once that file has closed.</summary>
    public void SaveAsked(OpenDocument document)
    {
        if (documents.Contains(document))
        {
            TrySave(document, asked: true);
        }
    }

    /// <summary>Saves every changed file, but those <paramref name="waiting"/> on a question; whether all were written. A
    /// file whose folder another program moved or deleted is left unsaved but for the person's Save All (<paramref
    /// name="asked"/>), so an autosave never brings that folder back.</summary>
    public bool SaveAll(IReadOnlySet<OpenDocument>? waiting = null, bool asked = false)
    {
        bool all = true;
        foreach (OpenDocument document in documents.Where(d => d.IsModified && waiting?.Contains(d) != true).ToList())
        {
            all &= TrySave(document, asked: asked);
        }
        return all;
    }

    /// <summary>Writes a file even over a version on disk newer than its own, for Keep Memory Changes.</summary>
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
        Relocate(new FileMove(source, PathNames.Canonical(to), moving), Moved);
        return null;
    }

    /// <summary>Open files under a folder another program moved or renamed (mv, git mv, Finder) follow it, each that the
    /// new place holds as it was last loaded or saved: its tab, carets, edits and undo stay, under its new path.</summary>
    public void FollowMove(string from, string to)
    {
        List<OpenDocument> moving = [.. documents.Where(d => PathNames.IsUnder(d.Path, from) && d.IsSavedAt(PathNames.Moved(d.Path, from, to)))];
        if (moving.Count > 0)
        {
            Relocate(new FileMove(from, to, moving), Followed);
        }
    }

    private void Relocate(FileMove move, EventHandler<FileMove>? told)
    {
        foreach (OpenDocument document in move.Documents)
        {
            document.MoveTo(PathNames.Moved(document.Path, move.From, move.To));
        }
        told?.Invoke(this, move);
        TitleWindows();
        if (Active is { } shown && move.Documents.Contains(shown))
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Saves text, keeping edits on failure or a disk conflict. Assets need no save and never reach serialization.</summary>
    /// <param name="asked">The person's own Save, which makes a folder moved or deleted elsewhere again.</param>
    private bool TrySave(OpenDocument document, bool overDisk = false, bool asked = false)
    {
        if (document.Asset is not null)
        {
            return true;
        }
        if (!overDisk && document.ChangedOnDisk())
        {
            SaveConflicted?.Invoke(this, document);
            return false;
        }
        try
        {
            // The carets of the editor showing it, whose lines keep the whitespace a save would trim.
            EditorGroup? showing = activeGroup.Has(document) ? activeGroup : AllGroups.FirstOrDefault(g => g.Has(document));
            document.Save(showing?.SelectionsOf(document), makeFolder: asked);
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

    /// <summary>Opens what a folder kept: the main window's splits and the editor windows where they stood, each group's
    /// tabs with their carets and the one shown. A file gone from disk is left out, and a group left with none goes, a
    /// window with it. The main window's tabs open before it (a file opened with no folder, a licence About opened) stay,
    /// after the folder's in its current group, the one shown still in front: the folder's from then, as it opens.</summary>
    public void Restore(EditorLayout? layout, IReadOnlyList<KeptEditorWindow>? windows = null)
    {
        List<EditorGroup> before = [.. main.Groups];
        OpenDocument? front = before.Contains(activeGroup) ? activeGroup.Active : null;
        // Read before the kept tabs open, which may show one of these in a group of the folder's.
        List<(OpenDocument Document, bool Pinned, SelectionSet Selections, Vector Scroll)> open =
            [.. before.SelectMany(g => g.Tabs.Select(t => (t.Document, t.IsPinned, g.SelectionsOf(t.Document), g.ScrollOf(t.Document))))];
        bool restored = false;
        if (layout is not null && Restore(layout, out EditorGroup? current) is { } tree)
        {
            // The groups left go: nothing of theirs may keep showing or following a file.
            foreach ((EditorGroup group, OpenDocument document) in before.SelectMany(g => g.Tabs.Select(t => (g, t.Document))).ToList())
            {
                group.Remove(document);
            }
            activeGroup.IsCurrent = false;
            main.Root = tree;
            activeGroup = current ?? main.Groups.First();
            main.Current = activeGroup;
            activeGroup.IsCurrent = true;
            foreach ((OpenDocument document, bool pinned, SelectionSet selections, Vector scroll) in open)
            {
                if (!main.Groups.Any(g => g.Has(document)))
                {
                    activeGroup.Add(document, activeGroup.Tabs.Count, pinned);
                    activeGroup.Remember(document, selections, scroll);
                }
            }
            if (front is not null && main.Groups.FirstOrDefault(g => g.Has(front)) is { } showing)
            {
                showing.Show(front);
                activeGroup.IsCurrent = false;
                activeGroup = showing;
                main.Current = showing;
                showing.IsCurrent = true;
            }
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

    /// <summary>A file's document, open already or loaded as text, a workbook, an asset now (still decoding) or, when
    /// <paramref name="asBytes"/>, a binary file's bytes; null when it cannot be loaded.</summary>
    private OpenDocument? Load(string path, bool asBytes = true)
    {
        string full = PathNames.Canonical(path);
        if (Find(full) is { } open)
        {
            return open;
        }
        try
        {
            OpenDocument document = OpenDocument.Load(full, asBytes);
            document.PreviewFailed += OnPreviewFailed;
            Opened?.Invoke(this, document);
            documents.Add(document);
            return document;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An asset whose first decode failed closes, its tabs with it, as a file that could not be read never
    /// opens, and the window says why.</summary>
    private void OnPreviewFailed(object? sender, string why)
    {
        if (sender is OpenDocument document && documents.Contains(document))
        {
            Close(document, forGood: true);
            OpenFailed?.Invoke(this, why);
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

    /// <summary>Unsplit: the split a group is in goes, the tabs of the other side joining the group's after its own,
    /// each with its carets and pin; with <paramref name="all"/> (Unsplit All) every split of its window goes
    /// so.</summary>
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
