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
/// goes.</summary>
public partial class MainWindow
{
    /// <summary>What Cut or Copy last put on the clipboard, whose text is their paths: a Paste finds them only while the
    /// clipboard still holds that text, so text copied since is never taken for them.</summary>
    private (IReadOnlyList<string> Paths, bool Cut, string Text)? held;

    private const string NothingHeld = "Nothing to paste: no files or folders were cut or copied";

    /// <summary>Cut or Copy on a row: it, or every file and folder chosen with it, but those inside another of them.</summary>
    private async Task HoldAsync(ExplorerNode node, bool cut)
    {
        IReadOnlyList<ExplorerNode> chosen = Explorer.SelectedNodes;
        IReadOnlyList<string> paths = Outermost([.. (chosen.Contains(node) ? chosen : [node]).Where(n => n.IsMovable).Select(n => n.Path!)]);
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
        status.Text = $"{(cut ? "Cut" : "Copied")} {Together([.. paths.Select(Path.GetFileName).OfType<string>()])}";
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

    /// <summary>The paths but those inside a folder among them, found by each one's folders rather than by every other path:
    /// thousands chosen at once would otherwise be compared in pairs.</summary>
    private static IReadOnlyList<string> Outermost(IReadOnlyList<string> paths)
    {
        HashSet<string> chosen = new(paths, PathNames.Comparer);
        return [.. paths.Where(path => !Above(path).Any(chosen.Contains))];

        static IEnumerable<string> Above(string path)
        {
            for (string? folder = Path.GetDirectoryName(path); folder is not null; folder = Path.GetDirectoryName(folder))
            {
                yield return folder;
            }
        }
    }

    /// <summary>Moves or copies files and folders into a folder: what is there already stays put, a folder never goes
    /// inside itself, a copy into its own folder is named by the person, and a name taken there is replaced (what had it
    /// going to the Trash) only once they agree. The status bar says what went where and what could not; the last one put
    /// is chosen in the Explorer. Answers with where each went.</summary>
    public async Task<IReadOnlyList<string>> PutAsync(IReadOnlyList<string> paths, string into, bool copy)
    {
        into = PathNames.Canonical(into);
        // A file's dependents go with it, renamed as a copy's name asks; one chosen with its parent goes as the parent's.
        IReadOnlyDictionary<string, IReadOnlyList<string>> following = DependentsOf(paths);
        HashSet<string> dependents = [.. following.Values.SelectMany(d => d)];
        List<string> put = [];
        List<(string From, string To)> moved = [];
        List<string> refused = [];
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
                if (!copy || await AskNameAsync($"Duplicate {name}", name, other => Invalid(other) ?? Taken(into, other)) is not { } other)
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
                if (await PutOneAsync(from, to, into, copy) is { } problem)
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
        if (FollowMoved(moved) is { } unfollowed)
        {
            refused.Add(unfollowed);
        }
        if (put.Count > 0)
        {
            FilesChanged();
            Explorer.Reveal(put[^1]);
            // Read again, the rows lost the keys; they take them back for the next Paste.
            Explorer.FocusRows();
            refused.Insert(0, $"{(copy ? "Copied" : "Moved")} {Together([.. put.Select(Path.GetFileName).OfType<string>()])} to {Path.GetFileName(into)}");
        }
        status.Text = string.Join("; ", refused);
        return put;
    }

    /// <summary>Moves or copies one file or folder to where it goes, replacing what has its name there once the person
    /// agrees; null once it is there, else why not ("" when they would not replace).</summary>
    private async Task<string?> PutOneAsync(string source, string target, string into, bool copy)
    {
        string name = Path.GetFileName(source);
        if (!PathNames.Same(target, source) && (File.Exists(target) || Directory.Exists(target)))
        {
            if (!await Confirm("Files Already Exist", $"{Path.GetFileName(target)} with the same name already exists in '{Path.GetFileName(into)}'. Replace?", "Replace")
                || !await DeleteAsync(target, asked: true))
            {
                return string.Empty;
            }
        }
        return (copy ? CopyOnDisk(source, target) : Editors.MoveFile(source, target)) is { } problem
            ? $"{name} could not be {(copy ? "copied" : "moved")}: {problem}"
            : null;
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

    /// <summary>What names the files moved follows them: the solution's projects and files, and every project's items
    /// naming one (an Update or Remove, a DependentUpon, a reference to a project moved), each file watched first when
    /// a command is recording, for its undo. A project moved is read again at its new path. Null once they follow, else
    /// why one could not.</summary>
    private string? FollowMoved(IReadOnlyList<(string From, string To)> moved, CommandHistory.Recording? recording = null)
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
        foreach (ProjectModel project in session.Workspace?.Projects ?? [])
        {
            string now = Now(project.Path);
            naming.Add((now, now != project.Path, path => ProjectEdits.MoveItems(path, moved)));
        }
        bool reread = naming.Any(n => n.Moved);
        foreach ((string path, bool itself, Func<string, bool> follow) in naming)
        {
            try
            {
                if (!File.Exists(path) || !itself && !Names(path))
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
            ReloadSolution();
        }
        return null;
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
        E.Paste when row.Into is null => $"Nothing can be pasted into {row.Name}",
        E.Paste when held is null => NothingHeld,
        E.Delete or E.Backspace when !row.CanRemove => $"{row.Name} cannot be deleted",
        _ => null,
    };

    /// <summary>The editor's Delete as the Explorer's menu names it: Remove for what only leaves the
    /// solution.</summary>
    private string? DeleteTitle(ActionContext context) =>
        ExplorerEdits(E.Delete, context) && RowOf(context) is { } row ? row.LeavesFilesBehind ? "Remove…" : "Delete…" : null;

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
    /// or makes all of it again, once the person agrees; in an editor, only when that command is its file's next step.
    /// False when Undo there is the file's own.</summary>
    private bool UndoCommand(bool redo, OpenDocument? document)
    {
        if ((redo ? Editors.Commands.ToRedo(document) : Editors.Commands.ToUndo(document)) is not { } command)
        {
            return false;
        }
        IdeErrors.Observe(UndoCommandAsync(command, redo));
        return true;
    }

    private async Task UndoCommandAsync(CommandHistory.Command command, bool redo)
    {
        string verb = redo ? "Redo" : "Undo";
        if (Editors.Commands.Blocked(command, redo) is { } changed)
        {
            status.Text = $"Cannot {verb.ToLowerInvariant()} {command.Name}: {changed}";
            return;
        }
        // Named as they are now: after the command's moves for an undo, before them for a redo.
        IEnumerable<string> files = command.Texts.Select(t => t.Document?.Path ?? (redo ? t.Path : command.Moved(t.Path)))
            .Concat(command.Moves.Select(m => redo ? m.From : m.To))
            .Concat(command.Created.Select(c => c.Path));
        if (!await Confirm(verb, $"{verb} {command.Name}? It changes {Together([.. files.Select(Path.GetFileName).OfType<string>().Distinct(), .. command.Parts.Select(p => p.Name)])}.", verb))
        {
            return;
        }
        // Asked again: a file may have changed while the question was open.
        string? failed = Editors.Commands.Blocked(command, redo) ?? (redo ? Editors.Commands.Redo(command) : Editors.Commands.Undo(command));
        FilesChanged();
        status.Text = failed is null ? $"{(redo ? "Redone" : "Undone")}: {command.Name}" : $"Cannot {verb.ToLowerInvariant()} {command.Name}: {failed}";
    }
}
