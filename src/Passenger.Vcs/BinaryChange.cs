using Passenger.Core;

namespace Passenger.Vcs;

/// <summary>A binary file's change since the last commit: its sizes there and now (null where it is not), and whether
/// its bytes are the same.</summary>
public sealed record BinaryChange(long? Before, long? After, bool Same)
{
    /// <summary>The change as git's diff says it, of a file by its name from the tree's top.</summary>
    public string Unified(string name) => Same ? "No changes"
        : $"Binary files {(Before is null ? "/dev/null" : $"a/{name}")} and {(After is null ? "/dev/null" : $"b/{name}")} differ";

    /// <summary>The change in a person's words, of a file by its name.</summary>
    public string Said(string name) => (Before, After) switch
    {
        _ when Same => $"{name} is binary, and as the last commit has it",
        (null, { } after) => $"{name} is a new binary file of {Plural.Of(after, "byte")}",
        ({ } before, null) => $"{name}, a binary file of {Plural.Of(before, "byte")} in the last commit, is deleted",
        _ => $"Binary files differ: {name} is {Plural.Of(After ?? 0, "byte")} now and {Plural.Of(Before ?? 0, "byte")} in the last commit",
    };
}
