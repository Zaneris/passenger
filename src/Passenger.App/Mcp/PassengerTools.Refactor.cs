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
        "left alone.\nIn a GDScript: extract_variable, extract_function, introduce_parameter ({name}, over a selection), inline, " +
        "safe_delete, change_signature of a function or signal ({name, parameters: [{from, name?, type?, defaultValue?} | " +
        "{name, type?, defaultValue?, callValue}]}) and move ({script}), scenes' connections and C# followed, refused naming any " +
        "use it can't see.")]
    public Task<RefactorAnswer> Refactor(
        string kind,
        string? path = null,
        int? line = null,
        int? column = null,
        int? endLine = null,
        int? endColumn = null,
        JsonElement? options = null,
        bool describe = false,
        CancellationToken cancel = default) => OnUi(cancel, () => Patience.RunAsync(Editors.Commands, () => Session.CSharp?.Session, async patience =>
    {
        string named = kind.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
        JsonElement? given = Options(options);
        using AgentsChange naming = window.NameAgentsChange(Refactoring(named, null));
        if (path is not null && Godot.GodotScriptTypes.IsScript(Resolve(path)))
        {
            if (line is { } scriptLine && column is { } scriptColumn)
            {
                OpenDocument script = await DocumentAsync(path, cancel);
                naming.Names(Refactoring(named, NameAt(script, Offset(script, scriptLine, scriptColumn))));
            }
            RefactorAnswer scripted = await GDScriptRefactorAsync(named, path, line, column, endLine, endColumn, given, describe, cancel);
            if (scripted.Files.Count > 0)
            {
                await SaveLandedAsync(cancel);
            }
            return scripted;
        }
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
        naming.Names(Refactoring(named, NameAt(document, offset)));
        bool describing = describe || (given is null && named is "change_signature" or "move" or "extract_interface" or "pull_members_up" or "push_members_down");
        RefactorAnswer answer = named switch
        {
            "change_signature" => await ChangeSignatureAsync(document, lsp, offset, given, describing, patience),
            "extract_interface" => await ExtractInterfaceAsync(document, lsp, offset, given, describing, patience),
            "move" => await MoveAsync(document, lsp, offset, given, describing, patience),
            "pull_members_up" => await MembersAsync(document, lsp, offset, given, describing, up: true, patience),
            "push_members_down" => await MembersAsync(document, lsp, offset, given, describing, up: false, patience),
            "safe_delete" => await SafeDeleteAsync(document, offset, given, describing, cancel),
            _ => await ServerRefactorAsync(named, document, lsp, offset, endLine, endColumn, given, describing, patience),
        };
        if (answer.Files.Count > 0)
        {
            await SaveLandedAsync(cancel);
        }
        return answer;
    }, AgentCall.Say, cancel));

    /// <summary>What Local History names an agent's refactoring of a kind, as the window names the refactoring, with the
    /// name it works on where one is at its place.</summary>
    private static string Refactoring(string kind, string? subject)
    {
        (string title, string? on) = kind switch
        {
            "change_signature" => ("Change Signature", " of "),
            "safe_delete" => ("Safe Delete", " "),
            "move" => ("Move", " "),
            "extract_interface" => ("Extract Interface", " from "),
            "pull_members_up" => ("Pull Members Up", " from "),
            "push_members_down" => ("Push Members Down", " from "),
            "encapsulate_field" => ("Encapsulate Field", " "),
            "inline" or "inline_variable" or "inline_function" => ("Inline", " "),
            "introduce_field" => ("Introduce Field", null),
            "introduce_parameter" => ("Introduce Parameter", null),
            "extract_variable" or "introduce_variable" => ("Extract Variable", null),
            "extract_function" or "extract_method" => ("Extract Function", null),
            "optimize_imports" => ("Optimize Imports", null),
            "code_cleanup" => ("Code Cleanup", null),
            _ => (kind, null),
        };
        return on is not null && subject is not null ? title + on + subject : title;
    }

    /// <summary>The name written at an offset of a file, as Rename takes it there, or null where none is.</summary>
    private string? NameAt(OpenDocument document, int offset)
    {
        TextSnapshot text = document.Text.Snapshot;
        string name = text.GetText(window.Assist.Actions.RenamedAt(document, text, offset));
        return name.Any(char.IsLetterOrDigit) ? name : null;
    }

    /// <summary>A GDScript refactoring for an agent, through the window's own (<see cref="CSharp.CodeActions.GDScriptRefactorAsync"/>):
    /// at path, line and column, over a selection to endLine and endColumn for those that take one, with the name or the
    /// signature options give; Change Signature without options, or any with describe, answers the function's signature.</summary>
    private async Task<RefactorAnswer> GDScriptRefactorAsync(string kind, string path, int? line, int? column, int? endLine, int? endColumn, JsonElement? given, bool describe,
        CancellationToken cancel)
    {
        CSharp.GDScriptRefactoring refactoring = kind switch
        {
            "extract_variable" or "introduce_variable" => CSharp.GDScriptRefactoring.ExtractVariable,
            "extract_function" or "extract_method" => CSharp.GDScriptRefactoring.ExtractFunction,
            "introduce_parameter" => CSharp.GDScriptRefactoring.IntroduceParameter,
            "inline" or "inline_variable" or "inline_function" => CSharp.GDScriptRefactoring.Inline,
            "safe_delete" => CSharp.GDScriptRefactoring.SafeDelete,
            "change_signature" => CSharp.GDScriptRefactoring.ChangeSignature,
            "move" => CSharp.GDScriptRefactoring.Move,
            _ => throw new ArgumentException($"No GDScript refactoring '{kind}'; there are extract_variable, extract_function, introduce_parameter, inline, safe_delete, change_signature and move",
                nameof(kind)),
        };
        if (line is null || column is null)
        {
            throw new ArgumentException($"{kind} needs the place it works on: line and column", nameof(line));
        }
        OpenDocument document = await DocumentAsync(path, cancel);
        int start = Offset(document, line.Value, column.Value);
        int end = endLine is { } toLine && endColumn is { } toColumn ? Offset(document, toLine, toColumn) : start;
        TextRange selection = TextRange.FromBounds(Math.Min(start, end), Math.Max(start, end));
        CSharp.CodeActions actions = window.Assist.Actions;
        if (refactoring == CSharp.GDScriptRefactoring.ChangeSignature && (describe || given is null))
        {
            (Signature? signature, string? why) = await actions.GDScriptSignatureAsync(document, start);
            if (signature is null)
            {
                throw new InvalidOperationException($"Change Signature is not available here: {why}");
            }
            return Described(kind, $"Change Signature of {signature.Name} in {signature.Container}", new
            {
                signature.Name,
                signature.ReturnType,
                Parameters = signature.Parameters.Select(p => new { p.Name, p.Type, p.DefaultValue }),
                Options = new { signature.Name, Parameters = signature.Parameters.Select(p => new { From = p.Name }) },
            });
        }
        if (describe)
        {
            return Described(kind, $"{kind} at {Relative(document.Path)}:{line}:{column}", new { Options = new { Name = refactoring == CSharp.GDScriptRefactoring.ExtractFunction ? "new_function" : "value" } });
        }
        string? name = given is { } options ? JsonMembers.Text(options, refactoring == CSharp.GDScriptRefactoring.Move ? "script" : "name") : null;
        if (refactoring == CSharp.GDScriptRefactoring.Move)
        {
            string script = name ?? throw new ArgumentException("move needs options {script}: the script it goes to, as res:// or the folder's path");
            name = script.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) ? script : GodotResPaths.Scheme + script.Replace('\\', '/').TrimStart('/');
        }
        if (name is null && refactoring is CSharp.GDScriptRefactoring.ExtractVariable or CSharp.GDScriptRefactoring.ExtractFunction or CSharp.GDScriptRefactoring.IntroduceParameter)
        {
            throw new ArgumentException($"{kind} needs options {{name}}: what it makes is called");
        }
        IReadOnlyList<Passenger.Workspace.GDScript.GDScriptParameterChange>? parameters = null;
        if (refactoring == CSharp.GDScriptRefactoring.ChangeSignature && given is { } signatureOptions)
        {
            (Signature? signature, string? why) = await actions.GDScriptSignatureAsync(document, start);
            Signature had = signature ?? throw new InvalidOperationException($"Change Signature is not available here: {why}");
            name ??= had.Name;
            parameters = JsonMembers.Value(signatureOptions, "parameters") is { ValueKind: JsonValueKind.Array } asked
                ? [.. asked.EnumerateArray().Select(p => GDScriptParameterOf(p, had))]
                : [.. had.Parameters.Select((p, i) => new Passenger.Workspace.GDScript.GDScriptParameterChange(i, p.Name, p.Type, p.DefaultValue, string.Empty))];
        }
        return Applied(kind, await actions.GDScriptRefactorAsync(document, refactoring, selection, name, parameters));
    }

    /// <summary>A GDScript parameter as an agent asks for it: one kept by its old name (from), else a new one with what calls pass.</summary>
    private static Passenger.Workspace.GDScript.GDScriptParameterChange GDScriptParameterOf(JsonElement parameter, Signature had)
    {
        string? from = parameter.ValueKind == JsonValueKind.String ? parameter.GetString() : JsonMembers.Text(parameter, "from");
        if (from is not null)
        {
            int was = had.Parameters.ToList().FindIndex(p => p.Name == from);
            if (was < 0)
            {
                throw new ArgumentException($"{had.Name} has no parameter '{from}'; it has {string.Join(", ", had.Parameters.Select(p => p.Name))}");
            }
            Parameter old = had.Parameters[was];
            return new(was, JsonMembers.Text(parameter, "name") ?? old.Name, JsonMembers.Text(parameter, "type") ?? old.Type, JsonMembers.Text(parameter, "defaultValue") ?? old.DefaultValue, string.Empty);
        }
        string made = JsonMembers.Text(parameter, "name") ?? throw new ArgumentException("A new parameter needs a name (and from, for one that was there: its old name)");
        return new(-1, made, JsonMembers.Text(parameter, "type") ?? string.Empty, JsonMembers.Text(parameter, "defaultValue") ?? string.Empty, JsonMembers.Text(parameter, "callValue") ?? string.Empty);
    }

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
            ShaderServed => throw Lacks(served, Titled(kind), ShaderHas),
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
        string name = JsonMembers.Text(given, "name") ?? signature.Name;
        string returnType = JsonMembers.Text(given, "returnType") ?? (method ? signature.ReturnType : string.Empty);
        if (!method && (name != signature.Name || returnType.Length > 0))
        {
            throw new ArgumentException($"A constructor's name is its type's and it has no return type: change only {signature.Container}'s parameters");
        }
        List<ParameterChange> parameters = [];
        if (JsonMembers.Value(given, "parameters") is not { ValueKind: not JsonValueKind.Null } asked)
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
                string? from = parameter.ValueKind == JsonValueKind.String ? parameter.GetString() : JsonMembers.Text(parameter, "from");
                if (from is not null)
                {
                    int was = signature.Parameters.ToList().FindIndex(p => p.Name == from);
                    if (was < 0)
                    {
                        throw new ArgumentException($"{signature.Name} has no parameter '{from}'; it has {string.Join(", ", signature.Parameters.Select(p => p.Name))}");
                    }
                    Parameter old = signature.Parameters[was];
                    parameters.Add(new ParameterChange(was, JsonMembers.Text(parameter, "modifier") ?? old.Modifier, JsonMembers.Text(parameter, "type") ?? old.Type,
                        JsonMembers.Text(parameter, "name") ?? old.Name, JsonMembers.Text(parameter, "defaultValue") ?? old.DefaultValue, string.Empty));
                }
                else
                {
                    string type = JsonMembers.Text(parameter, "type") ?? throw new ArgumentException("A new parameter needs its type (and from, for one that was there: its old name)");
                    string made = JsonMembers.Text(parameter, "name") ?? throw new ArgumentException($"The new {type} parameter needs a name");
                    parameters.Add(new ParameterChange(-1, JsonMembers.Text(parameter, "modifier") ?? string.Empty, type, made, JsonMembers.Text(parameter, "defaultValue") ?? string.Empty,
                        JsonMembers.Text(parameter, "callValue") ?? string.Empty));
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
        string name = JsonMembers.Text(given, "name") ?? source.Name;
        // The file follows the name until it is given, as the dialog's does until it is typed in.
        string file = JsonMembers.Text(given, "file") ?? (JsonMembers.Text(given, "name") is { } typed ? $"{typed.Trim()}.cs" : source.FileName);
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
                Options = new { File = type.TargetFileName, type.Namespace },
            });
        }
        JsonElement given = options!.Value;
        (string? file, string? space) = (JsonMembers.Text(given, "file"), JsonMembers.Text(given, "namespace"));
        // Each as described is left as it is: the one changed is the move, and the described file's where neither is.
        StringComparer names = PathNames.NamesIn(Path.GetDirectoryName(document.Path)!);
        bool toFile = file is not null && !names.Equals(file, type.TargetFileName);
        bool toSpace = space is not null && space != type.Namespace;
        if (toFile && toSpace)
        {
            throw new ArgumentException("move takes file or namespace, one at a time, as the window's Move does: move it to the namespace, then to the file, in two calls");
        }
        if (toSpace)
        {
            return Applied("move", await window.Assist.Actions.MoveTypeToNamespaceAsync(document, offset, type, space!, patience));
        }
        if (file is not null && names.Equals(file, type.FileName))
        {
            throw new ArgumentException($"'{type.Name}' is in {type.FileName} already: move takes the file it goes to, or namespace");
        }
        if (file is not null)
        {
            return Applied("move", await window.Assist.Actions.MoveTypeToFileAsync(document, offset, type, file, patience));
        }
        throw new ArgumentException(space is null
            ? "move takes file (a new file beside the type's) or namespace, as the window's Move to Another File and Move to Namespace do"
            : $"'{type.Name}' is in namespace '{space}' already: move takes the namespace it goes to, or file");
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
            ? JsonMembers.Text(given, "target") is { } baseType ? MemberId(hierarchy.Bases, baseType, $"base types of {hierarchy.Type}") : hierarchy.Bases[0].Id
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

    private async Task<RefactorAnswer> SafeDeleteAsync(OpenDocument document, int offset, JsonElement? options, bool describing, CancellationToken cancel)
    {
        (CSharp.CodeActions.Doomed? found, string? whyNot) = await window.Assist.Actions.FindDoomedAsync(document, offset);
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
            bool anyway = JsonMembers.Bool(options, "deleteAnyway") is true;
            if (uses.Count > 0 && !anyway)
            {
                throw new InvalidOperationException($"Safe Delete left {what} where it is: it has {Plural.Of(uses.Count, "usage")} that would no longer compile "
                    + $"once it is deleted ({string.Join("; ", uses.Select(u => $"{u.Path}:{u.Line}: {u.Text}"))}). Fix them first, or give options {{\"deleteAnyway\": true}}");
            }
            return Applied("safe_delete", await window.Assist.Actions.DeleteAsync(doomed)) with { Usages = uses.Count > 0 ? uses : null };
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
        string? title = JsonMembers.Text(options, "action");
        string? choice = JsonMembers.Text(options, "choice");
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
        string scope = JsonMembers.Text(options, "scope")?.Trim().ToLowerInvariant()
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
            await LandedAsync(cancel);
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
        using (Passenger.App.Editors.CommandHistory.Recording recording = Editors.Commands.RecordWrites(name))
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
            foreach ((string unsaved, Exception refused) in await recording.LandedAsync(null))
            {
                tidied.RemoveAll(t => t.Path == Relative(unsaved));
                left.Add($"{Relative(unsaved)}: it could not be saved: {refused.Message}");
            }
        }
        await LandedAsync(cancel);
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
                files.UnionWith(FolderWalk.ProjectFiles(project, ".cs"));
            }
            return (IReadOnlyList<string>)[.. files];
        }, cancel);
    }

    /// <summary>A refactoring's outcome as the agent reads it: refused with why, or each file it changed and what it left.</summary>
    private RefactorAnswer Applied(string kind, RefactorOutcome made) => made.Problem is { } problem
        ? throw new InvalidOperationException(problem)
        : new RefactorAnswer(kind, made.Said, [.. made.Files.Select(f => new RefactorFile(Relative(f.Path), f.Change, f.Lines, To: f.To is { } to ? Relative(to) : null))],
            [.. made.Left.Select(name => $"{name}: it was edited meanwhile, so it was left as it is"),
                .. made.Unchanged.Select(use => $"{Relative(use.Path)}:{use.Line}:{use.Column}: {use.Why}, so it was left as it is")]);

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

    private static IReadOnlyList<string>? Texts(JsonElement element, string name)
    {
        if (JsonMembers.Value(element, name) is not { ValueKind: not JsonValueKind.Null } value)
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
