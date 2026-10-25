using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App.Editors;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>The window's Git: the toolbar's branch widget and its menu, the Commit tool window, the change bars in
/// every editor, the Explorer's colours, and the change actions (next and previous change, Rollback Lines).</summary>
public partial class MainWindow
{
    private ToolWindows.CommitView? commitView;
    private ToolWindows.GitView? gitView;
    private DiffPanes? diffs;

    /// <summary>The Git tool window's log.</summary>
    public ToolWindows.GitLogView GitLog => GitWindow.Log;

    /// <summary>The Git tool window: its Log and the histories Show History opened beside it.</summary>
    public ToolWindows.GitView GitWindow => gitView ?? (ToolWindows.GitView)Dock[ToolWindowIds.Git].Content;

    /// <summary>The open folder's version control, its session's.</summary>
    public VcsController Vcs => session.Vcs;

    /// <summary>The window in front: the last diff or dialog opened over this one and still open (or over that, as the
    /// merge window opens over the Conflicts dialog), else the editor window the person works in, else this.</summary>
    public Window Frontmost
    {
        get
        {
            Window? front = OwnedWindows.LastOrDefault(w => w.IsVisible);
            while (front?.OwnedWindows.LastOrDefault(w => w.IsVisible) is { } over)
            {
                front = over;
            }
            return front ?? Editors.InFront?.Window ?? this;
        }
    }

    /// <summary>The Commit tool window, built as it is first shown or asked for.</summary>
    public ToolWindows.CommitView CommitView => commitView ?? (ToolWindows.CommitView)Dock[ToolWindowIds.Commit].Content;

    /// <summary>Asks the person to agree to something that cannot be undone (a rollback): its title, what happens, and
    /// the button that does it.</summary>
    public Func<string, string, string, Task<bool>> Confirm
    {
        get => confirm ?? ((title, text, yes) => ConfirmAsync(title, text, yes, null));
        set => confirm = value;
    }

    private Func<string, string, string, Task<bool>>? confirm;

    /// <summary>Asks as <see cref="Confirm"/> does, with what it applies to under the question (Rollback's files).</summary>
    private Task<bool> ConfirmAsync(string title, string text, string yes, Control? detail) =>
        confirm is { } answer ? answer(title, text, yes) : new ConfirmDialog(title, text, yes, detail: detail).ShowDialog<bool>(this);

    /// <summary>Asks the person for a value a fix sets (an AppHost parameter's), as <see cref="Confirm"/> asks, with a box for
    /// it under the question, its characters masked for a secret; null when they cancel or give none.</summary>
    private async Task<string?> AskValueAsync(string title, string text, bool secret)
    {
        TextBox box = new() { Name = "AskedValue", PasswordChar = secret ? '•' : default, RevealPassword = false };
        ConfirmDialog dialog = new(title, text, "Set", detail: box);
        dialog.Opened += (_, _) => box.Focus();
        return await dialog.ShowDialog<bool>(this) && box.Text is { Length: > 0 } value ? value : null;
    }

    private void AddVcs()
    {
        // Each built as it is first shown or asked for, over the session's Git then.
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Commit, "Commit", IconKeys.Commit, MakeCommit, Docking.ToolAnchor.LeftTop)
        {
            Action = AppActions.ShowCommit,
            ActionTitle = "Commit Tool Window",
            Menus = [new MenuPlace("Git", 3, 0)],
        });
        Dock.Add(new Docking.ToolWindow(ToolWindowIds.Git, "Git", IconKeys.Branch, MakeGit, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowGit,
            Menus = [new MenuPlace("Git", 3, 1)],
            Tools = () => gitView?.Tabs,
        });
        AddVcsActions();
        AddHistoryActions();
        AddMergeConflicts();
        // Back from another program, the open files take what it changed on disk, and Git is read again.
        Activated += (_, _) =>
        {
            session.Vcs.ReloadOpenFiles();
            IdeErrors.Observe(session.Vcs.RefreshAsync());
        };
    }

    /// <summary>The Commit window's view, as it is first shown or asked for.</summary>
    private ToolWindows.CommitView MakeCommit()
    {
        ToolWindows.CommitView view = commitView = new ToolWindows.CommitView(session.Vcs);
        view.OpenRequested += (_, path) => OpenFile(path);
        view.DiffRequested += (_, path) => IdeErrors.Observe(ShowDiffAsync(path));
        view.RollbackRequested += (_, changes) => IdeErrors.Observe(RollbackAsync(changes));
        view.DeleteRequested += (_, path) => IdeErrors.Observe(DeleteAsync(path));
        view.IgnoreRequested += (_, path) => Ignore(path);
        view.ResolveRequested += (_, _) => ShowConflicts();
        view.MergeRequested += (_, path) => IdeErrors.Observe(MergeAsync(path));
        view.Confirm = (title, text, yes) => Confirm(title, text, yes);
        return view;
    }

    /// <summary>The Git window's view, as it is first shown or asked for (Show History, a branch's menu, a blamed line's
    /// commit), its branches panel given the folder's favourites.</summary>
    private ToolWindows.GitView MakeGit()
    {
        ToolWindows.GitView view = gitView = new ToolWindows.GitView(session.Vcs);
        view.DiffRequested += (_, chosen) => IdeErrors.Observe(ShowCommitDiffAsync(chosen.Commit, chosen.File));
        view.CompareRequested += (_, chosen) => IdeErrors.Observe(CompareWithAsync(chosen.Local, chosen.Commit.Hash, chosen.Commit.Short, chosen.File.Path));
        view.NewBranchRequested += (_, commit) =>
        {
            NamePopup popup = new($"New Branch from {commit.Short}", string.Empty, async name => (await session.Vcs.NewBranchAtAsync(name, commit.Hash)).Problem);
            popups.Show(popup, _ => { });
        };
        view.ResetRequested += (_, reset) => IdeErrors.Observe(ResetToAsync(reset));
        FollowBranches(view.Log.Branches!);
        return view;
    }

    /// <summary>A session's Git in the window: its Commit and Git windows, what it says in the status bar, the branch
    /// widget and the Explorer's colours, and its Conflicts dialog, while it is the window's. One left takes its dialog.</summary>
    private void BindVcs(ProjectSession next)
    {
        conflictsDialog?.Close();
        VcsController vcs = next.Vcs;
        commitView?.Bind(vcs);
        gitView?.Bind(vcs);
        vcs.SaveFirst = () => Editors.SaveAll();
        vcs.Message += (_, message) =>
        {
            if (session == next)
            {
                status.Text = message;
            }
        };
        vcs.Changed += (_, _) =>
        {
            if (session == next)
            {
                OnVcsChanged();
            }
        };
        vcs.ConflictsFound += (_, _) =>
        {
            if (session == next)
            {
                ShowConflicts();
            }
        };
        OnVcsChanged();
    }

    private void OnVcsChanged()
    {
        VcsController vcs = session.Vcs;
        BranchName.Text = vcs.Repository is null ? "Version control" : vcs.Branch ?? "Detached HEAD";
        ToolTip.SetTip(BranchButton, vcs.Repository is null ? "Version control" : $"Git branch: {BranchName.Text}");
        Explorer.Tree.SetChanges(vcs.Changes.Where(c => c.Kind is not ChangeKind.Deleted).ToDictionary(c => c.Path, c => c.Kind));
    }

    /// <summary>The branch popup: new branch, commit, push, then the local branches, each with its own menu.</summary>
    private void OnBranchClicked(object? sender, RoutedEventArgs e) => ShowBranches();

    private void ShowBranches()
    {
        IdeErrors.Observe(ShowBranchesAsync());
    }

    private async Task ShowBranchesAsync()
    {
        ContextMenu menu = new();
        MenuItem Item(string header, Action run)
        {
            MenuItem item = new() { Header = header };
            item.Click += (_, _) => run();
            return item;
        }
        ActionContext context = actions!.Context();
        MenuItem Action(string id)
        {
            MenuItem item = Item(actions[id]!.Title, () => Request(id, context));
            // In the menu's column of keys, as every menu's are, not run on after the title.
            actions.ShowKeys(item, id);
            return item;
        }
        if (session.Vcs.Repository is null)
        {
            if (Folder is { } open)
            {
                menu.Items.Add(Item("Create Git Repository…", () => IdeErrors.Observe(session.Vcs.InitAsync(open))));
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = Refusals.NoFolder, IsEnabled = false });
            }
            Open(menu, BranchButton);
            return;
        }
        menu.Items.Add(Action(AppActions.UpdateProject));
        menu.Items.Add(Action(AppActions.Fetch));
        menu.Items.Add(Item("New Branch…", ShowNewBranch));
        menu.Items.Add(Action(AppActions.Commit));
        menu.Items.Add(Action(AppActions.Push));
        menu.Items.Add(Action(AppActions.Stash));
        menu.Items.Add(Action(AppActions.Unstash));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Local", IsEnabled = false });
        // Each branch opens its own menu (Checkout, Merge into Current and the rest), so only Checkout checks one out.
        (IReadOnlyList<BranchInfo> refs, _) = await session.Vcs.BranchTreeAsync();
        foreach (BranchInfo branch in refs.Where(b => b.Kind == RefKind.Local).OrderByDescending(b => b.IsCurrent))
        {
            menu.Items.Add(MenuChoice.Holding(branch.Name, branch.IsCurrent, GitBranches.Menu(branch)));
        }
        Open(menu, BranchButton);
    }

    /// <summary>Stash Changes: what is open saved first, then every change put away under the message given.</summary>
    public NamePopup? ShowStash()
    {
        if (session.Vcs.Repository is null)
        {
            status.Text = Refusals.NoRepository;
            return null;
        }
        NamePopup popup = new("Stash Changes", string.Empty, async message => (await session.Vcs.StashAsync(message)).Problem);
        popups.Show(popup, _ => { });
        return popup;
    }

    /// <summary>Unstash Changes: the stashes to pop, apply or drop (once the person agrees).</summary>
    public async Task<UnstashPopup?> ShowUnstashAsync()
    {
        if (session.Vcs.Repository is null)
        {
            status.Text = Refusals.NoRepository;
            return null;
        }
        UnstashPopup popup = new(await session.Vcs.StashesAsync(), async (stash, pop) => (await session.Vcs.UnstashAsync(stash, pop)).Succeeded,
            async stash => (await session.Vcs.DropStashAsync(stash)).Succeeded, (title, text, yes) => Confirm(title, text, yes));
        popup.Done += (_, _) =>
        {
            // Escape may have closed it while a pop ran, and another popup taken its place since.
            if (popups.Open == popup)
            {
                popups.Close();
            }
        };
        popups.Show(popup);
        return popup;
    }

    /// <summary>Create New Branch: from the head, checked out at once.</summary>
    public void ShowNewBranch()
    {
        NamePopup popup = new("Create New Branch", string.Empty, async name => (await session.Vcs.NewBranchAsync(name)).Problem);
        popups.Show(popup, _ => { });
    }

    /// <summary>Show Diff: the last commit's text beside the file's, in a window over this one. The right side is the
    /// open file's own text when it is open, so an edit or a change put back there is the file's, in the file's colours as
    /// they change.</summary>
    public async Task<DiffWindow?> ShowDiffAsync(string path)
    {
        if (session.Vcs.Repository is null)
        {
            status.Text = Refusals.NoRepository;
            return null;
        }
        OpenDocument? open = Editors.Find(path) is { HoldsText: true } held ? held : null;
        TextDocument mine = open?.Text ?? new TextDocument(EditorArea.ReadText(path) ?? string.Empty);
        // An open file's differences are its change bars' own when they are of its text now.
        if (open is null || await session.Vcs.DiffAsync(open) is not { } diff)
        {
            string head = await session.Vcs.HeadTextAsync(path) ?? string.Empty;
            TextSnapshot text = mine.Snapshot;
            diff = await BackgroundWork.Run(() => FileDiff.Compare(head, text));
        }
        return OpenLocalDiff(path, open, mine, diff, "HEAD");
    }

    /// <summary>A version's text (the last commit's, a branch's, a revision's), or another file's (Compare With, named by
    /// <paramref name="other"/>), beside the file's in a window over this one, the right side the open file's own text when
    /// it is still open.</summary>
    private DiffWindow OpenLocalDiff(string path, OpenDocument? open, TextDocument mine, FileDiff diff, string version, string? other = null)
    {
        // Closed while the differences were read, the file's text is one nothing would save: it is shown, not edited.
        if (open is not null && !Editors.Documents.Contains(open))
        {
            open = null;
        }
        string mineTitle = other is null ? "Your version" : InFolder(path);
        string title = other is null ? $"{Path.GetFileName(path)}: {version} and your version" : $"{Path.GetFileName(other)} vs {Path.GetFileName(path)}";
        DiffView view = Diffs.ForFile(other is null ? Path.GetFileName(path) : title, diff, mine, open,
            (version, open is not null ? mineTitle : $"{mineTitle} (read-only)"), other ?? path, path);
        DiffWindow window = new(title, view);
        if (open is { } file)
        {
            // The file stays open while the diff edits it, so what is typed there is saved; closed for good, the diff goes too.
            IDisposable hold = Editors.Hold(file);
            void OnFileClosed(object? sender, OpenDocument closed)
            {
                if (closed == file)
                {
                    window.Close();
                }
            }
            Editors.Closed += OnFileClosed;
            window.Closed += (_, _) =>
            {
                Editors.Closed -= OnFileClosed;
                hold.Dispose();
            };
        }
        ShowOwn(window);
        return window;
    }

    /// <summary>How the window's diffs and merges are made: Highlighting Differences as Settings has it, kept when a diff
    /// changes it.</summary>
    private DiffPanes Diffs => diffs ??= new(() => settings.DiffHighlighting, way => ApplySettings(settings with { DiffHighlighting = way }));

    /// <summary>What a commit did to a file: its first parent's text beside its own, both read-only.</summary>
    public async Task<DiffWindow?> ShowCommitDiffAsync(CommitInfo commit, FileChange file)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            return null;
        }
        string? parent = commit.Parents.Count > 0 ? commit.Parents[0] : null;
        string before = parent is null || file.Kind is ChangeKind.Added ? string.Empty : await repository.TextAtAsync(parent, file.From ?? file.Path) ?? string.Empty;
        string after = file.Kind is ChangeKind.Deleted ? string.Empty : await repository.TextAtAsync(commit.Hash, file.Path) ?? string.Empty;
        string leftTitle = parent is null ? "Before the first commit" : parent[..Math.Min(8, parent.Length)];
        TextDocument mine = new(after);
        TextSnapshot text = mine.Snapshot;
        FileDiff diff = await BackgroundWork.Run(() => FileDiff.Compare(before, text));
        DiffView view = Diffs.ForFile(file.Name, diff, mine, null, (leftTitle, commit.Short), file.From ?? file.Path, file.Path);
        DiffWindow window = new($"{file.Name}: {commit.Short} {commit.Subject}", view);
        ShowOwn(window);
        return window;
    }

    /// <summary>The change bar a gutter click on a line falls on, or null.</summary>
    private static ChangeMark? ChangeAt(OpenDocument document, int line) =>
        document.ChangeMarks.Cast<ChangeMark?>().FirstOrDefault(m => m!.Value.Count == 0
            ? line == m.Value.Line || line == m.Value.Line - 1
            : line >= m.Value.Line && line < m.Value.Line + m.Value.Count);

    /// <summary>A click on a change bar: a popup of what the lines were, with the change's actions.</summary>
    private async Task ShowChangePopupAsync(CodeEditor editor, OpenDocument document, ChangeMark change)
    {
        FileDiff? diff;
        using (BackgroundWork.Scope())
        {
            diff = await session.Vcs.DiffAsync(document);
        }
        if (diff is null || Editors.Active != document)
        {
            return;
        }
        DiffHunk? hunk = diff.Hunks.Cast<DiffHunk?>().FirstOrDefault(h => h!.Value.NewStart == change.Line);
        string? before = hunk is { OldCount: > 0 } found ? diff.Before(found) : null;
        InlineChanges words = InlineChanges.Empty;
        void Then(Action run)
        {
            popups.Close();
            run();
        }
        void Move(int step)
        {
            GoToChange(step);
            if (Editors.Active is { } now && ChangeAt(now, Editors.CodeEditor.CaretPosition.Line) is { } next)
            {
                IdeErrors.Observe(ShowChangePopupAsync(Editors.CodeEditor, now, next));
            }
        }
        // The file's changed words are marked in it while the popup shows them; an edit since moves them.
        void MarkFile(bool on) => editor.SetInlineChanges(on && ReferenceEquals(editor.Document.Snapshot, diff.Text) ? words.Right : []);
        ChangePopup popup = new(
            before,
            settings.ChangePopupWords,
            highlightWordsChanged: on =>
            {
                MarkFile(on);
                ApplySettings(settings with { ChangePopupWords = on });
            },
            previous: () => Then(() => Move(-1)),
            next: () => Then(() => Move(1)),
            rollback: () => Then(() =>
            {
                editor.GoTo(new TextPosition(Math.Min(change.Line, document.Text.Snapshot.LineCount - 1), 0));
                IdeErrors.Observe(RollbackLinesAsync());
            }),
            diff: () => Then(() => IdeErrors.Observe(ShowDiffAsync(document.Path))));
        bool open = true;
        popup.DetachedFromVisualTree += (_, _) =>
        {
            open = false;
            MarkFile(false);
        };
        Avalonia.Point below = editor.GutterPoint(change.Line + Math.Max(1, change.Count) - 1);
        popups.Show(popup, popups.PointIn(editor, new Avalonia.Point(4, below.Y + 2)));
        if (hunk is not { } compared)
        {
            return;
        }
        // The popup opens at once; the words that changed land in it, and in the file, once worked out.
        words = await BackgroundWork.Run(() => diff.InnerChanges(compared, DiffHighlighting.Words));
        if (open)
        {
            popup.ShowWords(words.Left);
            MarkFile(popup.HighlightToggle.IsChecked == true);
        }
    }

    /// <summary>Annotate with Git Blame, or Close Annotations when the file shows them or is about to.</summary>
    public async Task ToggleAnnotationsAsync(OpenDocument document)
    {
        if (session.Vcs.IsAnnotated(document))
        {
            session.Vcs.CloseAnnotations(document);
            return;
        }
        if (!await session.Vcs.AnnotateAsync(document))
        {
            status.Text = $"{document.Name} is not tracked by Git";
        }
    }

    /// <summary>A click on a line's annotation: its commit, chosen in the Git window's log.</summary>
    private async Task ShowBlamedCommitAsync(OpenDocument document, int line)
    {
        if (session.Vcs.BlameOf(document) is not { } lines || line >= lines.Count || !lines[line].IsCommitted)
        {
            return;
        }
        ShowToolWindow(ToolWindowIds.Git);
        GitWindow.ShowLog();
        await GitLog.SelectHashAsync(lines[line].Hash);
        status.Text = $"{lines[line].Short} {lines[line].Summary} ({lines[line].Author}, {lines[line].Time.ToLocalTime():f})";
    }

    /// <summary>The gutter menu where no breakpoint is: annotations on or off.</summary>
    private void ShowGutterMenu(CodeEditor editor, OpenDocument document)
    {
        ContextMenu menu = new();
        MenuItem annotate = new() { Header = session.Vcs.IsAnnotated(document) ? "Close Annotations" : "Annotate with Git Blame", IsEnabled = session.Vcs.Repository is not null };
        annotate.Click += (_, _) => IdeErrors.Observe(ToggleAnnotationsAsync(document));
        menu.Items.Add(annotate);
        menu.Open(editor);
    }

    /// <summary>The version control actions over the folder's working tree, each needing a repository, and those on a
    /// file the file shown.</summary>
    private void AddVcsActions()
    {
        Add(AppActions.Commit, "Commit…", context =>
        {
            // Every file is saved before a commit, so the window lists what the editors show.
            Editors.SaveAll();
            ShowToolWindow(ToolWindowIds.Commit);
            IdeErrors.Observe(session.Vcs.RefreshAsync());
            CommitView.FocusMessage();
        }, new("Git", 0, 0));
        Add(AppActions.Push, "Push…", context => IdeErrors.Observe(session.Vcs.PushAsync()), new("Git", 0, 1));
        Add(AppActions.UpdateProject, "Update Project…", context => IdeErrors.Observe(session.Vcs.UpdateAsync()), new("Git", 0, 2));
        Add(AppActions.Fetch, "Fetch", context => IdeErrors.Observe(session.Vcs.FetchAsync()), new("Git", 0, 3));
        Add(AppActions.Branches, "Branches…", _ => ShowBranches(), new("Git", 0, 4));
        Add(AppActions.Stash, "Stash Changes…", _ => ShowStash(), new("Git", 1, 0));
        Add(AppActions.Unstash, "Unstash Changes…", context => IdeErrors.Observe(ShowUnstashAsync()), new("Git", 1, 1));
        Add(AppActions.ShowDiff, "Show Diff", context => IdeErrors.Observe(ShowDiffAsync(context.File!.Path)), new("Git", 2, 0), onFile: true);
        Add(AppActions.Annotate, "Annotate with Git Blame", context => IdeErrors.Observe(ToggleAnnotationsAsync(context.File!)), new("Git", 2, 1), onFile: true);
        Add(AppActions.RollbackLines, "Rollback Lines", context => IdeErrors.Observe(RollbackLinesAsync()), new("Git", 2, 2), onFile: true);
        Add(AppActions.NextChange, "Next Change", _ => GoToChange(1), new("Navigate", 3, 2), onFile: true);
        Add(AppActions.PreviousChange, "Previous Change", _ => GoToChange(-1), new("Navigate", 3, 3), onFile: true);
        // A diff's own, whatever it compares: how its differences are highlighted.
        actions!.Add(new IdeAction(AppActions.DiffSettings, "Diff Settings", "Git", context => DiffWithKeys(context)!.ShowSettings())
        {
            WhyNot = context => DiffWithKeys(context) is null ? "No diff has the keys" : null,
        });

        void Add(string id, string title, Action<ActionContext> run, MenuPlace menu, bool onFile = false) =>
            actions!.Add(new IdeAction(id, title, "Git", run)
            {
                WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? Refusals.NoRepository : onFile ? Refusals.NeedsFile(context) : null),
                Menus = [menu],
            });
    }

    private static DiffView? DiffWithKeys(ActionContext context) => (context.Focused as Visual)?.FindAncestorOfType<DiffView>(includeSelf: true);

    /// <summary>The Git window's Reset Current Branch to Here: a hard reset, which drops changes, only once confirmed.</summary>
    private async Task ResetToAsync((CommitInfo Commit, ResetMode Mode) reset)
    {
        if (reset.Mode == ResetMode.Hard && !await Confirm("Reset Current Branch to Here",
            $"Move {session.Vcs.Branch ?? "HEAD"} to {reset.Commit.Short} and drop every change since, committed or not?", "Reset"))
        {
            return;
        }
        await session.Vcs.ResetAsync(reset.Commit.Hash, reset.Mode);
    }

    private async Task RollbackAsync(IReadOnlyList<FileChange> changes)
    {
        if (changes.Count == 0)
        {
            status.Text = "No files are chosen to roll back";
            return;
        }
        // The files are listed, so what goes back is seen before it does.
        string what = changes.Count == 1 ? changes[0].Name : $"these {changes.Count} files";
        bool added = changes.Any(c => c.Kind is ChangeKind.Unversioned);
        string text = added
            ? $"Roll back the changes to {what}? New files are deleted, and this cannot be undone."
            : $"Roll back the changes to {what}? This cannot be undone.";
        if (await ConfirmAsync("Rollback Changes", text, "Rollback", ToolWindows.CommitView.FileList(changes, session.Vcs.Repository)))
        {
            await session.Vcs.RollbackAsync(changes);
        }
    }

    /// <summary>Next Change and Previous Change: the caret to the start of the next or previous change bar, round past
    /// the file's end.</summary>
    private void GoToChange(int step)
    {
        if (Editors.Active is not { } document || document.ChangeMarks.Count == 0)
        {
            status.Text = "No changes in this file";
            return;
        }
        int line = Editors.CodeEditor.CaretPosition.Line;
        IReadOnlyList<ChangeMark> marks = [.. document.ChangeMarks.OrderBy(m => m.Line)];
        ChangeMark target = step > 0
            ? marks.FirstOrDefault(m => m.Line > line, marks[0])
            : marks.LastOrDefault(m => m.Line + Math.Max(1, m.Count) <= line, marks[^1]);
        int at = Math.Min(target.Line, document.Text.Snapshot.LineCount - 1);
        Editors.CodeEditor.GoTo(new TextPosition(at, 0));
    }

    /// <summary>Rollback Lines: the change under the caret goes back to what the last commit has there.</summary>
    private async Task RollbackLinesAsync()
    {
        if (Editors.Active is not { } document)
        {
            status.Text = "No change here to roll back";
            return;
        }
        FileDiff? diff;
        using (BackgroundWork.Scope())
        {
            // A comparison of a text edited while it ran says nothing of where the change is now: the newer is compared.
            do
            {
                diff = await session.Vcs.DiffAsync(document);
            }
            while (diff is not null && !ReferenceEquals(diff.Text, document.Text.Snapshot) && Editors.Active == document);
        }
        if (Editors.Active != document)
        {
            return;
        }
        if (diff?.HunkAt(Editors.CodeEditor.CaretPosition.Line) is not { } hunk)
        {
            status.Text = "No change here to roll back";
            return;
        }
        Editors.CodeEditor.ApplyEdits([diff.Restore(hunk)]);
    }

    /// <summary>Add to .gitignore: the file's path from the repository's top, a line of its .gitignore, which keeps its
    /// encoding, byte order mark and line breaks and is written as a save writes (through a temporary file, to Local
    /// History); open with unsaved edits, it is saved first, and shows the line after.</summary>
    private void Ignore(string path)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            return;
        }
        string ignore = Path.Combine(repository.Root, ".gitignore");
        string line = Path.GetRelativePath(repository.Root, path).Replace('\\', '/');
        if (SaveBeforeWriting([ignore]) is { } unsaved)
        {
            status.Text = $".gitignore was not changed: {unsaved}";
            return;
        }
        try
        {
            FileText? file = File.Exists(ignore) ? FileText.Load(ignore) ?? throw new IOException(".gitignore is not a text file") : null;
            string before = file?.Text ?? string.Empty;
            string eol = TextDocument.DetectLineEnding(before);
            FileText.Save(ignore, before + (before.Length > 0 && !before.EndsWith('\n') ? eol : string.Empty) + line + eol,
                file?.Encoding ?? new System.Text.UTF8Encoding(false), file?.HasBom ?? false, file?.CrBreaks ?? false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = $".gitignore was not changed: {error.Message}";
            return;
        }
        FollowWritten([ignore]);
        status.Text = $"Added {line} to .gitignore";
        IdeErrors.Observe(session.Vcs.RefreshAsync());
    }
}
