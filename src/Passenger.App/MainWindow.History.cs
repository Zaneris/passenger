using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Vcs;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App;

/// <summary>A file's history and the versions it compares with: Show History (a file's commits, renames followed, or a
/// folder's) and Show History for Selection (the commits that changed some lines) as tabs of the Git window, and
/// Compare with Branch or Tag and Compare with Revision, each a version's text beside the file's. They act on the file
/// shown, or on the Explorer's chosen row while it has the keys.</summary>
public partial class MainWindow
{
    private void AddHistoryActions()
    {
        Add(AppActions.CompareWithRevision, "Compare with Revision…", context => IdeErrors.Observe(ShowCompareWithRevisionAsync(Target(context, fileOnly: true)!)), 3, fileOnly: true);
        Add(AppActions.CompareWithBranch, "Compare with Branch or Tag…", context => IdeErrors.Observe(ShowCompareWithBranchAsync(Target(context, fileOnly: true)!)), 4, fileOnly: true);
        Add(AppActions.ShowHistory, "Show History", context => IdeErrors.Observe(ShowHistoryAsync(Target(context, fileOnly: false)!)), 5, fileOnly: false);
        actions!.Add(new IdeAction(AppActions.ShowHistoryForSelection, "Show History for Selection…", "Git", context =>
        {
            (int first, int last) = SelectedLines(context.Editor);
            IdeErrors.Observe(ShowSelectionHistoryAsync(context.File!.Path, first, last));
        })
        {
            WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? Refusals.NoRepository : Refusals.NeedsFile(context)),
            Menus = [new MenuPlace("Git", 2, 6)],
        });

        void Add(string id, string title, Action<ActionContext> run, int order, bool fileOnly) =>
            actions!.Add(new IdeAction(id, title, "Git", run)
            {
                WhyNot = context => NeedsFolder(context) ?? (session.Vcs.Repository is null ? Refusals.NoRepository : WhyNoTarget(context, fileOnly)),
                Menus = [new MenuPlace("Git", 2, order)],
            });
    }

    /// <summary>What a file action runs on: the Explorer's row its menu opened on, or its one chosen row while the Explorer
    /// has the keys (a project's or the solution's folder for theirs), else the file shown.</summary>
    private static string? Target(ActionContext context, bool fileOnly)
    {
        if (context.ExplorerRow is { } row)
        {
            return Target(row, fileOnly);
        }
        if (context.ToolWindow?.Id != ToolWindowIds.Explorer)
        {
            return context.File?.Path;
        }
        return context.Row is IReadOnlyList<ExplorerNode> { Count: 1 } chosen ? Target(chosen[0], fileOnly) : null;
    }

    private static string? Target(ExplorerNode node, bool fileOnly) => node switch
    {
        { Kind: NodeKind.File, Path: { } file } => file,
        { Kind: NodeKind.Folder, Path: { } folder } when !fileOnly => folder,
        { Kind: NodeKind.Project or NodeKind.Solution, Path: { } file } when !fileOnly => Path.GetDirectoryName(file),
        _ => null,
    };

    private static string? WhyNoTarget(ActionContext context, bool fileOnly) =>
        Target(context, fileOnly) is not null ? null
        : context.ToolWindow?.Id != ToolWindowIds.Explorer ? Refusals.NoFile
        : fileOnly ? "Choose a file in the Explorer" : "Choose a file or folder in the Explorer";

    /// <summary>The lines a selection covers (the caret's alone with none), less the line it ends at the start of.</summary>
    private static (int First, int Last) SelectedLines(CodeEditor editor)
    {
        Selection primary = editor.Selections.Primary;
        TextSnapshot text = editor.Document.Snapshot;
        TextPosition start = text.GetPosition(primary.Start);
        TextPosition end = text.GetPosition(primary.End);
        return (start.Line, end.Line > start.Line && end.Column == 0 ? end.Line - 1 : end.Line);
    }

    /// <summary>Show History: a file's commits from HEAD, followed through its renames, or a folder's, in a tab of the Git
    /// window (the one already open for it, if any).</summary>
    public async Task<ToolWindows.GitLogView?> ShowHistoryAsync(string path)
    {
        if (await ScopeOrSayAsync(path, null) is not { } scope)
        {
            return null;
        }
        // Its commits are read as its tab is first shown.
        ShowToolWindow(ToolWindowIds.Git);
        return GitWindow.ShowHistory(scope, Path.GetFileName(Path.TrimEndingDirectorySeparator(scope.Path)));
    }

    /// <summary>Show History for Selection: the commits from HEAD that changed a file's lines (from 0, as the editor has
    /// them), in a tab of the Git window.</summary>
    public async Task<ToolWindows.GitLogView?> ShowSelectionHistoryAsync(string path, int first, int last)
    {
        if (await ScopeOrSayAsync(path, (first, last)) is not { } scope)
        {
            return null;
        }
        ShowToolWindow(ToolWindowIds.Git);
        string lines = first == last ? $"line {first + 1}" : $"lines {first + 1}–{last + 1}";
        return GitWindow.ShowHistory(scope, $"{Path.GetFileName(path)}, {lines}");
    }

    /// <summary>The history to read for a path, or null with the reason why there is none in the status bar.</summary>
    private async Task<HistoryScope?> ScopeOrSayAsync(string path, (int First, int Last)? lines)
    {
        try
        {
            return await HistoryScopeAsync(path, lines);
        }
        catch (InvalidOperationException none)
        {
            status.Text = none.Message;
            return null;
        }
    }

    /// <summary>What Show History reads for a path, the window's and an agent's alike: a file or a folder, or a file's
    /// lines (from 0, in its text as the editor has it, unsaved changes and all) as the last commit numbers them. Where
    /// Git has no history to show, an <see cref="InvalidOperationException"/> says why.</summary>
    public async Task<HistoryScope> HistoryScopeAsync(string path, (int First, int Last)? lines = null)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            throw new InvalidOperationException(Refusals.NoRepository);
        }
        path = PathNames.Canonical(path);
        if (!repository.IsInside(path))
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} is not in the Git repository");
        }
        if (lines is not { } chosen)
        {
            return new HistoryScope(path, IsFolder: Directory.Exists(path));
        }
        if (Directory.Exists(path))
        {
            throw new InvalidOperationException("Show History for Selection needs a file, not a folder");
        }
        // The editor's lines are the last commit's once the changes above them are taken back.
        OpenDocument? open = Editors.Find(path) is { HoldsText: true } held ? held : null;
        FileDiff? diff = open is not null ? await session.Vcs.DiffAsync(open) : null;
        if (diff is null && open is null && await session.Vcs.HeadTextAsync(path) is { } head && EditorArea.ReadText(path) is { } saved)
        {
            TextSnapshot text = new TextDocument(saved).Snapshot;
            diff = await BackgroundWork.Run(() => FileDiff.Compare(head, text));
        }
        (int First, int Last)? committed = diff is null ? (Math.Min(chosen.First, chosen.Last), Math.Max(chosen.First, chosen.Last)) : diff.HeadLines(chosen.First, chosen.Last);
        if (committed is not { } range)
        {
            throw new InvalidOperationException("The lines chosen are not committed yet");
        }
        return new HistoryScope(path, FirstLine: range.First, LastLine: range.Last);
    }

    /// <summary>Compare with Branch or Tag: the branches and tags to choose from, the current branch first; the one chosen
    /// opens its text of the file beside the file's.</summary>
    public async Task<ChoicePopup?> ShowCompareWithBranchAsync(string path)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            status.Text = Refusals.NoRepository;
            return null;
        }
        IReadOnlyList<GitRef> refs = await repository.RefNamesAsync();
        string? current = session.Vcs.Branch;
        ChoicePopup popup = new($"Compare {Path.GetFileName(path)} with Branch or Tag", "Search for branches and tags",
            [.. refs.Select(r => new Choice(r.Name, r.Kind switch
            {
                RefKind.Local when r.Name == current => "current branch",
                RefKind.Local => "local branch",
                RefKind.Remote => "remote branch",
                _ => "tag",
            }, r))],
            "No branch or tag matches");
        popups.Show(popup, chosen =>
        {
            if (chosen?.Value is GitRef named)
            {
                IdeErrors.Observe(CompareWithAsync(path, named.FullName, named.Name, path));
            }
        });
        return popup;
    }

    /// <summary>Compare with Revision: the file's commits (its history, renames followed) to choose from; the one chosen
    /// opens its text of the file, under the name it had then, beside the file's.</summary>
    public async Task<ChoicePopup?> ShowCompareWithRevisionAsync(string path)
    {
        if (await ScopeOrSayAsync(path, null) is not { IsFolder: false } scope)
        {
            return null;
        }
        IReadOnlyList<HistoryCommit> history = await session.Vcs.Repository!.HistoryAsync(scope);
        ChoicePopup popup = new($"Compare {Path.GetFileName(path)} with Revision", "Search by message, author or hash",
            [.. history.Select(h => new Choice(h.Commit.Subject, $"{h.Commit.Short}  {h.Commit.Author}  {ToolWindows.GitLogView.When(h.Commit.Time)}", h))],
            history.Count == 0 ? $"Git has no history of {Path.GetFileName(path)}" : "No revision matches");
        popups.Show(popup, chosen =>
        {
            if (chosen?.Value is HistoryCommit revision)
            {
                IdeErrors.Observe(CompareWithAsync(path, revision.Commit.Hash, revision.Commit.Short, revision.Changes.FirstOrDefault()?.Path ?? path));
            }
        });
        return popup;
    }

    /// <summary>A version of a file beside the file as it is now: <paramref name="revision"/>'s text of it at
    /// <paramref name="then"/> (where it was in that version), the right side the open file's own text when it is open.</summary>
    public async Task<DiffWindow?> CompareWithAsync(string path, string revision, string version, string then)
    {
        if (session.Vcs.Repository is not { } repository)
        {
            status.Text = Refusals.NoRepository;
            return null;
        }
        if (await repository.TextAtAsync(revision, then) is not { } theirs)
        {
            status.Text = $"{Path.GetFileName(then)} is not in {version}";
            return null;
        }
        OpenDocument? open = Editors.Find(path) is { HoldsText: true } held ? held : null;
        TextDocument mine = open?.Text ?? new TextDocument(EditorArea.ReadText(path) ?? string.Empty);
        TextSnapshot text = mine.Snapshot;
        FileDiff diff = await BackgroundWork.Run(() => FileDiff.Compare(theirs, text));
        return OpenLocalDiff(path, open, mine, diff, version);
    }
}
