using System.Text;

namespace Passenger.Profiler;

/// <summary>A profiled frame's name as the code it runs is written: its type as the runtime names it (outer types before
/// '+', a generic type's arity kept, the arguments the runtime writes into it left out) and its method's name, a
/// constructor's ".ctor" and a static constructor's ".cctor".</summary>
public readonly record struct FrameName(string Type, string Method)
{
    /// <summary>A frame's name split: "Ns.Type..ctor(Int32)" into "Ns.Type" and ".ctor"; null for one with no type.</summary>
    public static FrameName? Of(string frame)
    {
        string bare = WithoutArguments(frame.Split('(')[0]);
        int dot = bare.EndsWith("..ctor", StringComparison.Ordinal) || bare.EndsWith("..cctor", StringComparison.Ordinal)
            ? bare.LastIndexOf("..", StringComparison.Ordinal)
            : bare.LastIndexOf('.');
        return dot <= 0 ? null : new FrameName(bare[..dot], bare[(dot + 1)..]);
    }

    /// <summary>Whether the method is a constructor, an instance's or the type's own.</summary>
    public bool IsConstructor => Method is ".ctor" or ".cctor";

    /// <summary>A frame's name without the generic arguments the runtime writes into it ([System.__Canon]), dots of their own
    /// and all.</summary>
    private static string WithoutArguments(string frame)
    {
        StringBuilder kept = new(frame.Length);
        int depth = 0;
        foreach (char c in frame)
        {
            depth += c == '[' ? 1 : 0;
            if (depth == 0)
            {
                kept.Append(c);
            }
            depth -= c == ']' && depth > 0 ? 1 : 0;
        }
        return kept.ToString();
    }
}
