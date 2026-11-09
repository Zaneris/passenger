using System.Diagnostics.CodeAnalysis;

namespace Passenger.Vcs;

/// <summary>A commit's hash as Passenger shows it short.</summary>
public static class GitHashes
{
    /// <summary>The hash's first 8 characters (all of one with fewer), as a commit is named in the log, a message or a
    /// title; null for none.</summary>
    [return: NotNullIfNotNull(nameof(hash))]
    public static string? Short(string? hash) => hash?[..Math.Min(8, hash.Length)];
}
