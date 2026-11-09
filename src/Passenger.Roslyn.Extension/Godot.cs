using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;
using Passenger.Workspace;
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
/// code runs on with the type each asks for, the res:// paths and uid:// names its strings give, and its strings naming what the
/// project's own files define (<see cref="GodotKinds"/>); and of the project's types what the scenes ask: an engine class's chain by Godot's names (<c>[GodotClassName]</c>, else the C# name), a script's class's,
/// and a node's signal or method by the name a scene writes (an engine one's snake_case, a script's own as declared).</summary>
internal static partial class GodotSource
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
        foreach (string script in asked.Scripts.Concat(asked.Signals.Select(s => s.Script)).Concat(asked.Methods.Select(m => m.Script))
            .Concat(asked.Properties.Select(p => p.Script)).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            scripts[script] = ScriptClass(compilation, script, cancel);
        }
        List<GodotChain?> scriptChains = [.. asked.Scripts.Select(s => scripts[s] is { } type ? new GodotChain(s, [.. Lineage(type)], engine.First(type) is { } first ? engine.NameOf(first) : null, type.Name) : null)];
        List<GodotSignature?> signals = [.. asked.Signals.Select(s => Signal(engine, s.Script is null ? null : scripts[s.Script], s.Engine, s.Name))];
        List<GodotSignature?> methods = [.. asked.Methods.Select(m => Method(engine, m.Script is null ? null : scripts[m.Script], m.Engine, m.Name))];
        List<GodotSignature?> properties = [.. asked.Properties.Select(p => Property(engine, p.Script is null ? null : scripts[p.Script], p.Engine, p.Name))];
        IReadOnlyList<GodotKindRead> kinds = GodotKinds.Read(solution, root, model, declared, text, cancel);
        return new GodotCodeFound(classes, calls, resources, chains, scriptChains, signals, methods)
        {
            Strings = Strings(solution, root, model, engine, godotObject, declared, asked.Engine, text, cancel),
            Kinds = kinds,
            Added = await GodotKinds.AddedAsync(solution, documents[0].Project, kinds, cancel).ConfigureAwait(false),
            Properties = properties,
        };
    }

    /// <summary>The file's strings Godot's calls take naming a signal, a method or a property (<see cref="GodotGenerated.NameCall"/>),
    /// each with its class, the member it names found on its object's class (a script's own, or its engine class's), an
    /// EmitSignal's count of arguments, and a Connect's Callable joined to its signal; one on an engine class's object with
    /// the node the object is (<see cref="GodotKinds.NodeOf"/>), whose script may give more, and where its class lacks it, the
    /// engine classes the scenes ask of (<paramref name="engines"/>) that derive from its class and have it.</summary>
    private static IReadOnlyList<GodotStringRead> Strings(Solution solution, SyntaxNode root, SemanticModel model, Engine engine, INamedTypeSymbol godotObject,
        IReadOnlyList<INamedTypeSymbol> declared, IReadOnlyList<string> engines, SourceText text, CancellationToken cancel)
    {
        List<GodotStringRead> reads = [];
        Dictionary<int, int> byStart = [];
        List<(int Signal, int Method)> joined = [];
        foreach (SyntaxToken token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken)))
        {
            // An AnimationTree's parameters/… property is its tree's parameter, which GodotKinds reads.
            if (GodotGenerated.NameCall(token, model, cancel) is not { On: INamedTypeSymbol on } call
                || call.Kind == GodotNameKind.Property && token.ValueText.StartsWith(GodotKinds.Parameters, StringComparison.Ordinal) && GodotKinds.Of(token, model, cancel) is not null)
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
            // An EmitSignal's arguments after the signal, or a Call's after the method, unless they are given as one array or span.
            int? given = (call.Kind == GodotNameKind.Signal && call.Method.Name == "EmitSignal" || call.Kind == GodotNameKind.Method && call.Method.Name is "Call" or "CallDeferred")
                && token.Parent?.Parent?.Parent is ArgumentListSyntax list
                && call.Arguments.FirstOrDefault(a => a.Parameter?.IsParams == true) is { ArgumentKind: not ArgumentKind.Explicit } ? list.Arguments.Count - 1 : null;
            bool exact = GodotGenerated.Exact(token);
            byStart[token.SpanStart] = reads.Count;
            GodotSignature? handler = null;
            if (call.Kind == GodotNameKind.Signal && call.Arguments.FirstOrDefault(a => a.Parameter?.Type is { Name: "Callable", ContainingNamespace.Name: Namespace })?.Value is { } callable)
            {
                if (GodotGenerated.Unconverted(callable) is IObjectCreationOperation { Arguments: [_, { Value: var method }] } && GodotGenerated.Unconverted(method) is ILiteralOperation { Syntax: LiteralExpressionSyntax literal })
                {
                    joined.Add((reads.Count, literal.Token.SpanStart));
                }
                else
                {
                    handler = HandlerOf(engine, GodotGenerated.Unconverted(callable));
                }
            }
            (string? node, string? export) = !script ? GodotKinds.NodeOf(solution, call.Object, model, cancel) ?? (null, null) : (null, null);
            IReadOnlyList<string> having = found is null && !script && engineClass is not null
                ? [.. engines.Distinct(StringComparer.Ordinal).Where(e => engine.Type(e) is { } type && engine.Chain(type).Contains(engineClass) && call.Kind switch
                {
                    GodotNameKind.Signal => Signal(engine, null, e, name),
                    GodotNameKind.Method => Method(engine, null, e, name),
                    _ => Property(engine, null, e, name),
                } is not null)]
                : [];
            reads.Add(new GodotStringRead(call.Kind, name, PlaceAt(text, exact ? token.SpanStart + 1 : token.SpanStart), PlaceAt(text, exact ? token.Span.End - 1 : token.Span.End), exact,
                on.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), script, found, given, null)
            {
                Class = GodotKinds.ClassOf(declared, token, model, cancel),
                Node = node,
                Export = export,
                Engines = having,
                Handler = handler,
                Connected = call.Kind == GodotNameKind.Method ? ConnectedTo(engine, token, model, cancel) : null,
            });
        }
        foreach ((int signal, int method) in joined)
        {
            if (byStart.TryGetValue(method, out int index) && reads[index].Kind == GodotNameKind.Method)
            {
                reads[index] = reads[index] with { Signal = signal };
                reads[signal] = reads[signal] with { Handler = reads[index].Found };
            }
        }
        return reads;
    }

    /// <summary>The C# method a Connect's Callable calls where no string the file reads names it: <c>new Callable(target,
    /// MethodName.X)</c> (or nameof, a constant) on a class of the code's, or <c>Callable.From(X)</c>; null for a lambda, or
    /// a target not of the code's.</summary>
    private static GodotSignature? HandlerOf(Engine engine, IOperation callable)
    {
        if (callable is IObjectCreationOperation { Arguments: [{ Value: var target }, { Value: var method }] }
            && GodotGenerated.Unconverted(target).Type is INamedTypeSymbol type && !engine.Owns(type) && type.Locations.Any(l => l.IsInSource)
            && NamedBy(GodotGenerated.Unconverted(method), "MethodName") is { } name)
        {
            return Method(engine, type, null, name);
        }
        return callable is IInvocationOperation { TargetMethod: { Name: "From", ContainingType.Name: "Callable" }, Arguments: [{ Value: var given }, ..] }
            && GodotGenerated.Unconverted(given) is IDelegateCreationOperation { Target: IMethodReferenceOperation { Method: { } from } }
            ? Signature(from.Name, from, from, source: true) : null;
    }

    /// <summary>The C# signal a Callable naming a method by a string is connected to, where its Connect names the signal by
    /// SignalName, nameof or a constant on a class of the code's; null for any other string.</summary>
    private static GodotSignature? ConnectedTo(Engine engine, SyntaxToken token, SemanticModel model, CancellationToken cancel)
    {
        if (token.Parent?.Parent?.Parent?.Parent is not BaseObjectCreationExpressionSyntax made || model.GetOperation(made, cancel) is not IObjectCreationOperation creation)
        {
            return null;
        }
        IOperation? up = creation.Parent;
        while (up is IConversionOperation)
        {
            up = up.Parent;
        }
        if (up is not IArgumentOperation { Parent: IInvocationOperation { TargetMethod.Name: "Connect" } connect }
            || connect.Arguments.FirstOrDefault(a => a.Parameter?.Name == "signal")?.Value is not { } signal
            || GodotGenerated.Unconverted(signal) is ILiteralOperation || NamedBy(GodotGenerated.Unconverted(signal), "SignalName") is not { } name)
        {
            return null;
        }
        INamedTypeSymbol? owner = (connect.Instance is { } instance ? GodotGenerated.Unconverted(instance).Type : model.GetEnclosingSymbol(made.SpanStart, cancel)?.ContainingType) as INamedTypeSymbol;
        return owner is not null && !engine.Owns(owner) && owner.Locations.Any(l => l.IsInSource) ? Signal(engine, owner, null, name) : null;
    }

    /// <summary>The name a value gives a member by: a constant (nameof's too), or a field of a generated class of names
    /// (<c>MethodName.OnDied</c>, <c>SignalName.Hit</c>).</summary>
    private static string? NamedBy(IOperation value, string names) => value switch
    {
        { ConstantValue: { HasValue: true, Value: string constant } } => constant,
        IFieldReferenceOperation { Field: { } field } when field.ContainingType?.Name == names => field.Name,
        IPropertyReferenceOperation { Property: { } property } when property.ContainingType?.Name == names => property.Name,
        _ => null,
    };

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
                exports.Add(new GodotMember(member.Name, of.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat), at.Path, at.At, at.End)
                {
                    Node = NodeOf(engine, of),
                    Inspected = await InspectedAsync(member, of, engine, solution, cancel).ConfigureAwait(false),
                });
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
                : new GodotNodeCall(path, PlaceAt(text, span.Start), PlaceAt(text, span.End), false, index, asked, method.Name == "GetNodeOrNull",
                    written is LiteralExpressionSyntax { Token: var literal } && literal.IsKind(SyntaxKind.StringLiteralToken));
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
    private static GodotSignature? Signal(Engine engine, INamedTypeSymbol? script, string? engineClass, string name) =>
        SignalOf(engine, script, engineClass, name) is ({ } invoke, var declared) ? Signature(name, invoke, declared, source: declared is not null) : null;

    /// <summary>A signal's delegate's Invoke, as <see cref="Signal"/> finds it, with the script's delegate where the code declares it.</summary>
    private static (IMethodSymbol Invoke, INamedTypeSymbol? Declared)? SignalOf(Engine engine, INamedTypeSymbol? script, string? engineClass, string name)
    {
        for (INamedTypeSymbol? type = script; type is not null && !engine.Owns(type); type = type.BaseType)
        {
            if (type.GetTypeMembers(name + "EventHandler").FirstOrDefault(d => d.TypeKind == TypeKind.Delegate && Has(d, "SignalAttribute")) is { DelegateInvokeMethod: { } invoke } signal)
            {
                return (invoke, signal);
            }
        }
        string wanted = Plain(name);
        for (INamedTypeSymbol? type = (script is null ? null : engine.First(script)) ?? (engineClass is null ? null : engine.Type(engineClass)); type is not null; type = type.BaseType)
        {
            if (type.GetMembers().OfType<IEventSymbol>().FirstOrDefault(e => Plain(e.Name) == wanted) is { Type: INamedTypeSymbol { DelegateInvokeMethod: { } invoke } })
            {
                return (invoke, null);
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
    /// base of the code's), else its engine class's by the snake_case name; its type as its one parameter, and for a script's
    /// own the node it names by its path where it is one (<see cref="NodeOf"/>).</summary>
    private static GodotSignature? Property(Engine engine, INamedTypeSymbol? script, string? engineClass, string name)
    {
        for (INamedTypeSymbol? type = script; type is not null && !engine.Owns(type); type = type.BaseType)
        {
            if (type.GetMembers(name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol && !m.IsImplicitlyDeclared) is { } own)
            {
                ITypeSymbol of = (own as IPropertySymbol)?.Type ?? ((IFieldSymbol)own).Type;
                string shown = of.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                return (Declared(own) is { } at ? new GodotSignature(name, [shown], 0, at.Path, at.At, at.End) : new GodotSignature(name, [shown], 0, null, null, null))
                    with { Node = NodeOf(engine, of) };
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

    /// <summary>A symbol of GodotSharp's engine by Godot's own names: a class by its name, a property, method or signal (its
    /// event) by its class's and its own (<see cref="GodotSharpNames"/>), a script's override of an engine method as the method
    /// it overrides, anything else of a class's as the class; null for what is not the engine's.</summary>
    internal static GodotEngineMember? EngineMember(ISymbol symbol, INamedTypeSymbol godotObject, Compilation compilation)
    {
        Engine engine = Engine.Of(godotObject.ContainingAssembly);
        ISymbol at = symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } made ? made.ContainingType : symbol;
        while (at is IMethodSymbol { OverriddenMethod: { } overridden } && at.ContainingType is { } declaring && !engine.Owns(declaring))
        {
            at = overridden;
        }
        if (at is INamedTypeSymbol type)
        {
            return engine.Owns(type) && engine.NameOf(type) is { } name ? new GodotEngineMember(name, null, null) : null;
        }
        if (at.ContainingType is not { } owner || !engine.Owns(owner) || engine.NameOf(owner) is not { } of)
        {
            return null;
        }
        (string? nested, GodotNameKind kind) = at switch
        {
            IPropertySymbol => ("PropertyName", GodotNameKind.Property),
            IMethodSymbol { MethodKind: MethodKind.Ordinary } => ("MethodName", GodotNameKind.Method),
            IEventSymbol => ("SignalName", GodotNameKind.Signal),
            _ => (null, GodotNameKind.Property),
        };
        return nested is not null && GodotSharpNames.Of(compilation, owner, nested, at.Name) is { } named
            ? new GodotEngineMember(of, kind, named) : new GodotEngineMember(of, null, null);
    }

    /// <summary>The Variant type of a Godot value type C# names (<c>Vector2I</c>, Godot's <c>Vector2i</c>), of those an
    /// export takes, Vector2 to RID; null for any other.</summary>
    private static GodotType? VariantOf(INamedTypeSymbol type) =>
        type.ContainingNamespace.Name == Namespace && GodotTypes.Named(TextNames.GetValueOrDefault(type.Name, type.Name)) is { } variant
            && variant is >= GodotType.Vector2 and <= GodotType.Rid ? variant : null;

    /// <summary>What Godot's inspector takes an exported member as, as Godot's source generator writes its property: its
    /// Variant type, an enum's names and values as its hint (flags for a <c>[Flags]</c> one), an <c>[Export]</c>'s own
    /// hint and hint string, a resource's or a node's class; and its default where its initializer is a constant.</summary>
    private static async Task<GodotExported> InspectedAsync(ISymbol member, ITypeSymbol of, Engine engine, Solution solution, CancellationToken cancel)
    {
        (int type, long hint, string hintString) = of switch
        {
            { SpecialType: SpecialType.System_Boolean } => ((int)GodotType.Bool, 0L, string.Empty),
            INamedTypeSymbol { TypeKind: TypeKind.Enum } named => ((int)GodotType.Int, named.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute") ? 6L : 2L,
                string.Join(",", named.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).Select(f => $"{f.Name}:{Convert.ToInt64(f.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)}"))),
            { SpecialType: SpecialType.System_Byte or SpecialType.System_SByte or SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32
                or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64 } => ((int)GodotType.Int, 0L, string.Empty),
            { SpecialType: SpecialType.System_Single or SpecialType.System_Double } => ((int)GodotType.Float, 0L, string.Empty),
            { SpecialType: SpecialType.System_String } => ((int)GodotType.String, 0L, string.Empty),
            INamedTypeSymbol godot when VariantOf(godot) is { } variant => ((int)variant, 0L, string.Empty),
            INamedTypeSymbol { ContainingNamespace.Name: "Collections" } collection when collection.Name is "Array" or "Dictionary" =>
                ((int)(collection.Name == "Array" ? GodotType.Array : GodotType.Dictionary), 0L, string.Empty),
            INamedTypeSymbol given when engine.Type("Resource") is { } resource && Derives(given, resource) => ((int)GodotType.Object, 17L, engine.NameOf(engine.First(given) ?? given) ?? given.Name),
            INamedTypeSymbol given when engine.Type("Node") is { } node && (SymbolEqualityComparer.Default.Equals(given, node) || Derives(given, node)) => ((int)GodotType.Object, 34L, engine.NameOf(engine.First(given) ?? given) ?? given.Name),
            _ => (0, 0L, string.Empty),
        };
        if (member.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ExportAttribute")?.ConstructorArguments is [{ Value: int asked }, { Value: string words }, ..] && asked != 0)
        {
            (hint, hintString) = (asked, words);
        }
        return new GodotExported(type, hint, hintString, await DefaultAsync(member, of, solution, cancel).ConfigureAwait(false));
    }

    /// <summary>An exported member's initializer as Godot's variant text writes its value, where it is a constant (an enum's
    /// member by its value); null where it writes none or one only running the code gives.</summary>
    private static async Task<string?> DefaultAsync(ISymbol member, ITypeSymbol of, Solution solution, CancellationToken cancel)
    {
        foreach (SyntaxReference reference in member.DeclaringSyntaxReferences)
        {
            ExpressionSyntax? initial = await reference.GetSyntaxAsync(cancel).ConfigureAwait(false) switch
            {
                PropertyDeclarationSyntax property => property.Initializer?.Value,
                VariableDeclaratorSyntax variable => variable.Initializer?.Value,
                _ => null,
            };
            if (initial is null || solution.GetDocument(reference.SyntaxTree) is not { } document
                || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
            {
                continue;
            }
            Optional<object?> constant = model.GetConstantValue(initial, cancel);
            if (!constant.HasValue)
            {
                return Created(model.GetOperation(initial, cancel));
            }
            return constant.Value switch
            {
                bool flag => flag ? "true" : "false",
                string text => SymbolDisplay.FormatLiteral(text, quote: true),
                float or double => Convert.ToDouble(constant.Value, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                null => of.IsReferenceType ? "null" : null,
                { } number => Convert.ToInt64(number, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
        }
        return null;
    }

    /// <summary>Godot's names of the built-in types a C# initializer makes, where its text writes them otherwise.</summary>
    private static readonly Dictionary<string, string> TextNames = new(StringComparer.Ordinal)
    {
        ["Vector2I"] = "Vector2i", ["Vector3I"] = "Vector3i", ["Vector4I"] = "Vector4i", ["Rect2I"] = "Rect2i", ["Aabb"] = "AABB", ["Rid"] = "RID",
    };

    /// <summary>A Godot built-in value an initializer makes of constants (<c>new Vector2(1, 2)</c>, <c>new Color(1, 0, 0)</c>), as
    /// Godot's variant text writes it, a colour with its alpha; null for any other.</summary>
    private static string? Created(IOperation? made)
    {
        if (made is IConversionOperation { Operand: var operand })
        {
            made = operand;
        }
        if (made is not IObjectCreationOperation { Type: INamedTypeSymbol type } creation || VariantOf(type) is null)
        {
            return null;
        }
        List<string> values = [];
        foreach (IArgumentOperation argument in creation.Arguments)
        {
            if (argument.Value.ConstantValue is not { HasValue: true, Value: { } value } || value is bool or string)
            {
                return null;
            }
            values.Add(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (type.Name == "Color" && values.Count == 3)
        {
            values.Add("1");
        }
        return values.Count == 0 ? null : $"{TextNames.GetValueOrDefault(type.Name, type.Name)}({string.Join(", ", values)})";
    }

    /// <summary>The node a member of a type names by its path where a scene sets it: a NodePath's any node, a node type's a
    /// node of that type; null for any other type.</summary>
    private static GodotTypeRef? NodeOf(Engine engine, ITypeSymbol of) =>
        of is INamedTypeSymbol { Name: "NodePath", ContainingNamespace.Name: Namespace } && engine.Type("Node") is { } any ? engine.Ref(any)
        : of is INamedTypeSymbol given && engine.Type("Node") is { } nodes && (SymbolEqualityComparer.Default.Equals(given, nodes) || Derives(given, nodes)) ? engine.Ref(given)
        : null;

    /// <summary>A signal's or method's parameters, and how many a call must give: every one of a script's method (Godot calls
    /// it with exactly as many), those without a default of an engine's; with where it is declared in the code.</summary>
    private static GodotSignature Signature(string name, IMethodSymbol method, ISymbol? declared, bool source)
    {
        List<string> parameters = [.. method.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))];
        int required = source ? parameters.Count : method.Parameters.Count(p => !p.HasExplicitDefaultValue && !p.IsParams);
        IReadOnlyList<string> names = [.. method.Parameters.Select(p => Written(p.Name))];
        return declared is not null && Declared(declared) is { } at ? new GodotSignature(name, parameters, required, at.Path, at.At, at.End) { Names = names }
            : new GodotSignature(name, parameters, required, null, null, null) { Names = names };
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
