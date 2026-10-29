using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App.Web;

/// <summary>The Node.js the web language servers run on: the one Settings, Node.js names, else the first on the PATH as
/// the login shell finds it (<see cref="Programs.Node"/>). Its version is asked of it, once. <see cref="Problem"/> says
/// plainly why there is none to use.</summary>
public sealed partial record NodeRuntime(string? Path, Version? Version, string? Problem)
{
    /// <summary>Where Settings names the interpreter, for what the status bar and banner say.</summary>
    public static readonly string SettingsPlace = SettingsDialog.PlaceOfPage(SettingsDialog.NodePage);

    /// <summary>The interpreter named, else the PATH's, with its version; one that cannot be run says why. Run off the UI
    /// thread: it starts <c>node --version</c>.</summary>
    public static async Task<NodeRuntime> FindAsync(string? configured)
    {
        string? path = configured is { Length: > 0 } named ? named : Programs.Node();
        if (path is null)
        {
            return new NodeRuntime(null, null, $"Node.js was not found on the PATH: install it, or name it in {SettingsPlace}");
        }
        if (!File.Exists(path))
        {
            return new NodeRuntime(path, null, $"there is no Node.js at {path}, which {SettingsPlace} names");
        }
        try
        {
            ChildResult asked = await ChildProcesses.RunAsync(ChildProcesses.Command(path, ["--version"]), limit: TimeSpan.FromSeconds(15));
            if (asked.Succeeded && Numbered().Match(asked.Output) is { Success: true } number && Version.TryParse(number.Groups[1].Value, out Version? version))
            {
                return new NodeRuntime(path, version, null);
            }
            return new NodeRuntime(path, null, $"{path} did not say its version as Node.js does (exit code {asked.ExitCode})");
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or TimeoutException)
        {
            return new NodeRuntime(path, null, $"{path} could not be run: {error.Message}");
        }
    }

    /// <summary>Why this Node.js cannot run what a package's package.json says needs another (its <c>engines.node</c>, as
    /// "&gt;=22.22.2", or eslint's "^20.19.0 || ^22.13.0 || &gt;=24"), or null when it can; <paramref name="what"/> names
    /// what needs it ("the TypeScript language server").</summary>
    public string? TooOldFor(string package, string what)
    {
        if (Version is not { } version || !File.Exists(package))
        {
            return null;
        }
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(package));
            if (read.RootElement.TryGetProperty("engines", out JsonElement engines) && engines.ValueKind == JsonValueKind.Object
                && engines.TryGetProperty("node", out JsonElement range) && range.ValueKind == JsonValueKind.String
                && range.GetString() is { } needs && !Meets(version, needs))
            {
                string wanted = Least().Match(needs) is { Success: true } least && least.Value.Trim() == needs.Trim() && Version.TryParse(Padded(least.Groups[1].Value), out Version? minimum)
                    ? $"{minimum} or later" : needs.Trim();
                return $"{what} needs Node.js {wanted}, and {Path} is {version}: update it, or name a later one in {SettingsPlace}";
            }
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
            // A package.json that does not read says nothing of what it needs.
        }
        return null;
    }

    /// <summary>Whether a version is in a semver range of npm's: any of its alternatives (between "||"), each every one of
    /// its parts (&gt;=, &gt;, &lt;=, &lt;, ^ a major's from there, ~ a minor's from there, a version alone, x or * any).
    /// A part this cannot read is taken as met, so only a range that says so turns a Node.js away.</summary>
    private static bool Meets(Version version, string range) =>
        range.Split("||").Any(alternative => alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(part => MeetsPart(version, part)));

    private static bool MeetsPart(Version version, string part)
    {
        Match read = Comparator().Match(part);
        if (!read.Success || read.Groups[2].Value is "x" or "X" or "*" || !Version.TryParse(Padded(read.Groups[2].Value), out Version? bound))
        {
            return true;
        }
        return read.Groups[1].Value switch
        {
            ">=" => version >= bound,
            ">" => version > bound,
            "<=" => version <= bound,
            "<" => version < bound,
            "^" => version >= bound && version.Major == bound.Major,
            "~" => version >= bound && version.Major == bound.Major && version.Minor == bound.Minor,
            _ => read.Groups[2].Value.Count(c => c == '.') switch
            {
                0 => version.Major == bound.Major,
                1 => version.Major == bound.Major && version.Minor == bound.Minor,
                _ => version == bound,
            },
        };
    }

    private static string Padded(string version) => version.Count(c => c == '.') switch
    {
        0 => version + ".0.0",
        1 => version + ".0",
        _ => version,
    };

    [GeneratedRegex(@"v?(\d+\.\d+\.\d+)")]
    private static partial Regex Numbered();

    [GeneratedRegex(@">=\s*v?(\d+(?:\.\d+){0,2})")]
    private static partial Regex Least();

    [GeneratedRegex(@"^(>=|<=|>|<|\^|~|=)?\s*v?(\d+(?:\.\d+){0,2}|[xX*])(?:\.[xX*])*$")]
    private static partial Regex Comparator();
}
