using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Editors;
using Passenger.App.Xaml;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;
using Passenger.Xaml.Syntax;

namespace Passenger.App.CSharp;

/// <summary>A type's rename and the files named after it: a C# file is named after a type it declares when its name up
/// to the first dot is the type's (Foo.cs, Foo.Designer.cs), and the rename gives it the new name before that dot; a
/// file declaring the type under another name (Shapes.cs) keeps its own. The markup a code-behind's class is made with
/// follows it (<see cref="MarkupAsync"/>). The code's rename and the files' moves are one command, which Undo takes back
/// whole (<see cref="CommandHistory"/>).</summary>
public static class TypeRename
{
    /// <summary>A type a rename names: its name, the name an <c>x:Class</c> gives it where a namespace declares it (null
    /// for one nested in another type), a file declaring it inside the folder, and those of its declarations' files that
    /// are named after it.</summary>
    public sealed record Named(string Type, string? FullName, string Declaring, IReadOnlyList<string> Files);

    /// <summary>What a type's rename takes along of the markup its class is made with: the files moved with it, and the
    /// XAML file whose <c>x:Class</c> names it.</summary>
    public sealed record Markup(IReadOnlyList<(string From, string To)> Moves, string? ClassFile);

    /// <summary>The type whose name is at an offset of a file the server has open, and its declarations' files inside
    /// the folder that are named after it (the server's definitions of a partial type are all its parts); null when the
    /// name is not a type's declared in the folder.</summary>
    public static async Task<Named?> NamedAsync(EditorArea editors, string folder, LspDocument server, int offset)
    {
        // The open files as the question went: what is typed meanwhile moves the places the answer names in them.
        using Asked asked = WorkspaceEdits.Track(editors);
        IReadOnlyList<LspLocation> definitions = await server.DefinitionAsync(offset);
        string? type = null;
        string? fullName = null;
        string? declaring = null;
        List<string> files = [];
        foreach (LspLocation place in definitions)
        {
            if (Local(folder, place) is not { } path || Declared(editors, asked, path, place) is not { } declared)
            {
                continue;
            }
            if (type is null)
            {
                (type, fullName) = declared;
                declaring = path;
            }
            if (declared.Name == type && IsNamedAfter(path, type) && !files.Contains(path))
            {
                files.Add(path);
            }
        }
        return type is null || declaring is null ? null : new Named(type, fullName, declaring, files);
    }

    /// <summary>What a type's rename to <paramref name="name"/> takes along of its markup. A Razor component's class is
    /// named after its file, so the X.razor beside a code-behind X.razor.cs moves with the type, and the files named after
    /// it beside it (X.razor.css, X.razor.js) with it, whether or not the type's own files are renamed. The XAML file
    /// whose <c>x:Class</c> names the type (<see cref="ClassEdit"/>), moved with its files when named after it.</summary>
    public static async Task<Markup> MarkupAsync(XamlService? xaml, Named type, string name, bool renameFiles)
    {
        List<(string From, string To)> moves = [];
        foreach (string file in type.Files.Where(f => f.EndsWith(RazorCodeBehind, StringComparison.OrdinalIgnoreCase)))
        {
            string component = file[..^".cs".Length];
            if (!File.Exists(component))
            {
                continue;
            }
            moves.Add((component, Renamed(component, name)));
            string prefix = Path.GetFileName(component) + ".";
            try
            {
                moves.AddRange(Directory.EnumerateFiles(Path.GetDirectoryName(component)!)
                    .Where(f => Path.GetFileName(f).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !IsCSharp(f))
                    .Order(StringComparer.Ordinal)
                    .Select(f => (f, Renamed(f, name))));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The folder gone or unreadable: the component alone moves, as the rename's moves then say.
            }
        }
        string? classFile = type.FullName is { } full && xaml is not null ? await xaml.ClassFileAsync(full, type.Declaring) : null;
        if (renameFiles && classFile is not null && Stem(classFile) == type.Type)
        {
            moves.Add((classFile, Renamed(classFile, name)));
        }
        return new Markup(moves, classFile);
    }

    /// <summary>The edit a XAML file's <c>x:Class</c> takes as the type it names is renamed, in its text as given (the
    /// one a request was made for): its last part; null when the root names another class.</summary>
    public static (string Path, LspTextEdit Edit)? ClassEdit(string path, TextSnapshot? text, string fullName, string name)
    {
        if (text is null || XmlTree.Parse(text.ToString()).Root?.Attribute("x:Class") is not { IsValueClosed: true } declared
            || declared.Value != fullName || declared.RawValue != declared.Value)
        {
            return null;
        }
        int start = declared.ValueStart + declared.RawValue.LastIndexOf('.') + 1;
        return (path, new LspTextEdit(ServerLines.Of(text).RangeOf(TextRange.FromBounds(start, declared.ValueEnd)), name));
    }

    /// <summary>A workspace edit with one more file's edit after its own, in the shape it came in.</summary>
    public static JsonElement With(JsonElement edit, string path, LspTextEdit change)
    {
        JsonObject whole = JsonNode.Parse(edit.GetRawText()) as JsonObject ?? [];
        string uri = DocumentUris.FromPath(path);
        JsonNode added = JsonSerializer.SerializeToNode(change, Json.Options)!;
        if (whole["documentChanges"] is JsonArray documents)
        {
            documents.Add(new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = null }, ["edits"] = new JsonArray(added) });
        }
        else
        {
            if (whole["changes"] is not JsonObject byUri)
            {
                whole["changes"] = byUri = [];
            }
            if (byUri[uri] is JsonArray already)
            {
                already.Add(added);
            }
            else
            {
                byUri[uri] = new JsonArray(added);
            }
        }
        return JsonSerializer.SerializeToElement(whole, Json.Options);
    }

    /// <summary>The type declared at a place the server gave in a file, with its <c>x:Class</c> name; null when none is. A
    /// place in an open file is in its text as the question was <paramref name="asked"/>, read where what was typed since
    /// has taken it.</summary>
    private static (string Name, string? FullName)? Declared(EditorArea editors, Asked asked, string path, LspLocation place)
    {
        if (Syntax(editors, path) is not { } parsed)
        {
            return null;
        }
        int offset = editors.Find(path) is { } open && asked.Trackers.GetValueOrDefault(open) is { } since
            ? since.MapOffset(ServerLines.Of(since.Snapshot).OffsetOf(place.Range.Start))
            : ServerLines.Of(parsed.Text).OffsetOf(place.Range.Start);
        return DeclarationAt(parsed.Root, offset) is { } declaration && Identifier(declaration) is { } name
            ? (name.ValueText, FullName(declaration, name.ValueText))
            : null;
    }

    /// <summary>A place's file when it is one inside the folder, never a server's own (metadata, generated source).</summary>
    private static string? Local(string folder, LspLocation place) =>
        place.Uri.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && DocumentUris.ToPath(place.Uri) is { } path && PathNames.IsUnder(path, folder)
            ? path
            : null;

    /// <summary>The type a C# file is named after, where its name is in the file's text as it is now; null when it
    /// declares none of its name.</summary>
    public static (string Name, int Offset)? NamedAfterFile(EditorArea editors, string path)
    {
        if (!IsCSharp(path) || Syntax(editors, path) is not { } parsed)
        {
            return null;
        }
        string stem = Stem(path);
        foreach (SyntaxNode node in parsed.Root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax))
        {
            if (Identifier(node) is { } name && name.ValueText == stem)
            {
                return (stem, name.SpanStart);
            }
        }
        return null;
    }

    /// <summary>Whether a file is named after a type: a C# file whose name up to its first dot is the type's.</summary>
    public static bool IsNamedAfter(string path, string type) => IsCSharp(path) && Stem(path) == type;

    /// <summary>A file's name up to its first dot, the part a type's name stands in.</summary>
    public static string Stem(string path)
    {
        string name = Path.GetFileName(path);
        int dot = name.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>Where a file named after a type goes once the type is renamed: what follows its first dot kept.</summary>
    public static string Renamed(string path, string type)
    {
        string name = Path.GetFileName(path);
        return Path.Combine(Path.GetDirectoryName(path)!, type + name[Stem(path).Length..]);
    }

    /// <summary>Applies a rename's edit and then moves the files it renames, as one command named for the person's undo
    /// (or within the caller's, <paramref name="into"/>, which waits for its writes before it ends), with what else the
    /// command does once they have moved (the projects following them). A file the moves would put where another file is
    /// stops the whole before anything changes, with why (<see cref="WorkspaceEditFailed"/>), as does the edit's own
    /// refusal. The number of files changed.</summary>
    public static int Apply(EditorArea editors, JsonElement edit, IReadOnlyList<(string From, string To)> moves, string name, Asked? asked, List<string> dropped,
        Action<CommandHistory.Recording>? moved = null, CommandHistory.Recording? into = null)
    {
        foreach ((string from, string to) in moves)
        {
            if ((File.Exists(to) || Directory.Exists(to)) && !PathNames.Same(from, to))
            {
                throw new WorkspaceEditFailed($"{Path.GetFileName(to)} is already there");
            }
        }
        using CommandHistory.Recording? own = into is null ? editors.Commands.Record(name) : null;
        CommandHistory.Recording recording = into ?? own!;
        foreach (FileEdits change in Shapes.WorkspaceEdit(edit).Where(c => c.Operation is null))
        {
            recording.Watch(DocumentUris.ToPath(change.Uri));
        }
        int files = WorkspaceEdits.Apply(editors, edit, asked, dropped);
        foreach ((string from, string to) in moves.Where(m => m.From != m.To && File.Exists(m.From)))
        {
            if (editors.MoveFile(from, to) is { } failed)
            {
                throw new WorkspaceEditFailed($"{Path.GetFileName(from)} could not be renamed: {failed}");
            }
        }
        moved?.Invoke(recording);
        return files;
    }

    /// <summary>The type whose name is at an offset of a file's syntax: a class, struct, interface, record, enum or
    /// delegate declared there, or the type a constructor or finalizer named there makes.</summary>
    private static SyntaxNode? DeclarationAt(SyntaxNode root, int offset)
    {
        SyntaxToken token = root.FindToken(offset);
        return token.Parent switch
        {
            ConstructorDeclarationSyntax constructor when constructor.Identifier == token => constructor.Parent,
            DestructorDeclarationSyntax finalizer when finalizer.Identifier == token => finalizer.Parent,
            { } declaration when Identifier(declaration) == token => declaration,
            _ => null,
        };
    }

    /// <summary>A type's name with the namespaces it is declared in, as an <c>x:Class</c> names it; null for a type nested
    /// in another.</summary>
    private static string? FullName(SyntaxNode declaration, string name)
    {
        List<string> names = [name];
        for (SyntaxNode? at = declaration.Parent; at is not null; at = at.Parent)
        {
            if (at is BaseTypeDeclarationSyntax)
            {
                return null;
            }
            if (at is BaseNamespaceDeclarationSyntax space)
            {
                names.Insert(0, space.Name.ToString());
            }
        }
        return string.Join('.', names);
    }

    private static SyntaxToken? Identifier(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier,
        DelegateDeclarationSyntax type => type.Identifier,
        _ => null,
    };

    /// <summary>A C# file's syntax and the text it read: an open file's as it stands, else the disk's, parsed.</summary>
    private static (SyntaxNode Root, TextSnapshot Text)? Syntax(EditorArea editors, string path)
    {
        if (editors.Find(path) is { } open)
        {
            return open.Syntax?.Current is { } current ? (current.Root, current.Text) : null;
        }
        if (!IsCSharp(path) || !File.Exists(path) || FileText.Load(path) is not { } file)
        {
            return null;
        }
        return (CSharpSyntaxTree.ParseText(file.Text, editors.ParseOptions(path)).GetRoot(), TextSnapshot.From(file.Text));
    }

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>A Razor component's code-behind's ending, after the component's own file name.</summary>
    private const string RazorCodeBehind = ".razor.cs";
}
