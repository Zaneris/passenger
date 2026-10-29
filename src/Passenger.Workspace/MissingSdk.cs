using System.Text.RegularExpressions;

namespace Passenger.Workspace;

/// <summary>Why a folder's projects have no .NET SDK to load, build and run them with, as dotnet's words say it (the
/// project host's, which loaded none for the folder, <see cref="ProjectHostFailed.MissingSdk"/>): the version a
/// global.json asks for and that file, when the one it asks for is not installed; whether none is installed at all; and
/// those words whole.</summary>
public sealed partial record MissingSdk(string? Asked, string? GlobalJson, bool NoneInstalled, string Said)
{
    private const string Detailed = "Detailed error:";

    /// <summary>What dotnet's words say of the SDK it found none of: its own words past the native host's call and code.</summary>
    public static MissingSdk From(string words)
    {
        int detailed = words.IndexOf(Detailed, StringComparison.Ordinal);
        string said = (detailed < 0 ? words : words[(detailed + Detailed.Length)..]).Trim();
        return new MissingSdk(
            Requested().Match(said) is { Success: true } version ? version.Groups[1].Value : null,
            GlobalJsonFile().Match(said) is { Success: true } file ? file.Groups[1].Value.Trim() : null,
            said.Contains("No .NET SDKs were found", StringComparison.Ordinal),
            said);
    }

    /// <summary>No dotnet at all, in the words of the search for it.</summary>
    public static MissingSdk NoDotnet(string said) => new(null, null, NoneInstalled: true, said);

    /// <summary>Where the SDK is downloaded: the page of the channel of the version asked for, else the page of them all.</summary>
    public Uri Download => Asked is { } asked && Channel().Match(asked) is { Success: true } channel
        ? new Uri($"https://dotnet.microsoft.com/download/dotnet/{channel.Groups[1].Value}")
        : new Uri("https://dotnet.microsoft.com/download");

    [GeneratedRegex(@"^Requested SDK version: *(\S+)", RegexOptions.Multiline)]
    private static partial Regex Requested();

    [GeneratedRegex(@"^global\.json file: *(\S.*)$", RegexOptions.Multiline)]
    private static partial Regex GlobalJsonFile();

    [GeneratedRegex(@"^(\d+\.\d+)\.")]
    private static partial Regex Channel();
}
