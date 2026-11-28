using System.Diagnostics.CodeAnalysis;

namespace Passenger.Vcs;

/// <summary>A commit's hash as Passenger shows it short.</summary>
public static class GitHashes
{
    /// <summary>The hash's first 8 characters (all of one with fewer), as a commit is named in the log, a message or a
    /// title; null for none.</summary>
    [return: NotNullIfNotNull(nameof(hash))]
    public static string? Short(string? hash) => hash?[..Math.Min(8, hash.Length)];

    /// <summary>Whether a word is an object's whole hash: 40 hex digits in a SHA-1 repository, 64 in a SHA-256 one.</summary>
    public static bool IsWhole(string word) => word.Length is 40 or 64 && word.All(char.IsAsciiHexDigit);
}
