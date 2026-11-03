using System.Collections.Concurrent;
using System.Diagnostics;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App.Godot;

/// <summary>The engine's API as the Godot found writes it (<c>--dump-extension-api</c>, headless, with user folders of its
/// own), dumped once per Godot version and build into the state's cache and read once per process: what GDScript's types
/// are resolved against. None where no Godot is found or its dump fails, which leaves GDScript typed by its own scripts
/// and built-in types alone.</summary>
internal sealed class GodotEngineApis(Func<string?> setting, Func<IReadOnlyDictionary<string, string>?> folders)
{
    /// <summary>How long a dump may take: Godot's start on a machine's first run, its classes written.</summary>
    private static readonly TimeSpan Dumping = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, Lazy<Task<GodotEngineApi?>>> Read = new(StringComparer.Ordinal);

    private static string Folder => Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "extension-api");

    /// <summary>The API of the Godot Settings name or found; null where none is, or its dump failed.</summary>
    public async Task<GodotEngineApi?> ApiAsync()
    {
        string? chosen = setting();
        GodotFound? found = await BackgroundWork.Run(() => GodotLaunch.Found(chosen));
        if (found is not { Exists: true })
        {
            return null;
        }
        string? version = found.Version ?? await GodotExecutables.VersionAsync(found);
        if (version is null)
        {
            return null;
        }
        string key = string.Concat(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
        return await Read.GetOrAdd(key, k => new Lazy<Task<GodotEngineApi?>>(() => BackgroundWork.Run(() => LoadAsync(found, k)))).Value;
    }

    private async Task<GodotEngineApi?> LoadAsync(GodotFound found, string key)
    {
        string kept = Path.Combine(Folder, $"{key}.json");
        if (!File.Exists(kept) && !await DumpAsync(found, kept))
        {
            return null;
        }
        try
        {
            return GodotEngineApi.Read(await File.ReadAllBytesAsync(kept));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            IdeErrors.Write($"Godot's API kept at {kept} could not be read: {e.Message}");
            return null;
        }
    }

    /// <summary>Godot's dump written into a folder of its own, then moved into the cache.</summary>
    private async Task<bool> DumpAsync(GodotFound found, string kept)
    {
        string working = Path.Combine(Folder, "dumping-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(working);
        ProcessStartInfo start = ChildProcesses.Command(found.Executable, [GodotExecutables.Headless, "--dump-extension-api"], working);
        foreach ((string name, string value) in folders() ?? GodotUserFolders.Under(Path.Combine(Folder, "home")))
        {
            start.Environment[name] = value;
        }
        ChildResult result = await ChildProcesses.RunAsync(start, limit: Waits.Of(Dumping));
        string dumped = Path.Combine(working, "extension_api.json");
        if (!File.Exists(dumped))
        {
            IdeErrors.Write($"Godot ({found.Executable}) wrote no extension_api.json: {ExitCodes.Said(result.ExitCode)}");
            return false;
        }
        AtomicFile.Write(kept, await File.ReadAllBytesAsync(dumped));
        Directory.Delete(working, recursive: true);
        IdeErrors.Write($"Godot's API for GDScript's types dumped from {found.Executable} into {kept}");
        return true;
    }
}
