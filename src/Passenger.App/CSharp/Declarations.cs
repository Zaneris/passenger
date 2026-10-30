using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.App.CSharp;

/// <summary>What an open file's own syntax says of its declarations: the override keyword Base Symbols asks the language
/// server about, the types and member a place is in, which Recent Locations names, and the outline of a file the server
/// does not hold.</summary>
internal static class Declarations
{
    /// <summary>Where the override keyword is of the member whose name in its declaration is at an offset, if it has one:
    /// the server's definition there is the member overridden, a library's included.</summary>
    public static int? OverrideKeywordAt(ParsedText syntax, int offset)
    {
        if (offset > syntax.Text.Length)
        {
            return null;
        }
        SyntaxToken token = syntax.Root.FindToken(offset);
        if (!token.IsKind(SyntaxKind.IdentifierToken) && offset > 0)
        {
            token = syntax.Root.FindToken(offset - 1);
        }
        SyntaxTokenList? modifiers = token.Parent switch
        {
            MethodDeclarationSyntax method when method.Identifier == token => method.Modifiers,
            PropertyDeclarationSyntax property when property.Identifier == token => property.Modifiers,
            EventDeclarationSyntax declared when declared.Identifier == token => declared.Modifiers,
            VariableDeclaratorSyntax { Parent.Parent: EventFieldDeclarationSyntax field } variable when variable.Identifier == token => field.Modifiers,
            _ => null,
        };
        SyntaxToken keyword = modifiers?.FirstOrDefault(m => m.IsKind(SyntaxKind.OverrideKeyword)) ?? default;
        return keyword.IsKind(SyntaxKind.OverrideKeyword) ? keyword.SpanStart : null;
    }

    /// <summary>The types and the member a place is in, outermost first ("Shop › Add(int)"), as the status bar's path names
    /// them; null outside any type.</summary>
    public static string? PathAt(ParsedText syntax, int offset)
    {
        if (offset > syntax.Text.Length)
        {
            return null;
        }
        List<string> parts = [];
        foreach (SyntaxNode node in syntax.Root.FindToken(offset).Parent?.AncestorsAndSelf() ?? [])
        {
            if (Name(node) is { } name)
            {
                parts.Insert(0, name);
            }
        }
        return parts.Count > 0 ? string.Join(" › ", parts) : null;
    }

    /// <summary>A file's namespaces, types and members, each on a line indented by its nesting, with its kind and its line
    /// in the editor's lines; empty where it declares none.</summary>
    public static string Outline(ParsedText syntax)
    {
        StringBuilder text = new();
        Write(syntax.Root, 0);
        return text.ToString().TrimEnd();

        void Write(SyntaxNode node, int depth)
        {
            foreach (SyntaxNode child in node.ChildNodes())
            {
                foreach ((string name, string kind, int at) in Parts(child))
                {
                    text.Append(' ', depth * 2).Append(name).Append(" (").Append(kind).Append(", line ")
                        .Append(syntax.Text.GetLineFromOffset(Math.Min(at, syntax.Text.Length)) + 1).Append(")\n");
                }
                if (child is BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax or EnumDeclarationSyntax)
                {
                    Write(child, depth + 1);
                }
            }
        }
    }

    /// <summary>What a declaration declares, as an outline names each part: a field's or event's every variable.</summary>
    private static IEnumerable<(string Name, string Kind, int At)> Parts(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax space => [(space.Name.ToString(), "namespace", space.Name.SpanStart)],
        RecordDeclarationSyntax record => [(Typed(record), record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record", record.Identifier.SpanStart)],
        ClassDeclarationSyntax type => [(Typed(type), "class", type.Identifier.SpanStart)],
        StructDeclarationSyntax type => [(Typed(type), "struct", type.Identifier.SpanStart)],
        InterfaceDeclarationSyntax type => [(Typed(type), "interface", type.Identifier.SpanStart)],
        EnumDeclarationSyntax type => [(type.Identifier.Text, "enum", type.Identifier.SpanStart)],
        DelegateDeclarationSyntax type => [($"{type.Identifier.Text}{type.TypeParameterList}({Parameters(type.ParameterList)})", "delegate", type.Identifier.SpanStart)],
        MethodDeclarationSyntax method => [($"{method.Identifier.Text}{method.TypeParameterList}({Parameters(method.ParameterList)})", "method", method.Identifier.SpanStart)],
        ConstructorDeclarationSyntax constructor => [($"{constructor.Identifier.Text}({Parameters(constructor.ParameterList)})", "constructor", constructor.Identifier.SpanStart)],
        DestructorDeclarationSyntax destructor => [($"~{destructor.Identifier.Text}()", "destructor", destructor.Identifier.SpanStart)],
        OperatorDeclarationSyntax operation => [($"operator {operation.OperatorToken.Text}({Parameters(operation.ParameterList)})", "operator", operation.OperatorToken.SpanStart)],
        ConversionOperatorDeclarationSyntax conversion => [($"{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}", "operator", conversion.Type.SpanStart)],
        PropertyDeclarationSyntax property => [(property.Identifier.Text, "property", property.Identifier.SpanStart)],
        IndexerDeclarationSyntax indexer => [($"this[{Parameters(indexer.ParameterList)}]", "indexer", indexer.ThisKeyword.SpanStart)],
        EventDeclarationSyntax declared => [(declared.Identifier.Text, "event", declared.Identifier.SpanStart)],
        EventFieldDeclarationSyntax field => [.. field.Declaration.Variables.Select(v => (Name: v.Identifier.Text, Kind: "event", At: v.Identifier.SpanStart))],
        FieldDeclarationSyntax field => [.. field.Declaration.Variables.Select(v =>
            (Name: v.Identifier.Text, Kind: field.Modifiers.Any(SyntaxKind.ConstKeyword) ? "constant" : "field", At: v.Identifier.SpanStart))],
        EnumMemberDeclarationSyntax member => [(member.Identifier.Text, "enum member", member.Identifier.SpanStart)],
        _ => [],
    };

    private static string Typed(TypeDeclarationSyntax type) => $"{type.Identifier.Text}{type.TypeParameterList}";

    private static string? Name(SyntaxNode node) => node switch
    {
        BaseTypeDeclarationSyntax type => type.Identifier.Text,
        MethodDeclarationSyntax method => $"{method.Identifier.Text}({Parameters(method.ParameterList)})",
        ConstructorDeclarationSyntax constructor => $"{constructor.Identifier.Text}({Parameters(constructor.ParameterList)})",
        PropertyDeclarationSyntax property => property.Identifier.Text,
        IndexerDeclarationSyntax => "this[]",
        EventDeclarationSyntax declared => declared.Identifier.Text,
        _ => null,
    };

    private static string Parameters(BaseParameterListSyntax list) => string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.Text));
}
