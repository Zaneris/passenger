using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>The aliases the parameter list at a caret names, as the compiler binds them: a file's own, a namespace's, and
/// those every file of the project takes (<c>global using Key = System.Guid;</c>, the SDK's for its Using items among them),
/// each by its name with the type it stands for written whole, so a method breakpoint names its parameters' types as the
/// debugger matches them. None where the caret is in no parameter list.</summary>
public sealed class ParameterAliases : IExtensionDocumentMessageHandler<Caret, AliasesFound>
{
    private static readonly SymbolDisplayFormat Whole = SymbolDisplayFormat.FullyQualifiedFormat;

    public async Task<AliasesFound> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(message.At.Line, message.At.Character));
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
            || root.FindToken(position).Parent?.FirstAncestorOrSelf<ParameterListSyntax>() is not { } parameters
            || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return new AliasesFound([]);
        }
        Dictionary<string, string> found = new(StringComparer.Ordinal);
        foreach (IdentifierNameSyntax name in parameters.Parameters.Where(p => p.Type is not null).SelectMany(p => p.Type!.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()))
        {
            if (model.GetAliasInfo(name, cancellationToken) is { Target: ITypeSymbol target } alias)
            {
                found.TryAdd(alias.Name, target.ToDisplayString(Whole));
            }
        }
        return new AliasesFound([.. found.Select(pair => new AliasFound(pair.Key, pair.Value))]);
    }
}
