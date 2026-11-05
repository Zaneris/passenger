namespace Passenger.Workspace.GDScript;

/// <summary>A type inferred where nothing is written: after a <c>:=</c> variable's or an untyped parameter's name, the
/// offset it goes at and the type.</summary>
public readonly record struct GDScriptInferred(int Offset, GDScriptType Type);

/// <summary>One script's analysis (<see cref="GDScriptAnalyzer.Analyze"/>): each expression's and declaration's type,
/// each name's declaration, and the places it answers for (a name at an offset, the types inferred where none is
/// written). Immutable once made.</summary>
public sealed class GDScriptAnalysis
{
    internal GDScriptAnalysis(GDScriptTree? tree, string path)
    {
        Tree = tree;
        Path = path;
    }

    public GDScriptTree? Tree { get; }

    /// <summary>The script's res:// path.</summary>
    public string Path { get; }

    private GDScriptEffects? effects;

    /// <summary>What the script's expressions do and the order they run in, read once.</summary>
    internal GDScriptEffects Effects => effects ??= new GDScriptEffects(this);

    /// <summary>The script's class and each class it is built on, itself first, to the engine's Object where every base
    /// was read.</summary>
    public IReadOnlyList<GDScriptType> Lineage { get; internal set; } = [];

    /// <summary>The engine's API the script was read against; null where none was dumped.</summary>
    public GodotEngineApi? Engine { get; internal set; }

    internal Dictionary<GDScriptNode, GDScriptType> Types { get; } = [];

    internal Dictionary<GDScriptIdentifier, GDScriptDeclaration> Declared { get; } = [];

    internal Dictionary<GDScriptNode, int> Offsets { get; } = [];

    /// <summary>The names that declare what they name, which are no uses of it.</summary>
    internal HashSet<GDScriptIdentifier> Declaring { get; } = [];

    /// <summary>Godot's findings as the analyzer pushed them, before a project's settings and the script's annotations.</summary>
    internal List<GDScriptFinding> Found { get; } = [];

    /// <summary>The signals named by their strings (<c>emit_signal("hit")</c>), which Godot counts as their uses.</summary>
    internal HashSet<string> SignalsNamed { get; } = new(StringComparer.Ordinal);

    /// <summary>The declarations (by their names' offsets) whose type is written as <c>Variant</c>.</summary>
    internal HashSet<int> WrittenVariant { get; } = [];

    /// <summary>Each name's colour as it was resolved, with the offset its positions count from.</summary>
    internal Dictionary<GDScriptIdentifier, (GDScriptColour Colour, int Offset)> Coloured { get; } = [];

    /// <summary>The calls of functions that wait (coroutines).</summary>
    internal HashSet<GDScriptCall> CoroutineCalls { get; } = [];

    /// <summary>A node's type: an expression's, or a declaration's (a variable's, constant's, parameter's); Variant where none was read.</summary>
    public GDScriptType TypeOf(GDScriptNode node) => Types.GetValueOrDefault(node) ?? GDScriptType.Variant.Soft;

    /// <summary>What a name refers to, where it was resolved.</summary>
    public GDScriptDeclaration? DeclarationOf(GDScriptIdentifier name) => Declared.GetValueOrDefault(name);

    /// <summary>The innermost node at an offset of the text, with the offset its positions count from (the script's
    /// class_name and base names among them); null outside every member.</summary>
    public (GDScriptNode Node, int Offset)? NodeAt(int offset)
    {
        if (Tree is null)
        {
            return null;
        }
        foreach (GDScriptMember member in Tree.Members)
        {
            if (offset >= member.Start && offset <= member.End)
            {
                GDScriptNode at = member.Node;
                int relative = offset - member.Offset;
                for (bool deeper = true; deeper;)
                {
                    deeper = false;
                    foreach (GDScriptNode child in at.Children)
                    {
                        if (child.Start >= 0 && relative >= child.Start && relative <= child.End && child is not GDScriptSuite { Statements.Count: 0 })
                        {
                            at = child;
                            deeper = true;
                            break;
                        }
                    }
                }
                return (at, member.Offset);
            }
        }
        // The script's own class_name and base, written before any member.
        foreach (GDScriptIdentifier name in Tree.Class.Extends.Prepend(Tree.Class.Identifier).OfType<GDScriptIdentifier>())
        {
            if (offset >= name.Start && offset <= name.End)
            {
                return (name, 0);
            }
        }
        return null;
    }

    /// <summary>The locals and parameters of the member an offset is in, declared before it, the nearest last.</summary>
    public IReadOnlyList<GDScriptMemberInfo> LocalsAt(int offset)
    {
        if (Tree?.Members.FirstOrDefault(m => offset >= m.Start && offset <= m.End) is not { Node: { } member } within)
        {
            return [];
        }
        List<(int Start, GDScriptMemberInfo Local)> locals = [];
        foreach ((GDScriptNode node, int at) in Offsets)
        {
            if (at == within.Offset && node != member && node is GDScriptAssignable { Identifier: { } name } && at + name.Start >= within.Start && at + name.Start < offset)
            {
                GDScriptType type = TypeOf(node);
                locals.Add((at + name.Start, new GDScriptMemberInfo(name.Name, node is GDScriptConstant ? "const" : "var", type, new GDScriptDeclaration(Path, at + name.Start, name.Name, type))));
            }
        }
        return [.. locals.OrderByDescending(l => l.Start).Select(l => l.Local)];
    }

    /// <summary>The types inferred where nothing is written: each <c>:=</c> variable's and constant's, and each untyped
    /// parameter's with a default value, after its name.</summary>
    public IReadOnlyList<GDScriptInferred> Inferred()
    {
        List<GDScriptInferred> inferred = [];
        foreach ((GDScriptNode node, int offset) in Offsets)
        {
            if (node is GDScriptAssignable { Identifier: { } name, Type: null, Initializer: not null } assignable
                && (assignable.Infers || node is GDScriptParameter) && TypeOf(node) is { IsVariant: false } type)
            {
                inferred.Add(new GDScriptInferred(offset + name.End, type));
            }
        }
        inferred.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return inferred;
    }

    /// <summary>How Godot's server writes a member or local in its symbols (its detail): <c>var speed: float</c>,
    /// <c>func hit(amount: int) -> void</c>, <c>const LIMIT: int</c>, a type shown only where it is hard, values left out.</summary>
    public string? Detail(GDScriptNode node)
    {
        static string Typed(GDScriptType type) => type.Hard && !type.IsVariant ? $": {type}" : type.Hard && type.IsVariant ? ": Variant" : string.Empty;
        switch (node)
        {
            case GDScriptVariable { Identifier: { } name } variable:
                return $"{(variable.Annotations.Any(a => a.Name.StartsWith("@export", StringComparison.Ordinal)) ? "@export " : string.Empty)}var {name.Name}{Typed(TypeOf(node))}";
            case GDScriptConstant { Identifier: { } name }:
                return $"const {name.Name}{Typed(TypeOf(node))}";
            case GDScriptFunction { Identifier: { } name } function:
                string parameters = string.Join(", ", function.Parameters.Select(p => $"{p.Identifier?.Name}{Typed(TypeOf(p))}"));
                if (function.Rest is { Identifier: { } rest } restParameter)
                {
                    parameters += (parameters.Length > 0 ? ", " : string.Empty) + $"...{rest.Name}: {TypeOf(restParameter)}";
                }
                GDScriptType returned = ReturnOf(function);
                // Godot gives _static_init no return type, whatever is written.
                string returns = name.Name == "_static_init" ? string.Empty
                    : returned.IsVoid ? " -> void" : returned.Hard && !returned.IsVariant || function.ReturnType is not null ? $" -> {returned}" : string.Empty;
                return $"func {name.Name}({parameters}){returns}";
            default:
                return null;
        }
    }

    /// <summary>Godot's findings as a project's settings give them (push_warning, apply_pending_warnings): its parser's errors,
    /// then the analyzer's errors and warnings, less a warning its level, its folder or a <c>@warning_ignore</c> leaves out, one
    /// the project makes an error said as one; each once, by line as Godot orders them (the first on a line its primary one),
    /// on a line errors, the warnings made errors, then warnings. A script read only in part has its parser's errors and a
    /// note where reading stopped: what the analyzer said stood on a class it never saw whole.</summary>
    public IReadOnlyList<GDScriptFinding> Findings(GDScriptWarningSettings settings)
    {
        if (Tree is null)
        {
            return [];
        }
        List<GDScriptFinding> found = [.. Tree.Errors.Select(e => new GDScriptFinding(e.Start, e.End, null, e.Message))];
        if (Tree.Stopped is { } stopped)
        {
            found.Add(new GDScriptFinding(stopped.Start, stopped.End, null, GDScriptParser.TooDeep, IsNote: true));
            return found;
        }
        if (found.Count > 0)
        {
            // Godot's analyzer runs only on a script that parses, and its warnings are given only once it has.
            return found;
        }
        bool silenced = settings.Silences(Path);
        int[] lines = LineStarts(Tree.Text);
        Dictionary<string, HashSet<int>>? ignored = null;
        foreach (GDScriptFinding finding in Found.Distinct())
        {
            if (finding.Code is not { } code)
            {
                found.Add(finding);
                continue;
            }
            GDScriptWarnLevel level = settings.LevelOf(code);
            if (silenced || level == GDScriptWarnLevel.Ignore)
            {
                continue;
            }
            ignored ??= Ignored(lines);
            if (ignored.TryGetValue(code, out HashSet<int>? covered) && covered.Contains(LineOf(lines, finding.Start)))
            {
                continue;
            }
            found.Add(level == GDScriptWarnLevel.Error ? finding with { Message = finding.Message + GDScriptWarnings.TreatedAsError } : finding);
        }
        return [.. found.OrderBy(f => LineOf(lines, f.Start)).ThenBy(f => f.Code is null ? 0 : f.IsError ? 1 : 2)];
    }

    private static int[] LineStarts(string text)
    {
        List<int> starts = [0];
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
        {
            starts.Add(i + 1);
        }
        return [.. starts];
    }

    /// <summary>The line (from 1) of an offset.</summary>
    private static int LineOf(int[] lines, int offset) => Core.Sorted.FirstAfter<int>(lines, offset);

    /// <summary>The lines each warning is ignored on, by its name: from a <c>@warning_ignore</c> to the end of what it is
    /// written before (a variable's value, a block's header, a function's parameters), and between a
    /// <c>@warning_ignore_start</c> and its <c>@warning_ignore_restore</c>, or the file's end.</summary>
    private Dictionary<string, HashSet<int>> Ignored(int[] lines)
    {
        Dictionary<string, HashSet<int>> ignored = new(StringComparer.Ordinal);
        void Cover(GDScriptAnnotation annotation, int from, int to)
        {
            foreach (string code in Codes(annotation))
            {
                HashSet<int> covered = ignored.TryGetValue(code, out HashSet<int>? kept) ? kept : ignored[code] = [];
                for (int line = from; line <= Math.Max(from, to); line++)
                {
                    covered.Add(line);
                }
            }
        }
        void Visit(GDScriptNode node, int offset)
        {
            foreach (GDScriptAnnotation annotation in node.Annotations)
            {
                if (annotation.Name == "@warning_ignore")
                {
                    int start = LineOf(lines, offset + annotation.Start);
                    Cover(annotation, start, IgnoredTo(node, offset, lines, start));
                }
            }
            foreach (GDScriptNode child in node.Children)
            {
                Visit(child, offset);
            }
        }
        foreach (GDScriptAnnotation annotation in Tree!.Class.Annotations.Where(a => a.Name == "@warning_ignore"))
        {
            int classLine = LineOf(lines, Math.Max(0, Tree.Class.Start));
            int first = Tree.Class.Annotations.Min(a => LineOf(lines, a.Start));
            int last = Tree.Class.Annotations.Max(a => LineOf(lines, Math.Max(a.Start, a.End - 1)));
            Cover(annotation, Math.Min(first, classLine), Math.Max(classLine, last));
        }
        foreach (GDScriptMember member in Tree.Members)
        {
            Visit(member.Node, member.Offset);
        }
        Dictionary<string, int> begun = new(StringComparer.Ordinal);
        foreach ((GDScriptAnnotation annotation, int offset) in Tree.Regions)
        {
            int line = LineOf(lines, offset + annotation.Start);
            foreach (string code in Codes(annotation))
            {
                if (annotation.Name == "@warning_ignore_start")
                {
                    begun.TryAdd(code, line);
                }
                else if (begun.Remove(code, out int from))
                {
                    HashSet<int> covered = ignored.TryGetValue(code, out HashSet<int>? kept) ? kept : ignored[code] = [];
                    for (int at = from; at <= Math.Max(from, line); at++)
                    {
                        covered.Add(at);
                    }
                }
            }
        }
        foreach ((string code, int from) in begun)
        {
            HashSet<int> covered = ignored.TryGetValue(code, out HashSet<int>? kept) ? kept : ignored[code] = [];
            for (int at = from; at <= lines.Length; at++)
            {
                covered.Add(at);
            }
        }
        return ignored;
    }

    /// <summary>The warnings an annotation names, in Godot's upper-case form, those Godot has no name for left out.</summary>
    private static IEnumerable<string> Codes(GDScriptAnnotation annotation) =>
        annotation.Arguments.OfType<GDScriptLiteral>().Select(a => a.Value as string).OfType<string>().Select(n => n.ToUpperInvariant()).Where(GDScriptWarnings.Exists);

    /// <summary>The last line a <c>@warning_ignore</c> before a node covers, as Godot's warning_ignore_annotation gives it.</summary>
    private static int IgnoredTo(GDScriptNode node, int offset, int[] lines, int start)
    {
        int End(GDScriptNode? part, GDScriptNode whole) => part is { End: >= 0 } ? LineOf(lines, offset + Math.Max(part.Start, part.End - 1)) : LineOf(lines, offset + whole.Start);
        return node switch
        {
            GDScriptVariable variable => End(variable.Initializer, variable),
            GDScriptFor loop => End(loop.List, loop),
            GDScriptIf branch => End(branch.Condition, branch),
            GDScriptMatch match => End(match.Test, match),
            GDScriptWhile loop => End(loop.Condition, loop),
            GDScriptFunction function => function.Parameters.Select(p => End(p.Initializer ?? (GDScriptNode)p, p)).Append(LineOf(lines, offset + function.Start)).Max(),
            GDScriptMatchBranch branch => branch.Patterns.Select(p => End(p, p)).Append(LineOf(lines, offset + branch.Start)).Max(),
            GDScriptClass @class => @class.Annotations.Select(a => End(a, a)).Append(LineOf(lines, offset + @class.Start)).Max(),
            _ => node.End >= 0 ? LineOf(lines, offset + Math.Max(node.Start, node.End - 1)) : start,
        };
    }

    /// <summary>Every name's colour the analysis knows, in the order of their places.</summary>
    public GDScriptColoured[] Colours()
    {
        GDScriptColoured[] colours = [.. Coloured.Select(c => new GDScriptColoured(c.Value.Offset + c.Key.Start, c.Key.End - c.Key.Start, c.Value.Colour)).Where(c => c.Length > 0)];
        Array.Sort(colours, static (a, b) => a.Start.CompareTo(b.Start));
        return colours;
    }

    /// <summary>A function's declared return type as read in its class; Variant where none is written.</summary>
    internal GDScriptType ReturnOf(GDScriptFunction function) => Types.GetValueOrDefault(function) ?? GDScriptType.Variant;
}
