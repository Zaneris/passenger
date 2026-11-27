using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.ValueTracking;

namespace Passenger.Roslyn.Extension;

/// <summary>Inspect This at a caret (<see cref="Inspected"/>): the symbol a name there refers to or a declaration there
/// declares, and which of its inspections it has.</summary>
public sealed class InspectAt : IExtensionDocumentMessageHandler<Caret, Inspected>
{
    public async Task<Inspected> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(message.At.Line, message.At.Character));
        if (await SymbolFinder.FindSymbolAtPositionAsync(document, position, cancellationToken).ConfigureAwait(false) is not { } symbol
            || await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root)
        {
            return new Inspected(null, null, false, false);
        }
        TextSpan at = root.FindToken(Math.Min(position, Math.Max(0, root.FullSpan.End - 1))).Span;
        ValueFlowItem? value = ValueFlow.Holds(symbol) && document.FilePath is { } path
            ? ValueFlow.ItemAt(text, path, at, symbol, root: true, parent: null, cancellationToken) with { Name = symbol.Name }
            : null;
        return new Inspected(symbol.Name, value, symbol is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.Constructor or MethodKind.LocalFunction }
            or IPropertySymbol or IEventSymbol, symbol is INamedTypeSymbol);
    }
}

/// <summary>One level of a value's flow (<see cref="ValueFlowAsked"/>): where it comes from, as the server's own value
/// tracking finds it, or where it goes, each read of it that lands whole in another variable, parameter, field, property or
/// return.</summary>
public sealed class ValueFlow : IExtensionWorkspaceMessageHandler<ValueFlowAsked, ValueFlowFound>
{
    /// <summary>The language server protocol's numbers of the kinds of symbol a value is held in.</summary>
    private const int LspMethod = 6, LspProperty = 7, LspField = 8, LspVariable = 13;

    public async Task<ValueFlowFound> ExecuteAsync(ValueFlowAsked message, ExtensionWorkspaceMessageContext context, CancellationToken cancellationToken)
    {
        Solution solution = context.Solution;
        if (DocumentOf(solution, message.Item.Path) is not { } document
            || await SymbolOf(document, message.Item.Symbol, cancellationToken).ConfigureAwait(false) is not { } symbol)
        {
            return new ValueFlowFound([]);
        }
        return new ValueFlowFound(message.Origins
            ? await OriginsAsync(solution, document, message.Item, symbol, cancellationToken).ConfigureAwait(false)
            : await DestinationsAsync(solution, message.Item, symbol, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Whether a symbol holds a value to follow: a variable, a parameter, a field, a property, or a method's return.</summary>
    public static bool Holds(ISymbol symbol) => symbol is ILocalSymbol or IParameterSymbol or IFieldSymbol or IPropertySymbol
        || symbol is IMethodSymbol { ReturnsVoid: false, MethodKind: MethodKind.Ordinary or MethodKind.LocalFunction };

    /// <summary>A place in a file as an item: its line's code, what holds the value there and its key.</summary>
    public static ValueFlowItem ItemAt(SourceText text, string path, TextSpan at, ISymbol symbol, bool root, ValueFlowItem? parent, CancellationToken cancel)
    {
        LinePosition from = text.Lines.GetLinePosition(at.Start);
        LinePosition to = text.Lines.GetLinePosition(at.End);
        return new ValueFlowItem(text.Lines[from.Line].ToString().Trim(), Described(symbol), KindOf(symbol), path, new Place(from.Line, from.Character),
            new Place(to.Line, to.Character), at.Start, at.Length, SymbolKey.CreateString(symbol, cancel), root, parent is null ? null : parent with { Parent = null });
    }

    /// <summary>What holds a value, as a place's detail says it.</summary>
    private static string Described(ISymbol symbol) => symbol switch
    {
        ILocalSymbol local => $"local {local.Name}",
        IParameterSymbol parameter => $"parameter {parameter.Name} of {OwnerOf(parameter.ContainingSymbol)}",
        IFieldSymbol field => $"field {field.ContainingType.Name}.{field.Name}",
        IPropertySymbol property => $"property {property.ContainingType.Name}.{property.Name}",
        IMethodSymbol method => $"returned by {OwnerOf(method)}",
        _ => symbol.Name,
    };

    /// <summary>A method as a detail names it: its type's name and its own, a lambda as one.</summary>
    private static string OwnerOf(ISymbol member) => member is IMethodSymbol { MethodKind: MethodKind.AnonymousFunction }
        ? "a lambda"
        : member.ContainingType is { } type && member is not IMethodSymbol { MethodKind: MethodKind.LocalFunction } ? $"{type.Name}.{member.Name}" : member.Name;

    private static int KindOf(ISymbol symbol) => symbol switch
    {
        IFieldSymbol => LspField,
        IPropertySymbol => LspProperty,
        IMethodSymbol => LspMethod,
        _ => LspVariable,
    };

    /// <summary>Where a value comes from, by the server's value tracking: from the caret its declarations and the values
    /// assigned to it (a method's from what it returns), and from any place found the places feeding that one.</summary>
    private static async Task<IReadOnlyList<ValueFlowItem>> OriginsAsync(Solution solution, Document document, ValueFlowItem asked, ISymbol symbol, CancellationToken cancel)
    {
        IValueTrackingService tracking = solution.Services.GetRequiredService<IValueTrackingService>();
        ImmutableArray<ValueTrackedItem> found;
        if (asked.Root && symbol is not IMethodSymbol)
        {
            found = await tracking.TrackValueSourceAsync(new TextSpan(asked.Start, asked.Length), document, cancel).ConfigureAwait(false);
        }
        else
        {
            ValueTrackedItem? parent = asked.Parent is { } above && DocumentOf(solution, above.Path) is { } held
                && await SymbolOf(held, above.Symbol, cancel).ConfigureAwait(false) is { } feeding
                ? await TrackedAsync(held, above, feeding, null, cancel).ConfigureAwait(false)
                : null;
            found = await tracking.TrackValueSourceAsync(solution, await TrackedAsync(document, asked, symbol, parent, cancel).ConfigureAwait(false), cancel).ConfigureAwait(false);
        }
        List<ValueFlowItem> made = [];
        foreach (ValueTrackedItem item in found)
        {
            if (solution.GetDocument(item.DocumentId) is { FilePath: { } path } source && source is not SourceGeneratedDocument
                && await source.GetSemanticModelAsync(cancel).ConfigureAwait(false) is { } model
                && item.SymbolKey.Resolve(model.Compilation, cancellationToken: cancel).GetAnySymbol() is { } holder)
            {
                made.Add(ItemAt(item.SourceText, path, item.Span, holder, root: false, asked, cancel));
            }
        }
        return made;
    }

    /// <summary>The server's own item for a place, to track on from.</summary>
    private static async Task<ValueTrackedItem> TrackedAsync(Document document, ValueFlowItem item, ISymbol symbol, ValueTrackedItem? parent, CancellationToken cancel) =>
        new(SymbolKey.Create(symbol, cancel), await document.GetTextAsync(cancel).ConfigureAwait(false), new TextSpan(item.Start, item.Length), document.Id,
            symbol.GetGlyph(), parent);

    /// <summary>Where a value goes: each read of what holds it (each call of a method, for what it returns) that lands whole,
    /// through conversions, parentheses and the branches of a ?: or ??, in a variable, a parameter (an argument), a field, a
    /// property or a method's return.</summary>
    private static async Task<IReadOnlyList<ValueFlowItem>> DestinationsAsync(Solution solution, ValueFlowItem asked, ISymbol symbol, CancellationToken cancel)
    {
        if (!Holds(symbol))
        {
            return [];
        }
        List<ValueFlowItem> made = [];
        HashSet<(DocumentId, TextSpan)> seen = [];
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancel).ConfigureAwait(false))
        {
            foreach (ReferenceLocation location in referenced.Locations)
            {
                if (location.IsCandidateLocation || location.IsImplicit || location.Document is SourceGeneratedDocument || location.Document.FilePath is not { } path
                    || await location.Document.GetSyntaxRootAsync(cancel).ConfigureAwait(false) is not { } root
                    || await location.Document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is not { } model
                    || OperationAt(model, root.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true), cancel) is not { } read
                    || Landing(read, model) is not (var holder, var where)
                    || !seen.Add((location.Document.Id, where.Span)))
                {
                    continue;
                }
                made.Add(ItemAt(await location.Document.GetTextAsync(cancel).ConfigureAwait(false), path, where.Span, holder, root: false, asked, cancel));
            }
        }
        return [.. made.OrderBy(i => i.Path, StringComparer.Ordinal).ThenBy(i => i.Start)];
    }

    /// <summary>The operation a reference is, its name's own or, for a member's name or a call's, the access or call it names;
    /// none outside a statement or a member's body.</summary>
    private static IOperation? OperationAt(SemanticModel model, SyntaxNode node, CancellationToken cancel)
    {
        for (SyntaxNode? at = node; at is not null and not Microsoft.CodeAnalysis.CSharp.Syntax.StatementSyntax and not Microsoft.CodeAnalysis.CSharp.Syntax.MemberDeclarationSyntax; at = at.Parent)
        {
            if (model.GetOperation(at, cancel) is { } operation)
            {
                return operation;
            }
        }
        return null;
    }

    /// <summary>What a read's value lands in whole and where: the variable, parameter, field or property it is assigned or
    /// passed to, or the method it is returned from; none where it is written, not read, or goes into another value.</summary>
    private static (ISymbol Holder, SyntaxNode Where)? Landing(IOperation read, SemanticModel model)
    {
        IOperation value = read;
        if (value.Parent is IAssignmentOperation { Target: var written } && written == value)
        {
            return null;
        }
        while (value.Parent is { } parent)
        {
            switch (parent)
            {
                case IConversionOperation or IParenthesizedOperation or ICoalesceOperation:
                case IConditionalOperation conditional when conditional.Condition != value:
                    value = parent;
                    continue;
                case IAssignmentOperation assigned when assigned.Value == value:
                    return HeldBy(assigned.Target) is { } target ? (target, assigned.Target.Syntax) : null;
                case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                    return (declarator.Symbol, declarator.Syntax);
                case IFieldInitializerOperation { InitializedFields: [var field, ..] } initializer:
                    return (field, initializer.Syntax);
                case IPropertyInitializerOperation { InitializedProperties: [var property, ..] } initializer:
                    return (property, initializer.Syntax);
                case IArgumentOperation { Parameter: { } parameter } argument:
                    return (parameter, argument.Syntax);
                case IReturnOperation { Kind: OperationKind.Return } returned when model.GetEnclosingSymbol(returned.Syntax.SpanStart) is IMethodSymbol method:
                    return (method, returned.Syntax);
                default:
                    return null;
            }
        }
        return null;
    }

    /// <summary>The variable, parameter, field or property an assignment's target is; none for an element or anything else.</summary>
    private static ISymbol? HeldBy(IOperation target) => target switch
    {
        ILocalReferenceOperation local => local.Local,
        IParameterReferenceOperation parameter => parameter.Parameter,
        IFieldReferenceOperation field => field.Field,
        IPropertyReferenceOperation property => property.Property,
        _ => null,
    };

    /// <summary>A C# document of the solution by its path; none where it has none.</summary>
    private static Document? DocumentOf(Solution solution, string path) =>
        SyntaxReads.DocumentIdsOf(solution, path).Select(solution.GetDocument).OfType<Document>().FirstOrDefault();

    /// <summary>A symbol by its key in a document's compilation; none where it resolves to none there.</summary>
    private static async Task<ISymbol?> SymbolOf(Document document, string key, CancellationToken cancel) =>
        await document.GetSemanticModelAsync(cancel).ConfigureAwait(false) is { } model ? SymbolKey.ResolveString(key, model.Compilation, cancellationToken: cancel).GetAnySymbol() : null;
}
