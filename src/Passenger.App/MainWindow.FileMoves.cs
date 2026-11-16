using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Workspace;
using E = Passenger.Editor.EditorActions;

namespace Passenger.App;

/// <summary>The Explorer's Cut, Copy and Paste, and its drags: files and folders cut or copied are pasted into a folder
/// or a project's (a cut once, dimmed until then), or dragged onto one to move them (copied with the platform's copy
/// key held). A copy into its own folder asks the copy's name, and a name already taken there asks whether to replace
/// what has it. A move carries its open files' tabs, undo and carets, saved first, and what knows a file by its path
/// follows it, its project's items too. A file's dependents, nested under it in the Solution view, go where it
/// goes. Each paste or drop is one command of Undo.</summary>
public partial class MainWindow
{
    /// <summary>What Cut or Copy last put on the clipboard, whose text is their paths: a Paste finds them only while the
    /// clipboard still holds that text, so text copied since is never taken for them.</summary>
    private (IReadOnlyList<string> Paths, bool Cut, string Text)? held;

    private const string NothingHeld = "Nothing to paste: no files or folders were cut or copied";

    /// <summary>The rows Cut or Copy on a row takes: it, or every file and folder chosen with it.</summary>
    private IEnumerable<ExplorerNode> RowsHeld(ExplorerNode node)
    {
        IReadOnlyList<ExplorerNode> chosen = Explorer.SelectedNodes;
        return (chosen.Contains(node) ? chosen : [node]).Where(n => n.IsMovable);
    }

    /// <summary>Cut or Copy on a row: it, or every file and folder chosen with it, but those inside another of them.</summary>
    private async Task HoldAsync(ExplorerNode node, bool cut)
    {
        IReadOnlyList<string> paths = Outermost([.. RowsHeld(node).Select(n => n.Path!)]);
        if (paths.Count == 0)
        {
            return;
        }
        string text = string.Join(Environment.NewLine, paths);
        held = (paths, cut, text);
        // What is nested under a file cut goes with it, so it dims with it.
        Explorer.Tree.SetCut(cut ? [.. paths, .. DependentsOf(paths).Values.SelectMany(d => d)] : []);
        if (Clipboard is { } clipboard)
        {
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        }
        status.Text = $"{(cut ? "Cut" : "Copied")} {Plural.Listed([.. paths.Select(Path.GetFileName).OfType<string>()])}";
    }

    /// <summary>Paste on a row: what was cut or copied goes into the folder the row stands for (<see cref="ExplorerNode.Into"/>).</summary>
    private async Task PasteAsync(ExplorerNode node)
    {
        if (node.Into is not { } into)
        {
            return;
        }
        string? text = Clipboard is { } clipboard ? await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) : null;
        if (held is not { } what || text != what.Text)
        {
            Forget();
            status.Text = NothingHeld;
            return;
        }
        if (what.Cut)
        {
            // A cut is pasted once.
            Forget();
        }
        await PutAsync(what.Paths, into, copy: !what.Cut);
    }

    private void Forget()
    {
        held = null;
        Explorer.Tree.SetCut([]);
    }

    private Task DropAsync(ExplorerDrop drop)
    {
        return drop.Target.Into is { } into ? PutAsync(Outermost([.. drop.Items.Select(i => i.Path!)]), into, drop.Copy) : Task.CompletedTask;
    }

    /// <summary>The paths, each once (two rows of the Solution view may show one file), but those inside a folder among
    /// them, found by each one's folders rather than by every other path: thousands chosen at once would otherwise be
    /// compared in pairs.</summary>
    private static IReadOnlyList<string> Outermost(IReadOnlyList<string> paths)
    {
        HashSet<string> chosen = new(paths, PathNames.Comparer);
        return [.. paths.Where(path => !Above(path).Any(chosen.Contains)).Distinct(PathNames.Comparer)];

        static IEnumerable<string> Above(string path)
        {
            for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                yield return folder;
            }
        }
    }

    /// <summary>Moves or copies files and folders into a folder: what is there already stays put, a folder never goes
    /// inside itself, two of one name never go at all, a copy into its own folder is named by the person, and a name taken
    /// there is replaced (to the Trash) only once they agree. A move or a copy, with what follows it and what it replaced, is
    /// one command, which Undo takes back whole, a copy going to the Trash. The status bar says what went where and what could
    /// not; the last one put is chosen in the Explorer. Answers with where each went.</summary>
    public async Task<IReadOnlyList<string>> PutAsync(IReadOnlyList<string> paths, string into, bool copy)
    {
        into = PathNames.Canonical(into);
        // A Godot file's .uid or .import copied with it is made for its copy as Godot's duplicate makes one.
        HashSet<string> chosen = new(paths, PathNames.Comparer);
        paths = copy ? [.. paths.Where(p => !GodotUids.IsBeside(p) || !chosen.Contains(GodotUids.BesideOf(p)) || session.GodotOf(p) is null)] : paths;
        // A file's dependents go with it, renamed as a copy's name asks; one chosen with its parent goes as the parent's.
        IReadOnlyDictionary<string, IReadOnlyList<string>> following = DependentsOf(paths);
        HashSet<string> dependents = [.. following.Values.SelectMany(d => d)];
        // Two going where one name can be would leave the first replaced by the second, which no Undo could bring back.
        StringComparer names = PathNames.NamesIn(into);
        List<(string Source, string Name)> claims = [.. paths.Where(p => !dependents.Contains(p))
            .SelectMany(p => following[p].Prepend(p).Select(f => (Source: f, Name: Path.GetFileName(f))))];
        if (claims.GroupBy(c => c.Name, names).FirstOrDefault(g => g.Count() > 1) is { } clash)
        {
            status.Text = $"Nothing was {(copy ? "copied" : "moved")}: {Plural.Listed([.. clash.Select(c => $"{c.Name} from {Path.GetFileName(Path.GetDirectoryName(c.Source))}")])} would each be {clash.Key} in {Path.GetFileName(into)}";
            return [];
        }
        HashSet<string> claimed = new(claims.Select(c => c.Name), names);
        // The folders moving elsewhere, whose projects' design-time builds a move waits for.
        List<string> elsewhere = [.. paths.Where(p => Directory.Exists(p) && !PathNames.Within(into, p) && !PathNames.Same(Path.Combine(into, Path.GetFileName(p)), p))];
        using IDisposable? releasing = copy ? null : await LetGoAsync(elsewhere,
            $"Waiting for the design-time {Plural.Noun(elsewhere.Count, "build")} of {Plural.Listed([.. elsewhere.Select(Path.GetFileName).OfType<string>()])} to end before {Plural.Agreeing(elsewhere.Count, "it moves", "they move")}…");
        using CommandHistory.Recording recording = Editors.Commands.RecordWrites(
            $"{(copy ? "Copy" : "Move")} {Plural.Listed([.. paths.Where(p => !dependents.Contains(p)).Select(Path.GetFileName).OfType<string>()])} to {Path.GetFileName(into)}");
        List<string> put = [];
        List<(string From, string To)> moved = [];
        // The ends of the moves, followed with the copies once all are put.
        List<string> came = [];
        List<string> refused = [];
        List<string> kept = [];
        foreach (string source in paths.Where(p => !dependents.Contains(p)))
        {
            string name = Path.GetFileName(source);
            if (!File.Exists(source) && !Directory.Exists(source))
            {
                refused.Add($"{name} is no longer there");
                continue;
            }
            if (Directory.Exists(source) && PathNames.Within(into, source))
            {
                refused.Add($"{name} cannot go inside itself");
                continue;
            }
            string target = Path.Combine(into, name);
            if (PathNames.Same(target, source))
            {
                if (!copy || await AskNameAsync($"Duplicate {name}", name, other => Invalid(other) ?? Taken(into, other)
                    ?? (claimed.Contains(other) ? $"{other} is the name of another going there" : null), BeforeExtension(source)) is not { } other)
                {
                    continue;
                }
                target = Path.Combine(into, other);
            }
            List<(string From, string To)> going = [(source, target), .. following[source]
                .Select(d => (d, Path.Combine(into, FileNesting.Renamed(d, name, Path.GetFileName(target)))))
                .Where(d => !PathNames.Same(d.Item1, d.Item2))];
            foreach ((string from, string to) in going)
            {
                if (await PutOneAsync(from, to, into, copy, recording, came, kept) is { } problem)
                {
                    if (problem.Length > 0)
                    {
                        refused.Add(problem);
                    }
                    if (from == source)
                    {
                        break;
                    }
                    continue;
                }
                put.Add(PathNames.Canonical(to));
                if (!copy)
                {
                    moved.Add((from, PathNames.Canonical(to)));
                }
            }
        }
        // Copies made in a shared project's folder go into its items file in the copy's command, Undo taking back both.
        string? unfollowed = !copy ? FollowMoved(moved, recording) ?? await FollowGodotAsync(moved, recording)
            : put.Count > 0 && InShared(put) ? IntoShared([.. put.SelectMany(FilesAt)], recording)
            : null;
        if (unfollowed is not null)
        {
            refused.Add(unfollowed);
        }
        else if (await recording.LandedAsync(waiting => status.Text = waiting) is [(string unwritten, Exception notLanded), ..])
        {
            refused.Add($"{Path.GetFileName(unwritten)} could not follow the files {(copy ? "copied" : "moved")}: {notLanded.Message}");
        }
        if (came.Count > 0 || put.Count > 0)
        {
            CameOrWent([.. came, .. put]);
        }
        if (put.Count > 0)
        {
            Explorer.Reveal(put[^1]);
            // The keys go with the choice to the row revealed, for the next Paste.
            Explorer.FocusRows();
            refused.Insert(0, $"{(copy ? "Copied" : "Moved")} {Plural.Listed([.. put.Select(Path.GetFileName).OfType<string>()])} to {Path.GetFileName(into)}");
        }
        status.Text = string.Join("; ", [.. refused, .. kept]);
        return put;
    }

    /// <summary>Moves or copies one file or folder to where it goes within the paste's command (<paramref name="recording"/>),
    /// replacing what has its name there once the person agrees (to the Trash, in the same command, whose Undo brings it
    /// back), a move's ends gathered into <paramref name="came"/> and a copy kept for Undo to take to the Trash, one failing
    /// partway taken back, what of a copy keeps its original's uid said into <paramref name="kept"/>; null once it is there,
    /// else why not ("" when they would not replace).</summary>
    private async Task<string?> PutOneAsync(string source, string target, string into, bool copy, CommandHistory.Recording recording, List<string> came,
        List<string> kept)
    {
        string name = Path.GetFileName(source);
        bool replaced = false;
        if (!PathNames.Same(target, source) && (File.Exists(target) || Directory.Exists(target)))
        {
            if (!await Confirm("Files Already Exist", $"{Path.GetFileName(target)} with the same name already exists in '{Path.GetFileName(into)}'. Replace?", "Replace")
                || !await DeleteAsync(target, asked: true, within: recording))
            {
                return string.Empty;
            }
            replaced = true;
        }
        if (!copy)
        {
            return Gathering(came, () => recording.Move(source, target)) is { } unmoved ? $"{name} could not be moved: {unmoved}" : null;
        }
        if (CopyOnDisk(source, target) is { } uncopied)
        {
            return await UncopiedAsync($"{name} could not be copied: {uncopied}", [target]);
        }
        (IReadOnlyList<string> made, string? unfit, IReadOnlyList<string> unchanged) = await assist.Languages.CSharp.GodotCopiedAsync(source, target);
        if (unfit is not null)
        {
            return await UncopiedAsync($"{name} could not be copied as Godot copies it: {unfit}", [target, .. made]);
        }
        kept.AddRange(unchanged);
        await recording.CopiedAsync(target);
        foreach (string beside in made)
        {
            await recording.CopiedAsync(beside);
            came.Add(beside);
        }
        return null;

        // A copy that failed partway is taken back, and what it replaced put back; what could not be is left to the command's Undo.
        async Task<string> UncopiedAsync(string why, IReadOnlyList<string> copies)
        {
            List<string> said = [why];
            foreach (string copied in copies)
            {
                if (Unmade(copied) is { } left)
                {
                    await recording.CopiedAsync(copied);
                    said.Add($"{Path.GetFileName(copied)} as far as it was copied could not be taken away ({left}), so Undo moves it to the Trash");
                }
            }
            came.Add(target);
            if (replaced && recording.PutBack(target) is { } kept)
            {
                said.Add(kept);
            }
            return string.Join("; ", said);
        }
    }

    /// <summary>Deletes a copy the paste made a moment before, a folder with all it holds, its links as links: null once gone,
    /// else why not.</summary>
    private static string? Unmade(string copy)
    {
        try
        {
            if (Directory.Exists(copy) && new DirectoryInfo(copy).LinkTarget is null)
            {
                Directory.Delete(copy, recursive: true);
            }
            else if (File.Exists(copy) || new FileInfo(copy).LinkTarget is not null)
            {
                File.Delete(copy);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    /// <summary>The files nested under one in the Solution view, by its project's DependentUpon and, while nesting by
    /// name is on, by the nesting rules: what goes where it goes. None for a file in no project.</summary>
    private IReadOnlyList<string> DependentsOf(string path) => DependentsOf([path]).GetValueOrDefault(path) ?? [];

    /// <summary>The files nested under each of several, by the path given (none for one in no project), each folder read once.</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<string>> DependentsOf(IReadOnlyList<string> paths)
    {
        Dictionary<string, string> canonical = new(StringComparer.Ordinal);
        List<(string Path, ProjectModel? Project)> owned = [];
        foreach (string path in paths)
        {
            string at = PathNames.Canonical(path);
            if (canonical.TryAdd(path, at) && File.Exists(at) && ProjectOf(at) is { } project)
            {
                owned.Add((at, project));
            }
        }
        IReadOnlyDictionary<string, IReadOnlyList<string>> found = FileNesting.DependentsOf(owned, settings.NestFiles);
        return canonical.ToDictionary(p => p.Key, p => found.GetValueOrDefault(p.Value) ?? [], StringComparer.Ordinal);
    }

    /// <summary>What names the files moved follows them: the solution's projects and files, the open solution filter's
    /// solution and projects, the solution's launch profiles, every project's paths naming one (an item, a DependentUpon, an
    /// Import or a HintPath, a reference to a project moved), every shared project's items (<see cref="SharedProjects.Follow"/>)
    /// and every program's directives naming one, each file watched first by the command recording, for its undo. A project
    /// or program moved is read again at its new path. Null once they follow, else why one could not.</summary>
    private string? FollowMoved(IReadOnlyList<(string From, string To)> moved, CommandHistory.Recording? recording)
    {
        string Now(string path) => moved.FirstOrDefault(m => PathNames.Within(path, m.From)) is { From: not null } move ? PathNames.Moved(path, move.From, move.To) : path;
        // A file naming one moved names it, or the folder it was in, by name: those that cannot are never read further.
        bool Names(string path) => Editors.TextOf(path)?.ToString() is { } text
            && moved.Any(m => text.Contains(Path.GetFileName(m.From), StringComparison.OrdinalIgnoreCase));
        // The solution, then every project at its path now, those moved themselves among them (read again once followed).
        List<(string Path, bool Moved, Func<string, bool> Follow)> naming = [];
        if (Solution is { } solution)
        {
            naming.Add((Now(solution.Path), false, path => SolutionEdits.Move(path, moved)));
        }
        if (session.Workspace?.Filter is { } filter)
        {
            // Opened again, the filter would leave out a project it names where it was.
            string now = Now(filter.Path);
            naming.Add((now, now != filter.Path, path => SolutionEdits.MoveInFilter(path, moved)));
        }
        if (Solution is { } launched)
        {
            // Its launch profiles, the team's and the person's, name projects from the solution's folder.
            string before = Path.GetDirectoryName(launched.Path)!;
            string after = Path.GetDirectoryName(Now(launched.Path))!;
            foreach (string launches in (string[])[SolutionLaunch.SharedFile(launched.Path), SolutionLaunch.UserFile(launched.Path)])
            {
                naming.Add((Now(launches), false, path => SolutionLaunch.Move(path, before, after, moved)));
            }
        }
        foreach (ProjectModel project in session.Workspace?.Projects ?? [])
        {
            string now = Now(project.Path);
            naming.Add((now, now != project.Path, project.IsProgram ? path => ProgramEdits.MoveDirectives(path, moved) : path => ProjectEdits.MoveItems(path, moved)));
        }
        // A shared project names its items file, which names its files, those come into its folder from elsewhere too.
        foreach (string shared in session.Workspace?.SharedProjects ?? [])
        {
            string now = Now(shared);
            naming.Add((now, now != shared, path => ProjectEdits.MoveItems(path, moved)));
            naming.Add((SharedProjects.ItemsFileOf(now), false, path => SharedProjects.Follow(path, moved, FilesAt)));
        }
        bool reread = naming.Any(n => n.Moved);
        foreach ((string path, bool itself, Func<string, bool> follow) in naming)
        {
            try
            {
                if (!File.Exists(path) || !itself && !Names(path) && !SharedProjects.IsItemsFile(path))
                {
                    continue;
                }
                recording?.Watch(path);
                if (SaveBeforeWriting([path]) is { } unsaved)
                {
                    return $"{Path.GetFileName(path)} could not follow the files moved: {unsaved}";
                }
                if (follow(path))
                {
                    // Open, it takes the text now, inside the command, so the command's undo takes it back too.
                    FollowWritten([path]);
                    reread |= WorkspaceModel.IsSolution(path);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return $"{Path.GetFileName(path)} could not follow the files moved: {error.Message}";
            }
        }
        if (reread)
        {
            // At once, not when the folder watcher hears of it, so the Solution view shows a project renamed under its new name.
            session.CameOrWent([.. moved.Select(m => m.To)], readAgain: true);
        }
        return null;
    }

    /// <summary>The Godot projects' files follow files and folders of theirs moved within the move's command
    /// (<see cref="CSharpAssist.GodotFollowAsync"/>): every res:// path naming one written anew in its scenes, resources,
    /// project.godot and C#, and the .uid and .import files beside a moved file moved with it. Null once they follow, else
    /// why one could not.</summary>
    private async Task<string?> FollowGodotAsync(IReadOnlyList<(string From, string To)> moved, CommandHistory.Recording recording)
    {
        if (moved.Count == 0 || !moved.Any(m => session.GodotOf(m.To) is not null))
        {
            return null;
        }
        using Asked asked = WorkspaceEdits.Track(Editors);
        (IReadOnlyList<(string Path, IReadOnlyList<Lsp.LspTextEdit> Edits)> files, IReadOnlyList<(string From, string To)> beside) = await assist.Languages.CSharp.GodotFollowAsync(moved);
        foreach ((string from, string to) in beside)
        {
            if (recording.Move(from, to) is { } refused)
            {
                return $"{Path.GetFileName(from)} could not go with {Path.GetFileNameWithoutExtension(to)}: {refused}";
            }
        }
        if (files.Count == 0)
        {
            return null;
        }
        System.Text.Json.JsonElement edit = TypeRename.With(System.Text.Json.JsonSerializer.SerializeToElement(new { changes = new Dictionary<string, object>() }), files);
        List<string> dropped = [];
        try
        {
            await FollowingAsync([.. files.Select(f => f.Path)], recording, () => WorkspaceEdits.ApplyAsCommand(Editors, edit, recording, asked, dropped));
        }
        catch (WorkspaceEditFailed failed)
        {
            return failed.Message;
        }
        return dropped.Count > 0 ? $"{string.Join(", ", dropped)} could not follow the files moved: it was edited meanwhile" : null;
    }

    private static string? Taken(string into, string name) =>
        File.Exists(Path.Combine(into, name)) || Directory.Exists(Path.Combine(into, name)) ? $"{name} is already there" : null;

    /// <summary>Copies a file or a folder with all it holds; the open files among them are saved first, so the copy is
    /// what the editor shows. Null once copied, else why not.</summary>
    private string? CopyOnDisk(string source, string target)
    {
        foreach (OpenDocument document in Editors.Documents.Where(d => d.IsModified && PathNames.Within(d.Path, source)))
        {
            if (!Editors.Save(document))
            {
                return $"{document.Name} could not be saved before it was copied";
            }
        }
        try
        {
            if (Directory.Exists(source))
            {
                CopyFolder(source, target);
            }
            else
            {
                File.Copy(source, target);
            }
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return error.Message;
        }
    }

    /// <summary>A folder's copy, a link inside it copied as the link it is rather than followed (it may loop).</summary>
    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (FileSystemInfo entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            string target = Path.Combine(to, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo)
                {
                    Directory.CreateSymbolicLink(target, link);
                }
                else
                {
                    File.CreateSymbolicLink(target, link);
                }
            }
            else if (entry is DirectoryInfo)
            {
                CopyFolder(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    /// <summary>Whether the editor's Cut, Copy, Paste, Delete (Backspace too), Undo or Redo is the Explorer's here: its
    /// row's menu asked, or its rows have the keys (the Edit menu of the Mac's menu bar runs them where the keys
    /// are).</summary>
    private bool ExplorerEdits(string action, ActionContext context) =>
        action is E.Undo or E.Redo ? ExplorerHasKeys(context)
        : action is E.Cut or E.Copy or E.Paste or E.Delete or E.Backspace && RowOf(context) is not null;

    /// <summary>Why one of the editor's actions cannot run on the Explorer's row; null when it can.</summary>
    private string? WhyNotOnRow(string action, ActionContext context) => RowOf(context) is not { } row ? null : action switch
    {
        E.Cut or E.Copy when !row.IsMovable => $"{row.Name} cannot be moved",
        E.Cut when RowsHeld(row).FirstOrDefault(n => n.Kept is not null) is { Kept: { } kept } => kept,
        E.Paste when row.Into is null => $"Nothing can be pasted into {row.Name}",
        E.Paste when held is null => NothingHeld,
        E.Delete or E.Backspace => WhyNotDeleted(row),
        _ => null,
    };

    /// <summary>The editor's Delete as the Explorer's menu names it: Remove for what only leaves the solution, chosen
    /// alone.</summary>
    private string? DeleteTitle(ActionContext context) =>
        ExplorerEdits(E.Delete, context) && RowOf(context) is { } row ? RowsDeleted(row) is [{ LeavesFilesBehind: true }] ? "Remove…" : "Delete…" : null;

    /// <summary>The editor's Cut, Copy, Paste or Delete run on the Explorer's row; its Undo and Redo on the last command
    /// that renamed or moved files.</summary>
    private void ExplorerEdit(string action, ActionContext context)
    {
        if (action is E.Undo or E.Redo)
        {
            UndoCommand(action == E.Redo, null);
            return;
        }
        if (RowOf(context) is not { } row)
        {
            return;
        }
        IdeErrors.Observe(action switch
        {
            E.Cut => HoldAsync(row, cut: true),
            E.Copy => HoldAsync(row, cut: false),
            E.Paste => PasteAsync(row),
            _ => DeleteRowAsync(row),
        });
    }

    /// <summary>Undo or Redo that reaches a command of several files or of a file moved (a rename) takes all of it back
    /// or makes all of it again, once the person agrees, said done once the files not open it wrote have landed; in an
    /// editor, only when that command is its file's next step. One pressed while another is under way, or while a command
    /// it would take is being made, waits for it and then takes what it reaches then. False when Undo there is the file's
    /// own.</summary>
    private bool UndoCommand(bool redo, OpenDocument? document)
    {
        if (undoing.IsCompleted && Editors.Commands.Making(document) is null)
        {
            if ((redo ? Editors.Commands.ToRedo(document) : Editors.Commands.ToUndo(document)) is not { } command)
            {
                return false;
            }
            undoing = UndoCommandAsync(command, redo);
        }
        else
        {
            undoing = UndoInTurnAsync(undoing, redo, document);
        }
        IdeErrors.Observe(undoing);
        return true;
    }

    /// <summary>The Undo or Redo of a command under way, its question and its writes, which the next waits for.</summary>
    private Task undoing = Task.CompletedTask;

    /// <summary>Undo or Redo pressed while another was under way (<paramref name="before"/>) or while a command it would take
    /// was being made: once those have ended, the command it reaches then, else the file's own step.</summary>
    private async Task UndoInTurnAsync(Task before, bool redo, OpenDocument? document)
    {
        string verb = redo ? "Redo" : "Undo";
        if (!before.IsCompleted)
        {
            status.Text = $"{verb} waits for the one under way…";
            await Task.WhenAny(before);
        }
        while (Editors.Commands.Making(document) is { } making)
        {
            status.Text = $"{verb} waits for {making.Name} to be made…";
            await making.Ended;
        }
        if (document is not null && !Editors.Documents.Contains(document))
        {
            return;
        }
        if ((redo ? Editors.Commands.ToRedo(document) : Editors.Commands.ToUndo(document)) is { } command)
        {
            await UndoCommandAsync(command, redo);
        }
        else if (document is not null)
        {
            Editors.Step(document, redo);
        }
    }

    private async Task UndoCommandAsync(CommandHistory.Command command, bool redo)
    {
        string verb = redo ? "Redo" : "Undo";
        if (await Editors.Commands.BlockedAsync(command, redo) is { } changed)
        {
            status.Text = $"Cannot {verb.ToLowerInvariant()} {command.Name}: {changed}";
            return;
        }
        // Named as they are now: after the command's moves for an undo, before them for a redo.
        string question = UndoQuestion.Of(command.Name, redo, [.. command.Texts.Select(t => t.Document?.Path ?? (redo ? t.Path : command.Moved(t.Path)))],
            [.. command.Moves.Select(m => redo ? (m.From, m.To) : (m.To, m.From))],
            [.. (redo ? command.Created.Select(c => c.Path) : command.Deleted.Select(d => d.Path))],
            [.. (redo ? command.Deleted.Select(d => d.Path) : command.Created.Select(c => c.Path))], [.. command.Parts.Select(p => p.Name)],
            [.. command.Copied.Select(c => c.Path)]);
        if (!await Confirm(verb, question, verb))
        {
            return;
        }
        // What may come or go, with the folders over it, as they stand before: a part may make or take away a folder.
        Dictionary<string, bool> stood = Standing(command.Moves.SelectMany(m => new[] { m.From, m.To }).Concat(command.Created.Select(c => c.Path))
            .Concat(command.Deleted.Select(d => d.Path)).Concat(command.Copied.Select(c => c.Path)));
        List<string> moved = [];
        IReadOnlyList<string> written = Editors.Commands.Writes(command, redo);
        bool rereads = command.Texts.Any(t => WorkspaceModel.IsSolution(t.Path))
            || command.Moves.Any(m => (session.Workspace?.Projects ?? []).Any(p => PathNames.Within(p.Path, m.From) || PathNames.Within(p.Path, m.To)));
        // Asked again, as a file may have changed while the question was open; its moves followed with the rest once it is done.
        (string? failed, bool began) = await Editors.Commands.PutAsync(command, redo, change => Gathering(moved, change));
        if (failed is null && await EditorArea.LandedAsync(written, waiting => status.Text = waiting) is [(string path, Exception why), ..])
        {
            failed = WorkspaceEdits.Unwritten(path, why);
        }
        if (began)
        {
            // Once its moves and writes have both landed: a solution it changed, or that holds a project it moved, is read as they left it.
            Followed(stood, moved, [.. command.Texts.Select(t => t.Document?.Path ?? (redo ? command.Moved(t.Path) : t.Path))], readAgain: rereads);
        }
        status.Text = failed is null ? $"{(redo ? "Redone" : "Undone")}: {command.Name}" : $"Cannot {verb.ToLowerInvariant()} {command.Name}: {failed}";
    }
}
