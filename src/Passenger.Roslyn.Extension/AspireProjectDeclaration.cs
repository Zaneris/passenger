using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Extensions;

namespace Passenger.Roslyn.Extension;

/// <summary>Aspire's Go to Declaration: AddProject's actual project rather than its generated Projects type, else the AppHost's
/// declaration of the resource a service names in a string (a connection string's name, a service it discovers).</summary>
public sealed class AspireProjectDeclaration : IExtensionDocumentMessageHandler<Caret, AspireProjectTarget>
{
    public async Task<AspireProjectTarget> ExecuteAsync(Caret message, ExtensionDocumentMessageContext context, CancellationToken cancellationToken) =>
        await ProjectAsync(message, context.Document(), cancellationToken).ConfigureAwait(false) is { Path: not null } project ? project
            : await AspireSource.DeclarationAsync(context.Document(), message.At, cancellationToken).ConfigureAwait(false);

    private static async Task<AspireProjectTarget> ProjectAsync(Caret message, Document document, CancellationToken cancellationToken)
    {
        SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return new AspireProjectTarget(null);
        }
        int offset = await Changes.OffsetAsync(document, message.At, cancellationToken).ConfigureAwait(false);
        SyntaxNode node = root.FindToken(Math.Clamp(offset, 0, Math.Max(0, root.FullSpan.End - 1))).Parent!;
        foreach (InvocationExpressionSyntax invocation in node.AncestorsAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method
                || method.Name != "AddProject"
                || method.ContainingType.ToDisplayString() != "Aspire.Hosting.ProjectResourceBuilderExtensions"
                || method.ContainingAssembly.Name != "Aspire.Hosting")
            {
                continue;
            }
            if (method.TypeArguments is [INamedTypeSymbol projectType])
            {
                GenericNameSyntax? generic = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax { Name: GenericNameSyntax name } => name,
                    GenericNameSyntax name => name,
                    _ => null,
                };
                if (generic is null || !generic.Span.Contains(offset))
                {
                    return new AspireProjectTarget(null);
                }
                return new AspireProjectTarget(await MetadataPathAsync(projectType, model.Compilation, cancellationToken).ConfigureAwait(false));
            }
            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (!argument.Span.Contains(offset))
                {
                    continue;
                }
                IParameterSymbol? parameter = Parameter(method, invocation, argument);
                if (parameter?.Name == "projectPath" && model.GetConstantValue(argument.Expression, cancellationToken) is { HasValue: true, Value: string path })
                {
                    return new AspireProjectTarget(Path.IsPathFullyQualified(path) ? path
                        : await AspireSource.ProjectPathAsync(document, invocation, model, cancellationToken).ConfigureAwait(false));
                }
            }
            return new AspireProjectTarget(null);
        }
        return new AspireProjectTarget(null);
    }

    internal static IParameterSymbol? Parameter(IMethodSymbol method, InvocationExpressionSyntax invocation, ArgumentSyntax argument)
    {
        if (argument.NameColon is { } named)
        {
            return method.Parameters.FirstOrDefault(p => p.Name == named.Name.Identifier.ValueText);
        }
        int index = invocation.ArgumentList.Arguments.IndexOf(argument);
        return index < method.Parameters.Length ? method.Parameters[index] : null;
    }

    internal static async Task<string?> MetadataPathAsync(INamedTypeSymbol type, Compilation compilation, CancellationToken cancel)
    {
        INamedTypeSymbol? metadata = compilation.GetTypeByMetadataName("Aspire.Hosting.IProjectMetadata");
        IPropertySymbol? contract = metadata?.GetMembers("ProjectPath").OfType<IPropertySymbol>().SingleOrDefault();
        if (contract is null || type.FindImplementationForInterfaceMember(contract) is not IPropertySymbol property)
        {
            return null;
        }
        foreach (SyntaxReference reference in property.DeclaringSyntaxReferences)
        {
            if (await reference.GetSyntaxAsync(cancel).ConfigureAwait(false) is not PropertyDeclarationSyntax declaration)
            {
                continue;
            }
            if (declaration.AccessorList?.Accessors.Any(a => a.Keyword.ValueText != "get") == true)
            {
                continue;
            }
            AccessorDeclarationSyntax? getter = declaration.AccessorList?.Accessors.FirstOrDefault(a => a.Keyword.ValueText == "get");
            ExpressionSyntax? expression = declaration.ExpressionBody?.Expression ?? declaration.Initializer?.Value
                ?? getter?.ExpressionBody?.Expression;
            if (expression is null && getter?.Body?.Statements is [ReturnStatementSyntax returned])
            {
                expression = returned.Expression;
            }
            if (expression is not null
                && compilation.GetSemanticModel(expression.SyntaxTree).GetConstantValue(expression, cancel) is { HasValue: true, Value: string path }
                && Path.IsPathFullyQualified(path))
            {
                return path;
            }
        }
        return null;
    }
}
