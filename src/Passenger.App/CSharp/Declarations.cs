using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;
using SymbolKind = Passenger.Lsp.SymbolKind;
using Passenger.Text;

namespace Passenger.App.CSharp;

/// <summary>What an open file's own syntax says of its declarations: the override keyword Base Symbols asks the language
/// server about, the types and member a place is in, which Recent Locations names, the outline of a file the server does
/// not hold, and the outline the navigation bar over a C# file's editor shows.</summary>
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
                foreach (Part part in Parts(child))
                {
                    text.Append(' ', depth * 2).Append(part.Name).Append(" (").Append(part.Kind).Append(", line ")
                        .Append(syntax.Text.GetLineFromOffset(Math.Min(part.At, syntax.Text.Length)) + 1).Append(")\n");
                }
                if (Holds(child))
                {
                    Write(child, depth + 1);
                }
            }
        }
    }

    /// <summary>A file's namespaces, types and members as an outline's rows, each made by <paramref name="row"/> from what it
    /// declares and its depth, spanning its declaration's lines in the editor's lines; a row the outline
    /// <paramref name="before"/> has in its place unchanged is that one. Made off the window's thread.</summary>
    public static Outline<T> Rows<T>(ParsedText syntax, Func<Declared, int, T> row, Outline<T>? before = null, CancellationToken cancel = default)
        where T : class
    {
        TextSnapshot text = syntax.Text;
        return Outline<T>.Of<Node>([.. Under(syntax.Root)], node => Holds(node.Syntax) ? [.. Under(node.Syntax)] : null, (node, depth) =>
        {
            Part part = node.Part;
            int last = Line(node.Syntax.Span.End);
            Declared declared = new(part.Name, part.Kind, part.Symbol, text.GetPosition(Math.Min(part.At, text.Length)), part.Type?.ToString(), last);
            return (row(declared, depth), Line(node.Syntax.SpanStart), last);
        }, before, cancel);

        int Line(int offset) => text.GetLineFromOffset(Math.Min(offset, text.Length));

        static IEnumerable<Node> Under(SyntaxNode node) => node.ChildNodes().SelectMany(child => Parts(child).Select(part => new Node(child, part)));
    }

    /// <summary>What a declaration declares, as an outline's row shows it: its name, its kind in words and as a symbol's,
    /// where its name is, the type it has or returns where it has one, and the last line (from zero) it spans.</summary>
    public sealed record Declared(string Name, string Kind, SymbolKind Symbol, TextPosition At, string? Type, int LastLine);

    /// <summary>Whether a declaration holds declarations of its own, which an outline lists under it.</summary>
    private static bool Holds(SyntaxNode node) => node is BaseNamespaceDeclarationSyntax or TypeDeclarationSyntax or EnumDeclarationSyntax;

    /// <summary>A part of an outline and the declaration it is of.</summary>
    private sealed record Node(SyntaxNode Syntax, Part Part);

    /// <summary>What a declaration declares, as an outline names each: its name, its kind in words and as a symbol's, where
    /// its name is, and the type it has or returns where it has one.</summary>
    private readonly record struct Part(string Name, string Kind, SymbolKind Symbol, int At, TypeSyntax? Type = null);

    /// <summary>What a declaration declares, as an outline names each part: a field's or event's every variable.</summary>
    private static IEnumerable<Part> Parts(SyntaxNode node) => node switch
    {
        BaseNamespaceDeclarationSyntax space => [new(space.Name.ToString(), "namespace", SymbolKind.Namespace, space.Name.SpanStart)],
        RecordDeclarationSyntax record => record.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
            ? [new(Typed(record), "record struct", SymbolKind.Struct, record.Identifier.SpanStart)]
            : [new(Typed(record), "record", SymbolKind.Class, record.Identifier.SpanStart)],
        ClassDeclarationSyntax type => [new(Typed(type), "class", SymbolKind.Class, type.Identifier.SpanStart)],
        StructDeclarationSyntax type => [new(Typed(type), "struct", SymbolKind.Struct, type.Identifier.SpanStart)],
        InterfaceDeclarationSyntax type => [new(Typed(type), "interface", SymbolKind.Interface, type.Identifier.SpanStart)],
        EnumDeclarationSyntax type => [new(type.Identifier.Text, "enum", SymbolKind.Enum, type.Identifier.SpanStart)],
        DelegateDeclarationSyntax type => [new($"{type.Identifier.Text}{type.TypeParameterList}({Parameters(type.ParameterList)})", "delegate", SymbolKind.Function,
            type.Identifier.SpanStart, type.ReturnType)],
        MethodDeclarationSyntax method => [new($"{method.Identifier.Text}{method.TypeParameterList}({Parameters(method.ParameterList)})", "method", SymbolKind.Method,
            method.Identifier.SpanStart, method.ReturnType)],
        ConstructorDeclarationSyntax constructor => [new($"{constructor.Identifier.Text}({Parameters(constructor.ParameterList)})", "constructor", SymbolKind.Constructor,
            constructor.Identifier.SpanStart)],
        DestructorDeclarationSyntax destructor => [new($"~{destructor.Identifier.Text}()", "destructor", SymbolKind.Method, destructor.Identifier.SpanStart)],
        OperatorDeclarationSyntax operation => [new($"operator {operation.OperatorToken.Text}({Parameters(operation.ParameterList)})", "operator", SymbolKind.Operator,
            operation.OperatorToken.SpanStart, operation.ReturnType)],
        ConversionOperatorDeclarationSyntax conversion => [new($"{conversion.ImplicitOrExplicitKeyword.Text} operator {conversion.Type}", "operator", SymbolKind.Operator,
            conversion.Type.SpanStart)],
        PropertyDeclarationSyntax property => [new(property.Identifier.Text, "property", SymbolKind.Property, property.Identifier.SpanStart, property.Type)],
        IndexerDeclarationSyntax indexer => [new($"this[{Parameters(indexer.ParameterList)}]", "indexer", SymbolKind.Property, indexer.ThisKeyword.SpanStart, indexer.Type)],
        EventDeclarationSyntax declared => [new(declared.Identifier.Text, "event", SymbolKind.Event, declared.Identifier.SpanStart, declared.Type)],
        EventFieldDeclarationSyntax field => [.. field.Declaration.Variables.Select(v => new Part(v.Identifier.Text, "event", SymbolKind.Event, v.Identifier.SpanStart, field.Declaration.Type))],
        FieldDeclarationSyntax field => [.. field.Declaration.Variables.Select(v => field.Modifiers.Any(SyntaxKind.ConstKeyword)
            ? new Part(v.Identifier.Text, "constant", SymbolKind.Constant, v.Identifier.SpanStart, field.Declaration.Type)
            : new Part(v.Identifier.Text, "field", SymbolKind.Field, v.Identifier.SpanStart, field.Declaration.Type))],
        EnumMemberDeclarationSyntax member => [new(member.Identifier.Text, "enum member", SymbolKind.EnumMember, member.Identifier.SpanStart)],
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
