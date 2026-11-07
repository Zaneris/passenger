using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.App.Editors;
using Passenger.Core;
using Passenger.Languages.CSharp;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.CSharp;

/// <summary>Where a symbol is declared in the source of a project the open folder has but C# does not load (a solution
/// filter leaves it out, or the person unloaded it), which the server shows only as its assembly's metadata stub: the stub's
/// declaration at the place, read by its syntax, found in that project's C# files by its namespace, containing types,
/// kind, name and parameters' types. A stub of any other assembly, or one no source declares so, stays as it was.</summary>
internal static partial class UnloadedSources
{
    /// <summary>The places, each metadata stub's of an unloaded project's assembly given as its source's declarations, and
    /// whether one of them is only of that name, the signature the stub shows being in no source.</summary>
    public static async Task<(IReadOnlyList<LspLocation> Places, bool OnlyByName)> OfAsync(IReadOnlyList<LspLocation> found, IReadOnlyList<ProjectModel> unloaded)
    {
        if (unloaded.Count == 0 || !found.Any(l => IsStub(DocumentUris.ToPath(l.Uri))))
        {
            return (found, false);
        }
        return await BackgroundWork.Run(() =>
        {
            List<LspLocation> places = [];
            bool onlyByName = false;
            foreach (LspLocation location in found)
            {
                (IReadOnlyList<LspLocation> sources, bool named) = SourcesOf(location, unloaded);
                places.AddRange(sources.Count > 0 ? sources : [location]);
                onlyByName |= named;
            }
            return ((IReadOnlyList<LspLocation>)places, onlyByName);
        });
    }

    /// <summary>Whether a file is the server's metadata stub of an assembly.</summary>
    private static bool IsStub(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}MetadataAsSource{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    /// <summary>A stub's place given as the declarations in its unloaded project's source, and whether they are only of its
    /// name; none where it is no such stub's.</summary>
    private static (IReadOnlyList<LspLocation> Sources, bool OnlyByName) SourcesOf(LspLocation location, IReadOnlyList<ProjectModel> unloaded)
    {
        string stub = DocumentUris.ToPath(location.Uri);
        if (!IsStub(stub) || EditorArea.ReadText(stub) is not { } text || AssemblyLine().Match(text) is not { Success: true } assembly)
        {
            return ([], false);
        }
        string name = assembly.Groups[1].Value;
        if (unloaded.FirstOrDefault(p => string.Equals(p.Evaluated?.AssemblyName ?? Path.GetFileNameWithoutExtension(p.Path), name, StringComparison.Ordinal)) is not { } project)
        {
            return ([], false);
        }
        SyntaxTree tree = CSharpParsing.Parse(text, CSharpParsing.Default);
        int position = tree.GetText().Lines.GetPosition(new Microsoft.CodeAnalysis.Text.LinePosition(location.Range.Start.Line, location.Range.Start.Character));
        if (Declared(tree.GetRoot().FindToken(position).Parent) is not { } wanted)
        {
            return ([], false);
        }
        CSharpParseOptions options = CompilationSymbols.Of(project);
        // Its overloads by name where none takes these parameters: the assembly the server read may be older than its source.
        List<LspLocation> sources = [];
        List<LspLocation> named = [];
        foreach (string file in FilesOf(project))
        {
            if (EditorArea.ReadText(file) is not { } source || !source.Contains(wanted.Key.Name.TrimStart('.'), StringComparison.Ordinal) && wanted.Key.Kind != Kind.Constructor)
            {
                continue;
            }
            SyntaxTree read = CSharpParsing.Parse(source, options, file);
            foreach (SyntaxNode node in read.GetRoot().DescendantNodes())
            {
                if (DeclaredHere(node) is not { } declared || declared.Key with { Parameters = string.Empty } != wanted.Key with { Parameters = string.Empty })
                {
                    continue;
                }
                FileLinePositionSpan span = read.GetLineSpan(declared.Name.Span);
                LspLocation place = new(DocumentUris.FromPath(file), new LspRange(
                    new LspPosition(span.StartLinePosition.Line, span.StartLinePosition.Character), new LspPosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
                (declared.Key == wanted.Key ? sources : named).Add(place);
            }
        }
        return sources.Count > 0 ? (sources, false) : (named, named.Count > 0);
    }

    /// <summary>The project's C# files: its evaluation's compile items, else those in its own folders.</summary>
    private static IEnumerable<string> FilesOf(ProjectModel project) =>
        project.Evaluated?.Items is { Count: > 0 } items
            ? items.Where(i => i.Type == "Compile" && i.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Select(i => i.Path).Distinct(PathNames.Comparer)
            : ProjectFolders.Under(project.Folder).SelectMany(f => Directory.EnumerateFiles(f, "*.cs"));

    private enum Kind
    {
        Type,
        Method,
        Constructor,
        Property,
        Indexer,
        Event,
        Field,
        Operator,
    }

    /// <summary>What names a declaration across its stub and its source: its kind, its name, where it is declared and what
    /// tells its overloads apart (<see cref="ParameterTypes"/>, a generic's type parameters' count before them, a type's
    /// alone; empty where it takes none).</summary>
    private readonly record struct Key(Kind Kind, string Name, string Container, string Parameters);

    /// <summary>The declaration a node is, or is inside the name of, with the token it is named by; null for none.</summary>
    private static (Key Key, SyntaxToken Name)? Declared(SyntaxNode? node)
    {
        for (SyntaxNode? at = node; at is not null; at = at.Parent)
        {
            if (DeclaredHere(at) is { } declared)
            {
                return declared;
            }
            if (at is StatementSyntax or BlockSyntax or ArrowExpressionClauseSyntax or AccessorListSyntax)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The declaration a node is itself, else null; a type with a primary constructor is the constructor's too, as
    /// its stub declares one.</summary>
    private static (Key Key, SyntaxToken Name)? DeclaredHere(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => (new Key(Kind.Type, type.Identifier.Text, Container(type), Generic((type as TypeDeclarationSyntax)?.TypeParameterList)), type.Identifier),
        DelegateDeclarationSyntax made => (new Key(Kind.Type, made.Identifier.Text, Container(made), Generic(made.TypeParameterList)), made.Identifier),
        MethodDeclarationSyntax method => (new Key(Kind.Method, method.Identifier.Text, Container(method), Generic(method.TypeParameterList) + ParameterTypes.Of(method.ParameterList)), method.Identifier),
        ConstructorDeclarationSyntax made => (new Key(Kind.Constructor, made.Modifiers.Any(SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor", Container(made), ParameterTypes.Of(made.ParameterList)), made.Identifier),
        PropertyDeclarationSyntax property => (new Key(Kind.Property, property.Identifier.Text, Container(property), string.Empty), property.Identifier),
        IndexerDeclarationSyntax indexer => (new Key(Kind.Indexer, "this", Container(indexer), ParameterTypes.Of(indexer.ParameterList)), indexer.ThisKeyword),
        EventDeclarationSyntax raised => (new Key(Kind.Event, raised.Identifier.Text, Container(raised), string.Empty), raised.Identifier),
        VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax raised } variable => (new Key(Kind.Event, variable.Identifier.Text, Container(raised), string.Empty), variable.Identifier),
        VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } variable => (new Key(Kind.Field, variable.Identifier.Text, Container(field), string.Empty), variable.Identifier),
        EnumMemberDeclarationSyntax member => (new Key(Kind.Field, member.Identifier.Text, Container(member), string.Empty), member.Identifier),
        OperatorDeclarationSyntax operation => (new Key(Kind.Operator, operation.OperatorToken.Text, Container(operation), ParameterTypes.Of(operation.ParameterList)), operation.OperatorToken),
        ConversionOperatorDeclarationSyntax conversion => (new Key(Kind.Operator, conversion.ImplicitOrExplicitKeyword.Text, Container(conversion), $"{ParameterTypes.Named(conversion.Type)}{ParameterTypes.Of(conversion.ParameterList)}"), conversion.ImplicitOrExplicitKeyword),
        ParameterListSyntax { Parent: TypeDeclarationSyntax primary } list => (new Key(Kind.Constructor, ".ctor", Container(list), ParameterTypes.Of(list)), primary.Identifier),
        _ => null,
    };

    /// <summary>A generic's type parameters' count, as a type's or a method's overloads differ by it.</summary>
    private static string Generic(TypeParameterListSyntax? list) => list is null ? string.Empty : $"`{list.Parameters.Count}";

    /// <summary>Where a declaration is declared: its namespaces and types, outermost first, a type by its name and type
    /// parameters' count, as a stub and a source both write them.</summary>
    private static string Container(SyntaxNode node)
    {
        List<string> names = [];
        for (SyntaxNode? at = node.Parent; at is not null; at = at.Parent)
        {
            switch (at)
            {
                case TypeDeclarationSyntax type:
                    names.Add($"{type.Identifier.Text}`{type.TypeParameterList?.Parameters.Count ?? 0}");
                    break;
                case BaseNamespaceDeclarationSyntax space:
                    names.Add(space.Name.ToString());
                    break;
            }
        }
        names.Reverse();
        return string.Join('.', names);
    }

    /// <summary>The stub's first line naming the assembly it shows: "#region Assembly Shop.Core, Version=…".</summary>
    [GeneratedRegex(@"^#region Assembly ([^,\s]+)", RegexOptions.Multiline)]
    private static partial Regex AssemblyLine();
}
