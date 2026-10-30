using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The window's Find in Files and Replace in Files, over the folder's index and the open files' own text, in
/// the scope and under the file mask chosen last: the solution, a project, a directory or a named scope. The scope is
/// kept with the folder and the mask for every folder.</summary>
public partial class MainWindow
{
    internal const string RecentlyViewedScope = "Recently Viewed Files";
    internal const string OpenFilesScope = "Open Files";
    internal const string CurrentFileScope = "Current File";

    /// <summary>Find in Files' mask and whether a directory's subdirectories are searched, every folder's.</summary>
    private FindSettingsState findSettings = new();

    private (Task Until, TaskCompletionSource Reached)? holdFindEvery;

    /// <summary>Find in Files (Ctrl+Shift+F) or, with replace, Replace in Files (Ctrl+Shift+H), seeded with the
    /// selection's word, in the scope given (from the Solution view), else the one chosen last.</summary>
    public Navigation.FindInFilesPopup? ShowFindInFiles(bool replace, SearchScope? on = null)
    {
        if (Folder is null)
        {
            return null;
        }
        Navigation.FindChoices choices = new(on ?? KeptScope(), Projects, session.FindScope.RecentDirectories, ScopeNames(), findSettings.RecentFileMasks);
        Navigation.FindInFilesPopup popup = new((query, scope, cancellation) => SearchFiles(query, scope, cancellation, FileSearch.MostMatches), Editors.TextOf, path => Editors.ParseOptions(path), replace, navigator.SelectedWord(), choices, settings.Ligatures);
        popup.ScopeChanged += (_, scope) => KeepFindScope(scope, closing: false);
        // However it closes: a match chosen, Escape, a click outside.
        popup.DetachedFromVisualTree += (_, _) => KeepFindScope(popup.Scope, closing: true);
        popup.ReplaceRequested += (_, asked) => IdeErrors.Observe(ReplaceAllAsync(popup, asked));
        popups.Show(popup, match =>
        {
            if (match is not null)
            {
                NavigateTo(match.Path, new TextPosition(match.Line, match.Column));
                if (Editors.Active?.Path == match.Path)
                {
                    int at = Editors.CodeEditor.Document.Snapshot.GetOffset(new TextPosition(match.Line, match.Column));
                    Editors.CodeEditor.Select(new TextRange(at, match.Length));
                }
            }
        });
        return popup;
    }

    /// <summary>The scope Find in Files takes on rows of the Solution view: the solution's, a project's, a folder's as
    /// a directory, and anything else (a file, a solution folder, several rows) a scope of its own named for them
    /// ("Program.cs", "Program.cs and 2 more item(s)"); the rest as kept. Null for rows with no files (a project's
    /// dependencies), which leave the scope kept.</summary>
    public SearchScope? ScopeOf(IReadOnlyList<ExplorerNode> rows)
    {
        rows = [.. rows.Where(r => r.Path is not null && r.Kind is NodeKind.File or NodeKind.Folder or NodeKind.Project or NodeKind.Solution
            || r.Kind == NodeKind.SolutionFolder)];
        SearchScope kept = KeptScope();
        switch (rows)
        {
            case []:
                return null;
            case [{ Kind: NodeKind.Solution }]:
                return kept with { Kind = SearchScopeKind.Solution };
            case [{ Kind: NodeKind.Project, Path: { } project }]:
                return kept with { Kind = SearchScopeKind.Project, Project = project };
            case [{ Kind: NodeKind.Folder, Path: { } directory }]:
                return kept with { Kind = SearchScopeKind.Directory, Directory = directory };
        }
        List<string> files = [];
        List<string> folders = [];
        foreach (ExplorerNode row in rows)
        {
            switch (row)
            {
                case { Kind: NodeKind.File, Path: { } file }:
                    files.Add(file);
                    break;
                case { Kind: NodeKind.Folder, Path: { } directory }:
                    folders.Add(directory);
                    break;
                case { Kind: NodeKind.Project, Path: { } project }:
                    folders.Add(Path.GetDirectoryName(project)!);
                    break;
                case { Kind: NodeKind.SolutionFolder }:
                    folders.AddRange(Explorer.Tree.ProjectsOf(row).Select(p => Path.GetDirectoryName(p)!));
                    break;
                case { Kind: NodeKind.Solution }:
                    return kept with { Kind = SearchScopeKind.Solution };
            }
        }
        string name = rows.Count == 1 ? rows[0].Name : $"{rows[0].Name} and {Plural.Of(rows.Count - 1, "more item")}";
        session.ChosenScope = (name, files, folders);
        return kept with { Kind = SearchScopeKind.Scope, Named = name };
    }

    /// <summary>The scope kept, which the popup opens on: the project and directory chosen last when that is the scope,
    /// else the active file's, ready for a switch to them.</summary>
    private SearchScope KeptScope()
    {
        string? active = Editors.Active?.Path;
        FindScopeState last = session.FindScope;
        string? project = last.Scope == SearchScopeKind.Project && Projects.Any(p => p.Path == last.Project) ? last.Project
            : (active is null ? null : ProjectOf(active)?.Path) ?? last.Project;
        string? directory = last.Scope == SearchScopeKind.Directory && last.Directory is not null ? last.Directory
            : (active is null ? null : Path.GetDirectoryName(active)) ?? last.Directory ?? Folder;
        string named = last.Named is { } kept && ScopeNames().Contains(kept) ? kept : OpenFilesScope;
        return new SearchScope(last.Scope, project, directory, findSettings.Recursive, named, findSettings.FileMask);
    }

    /// <summary>The named scopes, in their order, the Solution view's rows Find in Files was opened on last at the
    /// end.</summary>
    private IReadOnlyList<string> ScopeNames() =>
        [RecentlyViewedScope, OpenFilesScope, CurrentFileScope, .. session.ChosenScope is { } chosen ? [chosen.Name] : Array.Empty<string>()];

    /// <summary>Keeps what the popup shows (with the folder, and the mask for every folder, while the window keeps
    /// state); as it closes, its directory and mask go to the top of those used lately.</summary>
    private void KeepFindScope(SearchScope scope, bool closing)
    {
        string? directory = string.IsNullOrWhiteSpace(scope.Directory) ? null : scope.Directory.Trim();
        FindScopeState last = session.FindScope;
        session.FindScope = last with
        {
            Scope = scope.Kind,
            Project = scope.Project ?? last.Project,
            Directory = directory ?? last.Directory,
            Named = scope.Named ?? last.Named,
        };
        findSettings = findSettings with { FileMask = string.IsNullOrWhiteSpace(scope.Mask) ? null : scope.Mask.Trim(), Recursive = scope.Recursive };
        if (closing && scope.Kind == SearchScopeKind.Directory && directory is not null)
        {
            session.FindScope = session.FindScope with { RecentDirectories = [.. session.FindScope.RecentDirectories.Where(d => d != directory).Prepend(directory).Take(FindScopeState.RecentLimit)] };
        }
        if (closing && findSettings.FileMask is { } mask)
        {
            findSettings = findSettings with { RecentFileMasks = [.. findSettings.RecentFileMasks.Where(m => m != mask).Prepend(mask).Take(FindSettingsState.RecentLimit)] };
        }
        if (keepsWorkspace)
        {
            session.State?.Find.Set(session.FindScope);
            state?.Find.Set(findSettings);
        }
    }

    /// <summary>The files a scope searches: the project's own (a project nested in its folder has its own), the
    /// directory's, a named scope's, or every file of the folder; each passing the mask.</summary>
    private SearchPlace Place(SearchScope scope)
    {
        FileMask? mask = FileMask.Parse(scope.Mask);
        SearchPlace Named(IReadOnlyList<string> files) => new(false, [], true, [], files, mask);
        switch (scope.Kind)
        {
            case SearchScopeKind.Project:
                if (Projects.FirstOrDefault(p => p.Path == scope.Project) is not { } project)
                {
                    return Named([]);
                }
                IReadOnlyList<ProjectModel> projects = Projects;
                return new SearchPlace(false, [], true, [], FolderWalk.ProjectFiles(project, projects), mask);
            case SearchScopeKind.Directory when !string.IsNullOrWhiteSpace(scope.Directory):
                string directory = PathNames.Canonical(Path.IsPathRooted(scope.Directory) ? scope.Directory : Path.Combine(Folder!, scope.Directory));
                return new SearchPlace(false, [directory], scope.Recursive, [], [], mask);
            case SearchScopeKind.Directory:
                return Named([]);
            case SearchScopeKind.Scope:
                return scope.Named switch
                {
                    RecentlyViewedScope => Named([.. session.RecentFiles]),
                    OpenFilesScope => Named([.. Editors.Documents.Select(d => d.Path)]),
                    CurrentFileScope => Named(Editors.Active is { } shown ? [shown.Path] : []),
                    { } named when session.ChosenScope is { } chosen && chosen.Name == named => new SearchPlace(false, chosen.Folders, true, [], chosen.Files, mask),
                    _ => Named([]),
                };
            default:
                return SearchPlace.Everything(mask);
        }
    }

    /// <summary>The popup's Replace All, once confirmed: every match in its scope replaced, those past the list's limit
    /// found first, but in a file it leaves (<see cref="FilesFound.Unreplaceable"/>), said in the status bar, and the
    /// popup's search run again over what is left.</summary>
    private async Task ReplaceAllAsync(Navigation.FindInFilesPopup popup, (SearchQuery Query, string Replacement, FilesFound Found) asked)
    {
        FilesFound found = asked.Found;
        if (found.Matches.Count >= FileSearch.MostMatches)
        {
            (FilesFound? every, string? why) = await FindEveryMatchAsync(asked.Query, popup.Scope, CancellationToken.None);
            if (every is null)
            {
                status.Text = why;
                return;
            }
            found = every;
        }
        string? left = FilesReplaced.Listed(found.Unreplaceable, Path.GetFileName);
        if (found.Replaceable.Count == 0)
        {
            status.Text = $"Nothing replaced; not replaced: {left}";
            return;
        }
        string what = FilesReplaced.Count(found.Replaceable);
        string leaving = left is null ? string.Empty : $" Not replaced: {left}.";
        if (await Confirm("Replace in Files", $"Replace {what} with '{asked.Replacement}'? A closed file is written; one Undo takes back every file's change.{leaving}", "Replace All"))
        {
            FilesReplaced done = await ReplaceInFilesAsync(asked.Query, asked.Replacement, found);
            IReadOnlyList<SearchMatch> replaced = done.Replaced(found.Matches);
            string? refused = done.NotReplaced(Path.GetFileName);
            status.Text = string.Join("; ", [replaced.Count > 0 ? $"Replaced {FilesReplaced.Count(replaced)}" : "Nothing replaced", .. done.Converted, .. refused is null ? [] : new[] { refused }]);
            await popup.SearchAsync();
        }
    }

    /// <summary>Replaces a query's matches where Find in Files found them, as one command of Undo and one change set of Local
    /// History named for it: an open file's matches alone, so its carets and folds stay put; a closed one written off the
    /// window's thread in its encoding (UTF-8 when that cannot hold the replacement, said), answered for once it has landed.
    /// A file refused (read-only on disk or in the editor, access denied, held past the save's wait), one a regex ran too
    /// long in, or one edited while it was searched is left as it was and named with why; the rest are replaced all the same.</summary>
    public async Task<FilesReplaced> ReplaceInFilesAsync(SearchQuery query, string replacement, FilesFound found)
    {
        SearchPattern pattern = SearchPattern.For(query, TextSearch.Apart);
        List<(string Path, TextSnapshot? Open)> files = [];
        List<(string Path, string Why)> refused = [.. found.Unreplaceable];
        foreach (string path in found.Replaceable.Select(m => m.Path).Distinct())
        {
            OpenDocument? open = Editors.Find(path);
            if (open is { HoldsText: false })
            {
                // A preview's or a workbook's tab holds no text of the file's to replace in, and its bytes are no text.
                continue;
            }
            if (open is { IsReadOnly: true })
            {
                refused.Add((path, FileSearch.ReadOnlyWhy));
                continue;
            }
            files.Add((path, open?.Text.Snapshot));
        }
        List<FileSearch.Replacing> planned = await BackgroundWork.Run(() => files.Select(f => FileSearch.Plan(f.Path, f.Open, pattern, replacement)).ToList());
        List<(string Path, string? Note)> written = [];
        string name = FilesReplaced.Title(query, replacement);
        using History.LocalHistory.Activity? activity = session.LocalHistory?.During(name);
        IReadOnlyList<(string Path, Exception Refused)> unlanded;
        using (CommandHistory.Recording recording = Editors.Commands.Record(name))
        {
            List<FileSearch.Replacing> closed = [];
            foreach (FileSearch.Replacing plan in planned)
            {
                if (plan.Why is { } why)
                {
                    refused.Add((plan.Path, why));
                }
                else if (plan.Searched is { } searched)
                {
                    if (Editors.Find(plan.Path) is not { HoldsText: true } open || !ReferenceEquals(open.Text.Snapshot, searched))
                    {
                        refused.Add((plan.Path, FileSearch.ChangedWhy));
                    }
                    else if (plan.Edits.Count > 0)
                    {
                        open.Text.BreakUndoGroup();
                        open.Text.Edit(plan.Edits, open.Selections, TextEditing.MapThrough(open.Selections, plan.Edits));
                        open.Text.BreakUndoGroup();
                    }
                }
                else if (plan is { Closed: not null, After: not null })
                {
                    closed.Add(plan);
                }
            }
            recording.TakeOpen();
            // From here Local History names only the replace's own writes, not what is saved meanwhile.
            activity?.Dispose();
            List<FileSearch.Written> writes = closed.Count == 0 ? [] : await Editors.Commands.InTurnAsync(name, [.. closed.Select(c => c.Path)], wrote =>
            {
                using IDisposable? own = activity?.Own();
                List<FileSearch.Written> made = [];
                foreach (FileSearch.Replacing plan in closed)
                {
                    if (FileSearch.Write(plan) is { } write)
                    {
                        made.Add(write);
                    }
                    wrote(plan.Path);
                }
                return made;
            });
            foreach (FileSearch.Written write in writes)
            {
                if (write.Refused is { } why)
                {
                    refused.Add((write.Path, why));
                }
                else if (write is { Before: { } before, After: { } after })
                {
                    recording.Wrote(write.Path, before, after);
                    written.Add((write.Path, write.Converted));
                }
            }
            unlanded = await EditorArea.LandedAsync(written.Select(w => w.Path), waiting => status.Text = waiting);
            foreach ((string path, _) in unlanded)
            {
                recording.Unwritten(path);
            }
        }
        refused.AddRange(unlanded.Select(u => (u.Path, FileSearch.Refusal(u.Refused))));
        return new FilesReplaced([.. written.Where(w => !unlanded.Any(u => u.Path == w.Path)).Select(w => w.Note).OfType<string>()], refused) { Named = activity };
    }

    /// <summary>Recent Files (Ctrl+,): the files opened last, the latest first, narrowed as a name is typed; a file
    /// opens where its caret was left. Its Show edited only lists the files edited, the latest first, which is what
    /// Recently Changed Files opens it on.</summary>
    public Navigation.SymbolPopup ShowRecentFiles(bool editedOnly = false)
    {
        string root = Folder ?? string.Empty;
        bool edited = editedOnly;
        Navigation.SymbolPopup popup = new("Recent Files", "A file's name", (query, _) =>
        {
            IEnumerable<string> listed = edited ? session.Navigation.EditedFiles.Where(p => p != Editors.Active?.Path) : session.RecentFiles.Skip(Editors.Active is null ? 0 : 1);
            if (query.Trim().Length > 0)
            {
                listed = listed.Where(p => FuzzyMatch.Score(query.Trim(), Path.GetFileName(p)) is not null);
            }
            return Task.FromResult<IReadOnlyList<Navigation.SymbolRow>>([.. listed.Select(p => new Navigation.SymbolRow(
                Path.GetFileName(p), null, Path.GetDirectoryName(PathNames.Shown(p, root)),
                Navigation.SymbolRow.FileKind, p, new TextPosition(0, 0)))]);
        }, option: ("Show edited only", editedOnly));
        popup.OptionChanged += (_, on) => edited = on;
        popups.Show(popup, row =>
        {
            if (row is not null)
            {
                OpenFile(row.Path);
            }
        });
        return popup;
    }

    /// <summary>Find in Files for an agent: a query's matches in a scope under its mask, as the popup searches them, at most
    /// the list's limit.</summary>
    public Task<FilesFound> FindInFilesAsync(SearchQuery query, SearchScope scope, CancellationToken cancellation) =>
        SearchFiles(query, scope, cancellation, FileSearch.MostMatches);

    /// <summary>Every match of a query in a scope, past the list's limit, as Replace All and an agent's replace_in_files
    /// replace them, each kept without its line's text: found off the window's thread while the window's work says so,
    /// which Stop ends as <paramref name="cancel"/> does. Null with why when Stop ended it or another such find is under way.</summary>
    public async Task<(FilesFound? Found, string? Why)> FindEveryMatchAsync(SearchQuery query, SearchScope scope, CancellationToken cancel)
    {
        if (work.Begin(AppActions.ReplaceInFiles, "Finding every match to replace…") is not { } finding)
        {
            return (null, ActionWork.Busy(work.Of(AppActions.ReplaceInFiles)!));
        }
        using (finding)
        {
            using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(cancel, finding.Stopping);
            try
            {
                if (holdFindEvery is { } hold)
                {
                    holdFindEvery = null;
                    hold.Reached.TrySetResult();
                    await hold.Until.WaitAsync(stopping.Token);
                }
                return (await SearchFiles(query, scope, stopping.Token, int.MaxValue, lines: false), null);
            }
            catch (OperationCanceledException) when (finding.IsStopped)
            {
                return (null, "Stopped finding every match to replace: nothing was replaced");
            }
        }
    }

    /// <summary>The next find of every match (<see cref="FindEveryMatchAsync"/>) waits until <paramref name="until"/> has
    /// ended, or it is stopped, before it searches, as a large folder keeps it, for a test of what goes on meanwhile: done
    /// once it is waiting.</summary>
    public Task HoldNextFindEvery(Task until)
    {
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        holdFindEvery = (until, reached);
        return reached.Task;
    }

    private Task<FilesFound> SearchFiles(SearchQuery query, SearchScope scope, CancellationToken cancellation, int most, bool lines = true)
    {
        return FileSearch.FindAsync(session.Index.Root ?? Folder!, session.Index.Files, Place(scope), query, Editors.Texts(), cancellation, most, lines);
    }
}
