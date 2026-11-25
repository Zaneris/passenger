using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.Languages.WinForms;

/// <summary>What a statement of designer code does to a component: makes it, sets a property of it, hooks a handler to one of
/// its events, or calls a method of it (adds a control to it, begins its layout); anything else the designer writes is other.</summary>
public enum DesignerAct
{
    Create,
    Assign,
    Hook,
    Call,
    Other,
}

/// <summary>A statement of a Windows Forms class's InitializeComponent: what it does, to which component (the class's field,
/// empty for the class's own root) and which of its members (a property's dotted path, an event, a method's), and where it
/// starts in the text, as an offset (the editor's lines, never Roslyn's, are counted from it).</summary>
public sealed record DesignerStatement(DesignerAct Act, string Target, string? Member, int At);

/// <summary>A Windows Forms class's designer code as read from one file: where its InitializeComponent is named, and its
/// statements in order.</summary>
public sealed record DesignerSource(int Method, IReadOnlyList<DesignerStatement> Statements)
{
    /// <summary>Where a field is made; the method's name for the class's root (an empty field); null for a field it does not make.</summary>
    public int? MadeAt(string field) => field.Length == 0 ? Method
        : Statements.FirstOrDefault(s => s.Act == DesignerAct.Create && s.Target == field)?.At;

    /// <summary>Where a component's property is set, by its name (a part of it set counts, as FlatAppearance.BorderSize does
    /// for FlatAppearance); null where nothing sets it.</summary>
    public int? SetAt(string field, string property) => Statements.FirstOrDefault(s => s.Act == DesignerAct.Assign && s.Target == field && s.Member is { } member
        && (member == property || (member.StartsWith(property, StringComparison.Ordinal) && member.Length > property.Length && member[property.Length] == '.')))?.At;
}

/// <summary>Windows Forms designer code read as its designer writes it, from the file's parse (<see cref="CSharpParsing"/>):
/// the class's fields, and each statement of its InitializeComponent by what it does to which of them, whether it qualifies
/// them with <c>this.</c> or not.</summary>
public static class DesignerCode
{
    /// <summary>The designer code of a class, by its full name (its namespace's and its own, a nested class's after a '+'),
    /// in a file's text; null where the file declares no InitializeComponent of that class.</summary>
    public static DesignerSource? Read(string text, string fullName)
    {
        SyntaxNode root = CSharpParsing.Parse(text).GetRoot();
        foreach (ClassDeclarationSyntax type in root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or ClassDeclarationSyntax).OfType<ClassDeclarationSyntax>())
        {
            if (FullName(type) != fullName
                || type.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "InitializeComponent" && m.ParameterList.Parameters.Count == 0) is not { } method)
            {
                continue;
            }
            HashSet<string> fields = [.. type.Members.OfType<FieldDeclarationSyntax>().SelectMany(f => f.Declaration.Variables).Select(v => v.Identifier.ValueText)];
            IEnumerable<StatementSyntax> statements = method.Body?.Statements ?? [];
            return new DesignerSource(method.Identifier.SpanStart, [.. statements.Select(s => Statement(s, fields))]);
        }
        return null;
    }

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

    private static DesignerStatement Statement(StatementSyntax statement, HashSet<string> fields)
    {
        switch (statement)
        {
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.SimpleAssignmentExpression } assigned }
                when Path(assigned.Left) is { } set:
                return set.Count == 1 && fields.Contains(set[0])
                    ? new DesignerStatement(DesignerAct.Create, set[0], null, Start(assigned.Left))
                    : Of(DesignerAct.Assign, set, fields, Start(assigned.Left));
            case ExpressionStatementSyntax { Expression: AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } hooked }
                when Path(hooked.Left) is { } eventPath:
                return Of(DesignerAct.Hook, eventPath, fields, Start(hooked.Left));
            case ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: var called } } when Path(called) is { } method:
                return Of(DesignerAct.Call, method, fields, Start(called));
        }
        return new DesignerStatement(DesignerAct.Other, string.Empty, null, statement.SpanStart);
    }

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
