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
        Editors.Commands.LetGo = LetGoForCommandAsync;
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

    /// <summary>Asks for a name in a popup, which stays open saying why while <paramref name="attempt"/> refuses it, the
    /// first <paramref name="selected"/> characters of the initial name selected (all of them when null).</summary>
    private void AskName(string title, string initial, Func<string, Task<string?>> attempt, int? selected = null)
    {
        Vcs.NamePopup popup = new(title, initial, attempt, selected: selected);
        popups.Show(popup, _ => { });
    }

    /// <summary>Asks for a name as <see cref="AskName"/> does, answering with the one taken, or null once the popup closes
    /// without one (Escape, a click outside it).</summary>
    private Task<string?> AskNameAsync(string title, string initial, Func<string, string?> refuse, int? selected = null)
    {
        TaskCompletionSource<string?> answer = new();
        string? taken = null;
        Vcs.NamePopup popup = new(title, initial, name =>
        {
            string? why = refuse(name);
            taken = why is null ? name : null;
            return Task.FromResult(why);
        }, selected: selected);
        popup.DetachedFromVisualTree += (_, _) => answer.TrySetResult(taken);
        popups.Show(popup, _ => { });
        return answer.Task;
    }

    /// <summary>The C# types Class/Interface makes, in the order its popup lists them.</summary>
    public static IReadOnlyList<string> TypeKinds => Templates.OwnTemplates.TypeKinds;

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

    /// <summary>Makes a file in a folder (folders in its name made too) and opens it; null when it worked, else why not. One
    /// made in a shared project's folder goes into its items file too, the two one command Undo takes back whole.</summary>
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
        using CommandHistory.Recording? recording = InShared([path]) ? Editors.Commands.Record($"New File {Path.GetFileName(path)}") : null;
        recording?.Made(path);
        string? unshared = null;
        try
        {
            Following([path], () =>
            {
                FileText.SaveNew(path, text);
                unshared = recording is null ? null : IntoShared([PathNames.Canonical(path)], recording);
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            recording?.Unwritten(path);
            return NotMade(name, error);
        }
        if (unshared is not null)
        {
            status.Text = unshared;
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
    /// namespace its folder gives (<see cref="CreateOwn"/>).</summary>
    public string? CreateType(string into, string name, string kind) =>
        CreateOwn(into, name, Templates.OwnTemplates.All.First(t => t.Name == kind).Own!);

    /// <summary>A file of one of Passenger's own templates (Class/Interface's types, Add's New): of the name given, its
    /// extension added where it was not written, made from the template with the namespace its folder gives, a type's name
    /// a C# name. Folders before the name ("Shapes/Circle") are made and the file goes in them.</summary>
    public string? CreateOwn(string into, string name, Templates.OwnTemplate own)
    {
        string[] parts = name.Trim().Split('/', '\\');
        string bare = own.Bare(parts[^1]);
        if (bare.Length == 0)
        {
            return "A name is needed";
        }
        if (own.DeclaresType && !Microsoft.CodeAnalysis.CSharp.SyntaxFacts.IsValidIdentifier(bare))
        {
            return $"{bare} is not a C# name";
        }
        if (Invalid(name) is { } problem)
        {
            return problem;
        }
        into = Path.Combine([into, .. parts[..^1].Where(p => p.Length > 0)]);
        return CreateFile(into, bare + own.Extension, own.Text(bare, NamespaceFor(into)));
    }

    /// <summary>The namespace a folder's code takes: its project's evaluated RootNamespace (until it is evaluated, its
    /// name, "Shop.App"), a shared project's as its items file names it, then the folders below, each name a C#
    /// identifier or dotted ones.</summary>
    private string NamespaceFor(string into)
    {
        string full = PathNames.Canonical(into);
        ProjectModel? project = ProjectOf(full);
        string? shared = project is null ? SharedItemsOf(Path.Combine(full, "_")) : null;
        string root = shared is not null ? SharedProjects.RootNamespaceOf(shared) ?? Path.GetFileNameWithoutExtension(shared)
            : project is null ? Path.GetFileName(Folder ?? full)
            : project.Property(Passenger.Host.CarriedProperties.RootNamespace) is { Length: > 0 } named ? named
            : Path.GetFileNameWithoutExtension(project.Path);
        string below = shared is not null ? Path.GetRelativePath(Path.GetDirectoryName(shared)!, full)
            : project is null ? string.Empty : Path.GetRelativePath(project.Folder, full);
        IEnumerable<string> parts = [root, .. below == "." ? [] : below.Split(Path.DirectorySeparatorChar)];
        return string.Join('.', parts.SelectMany(p => p.Split('.', StringSplitOptions.RemoveEmptyEntries)).Select(Identifier));

        static string Identifier(string part)
        {
            string name = new([.. part.Select(c => AssistContext.IsIdentifierChar(c) ? c : '_')]);
            return AssistContext.IsIdentifierStart(name[0]) ? name : "_" + name;
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
            AskName("Rename", Path.GetFileName(path), name => RenameAsync(path, name), BeforeExtension(path));
            return;
        }
        OpenDocument? declaring = Editors.Find(type.Declaring);
        Anchor? held = declaring?.Text.AnchorAt(type.Offset, AnchorGravity.After);
        Vcs.NamePopup? popup = null;
        // The type's name selected, so what is typed over it keeps what follows (Main.xaml.cs's .xaml.cs).
        popup = new Vcs.NamePopup("Rename", Path.GetFileName(path), Attempt,
            option: ($"Rename type '{type.Name}' to match the new file name", assist.RenameSettings.RenameType), selected: TypeRename.Stem(path).Length);
        popup.OptionChanged += (_, on) => assist.RenameSettings = assist.RenameSettings with { RenameType = on };
        popup.DetachedFromVisualTree += (_, _) => held?.Remove();
        popups.Show(popup, _ => { });

        Task<string?> Attempt(string name) =>
            popup?.Option?.IsChecked != true || TypeRename.Stem(name) == type.Name ? RenameAsync(path, name)
            : TypeNow(path, type, declaring, held) is { } now ? RenameWithTypeAsync(path, name, now, renameType: true)
            : Task.FromResult<string?>($"{Path.GetFileName(type.Declaring)} no longer declares {type.Name}: turn renaming the type off to rename the file alone");
    }

    /// <summary>How much of a file's name its rename selects: what comes before its extension, so a name typed over it keeps
    /// the extension; null (all of it) for a folder and a file with no extension or no name before it (.gitignore).</summary>
    private static int? BeforeExtension(string path)
    {
        string name = Path.GetFileName(path);
        string stem = Path.GetFileNameWithoutExtension(path);
        return Directory.Exists(path) || stem.Length == 0 || stem.Length == name.Length ? null : stem.Length;
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
                // A type declared whole again elsewhere (the file a copy of another) is renamed in the file named alone.
                given = named is { Apart: true } ? TypeRename.Only(given, type.Declaring) : given;
                return await WorkspaceEdits.StaleAsync(Editors, paths => server.HeldTextsAsync(paths), given, asked.Since, patience,
                        elsewhere: after => server.WrittenSinceAsync(after)) is { } stale
                    ? (Refusals.NotRenamed(type.Name, stale), given) : (null, given);
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
            List<string> left = [];
            string? ungodot = applied is { Moved.Count: > 0 } done ? await FollowGodotAsync([.. done.Moved.Select(m => (m.From, PathNames.Canonical(m.To)))], recording, left) : null;
            Explorer.Reveal(PathNames.Canonical(applied?.Moved.Any(m => PathNames.Same(m.From, path)) == true ? target : path));
            await WorkspaceEdits.LandedAsync(recording, waiting => status.Text = waiting);
            string unfollowed = ungodot is not null || left.Count > 0 ? $"; {ungodot ?? string.Join("; ", left)}" : string.Empty;
            // The code renamed with a file left unmoved is said as made: the box closes, and Undo takes it back whole.
            status.Text = applied?.Unmoved is { } unmoved
                ? $"{Refusals.EditedMeanwhile($"Renamed {type.Name} to {renamed} in {Plural.Of(files, "file")}", dropped)}{(dropped.Count > 0 ? "; and" : ", but")} {unmoved}{unfollowed}"
                : $"{Refusals.EditedMeanwhile($"Renamed {Path.GetFileName(path)} to {name}, and {type.Name} to {renamed} in {Plural.Of(files, "file")}", dropped)}{unfollowed}";
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
    /// with Counter.razor), all or none: one that cannot move stops it, those moved before it taken back. Its open files
    /// keep their tabs, splits and undo, saved first, its bookmarks and breakpoints go with it, and what names it follows
    /// (projects' items, the solution, references to a project). Undo in the Solution view renames it all back, once what
    /// it wrote of the files that follow it has landed.</summary>
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
        if (moves.Select(m => m.From).Where(File.Exists).Select(Unmovable.Why).FirstOrDefault(why => why is not null) is { } held)
        {
            return $"{held}: nothing was renamed";
        }
        using IDisposable? releasing = Directory.Exists(path)
            ? await LetGoAsync([path], $"Waiting for the design-time build of {Path.GetFileName(path)} to end before it is renamed…")
            : null;
        using CommandHistory.Recording recording = Editors.Commands.Record($"Rename {Path.GetFileName(path)} to {name}");
        string? unmoved = null;
        string? failed = Following([.. moves.SelectMany(m => new[] { m.From, m.To })], () =>
        {
            foreach ((string from, string to) in moves)
            {
                if (recording.Move(from, to) is { } refused)
                {
                    unmoved = from;
                    string why = $"{Path.GetFileName(from)} could not be renamed: {refused}";
                    (IReadOnlyList<(string From, string To)> kept, string? stuck) = recording.MoveBack();
                    if (stuck is null)
                    {
                        return $"{why}, so nothing was renamed";
                    }
                    // Those that could not go back stay renamed, with what names them following them.
                    string? unnamed = FollowMoved(kept, recording);
                    return $"{why}; {stuck}{(unnamed is null ? string.Empty : $"; {unnamed}")}";
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
            return unmoved is not null && await HeldByAsync(unmoved) is { Length: > 0 } holders ? $"{failed}.{holders}" : failed;
        }
        List<string> left = [];
        if (await FollowGodotAsync([.. moves.Select(m => (m.From, PathNames.Canonical(m.To)))], recording, left) is { } ungodot)
        {
            status.Text = ungodot;
        }
        else if (left.Count > 0)
        {
            status.Text = string.Join("; ", left);
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

    /// <summary>Delete on a project: it leaves the solution, and the solution filter open, and its files stay. Moving its
    /// folder to the Trash is a second question, never asked of the open folder, a folder above it, or one holding the
    /// solution or another of its projects. Both are one command Undo takes back whole.</summary>
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
        // Its second question waits between its writes, so of the open files it takes only what it writes.
        using CommandHistory.Recording removing = Editors.Commands.RecordWrites($"Remove Project {name}");
        if (await EditSolutionAsync(file => SolutionEdits.RemoveProject(file, project), $"Removed {name} from {solution.Name}", [project], $"Remove Project {name}", removing) is { } problem)
        {
            status.Text = $"{name} could not be removed: {await removing.TakenBackAsync(problem)}";
            return false;
        }
        string own = PathNames.Canonical(Path.GetDirectoryName(project)!);
        bool apart = !PathNames.Within(Folder, own) && !PathNames.Within(solution.Path, own)
            && !Projects.Any(p => !PathNames.Same(p.Path, project) && PathNames.Within(p.Path, own));
        if (apart && await Confirm("Delete Project Files", $"Also move the folder {Path.GetFileName(own)} and everything in it to the Trash?", "Move to Trash"))
        {
            await DeleteAsync(own, asked: true, within: removing);
        }
        return true;
    }

    /// <summary>How long a delete, or a folder's rename or move, waits for the design-time builds of the projects it takes
    /// to end.</summary>
    private static readonly TimeSpan BuildsGoing = TimeSpan.FromSeconds(60);

    /// <summary>Deletes a file or folder to the Trash, as <see cref="DeleteAsync(IReadOnlyList{string}, bool, CommandHistory.Recording?)"/>
    /// deletes several.</summary>
    public Task<bool> DeleteAsync(string path, bool asked = false, CommandHistory.Recording? within = null) => DeleteAsync([path], asked, within);

    /// <summary>Deletes files and folders to the Trash, none inside another, once asked in one question (unless it was
    /// already), as <see cref="TrashAsync"/> moves them, within the command given: true once all went.</summary>
    public async Task<bool> DeleteAsync(IReadOnlyList<string> paths, bool asked = false, CommandHistory.Recording? within = null) =>
        (asked || await Confirm("Delete", DeleteQuestion(paths), "Delete")) && await TrashAsync(paths, within) is [];

    /// <summary>Moves files and folders to the Trash, none inside another, said in the status bar; their open files are
    /// saved first, and again just before they go with what was typed while it waited, so the Trash and Local History have
    /// what the editor showed, and close once gone. The design-time builds of the projects in them are held off, those
    /// running waited out. Each goes in the command given, else one of its own named after them (Undo takes each back out of
    /// the Trash, a shared project's items file with it). Each that stayed, with why; null where an open file was not saved.</summary>
    private async Task<IReadOnlyList<(string Path, string Why)>?> TrashAsync(IReadOnlyList<string> paths, CommandHistory.Recording? within)
    {
        List<string> names = [.. paths.Select(Path.GetFileName).OfType<string>()];
        if (SavedFirst() is null)
        {
            return null;
        }
        History.LocalHistory? keeping = session.LocalHistory;
        History.HistoryDeletion?[] taken = keeping is null ? new History.HistoryDeletion?[paths.Count] : await Task.WhenAll(paths.Select(keeping.CaptureAsync));
        using IDisposable releasing = await LetGoAsync(paths,
            $"Waiting for the design-time {Plural.Noun(paths.Count, "build")} of {Plural.Listed(names)} to end before {Plural.Agreeing(paths.Count, "it goes", "they go")} to the Trash…");
        // Nothing is awaited from here to the moves, so what was typed while it waited goes with them.
        if (SavedFirst() is not { } typed)
        {
            return null;
        }
        List<string> gone = [];
        List<(string Path, string Why)> refused = [];
        using CommandHistory.Recording? own = within is null ? Editors.Commands.Record($"Delete {Plural.Listed(names)}") : null;
        CommandHistory.Recording recording = within ?? own!;
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (taken[i] is { } deletion && typed.Where(t => PathNames.Within(t, path)).ToList() is { Count: > 0 } retyped)
            {
                taken[i] = keeping!.Retaken(deletion, retyped);
            }
            try
            {
                Trashed? went = Trash.Move(path);
                gone.Add(path);
                recording.Deleted(path, went);
                if (taken[i] is { } kept)
                {
                    keeping!.Deleted(kept);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                refused.Add((path, error.Message));
            }
        }
        foreach (OpenDocument document in Editors.Documents.Where(d => gone.Any(path => PathNames.Within(d.Path, path))).ToList())
        {
            Editors.CloseDeleted(document);
        }
        if (gone.Count > 0)
        {
            CameOrWent(gone);
        }
        List<string> said = gone.Count == 0 ? [] : [$"Moved {Plural.Listed([.. gone.Select(Path.GetFileName).OfType<string>()])} to the Trash"];
        if (gone.Count > 0 && OutOfShared(gone, recording) is { } unshared)
        {
            said.Add(unshared);
        }
        // Those refused for one reason are named together, as the Trash refusing every one would be.
        List<(string Path, string Why)> why = [];
        foreach ((string path, string message) in refused)
        {
            why.Add((path, message + await HeldByAsync(path)));
        }
        said.AddRange(why.GroupBy(r => r.Why).Select(g => $"{Plural.Listed([.. g.Select(r => Path.GetFileName(r.Path))])} could not be deleted: {g.Key}"));
        status.Text = string.Join("; ", said);
        return why;

        // The open files under the paths with edits not saved, each saved; null, said, when one could not be.
        List<string>? SavedFirst()
        {
            List<string> saved = [];
            foreach (OpenDocument document in Editors.Documents.Where(d => d.IsModified).ToList())
            {
                if (paths.FirstOrDefault(path => PathNames.Within(document.Path, path)) is not { } under)
                {
                    continue;
                }
                if (!Editors.Save(document))
                {
                    status.Text = $"{Path.GetFileName(under)} could not be deleted: {document.Name} could not be saved first";
                    return null;
                }
                saved.Add(document.Path);
            }
            return saved;
        }
    }

    /// <summary>Delete's question: the file or folder it takes by name, several by their count.</summary>
    private static string DeleteQuestion(IReadOnlyList<string> paths)
    {
        int folders = paths.Count(Directory.Exists);
        int files = paths.Count - folders;
        if (paths.Count == 1)
        {
            string what = Path.GetFileName(paths[0]);
            return folders == 1 ? $"Move the folder {what} and everything in it to the Trash?" : $"Move {what} to the Trash?";
        }
        string everything = $"everything in {Plural.Agreeing(folders, "it", "them")}";
        return folders == 0 ? $"Move {Plural.Of(files, "file")} to the Trash?"
            : files == 0 ? $"Move {Plural.Of(folders, "folder")} and {everything} to the Trash?"
            : $"Move {Plural.Of(files, "file")} and {Plural.Of(folders, "folder")}, with {everything}, to the Trash?";
    }

    /// <summary>Lets go of what is about to go or move until disposed: the builds and evaluations of the projects reaching it
    /// (<see cref="FolderBuilds.Reaches"/>) are held off, nothing under it is watched or read for the C# language server, and,
    /// once those have ended, the IDE's watchers read nothing there (<see cref="MovingFolders"/>), all within <see
    /// cref="BuildsGoing"/>, saying <paramref name="waiting"/> meanwhile. A build writing obj/ would make the folder again; on
    /// Windows a folder a build works in, or with a folder inside it watched or a file or folder inside it open, won't move.</summary>
    private async Task<IDisposable> LetGoAsync(IReadOnlyList<string> paths, string waiting)
    {
        Deadline until = Deadline.In(BuildsGoing);
        (IDisposable held, Task built) = session.DesignTimeBuilds.HoldOff(paths);
        (IDisposable unevaluated, Task evaluated) = session.Models.HoldOff(paths);
        (IDisposable? unwatched, Task unread) = session.CSharp is { } csharp ? csharp.LetGoOf(paths) : (null, Task.CompletedTask);
        // Only while one is under way, so a move with nothing to wait for goes on in this turn, saying nothing.
        if (!built.IsCompleted || !evaluated.IsCompleted || !unread.IsCompleted)
        {
            status.Text = waiting;
            try
            {
                await Task.WhenAll(built, evaluated, unread.Ended()).WaitAsync(until.Left);
            }
            catch (TimeoutException)
            {
                // Gone or moved anyway: what the build writes after makes only build output there again.
                IdeErrors.Write($"Waited {Durations.Said(BuildsGoing)} for the design-time builds in {string.Join(", ", paths)}, the watchers stopped there and the C# language server's loads and questions that read there, to end, and went on without them");
            }
        }
        // Held only now, so what the watchers hear elsewhere meanwhile is not held up behind a change there.
        (IDisposable unheard, Task quiet) = MovingFolders.Hold(paths);
        if (!quiet.IsCompleted)
        {
            try
            {
                await quiet.WaitAsync(until.Left);
            }
            catch (TimeoutException)
            {
                IdeErrors.Write($"Went on before the folder watchers' reads in {string.Join(", ", paths)} had ended, {Durations.Said(BuildsGoing)} after the wait began");
            }
        }
        return new LetGo(held, unevaluated, unwatched, unheard);
    }

    /// <summary>Lets go of the folders an Undo or Redo moves (<see cref="CommandHistory.LetGo"/>) as a move does, saying
    /// meanwhile whose design-time builds it waits for: those of the folders there now.</summary>
    private Task<IDisposable> LetGoForCommandAsync(IReadOnlyList<string> folders, string verb)
    {
        List<string> there = [.. folders.Where(Directory.Exists).Select(Path.GetFileName).OfType<string>()];
        return LetGoAsync(folders,
            $"Waiting for the design-time {Plural.Noun(there.Count, "build")} of {Plural.Listed(there)} to end before {verb} moves {Plural.Agreeing(there.Count, "it", "them")}…");
    }

    /// <summary>What was let go of for a move or a delete (<see cref="LetGoAsync"/>), taken up again once it is done.</summary>
    private sealed class LetGo(IDisposable builds, IDisposable evaluations, IDisposable? watches, IDisposable reads) : IDisposable
    {
        public void Dispose()
        {
            reads.Dispose();
            builds.Dispose();
            evaluations.Dispose();
            watches?.Dispose();
        }
    }

    /// <summary>The programs holding a file or folder that would not go or move, or what is under it, as Windows names them
    /// (<see cref="FileHolders.Within"/>): " Held by powershell (process 1234)." for one holding it, " Held by dotnet
    /// (process 1234: obj\project.assets.json)." for one holding what is under it; read off the window's thread, nothing
    /// where none is told.</summary>
    private static async Task<string> HeldByAsync(string path)
    {
        List<string> named = await BackgroundWork.Run(() => FileHolders.Within(path).Select(held =>
        {
            if (held.Process == Environment.ProcessId)
            {
                return FileHolders.Itself;
            }
            List<string> under = [.. held.Paths.Where(p => !PathNames.Same(p, path)).Select(p => PathNames.Shown(p, path))];
            string inside = under.Count <= 3 ? Plural.Listed(under) : $"{string.Join(", ", under.Take(2))} and {Numbers.Said(under.Count - 2)} more";
            return DotnetProcesses.Describe(held.Process) is { } process
                ? $"{process.Program} (process {held.Process}{(inside.Length == 0 ? string.Empty : $": {inside}")})"
                : $"process {held.Process}{(inside.Length == 0 ? string.Empty : $" ({inside})")}";
        }).ToList());
        return named.Count == 0 ? string.Empty : $" Held by {Plural.Listed(named)}.";
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

    /// <summary>Workspace files the IDE writes itself (a project's, the solution's, a launch profile), as part of the command
    /// given (none for a change no Undo takes back): those open with unsaved edits saved first, each watched for the command,
    /// what the change makes, moves or deletes followed (<see cref="Following{T}"/>), the open ones taking the text written
    /// inside the command, then its writes waited for. Null once landed, else why not: the change's own refusal, or the
    /// disk's words for a write it refused.</summary>
    private async Task<string?> ChangeFileAsync(IReadOnlyCollection<string> paths, Func<string?> write, CommandHistory.Recording? into)
    {
        // Through the command's recording, so its writes of the open ones among them are not asked about again.
        if (await (into is null ? Editors.ClearAsync(paths) : into.ClearAsync(paths)) is { } held)
        {
            return held;
        }
        if (SaveBeforeWriting(paths) is { } unsaved)
        {
            return unsaved;
        }
        foreach (string path in paths)
        {
            into?.Watch(path);
        }
        string? problem;
        try
        {
            problem = Following(paths, () =>
            {
                try
                {
                    return write();
                }
                finally
                {
                    // Open, they take the text now, inside the command, so its Undo takes it back too.
                    FollowWritten(paths);
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            problem = error.Message;
        }
        return problem ?? (await (into is null ? UnlandedAsync(paths) : UnlandedAsync(into)))?.Message;
    }

    /// <summary>As <see cref="ChangeFileAsync(IReadOnlyCollection{string}, Func{string?}, CommandHistory.Recording?)"/>, part
    /// of the command given, else one command of its own Undo takes back whole, named <paramref name="title"/>, all it did
    /// taken back first once it is refused partway (<see cref="CommandHistory.Recording.TakenBackAsync"/>).</summary>
    private async Task<string?> ChangeFileAsync(string title, IReadOnlyCollection<string> paths, Func<string?> write, CommandHistory.Recording? into = null)
    {
        if (into is not null)
        {
            return await ChangeFileAsync(paths, write, into);
        }
        using CommandHistory.Recording made = Editors.Commands.Record(title);
        return await ChangeFileAsync(paths, write, made) is { } problem ? await made.TakenBackAsync(problem) : null;
    }

    /// <summary>As <see cref="Following{T}"/>, a change made within a command's recording, followed only once the recording
    /// has written the files not open it changes (<see cref="CommandHistory.Recording.WriteAsync"/>), so what reads them
    /// then reads them as the change left them. Throws <see cref="WorkspaceEditFailed"/> naming a file the disk refused, once
    /// all the command did is taken back.</summary>
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
        await WorkspaceEdits.WrittenAsync(recording, refused);
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
    /// ends of the moves it made, as <see cref="CameOrWent"/> does, the solution read again given <paramref name="readAgain"/>;
    /// with none, the projects whose evaluation read one of <paramref name="texts"/> are evaluated again. The XAML reading
    /// a C# file among them or the moves' reads it again either way.</summary>
    private void Followed(Dictionary<string, bool> stood, IReadOnlyCollection<string> moved, IReadOnlyCollection<string> texts, bool readAgain = false)
    {
        List<string> cameOrWent = [.. moved, .. stood.Where(s => s.Value != Stands(s.Key)).Select(s => s.Key)];
        if (cameOrWent.Count > 0)
        {
            CameOrWent(cameOrWent, readAgain);
        }
        else if (readAgain)
        {
            ReloadSolution();
        }
        else if (session.Models.Read(texts))
        {
            // Only texts changed, a project's or an import's among them: the projects that read it are evaluated again.
            session.Evaluate();
        }
        // Now, rather than once the watcher hears of it.
        session.Xaml.FilesChanged([.. texts.Concat(moved).Where(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct(PathNames.Comparer)]);
        session.GodotScenes.FilesChanged(texts);
        IdeErrors.Observe(XamlWrittenAsync(texts));
    }

    /// <summary>Files or folders the IDE itself made, moved or deleted, followed at once rather than when the folder watcher
    /// hears of them: the Explorer reads again only the folders that held or hold them, keeping the rows still there, Go to
    /// File's index takes them in or out in place, the projects are checked for what their wildcards take in and for one
    /// come back (<see cref="ProjectSession.CameOrWent"/>, the solution read again given <paramref name="readAgain"/>), and
    /// Git's colours follow.</summary>
    private void CameOrWent(IReadOnlyCollection<string> paths, bool readAgain = false)
    {
        HashSet<string> named = new(paths.Select(PathNames.Canonical), StringComparer.Ordinal);
        Explorer.RefreshFolders(named.Select(Path.GetDirectoryName).OfType<string>());
        session.CameOrWent(named, readAgain);
        session.MSBuild.FilesCameOrWent();
        session.CSharp?.FilesCameOrWent(named);
        session.Xaml.CameOrWent(named);
        session.GodotScenes.CameOrWent(named);
        if (Folder is { } folder)
        {
            session.Index.Apply(folder, named);
        }
        IdeErrors.Observe(session.Vcs.RefreshAsync());
    }

    /// <summary>Why a name given a new or renamed file or folder is none: empty, holding a character no name may, or with a
    /// part that climbs out of its folder (".."), where two dots inside a name (release..notes.txt) are its own.</summary>
    private static string? Invalid(string name) =>
        name.Trim().Length == 0 ? "A name is needed"
        : name.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c is not '/' and not '\\').ToArray()) >= 0 || name.Split(['/', '\\']).Contains("..") ? $"{name} is not a file name"
        : null;
}
