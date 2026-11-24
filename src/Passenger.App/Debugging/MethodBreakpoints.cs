using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.App.Debugging;

/// <summary>The C# method a method breakpoint set in the editor is on, read from its file's parse: the method,
/// constructor, static constructor, finalizer or primary constructor whose declaration holds the caret, named as the
/// debugger finds it (its namespace and types, a generic one's arity, a constructor's .ctor, a static one's .cctor and a
/// finalizer's Finalize) with its parameters as written, ref, out and in with them and the file's aliases read, which the
/// debugger matches to the types the metadata gives, so it stops in that overload alone.</summary>
public static class MethodBreakpoints
{
    /// <summary>A finalizer's name in metadata, which C# writes ~Type.</summary>
    private const string Finalizer = "Finalize";

    /// <summary>A method's name for its breakpoint and the line (from zero) its name is on.</summary>
    public readonly record struct Method(string Name, int Line);

    /// <summary>The method whose declaration (its body too) holds an offset, or the primary constructor whose parameters do;
    /// null where none does.</summary>
    public static Method? At(ParsedText parsed, int offset)
    {
        SyntaxToken token = parsed.Root.FindToken(Math.Clamp(offset, 0, Math.Max(0, parsed.Root.FullSpan.End - 1)));
        foreach (SyntaxNode node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case MethodDeclarationSyntax or ConstructorDeclarationSyntax or DestructorDeclarationSyntax:
                    BaseMethodDeclarationSyntax method = (BaseMethodDeclarationSyntax)node;
                    return new Method(NameOf(method), parsed.Text.GetLineFromOffset(IdentifierOf(method).SpanStart));
                case ParameterListSyntax { Parent: TypeDeclarationSyntax type } primary:
                    return new Method($"{TypeOf(type)}.{ConstructorInfo.ConstructorName}{Parameters(primary)}", parsed.Text.GetLineFromOffset(type.Identifier.SpanStart));
                case TypeDeclarationSyntax:
                    return null;
            }
        }
        return null;
    }

    /// <summary>A method's, a constructor's or a finalizer's name as its breakpoint asks for it.</summary>
    public static string NameOf(BaseMethodDeclarationSyntax method)
    {
        string type = method.Parent is TypeDeclarationSyntax declaring ? TypeOf(declaring) : string.Empty;
        string name = method switch
        {
            ConstructorDeclarationSyntax constructor => constructor.Modifiers.Any(SyntaxKind.StaticKeyword) ? ConstructorInfo.TypeConstructorName : ConstructorInfo.ConstructorName,
            DestructorDeclarationSyntax => Finalizer,
            MethodDeclarationSyntax named => named.Identifier.ValueText + Arity(named.TypeParameterList),
            _ => IdentifierOf(method).ValueText,
        };
        return $"{(type.Length == 0 ? string.Empty : type + ".")}{name}{Parameters(method.ParameterList)}";
    }

    private static SyntaxToken IdentifierOf(BaseMethodDeclarationSyntax method) => method switch
    {
        MethodDeclarationSyntax named => named.Identifier,
        ConstructorDeclarationSyntax constructor => constructor.Identifier,
        DestructorDeclarationSyntax finalizer => finalizer.Identifier,
        _ => method.GetFirstToken(),
    };

    /// <summary>A type's name with its namespace and the types it is in, each generic one's parameters named.</summary>
    private static string TypeOf(TypeDeclarationSyntax type)
    {
        List<string> outer = [];
        for (SyntaxNode? at = type; at is not null; at = at.Parent)
        {
            switch (at)
            {
                case TypeDeclarationSyntax declaring:
                    outer.Add(declaring.Identifier.ValueText + Arity(declaring.TypeParameterList));
                    break;
                case BaseNamespaceDeclarationSyntax space:
                    outer.Add(space.Name.ToString());
                    break;
            }
        }
        outer.Reverse();
        return string.Join('.', outer);
    }

    /// <summary>The parameters as the breakpoint names them: each one's ref, out or in and its type as written, the file's
    /// aliases taken for the types they name; none where one has no type (__arglist), so it stops in every overload.</summary>
    private static string Parameters(ParameterListSyntax list)
    {
        if (list.Parameters.Any(p => p.Type is null))
        {
            return string.Empty;
        }
        Dictionary<string, TypeSyntax> aliases = Aliases(list);
        return $"({string.Join(", ", list.Parameters.Select(p => string.Join(' ', [.. Modifiers(p), Written(p.Type!, aliases)])))})";
    }

    private static IEnumerable<string> Modifiers(ParameterSyntax parameter) => parameter.Modifiers
        .Where(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword) || m.IsKind(SyntaxKind.ReadOnlyKeyword))
        .Select(m => m.ValueText);

    /// <summary>A type as written on one line, each alias of the file's it names put as the type it stands for.</summary>
    private static string Written(TypeSyntax type, Dictionary<string, TypeSyntax> aliases)
    {
        TypeSyntax named = aliases.Count == 0 ? type : type.ReplaceNodes(
            type.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Where(n => n.Parent is not QualifiedNameSyntax { Right: var right } || right != n)
                .Where(n => aliases.ContainsKey(n.Identifier.ValueText)),
            (n, _) => aliases[n.Identifier.ValueText]);
        return string.Join(' ', named.ToString().Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The using aliases of the file and of each namespace around a node, by name.</summary>
    private static Dictionary<string, TypeSyntax> Aliases(SyntaxNode node)
    {
        Dictionary<string, TypeSyntax> aliases = new(StringComparer.Ordinal);
        foreach (SyntaxNode around in node.Ancestors())
        {
            SyntaxList<UsingDirectiveSyntax> usings = around switch
            {
                CompilationUnitSyntax unit => unit.Usings,
                BaseNamespaceDeclarationSyntax space => space.Usings,
                _ => default,
            };
            foreach (UsingDirectiveSyntax alias in usings.Where(u => u.Alias is not null && u.NamespaceOrType is not null))
            {
                aliases.TryAdd(alias.Alias!.Name.Identifier.ValueText, alias.NamespaceOrType!);
            }
        }
        return aliases;
    }

    /// <summary>A generic's arity as the debugger reads it: &lt;T, U&gt; for two.</summary>
    private static string Arity(TypeParameterListSyntax? parameters) =>
        parameters is { Parameters.Count: > 0 } list ? $"<{string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText))}>" : string.Empty;
}
