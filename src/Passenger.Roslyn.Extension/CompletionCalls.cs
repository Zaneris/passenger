using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Text;

namespace Passenger.Roslyn.Extension;

/// <summary>Whether a method a completion writes is called where it goes (<see cref="CompletionCalls"/>).</summary>
public sealed class CompletionCall : IExtensionDocumentMessageHandler<CallAsked, CallFound>
{
    public Task<CallFound> ExecuteAsync(CallAsked message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        CompletionCalls.FindAsync(context.Document(), message, cancellationToken);
}

/// <summary>A completed method's call, read in the document as the completion's edits leave it: a call unless the name
/// is called already, given type arguments, named by nameof or converted to a delegate type the code gives (an event's
/// handler, a delegate parameter's argument), where it stays a method group.</summary>
internal static class CompletionCalls
{
    public static async Task<CallFound> FindAsync(Document document, CallAsked asked, CancellationToken cancel)
    {
        if (asked.Item < 0 || asked.Item >= asked.Edits.Count)
        {
            return CallFound.None;
        }
        SourceText text = await document.GetTextAsync(cancel).ConfigureAwait(false);
        List<TextChange> changes = [];
        int nameEnd = 0;
        int shift = 0;
        for (int i = 0; i < asked.Edits.Count; i++)
        {
            PlacedText edit = asked.Edits[i];
            int start = text.Lines.GetPosition(new LinePosition(edit.Start.Line, edit.Start.Character));
            int end = text.Lines.GetPosition(new LinePosition(edit.End.Line, edit.End.Character));
            if (end < start || changes.Count > 0 && start < changes[^1].Span.End)
            {
                return CallFound.None;
            }
            changes.Add(new TextChange(TextSpan.FromBounds(start, end), edit.Text));
            if (i == asked.Item)
            {
                nameEnd = start + shift + edit.Text.Length;
            }
            shift += edit.Text.Length - (end - start);
        }
        Document written = document.WithText(text.WithChanges(changes));
        SyntaxNode root = await written.GetSyntaxRootAsync(cancel).ConfigureAwait(false) ?? throw new InvalidOperationException($"{document.FilePath} has no syntax tree");
        if (nameEnd == 0 || root.FindToken(nameEnd - 1) is not { RawKind: (int)SyntaxKind.IdentifierToken } token || token.Span.End != nameEnd
            || token.Parent is not IdentifierNameSyntax name)
        {
            return CallFound.None;
        }
        ExpressionSyntax expression = name.Parent is MemberAccessExpressionSyntax access && access.Name == name ? access
            : name.Parent is MemberBindingExpressionSyntax binding ? binding
            : name;
        if (expression.Parent is InvocationExpressionSyntax invoked && invoked.Expression == expression || InNameof(expression))
        {
            return CallFound.None;
        }
        SemanticModel model = await written.GetSemanticModelAsync(cancel).ConfigureAwait(false) ?? throw new InvalidOperationException($"{document.FilePath} has no semantic model");
        ImmutableArray<IMethodSymbol> methods = [.. model.GetMemberGroup(expression, cancel).OfType<IMethodSymbol>()];
        if (methods.IsEmpty || model.GetTypeInfo(expression, cancel).ConvertedType is { TypeKind: TypeKind.Delegate } && !TakenByVar(expression))
        {
            return CallFound.None;
        }
        return new CallFound(true, methods.Any(m => m.Parameters.Length > 0));
    }

    /// <summary>Whether an expression is inside nameof's argument, which names a method without calling it.</summary>
    private static bool InNameof(ExpressionSyntax expression) =>
        expression.Ancestors().OfType<InvocationExpressionSyntax>().Any(call => call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
            && call.ArgumentList.Span.Contains(expression.Span));

    /// <summary>Whether a method group initializes a <c>var</c>, whose delegate type is the group's own rather than one the
    /// code asks for: a person writing <c>var x = Get</c> means its value.</summary>
    private static bool TakenByVar(ExpressionSyntax expression) =>
        expression.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax { Type.IsVar: true } } };
}
