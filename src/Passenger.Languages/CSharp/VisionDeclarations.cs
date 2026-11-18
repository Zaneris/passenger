using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Text;

namespace Passenger.Languages.CSharp;

/// <summary>What may inherit from a declaration, which decides whether Code Vision asks for its implementations.</summary>
public enum Heritage
{
    /// <summary>Nothing is asked: a sealed, concrete or ordinary declaration.</summary>
    None,

    /// <summary>An interface, or what it or a class declares abstract: what implements it.</summary>
    Implementations,

    /// <summary>A virtual member: what overrides it.</summary>
    Overrides,

    /// <summary>An abstract class: what derives from it.</summary>
    Inheritors,
}

/// <summary>A type or member Code Vision hints after: the name Roslyn is asked about, the lines the declaration spans (for its
/// authors) and the line its hints go after. <see cref="Key"/> tells it from another declaration of the file through edits
/// that do not rename it.</summary>
public sealed record VisionDeclaration(string Key, string Name, int NameOffset, int Line, int FirstLine, int LastLine, Heritage Heritage);

/// <summary>The types and members of a C# file that Code Vision hints after: a type, delegate, method, constructor, property,
/// event and field, one declaration on each line (the first), none of what a body holds. Reads only the syntax it is given, so
/// it runs on any thread.</summary>
public static class VisionDeclarations
{
    /// <summary>The declarations of a parsed file, in the order they stand.</summary>
    public static IReadOnlyList<VisionDeclaration> Of(ParsedText parsed)
    {
        List<VisionDeclaration> found = [];
        if (parsed.Root is CompilationUnitSyntax unit)
        {
            Collect(unit.Members, [], inInterface: false, parsed.Text, found, []);
        }
        return found;
    }

    private static void Collect(SyntaxList<MemberDeclarationSyntax> members, List<string> path, bool inInterface, TextSnapshot text,
        List<VisionDeclaration> found, HashSet<int> lines)
    {
        foreach (MemberDeclarationSyntax member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax space:
                    Collect(space.Members, path, inInterface: false, text, found, lines);
                    break;
                case TypeDeclarationSyntax type:
                    Add(type, type.Identifier, string.Empty, path, TypeHeritage(type), text, found, lines);
                    List<string> inside = [.. path, type.Identifier.ValueText];
                    Collect(type.Members, inside, type.IsKind(SyntaxKind.InterfaceDeclaration), text, found, lines);
                    break;
                case EnumDeclarationSyntax enumeration:
                    Add(enumeration, enumeration.Identifier, string.Empty, path, Heritage.None, text, found, lines);
                    break;
                case DelegateDeclarationSyntax delegated:
                    Add(delegated, delegated.Identifier, string.Empty, path, Heritage.None, text, found, lines);
                    break;
                case MethodDeclarationSyntax method:
                    Add(method, method.Identifier, Parameters(method.ParameterList), path, MemberHeritage(method, inInterface), text, found, lines);
                    break;
                case ConstructorDeclarationSyntax constructor:
                    Add(constructor, constructor.Identifier, Parameters(constructor.ParameterList), path, Heritage.None, text, found, lines);
                    break;
                case PropertyDeclarationSyntax property:
                    Add(property, property.Identifier, string.Empty, path, MemberHeritage(property, inInterface), text, found, lines);
                    break;
                case EventDeclarationSyntax declared:
                    Add(declared, declared.Identifier, string.Empty, path, MemberHeritage(declared, inInterface), text, found, lines);
                    break;
                case BaseFieldDeclarationSyntax field when field.Declaration.Variables.Count == 1:
                    Add(field, field.Declaration.Variables[0].Identifier, string.Empty, path, field is EventFieldDeclarationSyntax ? MemberHeritage(field, inInterface) : Heritage.None, text, found, lines);
                    break;
            }
        }
    }

    private static void Add(MemberDeclarationSyntax member, SyntaxToken name, string signature, List<string> path, Heritage heritage,
        TextSnapshot text, List<VisionDeclaration> found, HashSet<int> lines)
    {
        if (name.IsMissing || name.SpanStart >= text.Length)
        {
            return;
        }
        int line = text.GetLineFromOffset(name.SpanStart);
        // One declaration's hints a line: a second on the line would draw its own after the first's.
        if (!lines.Add(line))
        {
            return;
        }
        int first = text.GetLineFromOffset(Math.Min(member.SpanStart, text.Length));
        int last = text.GetLineFromOffset(Math.Min(Math.Max(member.SpanStart, member.Span.End - 1), text.Length));
        string key = $"{(int)member.Kind()}:{string.Join('.', path)}.{name.ValueText}{signature}";
        found.Add(new VisionDeclaration(key, name.ValueText, name.SpanStart, line, first, Math.Max(first, last), heritage));
    }

    /// <summary>The types of a method's or constructor's parameters, which tell its overloads apart.</summary>
    private static string Parameters(ParameterListSyntax list) =>
        $"({string.Join(',', list.Parameters.Select(p => p.Type?.ToString().Replace(" ", string.Empty, StringComparison.Ordinal) ?? string.Empty))})";

    private static Heritage TypeHeritage(TypeDeclarationSyntax type) => type.Kind() switch
    {
        SyntaxKind.InterfaceDeclaration => Heritage.Implementations,
        SyntaxKind.ClassDeclaration when type.Modifiers.Any(SyntaxKind.AbstractKeyword) => Heritage.Inheritors,
        _ => Heritage.None,
    };

    /// <summary>What a member of an interface, an abstract member or a virtual one is asked for: its implementations, or its
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
