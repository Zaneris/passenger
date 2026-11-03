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

    /// <summary>How long the Godot found for a setting is taken as found before it is looked for again: a typing pause
    /// asks for the API, and looking walks the PATH and the applications' folders.</summary>
    private static readonly TimeSpan Refound = TimeSpan.FromMinutes(1);

    /// <summary>The API last asked for, by the setting it was asked with and when. A UI-thread field.</summary>
    private (string? Setting, long At, Task<GodotEngineApi?> Api)? latest;

    /// <summary>The API of the Godot Settings name or found; null where none is, or its dump failed.</summary>
    public Task<GodotEngineApi?> ApiAsync()
    {
        string? chosen = setting();
        if (latest is { } kept && kept.Setting == chosen && Stopwatch.GetElapsedTime(kept.At) < Refound && !kept.Api.IsFaulted)
        {
            return kept.Api;
        }
        Task<GodotEngineApi?> api = FindAsync(chosen);
        latest = (chosen, Stopwatch.GetTimestamp(), api);
        return api;
    }

    private async Task<GodotEngineApi?> FindAsync(string? chosen) =>
        await KeyAsync(chosen) is ({ } found, { } key) ? await Read.GetOrAdd(key, k => new Lazy<Task<GodotEngineApi?>>(() => BackgroundWork.Run(() => LoadAsync(found, k)))).Value : null;

    /// <summary>The Godot Settings name or found, and the key of its version its readings are kept by; null where none is.</summary>
    private static async Task<(GodotFound Found, string Key)?> KeyAsync(string? chosen)
    {
        GodotFound? found = await BackgroundWork.Run(() => GodotLaunch.Found(chosen));
        if (found is not { Exists: true })
        {
            return null;
        }
        string? version = found.Version ?? await GodotExecutables.VersionAsync(found);
        return version is null ? null : (found, string.Concat(version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_')));
    }

    private static string ClassesFolder => Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "classes");

    private static readonly ConcurrentDictionary<string, Lazy<Task<GodotClassDefaults?>>> ClassesRead = new(StringComparer.Ordinal);

    private static readonly Lazy<byte[]> ClassesScript = new(() =>
    {
        using Stream stream = typeof(GodotEngineApis).Assembly.GetManifestResourceStream("Godot/PassengerClasses.gd")
            ?? throw new InvalidOperationException("Passenger carries no Godot/PassengerClasses.gd");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    });

    /// <summary>The engine's classes, their properties' hints and defaults, as the Godot Settings name or found lists them
    /// (<see cref="GodotClassDefaults"/>): read headless once per Godot version into the state's cache; null where no Godot
    /// is found or its reading fails.</summary>
    public async Task<GodotClassDefaults?> ClassesAsync() =>
        await KeyAsync(setting()) is ({ } found, { } key)
            ? await ClassesRead.GetOrAdd(key, k => new Lazy<Task<GodotClassDefaults?>>(() => BackgroundWork.Run(() => LoadClassesAsync(found, k)))).Value
            : null;

    private async Task<GodotClassDefaults?> LoadClassesAsync(GodotFound found, string key)
    {
        string kept = Path.Combine(ClassesFolder, $"{key}.json");
        if (!File.Exists(kept))
        {
            string working = Path.Combine(ClassesFolder, "reading-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(working);
            AtomicFile.Write(Path.Combine(working, "project.godot"), "config_version=5\n"u8.ToArray());
            AtomicFile.Write(Path.Combine(working, "PassengerClasses.gd"), ClassesScript.Value);
            string written = Path.Combine(working, "classes.json");
            ProcessStartInfo start = ChildProcesses.Command(found.Executable, [GodotExecutables.Headless, "--path", working, "--script", "res://PassengerClasses.gd", "--", written], working);
            foreach ((string name, string value) in folders() ?? GodotUserFolders.Under(Path.Combine(Folder, "home")))
            {
                start.Environment[name] = value;
            }
            ChildResult result = await ChildProcesses.RunAsync(start, limit: Waits.Of(Dumping));
            if (!File.Exists(written))
            {
                IdeErrors.Write($"Godot ({found.Executable}) wrote no reading of its classes: {ExitCodes.Said(result.ExitCode)}");
                return null;
            }
            AtomicFile.Write(kept, await File.ReadAllBytesAsync(written));
            Directory.Delete(working, recursive: true);
        }
        try
        {
            return GodotClassDefaults.Read(await File.ReadAllBytesAsync(kept));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            IdeErrors.Write($"Godot's classes kept at {kept} could not be read: {e.Message}");
            return null;
        }
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
