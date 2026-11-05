using System.Text.RegularExpressions;

namespace Passenger.Core;

/// <summary>A release's SHA256SUMS, as sha256sum writes it: a line a file, its SHA-256 in hex and its name, in text or
/// binary mode.</summary>
public static partial class ReleaseSums
{
    /// <summary>The file's name in every release.</summary>
    public const string Name = "SHA256SUMS";

    /// <summary>The SHA-256 the sums give a file, in lowercase hex, or null where they give it none.</summary>
    public static string? Of(string sums, string file) =>
        sums.Split('\n').Select(line => SumLine().Match(line.TrimEnd('\r'))).FirstOrDefault(m => m.Success && m.Groups[2].Value == file)
            ?.Groups[1].Value.ToLowerInvariant();

    [GeneratedRegex(@"^([0-9A-Fa-f]{64}) [ *](.+)$")]
    private static partial Regex SumLine();
}
