using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Aspire;
using Passenger.App.Workspace;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>Aspire's help in C#, from Passenger's extension in the server, asked only in an AppHost's file or a service's
/// (<see cref="AspireRole"/>): any other file's completion, actions and declarations are the server's alone, and cost it
/// no round trip.</summary>
internal sealed partial class CSharpAssist
{
    /// <summary>The file shown's role, when it is a C# file of the server's.</summary>
    internal AspireRole AspireRoleOfShown => context.Editors.Active is { Syntax: not null } shown && context.CSharp?.For(shown) is { } features
        ? features.AspireRole : AspireRole.None;

    /// <summary>A resource's names where one is typed in an AppHost or a service: true once they are listed, or the list was
    /// asked for again meanwhile; false for the server's own list, also when the text changed while the extension answered.</summary>
    private async Task<bool> StartAspireCompletion(LspDocument lsp, CancellationToken cancel)
    {
        int caret = context.Caret;
        if (AspireRoleOfShown == AspireRole.None || context.Editors.Active is not { } document || !MayNameResource(caret))
        {
            return false;
        }
        int version = document.Text.Version;
        AspireHelp help;
        try
        {
            help = await lsp.ExtensionAsync<AspireHelp>(Handlers.AspireCodeHelp, new AspireHelpRequest(lsp.PlaceOf(caret)), cancel);
        }
        catch (LanguageServerFailed)
        {
            return false;
        }
        if (cancel.IsCancellationRequested)
        {
            return true;
        }
        // Typed on meanwhile: the server's list for the text as it stands, rather than none.
        if (context.Editors.Active != document || document.Text.Version != version
            || help.Names.Count == 0 || help.Start is not { } start || help.End is not { } end)
        {
            return false;
        }
        LspRange range = new(new LspPosition(start.Line, start.Character), new LspPosition(end.Line, end.Character));
        completion.Open(new CodeCompletion.Completion([.. help.Names.Select(name => new CompletionItem
        {
            Label = name,
            Kind = CompletionItemKind.Value,
            Detail = "Aspire resource",
            TextEdit = new LspTextEdit(range, System.Text.Json.JsonSerializer.Serialize(name)[1..^1]),
        })], lsp.OffsetOf(range.Start), [], false)
        {
            ResourceNames = true,
            Tail = document.Text.Snapshot.GetText(caret, Math.Max(0, lsp.OffsetOf(range.End) - caret)),
        });
        return true;
    }

    /// <summary>Whether the caret may be in a resource's name, which is a string literal's: as the parse of the text as it stands
    /// has it, or anywhere while the parse is behind.</summary>
    internal bool MayNameResource(int caret)
    {
        if (context.Syntax is not { } parsed || !ReferenceEquals(parsed.Text, context.Editor.Document.Snapshot))
        {
            return true;
        }
        SyntaxToken token = parsed.Root.FindToken(Math.Max(0, caret - 1));
        return token.IsKind(SyntaxKind.StringLiteralToken) && token.SpanStart < caret && caret <= token.Span.End;
    }

    /// <summary>The fixes of a service's Aspire warnings on an offset's line, Passenger's own (<see cref="EditorFixes"/>), listed
    /// before the server's: a WithReference added in the AppHost, worked out again by the extension as it is chosen, at the
    /// warning's place as it then stands, as one command of global undo; and a connection string read by a name no resource
    /// has changed to a declared name near it (<see cref="ConnectionFix"/>). An AppHost's: a parameter's value set.</summary>
    public IReadOnlyList<EditorFix> AspireFixes(OpenDocument file, int offset)
    {
        if (context.CSharp?.For(file) is not { Lsp: { } lsp } features)
        {
            return [];
        }
        if (features.AspireRole == AspireRole.AppHost)
        {
            return [.. features.AspireUnsetOn(lsp.PositionOf(offset).Line).DistinctBy(p => p.Name).Select(p => ParameterFix(file, p.Range, p.Name, p.Secret))];
        }
        if (features.AspireRole != AspireRole.Service)
        {
            return [];
        }
        int line = lsp.PositionOf(offset).Line;
        return [.. features.AspireUnknownOn(line).DistinctBy(u => u.Range).SelectMany(u => u.Near.Select(near => ConnectionFix(file, u.Range, u.Name, near))),
            .. features.AspireMissingOn(line).DistinctBy(m => (m.Service, m.Resource)).Select(m => Fix(m.Range, m.Service, m.Resource))];

        EditorFix Fix(TextRange range, string named, string resource) =>
            new($"Add WithReference({resource}) to {named}", "Aspire AppHost reference", range, string.Empty)
        {
            // Where its warning stands as it is chosen, while it does; the extension says when the reference is no longer missing.
            Again = _ => context.CSharp?.For(file)?.AspireMissingOn(null).FirstOrDefault(m => m.Service == named && m.Resource == resource) is { } now
                ? Fix(now.Range, named, resource) : Fix(range, named, resource),
            Elsewhere = (at, patience) => actions.MakeAsync("Add Aspire reference", file,
                current => current.ExtensionAsync<Refactored>(Handlers.AspireReferenceFix,
                    new AspireFixRequest(current.PlaceOf(at.Start), named, resource)),
                _ => $"Added WithReference({resource}) to {named}", patience),
        };
    }

    /// <summary>Change to a resource the AppHost declares near the name a service reads a connection string by, which
    /// no resource has (<see cref="Passenger.Core.Spelling.Near"/>): one edit of the name in its string, worked out
    /// again as it is chosen at its warning as it then stands, and refused once the name is no longer written
    /// there.</summary>
    private EditorFix ConnectionFix(OpenDocument file, TextRange range, string name, string near) =>
        new($"Change to {near}", "a resource the AppHost declares", range, near)
        {
            Again = now => context.CSharp?.For(file)?.AspireUnknownOn(null).Where(u => u.Name == name && u.Near.Contains(near))
                .OrderBy(u => Math.Abs(u.Range.Start - range.Start)).Select(u => u.Range).FirstOrDefault() is { Length: > 0 } current
                && current.End <= now.Length && now.GetText(current.Start, current.Length) == name
                ? ConnectionFix(file, current, name, near) : null,
        };

    /// <summary>The fix of an AppHost's warning that a parameter has no value in its configuration: its value asked for (masked
    /// for a secret) and set in the AppHost's user secrets for a secret, else in its appsettings.json, as one command of global
    /// undo; worked out again as it is chosen, refused once the parameter has a value or is gone.</summary>
    private EditorFix ParameterFix(OpenDocument file, TextRange range, string name, bool secret) =>
        new(secret ? $"Set {name} in the AppHost's user secrets…" : $"Set {name} in the AppHost's appsettings.json…", "Aspire AppHost parameter", range, string.Empty)
        {
            Again = _ => context.CSharp?.For(file)?.AspireUnsetOn(null).FirstOrDefault(p => p.Name == name) is { } now ? ParameterFix(file, now.Range, name, secret) : null,
            Asks = new ValueAsked(secret ? $"The value of {name}, kept in the AppHost's user secrets, out of the project's files"
                : $"The value of {name}, kept in the AppHost's appsettings.json", secret, (value, _) => Task.FromResult(SetParameter(file, name, secret, value))),
        };

    /// <summary>A parameter's value set in its AppHost's configuration (<see cref="AspireParameters.Setting"/>) as one command,
    /// a secret giving the AppHost a UserSecretsId first where it has none; its files' texts as the editor has them when open,
    /// the AppHost's warnings pulled again; what it says never holds the value.</summary>
    private RefactorOutcome SetParameter(OpenDocument file, string name, bool secret, string value)
    {
        if (context.CSharp is not { } csharp || csharp.AspireAppHostOf?.Invoke(file.Path) is not { } host)
        {
            return RefactorOutcome.Refused($"{name} was not set: {file.Name} is no longer an AppHost's");
        }
        Func<string, string?> text = context.Editors.Texts().Text;
        if (AspireParameters.HasValue(host, name, text))
        {
            return RefactorOutcome.Refused($"{name} was not set: the AppHost's configuration has a value for it already");
        }
        List<Passenger.Xaml.Features.XamlFileChange> changes = [];
        string path = host.Settings;
        if (secret)
        {
            (UserSecretsFile? secrets, string? unkept) = UserSecrets.Ensure(host.AppHost, host.UserSecretsId, text);
            if (secrets is null)
            {
                return RefactorOutcome.Refused($"{name} was not set: {unkept}");
            }
            path = secrets.File;
            if (secrets.Given is { } given)
            {
                changes.Add(given);
            }
        }
        (string? changed, string? problem) = AspireParameters.Setting(path, text(path), name, secret, value);
        if (changed is null)
        {
            return RefactorOutcome.Refused(problem ?? $"{name} was not set");
        }
        bool made = !File.Exists(path) && text(path) is null;
        changes.Add(made ? new(path, null, changed) : new(path, now =>
            AspireParameters.Setting(path, now, name, secret, value) is { Text: { } then } ? [TextEdit.Between(now, then)] : []));
        if (made)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        if (actions.ApplyFileChanges($"Set {name}", changes) is { } refused)
        {
            return RefactorOutcome.Refused(refused);
        }
        csharp.For(file)?.RefreshDiagnostics();
        string where = secret ? "the AppHost's user secrets" : $"the AppHost's {Path.GetFileName(path)}";
        List<RefactoredFile> files = [new RefactoredFile(path, made ? "created" : "changed", [])];
        if (changes.Count > 1)
        {
            files.Add(new RefactoredFile(host.AppHost, "changed", []));
        }
        return new RefactorOutcome($"Set {name} in {where}" + (changes.Count > 1 ? ", giving it a UserSecretsId" : string.Empty), null, files, []);
    }

    /// <summary>Rename's box for the resource whose name the caret is on in an AppHost's or a service's string, placed at the
    /// name; once a new one is typed, <see cref="RenameResourceAsync"/>. False where no resource's name is, or the text moved
    /// on as the extension answered, for the server's rename.</summary>
    private async Task<bool> ShowAspireRename(OpenDocument document, int caret)
    {
        LspDocument lsp = context.Now(document);
        int version = document.Text.Version;
        AspireRenaming described;
        try
        {
            described = await lsp.ExtensionAsync<AspireRenaming>(Handlers.AspireRename, new AspireRenameRequest(lsp.PlaceOf(caret)));
        }
        catch (LanguageServerFailed)
        {
            return false;
        }
        if (described is not { Name: { } old, Start: { } start } || context.Editors.Active != document || document.Text.Version != version)
        {
            return false;
        }
        int at = lsp.OffsetOf(new LspPosition(start.Line, start.Character));
        RenameBox box = new(old);
        ChangeTracker opened = new(context.Editor.Document);
        popups.ShowModal(box, at, async name =>
        {
            int now = opened.MapOffset(at);
            if (name is null || name == old)
            {
                return;
            }
            RefactorOutcome made = await RenameResourceAsync(document, now, old, name, null);
            context.Say(made.Problem ?? made.Said);
        });
        popups.WhenClosed(opened.Dispose);
        return true;
    }

    /// <summary>The name of the resource whose name is at an offset of an AppHost's or a service's file; null where none is.</summary>
    internal static async Task<string?> AspireResourceAtAsync(LspDocument lsp, int offset)
    {
        try
        {
            return (await lsp.ExtensionAsync<AspireRenaming>(Handlers.AspireRename, new AspireRenameRequest(lsp.PlaceOf(offset)))).Name;
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
    }

    /// <summary>A resource renamed from its name at an offset: where its AppHost declares it and wherever its services read it,
    /// worked out again by the extension as it is made, one command of global undo; shared with the agents' rename.</summary>
    internal Task<RefactorOutcome> RenameResourceAsync(OpenDocument document, int offset, string old, string name, Patience? patience) =>
        actions.MakeAsync($"Rename {old} to {name}", document,
            async current => (await current.ExtensionAsync<AspireRenaming>(Handlers.AspireRename, new AspireRenameRequest(current.PlaceOf(offset), name))).Made
                ?? new Refactored($"{old} was not renamed: no resource is named there any longer", []),
            files => $"Renamed {old} to {name} in {Passenger.Core.Plural.Of(files, "file")}", patience);

    /// <summary>Aspire's Go to Declaration, in an AppHost's or a service's file: AddProject's project file, not its generated
    /// metadata type, and from a service's connection string or service-discovery name the AppHost's line that declares the
    /// resource; false elsewhere, for the server's own.</summary>
    private async Task<bool> AspireGoTo(LspDocument document, int offset)
    {
        if (AspireRoleOfShown == AspireRole.None || await AspireDeclarationAsync(document, offset, ServerPlaces.Texts(context.Editors)) is not { } place)
        {
            return false;
        }
        context.Navigate(place);
        return true;
    }

    /// <summary>Where Aspire's Go to Declaration goes from an offset of an AppHost's or a service's file, at the editor's line of
    /// the file it lands in; null where it finds nothing, or without Passenger's extension. Shared with the agents' definition.</summary>
    internal static async Task<(string Path, TextPosition Position)?> AspireDeclarationAsync(LspDocument document, int offset, Func<string, TextSnapshot?> texts)
    {
        AspireProjectTarget target;
        try
        {
            target = await document.ExtensionAsync<AspireProjectTarget>(Handlers.AspireProjectDeclaration, new Caret(document.PlaceOf(offset)));
        }
        catch (LanguageServerFailed)
        {
            // Ordinary declaration navigation still works without Passenger's extension.
            return null;
        }
        if (target.Path is not { } found || !File.Exists(found))
        {
            return null;
        }
        string path = Passenger.Core.PathNames.Canonical(found);
        return (path, target.At is { } at ? ServerPlaces.PositionOf(texts(path), new LspPosition(at.Line, at.Character)) : new TextPosition(0, 0));
    }
}
