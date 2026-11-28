using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>What may inherit from a declaration: what its signpost counts besides its usages.</summary>
public enum Heritage
{
    /// <summary>Nothing: a sealed, concrete or ordinary declaration.</summary>
    None,

    /// <summary>An interface, or what it or a class declares abstract: what implements it.</summary>
    Implementations,

    /// <summary>A virtual member: what overrides it.</summary>
    Overrides,

    /// <summary>An abstract class: what derives from it.</summary>
    Inheritors,
}

/// <summary>A type or member a signpost stands above: its name and where it is, the line the signpost heads, where that
/// line's code begins (an offset), and the lines the declaration spans (for its authors).</summary>
public sealed record SignpostDeclaration(string Name, int NameOffset, int Line, int LineCode, int FirstLine, int LastLine, Heritage Heritage);

/// <summary>The types and members of a C# file that signposts stand above: a type, delegate, method, constructor, property,
/// event and field, one declaration on each line (the first), none of what a body holds; and a signpost's words. Reads
/// only the syntax it is given, so it runs on any thread.</summary>
public static class SignpostDeclarations
{
    /// <summary>The declarations of a parsed file, in the order they stand.</summary>
    public static IReadOnlyList<SignpostDeclaration> Of(ParsedText parsed)
    {
        List<SignpostDeclaration> found = [];
        if (parsed.Root is CompilationUnitSyntax unit)
        {
            Collect(unit.Members, inInterface: false, parsed.Text, found, []);
        }
        return found;
    }

    /// <summary>How many places use a declaration, in words ("1 usage", "no usages").</summary>
    public static string Usages(int count) => count == 0 ? "no usages" : Plural.Of(count, "usage");

    /// <summary>What implements, overrides or derives from a declaration, in words by what it is ("1 override").</summary>
    public static string Inheriting(Heritage heritage, int count)
    {
        string noun = heritage switch
        {
            Heritage.Overrides => "override",
            Heritage.Inheritors => "inheritor",
            _ => "implementation",
        };
        return count == 0 ? $"no {noun}s" : Plural.Of(count, noun);
    }

    private static void Collect(SyntaxList<MemberDeclarationSyntax> members, bool inInterface, TextSnapshot text, List<SignpostDeclaration> found, HashSet<int> lines)
    {
        foreach (MemberDeclarationSyntax member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax space:
                    Collect(space.Members, inInterface: false, text, found, lines);
                    break;
                case TypeDeclarationSyntax type:
                    Add(type, type.Identifier, TypeHeritage(type), text, found, lines);
                    Collect(type.Members, type.IsKind(SyntaxKind.InterfaceDeclaration), text, found, lines);
                    break;
                case EnumDeclarationSyntax enumeration:
                    Add(enumeration, enumeration.Identifier, Heritage.None, text, found, lines);
                    break;
                case DelegateDeclarationSyntax delegated:
                    Add(delegated, delegated.Identifier, Heritage.None, text, found, lines);
                    break;
                case MethodDeclarationSyntax method:
                    Add(method, method.Identifier, MemberHeritage(method, inInterface), text, found, lines);
                    break;
                case ConstructorDeclarationSyntax constructor:
                    Add(constructor, constructor.Identifier, Heritage.None, text, found, lines);
                    break;
                case PropertyDeclarationSyntax property:
                    Add(property, property.Identifier, MemberHeritage(property, inInterface), text, found, lines);
                    break;
                case EventDeclarationSyntax declared:
                    Add(declared, declared.Identifier, MemberHeritage(declared, inInterface), text, found, lines);
                    break;
                case BaseFieldDeclarationSyntax field when field.Declaration.Variables.Count == 1:
                    Add(field, field.Declaration.Variables[0].Identifier, field is EventFieldDeclarationSyntax ? MemberHeritage(field, inInterface) : Heritage.None, text, found, lines);
                    break;
            }
        }
    }

    private static void Add(MemberDeclarationSyntax member, SyntaxToken name, Heritage heritage, TextSnapshot text, List<SignpostDeclaration> found, HashSet<int> lines)
    {
        if (name.IsMissing || name.SpanStart >= text.Length)
        {
            return;
        }
        // The signpost heads the declaration whole, its attributes under it with the rest.
        int line = text.GetLineFromOffset(Math.Min(member.GetFirstToken().SpanStart, text.Length));
        // One declaration's signpost a line: a second on the line would head it again.
        if (!lines.Add(line))
        {
            return;
        }
        int first = text.GetLineFromOffset(Math.Min(member.SpanStart, text.Length));
        int last = text.GetLineFromOffset(Math.Min(Math.Max(member.SpanStart, member.Span.End - 1), text.Length));
        int code = TextNavigation.IndentEnd(text, text.GetLine(line));
        found.Add(new SignpostDeclaration(name.ValueText, name.SpanStart, line, code, first, Math.Max(first, last), heritage));
    }

    private static Heritage TypeHeritage(TypeDeclarationSyntax type) => type.Kind() switch
    {
        SyntaxKind.InterfaceDeclaration => Heritage.Implementations,
        SyntaxKind.ClassDeclaration when type.Modifiers.Any(SyntaxKind.AbstractKeyword) => Heritage.Inheritors,
        _ => Heritage.None,
    };

    /// <summary>What a member of an interface, an abstract member or a virtual one counts: its implementations, or its
    /// overrides.</summary>
    private static Heritage MemberHeritage(MemberDeclarationSyntax member, bool inInterface)
    {
        SyntaxTokenList modifiers = member.Modifiers;
        if (modifiers.Any(SyntaxKind.StaticKeyword) || modifiers.Any(SyntaxKind.PrivateKeyword) || modifiers.Any(SyntaxKind.SealedKeyword))
        {
            return Heritage.None;
        }
        if (inInterface || modifiers.Any(SyntaxKind.AbstractKeyword))
        {
            return Heritage.Implementations;
        }
        return modifiers.Any(SyntaxKind.VirtualKeyword) ? Heritage.Overrides : Heritage.None;
    }
}
