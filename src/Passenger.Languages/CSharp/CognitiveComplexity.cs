using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>A function's score and the source lines that contribute to it. Offsets and lines are for the parsed snapshot.</summary>
public sealed record FunctionComplexity(string Name, int Offset, int RowOffset, int Score, IReadOnlyList<ComplexityLine> Lines);

/// <summary>The sum of the increments on a zero-based line.</summary>
public readonly record struct ComplexityLine(int Line, int Points);

/// <summary>Each C# function's cognitive complexity read from a parse, by the Cognitive Complexity white paper's rules: a
/// break in the flow (a branch, a loop, a catch, a jump, recursion) adds one, more for each level it nests in, and each run
/// of one logical operator adds one.</summary>
public static class CognitiveComplexity
{
    public static IReadOnlyList<FunctionComplexity> Of(ParsedText parsed, CancellationToken cancel = default)
    {
        if (parsed.Root is not CompilationUnitSyntax unit)
        {
            return [];
        }

        TextSnapshot text = parsed.Text;
        List<FunctionComplexity> functions = [];
        GlobalStatementSyntax[] globals = [.. unit.Members.OfType<GlobalStatementSyntax>()];
        if (globals.Length > 0)
        {
            SyntaxToken start = globals[0].GetFirstToken();
            Add("Top-level code", start, start, Score(text, globals, default, cancel));
        }
        foreach (SyntaxNode node in unit.DescendantNodes())
        {
            cancel.ThrowIfCancellationRequested();
            (SyntaxToken name, SyntaxNode? body, string? caption) = node switch
            {
                MethodDeclarationSyntax method => (method.Identifier, Body(method), null),
                ConstructorDeclarationSyntax constructor => (constructor.Identifier, Body(constructor), null),
                DestructorDeclarationSyntax destructor => (destructor.Identifier, Body(destructor), $"~{destructor.Identifier.ValueText}"),
                OperatorDeclarationSyntax op => (op.OperatorToken, Body(op), $"operator {op.OperatorToken.Text}"),
                ConversionOperatorDeclarationSyntax conversion => (conversion.OperatorKeyword, Body(conversion), $"{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}"),
                LocalFunctionStatementSyntax local => (local.Identifier, (SyntaxNode?)local.Body ?? local.ExpressionBody, null),
                AccessorDeclarationSyntax accessor => (accessor.Keyword, (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody, null),
                PropertyDeclarationSyntax { ExpressionBody: { } expression } property => (property.Identifier, expression, null),
                IndexerDeclarationSyntax { ExpressionBody: { } expression } indexer => (indexer.ThisKeyword, expression, "this[]"),
                LambdaExpressionSyntax lambda => (lambda.ArrowToken, lambda.Body, "lambda"),
                AnonymousMethodExpressionSyntax anonymous => (anonymous.DelegateKeyword, anonymous.Block, "delegate"),
                TypeDeclarationSyntax { ParameterList: { } } type => (type.Identifier,
                    type.BaseList?.Types.OfType<PrimaryConstructorBaseTypeSyntax>().FirstOrDefault(), $"{type.Identifier.ValueText} constructor"),
                _ => default,
            };
            if (name.RawKind == 0 || name.IsMissing)
            {
                continue;
            }
            List<SyntaxNode> parts = [];
            if (node is ConstructorDeclarationSyntax { Initializer: { } initializer })
            {
                parts.Add(initializer);
            }
            if (body is not null)
            {
                parts.Add(body);
            }
            (string? Name, int Arity) recursion = node switch
            {
                MethodDeclarationSyntax method => (name.ValueText, method.ParameterList.Parameters.Count),
                LocalFunctionStatementSyntax local => (name.ValueText, local.ParameterList.Parameters.Count),
                _ => default,
            };
            Add(caption ?? name.ValueText, name, node.GetFirstToken(), Score(text, parts, recursion, cancel));
        }
        return functions;

        static SyntaxNode? Body(BaseMethodDeclarationSyntax method) => (SyntaxNode?)method.Body ?? method.ExpressionBody;

        void Add(string name, SyntaxToken anchor, SyntaxToken start, IReadOnlyList<ComplexityLine> lines)
        {
            int line = text.GetLineFromOffset(start.SpanStart);
            int rowOffset = TextNavigation.IndentEnd(text, text.GetLine(line));
            functions.Add(new FunctionComplexity(name, anchor.SpanStart, rowOffset, lines.Sum(l => l.Points), lines));
        }
    }

    private static IReadOnlyList<ComplexityLine> Score(TextSnapshot text, IReadOnlyList<SyntaxNode> parts, (string? Name, int Arity) recursion,
        CancellationToken cancel)
    {
        if (parts.Count == 0)
        {
            return [];
        }
        Dictionary<int, int> increments = [];
        Stack<(SyntaxNode Node, int Depth)> pending = new();
        for (int i = parts.Count - 1; i >= 0; i--)
        {
            pending.Push((parts[i], 0));
        }

        bool recursive = false;
        while (pending.TryPop(out (SyntaxNode Node, int Depth) next))
        {
            cancel.ThrowIfCancellationRequested();
            (SyntaxNode node, int depth) = next;
            SyntaxToken branching = node switch
            {
                IfStatementSyntax statement when statement.Parent is not ElseClauseSyntax => statement.IfKeyword,
                SwitchStatementSyntax statement => statement.SwitchKeyword,
                SwitchExpressionSyntax expression => expression.SwitchKeyword,
                ConditionalExpressionSyntax expression => expression.QuestionToken,
                ForStatementSyntax statement => statement.ForKeyword,
                CommonForEachStatementSyntax statement => statement.ForEachKeyword,
                WhileStatementSyntax statement => statement.WhileKeyword,
                DoStatementSyntax statement => statement.DoKeyword,
                CatchClauseSyntax clause => clause.CatchKeyword,
                _ => default,
            };
            if (branching.RawKind != 0)
            {
                Count(branching, depth + 1);
                depth++;
            }
            else if (node is ElseClauseSyntax otherwise)
            {
                Count(otherwise.ElseKeyword, 1);
            }
            else if (node is GotoStatementSyntax jump)
            {
                Count(jump.GotoKeyword, depth + 1);
            }
            else if (Logical(node) is { } logical && !PreviousOperator(node, logical.Left).IsKind(logical.Op.Kind()))
            {
                Count(logical.Op, 1);
            }
            else if (!recursive && recursion.Name is not null && node is InvocationExpressionSyntax call
                && call.ArgumentList.Arguments.Count == recursion.Arity && CalledName(call) is { } called
                && string.Equals(called.Identifier.ValueText, recursion.Name, StringComparison.Ordinal))
            {
                Count(called.Identifier, 1);
                recursive = true;
            }

            // A static local function cannot capture the containing function's context.
            if (node is LocalFunctionStatementSyntax local && local.Modifiers.Any(SyntaxKind.StaticKeyword))
            {
                continue;
            }
            if (node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax)
            {
                depth++;
            }
            foreach (SyntaxNodeOrToken child in node.ChildNodesAndTokens().Reverse())
            {
                if (child.AsNode() is { } syntax)
                {
                    pending.Push((syntax, depth));
                }
            }
        }
        return [.. increments.OrderBy(entry => entry.Key).Select(entry => new ComplexityLine(entry.Key, entry.Value))];

        void Count(SyntaxToken token, int points)
        {
            if (!token.IsMissing)
            {
                int line = text.GetLineFromOffset(token.SpanStart);
                increments[line] = increments.GetValueOrDefault(line) + points;
            }
        }
    }

    private static SimpleNameSyntax? CalledName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } member => member.Name,
        _ => null,
    };

    private static (SyntaxNode Left, SyntaxToken Op, SyntaxNode Right)? Logical(SyntaxNode node) => node switch
    {
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.LogicalAndExpression) || binary.IsKind(SyntaxKind.LogicalOrExpression) =>
            (binary.Left, binary.OperatorToken, binary.Right),
        BinaryPatternSyntax pattern => (pattern.Left, pattern.OperatorToken, pattern.Right),
        _ => null,
    };

    /// <summary>Finds the preceding operator in source order within the same logical expression or pattern.</summary>
    private static SyntaxToken PreviousOperator(SyntaxNode node, SyntaxNode left)
    {
        SyntaxToken preceding = default;
        while (Logical(Unwrap(left)) is { } logical)
        {
            preceding = logical.Op;
            left = logical.Right;
        }
        if (preceding.RawKind != 0)
        {
            return preceding;
        }
        for (SyntaxNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is ParenthesizedExpressionSyntax or ParenthesizedPatternSyntax)
            {
                node = parent;
                continue;
            }
            if (Logical(parent) is not { } outer)
            {
                break;
            }
            if (outer.Right == node)
            {
                return outer.Op;
            }
            node = parent;
        }
        return default;
    }

    private static SyntaxNode Unwrap(SyntaxNode node)
    {
        while (true)
        {
            switch (node)
            {
                case ParenthesizedExpressionSyntax expression:
                    node = expression.Expression;
                    break;
                case ParenthesizedPatternSyntax pattern:
                    node = pattern.Pattern;
                    break;
                default:
                    return node;
            }
        }
    }
}
