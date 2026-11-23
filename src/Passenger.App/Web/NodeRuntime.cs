using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The runtime the web language servers, ESLint and Prettier run on (<see cref="Kind"/>): Node.js, the one Settings
/// names, else the first on the PATH as the login shell finds it (<see cref="Programs.Node"/>); or Bun or Deno, the PATH's.
/// Its version is asked of it, once. <see cref="Problem"/> says plainly why there is none to use. <see cref="Start"/> runs a
/// Node package's script on it as each runtime documents: Bun with no package installed behind the person's back, never the
/// folder's own bunfig.toml (whose preloads would run) or .env, Deno with every permission and the node_modules folders the
/// script's package lies in, never the folder's own deno.json or lockfile.</summary>
public sealed partial record NodeRuntime(string? Path, Version? Version, string? Problem)
{
    /// <summary>Where Settings names the interpreter, for what the status bar and banner say.</summary>
    public static readonly string SettingsPlace = SettingsDialog.PlaceOfPage(SettingsDialog.NodePage);

    /// <summary>The empty Bun configuration carried beside the servers, which Bun takes in place of the folder's.</summary>
    public static string BunConfig => System.IO.Path.Combine(WebLanguages.Carried, "passenger", "bunfig.toml");

    /// <summary>Which runtime it is.</summary>
    public ScriptRuntime Kind { get; init; } = ScriptRuntime.Node;

    /// <summary>Its name as Passenger says it ("Node.js").</summary>
    public string Name => NameOf(Kind);

    public static string NameOf(ScriptRuntime kind) => kind switch
    {
        ScriptRuntime.Bun => "Bun",
        ScriptRuntime.Deno => "Deno",
        _ => "Node.js",
    };

    /// <summary>The Node.js named, else the PATH's, with its version; one that cannot be run says why. Run off the UI
    /// thread: it starts <c>node --version</c>.</summary>
    public static Task<NodeRuntime> FindAsync(string? configured) => FindAsync(ScriptRuntime.Node, configured, null);

    /// <summary>The runtime of a kind with its version: Node.js as <see cref="FindAsync(string?)"/> finds it, Bun and Deno on
    /// the PATH; one that cannot be run says why, <paramref name="chosen"/> saying what chose it ("its bun.lock"). Run off the
    /// UI thread: it asks the runtime its version.</summary>
    public static async Task<NodeRuntime> FindAsync(ScriptRuntime kind, string? configured, string? chosen)
    {
        string name = NameOf(kind);
        string? path = kind != ScriptRuntime.Node ? Programs.Command(kind == ScriptRuntime.Bun ? "bun" : "deno")
            : configured is { Length: > 0 } named ? named : Programs.Node();
        string why = chosen is null ? string.Empty : $"the folder runs on {name}, as {chosen} says, and ";
        if (path is null)
        {
            return new NodeRuntime(null, null, kind switch
            {
                ScriptRuntime.Node => $"{Capital(why)}Node.js was not found on the PATH: install it, or name it in {SettingsPlace}",
                _ => $"{Capital(why)}{name} was not found on the PATH: install it from {(kind == ScriptRuntime.Bun ? "https://bun.sh" : "https://deno.com")}, or choose another runtime in {SettingsPlace}",
            }) { Kind = kind };
        }
        if (!File.Exists(path))
        {
            return new NodeRuntime(path, null, $"there is no Node.js at {path}, which {SettingsPlace} names") { Kind = kind };
        }
        try
        {
            ChildResult asked = await ChildProcesses.RunAsync(ChildProcesses.Command(path, ["--version"]), limit: TimeSpan.FromSeconds(15));
            if (asked.Succeeded && Numbered().Match(asked.Output) is { Success: true } number && Version.TryParse(number.Groups[1].Value, out Version? version))
            {
                return new NodeRuntime(path, version, null) { Kind = kind };
            }
            return new NodeRuntime(path, null, $"{path} did not say its version as {name} does ({ExitCodes.Said(asked.ExitCode)})") { Kind = kind };
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            return new NodeRuntime(path, null, $"{path} could not be run: {error.Message}") { Kind = kind };
        }
    }

    private static string Capital(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>The start of a Node package's script on it, with these arguments after it, in a folder (on Bun in the folder
    /// of Passenger's own bunfig.toml, which Bun reads where each program the script starts works): a preload first where
    /// given (a CommonJS <c>.cjs</c> or an ES module <c>.mjs</c>), and on Node.js a Plug'n'Play project's runtime loaded
    /// into it and every program it starts where <paramref name="pnp"/> names the project's .pnp.cjs.</summary>
    public ProcessStartInfo Start(string script, IReadOnlyList<string> arguments, string folder, string? preload = null, string? pnp = null)
    {
        bool module = preload is not null && preload.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<string> preloading = preload is null ? [] : Kind switch
        {
            ScriptRuntime.Bun => ["--preload", preload],
            ScriptRuntime.Deno => [module ? "--preload" : "--require", preload],
            // An ES module's preload, named by its URL, which --import takes where a Windows path is no specifier.
            _ => module ? ["--import", new Uri(preload).AbsoluteUri] : ["--require", preload],
        };
        IReadOnlyList<string> words = Kind switch
        {
            ScriptRuntime.Bun => ["--no-install", $"--config={BunConfig}", "--no-env-file", .. preloading, script, .. arguments],
            // Its node_modules read as Node.js reads them, so a package's .js files are CommonJS where it says no type.
            ScriptRuntime.Deno => ["run", "--allow-all", "--node-modules-dir=manual", "--no-config", "--no-lock", .. preloading, script, .. arguments],
            _ => [.. preloading, script, .. arguments],
        };
        ProcessStartInfo start = ChildProcesses.Command(Path!, words, Kind == ScriptRuntime.Bun ? System.IO.Path.GetDirectoryName(BunConfig) : folder);
        switch (Kind)
        {
            case ScriptRuntime.Deno:
                // Else it asks deno.land for a newer Deno as it runs.
                start.Environment["DENO_NO_UPDATE_CHECK"] = "1";
                break;
            case ScriptRuntime.Bun:
                // Else a crash's report is sent to bun.report.
                start.Environment["DO_NOT_TRACK"] = "1";
                break;
            default:
                if (pnp is not null)
                {
                    start.Environment["NODE_OPTIONS"] = YarnPnp.NodeOptions(pnp, start.Environment.TryGetValue("NODE_OPTIONS", out string? own) ? own : null);
                }
                break;
        }
        return start;
    }

    /// <summary>Why this Node.js cannot run what a package's package.json says needs another (its <c>engines.node</c>, as
    /// "&gt;=22.22.2", or eslint's "^20.19.0 || ^22.13.0 || &gt;=24"), or what Passenger runs with it needs beside
    /// (<paramref name="alsoNeeds"/>, a range as npm writes one), or null when it can; <paramref name="what"/> names what
    /// needs it ("the TypeScript language server"). Bun and Deno keep their own versions, which no such range is of.</summary>
    public string? TooOldFor(string package, string what, string? alsoNeeds = null) =>
        Kind != ScriptRuntime.Node ? null : Unmet(Engines(package), what) ?? Unmet(alsoNeeds, what);

    /// <summary>The Node.js a package's package.json says it needs; null where it says none or does not read.</summary>
    private static string? Engines(string package)
    {
        if (YarnPnp.ReadFile(package) is not { } text)
        {
            return null;
        }
        try
        {
            using JsonDocument read = JsonDocument.Parse(text);
            return JsonMembers.Text(JsonMembers.Object(read.RootElement, "engines"), "node");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
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
