using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>Expression Type Info: the type of the innermost expression at a caret (the one a caret just after a name or a
/// closing bracket ends), as C# writes it there; none where no expression with a type stands there.</summary>
public sealed class ExpressionType : IExtensionDocumentMessageHandler<Caret, ExpressionTyped>
{
    public async Task<ExpressionTyped> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken)
    {
        Document document = context.Document();
        SourceText text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
        int position = text.Lines.GetPosition(new LinePosition(message.At.Line, message.At.Character));
        if (await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false) is not { } root
            || await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false) is not { } model)
        {
            return new ExpressionTyped(null);
        }
        SyntaxToken token = root.FindToken(Math.Min(position, Math.Max(0, root.FullSpan.End - 1)));
        if (token.SpanStart >= position && token.GetPreviousToken() is { } before && before.Span.End == position
            && (before.IsKind(SyntaxKind.IdentifierToken) || before.IsKind(SyntaxKind.CloseParenToken) || before.IsKind(SyntaxKind.CloseBracketToken)
                || before.IsKind(SyntaxKind.NumericLiteralToken) || before.IsKind(SyntaxKind.StringLiteralToken)))
        {
            token = before;
        }
        for (SyntaxNode? node = token.Parent; node is not null and not StatementSyntax and not MemberDeclarationSyntax; node = node.Parent)
        {
            if (node is ExpressionSyntax expression && model.GetTypeInfo(expression, cancellationToken).Type is { } type and not IErrorTypeSymbol)
            {
                return new ExpressionTyped(type.ToMinimalDisplayString(model, position));
            }
        }
        return new ExpressionTyped(null);
    }
}
