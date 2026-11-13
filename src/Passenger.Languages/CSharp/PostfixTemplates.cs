using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>A postfix template: typed after an expression and a dot, it wraps the expression. Its name, its description
/// and what it writes; a statement's one takes an expression that is its whole statement; <c>$EXPR$</c> is the
/// expression's text, the rest live template syntax.</summary>
public sealed record PostfixTemplate(string Name, string Description, string Example, bool Statement, string Text, IReadOnlyDictionary<string, string> Defaults)
{
    /// <summary>What the list that asks which expression it wraps is titled.</summary>
    public string SelectTitle { get; init; } = "Select expression";
}

/// <summary>An expression a postfix template could wrap: where it starts, its text up to the dot, and the name .var
/// suggests for it.</summary>
public readonly record struct PostfixExpression(int Start, string Text, string Name);

/// <summary>What a postfix template may wrap: every expression ending at the dot, the innermost first (<c>c</c>, then
/// <c>a.b + c</c>), which an expression's template asks the person to choose from when there are several; a statement's
/// template takes the outermost, and only when it is all of its statement.</summary>
public sealed record PostfixTarget(IReadOnlyList<PostfixExpression> Expressions, bool WholeStatement)
{
    public PostfixExpression Outermost => Expressions[^1];
}

/// <summary>The C# postfix templates, the everyday ones, and the expression before a dot they would wrap.</summary>
public static class PostfixTemplates
{
    private static Dictionary<string, string> D(params (string Name, string Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);

    /// <summary>Each with Rider's description, but for and forr, which count to the expression's value here rather than
    /// over a collection's length.</summary>
    public static IReadOnlyList<PostfixTemplate> All { get; } =
    [
        new("if", "Checks boolean expression to be 'true'", "if (expr)", true, "if ($EXPR$)\n{\n\t$END$\n}", D()),
        new("null", "Checks expression to be null", "if (expr == null)", true, "if ($EXPR$ == null)\n{\n\t$END$\n}", D()),
        new("notnull", "Checks expression to be not-null", "if (expr != null)", true, "if ($EXPR$ != null)\n{\n\t$END$\n}", D()),
        new("var", "Introduces variable for expression", "var x = expr;", true, "var $NAME$ = $EXPR$;$END$", D(("NAME", "$name"))),
        new("new", "Creates an instance", "new Type()", false, "new $EXPR$($END$)", D()),
        new("foreach", "Iterates over enumerable collection", "foreach (var x in expr)", true, "foreach (var $VARIABLE$ in $EXPR$)\n{\n\t$END$\n}", D(("VARIABLE", "item"))),
        new("for", "Iterates with index up to expression", "for (var i = 0; i < expr; i++)", true, "for (var $INDEX$ = 0; $INDEX$ < $EXPR$; $INDEX$++)\n{\n\t$END$\n}", D(("INDEX", "i"))),
        new("forr", "Iterates in reverse with index from expression", "for (var i = expr - 1; i >= 0; i--)", true,
            "for (var $INDEX$ = $EXPR$ - 1; $INDEX$ >= 0; $INDEX$--)\n{\n\t$END$\n}", D(("INDEX", "i"))),
        new("while", "Iterating while boolean statement is 'true'", "while (expr)", true, "while ($EXPR$)\n{\n\t$END$\n}", D()),
        new("return", "Returns expression from current function", "return expr;", true, "return $EXPR$;$END$", D()),
        new("throw", "Throws expression of 'Exception' type", "throw expr;", true, "throw $EXPR$;$END$", D()),
        new("using", "Wraps resource with using statement", "using (expr)", true, "using ($EXPR$)\n{\n\t$END$\n}", D()),
        new("lock", "Surrounds expression with lock block", "lock (expr)", true, "lock ($EXPR$)\n{\n\t$END$\n}", D()),
        new("switch", "Produces switch statement", "switch (expr)", true, "switch ($EXPR$)\n{\n\t$END$\n}", D()),
        new("not", "Negates boolean expression", "!expr", false, "!$EXPR$$END$", D()) { SelectTitle = "Select expression to invert" },
        new("par", "Parenthesizes current expression", "(expr)", false, "($EXPR$)$END$", D()) { SelectTitle = "Select expression to parenthesize" },
        new("await", "Awaits expressions of 'Task' type", "await expr", false, "await $EXPR$$END$", D()),
        new("arg", "Surrounds expression with invocation", "Method(expr)", false, "$METHOD$($EXPR$)$END$", D(("METHOD", "Method")))
            { SelectTitle = "Select argument expression" },
    ];

    /// <summary>The expressions a template typed after a dot could wrap: those that end at the dot, the innermost first,
    /// read from the text with the dot and the half-typed name taken out and the statement ended there, which the file's
    /// own tree is carried to by an incremental parse of that one change. A member's name alone is none (<c>b</c> of
    /// <c>a.b</c>), nor is what follows a conditional access's <c>?</c>.</summary>
    public static PostfixTarget? Before(ParsedText syntax, int dot, int caret)
    {
        TextSnapshot text = syntax.Text;
        if (dot <= 0 || dot >= text.Length || caret < dot || caret > text.Length || text[dot] != '.')
        {
            return null;
        }
        SourceText ended = syntax.Tree.GetText().WithChanges(new TextChange(TextSpan.FromBounds(dot, caret), ";"));
        SyntaxNode root = CSharpParsing.Changed(syntax.Tree, ended).GetRoot();
        SyntaxToken last = root.FindToken(dot - 1);
        if (last.Span.End != dot || last.Parent is null)
        {
            return null;
        }
        SyntaxNode? start = last.Parent;
        // A call's closing parenthesis belongs to its argument list, which is no expression; the call is one.
        while (start is not null and not ExpressionSyntax && start.Span.End == dot)
        {
            start = start.Parent;
        }
        List<PostfixExpression> found = [];
        ExpressionSyntax? outermost = null;
        for (SyntaxNode? node = start; node is ExpressionSyntax expression && node.Span.End == dot; node = node.Parent)
        {
            outermost = expression;
            if (!expression.IsMissing && Stands(expression) && (found.Count == 0 || found[^1].Start != expression.SpanStart))
            {
                found.Add(new PostfixExpression(expression.SpanStart, text.GetText(expression.SpanStart, dot - expression.SpanStart), NameFor(expression)));
            }
        }
        if (outermost is null || outermost.IsMissing || found.Count == 0 || found[^1].Start != outermost.SpanStart)
        {
            return null;
        }
        bool whole = outermost.Parent is ExpressionStatementSyntax statement && statement.Expression == outermost;
        return new PostfixTarget(found, whole);
    }

    /// <summary>Whether an expression stands on its own: not a member's name after its dot, nor what follows a
    /// conditional access's <c>?</c>.</summary>
    private static bool Stands(ExpressionSyntax expression) => expression switch
    {
        MemberBindingExpressionSyntax or ElementBindingExpressionSyntax => false,
        SimpleNameSyntax name when name.Parent is MemberAccessExpressionSyntax access && access.Name == name => false,
        SimpleNameSyntax name when name.Parent is QualifiedNameSyntax qualified && qualified.Right == name => false,
        _ when expression.Parent is ConditionalAccessExpressionSyntax conditional && conditional.WhenNotNull == expression => false,
        _ => true,
    };

    /// <summary>The name .var suggests: an invocation's or a property's name without its Get, lower-cased at the
    /// start, a created type's name, else "value".</summary>
    private static string NameFor(ExpressionSyntax expression)
    {
        string? name = expression switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } => access.Name.Identifier.ValueText,
            InvocationExpressionSyntax { Expression: IdentifierNameSyntax identifier } => identifier.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            ObjectCreationExpressionSyntax { Type: var type } => type.ToString().Split('<')[0].Split('.')[^1],
            _ => null,
        };
        if (string.IsNullOrEmpty(name))
        {
            return "value";
        }
        if (name.StartsWith("Get", StringComparison.Ordinal) && name.Length > 3 && char.IsUpper(name[3]))
        {
            name = name[3..];
        }
        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
