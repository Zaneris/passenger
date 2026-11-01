using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
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
/// <c>[Export]</c> members and <c>[Signal]</c> delegates, the calls of Node's GetNode and GetNodeOrNull on the node the
/// code runs on with the type each asks for, and the res:// paths and uid:// names its strings give; and of the project's types what the
/// scenes ask: an engine class's chain by Godot's names (<c>[GodotClassName]</c>, else the C# name), a script's class's,
/// and a node's signal or method by the name a scene writes (an engine one's snake_case, a script's own as declared).</summary>
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
        List<GodotPathRead> resources = [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)
                && (t.ValueText.StartsWith("res://", StringComparison.Ordinal) || t.ValueText.StartsWith("uid://", StringComparison.Ordinal)))
            .Select(t => Exact(t) ? new GodotPathRead(t.ValueText, PlaceAt(text, t.SpanStart + 1), PlaceAt(text, t.Span.End - 1))
                : new GodotPathRead(t.ValueText, PlaceAt(text, t.SpanStart), PlaceAt(text, t.Span.End)))];
        List<GodotChain> chains = [.. asked.Engine.Distinct(StringComparer.Ordinal).Select(name => engine.Type(name) is { } type
            ? new GodotChain(name, [.. engine.Chain(type)], name, type.Name) : null).OfType<GodotChain>()];
        Dictionary<string, INamedTypeSymbol?> scripts = new(StringComparer.Ordinal);
        foreach (string script in asked.Scripts.Concat(asked.Signals.Select(s => s.Script)).Concat(asked.Methods.Select(m => m.Script)).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            scripts[script] = ScriptClass(compilation, script, cancel);
        }
        List<GodotChain?> scriptChains = [.. asked.Scripts.Select(s => scripts[s] is { } type ? new GodotChain(s, [.. Lineage(type)], engine.First(type) is { } first ? engine.NameOf(first) : null, type.Name) : null)];
        List<GodotSignature?> signals = [.. asked.Signals.Select(s => Signal(engine, s.Script is null ? null : scripts[s.Script], s.Engine, s.Name))];
        List<GodotSignature?> methods = [.. asked.Methods.Select(m => Method(engine, m.Script is null ? null : scripts[m.Script], m.Engine, m.Name))];
        return new GodotCodeFound(classes, calls, resources, chains, scriptChains, signals, methods) { Strings = Strings(root, model, engine, godotObject, text, cancel) };
    }

    /// <summary>The file's strings Godot's calls take naming a signal, a method or a property (<see cref="GodotGenerated.NameCall"/>),
    /// each with the member it names found on its object's class (a script's own, or its engine class's), an EmitSignal's
    /// count of arguments, and a Connect's Callable joined to the signal it is connected to.</summary>
    private static IReadOnlyList<GodotStringRead> Strings(SyntaxNode root, SemanticModel model, Engine engine, INamedTypeSymbol godotObject, SourceText text, CancellationToken cancel)
    {
        List<GodotStringRead> reads = [];
        Dictionary<int, int> byStart = [];
        List<(int Signal, int Method)> joined = [];
        foreach (SyntaxToken token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)))
        {
            if (GodotGenerated.NameCall(token, model, cancel) is not { On: INamedTypeSymbol on } call)
            {
                continue;
            }
            bool script = !engine.Owns(on) && on.Locations.Any(l => l.IsInSource) && Derives(on, godotObject);
            INamedTypeSymbol? own = script ? on : null;
            string? engineClass = script ? null : engine.First(on) is { } first ? engine.NameOf(first) : null;
            string name = token.ValueText;
            GodotSignature? found = call.Kind switch
            {
                GodotNameKind.Signal => Signal(engine, own, engineClass, name),
                GodotNameKind.Method => Method(engine, own, engineClass, name),
                _ => Property(engine, own, engineClass, name),
            };
            // An EmitSignal's arguments after the signal, unless they are given as one array or span.
            int? given = call.Kind == GodotNameKind.Signal && call.Method.Name == "EmitSignal" && token.Parent?.Parent?.Parent is ArgumentListSyntax list
                && call.Arguments.FirstOrDefault(a => a.Parameter?.IsParams == true) is { ArgumentKind: not ArgumentKind.Explicit } ? list.Arguments.Count - 1 : null;
            bool exact = GodotGenerated.Exact(token);
            byStart[token.SpanStart] = reads.Count;
            if (call.Kind == GodotNameKind.Signal && call.Arguments.FirstOrDefault(a => a.Parameter?.Type is { Name: "Callable", ContainingNamespace.Name: Namespace })?.Value is { } callable
                && GodotGenerated.Unconverted(callable) is IObjectCreationOperation { Arguments: [_, { Value: var method }] } && GodotGenerated.Unconverted(method) is ILiteralOperation { Syntax: LiteralExpressionSyntax literal })
            {
                joined.Add((reads.Count, literal.Token.SpanStart));
            }
            reads.Add(new GodotStringRead(call.Kind, name, PlaceAt(text, exact ? token.SpanStart + 1 : token.SpanStart), PlaceAt(text, exact ? token.Span.End - 1 : token.Span.End), exact,
                on.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), script, found, given, null));
        }
        foreach ((int signal, int method) in joined)
        {
            if (byStart.TryGetValue(method, out int index) && reads[index].Kind == GodotNameKind.Method)
            {
                reads[index] = reads[index] with { Signal = signal };
            }
        }
        return reads;
    }

    /// <summary>A class of the file: its place, engine class, scripts, exports and signals.</summary>
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
                ITypeSymbol of = (member as IPropertySymbol)?.Type ?? ((IFieldSymbol)member).Type;
                // A NodePath names any node; a node's type, a node of it.
                GodotTypeRef? node = of is INamedTypeSymbol { Name: "NodePath", ContainingNamespace.Name: Namespace } && engine.Type("Node") is { } any ? engine.Ref(any)
                    : of is INamedTypeSymbol given && engine.Type("Node") is { } nodes && (SymbolEqualityComparer.Default.Equals(given, nodes) || Derives(given, nodes)) ? engine.Ref(given)
                    : null;
                exports.Add(new GodotMember(member.Name, of.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), at.Path, at.At, at.End) { Node = node });
            }
        }
        List<GodotMember> signals = [];
        foreach (INamedTypeSymbol signal in type.GetTypeMembers().Where(d => d.TypeKind == TypeKind.Delegate && Has(d, "SignalAttribute")))
        {
            if (Declared(signal) is { } at)
            {
                signals.Add(new GodotMember(SignalName(signal), Parameters(signal.DelegateInvokeMethod), at.Path, at.At, at.End));
            }
        }
        return new GodotClassFound(type.Name, Full(type), here ? PlaceAt(text, span.Start) : new Place(0, 0), here ? PlaceAt(text, span.End) : new Place(0, 0),
            engine.First(type) is { } first ? engine.NameOf(first) : null, scripts, exports, signals);
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

    /// <summary>A signal on a node: its script's <c>[Signal]</c> delegate of the name (in its class or a base of the code's),
    /// else its engine class's event of the name, by the script's first engine class or the node's own.</summary>
    private static GodotSignature? Signal(Engine engine, INamedTypeSymbol? script, string? engineClass, string name)
    {
        for (INamedTypeSymbol? type = script; type is not null && !engine.Owns(type); type = type.BaseType)
        {
            if (type.GetTypeMembers(name + "EventHandler").FirstOrDefault(d => d.TypeKind == TypeKind.Delegate && Has(d, "SignalAttribute")) is { DelegateInvokeMethod: { } invoke } signal)
            {
                return Signature(name, invoke, signal, source: true);
            }
        }
        string wanted = Plain(name);
        for (INamedTypeSymbol? type = (script is null ? null : engine.First(script)) ?? (engineClass is null ? null : engine.Type(engineClass)); type is not null; type = type.BaseType)
        {
            if (type.GetMembers().OfType<IEventSymbol>().FirstOrDefault(e => Plain(e.Name) == wanted) is { Type: INamedTypeSymbol { DelegateInvokeMethod: { } invoke } })
            {
                return Signature(name, invoke, null, source: false);
            }
        }
        return null;
    }

    /// <summary>A method on a node a signal can call: its script's of the name (in its class or a base of the code's), else
    /// its engine class's by the scene's snake_case name.</summary>
    private static GodotSignature? Method(Engine engine, INamedTypeSymbol? script, string? engineClass, string name)
    {
        for (INamedTypeSymbol? type = script; type is not null && !engine.Owns(type); type = type.BaseType)
        {
            if (type.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared) is { } own)
            {
                return Signature(name, own, own, source: true);
            }
        }
        string wanted = Plain(name);
        for (INamedTypeSymbol? type = (script is null ? null : engine.First(script)) ?? (engineClass is null ? null : engine.Type(engineClass)); type is not null; type = type.BaseType)
        {
            if (type.GetMembers().OfType<IMethodSymbol>().FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && Plain(m.Name) == wanted) is { } method)
            {
                return Signature(name, method, null, source: false);
            }
        }
        return null;
    }

    /// <summary>A property on a node Godot's Get and Set name: its script's property or field of the name (in its class or a
    /// base of the code's), else its engine class's by the snake_case name; its type as its one parameter.</summary>
    private static GodotSignature? Property(Engine engine, INamedTypeSymbol? script, string? engineClass, string name)
    {
        for (INamedTypeSymbol? type = script; type is not null && !engine.Owns(type); type = type.BaseType)
        {
            if (type.GetMembers(name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol && !m.IsImplicitlyDeclared) is { } own)
            {
                string shown = ((own as IPropertySymbol)?.Type ?? ((IFieldSymbol)own).Type).ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                return Declared(own) is { } at ? new GodotSignature(name, [shown], 0, at.Path, at.At, at.End) : new GodotSignature(name, [shown], 0, null, null, null);
            }
        }
        string wanted = Plain(name);
        for (INamedTypeSymbol? type = (script is null ? null : engine.First(script)) ?? (engineClass is null ? null : engine.Type(engineClass)); type is not null; type = type.BaseType)
        {
            if (type.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => Plain(p.Name) == wanted) is { } property)
            {
                return new GodotSignature(name, [property.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)], 0, null, null, null);
            }
        }
        return null;
    }

    /// <summary>A signal's or method's parameters, and how many a call must give: every one of a script's method (Godot calls
    /// it with exactly as many), those without a default of an engine's; with where it is declared in the code.</summary>
    private static GodotSignature Signature(string name, IMethodSymbol method, ISymbol? declared, bool source)
    {
        List<string> parameters = [.. method.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))];
        int required = source ? parameters.Count : method.Parameters.Count(p => !p.HasExplicitDefaultValue && !p.IsParams);
        return declared is not null && Declared(declared) is { } at ? new GodotSignature(name, parameters, required, at.Path, at.At, at.End)
            : new GodotSignature(name, parameters, required, null, null, null);
    }

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

    private static string Parameters(IMethodSymbol? invoke) =>
        $"({string.Join(", ", invoke?.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}") ?? [])})";

    /// <summary>A <c>[Signal]</c> delegate's signal: its name less EventHandler, as Godot's source generator names it.</summary>
    private static string SignalName(INamedTypeSymbol signal) =>
        signal.Name.EndsWith("EventHandler", StringComparison.Ordinal) ? signal.Name[..^"EventHandler".Length] : signal.Name;

    /// <summary>A name with its underscores and letter case left aside, as an engine member's C# name and a scene's
    /// snake_case meet (body_entered, BodyEntered).</summary>
    private static string Plain(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

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
