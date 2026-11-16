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
            return new NodeRuntime(path, null, $"{path} did not say its version as Node.js does ({ExitCodes.Said(asked.ExitCode)})");
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            return new NodeRuntime(path, null, $"{path} could not be run: {error.Message}");
        }
    }

    /// <summary>Why this Node.js cannot run what a package's package.json says needs another (its <c>engines.node</c>, as
    /// "&gt;=22.22.2", or eslint's "^20.19.0 || ^22.13.0 || &gt;=24"), or what Passenger runs with it needs beside
    /// (<paramref name="alsoNeeds"/>, a range as npm writes one), or null when it can; <paramref name="what"/> names what
    /// needs it ("the TypeScript language server").</summary>
    public string? TooOldFor(string package, string what, string? alsoNeeds = null) =>
        Unmet(Engines(package), what) ?? Unmet(alsoNeeds, what);

    /// <summary>The Node.js a package's package.json says it needs; null where it says none or does not read.</summary>
    private static string? Engines(string package)
    {
        if (!File.Exists(package))
        {
            return null;
        }
        try
        {
            using JsonDocument read = JsonDocument.Parse(File.ReadAllText(package));
            return JsonMembers.Text(JsonMembers.Object(read.RootElement, "engines"), "node");
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException)
        {
            // A package.json that does not read says nothing of what it needs.
            return null;
        }
    }

    /// <summary>Why this Node.js is outside the range <paramref name="needs"/>, or null where it is in it or either is unknown.</summary>
    private string? Unmet(string? needs, string what)
    {
        if (needs is null || Version is not { } version || NpmRange.Meets(version, needs))
        {
            return null;
        }
        string wanted = Least().Match(needs) is { Success: true } least && least.Value.Trim() == needs.Trim() && Version.TryParse(Padded(least.Groups[1].Value), out Version? minimum)
            ? $"{minimum} or later" : needs.Trim();
        return $"{what} needs Node.js {wanted}, and {Path} is {version}: update it, or name a later one in {SettingsPlace}";
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
}
