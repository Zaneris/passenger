using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.SignatureHelp;

namespace Passenger.Roslyn.Extension;

/// <summary>Which of a call's signatures still fit the arguments it has so far, and the parameter the caret's argument
/// fills in each, as C# binds arguments: by name where one is named, else by place, the rest of a <c>params</c> list in its
/// last parameter. An argument fits a parameter it converts to implicitly as written (a constant to a smaller number, an
/// interpolated string to its handler), a <c>params</c> list's parameter also where it converts to the list's element; one
/// whose type is not known yet (empty, typed partway, a lambda, <c>null</c>) or a parameter of a type still to be inferred
/// is no evidence either way, so the signature still fits. Read from the signatures Roslyn's signature help gives, in its
/// order, each keeping its symbol's key.</summary>
internal static class SignatureFits
{
    /// <summary>Each signature's fit and the parameter the caret's argument fills there (-1 for none), in the order the
    /// server lists them; null where the call or a signature's parameters can't be read.</summary>
    public static async Task<(bool Fits, int Parameter)[]?> OfAsync(Document document, int position, SignatureHelpItems items, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null || position > root.FullSpan.End
            || root.FindToken(position).Parent?.AncestorsAndSelf().OfType<BaseArgumentListSyntax>()
                .FirstOrDefault(l => l.Span.IntersectsWith(items.ApplicableSpan)) is not { } list)
        {
            return null;
        }
        Argument[] arguments = Arguments(model, list, cancellationToken);
        // The place the caret's argument stands at, past the arguments written where it follows the last comma.
        int at = list.Arguments.GetSeparators().Count(comma => comma.SpanStart < position);
        if (arguments.Length <= at)
        {
            arguments = [.. arguments, .. Enumerable.Repeat(new Argument(null, null, null), at + 1 - arguments.Length)];
        }
        (bool, int)[] fits = new (bool, int)[items.Items.Count];
        for (int i = 0; i < fits.Length; i++)
        {
            if (Parameters(items.Items[i], model.Compilation, cancellationToken) is not { } parameters)
            {
                return null;
            }
            fits[i] = Fit(model, parameters, arguments, at, items.ArgumentName);
        }
        return fits;
    }

    /// <summary>Whether each of a name's overloads fits the arguments of the call the name at <paramref name="position"/> makes
    /// (a call's name completed again); each fits where it makes none.</summary>
    public static async Task<bool[]> OfNameAsync(Document document, int position, IReadOnlyList<IMethodSymbol> overloads, CancellationToken cancellationToken)
    {
        bool[] fits = [.. overloads.Select(_ => true)];
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null || position > root.FullSpan.End)
        {
            return fits;
        }
        SyntaxToken name = root.FindToken(position);
        if (name.Parent is not SimpleNameSyntax && position > 0)
        {
            name = root.FindToken(position - 1);
        }
        if (name.Parent is not SimpleNameSyntax called
            || called.AncestorsAndSelf().TakeWhile(n => n is ExpressionSyntax).OfType<InvocationExpressionSyntax>().FirstOrDefault() is not { } call
            || !call.Expression.Span.Contains(called.Span))
        {
            return fits;
        }
        Argument[] arguments = Arguments(model, call.ArgumentList, cancellationToken);
        for (int i = 0; i < fits.Length; i++)
        {
            fits[i] = Fit(model, [.. overloads[i].Parameters], arguments, -1, null).Fits;
        }
        return fits;
    }

    /// <summary>An argument of a call as it binds: its name where it is named, what is written for it as a value (none for
    /// one passed by reference, whose type must match), and its type where one is known.</summary>
    private sealed record Argument(string? Name, ExpressionSyntax? Value, ITypeSymbol? Type);

    private static Argument[] Arguments(SemanticModel model, BaseArgumentListSyntax list, CancellationToken cancellationToken) =>
        [.. list.Arguments.Select(a => new Argument(a.NameColon?.Name.Identifier.ValueText, a.RefKindKeyword.IsKind(SyntaxKind.None) ? a.Expression : null,
            a.Expression.IsMissing ? null : model.GetTypeInfo(a.Expression, cancellationToken).Type))];

    /// <summary>A signature's parameters, from the symbol its key names, as many as it shows; null where it keeps none.</summary>
    private static IParameterSymbol[]? Parameters(SignatureHelpItem item, Compilation compilation, CancellationToken cancellationToken)
    {
        ImmutableArray<IParameterSymbol>? parameters = item is AbstractSignatureHelpProvider.SymbolKeySignatureHelpItem { SymbolKey: { } key }
            ? key.Resolve(compilation, cancellationToken: cancellationToken).Symbol switch
            {
                IMethodSymbol method => method.Parameters,
                IPropertySymbol indexer => indexer.Parameters,
                INamedTypeSymbol { DelegateInvokeMethod: { } invoke } => invoke.Parameters,
                _ => null,
            }
            : null;
        return parameters is { } known && known.Length == item.Parameters.Length ? [.. known] : null;
    }

    private static (bool Fits, int Parameter) Fit(SemanticModel model, IParameterSymbol[] parameters, Argument[] arguments, int at, string? named)
    {
        int filled = -1;
        for (int k = 0; k < arguments.Length; k++)
        {
            (string? name, ExpressionSyntax? value, ITypeSymbol? type) = arguments[k];
            name ??= k == at ? named : null;
            int place = name is not null ? Array.FindIndex(parameters, p => p.Name == name)
                : k < parameters.Length ? k
                : parameters.Length > 0 && parameters[^1].IsParams ? parameters.Length - 1
                : -1;
            if (place < 0)
            {
                return (false, -1);
            }
            if (k == at)
            {
                filled = place;
            }
            if (type is null or { TypeKind: TypeKind.Error } || !Fixed(parameters[place].Type))
            {
                continue;
            }
            ITypeSymbol wanted = parameters[place].Type;
            if (!Converts(model, value, type, wanted)
                && !(parameters[place].IsParams && ElementOf(model.Compilation, wanted) is { } element && Converts(model, value, type, element)))
            {
                return (false, -1);
            }
        }
        return (true, filled);
    }

    /// <summary>Whether an argument converts to a type implicitly: as written where it is passed as a value, an interpolated
    /// string to any handler of one, else by its type.</summary>
    private static bool Converts(SemanticModel model, ExpressionSyntax? value, ITypeSymbol type, ITypeSymbol to) =>
        value is not null
            ? model.ClassifyConversion(value, to).IsImplicit || (value is InterpolatedStringExpressionSyntax && to.GetAttributes().Any(IsHandlerMark))
            : model.Compilation.ClassifyCommonConversion(type, to).IsImplicit;

    /// <summary>Whether an attribute marks its type an interpolated string handler.</summary>
    private static bool IsHandlerMark(AttributeData attribute) =>
        attribute.AttributeClass?.ToDisplayString() == "System.Runtime.CompilerServices.InterpolatedStringHandlerAttribute";

    /// <summary>What a <c>params</c> list of a type takes each of: an array's elements, a span's, or what the collection
    /// enumerates; null for a type that enumerates nothing it names.</summary>
    private static ITypeSymbol? ElementOf(Compilation compilation, ITypeSymbol list) => list switch
    {
        IArrayTypeSymbol array => array.ElementType,
        INamedTypeSymbol { TypeArguments: [var element] } named when SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, compilation.GetTypeByMetadataName("System.Span`1"))
            || SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, compilation.GetTypeByMetadataName("System.ReadOnlySpan`1"))
            || named.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T => element,
        INamedTypeSymbol named => named.AllInterfaces.FirstOrDefault(i => i.OriginalDefinition.SpecialType == SpecialType.System_Collections_Generic_IEnumerable_T)?.TypeArguments[0],
        _ => null,
    };

    /// <summary>Whether a type is known as it stands, none of it a type parameter still to be inferred.</summary>
    private static bool Fixed(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => false,
        IArrayTypeSymbol array => Fixed(array.ElementType),
        IPointerTypeSymbol pointer => Fixed(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.All(Fixed),
        _ => true,
    };
}
