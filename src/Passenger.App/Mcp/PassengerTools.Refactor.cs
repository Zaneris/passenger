using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Passenger.App.CSharp;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;

namespace Passenger.App.Mcp;

/// <summary>The window's refactorings for an agent, each through the same method of <see cref="CodeActions"/> its command
/// goes through, with its dialog's options as arguments: Change Signature, Safe Delete, Move, Extract Interface, Pull
/// Members Up and Push Members Down, the server's Introduce Field, Introduce Parameter and Encapsulate Field, and Optimize
/// Imports and Code Cleanup over a file, a project or the solution. Asked without options, a kind with a dialog answers
/// what its dialog would show; each change is one command of the IDE's undo, and the files are saved.</summary>
public sealed partial class PassengerTools
{
    private static readonly JsonSerializerOptions DialogJson = new(JsonSerializerDefaults.Web);

    private const string RefactorKinds = "change_signature, safe_delete, move, extract_interface, pull_members_up, push_members_down, introduce_field, "
        + "introduce_parameter, encapsulate_field, optimize_imports, code_cleanup";

    [McpServerTool(Name = McpTools.Refactor, Title = "Refactor across the solution", Destructive = true), Description(
        "Runs one of the IDE's C# refactorings (kind) at path, line, column (a selection to endLine:endColumn), as its command " +
        "and dialog do: every file it touches (callers, implementations, usings, new files) changed as one Undo step, saved. " +
        "options is one JSON object, by kind:\nchange_signature (a method or constructor): {name, returnType, parameters: " +
        "[{from: \"oldName\", name?, type?, modifier?, defaultValue?} for each kept, in the new order; {type, name, callValue " +
        "(what every call passes), modifier?, defaultValue?} for a new one]}; a parameter left out is removed.\nmove (a type): " +
        "{file: \"Circle.cs\"}, a new file beside its own, or {namespace: \"Shop.Sales\"}.\nextract_interface (a class, struct " +
        "or record): {name, file, members: [...]}, all public members by default.\npull_members_up: {target: a base type, " +
        "members: [...], abstract: [...]}; push_members_down: {targets: [derived classes], members, abstract}; a member by its " +
        "text as described (\"Add(int): void\"), its name when that is one member, or its id.\nsafe_delete (a declaration or " +
        "use): refused, listing the usages that would break, unless {deleteAnyway: true}.\nintroduce_field, " +
        "introduce_parameter (an expression selected), encapsulate_field (a field): the language server's, {action, choice} " +
        "naming one where it offers several.\noptimize_imports, code_cleanup: {scope: file, project or solution}, by default " +
        "path's file, a .csproj path's project, or with no path the solution; code_cleanup reformats XAML too.\nWithout " +
        "options, the kinds with a dialog (change_signature, move, extract_interface, pull_members_up, push_members_down) " +
        "answer it (dialog), its defaults as options to change and send back; describe true does so for any kind, changing " +
        "nothing. Applied, it answers each file created, changed, moved or deleted, the lines its edits start on, and what it " +
        "left alone.")]
    public Task<RefactorAnswer> Refactor(
        string kind,
        string? path = null,
        int? line = null,
        int? column = null,
        int? endLine = null,
        int? endColumn = null,
        JsonElement? options = null,
        bool describe = false,
        CancellationToken cancel = default) => OnUi(cancel, () => Patience.RunAsync(async patience =>
    {
        string named = kind.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        JsonElement? given = Options(options);
        switch (named)
        {
            case "optimize_imports" or "code_cleanup":
                return await TidyAsync(named, path, given, describe, cancel);
            case "change_signature" or "safe_delete" or "move" or "extract_interface" or "pull_members_up" or "push_members_down"
                or "introduce_field" or "introduce_parameter" or "encapsulate_field":
                break;
            default:
                throw new ArgumentException($"No refactoring '{kind}'; there are {RefactorKinds}", nameof(kind));
        }
        if (path is null || line is null || column is null)
        {
            throw new ArgumentException($"{named} needs the place it works on: path, line and column", nameof(path));
        }
        (OpenDocument document, LspDocument lsp) = await CSharpOnly(path, named, cancel);
        int offset = Offset(document, line.Value, column.Value);
        bool describing = describe || (given is null && named is "change_signature" or "move" or "extract_interface" or "pull_members_up" or "push_members_down");
        RefactorAnswer answer = named switch
        {
            "change_signature" => await ChangeSignatureAsync(document, lsp, offset, given, describing, patience),
            "extract_interface" => await ExtractInterfaceAsync(document, lsp, offset, given, describing, patience),
            "move" => await MoveAsync(document, lsp, offset, given, describing, patience),
            "pull_members_up" => await MembersAsync(document, lsp, offset, given, describing, up: true, patience),
            "push_members_down" => await MembersAsync(document, lsp, offset, given, describing, up: false, patience),
            "safe_delete" => await SafeDeleteAsync(lsp, offset, given, describing, cancel),
            _ => await ServerRefactorAsync(named, document, lsp, offset, endLine, endColumn, given, describing, patience),
        };
        if (answer.Files.Count > 0)
        {
            Editors.SaveAll();
        }
        return answer;
    }, cancel));

    /// <summary>The file a refactoring works on, which only C# files have: any other language is told what it has instead.</summary>
    private async Task<(OpenDocument Document, LspDocument Lsp)> CSharpOnly(string path, string kind, CancellationToken cancel)
    {
        Served served = await Serve(path, cancel);
        return served switch
        {
            ServerServed { Web: null } csharp when csharp.Document.Language == Languages.Language.CSharp => (csharp.Document, csharp.Lsp),
            XamlServed => throw Lacks(served, Titled(kind),
                "rename renames an element's name, code_actions and apply_code_action extract a resource or a style, and code_cleanup reformats it"),
            MSBuildServed => throw Lacks(served, Titled(kind), MSBuildHas),
            _ => throw Lacks(served, Titled(kind), "rename, code_actions and apply_code_action are its language server's refactorings"),
        };
    }

    private static string Titled(string kind) => string.Join(' ', kind.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    private async Task<RefactorAnswer> ChangeSignatureAsync(OpenDocument document, LspDocument lsp, int offset, JsonElement? options, bool describing, Patience patience)
    {
        Signature signature = await CSharp.CodeActions.DescribeAtAsync<Signature>(lsp, offset, Handlers.DescribeSignature);
        if (signature.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        bool method = signature.Kind == "method";
        if (describing)
        {
            string preview = $"{(method ? $"{signature.Modifiers} {signature.ReturnType} {signature.Name}" : $"{signature.Modifiers} {signature.Container}").Trim()}"
                + $"({string.Join(", ", signature.Parameters.Select(p => $"{p.Modifier} {p.Type} {p.Name}".Trim() + (p.DefaultValue.Length > 0 ? $" = {p.DefaultValue}" : string.Empty)))})";
            return Described("change_signature", $"Change Signature of {signature.Container}{(method ? "." + signature.Name : string.Empty)}", new
            {
                signature.Kind,
                signature.Container,
                signature.Modifiers,
                signature.Name,
                signature.ReturnType,
                signature.Parameters,
                signature.IsExtension,
                Preview = preview,
                Options = new { signature.Name, ReturnType = method ? signature.ReturnType : string.Empty, Parameters = signature.Parameters.Select(p => new { From = p.Name }) },
            });
        }
        JsonElement given = options!.Value;
        string name = Text(given, "name") ?? signature.Name;
        string returnType = Text(given, "returnType") ?? (method ? signature.ReturnType : string.Empty);
        if (!method && (name != signature.Name || returnType.Length > 0))
        {
            throw new ArgumentException($"A constructor's name is its type's and it has no return type: change only {signature.Container}'s parameters");
        }
        List<ParameterChange> parameters = [];
        if (!given.TryGetProperty("parameters", out JsonElement asked) || asked.ValueKind == JsonValueKind.Null)
        {
            parameters.AddRange(signature.Parameters.Select((p, i) => new ParameterChange(i, p.Modifier, p.Type, p.Name, p.DefaultValue, string.Empty)));
        }
        else if (asked.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("parameters is a list, in the new order: {from: \"oldName\"} for each kept, {type, name, callValue} for each new one");
        }
        else
        {
            foreach (JsonElement parameter in asked.EnumerateArray())
            {
                string? from = parameter.ValueKind == JsonValueKind.String ? parameter.GetString() : Text(parameter, "from");
                if (from is not null)
                {
                    int was = signature.Parameters.ToList().FindIndex(p => p.Name == from);
                    if (was < 0)
                    {
                        throw new ArgumentException($"{signature.Name} has no parameter '{from}'; it has {string.Join(", ", signature.Parameters.Select(p => p.Name))}");
                    }
                    Parameter old = signature.Parameters[was];
                    JsonElement changed = parameter.ValueKind == JsonValueKind.Object ? parameter : default;
                    parameters.Add(new ParameterChange(was, Text(changed, "modifier") ?? old.Modifier, Text(changed, "type") ?? old.Type, Text(changed, "name") ?? old.Name,
                        Text(changed, "defaultValue") ?? old.DefaultValue, string.Empty));
                }
                else
                {
                    string type = Text(parameter, "type") ?? throw new ArgumentException("A new parameter needs its type (and from, for one that was there: its old name)");
                    string made = Text(parameter, "name") ?? throw new ArgumentException($"The new {type} parameter needs a name");
                    parameters.Add(new ParameterChange(-1, Text(parameter, "modifier") ?? string.Empty, type, made, Text(parameter, "defaultValue") ?? string.Empty,
                        Text(parameter, "callValue") ?? string.Empty));
                }
            }
        }
        return Applied("change_signature", await window.Assist.Actions.ChangeSignatureAsync(document, offset, signature, name, returnType, parameters, patience));
    }

    private async Task<RefactorAnswer> ExtractInterfaceAsync(OpenDocument document, LspDocument lsp, int offset, JsonElement? options, bool describing, Patience patience)
    {
        InterfaceSource source = await CSharp.CodeActions.DescribeAtAsync<InterfaceSource>(lsp, offset, Handlers.DescribeInterface);
        if (source.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        if (describing)
        {
            return Described("extract_interface", $"Extract Interface from {source.Type}", new
            {
                source.Type,
                source.Namespace,
                source.Members,
                Options = new { source.Name, File = source.FileName, Members = source.Members.Select(m => m.Text) },
            });
        }
        JsonElement given = options!.Value;
        string name = Text(given, "name") ?? source.Name;
        // The file follows the name until it is given, as the dialog's does until it is typed in.
        string file = Text(given, "file") ?? (Text(given, "name") is { } typed ? $"{typed.Trim()}.cs" : source.FileName);
        IReadOnlyList<string> members = Texts(given, "members") is { } chosen ? [.. chosen.Select(m => MemberId(source.Members, m, $"members {source.Type} can give an interface"))] : [.. source.Members.Select(m => m.Id)];
        return Applied("extract_interface", await window.Assist.Actions.ExtractInterfaceAsync(document, offset, source, name, file, members, patience));
    }

    private async Task<RefactorAnswer> MoveAsync(OpenDocument document, LspDocument lsp, int offset, JsonElement? options, bool describing, Patience patience)
    {
        TypeSource type = await CSharp.CodeActions.DescribeAtAsync<TypeSource>(lsp, offset, Handlers.DescribeType);
        if (type.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        if (describing)
        {
            return Described("move", $"Move {type.Kind} '{type.Name}': to another file, or to a namespace", new
            {
                type.Name,
                type.Kind,
                type.Namespace,
                File = type.FileName,
                type.TypesInFile,
                ToFile = new { File = type.TargetFileName },
                ToNamespace = new { type.Namespace },
            });
        }
        JsonElement given = options!.Value;
        (string? file, string? space) = (Text(given, "file"), Text(given, "namespace"));
        return (file, space) switch
        {
            (null, null) => throw new ArgumentException("move takes file (a new file beside the type's) or namespace, as the window's Move to Another File and Move to Namespace do"),
            ({ }, { }) => throw new ArgumentException("move takes file or namespace, one at a time, as the window's Move does: move it to the namespace, then to the file, in two calls"),
            ({ } toFile, null) => Applied("move", await window.Assist.Actions.MoveTypeToFileAsync(document, offset, type, toFile, patience)),
            _ => Applied("move", await window.Assist.Actions.MoveTypeToNamespaceAsync(document, offset, type, space!, patience)),
        };
    }

    private async Task<RefactorAnswer> MembersAsync(OpenDocument document, LspDocument lsp, int offset, JsonElement? options, bool describing, bool up, Patience patience)
    {
        string kind = up ? "pull_members_up" : "push_members_down";
        Hierarchy hierarchy = await CSharp.CodeActions.DescribeAtAsync<Hierarchy>(lsp, offset, Handlers.DescribeHierarchy);
        if (CSharp.CodeActions.MembersMoveProblem(hierarchy, up) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
        if (describing)
        {
            object shown = up
                ? new { hierarchy.Type, hierarchy.Members, hierarchy.Bases, Options = new { Target = hierarchy.Bases[0].Text, Members = Array.Empty<string>(), Abstract = Array.Empty<string>() } }
                : new { hierarchy.Type, hierarchy.Members, hierarchy.Derived, Options = new { Targets = hierarchy.Derived.Select(d => d.Text), Members = Array.Empty<string>(), Abstract = Array.Empty<string>() } };
            return Described(kind, up ? $"Pull Members Up from {hierarchy.Type}" : $"Push Members Down from {hierarchy.Type}", shown);
        }
        JsonElement given = options!.Value;
        string target = up
            ? Text(given, "target") is { } baseType ? MemberId(hierarchy.Bases, baseType, $"base types of {hierarchy.Type}") : hierarchy.Bases[0].Id
            : string.Join("\n", Texts(given, "targets") is { } targets
                ? targets.Select(t => MemberId(hierarchy.Derived, t, $"classes derived from {hierarchy.Type}"))
                : hierarchy.Derived.Select(d => d.Id));
        string where = $"members of {hierarchy.Type}";
        IReadOnlyList<string> members = [.. (Texts(given, "members") ?? []).Select(m => MemberId(hierarchy.Members, m, where))];
        if (members.Count == 0)
        {
            throw new ArgumentException($"Name the members to {(up ? "pull up" : "push down")} (members); {hierarchy.Type} has {string.Join("; ", hierarchy.Members.Select(m => m.Text))}");
        }
        IReadOnlyList<string> made = [.. (Texts(given, "abstract") ?? []).Select(m => MemberId(hierarchy.Members, m, where))];
        if (made.FirstOrDefault(m => !members.Contains(m)) is { } stays)
        {
            throw new ArgumentException($"{hierarchy.Members.First(m => m.Id == stays).Text} is made abstract only as it moves: list it in members too");
        }
        return Applied(kind, await window.Assist.Actions.MoveMembersAsync(document, offset, hierarchy, up, target, members, made, patience));
    }

    private async Task<RefactorAnswer> SafeDeleteAsync(LspDocument lsp, int offset, JsonElement? options, bool describing, CancellationToken cancel)
    {
        (CSharp.CodeActions.Doomed? found, string? whyNot) = await window.Assist.Actions.FindDoomedAsync(lsp, offset);
        if (found is not { } doomed)
        {
            throw new InvalidOperationException(whyNot ?? "Nothing to delete here");
        }
        using (doomed)
        {
            IReadOnlyList<Place> uses = await Places(doomed.Uses, cancel);
            string what = $"the {doomed.Removal.Kind} '{doomed.Removal.Name}'";
            if (describing)
            {
                return new RefactorAnswer("safe_delete", $"Safe Delete would take {what} out of {Relative(doomed.Path)}, "
                    + (uses.Count == 0 ? "used nowhere else" : $"leaving {Plural.Of(uses.Count, "usage")} that would no longer compile"), [], [], Usages: uses);
            }
            bool anyway = options is { } given && given.TryGetProperty("deleteAnyway", out JsonElement value) && value.ValueKind == JsonValueKind.True;
            if (uses.Count > 0 && !anyway)
            {
                throw new InvalidOperationException($"Safe Delete left {what} where it is: it has {Plural.Of(uses.Count, "usage")} that would no longer compile "
                    + $"once it is deleted ({string.Join("; ", uses.Select(u => $"{u.Path}:{u.Line}: {u.Text}"))}). Fix them first, or give deleteAnyway true");
            }
            return Applied("safe_delete", window.Assist.Actions.Delete(doomed)) with { Usages = uses.Count > 0 ? uses : null };
        }
    }

    /// <summary>Introduce Field, Introduce Parameter or Encapsulate Field: the server's own action of that kind over the
    /// selection, the one named where it offers several, then the choice named of those nested under it.</summary>
    private async Task<RefactorAnswer> ServerRefactorAsync(string kind, OpenDocument document, LspDocument lsp, int offset, int? endLine, int? endColumn, JsonElement? options, bool describing, Patience patience)
    {
        CSharp.CodeActions.ServerRefactoring refactoring = kind switch
        {
            "introduce_field" => CSharp.CodeActions.IntroduceField,
            "introduce_parameter" => CSharp.CodeActions.IntroduceParameter,
            _ => CSharp.CodeActions.EncapsulateField,
        };
        int end = endLine is int l ? Offset(document, l, endColumn ?? 1) : offset;
        TextRange range = new(Math.Min(offset, end), Math.Abs(end - offset));
        using Asked asked = WorkspaceEdits.Track(Editors);
        IReadOnlyList<CodeAction> offered = await CSharp.CodeActions.OfferedAsync(lsp, range, refactoring);
        if (offered.Count == 0)
        {
            throw new InvalidOperationException($"{refactoring.Name} is not available here: {refactoring.Missing}");
        }
        if (describing)
        {
            return Described(kind, $"{refactoring.Name} here", new
            {
                Actions = offered.Select(a => new { a.Title, Choices = CSharp.CodeActions.ChoicesOf(a, refactoring).Select(c => c.Text) }),
            });
        }
        string? title = options is { } given ? Text(given, "action") : null;
        string? choice = options is { } chosenBy ? Text(chosenBy, "choice") : null;
        CodeAction action = title is null
            ? offered.Count == 1 ? offered[0] : throw new ArgumentException($"{refactoring.Name} offers {offered.Count} actions here; name one as action: {string.Join("; ", offered.Select(a => a.Title))}")
            : offered.FirstOrDefault(a => a.Title == title) ?? throw new ArgumentException($"{refactoring.Name} offers no '{title}' here; it offers {string.Join("; ", offered.Select(a => a.Title))}");
        IReadOnlyList<ActionChoice> all = ClientCommands.Choices(action);
        IReadOnlyList<ActionChoice> choices = CSharp.CodeActions.ChoicesOf(action, refactoring);
        if (all.Count > 0 && choices.Count == 0)
        {
            throw new InvalidOperationException($"{refactoring.Name} is not available here: the language server offers none of its kind");
        }
        (CodeAction chosen, string? scope) = (action, null);
        if (choices.Count > 0)
        {
            ActionChoice taken = choice is null
                ? choices.Count == 1 && refactoring.Keep is not null ? choices[0]
                    : throw new ArgumentException($"'{action.Title}' needs a choice: {string.Join("; ", choices.Select(c => c.Text))}")
                : choices.FirstOrDefault(c => c.Text == choice) ?? throw new ArgumentException($"'{action.Title}' offers no choice '{choice}'; it offers {string.Join("; ", choices.Select(c => c.Text))}");
            (chosen, scope) = (taken.Action, taken.Scope);
        }
        (RefactorOutcome made, _) = await window.Assist.Actions.ApplyAsync(document, chosen, scope, refactoring.Name, asked, patience);
        return Applied(kind, made.Said.Length > 0 || made.Problem is not null ? made : made with { Said = $"{refactoring.Name} done" });
    }

    /// <summary>Optimize Imports or Code Cleanup, as the window's command makes it, on a file or on each C# file of a
    /// project or of the solution: those open first, each as its own command makes it, then those not open, read without a
    /// tab, as one command that Undo takes back whole.</summary>
    private async Task<RefactorAnswer> TidyAsync(string kind, string? path, JsonElement? options, bool describe, CancellationToken cancel)
    {
        bool cleanup = kind == "code_cleanup";
        string name = cleanup ? "Code Cleanup" : "Optimize Imports";
        string scope = (options is { } given ? Text(given, "scope") : null)?.Trim().ToLowerInvariant()
            ?? (path is null ? "solution" : path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? "project" : "file");
        if (scope == "file")
        {
            if (path is null)
            {
                throw new ArgumentException($"{kind} on a file needs its path; give scope project or solution for more");
            }
            if (cleanup && Passenger.App.Xaml.XamlService.IsXaml(Resolve(path)))
            {
                // XAML's Code Cleanup is its reformatting, by the window's XAML code style.
                OpenDocument markup = await DocumentAsync(path, cancel);
                if (describe)
                {
                    return new RefactorAnswer(kind, $"Code Cleanup would reformat {Relative(markup.Path)}", [], []);
                }
                CSharp.CodeActions.Reformatted formatted = await FormatXamlAsync(markup, name);
                return new RefactorAnswer(kind, formatted.Said, formatted.Changed ? [new RefactorFile(Relative(markup.Path), "changed", [], formatted.Said)] : [], []);
            }
            (OpenDocument document, LspDocument lsp) = await CSharpOnly(path, kind, cancel);
            if (describe)
            {
                return new RefactorAnswer(kind, $"{name} would tidy {Relative(document.Path)}", [], []);
            }
            int before = document.Text.Version;
            string done = await TidyOne(lsp, document, cleanup);
            document.Save();
            return new RefactorAnswer(kind, $"{Relative(document.Path)}: {done}",
                document.Text.Version == before ? [] : [new RefactorFile(Relative(document.Path), "changed", [], done)], []);
        }
        if (scope is not ("project" or "solution"))
        {
            throw new ArgumentException($"No scope '{scope}'; there are file, project and solution");
        }
        IReadOnlyList<string> files = await CodeFilesAsync(scope, path, cancel);
        HashSet<string> named = new(files, StringComparer.Ordinal);
        if (describe)
        {
            return new RefactorAnswer(kind, $"{name} would tidy {Plural.Of(files.Count, "C# file")} of the {scope}",
                [.. files.Select(f => new RefactorFile(Relative(f), "unchanged", []))], []);
        }
        // Answers before the server has loaded the solution come from a file it knows alone, and are wrong.
        await When(() => Session.CSharp is not { } csharp || csharp.IsReady, ServerLimit, cancel, ServerState());
        CSharpService service = Session.CSharp is { IsReady: true } ready ? ready : throw new TimeoutException($"The language server has not loaded the workspace ({Session.CSharp?.State ?? "none"})");
        List<RefactorFile> tidied = [];
        List<string> left = [];
        // Those open are the person's, each with its own undo; the others are one command.
        foreach (OpenDocument open in Editors.Documents.Where(d => named.Contains(d.Path)).ToList())
        {
            cancel.ThrowIfCancellationRequested();
            await Tidied(open, service.For(open)?.Lsp);
        }
        using (Passenger.App.Editors.CommandHistory.Recording recording = Editors.Commands.Record(name))
        {
            HashSet<string> shown = new(Editors.Documents.Select(d => d.Path), StringComparer.Ordinal);
            foreach (string file in files.Where(f => !shown.Contains(f)))
            {
                cancel.ThrowIfCancellationRequested();
                recording.Watch(file);
                if (Editors.Borrow(file) is not ({ } borrowed, { } hold))
                {
                    left.Add($"{Relative(file)}: it cannot be read as text");
                    continue;
                }
                using (hold)
                {
                    await Tidied(borrowed, service.For(borrowed)?.Lsp);
                    if (borrowed.IsModified && !Editors.Save(borrowed))
                    {
                        left.Add($"{Relative(file)}: it could not be saved");
                    }
                }
            }
        }
        return new RefactorAnswer(kind, $"{name} changed {tidied.Count} of the {Plural.Of(files.Count, "C# file")} of the {scope}", tidied, left);

        async Task Tidied(OpenDocument document, LspDocument? lsp)
        {
            if (lsp is null)
            {
                left.Add($"{Relative(document.Path)}: the language server does not read it");
                return;
            }
            // Its text's version says whether the tidy changed it, never the text made into a string twice.
            int before = document.Text.Version;
            string done = await TidyOne(lsp, document, cleanup);
            if (document.Text.Version != before)
            {
                tidied.Add(new RefactorFile(Relative(document.Path), "changed", [], done));
            }
        }
    }

    /// <summary>One file's Optimize Imports or Code Cleanup, in the status bar's words.</summary>
    private async Task<string> TidyOne(LspDocument lsp, OpenDocument document, bool cleanup)
    {
        if (cleanup)
        {
            return CSharp.CodeActions.CleanedUp(await window.Assist.Actions.CleanUpAsync(lsp, document));
        }
        (bool removed, bool sorted) = await window.Assist.Actions.TidyUsings(lsp, document);
        return CSharp.CodeActions.Optimized(removed, sorted);
    }

    /// <summary>The C# files of a project (the one a path names or holds) or of the solution, as Find in Files' scopes
    /// have them: each project's own (<see cref="FolderWalk.ProjectFiles"/>), walked off the UI thread.</summary>
    private async Task<IReadOnlyList<string>> CodeFilesAsync(string scope, string? path, CancellationToken cancel)
    {
        IReadOnlyList<ProjectModel> projects = Session.Projects;
        if (projects.Count == 0)
        {
            throw new InvalidOperationException("The open folder has no projects");
        }
        IEnumerable<ProjectModel> chosen = projects;
        if (scope == "project")
        {
            string full = path is null ? throw new ArgumentException("A project scope needs path: the .csproj, or a file in it") : Resolve(path);
            chosen = [projects.FirstOrDefault(p => p.Path == full) ?? ProjectModel.Owning(projects, full)
                ?? throw new ArgumentException($"{Relative(full)} is in none of the solution's projects")];
        }
        List<ProjectModel> wanted = [.. chosen];
        return await BackgroundWork.Run(() =>
        {
            SortedSet<string> files = new(StringComparer.Ordinal);
            foreach (ProjectModel project in wanted)
            {
                cancel.ThrowIfCancellationRequested();
                files.UnionWith(FolderWalk.ProjectFiles(project, projects, ".cs"));
            }
            return (IReadOnlyList<string>)[.. files];
        }, cancel);
    }

    /// <summary>A refactoring's outcome as the agent reads it: refused with why, or each file it changed and what it left.</summary>
    private RefactorAnswer Applied(string kind, RefactorOutcome made) => made.Problem is { } problem
        ? throw new InvalidOperationException(problem)
        : new RefactorAnswer(kind, made.Said, [.. made.Files.Select(f => new RefactorFile(Relative(f.Path), f.Change, f.Lines, To: f.To is { } to ? Relative(to) : null))],
            [.. made.Left.Select(name => $"{name}: it was edited meanwhile, so it was left as it is")]);

    private static RefactorAnswer Described(string kind, string said, object dialog) =>
        new(kind, said + "; send options to apply it", [], [], JsonSerializer.SerializeToElement(dialog, DialogJson));

    /// <summary>The options as an object: given as JSON text by a client that sends objects as strings, read as the object.</summary>
    private static JsonElement? Options(JsonElement? options) => options switch
    {
        null or { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        { ValueKind: JsonValueKind.Object } given => given,
        { ValueKind: JsonValueKind.String } text when text.GetString() is { Length: > 0 } json && json.TrimStart().StartsWith('{') => JsonDocument.Parse(json).RootElement.Clone(),
        _ => throw new ArgumentException("options is one JSON object, as the tool's description gives for each kind", nameof(options)),
    };

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IReadOnlyList<string>? Texts(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.Array => [.. value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText())],
            JsonValueKind.String => [value.GetString()!],
            _ => throw new ArgumentException($"{name} is a list of names"),
        };
    }

    /// <summary>A member or type the agent named, by its id, by how it reads, or by its name where that is one of them.</summary>
    private static string MemberId(IReadOnlyList<Member> members, string given, string what)
    {
        string name = given.Trim();
        if (members.FirstOrDefault(m => m.Id == name || m.Text == name) is { } exact)
        {
            return exact.Id;
        }
        List<Member> named = [.. members.Where(m => NameOf(m.Text) == name || NameOf(m.Id.Split(':').Last().Split('.').Last()) == name)];
        return named.Count switch
        {
            1 => named[0].Id,
            0 => throw new ArgumentException($"'{given}' is none of the {what}: {string.Join("; ", members.Select(m => m.Text))}"),
            _ => throw new ArgumentException($"'{given}' names {named.Count} of the {what}; give the one meant as it reads: {string.Join("; ", named.Select(m => m.Text))}"),
        };

        static string NameOf(string text) => text.Split('(', ':', '<', '[')[0].Trim();
    }
}

/// <summary>What refactor answers: what it did or would do, each file it changed, what it left alone and why; when describing,
/// what the dialog shows with its options as they stand (dialog), and Safe Delete's usages.</summary>
public sealed record RefactorAnswer(string Kind, string Said, IReadOnlyList<RefactorFile> Files, IReadOnlyList<string> NotApplied,
    JsonElement? Dialog = null, IReadOnlyList<Place>? Usages = null);

/// <summary>A file a refactoring changed (created, changed, moved to <paramref name="To"/>, deleted), the lines its edits start
/// on, from one, in its text as it was, and for a cleanup what was done to it.</summary>
public sealed record RefactorFile(string Path, string Change, IReadOnlyList<int> Lines, string? Done = null, string? To = null);
