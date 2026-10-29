using System.Runtime.InteropServices;
using Avalonia.Input.Platform;
using Passenger.App.CSharp;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App;

/// <summary>The Explorer's file operations: New File, New Folder and New Class where a row holds things, Rename and
/// Delete (to the Trash) for what is on disk, though a project's Delete takes it out of the solution; the solution's
/// own folders' are in MainWindow.Solution.cs, and the actions that run them on a row in MainWindow.ExplorerActions.cs.
/// The open tabs follow a rename and close with a delete, and the Explorer, the file index and Git follow what each
/// made, moved or deleted.</summary>
public partial class MainWindow
{
    private void AddFileOperations()
    {
        AddExplorerActions();
        AddConfiguration();
        Explorer.Dropped += (_, dropped) => IdeErrors.Observe(DropAsync(dropped));
        Explorer.NestingAsked += (_, _) => IdeErrors.Observe(AskNestingAsync());
        Editors.Moved += (_, move) => OnFileMoved(move);
        // The watcher that saw the move reads the Explorer, the index and Git again itself.
        Editors.Followed += (_, move) => session.Followed(move);
    }

    /// <summary>File Nesting, over the window: its "Show files with the same names as nested" is the setting every
    /// Solution view nests by.</summary>
    private async Task AskNestingAsync()
    {
        FileNestingDialog dialog = new(settings.NestFiles);
        if (await dialog.ShowDialog<bool>(this))
        {
            ApplySettings(settings with { NestFiles = dialog.Nest });
        }
    }

    private void AskName(string title, string initial, Func<string, string?> attempt) => AskName(title, initial, name => Task.FromResult(attempt(name)));

    /// <summary>Asks for a name in a popup, which stays open saying why while <paramref name="attempt"/> refuses it.</summary>
    private void AskName(string title, string initial, Func<string, Task<string?>> attempt)
    {
        Vcs.NamePopup popup = new(title, initial, attempt);
        popups.Show(popup, _ => { });
    }

    /// <summary>Asks for a name as <see cref="AskName"/> does, answering with the one taken, or null once the popup closes
    /// without one (Escape, a click outside it).</summary>
    private Task<string?> AskNameAsync(string title, string initial, Func<string, string?> refuse)
    {
        TaskCompletionSource<string?> answer = new();
        string? taken = null;
        Vcs.NamePopup popup = new(title, initial, name =>
        {
            string? why = refuse(name);
            taken = why is null ? name : null;
            return Task.FromResult(why);
        });
        popup.DetachedFromVisualTree += (_, _) => answer.TrySetResult(taken);
        popups.Show(popup, _ => { });
        return answer.Task;
    }

    /// <summary>The C# types Class/Interface makes, in the order its popup lists them.</summary>
    public static IReadOnlyList<string> TypeKinds { get; } = ["Class", "Interface", "Record", "Struct", "Enum"];

    /// <summary>Add, Class/Interface: a name, and the kind of type chosen under it with Up and Down.</summary>
    private void AskType(string into)
    {
        Vcs.NamePopup? popup = null;
        popup = new Vcs.NamePopup("New Class/Interface", string.Empty, name => Task.FromResult(CreateType(into, name, popup?.Kind ?? TypeKinds[0])),
            [.. TypeKinds.Select(kind => (kind, TypeBadge(kind)))]);
        popups.Show(popup, _ => { });
    }

    /// <summary>The letter and colour a kind of type is marked with, as Go to Symbol shows it.</summary>
    private static Avalonia.Controls.Control TypeBadge(string kind)
    {
        (string letter, string brush) = Navigation.SymbolPopup.Badge(kind switch { "Interface" => Lsp.SymbolKind.Interface, "Struct" => Lsp.SymbolKind.Struct, "Enum" => Lsp.SymbolKind.Enum, _ => Lsp.SymbolKind.Class });
        return Navigation.SymbolPopup.KindBadge(kind == "Record" ? "R" : letter, brush);
    }

    /// <summary>Makes a file in a folder (folders in its name made too) and opens it; null when it worked, else why not.</summary>
    public string? CreateFile(string into, string name, string text)
    {
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        string path = Path.GetFullPath(Path.Combine(into, name));
        if (File.Exists(path) || Directory.Exists(path))
        {
            return $"{name} is already there";
        }
        try
        {
            Following([path], () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                // Made as its .editorconfig asks: its line breaks and its charset.
                EditorConfig conventions = EditorConfig.For(path);
                FileEncoding charset = conventions.Charset ?? FileEncoding.Utf8;
                LineSeparator separator = conventions.EndOfLine ?? LineSeparator.LF;
                FileText.Save(path, LineEndings.In(text, separator.Held()), charset.Encoding, charset.Bom, separator == LineSeparator.CR);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return NotMade(name, error);
        }
        OpenFile(path);
        Explorer.Reveal(PathNames.Canonical(path));
        return null;
    }

    public string? CreateFolder(string into, string name)
    {
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        string path = Path.GetFullPath(Path.Combine(into, name));
        if (File.Exists(path) || Directory.Exists(path))
        {
            return $"{name} is already there";
        }
        try
        {
            Following([path], () => Directory.CreateDirectory(path));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return NotMade(name, error);
        }
        Explorer.Reveal(PathNames.Canonical(path));
        return null;
    }

    /// <summary>Why a file or folder could not be made (its folder read-only or denied to this user, a path too long, a
    /// full disk), as the popup that asked for its name says it.</summary>
    private static string NotMade(string name, Exception error) =>
        $"{name} could not be made: {(error is UnauthorizedAccessException ? "access denied" : error.Message)}";

    /// <summary>New Class: a file of its name holding a class in the namespace its folder gives (the project's name,
    /// then the folders under it).</summary>
    public string? CreateClass(string into, string name) => CreateType(into, name, "Class");

    /// <summary>Class/Interface: a file of its name holding a type of the kind chosen (<see cref="TypeKinds"/>), in the
    /// namespace its folder gives. Folders before the name ("Shapes/Circle") are made and the type goes in
    /// them.</summary>
    public string? CreateType(string into, string name, string kind)
    {
        string[] parts = name.Trim().Split('/', '\\');
        string last = parts[^1];
        string type = last.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ? last[..^3] : last;
        if (type.Length == 0 || !char.IsLetter(type[0]) && type[0] != '_' || !type.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return $"{type} is not a C# name";
        }
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        into = Path.Combine([into, .. parts[..^1].Where(p => p.Length > 0)]);
        string declaration = kind switch
        {
            "Interface" => $"public interface {type}\n{{\n}}\n",
            "Record" => $"public record {type}();\n",
            "Struct" => $"public struct {type}\n{{\n}}\n",
            "Enum" => $"public enum {type}\n{{\n}}\n",
            _ => $"public class {type}\n{{\n}}\n",
        };
        return CreateFile(into, type + ".cs", $"namespace {NamespaceFor(into)};\n\n{declaration}");
    }

    /// <summary>The namespace a folder's code takes: its project's root namespace (its name, "Shop.App"), then the
    /// folders below, each name a C# identifier or dotted ones.</summary>
    private string NamespaceFor(string into)
    {
        string full = PathNames.Canonical(into);
        ProjectModel? project = ProjectOf(full);
        string root = project is null ? Path.GetFileName(Folder ?? full) : Path.GetFileNameWithoutExtension(project.Path);
        string below = project is null ? string.Empty : Path.GetRelativePath(project.Folder, full);
        IEnumerable<string> parts = [root, .. below == "." ? [] : below.Split(Path.DirectorySeparatorChar)];
        return string.Join('.', parts.SelectMany(p => p.Split('.', StringSplitOptions.RemoveEmptyEntries)).Select(Identifier));

        static string Identifier(string part)
        {
            string name = new([.. part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
            return char.IsDigit(name[0]) ? "_" + name : name;
        }
    }

    /// <summary>Rename on a row: a file named after a type (a C# file declaring it, a XAML or Razor file whose code-behind
    /// declares its class, <see cref="TypeRename.NamedAfterFile"/>) offers to rename the type too (Rename type 'Foo' to
    /// match the new file name), checked as it was left (on until turned off). The type's name is held in its open file
    /// from the popup's opening, through what changes the file meanwhile (an agent's edit, a fix), and found again in a
    /// file not open as the rename begins.</summary>
    private void AskRename(string path)
    {
        if (TypeRename.NamedAfterFile(Editors, path) is not { } type)
        {
            AskName("Rename", Path.GetFileName(path), name => RenameAsync(path, name));
            return;
        }
        OpenDocument? declaring = Editors.Find(type.Declaring);
        Anchor? held = declaring?.Text.AnchorAt(type.Offset, AnchorGravity.After);
        Vcs.NamePopup? popup = null;
        popup = new Vcs.NamePopup("Rename", Path.GetFileName(path), Attempt,
            option: ($"Rename type '{type.Name}' to match the new file name", assist.RenameSettings.RenameType));
        popup.OptionChanged += (_, on) => assist.RenameSettings = assist.RenameSettings with { RenameType = on };
        popup.DetachedFromVisualTree += (_, _) => held?.Remove();
        popups.Show(popup, _ => { });

        Task<string?> Attempt(string name) =>
            popup?.Option?.IsChecked != true || TypeRename.Stem(name) == type.Name ? RenameAsync(path, name)
            : TypeNow(path, type, declaring, held) is { } now ? RenameWithTypeAsync(path, name, now, renameType: true)
            : Task.FromResult<string?>($"{Path.GetFileName(type.Declaring)} no longer declares {type.Name}: turn renaming the type off to rename the file alone");
    }

    /// <summary>The type a popup's rename offers as it stands now: at its name's place held in the open file that declares
    /// it, else found again where the file declares it; null when the file no longer declares a type of its name.</summary>
    private TypeRename.FileType? TypeNow(string path, TypeRename.FileType type, OpenDocument? declaring, Anchor? held) =>
        held is { IsLive: true } && declaring is not null && Editors.Find(type.Declaring) == declaring ? type with { Offset = held.Offset }
        : TypeRename.NamedAfterFile(Editors, path) is { } found && found.Name == type.Name && PathNames.Same(found.Declaring, type.Declaring) ? found
        : null;

    /// <summary>Renames a file and, when asked, the type it is named after: the language server renames the type
    /// everywhere it is used, from its declaration (a markup file's in its code-behind, a Razor component's with none
    /// where the server takes its class, <see cref="TypeRename.ComponentAsync"/>), then the file and any other named after
    /// the type (Foo.Designer.cs, a code-behind) take its name, the markup its class is made with following
    /// (<see cref="TypeRename.MarkupAsync"/>), all one command for Undo. Null once renamed, else why not.</summary>
    public async Task<string?> RenameWithTypeAsync(string path, string name, TypeRename.FileType type, bool renameType)
    {
        string renamed = TypeRename.Stem(name);
        if (!renameType || renamed == type.Name)
        {
            return await RenameAsync(path, name);
        }
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        string extension = Path.GetExtension(path);
        if (!name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            return extension == ".cs" ? "The new file name extension is not valid for source code files" : $"The new file name must end in {extension} to name its class";
        }
        if (!Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(renamed))
        {
            return "The new file name cannot be used as a valid type name";
        }
        string target = Path.Combine(Path.GetDirectoryName(path)!, name);
        if ((File.Exists(target) || Directory.Exists(target)) && !PathNames.Same(target, path))
        {
            return $"{name} is already there";
        }
        CSharpService? csharp = session.CSharp;
        if (csharp?.Session is not { IsLoaded: true } server)
        {
            return csharp?.Unready("Renaming the type") ?? "Renaming the type needs the C# language server, which runs for a folder";
        }
        OpenDocument? open = Editors.Find(type.Declaring);
        // A file not open is shown to the server for the question alone, never opened in a tab.
        Lsp.LspDocument? shown = null;
        Anchor? at = null;
        Asked? asked = null;
        BackgroundWork.Begin();
        try
        {
            // A component with no code-behind is asked about where the server takes the class Razor makes of its file.
            bool componentFile = TypeRename.IsComponentFile(type.Declaring);
            TypeRename.Component? component = componentFile ? await TypeRename.ComponentAsync(server, type.Declaring) : null;
            if (componentFile && component is null)
            {
                return $"{type.Name} can't be renamed: the C# language server has no class of {Path.GetFileName(type.Declaring)}";
            }
            Lsp.LspDocument? opened = component is null && open is not null
                ? csharp.For(open)?.Lsp ?? throw new Lsp.LanguageServerFailed("the C# language server has not opened the file yet") : null;
            // Held on the type's name through what is typed in its open file while it is asked about.
            at = opened?.Text.AnchorAt(type.Offset, AnchorGravity.After);
            TypeRename.Named? named = null;
            TypeRename.Markup markup = new([], null);
            // Asked again once the server has read a file not open written a moment before (by Undo, another change).
            (string? refused, System.Text.Json.JsonElement made) = await assist.PatientlyAsync(AppActions.Rename, async patience =>
            {
                Lsp.LspDocument? document = component is not null ? null
                    : opened ?? (shown = server.Open(type.Declaring, new TextDocument(FileText.Load(type.Declaring)?.Text ?? string.Empty)));
                named = component?.Type ?? await TypeRename.NamedAsync(Editors, server.Folder, document!, at?.Offset ?? type.Offset);
                asked?.Dispose();
                asked = WorkspaceEdits.Track(Editors);
                System.Text.Json.JsonElement? edit = component is not null ? await server.RenameAsync(component.Name, renamed)
                    : await document!.RenameAsync(at?.Offset ?? type.Offset, renamed);
                shown?.Dispose();
                shown = null;
                if (edit is not { } given)
                {
                    return ($"{type.Name} can't be renamed", default(System.Text.Json.JsonElement));
                }
                markup = named is null ? new([], null) : await TypeRename.MarkupAsync(session.Xaml, named, renamed, renameFiles: true);
                return await WorkspaceEdits.StaleAsync(Editors, paths => server.HeldTextsAsync(paths), given, asked.Since, patience) is { } stale
                    ? ($"{type.Name} was not renamed: {stale}", given) : (null, given);
            }, answer => answer.Item1 is not null);
            if (refused is not null || asked is null)
            {
                return refused;
            }
            if (markup.ClassFile is { } xaml && named?.FullName is { } fullName
                && TypeRename.ClassEdit(xaml, WorkspaceEdits.Answered(Editors, asked)(xaml), fullName, renamed) is { } classEdit)
            {
                made = TypeRename.With(made, classEdit.Path, classEdit.Edit);
            }
            List<(string From, string To)> moves = [(path, target)];
            if (named is { } parts)
            {
                moves.AddRange(parts.Files.Where(f => !PathNames.Same(f, path)).Select(f => (f, TypeRename.Renamed(f, renamed))));
            }
            moves.AddRange(markup.Moves.Where(m => !moves.Any(n => PathNames.Same(n.From, m.From))));
            // The files nested under it go with it too (Form1.resx with Form1.cs), those that are parts of the type once.
            moves.AddRange(WithDependents(path, name).Where(d => !moves.Any(m => PathNames.Same(m.From, d.From))));
            List<string> dropped = [];
            string command = $"Rename {type.Name} to {renamed}";
            using CommandHistory.Recording recording = Editors.Commands.Record(command);
            TypeRename.Applied? applied = null;
            await FollowingAsync([.. moves.SelectMany(m => new[] { m.From, m.To }), .. WorkspaceEdits.Paths(made)], recording,
                () => applied = TypeRename.Apply(Editors, made, moves, asked, dropped, recording,
                    (moving, moved) => FollowMoved([.. moved.Select(m => (m.From, PathNames.Canonical(m.To)))], moving)));
            int files = applied?.Files ?? 0;
            Explorer.Reveal(PathNames.Canonical(applied?.Moved.Any(m => PathNames.Same(m.From, path)) == true ? target : path));
            await WorkspaceEdits.LandedAsync(recording, waiting => status.Text = waiting);
            string unchanged = dropped.Count > 0 ? $", but not in {string.Join(", ", dropped)}: it was edited meanwhile" : string.Empty;
            // The code renamed with a file left unmoved is said as made: the box closes, and Undo takes it back whole.
            status.Text = applied?.Unmoved is { } unmoved
                ? $"Renamed {type.Name} to {renamed} in {Plural.Of(files, "file")}{unchanged}{(unchanged.Length > 0 ? "; and" : ", but")} {unmoved}"
                : $"Renamed {Path.GetFileName(path)} to {name}, and {type.Name} to {renamed} in {Plural.Of(files, "file")}{unchanged}";
            return null;
        }
        catch (Lsp.LanguageServerFailed failed)
        {
            return $"{type.Name} could not be renamed: {failed.Reason}";
        }
        catch (WorkspaceEditFailed failed)
        {
            return failed.Message;
        }
        finally
        {
            at?.Remove();
            shown?.Dispose();
            asked?.Dispose();
            BackgroundWork.End();
        }
    }

    /// <summary>Rename on a project: its file takes the name, its extension kept, and the solution and the projects
    /// referencing it follow (<see cref="RenameAsync"/>). Null once renamed, else why not.</summary>
    public async Task<string?> RenameProjectAsync(string project, string name)
    {
        string extension = Path.GetExtension(project);
        return Invalid(name) ?? await RenameAsync(project, name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name : name + extension);
    }

    /// <summary>Renames a file or folder in place, and the files nested under a file with it (Counter.razor.cs going
    /// with Counter.razor). Its open files keep their tabs, splits and undo, saved first, its bookmarks and breakpoints
    /// go with it, and what names it follows (projects' items, the solution, references to a project). Undo in the
    /// Solution view renames it all back, once what it wrote of the files that follow it has landed.</summary>
    public async Task<string?> RenameAsync(string path, string name)
    {
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        string target = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (target == path)
        {
            return null;
        }
        List<(string From, string To)> moves = [(path, target), .. WithDependents(path, name)];
        if (moves.FirstOrDefault(m => (File.Exists(m.To) || Directory.Exists(m.To)) && !PathNames.Same(m.To, m.From)) is { To: not null } taken)
        {
            return $"{Path.GetFileName(taken.To)} is already there";
        }
        using CommandHistory.Recording recording = Editors.Commands.Record($"Rename {Path.GetFileName(path)} to {name}");
        string? failed = Following([.. moves.SelectMany(m => new[] { m.From, m.To })], () =>
        {
            foreach ((string from, string to) in moves)
            {
                if (recording.Move(from, to) is { } refused)
                {
                    return refused;
                }
            }
            if (FollowMoved([.. moves.Select(m => (m.From, PathNames.Canonical(m.To)))], recording) is { } unfollowed)
            {
                status.Text = unfollowed;
            }
            return null;
        });
        if (failed is not null)
        {
            return failed;
        }
        Explorer.Reveal(PathNames.Canonical(target));
        if (await recording.LandedAsync(waiting => status.Text = waiting) is [(string unfollowed, Exception refused), ..])
        {
            status.Text = $"{Path.GetFileName(unfollowed)} could not follow the files moved: {refused.Message}";
        }
        return null;
    }

    /// <summary>Where the files nested under a file go as it takes a new name: beside it, named after it.</summary>
    private IEnumerable<(string From, string To)> WithDependents(string path, string name) =>
        DependentsOf(path)
            .Select(d => (d, Path.Combine(Path.GetDirectoryName(d)!, FileNesting.Renamed(d, Path.GetFileName(path), name))))
            .Where(d => d.Item1 != d.Item2);

    /// <summary>A file or folder moved, by a rename here or a code fix: what knows a file by its path follows it. The
    /// language server knows one by its URI, so each open file is closed there and opened at the new one.</summary>
    private void OnFileMoved(FileMove move)
    {
        session.Moved(move);
        if (movesFollowed is { } gathered)
        {
            gathered.Add(move.From);
            gathered.Add(move.To);
            return;
        }
        CameOrWent([move.From, move.To]);
    }

    /// <summary>While an Undo or Redo of a command runs, the ends of the moves it makes, followed with the rest of what it
    /// changed once it is done.</summary>
    private List<string>? movesFollowed;

    /// <summary>Delete on a project: it leaves the solution, and the solution filter open, as one command Undo takes back
    /// whole, and its files stay. Moving its folder to the Trash is a second question, never asked of the open folder, a
    /// folder above it, or one holding the solution or another of its projects.</summary>
    public async Task<bool> RemoveProjectAsync(string project)
    {
        string name = Path.GetFileNameWithoutExtension(project);
        if (Solution is not { } solution || Folder is null)
        {
            status.Text = $"{name} is in no solution to remove it from";
            return false;
        }
        if (!await Confirm("Remove Project", $"Remove {name} from the solution? Its folder and files stay on disk.", "Remove"))
        {
            return false;
        }
        if (await EditSolutionAsync(file => SolutionEdits.RemoveProject(file, project), $"Removed {name} from {solution.Name}", [project], $"Remove Project {name}") is { } problem)
        {
            status.Text = $"{name} could not be removed: {problem}";
            return false;
        }
        string own = PathNames.Canonical(Path.GetDirectoryName(project)!);
        bool apart = !PathNames.Within(Folder, own) && !PathNames.Within(solution.Path, own) && !Projects.Any(p => PathNames.Within(p.Path, own));
        if (apart && await Confirm("Delete Project Files", $"Also move the folder {Path.GetFileName(own)} and everything in it to the Trash?", "Move to Trash"))
        {
            await DeleteAsync(own, asked: true);
        }
        return true;
    }

    /// <summary>How long a delete waits for the design-time builds of the projects it takes to end.</summary>
    private static readonly TimeSpan BuildsGoing = TimeSpan.FromSeconds(60);

    /// <summary>Deletes a file or folder to the Trash, once asked (unless it was already); its open files are saved first,
    /// so the Trash and Local History have what the editor showed, and close once it is gone, or keep their tabs when it
    /// could not go. The design-time builds of the projects in it are held off, those running waited out.</summary>
    public async Task<bool> DeleteAsync(string path, bool asked = false)
    {
        string what = Path.GetFileName(path);
        bool isFolder = Directory.Exists(path);
        if (!asked && !await Confirm("Delete", isFolder ? $"Move the folder {what} and everything in it to the Trash?" : $"Move {what} to the Trash?", "Delete"))
        {
            return false;
        }
        foreach (OpenDocument document in Editors.Documents.Where(d => d.IsModified && PathNames.Within(d.Path, path)).ToList())
        {
            if (!Editors.Save(document))
            {
                status.Text = $"{what} could not be deleted: {document.Name} could not be saved first";
                return false;
            }
        }
        History.LocalHistory? keeping = session.LocalHistory;
        History.HistoryDeletion? taken = keeping is null ? null : await keeping.CaptureAsync(path);
        // A design-time build of a project in it, still writing its obj/, would make the folder again once it had gone.
        (IDisposable held, Task built) = session.DesignTimeBuilds.HoldOff(path);
        using IDisposable releasing = held;
        if (!built.IsCompleted)
        {
            status.Text = $"Waiting for the design-time build of {what} to end before it goes to the Trash…";
            try
            {
                await built.WaitAsync(BuildsGoing);
            }
            catch (TimeoutException)
            {
                // Moved anyway: what the build writes after makes only build output there again.
            }
        }
        try
        {
            Trash.Move(path);
            if (taken is not null)
            {
                keeping!.Deleted(taken);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            status.Text = $"{what} could not be deleted: {error.Message}";
            return false;
        }
        foreach (OpenDocument document in Editors.Documents.Where(d => PathNames.Within(d.Path, path)).ToList())
        {
            Editors.CloseDeleted(document);
        }
        CameOrWent([path]);
        status.Text = $"Moved {what} to the Trash";
        return true;
    }

    /// <summary>Runs a change of files the IDE makes itself, then follows only what it came to: what it made, moved or
    /// deleted along <paramref name="paths"/> (a folder over one among them) and the moves it made, as
    /// <see cref="CameOrWent"/> does; a change of texts alone has evaluated again the projects that read one of them.</summary>
    private T Following<T>(IReadOnlyCollection<string> paths, Func<T> change)
    {
        Dictionary<string, bool> stood = Standing(paths);
        List<string> moved = [];
        try
        {
            return Gathering(moved, change);
        }
        finally
        {
            Followed(stood, moved, paths);
        }
    }

    private void Following(IReadOnlyCollection<string> paths, Action change) => Following(paths, () =>
    {
        change();
        return true;
    });

    /// <summary>As <see cref="Following{T}"/>, a change made within a command's recording, followed only once the recording
    /// has written the files not open it changes (<see cref="CommandHistory.Recording.WriteAsync"/>), so what reads them
    /// then reads them as the change left them. Throws <see cref="WorkspaceEditFailed"/> naming a file the disk refused.</summary>
    private async Task FollowingAsync(IReadOnlyCollection<string> paths, CommandHistory.Recording recording, Action change)
    {
        Dictionary<string, bool> stood = Standing(paths);
        List<string> moved = [];
        (string Path, Exception Refused)? refused;
        try
        {
            Gathering(moved, () =>
            {
                change();
                return true;
            });
        }
        finally
        {
            refused = await recording.WriteAsync();
            Followed(stood, moved, paths);
        }
        WorkspaceEdits.Written(refused);
    }

    /// <summary>Whether each file and folder along the paths stands, up to the open folder: what a change made or took away
    /// is what stands otherwise after it, a folder made or taken with a file among them.</summary>
    private Dictionary<string, bool> Standing(IEnumerable<string> paths)
    {
        Dictionary<string, bool> stood = new(PathNames.Comparer);
        foreach (string path in paths)
        {
            for (string? at = PathNames.Canonical(path); at is not null && (Folder is not { } folder || PathNames.IsUnder(at, folder)); at = Path.GetDirectoryName(at))
            {
                // A folder already looked at was looked at with every folder over it.
                if (!stood.TryAdd(at, Stands(at)))
                {
                    break;
                }
            }
        }
        return stood;
    }

    private static bool Stands(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Runs a change, the ends of the moves it makes gathered into <paramref name="moved"/>, to be followed with the
    /// rest of what it changed once it is done rather than one by one.</summary>
    private T Gathering<T>(List<string> moved, Func<T> change)
    {
        List<string>? outer = movesFollowed;
        movesFollowed = moved;
        try
        {
            return change();
        }
        finally
        {
            movesFollowed = outer;
        }
    }

    /// <summary>Follows a change of files once it is done: what came or went along <paramref name="stood"/>'s paths, with the
    /// ends of the moves it made, as <see cref="CameOrWent"/> does; with none, the projects whose evaluation read one of
    /// <paramref name="texts"/> are evaluated again. The XAML reading a C# file among them or the moves' reads it again either way.</summary>
    private void Followed(Dictionary<string, bool> stood, IReadOnlyCollection<string> moved, IReadOnlyCollection<string> texts)
    {
        List<string> cameOrWent = [.. moved, .. stood.Where(s => s.Value != Stands(s.Key)).Select(s => s.Key)];
        if (cameOrWent.Count > 0)
        {
            CameOrWent(cameOrWent);
        }
        else if (session.Models.Read(texts))
        {
            // Only texts changed, a project's or an import's among them: the projects that read it are evaluated again.
            session.Evaluate();
        }
        // Now, rather than once the watcher hears of it.
        session.Xaml.FilesChanged([.. texts.Concat(moved).Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct(PathNames.Comparer)]);
    }

    /// <summary>Files or folders the IDE itself made, moved or deleted, followed at once rather than when the folder watcher
    /// hears of them: the Explorer reads again only the folders that held or hold them, keeping the rows still there, Go to
    /// File's index takes them in or out in place, the projects are checked for what their wildcards take in, and Git's
    /// colours follow.</summary>
    private void CameOrWent(IReadOnlyCollection<string> paths)
    {
        HashSet<string> named = new(paths.Select(PathNames.Canonical), StringComparer.Ordinal);
        Explorer.RefreshFolders(named.Select(Path.GetDirectoryName).OfType<string>());
        session.Evaluate();
        session.MSBuild.FilesCameOrWent();
        session.CSharp?.FilesCameOrWent(named);
        session.Xaml.CameOrWent(named);
        if (Folder is { } folder)
        {
            session.Index.Apply(folder, named);
        }
        IdeErrors.Observe(session.Vcs.RefreshAsync());
    }

    private static string? Invalid(string name) =>
        name.Trim().Length == 0 ? "A name is needed"
        : name.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c is not '/' and not '\\').ToArray()) >= 0 || name.Contains("..", StringComparison.Ordinal) ? $"{name} is not a file name"
        : null;
}

/// <summary>The platform's Trash (the Recycle Bin on Windows), where a deleted file can still be found.</summary>
public static partial class Trash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>A folder to use as the Trash instead of the platform's. The process's, as the system's Trash is every
    /// window's.</summary>
    public static string? Folder { get; set; }

    public static void Move(string path)
    {
        if (OperatingSystem.IsWindows() && Folder is null)
        {
            if (Directory.Exists(path))
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            return;
        }
        if (OperatingSystem.IsLinux() && Folder is null)
        {
            MoveToFreedesktopTrash(path);
            return;
        }
        if (OperatingSystem.IsMacOS() && Folder is null)
        {
            MoveToMacTrash(path);
            return;
        }
        string bin = Folder ?? throw new PlatformNotSupportedException("Passenger knows no Trash on this system");
        Directory.CreateDirectory(bin);
        string name = Path.GetFileName(path);
        string target = Path.Combine(bin, name);
        // The Trash keeps what it already holds: a second one of a name takes the time beside it.
        if (File.Exists(target) || Directory.Exists(target))
        {
            target = Path.Combine(bin, $"{Path.GetFileNameWithoutExtension(name)} {DateTime.Now:HH-mm-ss-fff}{Path.GetExtension(name)}");
        }
        MoveTo(path, target);
    }

    /// <summary>Foundation, where NSFileManager is: the runtime's own native library loads it today, which nothing promises.</summary>
    private static readonly Lazy<nint> Foundation = new(() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation"));

    /// <summary>The Trash as Finder fills it, through NSFileManager: it records where the item was, so Finder's Put Back
    /// returns it, and an item on another volume goes to that volume's own Trash rather than across. What the system
    /// refuses is an IOException in its own words.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static void MoveToMacTrash(string path)
    {
        _ = Foundation.Value;
        // Its own pool, so what the calls autorelease goes now on any thread, with or without AppKit's run loop.
        nint pool = Send(Class("NSAutoreleasePool"), Selector("new"));
        try
        {
            nint url = Send(Class("NSURL"), Selector("fileURLWithPath:"), Send(Class("NSString"), Selector("stringWithUTF8String:"), path));
            nint manager = Send(Class("NSFileManager"), Selector("defaultManager"));
            nint error = 0;
            if (!Send(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, 0, ref error))
            {
                string? said = error == 0 ? null : Marshal.PtrToStringUTF8(Send(Send(error, Selector("localizedDescription")), Selector("UTF8String")));
                throw new IOException(said ?? $"The Trash refused {Path.GetFileName(path)}");
            }
        }
        finally
        {
            Send(pool, Selector("drain"));
        }
    }

    /// <summary>The freedesktop.org Trash in the person's data folder (~/.local/share/Trash), as Linux's file managers
    /// keep it: the item in files/ under a name of its own, beside a record in info/ of where it was and when it went,
    /// from which they put it back. A folder on another file system is refused, as a move across them cannot be made.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static void MoveToFreedesktopTrash(string path)
    {
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        string trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trash");
        Directory.CreateDirectory(trash, Private);
        string files = Directory.CreateDirectory(Path.Combine(trash, "files"), Private).FullName;
        string info = Directory.CreateDirectory(Path.Combine(trash, "info"), Private).FullName;
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string name = Path.GetFileName(full);
        string where = string.Join('/', full.Split('/').Select(Uri.EscapeDataString));
        for (int copy = 1; ; copy++)
        {
            string kept = copy == 1 ? name : $"{name}.{copy}";
            string target = Path.Combine(files, kept);
            string record = Path.Combine(info, kept + ".trashinfo");
            if (Path.Exists(target))
            {
                continue;
            }
            // Made only when no record has the name, so two programs trashing at once never share one.
            try
            {
                using FileStream claimed = new(record, FileMode.CreateNew, FileAccess.Write);
                using StreamWriter writer = new(claimed);
                writer.Write($"[Trash Info]\nPath={where}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
            }
            catch (IOException) when (File.Exists(record))
            {
                continue;
            }
            try
            {
                MoveTo(full, target);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                File.Delete(record);
                throw;
            }
            return;
        }
    }

    private static void MoveTo(string path, string target)
    {
        if (Directory.Exists(path))
        {
            Directory.Move(path, target);
        }
        else
        {
            File.Move(path, target);
        }
    }

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Class(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint argument);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Send(nint receiver, nint selector, string argument);

    /// <summary>A message answering a BOOL, its last argument where an NSError is put (left as it was if none is).</summary>
    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool Send(nint receiver, nint selector, nint argument, nint second, ref nint error);
}
