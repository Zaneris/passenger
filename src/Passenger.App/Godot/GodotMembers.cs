using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Passenger.Languages.CSharp;
using Passenger.Text;
using Passenger.Workspace.GDScript;

namespace Passenger.App.Godot;

/// <summary>A member of a script's class whose value a running game is read for: its name, and the line (from zero) its
/// name is on.</summary>
public readonly record struct GodotScriptMember(string Name, int Line);

/// <summary>A script's class as a file declares it: the script's <c>res://</c> path, the class's name and the line (from
/// zero) it is named on, and its members whose values are read, in the order written.</summary>
public sealed record GodotScriptMembers(string Script, string Class, int ClassLine, IReadOnlyList<GodotScriptMember> Members);

/// <summary>The members of a Godot script's class a file declares, whose values a running game's nodes hold: a C# script's
/// instance fields and auto-properties, a GDScript's <c>var</c>s outside its inner classes. A member with a getter of its
/// own is left out in either language: reading it would run the person's code, so that watching a game changed it.</summary>
public static partial class GodotMembers
{
    /// <summary>The members of the C# script class a file (at <paramref name="path"/>, a <c>res://</c> one) declares: the class
    /// named as the file is, or as the part of its name before a dot (a partial class's other file, Main.Movement.cs of
    /// Main.cs), whose script is the file of the class's name beside it; null where the file declares none.</summary>
    public static GodotScriptMembers? OfCSharp(SyntaxNode root, TextSnapshot text, string path)
    {
        int slash = path.LastIndexOf('/');
        string file = path[(slash + 1)..];
        string name = file[..(file.IndexOf('.') is int dot and >= 0 ? dot : file.Length)];
        ClassDeclarationSyntax? declared = root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.ValueText == name);
        if (declared is null || name.Length == 0)
        {
            return null;
        }
        List<GodotScriptMember> members = [];
        foreach (MemberDeclarationSyntax member in declared.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax field when !field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)):
                    foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                    {
                        members.Add(new GodotScriptMember(variable.Identifier.ValueText, Line(text, variable.Identifier)));
                    }
                    break;
                case PropertyDeclarationSyntax property when IsAuto(property):
                    members.Add(new GodotScriptMember(property.Identifier.ValueText, Line(text, property.Identifier)));
                    break;
            }
        }
        return new GodotScriptMembers($"{path[..(slash + 1)]}{name}.cs", name, Line(text, declared.Identifier), members);
    }

    /// <summary>Whether a property's getter is the compiler's own, which reading it runs nothing of the person's.</summary>
    private static bool IsAuto(PropertyDeclarationSyntax property) =>
        property.ExpressionBody is null
        && !property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.AbstractKeyword))
        && property.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) is { Body: null, ExpressionBody: null };

    private static int Line(TextSnapshot text, SyntaxToken token) => text.GetLineFromOffset(Math.Min(token.SpanStart, text.Length));

    /// <summary>The members of a GDScript file (its <c>res://</c> path), as Godot's parser reads them: each <c>var</c> of its
    /// class, annotated or not, but a static one and one with a getter of its own, whichever way that is written; its class
    /// named where <c>class_name</c> names it, else by its file, on that line or its <c>extends</c> line, else the first.</summary>
    public static GodotScriptMembers OfGdScript(TextSnapshot text, string path)
    {
        GDScriptTree tree = GDScriptTree.Parse(text.ToString());
        List<GodotScriptMember> members = [];
        foreach (GDScriptMember member in tree.Members)
        {
            if (member.Node is GDScriptVariable { IsStatic: false, Getter: null, GetterName: null, Identifier: { } variable })
            {
                members.Add(new GodotScriptMember(variable.Name, text.GetLineFromOffset(member.Offset + variable.Start)));
            }
        }
        string file = path[(path.LastIndexOf('/') + 1)..];
        if (tree.Class.Identifier is { } named)
        {
            return new GodotScriptMembers(path, named.Name, text.GetLineFromOffset(Math.Min(named.Start, text.Length)), members);
        }
        int classLine = 0;
        for (int line = 0; line < text.LineCount; line++)
        {
            LineSpan span = text.GetLine(line);
            if (span.Length >= 7 && text.GetText(span.Start, 7) == "extends")
            {
                classLine = line;
                break;
            }
        }
        return new GodotScriptMembers(path, Path.GetFileNameWithoutExtension(file), classLine, members);
    }

    /// <summary>What of a GDScript's own class Godot's reading of a node runs: each <c>var</c> with a getter of its own,
    /// static ones too, then its <c>_get</c> and <c>_get_property_list</c> where it writes them.</summary>
    public static IReadOnlyList<string> RunByReading(GDScriptTree tree) =>
    [
        .. tree.Members.Select(m => m.Node).OfType<GDScriptVariable>().Where(v => v.Getter is not null || v.GetterName is not null).Select(v => v.Identifier?.Name).OfType<string>(),
        .. tree.Members.Select(m => m.Node).OfType<GDScriptFunction>().Select(f => f.Identifier?.Name).OfType<string>().Where(n => n is "_get" or "_get_property_list"),
    ];

    /// <summary>What of a C# script's class (the file's class of its name, <paramref name="file"/>) Godot's reading of a node
    /// runs: each property with a getter of the person's, then its <c>_Get</c> and <c>_GetPropertyList</c> where it writes them.</summary>
    public static IReadOnlyList<string> RunByReading(SyntaxNode root, string file)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        IReadOnlyList<MemberDeclarationSyntax> members = [.. root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.ValueText == name).SelectMany(c => c.Members)];
        return
        [
            .. members.OfType<PropertyDeclarationSyntax>().Where(p => !IsAuto(p) && !p.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.AbstractKeyword)) && HasGetter(p))
                .Select(p => p.Identifier.ValueText),
            .. members.OfType<MethodDeclarationSyntax>().Select(m => m.Identifier.ValueText).Where(n => n is "_Get" or "_GetPropertyList"),
        ];
    }

    private static bool HasGetter(PropertyDeclarationSyntax property) =>
        property.ExpressionBody is not null || property.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) == true;

    /// <summary>What Godot's reading of a node running a script (a <c>res://</c> path) runs of it and of the scripts it extends,
    /// by path or by class name, as <paramref name="read"/> reads a <c>res://</c> path (null for none); nearest first, each once.</summary>
    public static IReadOnlyList<string> RunByReading(string script, Func<string, string?> read)
    {
        Dictionary<string, string> classes = read(ClassCache) is { } cache
            ? CachedClass().Matches(cache).DistinctBy(m => m.Groups[1].Value).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal)
            : [];
        List<string> run = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        string? at = script;
        while (at is not null && seen.Add(at) && read(at) is { } text)
        {
            if (at.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                run.AddRange(RunByReading(CSharpParsing.Parse(text, CSharpParsing.Default).GetRoot(), at));
                break;
            }
            GDScriptTree tree = GDScriptTree.Parse(text);
            run.AddRange(RunByReading(tree));
            at = tree.Class.ExtendsPath is { Length: > 0 } written ? Beside(written, at)
                : tree.Class.Extends is [{ } named, ..] ? classes.GetValueOrDefault(named.Name) : null;
        }
        return [.. run.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>A script <c>extends</c> names by path, written whole or beside the script it is written in.</summary>
    private static string Beside(string written, string script) =>
        written.Contains("://", StringComparison.Ordinal) ? written : $"{script[..(script.LastIndexOf('/') + 1)]}{written}";

    /// <summary>The file Godot lists a project's <c>class_name</c>s in.</summary>
    private const string ClassCache = "res://.godot/global_script_class_cache.cfg";

    [GeneratedRegex(@"""class"": &""([^""]+)""[^}]*?""path"": ""([^""]+)""")]
    private static partial Regex CachedClass();
}
