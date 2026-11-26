using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Languages.CSharp;

namespace Passenger.Languages.WinForms;

/// <summary>A Windows Forms class's designer code as read from one file: its namespace, the namespaces its usings bring, its
/// fields with their types as written, where its InitializeComponent is named, and that method's statements in order;
/// with where each statement and each field's declaration lies (<see cref="DesignerLayout"/>), which a change is written by.</summary>
public sealed record DesignerSource(string? Namespace, IReadOnlyList<string> Usings, IReadOnlyList<DesignerField> Fields, int Method,
    IReadOnlyList<DesignerStatement> Statements)
{
    public DesignerLayout Layout { get; init; } = DesignerLayout.None;

    /// <summary>Where a field is made; the method's name for the class's root (an empty field); null for a field it does not make.</summary>
    public int? MadeAt(string field) => field.Length == 0 ? Method
        : Statements.FirstOrDefault(s => s.Act == DesignerAct.Create && s.Target == field)?.At;

    /// <summary>Where a component's property is set, by its name (a part of it set counts, as FlatAppearance.BorderSize does
    /// for FlatAppearance); null where nothing sets it.</summary>
    public int? SetAt(string field, string property) => Statements.FirstOrDefault(s => s.Act == DesignerAct.Assign && s.Target == field && s.Member is { } member
        && (member == property || (member.StartsWith(property, StringComparison.Ordinal) && member.Length > property.Length && member[property.Length] == '.')))?.At;
}

/// <summary>Where a class's designer code lies in its file: each statement's span (its first character to past its last), each
/// field's declaration's span with whether it declares that field alone and where its name is, the method's body between its braces, where a new
/// component's field goes in a class declaring none (past an <c>#endregion</c> after the method, else before the class's
/// closing brace), and whether the code qualifies the class's members with <c>this.</c>.</summary>
public sealed record DesignerLayout(IReadOnlyList<(int Start, int End)> Statements, IReadOnlyList<(int Start, int End, bool Alone, int Named)> Fields, int BodyStart, int BodyEnd,
    int FieldsEnd, bool Qualified)
{
    public static DesignerLayout None { get; } = new([], [], 0, 0, 0, false);
}

/// <summary>Windows Forms designer code read as its designer writes it, from the file's parse (<see cref="CSharpParsing"/>):
/// the class's fields, and each statement of its InitializeComponent by what it does to which of them, whether it qualifies
/// them with <c>this.</c> or not, with each value as the code writes it, its names left for the designer to resolve.</summary>
public static class DesignerCode
{
    /// <summary>The designer code of a class, by its full name (its namespace's and its own, a nested class's after a '+'),
    /// in a file's text; null where the file declares no InitializeComponent of that class.</summary>
    public static DesignerSource? Read(string text, string fullName)
    {
        CompilationUnitSyntax root = (CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot();
        foreach (ClassDeclarationSyntax type in Classes(root))
        {
            if (FullName(type) != fullName
                || type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0) is not { } method)
            {
                continue;
            }
            List<FieldDeclarationSyntax> declared = [.. type.Members.OfType<FieldDeclarationSyntax>()];
            List<DesignerField> fields = [.. declared.SelectMany(f => f.Declaration.Variables.Select(v => new DesignerField(v.Identifier.ValueText, Written(f.Declaration.Type))))];
            HashSet<string> named = [.. fields.Select(f => f.Name)];
            IReadOnlyList<StatementSyntax> statements = method.Body?.Statements ?? [];
            string? section = null;
            List<DesignerStatement> read = [.. statements.Select(s => Statement(s, named) with { Section = section = SectionAbove(s) ?? section })];
            int qualified = statements.Count(s => s is ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { Left: MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } } });
            DesignerLayout layout = new(
                [.. statements.Select(s => (s.SpanStart, s.Span.End))],
                [.. declared.SelectMany(f => f.Declaration.Variables.Select(v => (f.SpanStart, f.Span.End, f.Declaration.Variables.Count == 1, v.Identifier.SpanStart)))],
                method.Body is { } body ? body.OpenBraceToken.Span.End : method.Span.End,
                method.Body is { } closed ? closed.CloseBraceToken.SpanStart : method.Span.End,
                FieldsEnd(type, method),
                qualified * 2 > read.Count(s => s.Act is DesignerAct.Create or DesignerAct.Assign));
            return new DesignerSource(NamespaceOf(type), UsingsOf(type), fields, method.Identifier.SpanStart, read) { Layout = layout };
        }
        return null;
    }

    /// <summary>The component a section comment just above a statement names, as the designer writes one: three comment
    /// lines, the middle one the name; null where none stands there.</summary>
    private static string? SectionAbove(StatementSyntax statement)
    {
        List<string> comments = [.. statement.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)).Select(t => t.ToString()[2..].Trim())];
        return comments is [.., "", { Length: > 0 } name, ""] ? name : null;
    }

    /// <summary>The classes a file's text gives an InitializeComponent of no parameters, by their full names.</summary>
    public static IReadOnlyList<string> Designed(string text) =>
        [.. Classes((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot())
            .Where(type => type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0))
            .Select(FullName)];

    /// <summary>Where a file's text declares a method of a class (by its full name) of a name, any parameters: its name's offset
    /// and where its body's first line starts; null where it declares none.</summary>
    public static (int Named, int Body)? MethodOf(string text, string fullName, string method)
    {
        foreach (ClassDeclarationSyntax type in Classes((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Where(t => FullName(t) == fullName))
        {
            if (type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == method) is { } declared)
            {
                int body = declared.Body is { } block ? block.OpenBraceToken.Span.End : declared.Span.End;
                return (declared.Identifier.SpanStart, body);
            }
        }
        return null;
    }

    /// <summary>Where a file's text ends a class's last declaration there, by its full name: its closing brace's offset, and the
    /// indentation of its members (its first member's, else its brace's and four spaces); null where it declares none.</summary>
    public static (int Brace, string Indent)? ClassEnd(string text, string fullName)
    {
        if (Classes((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).LastOrDefault(t => FullName(t) == fullName) is not { CloseBraceToken.IsMissing: false } type)
        {
            return null;
        }
        int brace = type.CloseBraceToken.SpanStart;
        string indent = type.Members.FirstOrDefault() is { } first ? Passenger.Text.TextNavigation.IndentOf(text, first.SpanStart)
            : Passenger.Text.TextNavigation.IndentOf(text, brace) + "    ";
        return (brace, indent);
    }

    /// <summary>Whether a file's text declares a class, by its full name.</summary>
    public static bool Declares(string text, string fullName) => Classes((CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot()).Any(type => FullName(type) == fullName);

    /// <summary>The type a class derives from as one of its declarations in a file's text writes it, with the namespaces that
    /// file's usings bring; null where none of its declarations there names one.</summary>
    public static (string Base, IReadOnlyList<string> Usings)? BaseOf(string text, string fullName)
    {
        CompilationUnitSyntax root = (CompilationUnitSyntax)CSharpParsing.Parse(text).GetRoot();
        return Classes(root).FirstOrDefault(type => FullName(type) == fullName && type.BaseList is { Types.Count: > 0 }) is { BaseList: { } bases } declared
            ? (Written(bases.Types[0].Type), UsingsOf(declared))
            : null;
    }

    /// <summary>Where a new component's field goes in a class that declares none: past the end of the line of an
    /// <c>#endregion</c> after the method, as the designer writes it there, else before the class's closing brace.</summary>
    private static int FieldsEnd(ClassDeclarationSyntax type, MethodDeclarationSyntax method)
    {
        foreach (SyntaxTrivia trivia in type.DescendantTrivia(type.Span).Where(t => t.SpanStart > method.Span.End))
        {
            if (trivia.IsKind(SyntaxKind.EndRegionDirectiveTrivia))
            {
                return trivia.GetStructure() is EndRegionDirectiveTriviaSyntax region ? region.EndOfDirectiveToken.Span.End : trivia.Span.End;
            }
        }
        return type.CloseBraceToken.SpanStart;
    }

    private static IEnumerable<ClassDeclarationSyntax> Classes(CompilationUnitSyntax root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or ClassDeclarationSyntax).OfType<ClassDeclarationSyntax>();

    /// <summary>A class's full name as the runtime writes it: its namespaces' names, then its own, a nested one's after its
    /// holder's and a '+'.</summary>
    private static string FullName(ClassDeclarationSyntax type)
    {
        string name = type.Identifier.ValueText;
        for (SyntaxNode? at = type.Parent; at is not null; at = at.Parent)
        {
            name = at switch
            {
                ClassDeclarationSyntax holder => $"{holder.Identifier.ValueText}+{name}",
                BaseNamespaceDeclarationSyntax space => $"{space.Name}.{name}",
                _ => name,
            };
        }
        return name;
    }

    private static string? NamespaceOf(ClassDeclarationSyntax type)
    {
        List<string> names = [.. type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse()];
        return names.Count == 0 ? null : string.Join('.', names);
    }

    /// <summary>The namespaces the usings around a class bring, its file's and its namespaces', aliases and static ones left out.</summary>
    private static IReadOnlyList<string> UsingsOf(ClassDeclarationSyntax type) =>
        [.. type.Ancestors().SelectMany(n => n switch
        {
            CompilationUnitSyntax unit => unit.Usings,
            BaseNamespaceDeclarationSyntax space => space.Usings,
            _ => [],
        }).Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name is not null).Select(u => Plain(u.Name!.ToString()))];

    /// <summary>A type as the code writes it, a <c>global::</c> left out.</summary>
    private static string Written(TypeSyntax type) => Plain(type.ToString());

    private static string Plain(string written) => written.Replace("global::", string.Empty, StringComparison.Ordinal);

    private static DesignerStatement Statement(StatementSyntax statement, HashSet<string> fields)
    {
        switch (statement)
        {
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assigned }
                when Path(assigned.Left) is { } set:
                return (set.Count == 1 && fields.Contains(set[0])
                    ? new DesignerStatement(DesignerAct.Create, set[0], null, Start(assigned.Left))
                    : Of(DesignerAct.Assign, set, fields, Start(assigned.Left))) with { Value = Value(assigned.Right) };
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } hooked }
                when Path(hooked.Left) is { } eventPath:
                return Of(DesignerAct.Hook, eventPath, fields, Start(hooked.Left)) with { Value = Value(hooked.Right) };
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: var called } invoked } when Path(called) is { } method:
                return Of(DesignerAct.Call, method, fields, Start(called)) with { Arguments = [.. invoked.ArgumentList.Arguments.Select(a => Value(a.Expression))] };
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: var on, Name: var name } } invoked }
                when Inside(on) is CastExpressionSyntax { Expression: var cast } && Path(Inside(cast)) is { } target:
                // ((ISupportInitialize)(grid)).BeginInit(): the method of an interface the component implements.
                return Of(DesignerAct.Call, [.. target, name.Identifier.ValueText], fields, Start(Inside(cast))) with
                {
                    Arguments = [.. invoked.ArgumentList.Arguments.Select(a => Value(a.Expression))],
                };
        }
        // A local the code declares (a resource manager) is the designer's own: its name tells it from another, its value what it is.
        return statement is LocalDeclarationStatementSyntax { Declaration.Variables: [var local] }
            ? new DesignerStatement(DesignerAct.Other, local.Identifier.ValueText, null, statement.SpanStart) { Value = local.Initializer is { } made ? Value(made.Value) : null }
            : new DesignerStatement(DesignerAct.Other, string.Empty, null, statement.SpanStart);
    }

    /// <summary>An expression without the parentheses around it.</summary>
    private static ExpressionSyntax Inside(ExpressionSyntax expression)
    {
        ExpressionSyntax at = expression;
        while (at is ParenthesizedExpressionSyntax { Expression: var inner })
        {
            at = inner;
        }
        return at;
    }

    /// <summary>A value as the code writes it; one the designer does not write (a lambda, an initializer) is unread, its text kept.</summary>
    private static DesignerValue Value(ExpressionSyntax expression)
    {
        ExpressionSyntax at = Inside(expression);
        switch (at)
        {
            case LiteralExpressionSyntax literal:
                return Literal(literal);
            case ThisExpressionSyntax:
                return new DesignerValue(DesignerValueKinds.Name) { Name = "this" };
            case IdentifierNameSyntax or MemberAccessExpressionSyntax or QualifiedNameSyntax or AliasQualifiedNameSyntax when Dotted(at) is { } name:
                return new DesignerValue(DesignerValueKinds.Name) { Name = name };
            case ObjectCreationExpressionSyntax { Initializer: null } made:
                return new DesignerValue(DesignerValueKinds.New) { Type = Written(made.Type), Arguments = Values(made.ArgumentList) };
            case ImplicitObjectCreationExpressionSyntax { Initializer: null } made:
                return new DesignerValue(DesignerValueKinds.New) { Arguments = Values(made.ArgumentList) };
            case InvocationExpressionSyntax { Expression: var method } called when Dotted(method) is { } name:
                return new DesignerValue(DesignerValueKinds.Call) { Name = name, Arguments = Values(called.ArgumentList) };
            case ArrayCreationExpressionSyntax { Initializer: { } items } array:
                return new DesignerValue(DesignerValueKinds.Array) { Type = Written(array.Type.ElementType), Arguments = [.. items.Expressions.Select(Value)] };
            case ImplicitArrayCreationExpressionSyntax { Initializer: var items }:
                return new DesignerValue(DesignerValueKinds.Array) { Arguments = [.. items.Expressions.Select(Value)] };
            case CollectionExpressionSyntax collection when collection.Elements.All(e => e is ExpressionElementSyntax):
                return new DesignerValue(DesignerValueKinds.Array) { Arguments = [.. collection.Elements.Cast<ExpressionElementSyntax>().Select(e => Value(e.Expression))] };
            case CastExpressionSyntax cast:
                return new DesignerValue(DesignerValueKinds.Cast) { Type = Written(cast.Type), Arguments = [Value(cast.Expression)] };
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.BitwiseOrExpression } ored:
                return new DesignerValue(DesignerValueKinds.Or) { Arguments = [Value(ored.Left), Value(ored.Right)] };
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression } negated:
                return new DesignerValue(DesignerValueKinds.Negate) { Arguments = [Value(negated.Operand)] };
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryPlusExpression } plus:
                return Value(plus.Operand);
            case TypeOfExpressionSyntax typed:
                return new DesignerValue(DesignerValueKinds.TypeOf) { Type = Written(typed.Type) };
        }
        return new DesignerValue(DesignerValueKinds.Unread) { Text = at.ToString() };
    }

    private static IReadOnlyList<DesignerValue> Values(ArgumentListSyntax? arguments) => [.. (arguments?.Arguments ?? []).Select(a => Value(a.Expression))];

    private static DesignerValue Literal(LiteralExpressionSyntax literal)
    {
        object? value = literal.Token.Value;
        string type = value switch
        {
            null => "null",
            string => "string",
            char => "char",
            bool => "bool",
            int => "int",
            uint => "uint",
            long => "long",
            ulong => "ulong",
            float => "float",
            double => "double",
            decimal => "decimal",
            _ => "unknown",
        };
        string? text = value switch
        {
            null => null,
            string s => s,
            char c => c.ToString(),
            bool b => b ? "true" : "false",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        return new DesignerValue(DesignerValueKinds.Literal) { Type = type, Text = text };
    }

    /// <summary>A chain of names as one dotted name, <c>this.</c> and <c>global::</c> left out; null for anything else.</summary>
    private static string? Dotted(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Expression: ThisExpressionSyntax, Name: var name } => name.Identifier.ValueText,
        MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression, Expression: var on, Name: var name } when Dotted(on) is { } before => $"{before}.{name.Identifier.ValueText}",
        QualifiedNameSyntax { Left: var left, Right: var right } when Dotted(left) is { } before => $"{before}.{right.Identifier.ValueText}",
        AliasQualifiedNameSyntax { Name: var name } => name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>A statement on a path: the field it starts with, or the root where it starts with none, and the rest as its
    /// member.</summary>
    private static DesignerStatement Of(DesignerAct act, IReadOnlyList<string> path, HashSet<string> fields, int at) =>
        fields.Contains(path[0]) && path.Count > 1
            ? new DesignerStatement(act, path[0], string.Join('.', path.Skip(1)), at)
            : new DesignerStatement(act, string.Empty, string.Join('.', path), at);

    /// <summary>Where a statement's target starts: past a <c>this.</c> it names.</summary>
    private static int Start(ExpressionSyntax target) =>
        target is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: var name } ? name.SpanStart : Leftmost(target).SpanStart;

    private static ExpressionSyntax Leftmost(ExpressionSyntax expression)
    {
        ExpressionSyntax at = expression;
        while (at is MemberAccessExpressionSyntax { Expression: var inner } && inner is not ThisExpressionSyntax)
        {
            at = inner;
        }
        return at is MemberAccessExpressionSyntax { Name: var name } ? name : at;
    }

    /// <summary>The names a target reads, outermost first, a leading <c>this.</c> left out; null for a target that is no chain
    /// of names (a cast's, an element's).</summary>
    private static List<string>? Path(ExpressionSyntax target)
    {
        List<string> names = [];
        ExpressionSyntax? at = target;
        while (true)
        {
            switch (at)
            {
                case IdentifierNameSyntax name:
                    names.Insert(0, name.Identifier.ValueText);
                    return names;
                case MemberAccessExpressionSyntax { RawKind: (int)SyntaxKind.SimpleMemberAccessExpression } access:
                    names.Insert(0, access.Name.Identifier.ValueText);
                    if (access.Expression is ThisExpressionSyntax)
                    {
                        return names;
                    }
                    at = access.Expression;
                    break;
                default:
                    return null;
            }
        }
    }
}
