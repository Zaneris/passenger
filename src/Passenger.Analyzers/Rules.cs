using Microsoft.CodeAnalysis;

namespace Passenger.Analyzers;

/// <summary>Every rule's id and words, in one place: the rules of Passenger's code from PSG0001 on, each a warning the build
/// makes an error, and from PSG9001 on what a generator finds wrong in the files it reads. A rule is added here, in
/// AnalyzerReleases.Unshipped.md, and as an analyzer of its own beside the others.</summary>
internal static class Rules
{
    private const string Category = "Passenger";

    public static readonly DiagnosticDescriptor IdentifierLiteral = new(
        "PSG0001",
        "An identifier is written outside its home",
        "\"{0}\" is {1}: name it from there",
        Category,
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "An identifier ([IdentifierHome]: an action's id, a tool window's, a theme key, an environment variable, "
            + "an MCP tool's name) is written once, in its home, so a misspelt one is a build error rather than something "
            + "that silently finds nothing.");

    public static readonly DiagnosticDescriptor KeysUnread = new(
        "PSG9001",
        "A resource keys file is not read",
        "{0}'s keys are not all in {1}: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
