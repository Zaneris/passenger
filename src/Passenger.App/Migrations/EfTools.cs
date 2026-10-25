using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Passenger.Packages;

namespace Passenger.App.Migrations;

/// <summary>Where the <c>dotnet ef</c> a project runs comes from: the local tool manifest dotnet finds from its folder up,
/// else the person's global tool.</summary>
public enum EfToolPlace
{
    Local,
    Global,
}

/// <summary>The <c>dotnet ef</c> a project would run: where it is (the manifest naming it, or the global tool's program) and
/// its version.</summary>
public sealed record EfTool(EfToolPlace Place, string Version, string Where)
{
    /// <summary>How the tool is named to the person: "the local dotnet-ef 10.0.12" or "the global dotnet-ef 10.0.2".</summary>
    public string Shown => $"the {(Place == EfToolPlace.Local ? "local" : "global")} {EfTools.Id} {Version}";

    /// <summary>The command that runs it with these arguments in a folder: <c>dotnet tool run</c> for the local tool, which
    /// finds its manifest from there; the global tool's own program, on the IDE's .NET.</summary>
    public ProcessStartInfo Command(IEnumerable<string> arguments, string folder)
    {
        if (Place == EfToolPlace.Local)
        {
            return ChildProcesses.DotnetCommand(["tool", "run", EfTools.Id, .. arguments], folder);
        }
        ProcessStartInfo start = ChildProcesses.DotnetCommand(arguments, folder);
        string host = start.FileName;
        start.FileName = Where;
        // The tool's apphost finds the runtime the IDE runs builds on, whatever the PATH it was started with says.
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(host)!;
        return start;
    }
}

/// <summary>Finds a project's <c>dotnet ef</c> as dotnet finds it (a local tool manifest from its folder up, else the
/// person's global tool), and writes the local tool manifest Passenger makes for one.</summary>
public static class EfTools
{
    /// <summary>The tool's package and command.</summary>
    public const string Id = "dotnet-ef";

    /// <summary>The project's tool from its folder: the local one, else the global one; null with neither.</summary>
    public static EfTool? Find(string folder) => Local(folder) ?? Global();

    /// <summary>The local tool a manifest from the folder up names; null with none.</summary>
    public static EfTool? Local(string folder)
    {
        foreach (string manifest in Manifests(folder))
        {
            if (Read(manifest) is not { } read)
            {
                continue;
            }
            if (read["tools"] is JsonObject tools
                && tools.FirstOrDefault(t => t.Key.Equals(Id, StringComparison.OrdinalIgnoreCase)).Value is JsonObject tool
                && tool["version"]?.GetValue<string>() is { Length: > 0 } version)
            {
                return new EfTool(EfToolPlace.Local, version, manifest);
            }
            if (read["isRoot"] is JsonValue root && root.TryGetValue(out bool isRoot) && isRoot)
            {
                break;
            }
        }
        return null;
    }

    /// <summary>The manifest a local tool installed from the folder goes in: the nearest one from the folder up, else null,
    /// when Passenger makes one (<see cref="NewManifest"/>).</summary>
    public static string? NearestManifest(string folder) => Manifests(folder).FirstOrDefault(m => Read(m) is not null);

    /// <summary>A manifest of no tools, as <c>dotnet new tool-manifest</c> writes it, the root of the folder's tools.</summary>
    public const string NewManifest = """
        {
          "version": 1,
          "isRoot": true,
          "tools": {}
        }

        """;

    /// <summary>The person's global tool, from their tools folder (DOTNET_CLI_HOME's, else their home's); null with none.</summary>
    public static EfTool? Global()
    {
        string home = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } cli ? cli : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string tools = Path.Combine(home, ".dotnet", "tools");
        string program = Path.Combine(tools, OperatingSystem.IsWindows() ? $"{Id}.exe" : Id);
        string store = Path.Combine(tools, ".store", Id);
        if (!File.Exists(program) || !Directory.Exists(store))
        {
            return null;
        }
        string? version = Directory.GetDirectories(store).Select(Path.GetFileName).OfType<string>().Max(VersionOrder.Instance);
        return version is null ? null : new EfTool(EfToolPlace.Global, version, program);
    }

    /// <summary>Whether the tool's package at a version is in the package cache the folder's restores use, so restoring the
    /// folder's local tools reaches no feed.</summary>
    public static bool Cached(string version, string folder)
    {
        try
        {
            string packages = NuGet.Configuration.SettingsUtility.GetGlobalPackagesFolder(NuGet.Configuration.Settings.LoadDefaultSettings(folder));
            return File.Exists(Path.Combine(packages, Id, version.ToLowerInvariant(), $"{Id}.{version.ToLowerInvariant()}.nupkg"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or NuGet.Configuration.NuGetConfigurationException)
        {
            // A configuration that does not read: the restore is asked about, as one that downloads.
            return false;
        }
    }

    /// <summary>Each folder's manifests from the folder up, nearest first, as dotnet looks for them.</summary>
    private static IEnumerable<string> Manifests(string folder)
    {
        for (string? at = folder; at is not null; at = Path.GetDirectoryName(at))
        {
            foreach (string manifest in new[] { Path.Combine(at, ".config", "dotnet-tools.json"), Path.Combine(at, "dotnet-tools.json") })
            {
                if (File.Exists(manifest))
                {
                    yield return manifest;
                }
            }
        }
    }

    private static JsonObject? Read(string manifest)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(manifest), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // A manifest that does not read names no tool; dotnet says so itself if it is run.
            return null;
        }
    }
}
