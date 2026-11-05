using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>A function a scene's connection calls when a signal is emitted: its script and name.</summary>
public readonly record struct GDScriptHandler(string Script, string Name, GDScriptPlace At);

public static partial class GDScriptRefactorings
{
    /// <summary>A signal's signature as Change Signature shows it: each parameter's name and type as written.</summary>
    public static IReadOnlyList<GDScriptParameterChange> SignalParametersOf(GDScriptRead own, GDScriptTarget target) =>
        target.Node is GDScriptSignal signal
            ? [.. signal.Parameters.Select((p, i) => new GDScriptParameterChange(i, p.Identifier?.Name ?? string.Empty, Written(own.Text, target.Offset, p.Type), string.Empty, string.Empty))]
            : [];

    /// <summary>Change Signature of a signal: each <c>emit</c> and <c>emit_signal</c> given its arguments in the new order,
    /// each function connected by a script's <c>connect</c> or a scene (<paramref name="handlers"/>) its parameters, its own
    /// calls following. Refused where an emit passes fewer than it declares, a handler is a lambda, bound, unread, of another
    /// count, reads a parameter taken out or handles another signal, or <paramref name="elsewhere"/> (C#) connects it.</summary>
    public static GDScriptRefactored ChangeSignalSignature(IReadOnlyList<GDScriptRead> scripts, GDScriptTarget target, string name, IReadOnlyList<GDScriptParameterChange> parameters,
        IReadOnlyList<GDScriptHandler> handlers, IReadOnlyList<GDScriptPlace> elsewhere)
    {
        if (target.Node is not GDScriptSignal signal)
        {
            return GDScriptRefactored.No("put the caret on a signal");
        }
        if ((WhyNotName(name) ?? parameters.Select(p => WhyNotName(p.Name)).FirstOrDefault(w => w is not null)) is { } bad)
        {
            return GDScriptRefactored.No(bad);
        }
        if (parameters.GroupBy(p => p.Name, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            return GDScriptRefactored.No($"two parameters are named '{twice.Key}'");
        }
        if (parameters.FirstOrDefault(p => p.From < 0 && p.CallValue.Length == 0) is { } unpassed)
        {
            return GDScriptRefactored.No($"the new parameter '{unpassed.Name}' needs what each emit passes");
        }
        GDScriptRead own = scripts.First(s => s.Script == target.Script);
        IReadOnlyList<GDScriptParameterChange> was = SignalParametersOf(own, target);
        bool reshaped = parameters.Count != was.Count || parameters.Select((p, i) => p.From != i).Any(x => x);
        bool renamed = name != target.Named;
        if (renamed && own.Tree.Members.Any(m => NameOf(m.Node) == name))
        {
            return GDScriptRefactored.No($"the script already has a member '{name}'");
        }
        GDScriptUses uses = UsesOf(scripts, target);
        List<GDScriptPlace> blocking = [.. uses.Unsure];
        Dictionary<string, List<TextEdit>> edits = [];
        void Edit(string script, TextEdit edit) => (edits.TryGetValue(script, out List<TextEdit>? list) ? list : edits[script] = []).Add(edit);
        // Each handler connected, by the script it is in and the names that connect it there.
        List<(GDScriptTarget Handler, GDScriptNode? Named)> connected = [];
        foreach (GDScriptUses.Use use in uses.Names)
        {
            if (renamed)
            {
                Edit(use.Script.Script, new TextEdit(use.Offset + use.Name.Start, use.Name.End - use.Name.Start, name));
            }
            Dictionary<GDScriptNode, GDScriptNode?> parents = Parents(use.Within);
            // The signal as written: its name, or self.name.
            GDScriptNode named = parents.GetValueOrDefault(use.Name) is GDScriptSubscript { IsAttribute: true } owner && owner.Attribute == use.Name ? owner : use.Name;
            if (parents.GetValueOrDefault(named) is not GDScriptSubscript { IsAttribute: true, Attribute.Name: var method } member || member.Base != named
                || parents.GetValueOrDefault(member) is not GDScriptCall call || call.Callee != member)
            {
                if (reshaped)
                {
                    blocking.Add(GDScriptUses.Place(use.Script, use.Offset, use.Name, "takes it as a value"));
                }
                continue;
            }
            switch (method)
            {
                case "emit" when reshaped:
                    if (Disarranged(use.Script, use.Offset, call, 0, parameters) is { } why)
                    {
                        blocking.Add(GDScriptUses.Place(use.Script, use.Offset, call, why));
                    }
                    else if (Emitted(use.Script, use.Offset, call, 0, was, parameters) is { } emitted)
                    {
                        Edit(use.Script.Script, emitted);
                    }
                    else
                    {
                        blocking.Add(GDScriptUses.Place(use.Script, use.Offset, call, "emits it with fewer arguments than it declares"));
                    }
                    break;
                case "connect" when reshaped:
                    if (call.Arguments is [var callable, ..] && HandlerOf(scripts, use.Script, use.Offset, callable) is { } handler)
                    {
                        connected.Add(handler);
                    }
                    else
                    {
                        blocking.Add(GDScriptUses.Place(use.Script, use.Offset, call, "connects a handler it can't follow"));
                    }
                    break;
            }
        }
        foreach (GDScriptUses.Named named in uses.Strings)
        {
            string? literal = renamed ? Requoted(named.Script.Text[(named.Offset + named.Literal.Start)..(named.Offset + named.Literal.End)], name) : null;
            if (reshaped && named.Call is { FunctionName: "emit_signal" } emitting)
            {
                if (Disarranged(named.Script, named.Offset, emitting, 1, parameters) is { } why)
                {
                    blocking.Add(GDScriptUses.Place(named.Script, named.Offset, emitting, why));
                }
                else if (Emitted(named.Script, named.Offset, emitting, 1, was, parameters, (named.Literal, literal)) is { } emitted)
                {
                    Edit(named.Script.Script, emitted);
                }
                else
                {
                    blocking.Add(GDScriptUses.Place(named.Script, named.Offset, emitting, "emits it with fewer arguments than it declares"));
                }
                continue;
            }
            if (literal is not null)
            {
                Edit(named.Script.Script, new TextEdit(named.Offset + named.Literal.Start, named.Literal.End - named.Literal.Start, literal));
            }
            if (!reshaped)
            {
                continue;
            }
            switch (named.Call?.FunctionName)
            {
                case "connect":
                    if (named.Call.Arguments is [_, var callable, ..] && HandlerOf(scripts, named.Script, named.Offset, callable) is { } handler)
                    {
                        connected.Add(handler);
                    }
                    else
                    {
                        blocking.Add(GDScriptUses.Place(named.Script, named.Offset, named.Site, "connects a handler it can't follow"));
                    }
                    break;
                case "disconnect" or "has_signal" or "is_connected" or "get_signal_connection_list":
                    break;
                default:
                    blocking.Add(GDScriptUses.Place(named.Script, named.Offset, named.Site, "takes it as a value"));
                    break;
            }
        }
        if (reshaped)
        {
            blocking.AddRange(elsewhere);
            foreach (GDScriptHandler scened in handlers)
            {
                if (scripts.FirstOrDefault(s => s.Script == scened.Script) is { } script
                    && script.Tree.Members.FirstOrDefault(m => m.Node is GDScriptFunction { Identifier.Name: var n } && n == scened.Name) is { Node: GDScriptFunction { Identifier: { } id } } member)
                {
                    connected.Add((new GDScriptTarget(script.Script, member.Node, member.Offset, id), null));
                }
                else
                {
                    blocking.Add(scened.At with { What = $"connects it to {scened.Name}, which no script it reads declares" });
                }
            }
        }
        if (blocking.Count > 0)
        {
            return GDScriptRefactored.No($"Change Signature can't follow every use ({Places(blocking)})", blocking);
        }
        // The declaration.
        string text = own.Text;
        string declared = string.Join(", ", parameters.Select(p => p.From >= 0 && was[p.From] is var old && old.Name == p.Name && old.Type == p.Type
            ? Written(text, target.Offset, signal.Parameters[p.From]) : $"{p.Name}{(p.Type.Length > 0 ? $": {p.Type}" : string.Empty)}"));
        int nameEnd = target.Start + target.Named.Length;
        if (signal.HasParentheses && text.IndexOf('(', nameEnd) is int open and >= 0 && open < target.Offset + signal.ParametersEnd)
        {
            Edit(target.Script, new TextEdit(open + 1, target.Offset + signal.ParametersEnd - 1 - (open + 1), declared));
        }
        else if (parameters.Count > 0)
        {
            Edit(target.Script, new TextEdit(nameEnd, 0, $"({declared})"));
        }
        if (renamed)
        {
            Edit(target.Script, new TextEdit(target.Start, target.Named.Length, name));
        }
        // Each handler takes its parameters in the signal's new order, a new one by the signal's name for it.
        foreach (IGrouping<GDScriptTarget, (GDScriptTarget Handler, GDScriptNode? Named)> handler in connected.GroupBy(c => c.Handler, TargetComparer.Instance))
        {
            GDScriptTarget function = handler.Key;
            GDScriptRead declaring = scripts.First(s => s.Script == function.Script);
            IReadOnlyList<GDScriptParameterChange> takes = ParametersOf(declaring, function);
            if (takes.Count != was.Count)
            {
                return GDScriptRefactored.No($"{function.Named} takes {takes.Count} parameters, but the signal gives {was.Count}",
                    [new GDScriptPlace(function.Script, function.Start, function.Start + function.Named.Length, "handles it")]);
            }
            List<GDScriptParameterChange> following = [.. parameters.Select(p => p.From >= 0 ? takes[p.From] with { CallValue = string.Empty }
                : new GDScriptParameterChange(-1, p.Name, p.Type, string.Empty, p.CallValue))];
            HashSet<GDScriptNode> followed = [.. handler.Select(h => h.Named).OfType<GDScriptNode>()];
            GDScriptRefactored made = ChangeSignature(scripts, function, function.Named, following, [], followed);
            if (made.Refused is { } refused)
            {
                return GDScriptRefactored.No($"its handler {function.Named} can't follow: {refused}", made.Places);
            }
            foreach (GDScriptEdits script in made.Scripts)
            {
                foreach (TextEdit edit in script.Edits)
                {
                    Edit(script.Script, edit);
                }
            }
        }
        foreach ((string script, List<TextEdit> list) in edits)
        {
            List<TextEdit> sorted = [.. list.Distinct().OrderBy(e => e.Start)];
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Start < sorted[i - 1].Start + sorted[i - 1].Length)
                {
                    return GDScriptRefactored.No("its edits overlap", [new GDScriptPlace(script, sorted[i].Start, sorted[i].Start + sorted[i].Length, "emits it inside an emit of it")]);
                }
            }
            list.Clear();
            list.AddRange(sorted);
        }
        return new GDScriptRefactored([.. edits.Select(e => new GDScriptEdits(e.Key, e.Value))], null, []);
    }

    /// <summary>An emit's arguments after the first <paramref name="kept"/> (emit_signal's name) in the new order; null
    /// where it passes fewer than the signal declares.</summary>
    private static TextEdit? Emitted(GDScriptRead script, int offset, GDScriptCall call, int kept, IReadOnlyList<GDScriptParameterChange> was, IReadOnlyList<GDScriptParameterChange> parameters,
        (GDScriptLiteral Literal, string? Text) renaming = default) =>
        call.Arguments.Count - kept < was.Count ? null : Reargued(script.Text, offset, call, kept, was, parameters, renaming);

    /// <summary>The function a callable connected names, and the name that names it as a value: a function's name, a member
    /// one (<c>self.f</c>), or <c>Callable(self, "f")</c>; null for a lambda, a bound one or one not read.</summary>
    private static (GDScriptTarget Handler, GDScriptNode? Named)? HandlerOf(IReadOnlyList<GDScriptRead> scripts, GDScriptRead script, int offset, GDScriptExpression callable)
    {
        GDScriptIdentifier? name = callable switch
        {
            GDScriptIdentifier identifier => identifier,
            GDScriptSubscript { IsAttribute: true, Attribute: { } attribute } => attribute,
            _ => null,
        };
        if (name is not null && script.Analysis.DeclarationOf(name) is { Script: { } declaring } declared
            && scripts.FirstOrDefault(s => s.Script == declaring) is { } read
            && read.Tree.Members.FirstOrDefault(m => m.Node is GDScriptFunction { Identifier: { } n } && m.Offset + n.Start == declared.Start) is { Node: GDScriptFunction { Identifier: { } id } } member)
        {
            return (new GDScriptTarget(declaring, member.Node, member.Offset, id), name);
        }
        if (callable is GDScriptCall { FunctionName: "Callable", Arguments: [GDScriptSelf, GDScriptLiteral { Value: string method } literal] }
            && script.Tree.Members.FirstOrDefault(m => m.Node is GDScriptFunction { Identifier.Name: var n } && n == method) is { Node: GDScriptFunction { Identifier: { } own } } found)
        {
            return (new GDScriptTarget(script.Script, found.Node, found.Offset, own), literal);
        }
        return null;
    }

    /// <summary>Members alike by their script and where their names are.</summary>
    private sealed class TargetComparer : IEqualityComparer<GDScriptTarget>
    {
        public static readonly TargetComparer Instance = new();

        public bool Equals(GDScriptTarget? x, GDScriptTarget? y) => x?.Script == y?.Script && x?.Start == y?.Start;

        public int GetHashCode(GDScriptTarget target) => HashCode.Combine(target.Script, target.Start);
    }
}
