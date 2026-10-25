using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Editor;
using Passenger.Languages.CSharp;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>Inline values: after each line of the stopped method, from its first line to the one it
/// stopped on, the value of each local and parameter that line names, read from the frame's variables. A name
/// that is a member of something else (the <c>count</c> of <c>list.count</c>) is not the local's.</summary>
public static class InlineValues
{
    /// <summary>The longest value shown in full; a longer one is cut.</summary>
    private const int Longest = 60;

    /// <summary>The values to show from the file's syntax, by the editor's lines (from zero).</summary>
    public static IReadOnlyList<LineAnnotation> For(ParsedText syntax, int stopLine, IReadOnlyDictionary<string, string> values)
    {
        TextSnapshot text = syntax.Text;
        if (values.Count == 0 || stopLine < 0 || stopLine >= text.LineCount)
        {
            return [];
        }
        SyntaxNode root = syntax.Root;
        LineSpan stop = text.GetLine(stopLine);
        string content = text.GetText(stop.Start, stop.Length);
        int position = stop.Start + (content.Length - content.TrimStart().Length);
        SyntaxNode? at = root.FindToken(Math.Min(position, Math.Max(0, root.FullSpan.End - 1))).Parent;
        SyntaxNode? body = at?.AncestorsAndSelf().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax
            or AccessorDeclarationSyntax or AnonymousFunctionExpressionSyntax);
        // Top-level statements are Main's body: from the first of them.
        int start = body?.SpanStart ?? root.ChildNodes().OfType<GlobalStatementSyntax>().FirstOrDefault()?.SpanStart ?? stop.Start;
        TextSpan span = TextSpan.FromBounds(Math.Min(start, stop.Start), stop.End);

        Dictionary<int, List<string>> named = [];
        foreach (SyntaxToken token in root.DescendantTokens(span))
        {
            if (!token.IsKind(SyntaxKind.IdentifierToken) || !values.ContainsKey(token.ValueText) || !NamesVariable(token))
            {
                continue;
            }
            int line = text.GetLineFromOffset(token.SpanStart);
            List<string> onLine = named.TryGetValue(line, out List<string>? list) ? list : named[line] = [];
            if (!onLine.Contains(token.ValueText))
            {
                onLine.Add(token.ValueText);
            }
        }
        return [.. named.OrderBy(p => p.Key).SelectMany(p => p.Value.Select(name =>
            new LineAnnotation(p.Key, $"{name}: {Cut(values[name])}", LineAnnotationLook.Muted)))];
    }

    /// <summary>A token that names a local or parameter: declaring one, or used alone (not as a member of
    /// something, not a method called by that name).</summary>
    private static bool NamesVariable(SyntaxToken token) => token.Parent switch
    {
        VariableDeclaratorSyntax or ForEachStatementSyntax or ParameterSyntax or SingleVariableDesignationSyntax or CatchDeclarationSyntax => true,
        IdentifierNameSyntax name => name.Parent switch
        {
            MemberAccessExpressionSyntax access => access.Expression == name,
            MemberBindingExpressionSyntax => false,
            InvocationExpressionSyntax invocation => invocation.Expression != name,
            NameColonSyntax or NameEqualsSyntax => false,
            QualifiedNameSyntax => false,
            _ => true,
        },
        _ => false,
    };

    private static string Cut(string value) => value.Length <= Longest ? value : value[..(Longest - 1)] + "…";
}
