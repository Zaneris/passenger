using System.Collections.Immutable;
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

/// <summary>The member of a Godot script's code at a caret by every name Godot knows it by (<see cref="GodotGenerated"/>), or
/// the engine's class or member there by Godot's own names (<see cref="GodotSource.EngineMember"/>).</summary>
public sealed class GodotNames : IExtensionDocumentMessageHandler<GodotNamesAsked, GodotNamesFound>
{
    public Task<GodotNamesFound> ExecuteAsync(GodotNamesAsked message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        GodotGenerated.AtAsync(context.Solution, context.Document(), message, cancellationToken);
}

/// <summary>The res:// strings of a Godot project's C# naming files or folders asked for (<see cref="GodotGenerated.PathsAsync"/>).</summary>
public sealed class GodotPaths : IExtensionWorkspaceMessageHandler<GodotPathsAsked, GodotPathsFound>
{
    public Task<GodotPathsFound> ExecuteAsync(GodotPathsAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        GodotGenerated.PathsAsync(context.Solution, message, cancellationToken);
}

/// <summary>A Godot script's signals, methods and properties by the names Godot's source generator gives them beside the
/// code's own (a <c>[Signal]</c> delegate XEventHandler's event X, <c>SignalName.X</c> and <c>EmitSignalX</c>; a method's
/// <c>MethodName.X</c>; a property's or field's <c>PropertyName.X</c>) and by the strings Godot's own calls take for them
/// (<c>EmitSignal("X")</c>, <c>new Callable(this, "X")</c>, <c>Set("X", value)</c>), a string's kind read from the name of
/// the parameter of Godot's API it is given to: the code's member at a caret by any of those names, and every use of it
/// by them, which no rename of the code's own name reaches.</summary>
internal static class GodotGenerated
{
    private const string Emit = "EmitSignal";

    public static async Task<GodotNamesFound> AtAsync(Solution solution, Document document, GodotNamesAsked asked, CancellationToken cancel)
    {
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(asked.At.Line, asked.At.Character));
        if (await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
            || GodotSharp.ObjectIn(model.Compilation) is not { } godotObject)
        {
            return GodotNamesFound.None;
        }
        (ISymbol Source, GodotNameKind Kind, GodotNameVia Via)? found = null;
        GodotEngineMember? engine = null;
        SyntaxToken token = root.FindToken(position);
        if (token.IsKind(SyntaxKind.StringLiteralToken) && token.SpanStart < position && position < token.Span.End
            && NameString(token, model, cancel) is (var kind, INamedTypeSymbol on) && Member(on, kind, token.ValueText) is { } named)
        {
            found = (named, kind, GodotNameVia.String);
        }
        else if (await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancel).ConfigureAwait(false) is { } symbol)
        {
            engine = GodotSource.EngineMember(symbol, godotObject, model.Compilation);
            found = SourceOf(symbol, godotObject, solution);
        }
        if (found is not ({ } source, var foundKind, var via))
        {
            return GodotNamesFound.None with { Engine = engine };
        }
        string written = foundKind == GodotNameKind.Signal ? source.Name[..^GodotSharp.EventHandler.Length] : source.Name;
        IReadOnlyList<GodotNameUse> uses = asked.Uses ? await UsesAsync(solution, source, foundKind, written, cancel).ConfigureAwait(false) : [];
        return new GodotNamesFound(SymbolNames.Named(solution, source), foundKind, written, via, uses) { Engine = engine };
    }

    /// <summary>The code's member a symbol at a caret stands for, and by which of Godot's names it was reached: itself (a
    /// <c>[Signal]</c> delegate, or a method, property or field of a class Godot runs), or a member the generator wrote for one.</summary>
    private static (ISymbol Source, GodotNameKind Kind, GodotNameVia Via)? SourceOf(ISymbol symbol, INamedTypeSymbol godotObject, Solution solution)
    {
        if (symbol.ContainingType is not { } type)
        {
            return null;
        }
        bool generated = SymbolNames.IsGenerated(symbol, solution);
        switch (symbol)
        {
            case INamedTypeSymbol { TypeKind: TypeKind.Delegate } signal when IsNamedSignal(signal) && GodotSharp.Derives(type, godotObject):
                return (signal, GodotNameKind.Signal, GodotNameVia.Own);
            case IEventSymbol { Type: INamedTypeSymbol signal } when generated && IsNamedSignal(signal):
                return (signal, GodotNameKind.Signal, GodotNameVia.Event);
            case IMethodSymbol emit when generated && emit.Name.StartsWith(Emit, StringComparison.Ordinal) && Member(type, GodotNameKind.Signal, emit.Name[Emit.Length..]) is { } signal:
                return (signal, GodotNameKind.Signal, GodotNameVia.Emit);
            case IFieldSymbol name when generated && type.ContainingType is { } owner && GodotSharp.Derives(owner, godotObject):
                GodotNameKind? kind = type.Name switch
                {
                    GodotSharpIl.SignalName => GodotNameKind.Signal,
                    GodotSharpIl.MethodName => GodotNameKind.Method,
                    GodotSharpIl.PropertyName => GodotNameKind.Property,
                    _ => null,
                };
                return kind is { } of && Member(owner, of, name.Name) is { } member ? (member, of, GodotNameVia.Name) : null;
            case IMethodSymbol { MethodKind: MethodKind.Ordinary } method when !generated && GodotSharp.Derives(type, godotObject):
                return (method, GodotNameKind.Method, GodotNameVia.Own);
            case IPropertySymbol or IFieldSymbol when !generated && !symbol.IsImplicitlyDeclared && GodotSharp.Derives(type, godotObject):
                return (symbol, GodotNameKind.Property, GodotNameVia.Own);
        }
        return null;
    }

    /// <summary>A script's own member of a kind by the name Godot knows it by, in its class or a base of the code's: a
    /// signal's <c>[Signal]</c> delegate, a method, a property or field; null for none (an engine class's).</summary>
    internal static ISymbol? Member(INamedTypeSymbol type, GodotNameKind kind, string name)
    {
        for (INamedTypeSymbol? at = type; at is not null && at.Locations.Any(l => l.IsInSource); at = at.BaseType)
        {
            ISymbol? found = kind switch
            {
                GodotNameKind.Signal => at.GetTypeMembers(name + GodotSharp.EventHandler).FirstOrDefault(IsNamedSignal),
                GodotNameKind.Method => at.GetMembers(name).OfType<IMethodSymbol>().FirstOrDefault(m => m.MethodKind == MethodKind.Ordinary && m.Locations.Any(l => l.IsInSource)),
                _ => at.GetMembers(name).FirstOrDefault(m => m is IPropertySymbol or IFieldSymbol && !m.IsImplicitlyDeclared && m.Locations.Any(l => l.IsInSource)),
            };
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>Every use of a member by Godot's other names for it in the solution's own files: each use of a member the
    /// generator wrote for it (with what its name holds before the member's, <c>EmitSignal</c> for an EmitSignalX), and each
    /// string Godot's calls take naming it on an object of its class, or of one deriving from it.</summary>
    private static async Task<IReadOnlyList<GodotNameUse>> UsesAsync(Solution solution, ISymbol source, GodotNameKind kind, string written, CancellationToken cancel)
    {
        INamedTypeSymbol owner = source.ContainingType;
        List<(ISymbol Mirror, string Before)> mirrors = [];
        if (kind == GodotNameKind.Signal)
        {
            mirrors.AddRange(owner.GetMembers(written).OfType<IEventSymbol>().Where(e => SymbolNames.IsGenerated(e, solution)).Select(e => ((ISymbol)e, string.Empty)));
            mirrors.AddRange(owner.GetMembers(Emit + written).OfType<IMethodSymbol>().Where(m => SymbolNames.IsGenerated(m, solution)).Select(m => ((ISymbol)m, Emit)));
        }
        string nested = kind switch { GodotNameKind.Signal => GodotSharpIl.SignalName, GodotNameKind.Method => GodotSharpIl.MethodName, _ => GodotSharpIl.PropertyName };
        mirrors.AddRange(owner.GetTypeMembers(nested).SelectMany(n => n.GetMembers(written)).OfType<IFieldSymbol>().Where(f => SymbolNames.IsGenerated(f, solution))
            .Select(f => ((ISymbol)f, string.Empty)));
        List<GodotNameUse> uses = [];
        HashSet<(string, int)> seen = [];
        foreach ((ISymbol mirror, string before) in mirrors)
        {
            foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(mirror, solution, cancel).ConfigureAwait(false))
            {
                foreach (ReferenceLocation location in referenced.Locations)
                {
                    if (location.Document is SourceGeneratedDocument || location.Document.FilePath is not { Length: > 0 } path || !location.Location.IsInSource
                        || !seen.Add((path, location.Location.SourceSpan.Start)))
                    {
                        continue;
                    }
                    SourceText text = await location.Document.GetTextAsync(cancel).ConfigureAwait(false);
                    TextSpan span = location.Location.SourceSpan;
                    uses.Add(new GodotNameUse(path, PlaceAt(text, span.Start), PlaceAt(text, span.End), before));
                }
            }
        }
        if (solution.GetProject(owner.ContainingAssembly, cancel) is not { } home)
        {
            return uses;
        }
        IEnumerable<Project> projects = [home, .. solution.GetProjectDependencyGraph().GetProjectsThatTransitivelyDependOnThisProject(home.Id).Select(solution.GetProject).OfType<Project>()];
        foreach (Document document in projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is not { Length: > 0 } path || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
            {
                continue;
            }
            List<SyntaxToken> strings = [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && t.ValueText == written && Exact(t))];
            if (strings.Count == 0 || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
            {
                continue;
            }
            SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
            foreach (SyntaxToken token in strings)
            {
                if (NameString(token, model, cancel) is (var named, INamedTypeSymbol on) && named == kind && Within(on, owner) && seen.Add((path, token.SpanStart + 1)))
                {
                    uses.Add(new GodotNameUse(path, PlaceAt(text, token.SpanStart + 1), PlaceAt(text, token.Span.End - 1), string.Empty));
                }
            }
        }
        return uses;
    }

    /// <summary>Every string written as its characters in the C# of a Godot project (<see cref="GodotKinds.ProjectsOf"/>) that Godot's
    /// calls take naming a member of the kind and name asked for on an engine class's object, each once though a project builds it
    /// for several frameworks, with the scripts its class runs as and the node the object is where the code tells it
    /// (<see cref="GodotKinds.NodeOf"/>), so a GDScript's member is found among them by the node its scenes run it on.</summary>
    public static async Task<GodotKindUses> MemberStringsAsync(Solution solution, GodotNameStringsAsked asked, CancellationToken cancel)
    {
        List<GodotKindUse> uses = [];
        HashSet<(string, int)> seen = [];
        HashSet<string> names = [asked.Name];
        GodotKinds.LetGoOfGone(solution);
        foreach (Project project in GodotKinds.ProjectsOf(solution, asked.Folder, asked.Projects))
        {
            foreach (Document document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path || !await GodotKinds.WritesAsync(document, names, prefix: false, cancel).ConfigureAwait(false)
                    || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
                {
                    continue;
                }
                List<SyntaxToken> strings = [.. root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && Exact(t) && t.ValueText == asked.Name)];
                if (strings.Count == 0 || await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model)
                {
                    continue;
                }
                SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
                foreach (SyntaxToken token in strings)
                {
                    if (NameCall(token, model, cancel) is not { On: INamedTypeSymbol { ContainingAssembly.Name: GodotSharp.Assembly } } call || call.Kind != asked.Kind
                        || !seen.Add((path, token.SpanStart)))
                    {
                        continue;
                    }
                    (string? node, string? export) = GodotKinds.NodeOf(solution, call.Object, model, cancel) ?? (null, null);
                    IReadOnlyList<string> scripts = model.GetEnclosingSymbol(token.SpanStart, cancel)?.ContainingType is { } inside
                        ? await GodotSharp.ScriptsOfAsync(inside, solution, cancel).ConfigureAwait(false) : [];
                    uses.Add(new GodotKindUse(path, PlaceAt(text, token.SpanStart + 1), PlaceAt(text, token.Span.End - 1), scripts, node, export, null)
                    {
                        Rest = RestOf(token, call, text),
                    });
                }
            }
        }
        return new GodotKindUses(uses);
    }

    /// <summary>The strings written as their characters in the C# of a Godot project (<see cref="GodotKinds.ProjectsOf"/>) that
    /// give a res:// path naming one asked for, or inside a folder asked for; each once, though a project builds it for
    /// several frameworks.</summary>
    public static async Task<GodotPathsFound> PathsAsync(Solution solution, GodotPathsAsked asked, CancellationToken cancel)
    {
        List<GodotPathUse> uses = [];
        HashSet<(string, int)> seen = [];
        foreach (Project project in GodotKinds.ProjectsOf(solution, asked.Folder, asked.Projects))
        {
            foreach (Document document in project.Documents)
            {
                if (document.FilePath is not { Length: > 0 } path || await document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root)
                {
                    continue;
                }
                SourceText? text = null;
                foreach (SyntaxToken token in root.DescendantTokens().Where(t => t.IsKind(SyntaxKind.StringLiteralToken) && Exact(t)
                    && t.ValueText.StartsWith("res://", StringComparison.Ordinal) && asked.Paths.Any(p => Names(p, t.ValueText))))
                {
                    if (seen.Add((path, token.SpanStart)))
                    {
                        text ??= await document.GetTextAsync(cancel).ConfigureAwait(false);
                        uses.Add(new GodotPathUse(path, token.ValueText, PlaceAt(text, token.SpanStart + 1), PlaceAt(text, token.Span.End - 1)));
                    }
                }
            }
        }
        return new GodotPathsFound(uses);

        static bool Names(string asked, string res) => asked.EndsWith('/') ? res.StartsWith(asked, StringComparison.Ordinal)
            : res == asked || res.StartsWith(asked + "::", StringComparison.Ordinal);
    }

    /// <summary>What a string literal names as Godot's own calls take it (<see cref="NameCall"/>): what it names and the type
    /// of the object it names it on; null for any other string.</summary>
    internal static (GodotNameKind Kind, ITypeSymbol On)? NameString(SyntaxToken token, SemanticModel model, CancellationToken cancel) =>
        NameCall(token, model, cancel) is { } call ? (call.Kind, call.On) : null;

    /// <summary>A call of Godot's API a string literal is given to naming a signal, a method or a property, by the name of the
    /// <c>StringName</c> parameter it is given to: what it names, the type of the object it names it on (the call's own, or
    /// the object given beside it: ToSignal's source, a Callable's target), the method called and its arguments. Null for
    /// any other string.</summary>
    internal static GodotNameCall? NameCall(SyntaxToken token, SemanticModel model, CancellationToken cancel)
    {
        if (token.Parent is not LiteralExpressionSyntax literal || literal.Parent is not ArgumentSyntax argument || argument.Parent?.Parent is not { } call)
        {
            return null;
        }
        (IMethodSymbol? method, IOperation? instance, ImmutableArray<IArgumentOperation> arguments) = model.GetOperation(call, cancel) switch
        {
            IInvocationOperation invocation => (invocation.TargetMethod, invocation.Instance, invocation.Arguments),
            IObjectCreationOperation { Constructor: { } made } creation => (made, null, creation.Arguments),
            _ => (null, null, default),
        };
        if (method?.ContainingAssembly.Name != GodotSharp.Assembly || arguments.IsDefault
            || arguments.FirstOrDefault(a => a.Syntax == argument) is not { Parameter: { Type.Name: "StringName" } parameter } given)
        {
            return null;
        }
        GodotNameKind? kind = parameter.Name switch
        {
            "signal" => GodotNameKind.Signal,
            "method" => GodotNameKind.Method,
            "property" => GodotNameKind.Property,
            _ => null,
        };
        IOperation? named = arguments.FirstOrDefault(a => a != given && GodotSharp.IsObject(a.Parameter?.Type)) is { } beside
            ? Unconverted(beside.Value) : instance;
        return kind is { } of && named?.Type is { } on ? new GodotNameCall(of, on, method, arguments) { Object = named } : null;
    }

    /// <summary>The arguments a call passing them on after a name gives (<see cref="GodotCallRest"/>), each written by
    /// place; null for another call, or one that names an argument or passes them as an array.</summary>
    private static GodotCallRest? RestOf(SyntaxToken token, GodotNameCall call, SourceText text)
    {
        if (call.Method.Name is not ("Call" or "CallDeferred" or "Rpc" or "EmitSignal") || token.Parent?.Parent is not ArgumentSyntax named
            || named.Parent is not ArgumentListSyntax list || list.Arguments.Any(a => a.NameColon is not null))
        {
            return null;
        }
        int at = list.Arguments.IndexOf(named);
        if (at < 0 || call.Arguments.Any(a => a.ArgumentKind == ArgumentKind.ParamArray && a.Value is IArrayCreationOperation { IsImplicit: false }))
        {
            return null;
        }
        return new GodotCallRest(call.Method.Name, PlaceAt(text, named.Span.End), PlaceAt(text, list.CloseParenToken.SpanStart),
            [.. list.Arguments.Skip(at + 1).Select(a => a.ToString())]);
    }

    /// <summary>An argument's value as written, before what converts it to its parameter's type.</summary>
    internal static IOperation Unconverted(IOperation value)
    {
        while (value is IConversionOperation { IsImplicit: true } conversion)
        {
            value = conversion.Operand;
        }
        return value;
    }

    /// <summary>Whether a type is a class or derives from it.</summary>
    private static bool Within(INamedTypeSymbol type, INamedTypeSymbol owner)
    {
        for (INamedTypeSymbol? at = type; at is not null; at = at.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(at.OriginalDefinition, owner.OriginalDefinition))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a type is a script's signal named as Godot's source generator takes one, XEventHandler.</summary>
    private static bool IsNamedSignal(INamedTypeSymbol type) =>
        type.Name.EndsWith(GodotSharp.EventHandler, StringComparison.Ordinal) && type.Name.Length > GodotSharp.EventHandler.Length && GodotSharp.IsSignal(type);
}

/// <summary>A call of Godot's API given a string naming a signal, a method or a property (<see cref="GodotGenerated.NameCall"/>),
/// with the object it names it on as written.</summary>
internal sealed record GodotNameCall(GodotNameKind Kind, ITypeSymbol On, IMethodSymbol Method, ImmutableArray<IArgumentOperation> Arguments)
{
    public IOperation? Object { get; init; }
}

/// <summary>The strings Godot's calls take naming a member of a kind by a name in a Godot project's C#
/// (<see cref="GodotGenerated.MemberStringsAsync"/>), which a GDScript member's usages list.</summary>
public sealed class GodotNameStrings : IExtensionWorkspaceMessageHandler<GodotNameStringsAsked, GodotKindUses>
{
    public Task<GodotKindUses> ExecuteAsync(GodotNameStringsAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken) =>
        GodotGenerated.MemberStringsAsync(context.Solution, message, cancellationToken);
}
