using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>Smart Type Completion's names: the type C# expects where the caret's name goes (a value assigned, an argument,
/// a return), as Roslyn's own inference finds it, and the names in reach there whose type fits it: locals, parameters,
/// fields, properties, methods by what they return, after a dot the members of what stands before it, after <c>new</c> the
/// types that convert to it, an enum's type for its members, and the literals that fit (true, false, null).</summary>
public sealed class TypeFits : IExtensionDocumentMessageHandler<Caret, FittingNames>
{
    public async Task<FittingNames> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(message.At.Line, message.At.Character));
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model
            || document.Project.Services.GetService<ITypeInferenceService>() is not { } inference)
        {
            return new FittingNames(null, []);
        }
        // The name begun at the caret, and what stands before its dot where it names a member.
        SyntaxToken left = root.FindToken(Math.Min(position, Math.Max(0, root.FullSpan.End - 1)));
        if (left.SpanStart >= position)
        {
            left = left.GetPreviousToken();
        }
        int start = position;
        if (left.IsKind(SyntaxKind.IdentifierToken) && left.Span.End == position)
        {
            start = left.SpanStart;
            left = left.GetPreviousToken();
        }
        ExpressionSyntax? before = left.IsKind(SyntaxKind.DotToken) && left.Parent is MemberAccessExpressionSyntax access ? access.Expression : null;
        bool afterNew = left.IsKind(SyntaxKind.NewKeyword);
        ImmutableArray<ITypeSymbol> expected = [.. inference.InferTypes(model, before?.SpanStart ?? start, null, cancellationToken).Where(t => t is not IErrorTypeSymbol).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default)];
        if (expected.IsEmpty)
        {
            return new FittingNames(null, []);
        }
        INamespaceOrTypeSymbol? container = null;
        bool ofType = false;
        if (before is not null)
        {
            if (model.GetSymbolInfo(before, cancellationToken).Symbol is INamespaceOrTypeSymbol named)
            {
                container = named;
                ofType = true;
            }
            else
            {
                container = model.GetTypeInfo(before, cancellationToken).Type;
            }
            if (container is null)
            {
                return new FittingNames(Shown(expected[0], model, start), []);
            }
        }
        ImmutableArray<ISymbol> inReach = container is null
            ? model.LookupSymbols(start)
            : model.LookupSymbols(start, container, includeReducedExtensionMethods: true);
        List<string> names = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ISymbol symbol in inReach)
        {
            if (before is not null && !ofType && symbol.IsStatic && symbol is not IMethodSymbol { MethodKind: MethodKind.ReducedExtension })
            {
                continue;
            }
            if (ofType && !symbol.IsStatic && symbol is not INamedTypeSymbol)
            {
                continue;
            }
            ITypeSymbol? type = symbol switch
            {
                ILocalSymbol local => local.Type,
                IParameterSymbol parameter => parameter.Type,
                IFieldSymbol field => field.Type,
                IPropertySymbol property => property.Type,
                IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ReducedExtension, ReturnsVoid: false } method => method.ReturnType,
                INamedTypeSymbol made when afterNew && !made.IsAbstract && made.TypeKind is TypeKind.Class or TypeKind.Struct => made,
                _ => null,
            };
            if (type is not null && Fits(type) && seen.Add(symbol.Name))
            {
                names.Add(symbol.Name);
            }
            else if (before is null && symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType && expected.Contains(enumType, SymbolEqualityComparer.Default) && seen.Add(enumType.Name))
            {
                names.Add(enumType.Name);
            }
        }
        if (before is null && !afterNew)
        {
            if (expected.Any(t => t.SpecialType == SpecialType.System_Boolean))
            {
                names.AddRange(["true", "false"]);
            }
            if (expected.Any(t => t.IsReferenceType || t.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T))
            {
                names.Add("null");
            }
        }
        return new FittingNames(Shown(expected[0], model, start), names);

        bool Fits(ITypeSymbol type) => expected.Any(wanted => model.Compilation.ClassifyCommonConversion(type, wanted).IsImplicit);
    }

    private static string Shown(ITypeSymbol type, SemanticModel model, int position) => type.ToMinimalDisplayString(model, position);
}
