using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A fix of one of Godot's findings: what it is called and the edits it makes, together, in the script's text.</summary>
public sealed record GDScriptFix(string Title, IReadOnlyList<TextEdit> Edits);

/// <summary>The fixes Passenger offers for Godot's findings in a script, read from its analysis: an unused variable, constant
/// or parameter prefixed with an underscore (its uses too); a declaration's type written where it is only inferred or none
/// is given; <c>await</c> put before a coroutine's call; and a return of the declared type's default put last where not
/// every way through a function returns. Pure.</summary>
public static class GDScriptFixes
{
    public static IReadOnlyList<GDScriptFix> Of(GDScriptAnalysis analysis, GDScriptFinding finding)
    {
        if (analysis.Tree is not { } tree)
        {
            return [];
        }
        string text = tree.Text;
        switch (finding.Code)
        {
            case "UNUSED_VARIABLE" or "UNUSED_LOCAL_CONSTANT" or "UNUSED_PARAMETER":
                return Underscored(analysis, finding) is { } prefixed ? [prefixed] : [];
            case "UNTYPED_DECLARATION" or "INFERRED_DECLARATION":
                return Annotated(analysis, finding, text) is { } annotated ? [annotated] : [];
            case "MISSING_AWAIT":
                return [new GDScriptFix("Add 'await'", [new TextEdit(finding.Start, 0, "await ")])];
            case null when finding.Message.EndsWith("so it must be called with \"await\".", StringComparison.Ordinal):
                return [new GDScriptFix("Add 'await'", [new TextEdit(finding.Start, 0, "await ")])];
            case null when finding.Message == "Not all code paths return a value.":
                return Returned(analysis, finding, text) is { } returned ? [returned] : [];
            default:
                return [];
        }
    }

    /// <summary>The declaration a finding's range starts with, its name, and the offset its member's positions count from.</summary>
    private static (GDScriptNode Node, GDScriptIdentifier Name, int Offset)? DeclarationIn(GDScriptAnalysis analysis, GDScriptFinding finding)
    {
        foreach (GDScriptMember member in analysis.Tree!.Members)
        {
            if (finding.Start < member.Start || finding.Start > member.End)
            {
                continue;
            }
            GDScriptNode? found = Find(member.Node, member.Offset, finding);
            return found switch
            {
                GDScriptAssignable { Identifier: { } name } assignable => (assignable, name, member.Offset),
                GDScriptIdentifier name => (name, name, member.Offset),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>The outermost node lying exactly over a finding's range, a declaring name or a declaration.</summary>
    private static GDScriptNode? Find(GDScriptNode node, int offset, GDScriptFinding finding)
    {
        if (node.Start >= 0 && offset + node.Start == finding.Start && offset + node.End == finding.End && node is GDScriptAssignable or GDScriptIdentifier)
        {
            return node;
        }
        foreach (GDScriptNode child in node.Children)
        {
            if (child.Start >= 0 && offset + child.Start <= finding.Start && finding.Start <= offset + child.End && Find(child, offset, finding) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static GDScriptFix? Underscored(GDScriptAnalysis analysis, GDScriptFinding finding)
    {
        if (DeclarationIn(analysis, finding) is not (_, { } name, int offset) || analysis.DeclarationOf(name) is not { } declared)
        {
            return null;
        }
        List<TextEdit> edits = [];
        foreach (GDScriptMember member in analysis.Tree!.Members.Where(m => m.Offset == offset))
        {
            Collect(member.Node);
        }
        void Collect(GDScriptNode at)
        {
            if (at is GDScriptIdentifier used && used.Name == name.Name && analysis.DeclarationOf(used) is { } same && same.Start == declared.Start && same.Script == declared.Script)
            {
                edits.Add(new TextEdit(offset + used.Start, 0, "_"));
            }
            foreach (GDScriptNode child in at.Children)
            {
                Collect(child);
            }
        }
        if (!edits.Any(e => e.Start == declared.Start))
        {
            edits.Add(new TextEdit(declared.Start, 0, "_"));
        }
        return new GDScriptFix($"Prefix '{name.Name}' with an underscore", [.. edits.DistinctBy(e => e.Start).OrderBy(e => e.Start)]);
    }

    private static GDScriptFix? Annotated(GDScriptAnalysis analysis, GDScriptFinding finding, string text)
    {
        if (DeclarationIn(analysis, finding) is not ({ } declaration, { } name, int offset))
        {
            return null;
        }
        GDScriptType type = analysis.TypeOf(declaration);
        if (Written(type) is not { } written)
        {
            return null;
        }
        int nameEnd = offset + name.End;
        if (declaration is GDScriptAssignable { Initializer: { } value })
        {
            int valueStart = offset + value.Start;
            // Between the name and its value: " := ", " = ", or ": = ".
            if (text.AsSpan(nameEnd, valueStart - nameEnd).Trim().ToString() is ":=" or "=" or ": =")
            {
                return new GDScriptFix($"Add type annotation ': {written}'", [new TextEdit(nameEnd, valueStart - nameEnd, $": {written} = ")]);
            }
            return null;
        }
        return new GDScriptFix($"Add type annotation ': {written}'", [new TextEdit(nameEnd, 0, $": {written}")]);
    }

    /// <summary>A type as a script may write it; null for one it cannot name (Variant, an enum or class of a script without
    /// a <c>class_name</c>).</summary>
    public static string? Written(GDScriptType type)
    {
        if (type.IsVariant || type.IsVoid || type.Meta)
        {
            return null;
        }
        string shown = type.ToString();
        bool nameable = type.Kind switch
        {
            GDScriptTypeKind.Builtin => type.Name != "Nil" && type.Elements.All(e => Written(e) is not null),
            GDScriptTypeKind.Native => true,
            GDScriptTypeKind.Class => !type.Name.StartsWith(GodotResPaths.Scheme, StringComparison.Ordinal) && type.Class is { Identifier: not null },
            GDScriptTypeKind.Enum => !shown.Contains(".gd", StringComparison.Ordinal) && !shown.Contains('<', StringComparison.Ordinal),
            _ => false,
        };
        return nameable ? shown : null;
    }

    private static GDScriptFix? Returned(GDScriptAnalysis analysis, GDScriptFinding finding, string text)
    {
        foreach (GDScriptMember member in analysis.Tree!.Members)
        {
            if (Function(member.Node, member.Offset, finding) is { Body: { Statements: [.., { } last] } body } function)
            {
                GDScriptType returns = analysis.ReturnOf(function);
                string value = Default(returns);
                int first = member.Offset + body.Statements[0].Start;
                int lineStart = text.LastIndexOf('\n', Math.Max(0, first - 1)) + 1;
                string indent = text[lineStart..first];
                if (indent.Trim().Length > 0)
                {
                    return null;
                }
                int end = member.Offset + last.End;
                return new GDScriptFix($"Add 'return {value}'", [new TextEdit(end, 0, $"\n{indent}return {value}")]);
            }
        }
        return null;
    }

    private static GDScriptFunction? Function(GDScriptNode node, int offset, GDScriptFinding finding)
    {
        if (node is GDScriptFunction { ReturnType: { } returns } function && offset + returns.Start == finding.Start)
        {
            return function;
        }
        foreach (GDScriptNode child in node.Children)
        {
            if (Function(child, offset, finding) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    /// <summary>The value a variable of a type holds before it is given one, as a script writes it.</summary>
    private static string Default(GDScriptType type) => type switch
    {
        { Kind: GDScriptTypeKind.Builtin, Name: "int" } => "0",
        { Kind: GDScriptTypeKind.Builtin, Name: "float" } => "0.0",
        { Kind: GDScriptTypeKind.Builtin, Name: "bool" } => "false",
        { Kind: GDScriptTypeKind.Builtin, Name: "String" } => "\"\"",
        { Kind: GDScriptTypeKind.Builtin, Name: "StringName" } => "&\"\"",
        { Kind: GDScriptTypeKind.Builtin, Name: "NodePath" } => "^\"\"",
        { Kind: GDScriptTypeKind.Builtin, Name: "Array" } => "[]",
        { Kind: GDScriptTypeKind.Builtin, Name: "Dictionary" } => "{}",
        { Kind: GDScriptTypeKind.Builtin, Name: not "Nil" } builtin => $"{builtin.Name}()",
        { Kind: GDScriptTypeKind.Enum } => "0",
        _ => "null",
    };
}
