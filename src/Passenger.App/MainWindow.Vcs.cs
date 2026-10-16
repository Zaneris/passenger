using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App;

/// <summary>The window's Git: the toolbar's branch widget and its menu, the Commit tool window, the change bars in
/// every editor, the Explorer's colours, and the change actions (next and previous change, Rollback Lines).</summary>
public partial class MainWindow
{
    private ToolWindows.CommitView? commitView;
    private ToolWindows.GitView? gitView;

    /// <summary>The Git tool window's log.</summary>
    public ToolWindows.GitLogView GitLog => gitView!.Log;

    /// <summary>The Git tool window: its Log and the histories Show History opened beside it.</summary>
    public ToolWindows.GitView GitWindow => gitView!;

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

    /// <summary>The Commit tool window.</summary>
    public ToolWindows.CommitView CommitView => commitView!;

    /// <summary>Asks the person to agree to something that cannot be undone (a rollback): its title, what happens,
    /// and the button that does it. Tests answer for them.</summary>
    public Func<string, string, string, Task<bool>> Confirm
    {
        get => confirm ?? ((title, text, yes) => ConfirmAsync(title, text, yes, null));
        set => confirm = value;
    }

    private Func<string, string, string, Task<bool>>? confirm;

    /// <summary>Asks as <see cref="Confirm"/> does, with what it applies to under the question (Rollback's files).</summary>
    private Task<bool> ConfirmAsync(string title, string text, string yes, Control? detail) =>
        confirm is { } answer ? answer(title, text, yes) : new ConfirmDialog(title, text, yes, detail: detail).ShowDialog<bool>(this);

    private void AddVcs()
    {
        commitView = new ToolWindows.CommitView(session.Vcs);
        Dock.Add(new Docking.ToolWindow("commit", "Commit", "Icon.Commit", commitView, Docking.ToolAnchor.LeftTop)
        {
            Action = AppActions.ShowCommit,
            ActionTitle = "Commit Tool Window",
            Menus = [new MenuPlace("Git", 3, 0)],
        });
        gitView = new ToolWindows.GitView(session.Vcs);
        Dock.Add(new Docking.ToolWindow("git", "Git", "Icon.Branch", gitView, Docking.ToolAnchor.BottomLeft)
        {
            Action = AppActions.ShowGit,
            Menus = [new MenuPlace("Git", 3, 1)],
            HeaderTools = gitView.Tabs,
        });
        AddVcsActions();
        AddHistoryActions();
        AddMergeConflicts();
        AddBranches();
        gitView.DiffRequested += (_, chosen) => _ = ShowCommitDiffAsync(chosen.Commit, chosen.File);
        gitView.CompareRequested += (_, chosen) => _ = CompareWithAsync(chosen.Local, chosen.Commit.Hash, chosen.Commit.Short, chosen.File.Path);
        commitView.OpenRequested += (_, path) => OpenFile(path);
        commitView.DiffRequested += (_, path) => _ = ShowDiffAsync(path);
        commitView.RollbackRequested += async (_, changes) => await RollbackAsync(changes);
        commitView.DeleteRequested += async (_, path) => await DeleteAsync(path);
        commitView.IgnoreRequested += (_, path) => Ignore(path);
        gitView.NewBranchRequested += (_, commit) =>
        {
            NamePopup popup = new($"New Branch from {commit.Short}", string.Empty, async name => (await session.Vcs.NewBranchAtAsync(name, commit.Hash)).Problem);
            popup.Done += (_, _) => popups.Close();
            popups.Show(popup);
        };
        gitView.ResetRequested += async (_, reset) =>
        {
            if (reset.Mode == ResetMode.Hard && !await Confirm("Reset Current Branch to Here",
                $"Move {session.Vcs.Branch ?? "HEAD"} to {reset.Commit.Short} and drop every change since, committed or not?", "Reset"))
            {
                return;
            }
            await session.Vcs.ResetAsync(reset.Commit.Hash, reset.Mode);
        };
        Explorer.IsVersioned = path => session.Vcs.Repository?.IsInside(path) == true;
        // Back from another program, the open files take what it changed on disk, and Git is read again (Rider's
        // synchronize on frame activation).
        Activated += (_, _) =>
        {
            session.Vcs.ReloadOpenFiles();
            _ = session.Vcs.RefreshAsync();
        };
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

    /// <summary>Rider's branch popup: new branch, commit, push, then the local branches to check out.</summary>
    private void OnBranchClicked(object? sender, RoutedEventArgs e) => ShowBranches();

    private void ShowBranches()
    {
        _ = ShowBranchesAsync();
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
        // One of the window's actions, by its title, with its shortcut in the window's keymap.
        ActionContext context = actions!.Context();
        MenuItem Action(string id)
        {
            MenuItem item = Item(actions[id]!.Title, () => Request(id, context));
            // In the menu's column of keys, as every menu's are, not run on after the title.
            ShowKeys(item, id);
            return item;
        }
        if (session.Vcs.Repository is null)
        {
            if (Folder is { } open)
            {
                menu.Items.Add(Item("Create Git Repository...", () => _ = session.Vcs.InitAsync(open)));
            }
            else
            {
                menu.Items.Add(new MenuItem { Header = "No folder is open", IsEnabled = false });
            }
            Open(menu, BranchButton);
            return;
        }
        menu.Items.Add(Action(AppActions.UpdateProject));
        menu.Items.Add(Action(AppActions.Fetch));
        menu.Items.Add(Item("New Branch...", ShowNewBranch));
        menu.Items.Add(Action(AppActions.Commit));
        menu.Items.Add(Action(AppActions.Push));
        menu.Items.Add(Action(AppActions.Stash));
        menu.Items.Add(Action(AppActions.Unstash));
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Local", IsEnabled = false });
        foreach (string branch in await session.Vcs.Repository.BranchesAsync())
        {
            string chosen = branch;
            MenuItem item = MenuChoice(branch, branch == session.Vcs.Branch, () => _ = session.Vcs.CheckoutAsync(chosen));
            menu.Items.Add(item);
        }
        Open(menu, BranchButton);
    }

    /// <summary>Stash Changes: what is open saved first, then every change put away under the message given.</summary>
    public NamePopup? ShowStash()
    {
        if (session.Vcs.Repository is null)
        {
            status.Text = "The folder is not in a Git repository";
            return null;
        }
        NamePopup popup = new("Stash Changes", string.Empty, async message => (await session.Vcs.StashAsync(message)).Problem);
        popup.Done += (_, _) => popups.Close();
        popups.Show(popup);
        return popup;
    }

    /// <summary>Unstash Changes: the stashes to pop, apply or drop (once the person agrees).</summary>
    public async Task<UnstashPopup?> ShowUnstashAsync()
    {
        if (session.Vcs.Repository is null)
        {
            status.Text = "The folder is not in a Git repository";
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

    /// <summary>Create New Branch: from the head, checked out at once, as Rider's is.</summary>
    public void ShowNewBranch()
    {
        NamePopup popup = new("Create New Branch", string.Empty, async name => (await session.Vcs.NewBranchAsync(name)).Problem);
        popup.Done += (_, _) => popups.Close();
        popups.Show(popup);
    }

    /// <summary>Show Diff: the last commit's text beside the file's, in a window over this one. The right side is the
    /// open file's own text when it is open, so an edit or a change put back there is the file's, in the file's colours as
    /// they change.</summary>
    public async Task<DiffWindow?> ShowDiffAsync(string path)
    {
        if (session.Vcs.Repository is null)
        {
            status.Text = "The folder is not in a Git repository";
            return null;
        }
        OpenDocument? open = Editors.Documents.FirstOrDefault(d => d.Path == path);
        TextDocument mine = open?.Text ?? new TextDocument(File.Exists(path) ? FileText.Load(path)?.Text ?? string.Empty : string.Empty);
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
        DiffView view = Highlighted(new(other is null ? Path.GetFileName(path) : title, diff, mine, editable: open is not null, rightColours: open?.Highlighter,
            titles: (version, open is not null ? mineTitle : $"{mineTitle} (read-only)"), highlighting: settings.DiffHighlighting));
        IHighlighter? left = Languages.Highlighters.For(Workspace.Language.ForPath(other ?? path).Name, view.Left.Document);
        if (left is not null)
        {
            view.Left.Highlighter = left;
        }
        DiffWindow window = new(title, view);
        window.Closed += (_, _) => (left as IDisposable)?.Dispose();
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
            // The file's colours as they are now: the language server's once it answers, another server's once it starts again.
            void OnFileChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(OpenDocument.Highlighter))
                {
                    view.Right.Highlighter = file.Highlighter;
                }
            }
            Editors.Closed += OnFileClosed;
            file.PropertyChanged += OnFileChanged;
            window.Closed += (_, _) =>
            {
                Editors.Closed -= OnFileClosed;
                file.PropertyChanged -= OnFileChanged;
                hold.Dispose();
            };
        }
        window.Show(this);
        return window;
    }

    /// <summary>A diff whose Highlighting Differences, once changed, is the one every diff after opens with.</summary>
    private DiffView Highlighted(DiffView view)
    {
        view.HighlightingChanged += (_, chosen) => ApplySettings(settings with { DiffHighlighting = chosen });
        return view;
    }

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
        DiffView view = Highlighted(new(file.Name, diff, mine, editable: false, titles: (leftTitle, commit.Short), highlighting: settings.DiffHighlighting));
        List<IDisposable> colours = [];
        string language = Workspace.Language.ForPath(file.Path).Name;
        if (Languages.Highlighters.For(language, view.Left.Document) is { } left && Languages.Highlighters.For(language, view.Right.Document) is { } right)
        {
            view.Left.Highlighter = left;
            view.Right.Highlighter = right;
            colours.AddRange([.. new[] { left, right }.OfType<IDisposable>()]);
        }
        DiffWindow window = new($"{file.Name}: {commit.Short} {commit.Subject}", view);
        window.Closed += (_, _) => colours.ForEach(c => c.Dispose());
        window.Show(this);
        return window;
    }

    /// <summary>The change bar a gutter click on a line falls on, or null.</summary>
    private static ChangeMark? ChangeAt(OpenDocument document, int line) =>
        document.ChangeMarks.Cast<ChangeMark?>().FirstOrDefault(m => m!.Value.Count == 0
            ? line == m.Value.Line || line == m.Value.Line - 1
            : line >= m.Value.Line && line < m.Value.Line + m.Value.Count);

    /// <summary>A click on a change bar: Rider's popup of what the lines were, with the change's actions.</summary>
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
                _ = ShowChangePopupAsync(Editors.CodeEditor, now, next);
            }
        }
        // The file's changed words are marked in it while the popup shows them, as Rider marks them; an edit since moves them.
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
                _ = RollbackLinesAsync();
            }),
            diff: () => Then(() => _ = ShowDiffAsync(document.Path)));
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
        ShowToolWindow("git");
        GitWindow.ShowLog();
        await GitLog.SelectHashAsync(lines[line].Hash);
        status.Text = $"{lines[line].Short} {lines[line].Summary} ({lines[line].Author}, {lines[line].Time.ToLocalTime():f})";
    }

    /// <summary>Rider's gutter menu where no breakpoint is: annotations on or off.</summary>
    private void ShowGutterMenu(CodeEditor editor, OpenDocument document)
    {
        ContextMenu menu = new();
        MenuItem annotate = new() { Header = session.Vcs.IsAnnotated(document) ? "Close Annotations" : "Annotate with Git Blame", IsEnabled = session.Vcs.Repository is not null };
        annotate.Click += (_, _) => _ = ToggleAnnotationsAsync(document);
        menu.Items.Add(annotate);
        menu.Open(editor);
    }

    /// <summary>Rider's version control actions over the folder's working tree, each needing a repository, and those on a
    /// file the file shown.</summary>
    private void AddVcsActions()
    {
        Add(AppActions.Commit, "Commit...", context =>
        {
            // Rider saves every file before a commit, so the window lists what the editors show.
            Editors.SaveAll();
            ShowToolWindow("commit");
            _ = session.Vcs.RefreshAsync();
            commitView!.FocusMessage();
        }, new("Git", 0, 0));
        Add(AppActions.Push, "Push...", context => _ = session.Vcs.PushAsync(), new("Git", 0, 1));
        Add(AppActions.UpdateProject, "Update Project...", context => _ = session.Vcs.UpdateAsync(), new("Git", 0, 2));
        Add(AppActions.Fetch, "Fetch", context => _ = session.Vcs.FetchAsync(), new("Git", 0, 3));
        Add(AppActions.Branches, "Branches...", _ => ShowBranches(), new("Git", 0, 4));
        Add(AppActions.Stash, "Stash Changes...", _ => ShowStash(), new("Git", 1, 0));
        Add(AppActions.Unstash, "Unstash Changes...", context => _ = ShowUnstashAsync(), new("Git", 1, 1));
        Add(AppActions.ShowDiff, "Show Diff", context => _ = ShowDiffAsync(context.File!.Path), new("Git", 2, 0), onFile: true);
        Add(AppActions.Annotate, "Annotate with Git Blame", context => _ = ToggleAnnotationsAsync(context.File!), new("Git", 2, 1), onFile: true);
        Add(AppActions.RollbackLines, "Rollback Lines", context => _ = RollbackLinesAsync(), new("Git", 2, 2), onFile: true);
        Add(AppActions.NextChange, "Next Change", _ => GoToChange(1), new("Navigate", 3, 2), onFile: true);
        Add(AppActions.PreviousChange, "Previous Change", _ => GoToChange(-1), new("Navigate", 3, 3), onFile: true);

        void Add(string id, string title, Action<ActionContext> run, MenuPlace menu, bool onFile = false) =>
            actions!.Add(new IdeAction(id, title, "Git", run)
            {
                WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? NoRepository : onFile ? NeedsFile(context) : null),
                Menus = [menu],
            });
    }

    private const string NoRepository = "The open folder is not in a Git repository";

    private async Task RollbackAsync(IReadOnlyList<FileChange> changes)
    {
        if (changes.Count == 0)
        {
            status.Text = "No files are chosen to roll back";
            return;
        }
        // The files are listed, as Rider's Rollback Changes lists them, so what goes back is seen before it does.
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

    /// <summary>Next Change and Previous Change: the caret to the start of the next or previous change bar, round
    /// past the file's end as Rider goes.</summary>
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

    /// <summary>Rider's Add to .gitignore: the file's path from the repository's top, a line of its .gitignore.</summary>
    private void Ignore(string path)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            return;
        }
        string ignore = Path.Combine(repository.Root, ".gitignore");
        string line = Path.GetRelativePath(repository.Root, path).Replace('\\', '/');
        string before = File.Exists(ignore) ? File.ReadAllText(ignore) : string.Empty;
        File.WriteAllText(ignore, before + (before.Length > 0 && !before.EndsWith('\n') ? "\n" : string.Empty) + line + "\n");
        status.Text = $"Added {line} to .gitignore";
        _ = session.Vcs.RefreshAsync();
    }
}
