using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A member of a script's own class a refactoring works on: its node, the offset its positions count from, and its
/// name.</summary>
public sealed record GDScriptTarget(string Script, GDScriptNode Node, int Offset, GDScriptIdentifier Name)
{
    /// <summary>Where its name is, as the analyzer's declarations say it.</summary>
    public int Start => Offset + Name.Start;

    public string Named => Name.Name;
}

/// <summary>A member's uses across the scripts: each name resolved to it (with the call it makes, where it is called), each
/// string a call names it by, and each place that may use it unseen.</summary>
public sealed record GDScriptUses(IReadOnlyList<GDScriptUses.Use> Names, IReadOnlyList<GDScriptUses.Named> Strings, IReadOnlyList<GDScriptPlace> Unsure)
{
    /// <summary>A name resolved to the member, and the call it is called by (its callee), with the call's receiver.</summary>
    public sealed record Use(GDScriptRead Script, GDScriptIdentifier Name, int Offset, GDScriptCall? Call, GDScriptExpression? Receiver);

    /// <summary>A string naming the member as an argument of a call (<c>call("f")</c>).</summary>
    public sealed record Named(GDScriptRead Script, GDScriptLiteral Literal, int Offset, GDScriptCall Call);

    public IEnumerable<GDScriptPlace> Places =>
        Names.Select(u => Place(u.Script, u.Offset, u.Call is { } c ? c : u.Name, u.Call is null ? "takes it as a value" : "calls it"))
            .Concat(Strings.Select(s => Place(s.Script, s.Offset, s.Call, "names it in a string")));

    internal static GDScriptPlace Place(GDScriptRead script, int offset, GDScriptNode node, string what) =>
        new(script.Script, offset + node.Start, offset + node.End, what);
}

public static partial class GDScriptRefactorings
{
    /// <summary>The engine's functions a script's class overrides that Godot itself calls, whose signature Godot fixes.</summary>
    private static readonly HashSet<string> Virtuals = new(StringComparer.Ordinal)
    {
        "_init", "_ready", "_process", "_physics_process", "_enter_tree", "_exit_tree", "_input", "_unhandled_input", "_unhandled_key_input",
        "_shortcut_input", "_gui_input", "_draw", "_notification", "_get", "_set", "_get_property_list", "_to_string", "_static_init",
        "_integrate_forces", "_get_configuration_warnings", "_can_drop_data", "_drop_data", "_get_drag_data", "_has_point", "_validate_property",
    };

    /// <summary>The member of a script's own class a name at an offset declares or refers to; null for none.</summary>
    public static GDScriptTarget? MemberAt(IReadOnlyList<GDScriptRead> scripts, GDScriptRead read, int offset)
    {
        GDScriptIdentifier? named = null;
        foreach (GDScriptMember member in read.Tree.Members)
        {
            if (offset >= member.Start && offset <= member.End)
            {
                named = Below(member.Node).OfType<GDScriptIdentifier>().FirstOrDefault(i => offset >= member.Offset + i.Start && offset <= member.Offset + i.End);
                if (named is not null && NameOf(member.Node) == named.Name && DeclaringName(member.Node) == named)
                {
                    return new GDScriptTarget(read.Script, member.Node, member.Offset, named);
                }
                break;
            }
        }
        if (named is null || read.Analysis.DeclarationOf(named) is not { Script: { } script } declared)
        {
            return null;
        }
        GDScriptRead? declaring = scripts.FirstOrDefault(s => s.Script == script);
        return declaring?.Tree.Members.Where(m => DeclaringName(m.Node) is { } n && m.Offset + n.Start == declared.Start)
            .Select(m => new GDScriptTarget(script, m.Node, m.Offset, DeclaringName(m.Node)!)).FirstOrDefault();
    }

    private static GDScriptIdentifier? DeclaringName(GDScriptNode node) => node switch
    {
        GDScriptFunction function => function.Identifier,
        GDScriptAssignable assignable => assignable.Identifier,
        GDScriptSignal signal => signal.Identifier,
        GDScriptEnum enumerated => enumerated.Identifier,
        GDScriptClass inner => inner.Identifier,
        _ => null,
    };

    /// <summary>A member's uses in every script: the names the analyzer resolves to it, the strings calls name it by, and,
    /// as unsure, a call naming a method by what is built at run time, and a member of its name the analyzer resolves to
    /// nothing (on a value of an engine class, or of no type written).</summary>
    public static GDScriptUses UsesOf(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target)
    {
        List<GDScriptUses.Use> names = [];
        List<GDScriptUses.Named> strings = [];
        List<GDScriptPlace> unsure = [];
        foreach (GDScriptRead script in scripts)
        {
            foreach (GDScriptMember member in script.Tree.Members)
            {
                Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(member.Node);
                foreach ((GDScriptNode node, GDScriptNode? parent) in parents)
                {
                    switch (node)
                    {
                        case GDScriptIdentifier identifier when script.Analysis.DeclarationOf(identifier) is { } declared && declared.Script == target.Script
                            && declared.Start == target.Start && !(script.Script == target.Script && member.Offset + identifier.Start == target.Start):
                            (GDScriptCall? calling, GDScriptExpression? receiver) = parent switch
                            {
                                GDScriptCall direct when direct.Callee == identifier => (direct, null),
                                GDScriptSubscript { IsAttribute: true } attribute when attribute.Attribute == identifier && parents.GetValueOrDefault(attribute) is GDScriptCall through
                                    && through.Callee == attribute => (through, attribute.Base),
                                _ => ((GDScriptCall?)null, (GDScriptExpression?)null),
                            };
                            names.Add(new GDScriptUses.Use(script, identifier, member.Offset, calling, receiver));
                            break;
                        case GDScriptSubscript { IsAttribute: true, Attribute: { } attribute } when attribute.Name == target.Named && script.Analysis.DeclarationOf(attribute) is null:
                            // A value typed as an engine class, or not at all, may hold a node running the script.
                            unsure.Add(GDScriptUses.Place(script, member.Offset, node, $"its '{target.Named}' is on a value whose script is not read"));
                            break;
                        case GDScriptCall { FunctionName: { } function, Arguments: var arguments } call when NamingCalls.TryGetValue(function, out int index) && arguments.Count > index:
                            GDScriptExpression argument = arguments[index];
                            if (argument is GDScriptLiteral { Value: string text } literal)
                            {
                                if (text == target.Named)
                                {
                                    strings.Add(new GDScriptUses.Named(script, literal, member.Offset, call));
                                }
                            }
                            else if (target.Node is GDScriptFunction)
                            {
                                unsure.Add(GDScriptUses.Place(script, member.Offset, call, "names a method by what it reads at run time"));
                            }
                            break;
                        case GDScriptCall { FunctionName: "emit_signal" or "connect" or "disconnect" or "has_signal", Arguments: [GDScriptLiteral { Value: string signal } named, ..] } emitting
                            when signal == target.Named && target.Node is GDScriptSignal:
                            strings.Add(new GDScriptUses.Named(script, named, member.Offset, emitting));
                            break;
                    }
                }
            }
        }
        return new GDScriptUses(names, strings, unsure);
    }

    /// <summary>The functions of other scripts with the member's name, which may override it or be overridden by it.</summary>
    private static IEnumerable<GDScriptPlace> Namesakes(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target) =>
        scripts.Where(s => s.Script != target.Script).SelectMany(s => s.Tree.Members.Where(m => m.Node is GDScriptFunction f && f.Identifier?.Name == target.Named)
            .Select(m => new GDScriptPlace(s.Script, m.Start, m.End, $"declares a '{target.Named}' too, which may override it")));

    /// <summary>A member's lines and the blank lines after it (its doc comments and annotations before it with it), as the
    /// range a deletion takes out; for the last member, the blank lines before it instead.</summary>
    private static TextRange MemberLines(string text, GDScriptTarget target)
    {
        int first = target.Offset + (target.Node.Annotations.Count > 0 ? Math.Min(target.Node.Start, target.Node.Annotations.Min(a => a.Start)) : target.Node.Start);
        int start = LineStart(text, first);
        // Its doc comments, the ## lines just above it.
        while (start > 0)
        {
            int above = LineStart(text, start - 1);
            if (!text[above..start].TrimStart().StartsWith("##", StringComparison.Ordinal))
            {
                break;
            }
            start = above;
        }
        int end = NextLine(text, target.Offset + target.Node.End);
        int next = end;
        while (next < text.Length && text[next..NextLine(text, next)].Trim().Length == 0)
        {
            next = NextLine(text, next);
        }
        if (next < text.Length)
        {
            return TextRange.FromBounds(start, next);
        }
        while (start > 0 && text[LineStart(text, start - 1)..start].Trim().Length == 0)
        {
            start = LineStart(text, start - 1);
        }
        return TextRange.FromBounds(start, text.Length);
    }

    /// <summary>Safe Delete: a member taken out of its script, its lines and doc comments with it, where nothing names it: no
    /// script, no string, no place <paramref name="elsewhere"/> (a scene's connection, C#), none unsure; else refused,
    /// naming every use.</summary>
    public static GDScriptRefactored SafeDelete(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, IReadOnlyList<GDScriptPlace> elsewhere)
    {
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. uses.Places, .. uses.Unsure, .. elsewhere];
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"'{target.Named}' is still used ({Places(blocking)})", blocking);
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        TextRange lines = MemberLines(own.Text, target);
        return GDScriptRefactored.Of(target.Script, [new TextEdit(lines.Start, lines.Length, string.Empty)]);
    }

    /// <summary>A short list of places, by script and line.</summary>
    public static string Places(IReadOnlyList<GDScriptPlace> places) =>
        string.Join("; ", places.Take(4).Select(p => $"{p.Script}: {p.What}")) + (places.Count > 4 ? $"; and {places.Count - 4} more" : string.Empty);

    /// <summary>Inline Function: each call of a function whose body is one statement given that statement, its parameters
    /// read as the arguments passed (a default where none is), the function then taken out. Refused where it is used other
    /// than by a call on itself, named by a string or <paramref name="elsewhere"/>, awaits, or where an argument that is
    /// more than a name or a literal would be read other than once.</summary>
    public static GDScriptRefactored InlineFunction(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, IReadOnlyList<GDScriptPlace> elsewhere)
    {
        if (target.Node is not GDScriptFunction { Body.Statements: [var only] } function || function.Rest is not null)
        {
            return GDScriptRefactored.No(target.Node is GDScriptFunction ? "only a function of one statement inlines" : "put the caret on a function");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        GDScriptExpression? body = only switch
        {
            GDScriptReturn { Value: { } value } => value,
            GDScriptExpression statement => statement,
            _ => null,
        };
        if (body is null || Below(only).Any(n => n is GDScriptAwait or GDScriptLambda or GDScriptAssignment))
        {
            return GDScriptRefactored.No("only a function returning an expression, or of one call, inlines");
        }
        bool returns = only is GDScriptReturn;
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. uses.Strings.Select(s => GDScriptUses.Place(s.Script, s.Offset, s.Call, "names it in a string")), .. uses.Unsure, .. elsewhere,
            .. uses.Names.Where(u => u.Call is null || u.Receiver is not (null or GDScriptSelf)).Select(u => GDScriptUses.Place(u.Script, u.Offset, u.Name, u.Call is null ? "takes it as a value" : "calls it on another object"))];
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"'{target.Named}' is used where it can't be inlined ({Places(blocking)})", blocking);
        }
        // Each parameter's reads in the body.
        Dictionary<int, List<GDScriptIdentifier>> reads = [];
        foreach (GDScriptIdentifier name in Below(body).OfType<GDScriptIdentifier>())
        {
            if (own.Analysis.DeclarationOf(name) is { } declared && declared.Script == target.Script
                && function.Parameters.FindIndex(p => p.Identifier is { } n && target.Offset + n.Start == declared.Start) is >= 0 and int index)
            {
                (reads.TryGetValue(index, out List<GDScriptIdentifier>? list) ? list : reads[index] = []).Add(name);
            }
        }
        Dictionary<GDScriptNode, GDScriptNode?> bodyParents = Parents(only);
        Dictionary<string, List<TextEdit>> edits = [];
        foreach (GDScriptUses.Use use in uses.Names)
        {
            GDScriptCall call = use.Call!;
            string text = use.Script.Text;
            if (uses.Names.Any(other => other != use && other.Script == use.Script && other.Offset == use.Offset && call.Start < other.Call!.Start && other.Call.End <= call.End))
            {
                return GDScriptRefactored.No("its calls nest", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it inside a call of it")]);
            }
            Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(use.Script.Tree.Members.First(m => m.Offset == use.Offset).Node);
            if (!returns && parents.GetValueOrDefault(call) is not GDScriptSuite)
            {
                return GDScriptRefactored.No("it returns nothing, and a call reads its value", [GDScriptUses.Place(use.Script, use.Offset, call, "reads its value")]);
            }
            // The body's text with each parameter read as its argument, from its end back.
            int bodyStart = target.Offset + body.Start;
            string inlined = own.Text[bodyStart..(target.Offset + body.End)];
            List<(int Start, int End, string Text)> swaps = [];
            for (int i = 0; i < function.Parameters.Count; i++)
            {
                GDScriptParameter parameter = function.Parameters[i];
                GDScriptExpression? argument = i < call.Arguments.Count ? call.Arguments[i] : null;
                string? given = argument is not null ? text[(use.Offset + argument.Start)..(use.Offset + argument.End)]
                    : parameter.Initializer is { } fallback ? own.Text[(target.Offset + fallback.Start)..(target.Offset + fallback.End)] : null;
                if (given is null)
                {
                    return GDScriptRefactored.No($"a call passes no '{parameter.Identifier?.Name}'", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
                }
                List<GDScriptIdentifier> read = reads.GetValueOrDefault(i) ?? [];
                bool simple = argument is null or GDScriptIdentifier or GDScriptLiteral or GDScriptSelf;
                if (!simple && read.Count != 1)
                {
                    return GDScriptRefactored.No($"'{given}' would be read {read.Count} times", [GDScriptUses.Place(use.Script, use.Offset, call, "calls it")]);
                }
                bool compound = argument is not null ? Compound(argument) : parameter.Initializer is { } d && Compound(d);
                foreach (GDScriptIdentifier name in read)
                {
                    swaps.Add((target.Offset + name.Start - bodyStart, target.Offset + name.End - bodyStart, Placed(given, name, compound, bodyParents)));
                }
            }
            foreach ((int start, int end, string swapped) in swaps.OrderByDescending(s => s.Start))
            {
                inlined = inlined[..start] + swapped + inlined[end..];
            }
            string placed = returns ? Placed(inlined, call, Compound(body), parents) : inlined;
            (edits.TryGetValue(use.Script.Script, out List<TextEdit>? list) ? list : edits[use.Script.Script] = [])
                .Add(new TextEdit(use.Offset + call.Start, call.End - call.Start, placed));
        }
        TextRange lines = MemberLines(own.Text, target);
        (edits.TryGetValue(target.Script, out List<TextEdit>? ownEdits) ? ownEdits : edits[target.Script] = []).Add(new TextEdit(lines.Start, lines.Length, string.Empty));
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.OrderBy(x => x.Start)]))], null, []);
    }

    /// <summary>A function's signature as Change Signature shows it: each parameter's name, type and default as written.</summary>
    public static IReadOnlyList<GDScriptParameterChange> ParametersOf(GDScriptRead own, GDScriptTarget target) =>
        target.Node is GDScriptFunction function
            ? [.. function.Parameters.Select((p, i) => new GDScriptParameterChange(i, p.Identifier?.Name ?? string.Empty, Written(own.Text, target.Offset, p.Type), Written(own.Text, target.Offset, p.Initializer), string.Empty))]
            : [];

    private static string Written(string text, int offset, GDScriptNode? node) => node is null ? string.Empty : text[(offset + node.Start)..(offset + node.End)];

    /// <summary>Change Signature of a function: its name and parameters as given, each call and string naming it updated,
    /// a renamed parameter's reads renamed. Refused where a removed parameter is read, another script declares its name, it
    /// is Godot's own, or, its parameters changing, it is a value (but one <paramref name="followed"/> names: a signal's
    /// connection changing with it), named at run time, read unseen or called from <paramref name="elsewhere"/>.</summary>
    public static GDScriptRefactored ChangeSignature(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, string name, IReadOnlyList<GDScriptParameterChange> parameters,
        IReadOnlyList<GDScriptPlace> elsewhere, IReadOnlySet<GDScriptNode>? followed = null)
    {
        if (target.Node is not GDScriptFunction { ParametersStart: >= 0, ParametersEnd: > 0 } function || function.IsLambda)
        {
            return GDScriptRefactored.No("put the caret on a function");
        }
        if (function.Rest is not null)
        {
            return GDScriptRefactored.No("the function takes the rest of its arguments");
        }
        if ((WhyNotName(name) ?? parameters.Select(p => WhyNotName(p.Name)).FirstOrDefault(w => w is not null)) is { } bad)
        {
            return GDScriptRefactored.No(bad);
        }
        if (parameters.SkipWhile(p => p.Default.Length == 0).FirstOrDefault(p => p.Default.Length == 0) is { } undefaulted)
        {
            return GDScriptRefactored.No($"'{undefaulted.Name}' has no default after a parameter with one, which GDScript refuses");
        }
        if (parameters.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return GDScriptRefactored.No($"two parameters are named '{twice.Key}'");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        IReadOnlyList<GDScriptParameterChange> was = ParametersOf(own, target);
        // What calls pass changes: an order, a parameter added or taken out, or a default a call leans on taken away.
        bool reshaped = parameters.Count != was.Count || parameters.Select((p, i) => p.From != i || p.Default.Length == 0 && was[p.From].Default.Length > 0).Any(x => x);
        bool renamed = name != target.Named;
        if (reshaped && Virtuals.Contains(target.Named) || renamed && Virtuals.Contains(target.Named))
        {
            return GDScriptRefactored.No($"'{target.Named}' is Godot's own, which Godot calls as it declares it");
        }
        if (renamed && own.Tree.Members.Any(m => NameOf(m.Node) == name))
        {
            return GDScriptRefactored.No($"the script already has a member '{name}'");
        }
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. Namesakes(scripts, target), .. uses.Unsure];
        if (reshaped)
        {
            blocking.AddRange(elsewhere);
            blocking.AddRange(uses.Names.Where(u => u.Call is null && followed?.Contains(u.Name) != true)
                .Select(u => GDScriptUses.Place(u.Script, u.Offset, u.Name, "takes it as a value, whose callers can't be followed")));
            blocking.AddRange(uses.Strings.Where(s => s.Call.FunctionName is not ("call" or "call_deferred" or "rpc" or "rpc_id" or "has_method") && followed?.Contains(s.Literal) != true)
                .Select(s => GDScriptUses.Place(s.Script, s.Offset, s.Call, "names it for a later call")));
        }
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"Change Signature can't follow every use ({Places(blocking)})", blocking);
        }
        // A parameter removed must be read no more.
        Dictionary<int, List<GDScriptIdentifier>> reads = [];
        foreach (GDScriptIdentifier read in function.Body is { } body ? Below(body).OfType<GDScriptIdentifier>() : [])
        {
            if (own.Analysis.DeclarationOf(read) is { } declared && declared.Script == target.Script
                && function.Parameters.FindIndex(p => p.Identifier is { } n && target.Offset + n.Start == declared.Start) is >= 0 and int index)
            {
                (reads.TryGetValue(index, out List<GDScriptIdentifier>? list) ? list : reads[index] = []).Add(read);
            }
        }
        foreach ((int index, List<GDScriptIdentifier> read) in reads)
        {
            if (!parameters.Any(p => p.From == index))
            {
                return GDScriptRefactored.No($"'{was[index].Name}' is still read in the function", [GDScriptUses.Place(own, target.Offset, read[0], "reads it")]);
            }
        }
        if (parameters.FirstOrDefault(p => p.From < 0 && p.CallValue.Length == 0 && p.Default.Length == 0) is { } unpassed)
        {
            return GDScriptRefactored.No($"the new parameter '{unpassed.Name}' needs what each call passes, or a default");
        }
        Dictionary<string, List<TextEdit>> edits = [];
        void Edit(string script, TextEdit edit) => (edits.TryGetValue(script, out List<TextEdit>? list) ? list : edits[script] = []).Add(edit);
        // The declaration.
        string text = own.Text;
        string written = string.Join(", ", parameters.Select(p =>
        {
            if (p.From >= 0 && was[p.From] is var old && old.Name == p.Name && old.Type == p.Type && old.Default == p.Default)
            {
                return Written(text, target.Offset, function.Parameters[p.From]);
            }
            string typed = p.Type.Length > 0 ? $": {p.Type}" : string.Empty;
            return $"{p.Name}{typed}{(p.Default.Length > 0 ? $" = {p.Default}" : string.Empty)}";
        }));
        Edit(target.Script, new TextEdit(target.Offset + function.ParametersStart + 1, function.ParametersEnd - function.ParametersStart - 2, written));
        if (renamed)
        {
            Edit(target.Script, new TextEdit(target.Start, target.Named.Length, name));
        }
        foreach ((int index, List<GDScriptIdentifier> read) in reads)
        {
            if (parameters.First(p => p.From == index).Name is var now && now != was[index].Name)
            {
                foreach (GDScriptIdentifier at in read)
                {
                    Edit(target.Script, new TextEdit(target.Offset + at.Start, at.End - at.Start, now));
                }
            }
        }
        // Each call: its arguments put in the new order, a default written where one is left out before one passed.
        foreach (GDScriptUses.Use use in uses.Names)
        {
            if (renamed)
            {
                Edit(use.Script.Script, new TextEdit(use.Offset + use.Name.Start, use.Name.End - use.Name.Start, name));
            }
            if (reshaped && use.Call is { } call && Reargued(use.Script.Text, use.Offset, call, 0, was, parameters) is { } argued)
            {
                Edit(use.Script.Script, argued);
            }
        }
        foreach (GDScriptUses.Named named in uses.Strings)
        {
            string? literal = renamed ? Requoted(named.Script.Text[(named.Offset + named.Literal.Start)..(named.Offset + named.Literal.End)], name) : null;
            if (reshaped && named.Call.FunctionName is "call" or "call_deferred" or "rpc" or "rpc_id"
                && Reargued(named.Script.Text, named.Offset, named.Call, named.Call.FunctionName == "rpc_id" ? 2 : 1, was, parameters, (named.Literal, literal)) is { } argued)
            {
                Edit(named.Script.Script, argued);
            }
            else if (literal is not null)
            {
                Edit(named.Script.Script, new TextEdit(named.Offset + named.Literal.Start, named.Literal.End - named.Literal.Start, literal));
            }
        }
        foreach ((string script, List<TextEdit> list) in edits)
        {
            List<TextEdit> sorted = [.. list.OrderBy(e => e.Start)];
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Start < sorted[i - 1].Start + sorted[i - 1].Length)
                {
                    return GDScriptRefactored.No("its calls nest", [new GDScriptPlace(script, sorted[i].Start, sorted[i].Start + sorted[i].Length, "calls it inside a call of it")]);
                }
            }
        }
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, [.. e.Value.OrderBy(x => x.Start)]))], null, []);
    }

    /// <summary>A string literal as written with another text inside its quotes.</summary>
    private static string Requoted(string written, string name)
    {
        int open = written.IndexOfAny(['"', '\'']);
        int close = open < 0 ? -1 : written.LastIndexOf(written[open]);
        return close > open ? written[..(open + 1)] + name + written[close..] : written;
    }

    /// <summary>A call's arguments after its first <paramref name="kept"/> (a string call's name), put as a signature's
    /// change says: each kept parameter's argument where it was passed, its old default where it was left out before one
    /// passed, a new one's value; null where nothing changes.</summary>
    private static TextEdit? Reargued(string text, int offset, GDScriptCall call, int kept, IReadOnlyList<GDScriptParameterChange> was, IReadOnlyList<GDScriptParameterChange> parameters,
        (GDScriptLiteral Literal, string? Text) renaming = default)
    {
        int open = text.IndexOf('(', offset + (call.Callee?.End ?? call.Start));
        int close = offset + call.End - 1;
        if (open < 0 || close <= open || text[close] != ')')
        {
            return null;
        }
        List<string> given = [.. call.Arguments.Skip(kept).Select(a => text[(offset + a.Start)..(offset + a.End)])];
        IEnumerable<string> before = call.Arguments.Take(kept).Select(a => a == renaming.Literal && renaming.Text is { } renamed ? renamed : text[(offset + a.Start)..(offset + a.End)]);
        string arguments = string.Join(", ", before.Concat(Rearranged(given, was, parameters)));
        return new TextEdit(open + 1, close - open - 1, arguments);
    }

    /// <summary>The arguments a call passes after a signature's change, from those it passed: each kept parameter's where
    /// it was passed, its old default where it was left out before one passed, a new one's value; none left out at the end
    /// that a default now covers. A C# call's too.</summary>
    public static IReadOnlyList<string> Rearranged(IReadOnlyList<string> given, IReadOnlyList<GDScriptParameterChange> was, IReadOnlyList<GDScriptParameterChange> parameters)
    {
        List<(string Text, bool Left)> made = [];
        foreach (GDScriptParameterChange parameter in parameters)
        {
            made.Add(parameter.From >= 0
                ? parameter.From < given.Count ? (given[parameter.From], false) : (was[parameter.From].Default, parameter.Default.Length > 0)
                : parameter.CallValue.Length > 0 ? (parameter.CallValue, false) : (parameter.Default, true));
        }
        while (made.Count > 0 && made[^1].Left)
        {
            made.RemoveAt(made.Count - 1);
        }
        return [.. made.Select(m => m.Text)];
    }
}
