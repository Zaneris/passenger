using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Passenger.Debugger.ExpressionEvaluator.Compiler;

/// <summary>An expression on a value of a generic type, written again to read the value at its exact type. The compiler reads
/// such an expression inside the type's definition, where a member typed by a type parameter offers only what the parameter
/// promises, so Held.Length on a Box&lt;int[]&gt; finds no Length on T; through a cast to the construction, Held is an int[].
/// The compiler can't be given the construction itself: its scope holds a type's arguments as type parameters alone.</summary>
internal static class ExactFragment
{
    /// <summary><paramref name="expression"/> with <c>this</c> and each member of the value it names by itself reached through
    /// <paramref name="exactType"/>, which any scope can name; null where it names none or doesn't parse. A name the expression
    /// declares itself (a lambda's parameter, a pattern's variable) stays that, and a name in a type's place, a pattern, after
    /// a dot or naming an argument or an initialized member is no member of the value.</summary>
    public static string? Of(string expression, string exactType, IReadOnlySet<string> instanceMembers, IReadOnlySet<string> staticMembers)
    {
        ExpressionSyntax fragment = SyntaxFactory.ParseExpression(expression);
        if (fragment.ContainsDiagnostics)
        {
            return null;
        }
        string value = $"(({exactType})(object)this)";
        HashSet<string> declared = [.. fragment.DescendantNodes().Select(Declared).OfType<string>()];
        SyntaxNode[] reads = [.. fragment.DescendantNodesAndSelf().Where(node => node is ThisExpressionSyntax
            || (node is SimpleNameSyntax name && !declared.Contains(name.Identifier.ValueText) && ReadsTheValue(name)
                && (instanceMembers.Contains(name.Identifier.ValueText) || staticMembers.Contains(name.Identifier.ValueText))))];
        return reads.Length == 0 ? null : fragment.ReplaceNodes(reads, (node, _) => SyntaxFactory.ParseExpression(Through(node)).WithTriviaFrom(node)).ToFullString();

        string Through(SyntaxNode node) => node is SimpleNameSyntax name
            ? $"{(instanceMembers.Contains(name.Identifier.ValueText) ? value : exactType)}.{name.WithoutTrivia().ToFullString()}"
            : value;
    }

    private static bool ReadsTheValue(SimpleNameSyntax name) => name.FirstAncestorOrSelf<PatternSyntax>() is null && name.Parent switch
    {
        MemberAccessExpressionSyntax access => access.Expression == name,
        MemberBindingExpressionSyntax or NameColonSyntax or NameEqualsSyntax => false,
        AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax } member => member.Right == name,
        _ => !SyntaxFacts.IsInNamespaceOrTypeContext(name),
    };

    private static string? Declared(SyntaxNode node) => node switch
    {
        ParameterSyntax parameter => parameter.Identifier.ValueText,
        SingleVariableDesignationSyntax variable => variable.Identifier.ValueText,
        VariableDeclaratorSyntax local => local.Identifier.ValueText,
        FromClauseSyntax from => from.Identifier.ValueText,
        LetClauseSyntax let => let.Identifier.ValueText,
        JoinClauseSyntax join => join.Identifier.ValueText,
        JoinIntoClauseSyntax into => into.Identifier.ValueText,
        QueryContinuationSyntax continuation => continuation.Identifier.ValueText,
        _ => null,
    };
}
