using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using static Passenger.Roslyn.Extension.SyntaxReads;

namespace Passenger.Roslyn.Extension;

/// <summary>What a Godot project's C# file holds that its scenes speak of (<see cref="GodotSource"/>).</summary>
public sealed class GodotCode : IExtensionWorkspaceMessageHandler<GodotCodeRequest, GodotCodeFound>
{
    public Task<GodotCodeFound> ExecuteAsync(GodotCodeRequest message, ExtensionMessageContext context, CancellationToken cancellationToken) =>
        GodotSource.ReadAsync(context.Solution, message, cancellationToken);
}

/// <summary>A Godot project's C# as Roslyn binds it against GodotSharp, never by names alone: the file's classes Godot runs
/// as scripts (each class's script path is the one Godot's source generator gives it, <c>[ScriptPath]</c>), their
/// <c>[Export]</c> members, the calls of Node's GetNode and GetNodeOrNull on the node the code runs on with the type each
/// asks for, and the res:// paths its strings give; and of the project's types what the scenes ask: an engine class's
/// chain by Godot's names (<c>[GodotClassName]</c>, else the C# name), and a script's class's.</summary>
internal static class GodotSource
{
    private const string Namespace = "Godot";

    public static async Task<GodotCodeFound> ReadAsync(Solution solution, GodotCodeRequest asked, CancellationToken cancel)
    {
        IReadOnlyList<(Document Document, Project Project)> documents = await LocalizationSource.DocumentsOfAsync(solution, asked.Path, false, cancel).ConfigureAwait(false);
        if (documents.Count == 0 || await documents[0].Project.GetCompilationAsync(cancel).ConfigureAwait(false) is not { } compilation
            || compilation.GetTypeByMetadataName("Godot.GodotObject") is not { } godotObject
            || await documents[0].Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || await documents[0].Document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
        {
            return GodotCodeFound.None;
        }
        Engine engine = Engine.Of(godotObject.ContainingAssembly);
        SourceText text = await documents[0].Document.GetTextAsync(cancel).ConfigureAwait(false);
        List<INamedTypeSymbol> declared = [.. root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Select(c => model.GetDeclaredSymbol(c, cancel)).OfType<INamedTypeSymbol>()
            .Where(t => Derives(t, godotObject)).Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)];
        List<GodotClassFound> classes = [];
        foreach (INamedTypeSymbol type in declared)
        {
            classes.Add(await ClassAsync(type, solution, engine, asked.Path, text, cancel).ConfigureAwait(false));
        }
        List<GodotNodeCall> calls = [.. Calls(root, model, declared, engine, text, cancel)];
        List<GodotPathRead> resources = [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText.StartsWith("res://", StringComparison.Ordinal))
            .Select(t => Exact(t) ? new GodotPathRead(t.ValueText, PlaceAt(text, t.SpanStart + 1), PlaceAt(text, t.Span.End - 1))
                : new GodotPathRead(t.ValueText, PlaceAt(text, t.SpanStart), PlaceAt(text, t.Span.End)))];
        List<GodotChain> chains = [.. asked.Engine.Distinct(StringComparer.Ordinal).Select(name => engine.Type(name) is { } type
            ? new GodotChain(name, [.. engine.Chain(type)], name, type.Name) : null).OfType<GodotChain>()];
        Dictionary<string, INamedTypeSymbol?> scripts = new(StringComparer.Ordinal);
        foreach (string script in asked.Scripts.Distinct(StringComparer.Ordinal))
        {
            scripts[script] = ScriptClass(compilation, script, cancel);
        }
        List<GodotChain?> scriptChains = [.. asked.Scripts.Select(s => scripts[s] is { } type ? new GodotChain(s, [.. Lineage(type)], engine.First(type) is { } first ? engine.NameOf(first) : null, type.Name) : null)];
        return new GodotCodeFound(classes, calls, resources, chains, scriptChains);
    }

    /// <summary>A class of the file: its place, engine class, scripts and exports.</summary>
    private static async Task<GodotClassFound> ClassAsync(INamedTypeSymbol type, Solution solution, Engine engine, string path, SourceText text, CancellationToken cancel)
    {
        List<string> scripts = [.. ScriptPath(type) is { } own ? [own] : Array.Empty<string>()];
        foreach (INamedTypeSymbol derived in await SymbolFinder.FindDerivedClassesAsync(type, solution, transitive: true, cancellationToken: cancel).ConfigureAwait(false))
        {
            if (ScriptPath(derived) is { } script && !scripts.Contains(script))
            {
                scripts.Add(script);
            }
        }
        Location? named = type.Locations.FirstOrDefault(l => l.IsInSource && SamePath(l.SourceTree?.FilePath, path)) ?? type.Locations.FirstOrDefault(l => l.IsInSource);
        TextSpan span = named?.SourceSpan ?? default;
        bool here = named is not null && SamePath(named.SourceTree?.FilePath, path);
        List<GodotMember> exports = [];
        foreach (ISymbol member in type.GetMembers().Where(m => m is IPropertySymbol or IFieldSymbol && !m.IsImplicitlyDeclared && Has(m, "ExportAttribute")))
        {
            if (Declared(member) is { } at)
            {
                exports.Add(new GodotMember(member.Name, (member as IPropertySymbol)?.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)
                    ?? ((IFieldSymbol)member).Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), at.Path, at.At, at.End));
            }
        }
        return new GodotClassFound(type.Name, Full(type), here ? PlaceAt(text, span.Start) : new Place(0, 0), here ? PlaceAt(text, span.End) : new Place(0, 0),
            engine.First(type) is { } first ? engine.NameOf(first) : null, scripts, exports);
    }

    /// <summary>The calls of Node's GetNode and GetNodeOrNull, given a constant path, on the node a class of the file runs on
    /// (called by name alone, or after this. or base.).</summary>
    private static IEnumerable<GodotNodeCall> Calls(SyntaxNode root, SemanticModel model, List<INamedTypeSymbol> declared, Engine engine, SourceText text, CancellationToken cancel)
    {
        foreach (InvocationExpressionSyntax call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            bool onThis = call.Expression is SimpleNameSyntax
                || call.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax };
            if (!onThis || NameOf(call.Expression) is not ("GetNode" or "GetNodeOrNull") || model.GetSymbolInfo(call, cancel).Symbol is not IMethodSymbol method
                || method.OriginalDefinition.ContainingType is not { Name: "Node", ContainingNamespace.Name: Namespace } || call.ArgumentList.Arguments.FirstOrDefault()?.Expression is not { } given
                || model.GetEnclosingSymbol(call.SpanStart, cancel)?.ContainingType is not { } inside)
            {
                continue;
            }
            int index = declared.FindIndex(t => SymbolEqualityComparer.Default.Equals(t, inside) || SymbolEqualityComparer.Default.Equals(t, inside.ContainingType));
            ExpressionSyntax written = given is ObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ } only] } made && model.GetTypeInfo(made, cancel).Type?.Name == "NodePath"
                ? only.Expression : given;
            if (index < 0 || Constant(written, model, cancel) is not { } path)
            {
                continue;
            }
            GodotTypeRef? asked = method.TypeArguments is [{ } type] ? engine.Ref(type) : null;
            bool exact = written is LiteralExpressionSyntax { Token: var token } && Exact(token);
            TextSpan span = written.Span;
            yield return exact ? new GodotNodeCall(path, PlaceAt(text, span.Start + 1), PlaceAt(text, span.End - 1), true, index, asked, method.Name == "GetNodeOrNull")
                : new GodotNodeCall(path, PlaceAt(text, span.Start), PlaceAt(text, span.End), false, index, asked, method.Name == "GetNodeOrNull");
        }
    }

    /// <summary>Whether a string literal is written as its characters: a plain one, no escape in it.</summary>
    private static bool Exact(SyntaxToken token) => token.Text.Length == token.ValueText.Length + 2 && token.Text[0] == '"';

    /// <summary>The class whose <c>[ScriptPath]</c> is a script's res:// path, among the classes of its file's name.</summary>
    private static INamedTypeSymbol? ScriptClass(Compilation compilation, string script, CancellationToken cancel)
    {
        string stem = System.IO.Path.GetFileNameWithoutExtension(script.Split('/')[^1]);
        return compilation.GetSymbolsWithName(stem, SymbolFilter.Type, cancel).OfType<INamedTypeSymbol>().FirstOrDefault(t => ScriptPath(t) == script);
    }

    /// <summary>The res:// path Godot's source generator gives a class (<c>[ScriptPath("res://Main.cs")]</c>); null for none.</summary>
    private static string? ScriptPath(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a => a.AttributeClass is { Name: "ScriptPathAttribute", ContainingNamespace.Name: Namespace })?.ConstructorArguments
            is [{ Value: string path }] ? path : null;

    /// <summary>Where a symbol's name is written in the code, by its file's path; null for one only a generator writes.</summary>
    private static (string Path, Place At, Place End)? Declared(ISymbol symbol)
    {
        foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
        {
            if (reference.SyntaxTree.FilePath is not { Length: > 0 } path || !File.Exists(path))
            {
                continue;
            }
            SyntaxNode node = reference.GetSyntax();
            SyntaxToken name = node switch
            {
                BaseTypeDeclarationSyntax type => type.Identifier,
                DelegateDeclarationSyntax @delegate => @delegate.Identifier,
                MethodDeclarationSyntax method => method.Identifier,
                PropertyDeclarationSyntax property => property.Identifier,
                VariableDeclaratorSyntax variable => variable.Identifier,
                _ => node.GetFirstToken(),
            };
            SourceText text = reference.SyntaxTree.GetText();
            return (path, PlaceAt(text, name.SpanStart), PlaceAt(text, name.Span.End));
        }
        return null;
    }

    private static bool Has(ISymbol symbol, string attribute) =>
        symbol.GetAttributes().Any(a => a.AttributeClass is { } type && type.Name == attribute && type.ContainingNamespace.Name == Namespace);

    private static bool Derives(INamedTypeSymbol type, INamedTypeSymbol godotObject)
    {
        for (INamedTypeSymbol? at = type.BaseType; at is not null; at = at.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(at, godotObject))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>A class and every type it is: its bases and its interfaces, by full name.</summary>
    private static IEnumerable<string> Lineage(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
        {
            yield return Full(at);
        }
        foreach (INamedTypeSymbol contract in type.AllInterfaces)
        {
            yield return Full(contract);
        }
    }

    private static string Full(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);

    /// <summary>GodotSharp's engine classes by Godot's names, read once for each GodotSharp the projects reference.</summary>
    private sealed class Engine
    {
        private static readonly ConditionalWeakTable<IAssemblySymbol, Engine> Kept = [];

        private readonly IAssemblySymbol assembly;
        private readonly Dictionary<string, INamedTypeSymbol> types = new(StringComparer.Ordinal);
        private readonly Dictionary<INamedTypeSymbol, string> names = new(SymbolEqualityComparer.Default);

        private Engine(IAssemblySymbol assembly)
        {
            this.assembly = assembly;
            INamespaceSymbol? godot = assembly.GlobalNamespace.GetNamespaceMembers().FirstOrDefault(n => n.Name == Namespace);
            INamedTypeSymbol? godotObject = godot?.GetTypeMembers("GodotObject").FirstOrDefault();
            foreach (INamedTypeSymbol type in godot?.GetTypeMembers() ?? [])
            {
                if (godotObject is null || type.TypeKind != TypeKind.Class || !(SymbolEqualityComparer.Default.Equals(type, godotObject) || Derives(type, godotObject)))
                {
                    continue;
                }
                string name = type.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "GodotClassNameAttribute")?.ConstructorArguments is [{ Value: string given }]
                    ? given : SymbolEqualityComparer.Default.Equals(type, godotObject) ? "Object" : type.Name;
                types.TryAdd(name, type);
                names[type] = name;
            }
        }

        public static Engine Of(IAssemblySymbol assembly) => Kept.GetValue(assembly, static a => new Engine(a));

        public INamedTypeSymbol? Type(string name) => types.GetValueOrDefault(name);

        public string? NameOf(INamedTypeSymbol type) => names.GetValueOrDefault(type.OriginalDefinition);

        public bool Owns(INamedTypeSymbol type) => SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, assembly);

        /// <summary>A type's first engine class: itself, or its nearest base of GodotSharp's.</summary>
        public INamedTypeSymbol? First(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
            {
                if (NameOf(at) is not null)
                {
                    return at;
                }
            }
            return null;
        }

        /// <summary>An engine class and its bases, by Godot's names.</summary>
        public IEnumerable<string> Chain(INamedTypeSymbol type)
        {
            for (INamedTypeSymbol? at = type; at is not null && NameOf(at) is { } name; at = at.BaseType)
            {
                yield return name;
            }
        }

        /// <summary>A type a node is matched against: an engine class by Godot's name, else by its full name.</summary>
        public GodotTypeRef Ref(ITypeSymbol type) => type is INamedTypeSymbol named && NameOf(named) is { } name
            ? new GodotTypeRef(name, true, type.Name) : new GodotTypeRef(Full(type), false, type.Name);
    }
}
