using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.App.Debugging;

/// <summary>The C# method a method breakpoint set in the editor is on, read from its file's parse: the method whose
/// declaration holds the caret, named as the debugger finds it (its namespace and types, a generic one's arity), with its
/// parameters' types where each is one the parse alone names as its metadata does, so it stops in that overload alone;
/// where one is not (a type parameter, a reference type's nullable mark, ref, out or in), every overload of the name.</summary>
public static class MethodBreakpoints
{
    /// <summary>A method's name for its breakpoint and the line (from zero) its name is on.</summary>
    public readonly record struct Method(string Name, int Line);

    /// <summary>The method whose declaration (its body too) holds an offset; null where none does.</summary>
    public static Method? At(ParsedText parsed, int offset)
    {
        if (parsed.Root.FindToken(Math.Clamp(offset, 0, Math.Max(0, parsed.Root.FullSpan.End - 1))).Parent?.AncestorsAndSelf()
            .OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } method)
        {
            return null;
        }
        return new Method(NameOf(method), parsed.Text.GetLineFromOffset(method.Identifier.SpanStart));
    }

    /// <summary>A method's name as its breakpoint asks for it.</summary>
    public static string NameOf(MethodDeclarationSyntax method)
    {
        List<string> outer = [];
        HashSet<string> typeParameters = [.. method.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText) ?? []];
        for (SyntaxNode? at = method.Parent; at is not null; at = at.Parent)
        {
            switch (at)
            {
                case TypeDeclarationSyntax type:
                    outer.Add(type.Identifier.ValueText + Arity(type.TypeParameterList));
                    typeParameters.UnionWith(type.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText) ?? []);
                    break;
                case BaseNamespaceDeclarationSyntax space:
                    outer.Add(space.Name.ToString());
                    break;
            }
        }
        outer.Reverse();
        string name = string.Join('.', [.. outer, method.Identifier.ValueText + Arity(method.TypeParameterList)]);
        IReadOnlyList<ParameterSyntax> parameters = method.ParameterList.Parameters;
        return parameters.All(p => Plain(p, typeParameters)) ? $"{name}({string.Join(", ", parameters.Select(p => p.Type!.ToString()))})" : name;
    }

    /// <summary>A generic's arity as the debugger reads it: &lt;T, U&gt; for two.</summary>
    private static string Arity(TypeParameterListSyntax? parameters) =>
        parameters is { Parameters.Count: > 0 } list ? $"<{string.Join(", ", list.Parameters.Select(p => p.Identifier.ValueText))}>" : string.Empty;

    /// <summary>Whether a parameter's type, as written, names the type its metadata does.</summary>
    private static bool Plain(ParameterSyntax parameter, HashSet<string> typeParameters) =>
        parameter.Type is { } type
        && !parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword) || m.IsKind(SyntaxKind.ScopedKeyword)
            || m.IsKind(SyntaxKind.ReadOnlyKeyword))
        && type.DescendantNodesAndSelf().All(node => node switch
        {
            NullableTypeSyntax { ElementType: PredefinedTypeSyntax element } => element.Keyword.Kind() is not (SyntaxKind.StringKeyword or SyntaxKind.ObjectKeyword),
            NullableTypeSyntax => false,
            TupleTypeSyntax or PointerTypeSyntax or FunctionPointerTypeSyntax or RefTypeSyntax => false,
            IdentifierNameSyntax { Identifier.ValueText: var named } => !typeParameters.Contains(named) && named != "dynamic",
            _ => true,
        });
}
