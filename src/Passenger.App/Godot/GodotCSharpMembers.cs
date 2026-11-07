using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;

namespace Passenger.App.Godot;

/// <summary>A member of a node's C# script a GDScript reaches (<c>$Player.TakeDamage</c>): its name as the GDScript writes it,
/// its kind as GDScript's own members are named (func, var, const, signal), what it is as C# declares it, and where its
/// name is written (offsets in the file's text).</summary>
public sealed record GodotCSharpMember(string Name, string Kind, string Shown, string Path, int Start, int End);

/// <summary>The members of the C# script class a file declares that a GDScript reaches on a node running it: the class
/// named as the file is, its public methods, properties, fields, constants and <c>[Signal]</c> delegates (by the signal's
/// name, the delegate's less EventHandler), read from the file's syntax alone, so before the C# server has loaded it, and
/// past what Godot's own server knows of a script it has not built.</summary>
public static class GodotCSharpMembers
{
    public static IReadOnlyList<GodotCSharpMember> Of(string path, string text)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        SyntaxNode root = CSharpParsing.Parse(text, CSharpParsing.Default).GetRoot();
        ClassDeclarationSyntax? declared = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.ValueText == name);
        if (declared is null)
        {
            return [];
        }
        List<GodotCSharpMember> members = [];
        void Add(SyntaxToken identifier, string kind, string shown, string? named = null) =>
            members.Add(new GodotCSharpMember(named ?? identifier.ValueText, kind, shown, path, identifier.SpanStart, identifier.Span.End));
        foreach (MemberDeclarationSyntax member in declared.Members)
        {
            bool shared = member.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
            switch (member)
            {
                case DelegateDeclarationSyntax signal when signal.AttributeLists.SelectMany(l => l.Attributes).Any(a => a.Name.ToString() is "Signal" or "SignalAttribute" or "Godot.Signal")
                    && signal.Identifier.ValueText.EndsWith("EventHandler", StringComparison.Ordinal):
                    string signalName = signal.Identifier.ValueText[..^"EventHandler".Length];
                    Add(signal.Identifier, "signal", $"signal {signalName}{signal.ParameterList}", signalName);
                    break;
                case MethodDeclarationSyntax method when shared:
                    Add(method.Identifier, "func", $"{method.ReturnType} {method.Identifier.ValueText}{method.ParameterList}");
                    break;
                case PropertyDeclarationSyntax property when shared:
                    Add(property.Identifier, "var", $"{property.Type} {property.Identifier.ValueText}");
                    break;
                case FieldDeclarationSyntax field when shared:
                    bool constant = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword));
                    foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                    {
                        Add(variable.Identifier, constant ? "const" : "var", $"{(constant ? "const " : string.Empty)}{field.Declaration.Type} {variable.Identifier.ValueText}");
                    }
                    break;
            }
        }
        return members;
    }
}
