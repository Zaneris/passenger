namespace Passenger.App.CSharp;

/// <summary>The words Roslyn's language server gives the code actions and diagnostics Passenger's own actions run (Extract
/// Method's, Introduce Variable's, Optimize Imports' and the rest), by which they are found among all it offers: each
/// spelled once here, so a server that words one otherwise is mended in one place.</summary>
internal static class RoslynNames
{
    /// <summary>Extract Method's action, the whole title.</summary>
    public const string ExtractMethod = "Extract method";

    /// <summary>How the server's own Extract Interface's title begins, its dialog's three dots after it.</summary>
    public const string ExtractInterface = "Extract interface";

    /// <summary>How Introduce Variable's actions begin: "Introduce local for 'x'" and "for all occurrences of 'x'".</summary>
    public const string IntroduceLocal = "Introduce local";

    /// <summary>How Inline's actions begin: Inline temporary variable, Inline 'Half(int value)'.</summary>
    public const string Inline = "Inline";

    /// <summary>The kind the server gives most of Inline's actions.</summary>
    public const string InlineKind = "refactor.inline";

    /// <summary>How Introduce Field's action for a field initializer's value begins.</summary>
    public const string IntroduceField = "Introduce field";

    /// <summary>How Introduce Field's action for a constant begins.</summary>
    public const string IntroduceConstant = "Introduce constant";

    /// <summary>The word in Introduce constant's nested choices that are a local constant, not a field.</summary>
    public const string Local = "local";

    /// <summary>How Introduce Parameter's actions begin.</summary>
    public const string IntroduceParameter = "Introduce parameter";

    /// <summary>How Encapsulate Field's actions begin.</summary>
    public const string EncapsulateField = "Encapsulate field";

    /// <summary>The diagnostic of a using nothing needs.</summary>
    public const string UnnecessaryUsing = "IDE0005";

    /// <summary>The fix that removes the usings nothing needs, the whole title.</summary>
    public const string RemoveUnnecessaryUsings = "Remove unnecessary usings";

    /// <summary>Sort Usings' action, the whole title.</summary>
    public const string SortUsings = "Sort Usings";

    /// <summary>How the actions that write code for a type begin, which Generate lists: constructors, equality, members,
    /// overrides, parameters and null checks.</summary>
    public static readonly IReadOnlyList<string> Generate = ["Generate", "Implement", "Override", "Add parameter", "Add null check", "Create and assign"];
}
