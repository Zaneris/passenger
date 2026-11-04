namespace Passenger.Workspace.GDScript;

/// <summary>A parameter as a signature shows it: its name, its type and its default as written (or as the engine's API
/// gives them), each null where there is none.</summary>
public sealed record GDScriptParameterShown(string Name, string? Type, string? Default)
{
    public override string ToString() => Name + (Type is null ? string.Empty : $": {Type}") + (Default is null ? string.Empty : $" = {Default}");
}

/// <summary>A function's or signal's signature as cards, Parameter Info and an override show it: what it is (<c>func</c>,
/// <c>signal</c>, empty for a constructor), the class that declares it where it is the engine's, its name, its parameters, what it returns (null
/// where nothing is written) and whether it takes any number more (<c>...</c>). Pure.</summary>
public sealed record GDScriptSignature(string Kind, string? Owner, string Name, IReadOnlyList<GDScriptParameterShown> Parameters, string? Returns, bool Varargs)
{
    /// <summary>Its parameters as a call's list shows them: each, then <c>...</c> where it takes any number more.</summary>
    public IReadOnlyList<string> Shown => [.. Parameters.Select(p => p.ToString()), .. Varargs ? ["..."] : Array.Empty<string>()];

    /// <summary>The whole signature: <c>func Node.add_child(node: Node, force_readable_name: bool = false) -> void</c>.</summary>
    public override string ToString() =>
        $"{(Kind.Length > 0 ? Kind + " " : string.Empty)}{(Owner is null ? string.Empty : Owner + ".")}{Name}({string.Join(", ", Shown)}){(Returns is null ? string.Empty : $" -> {Returns}")}";

    /// <summary>An engine method's or utility function's signature, its types as GDScript writes them.</summary>
    public static GDScriptSignature Of(GodotApiMethod method, string? owner, GDScriptAnalyzer analyzer, string kind = "func") =>
        new(kind, owner, method.Name, [.. method.Parameters.Select((p, i) => new GDScriptParameterShown(p.Name, Typed(p.Type, analyzer),
            i < method.Defaults.Count ? method.Defaults[i] : null))], kind == "signal" ? null : Typed(method.Returns, analyzer) ?? "Variant", method.Varargs);

    private static string? Typed(string written, GDScriptAnalyzer analyzer) =>
        written is "" ? null : written == "void" ? "void" : analyzer.FromApi(written).ToString();

    /// <summary>A script's function as written: each parameter's type and default as the text gives them, its rest
    /// parameter last, its return type where one is written.</summary>
    public static GDScriptSignature Of(GDScriptFunction function, string text, int offset)
    {
        List<GDScriptParameterShown> parameters = [.. function.Parameters.Select(p => ShownOf(p, text, offset))];
        if (function.Rest is { Identifier: { } rest } restParameter)
        {
            parameters.Add(ShownOf(restParameter, text, offset) with { Name = "..." + rest.Name });
        }
        return new GDScriptSignature("func", null, function.Identifier?.Name ?? string.Empty, parameters, Written(text, offset, function.ReturnType), false);
    }

    /// <summary>A script's signal as written.</summary>
    public static GDScriptSignature Of(GDScriptSignal signal, string text, int offset) =>
        new("signal", null, signal.Identifier?.Name ?? string.Empty, [.. signal.Parameters.Select(p => ShownOf(p, text, offset))], null, false);

    private static GDScriptParameterShown ShownOf(GDScriptParameter parameter, string text, int offset) =>
        new(parameter.Identifier?.Name ?? string.Empty, Written(text, offset, parameter.Type), Written(text, offset, parameter.Initializer));

    private static string? Written(string text, int offset, GDScriptNode? node) =>
        node is { Start: >= 0 } && offset + node.End <= text.Length ? text[(offset + node.Start)..(offset + node.End)] : null;

    /// <summary>The function or signal a script declares by its name's offset, its own class's or an inner class's, with the
    /// offset its positions count from; null for none.</summary>
    public static (GDScriptNode Node, int Offset)? DeclaredAt(GDScriptTree tree, int nameStart)
    {
        foreach (GDScriptMember member in tree.Members)
        {
            if (Find(member.Node, member.Offset) is { } found)
            {
                return found;
            }
        }
        return null;

        (GDScriptNode, int)? Find(GDScriptNode node, int offset) => node switch
        {
            GDScriptFunction { Identifier: { } name } when offset + name.Start == nameStart => (node, offset),
            GDScriptSignal { Identifier: { } name } when offset + name.Start == nameStart => (node, offset),
            GDScriptVariable { Identifier: { } name } when offset + name.Start == nameStart => (node, offset),
            GDScriptConstant { Identifier: { } name } when offset + name.Start == nameStart => (node, offset),
            GDScriptEnum { Identifier: { } name } when offset + name.Start == nameStart => (node, offset),
            GDScriptClass { Identifier: { } name } when offset + name.Start == nameStart => (node, offset),
            GDScriptClass inner => inner.Members.Select(m => Find(m, offset)).FirstOrDefault(f => f is not null),
            _ => null,
        };
    }
}
