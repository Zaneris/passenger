using Microsoft.CodeAnalysis.CSharp;

namespace Passenger.App.Templates;

/// <summary>Whether a name can be a new solution's or project's: one a file takes and, for a project whose template writes
/// C#, one whose code builds, since dotnet new names the code's namespace after it, each dotted part made an identifier.</summary>
public static class ProjectNames
{
    /// <summary>Why a solution or project name cannot be one: none, characters no file name takes, or, where it names C#
    /// code's namespace (<paramref name="namesCode"/>), a part that is a C# keyword; null when it can.</summary>
    public static string? Problem(string name, string what, bool namesCode) =>
        name.Length == 0 ? $"{what} name cannot be empty"
        : name is "." or ".." || name.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 ? $"{what} name contains invalid characters"
        : namesCode && Keyword(name) is { } keyword ? $"{what} name cannot use the C# keyword '{keyword}': its code's namespace is named after it"
        : null;

    /// <summary>The C# keyword the namespace dotnet new makes of a name holds, or null: each dotted part made an identifier
    /// as its templates make one (any other character an underscore, one put before a digit that begins it).</summary>
    public static string? Keyword(string name) =>
        name.Split('.').Select(Identifier).FirstOrDefault(part => SyntaxFacts.GetKeywordKind(part) != SyntaxKind.None);

    private static string Identifier(string part)
    {
        string word = new([.. part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_')]);
        return word.Length > 0 && char.IsDigit(word[0]) ? "_" + word : word;
    }
}
