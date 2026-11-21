namespace Passenger.Roslyn.Extension;

/// <summary>What a C# completion item names where the protocol's kind says too little: a local, a parameter and a named
/// argument all come as a variable, an extension method as any method. Said beside a list's items as runs in their order,
/// as <see cref="CompletionPriorities"/> says priorities, a list naming none of these saying nothing. Compiled into both
/// the extension, which writes it, and Passenger, which reads it.</summary>
public enum CompletionKind
{
    /// <summary>What the protocol's kind says.</summary>
    AsListed = 0,
    Local = 1,
    Parameter = 2,

    /// <summary>A parameter named at a call to give it its argument (<c>value:</c>).</summary>
    NamedArgument = 3,
    ExtensionMethod = 4,
}

/// <summary>The members of a C# completion list beside its items: each item's <see cref="CompletionKind"/>, and the type a
/// value it names has, or a method returns where every overload returns the same, by the item's place in the list.</summary>
public static class CompletionKinds
{
    /// <summary>The list's member holding the kinds' runs.</summary>
    public const string Member = "itemKinds";

    /// <summary>The list's member holding the types, from an item's place in the list to its type as code shows it.</summary>
    public const string TypesMember = "itemTypes";

    /// <summary>The runs of <paramref name="count"/> items' kinds, or null where every item's is as listed.</summary>
    public static int[]? Runs(int count, Func<int, CompletionKind> kindOf) => CompletionPriorities.Runs(count, i => (int)kindOf(i));

    /// <summary>Each of <paramref name="count"/> items' kind from its list's runs; null where there are none, or where they
    /// do not cover exactly that many items or name a kind not known.</summary>
    public static CompletionKind[]? Each(int[]? runs, int count) =>
        CompletionPriorities.Each(runs, count) is { } each && each.All(k => Enum.IsDefined((CompletionKind)k))
            ? [.. each.Select(k => (CompletionKind)k)]
            : null;
}
