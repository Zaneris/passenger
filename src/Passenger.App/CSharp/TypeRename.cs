using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>A type's rename and the files named after it: a C# file is named after a type it declares when its name up
/// to the first dot is the type's (Foo.cs, Foo.Designer.cs), and the rename gives it the new name before that dot; a
/// file declaring the type under another name (Shapes.cs) keeps its own. The code's rename and the files' moves are one
/// command, which Undo takes back whole (<see cref="CommandHistory"/>).</summary>
public static class TypeRename
{
    /// <summary>The type whose name is at an offset of a file the server has open, and its declarations' files inside
    /// the folder that are named after it (the server's definitions of a partial type are all its parts); null when the
    /// name is not a type's declared in the folder.</summary>
    public static async Task<(string Type, IReadOnlyList<string> Files)?> NamedAsync(EditorArea editors, string folder, LspDocument server, int offset)
    {
        IReadOnlyList<LspLocation> definitions = await server.DefinitionAsync(offset);
        string? type = null;
        List<string> files = [];
        foreach (LspLocation place in definitions)
        {
            if (Local(folder, place) is not { } path || Declared(editors, path, place) is not { } declared)
            {
                continue;
            }
            type ??= declared;
            if (declared == type && IsNamedAfter(path, type) && !files.Contains(path))
            {
                files.Add(path);
            }
        }
        return type is null ? null : (type, files);
    }

    /// <summary>The type declared at a place the server gave in a file; null when none is.</summary>
    private static string? Declared(EditorArea editors, string path, LspLocation place) =>
        Syntax(editors, path) is { } parsed ? DeclaredAt(parsed.Root, ServerLines.Of(parsed.Text).OffsetOf(place.Range.Start)) : null;

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

    /// <summary>Applies a rename's edit and then moves the files it renames, as one command named for the person's undo,
    /// with what else the command does once they have moved (the projects following them). A file the moves would put
    /// where another file is stops the whole before anything changes, with why (<see cref="WorkspaceEditFailed"/>), as
    /// does the edit's own refusal. The number of files changed.</summary>
    public static int Apply(EditorArea editors, JsonElement edit, IReadOnlyList<(string From, string To)> moves, string name, Asked? asked, List<string> dropped,
        Action<CommandHistory.Recording>? moved = null)
    {
        foreach ((string from, string to) in moves)
        {
            if ((File.Exists(to) || Directory.Exists(to)) && !PathNames.Same(from, to))
            {
                throw new WorkspaceEditFailed($"{Path.GetFileName(to)} is already there");
            }
        }
        using CommandHistory.Recording recording = editors.Commands.Record(name);
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
    private static string? DeclaredAt(SyntaxNode root, int offset)
    {
        SyntaxToken token = root.FindToken(offset);
        return token.Parent switch
        {
            ConstructorDeclarationSyntax constructor when constructor.Identifier == token => token.ValueText,
            DestructorDeclarationSyntax finalizer when finalizer.Identifier == token => token.ValueText,
            { } declaration when Identifier(declaration) == token => token.ValueText,
            _ => null,
        };
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
        return (CSharpSyntaxTree.ParseText(file.Text).GetRoot(), TextSnapshot.From(file.Text));
    }

    private static bool IsCSharp(string path) => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
