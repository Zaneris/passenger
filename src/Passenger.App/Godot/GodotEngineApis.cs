using System.Collections.Concurrent;
using System.Diagnostics;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;
using Passenger.Workspace.GDScript;

namespace Passenger.App.Godot;

/// <summary>The engine's API as the Godot found writes it (<c>--dump-extension-api</c>, headless, with user folders of its
/// own), dumped once per Godot version and build into the state's cache and read once per process: what GDScript's types
/// are resolved against; its class reference, the same dump with its documentation (from Godot 4.2), kept as its
/// descriptions alone (<see cref="GodotClassReference"/>); and the shading language's table as its compiler takes it
/// (<see cref="ShaderTableAsync"/>). None where no Godot is found or its dump fails, which leaves GDScript typed by its
/// own scripts and built-in types alone, its cards without the engine's words, and shaders read by the carried table.</summary>
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

    /// <summary>The class reference last asked for, by the setting it was asked with and when. A UI-thread field.</summary>
    private (string? Setting, long At, Task<GodotClassReference?> Reference)? referenced;

    private static string ReferenceFolder => Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "class-reference");

    private static readonly ConcurrentDictionary<string, Lazy<Task<GodotClassReference?>>> ReferenceRead = new(StringComparer.Ordinal);

    /// <summary>The first Godot whose dump of its API holds its documentation.</summary>
    private static readonly Version Documented = new(4, 2);

    /// <summary>The class reference of the Godot Settings name or found; null where none is, it is older than 4.2, or its
    /// dump failed.</summary>
    public Task<GodotClassReference?> ReferenceAsync()
    {
        string? chosen = setting();
        if (referenced is { } kept && kept.Setting == chosen && Stopwatch.GetElapsedTime(kept.At) < Refound && !kept.Reference.IsFaulted)
        {
            return kept.Reference;
        }
        Task<GodotClassReference?> reference = FindReferenceAsync(chosen);
        referenced = (chosen, Stopwatch.GetTimestamp(), reference);
        return reference;
    }

    private async Task<GodotClassReference?> FindReferenceAsync(string? chosen) =>
        await KeyAsync(chosen) is ({ } found, { } key) && VersionOf(key) is { } version && version >= Documented
            ? await ReferenceRead.GetOrAdd(key, k => new Lazy<Task<GodotClassReference?>>(() => BackgroundWork.Run(() => LoadReferenceAsync(found, k)))).Value
            : null;

    /// <summary>The major and minor version a key begins with (<c>4.7</c> of <c>4.7.2.stable.mono…</c>); null for none.</summary>
    private static Version? VersionOf(string key) =>
        key.Split('.') is [{ } major, { } minor, ..] && int.TryParse(major, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int high)
            && int.TryParse(minor, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int low) ? new Version(high, low) : null;

    private async Task<GodotClassReference?> LoadReferenceAsync(GodotFound found, string key)
    {
        string kept = Path.Combine(ReferenceFolder, $"{key}.json");
        if (!File.Exists(kept))
        {
            string working = Path.Combine(ReferenceFolder, "dumping-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(working);
            ProcessStartInfo start = ChildProcesses.Command(found.Executable, [GodotExecutables.Headless, "--dump-extension-api-with-docs"], working);
            foreach ((string name, string value) in folders() ?? GodotUserFolders.Under(Path.Combine(Folder, "home")))
            {
                start.Environment[name] = value;
            }
            ChildResult result = await ChildProcesses.RunAsync(start, limit: Waits.Of(Dumping));
            string dumped = Path.Combine(working, "extension_api.json");
            if (!File.Exists(dumped))
            {
                IdeErrors.Write($"Godot ({found.Executable}) wrote no extension_api.json with its documentation: {ExitCodes.Said(result.ExitCode)}");
                return null;
            }
            try
            {
                AtomicFile.Write(kept, GodotClassReference.Reduce(await File.ReadAllBytesAsync(dumped)));
            }
            catch (System.Text.Json.JsonException e)
            {
                IdeErrors.Write($"Godot's API with its documentation from {found.Executable} could not be read: {e.Message}");
                return null;
            }
            Directory.Delete(working, recursive: true);
        }
        try
        {
            return GodotClassReference.Read(await File.ReadAllBytesAsync(kept));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            IdeErrors.Write($"Godot's class reference kept at {kept} could not be read: {e.Message}");
            return null;
        }
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

    private static string ShaderFolder => Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "shader-built-ins");

    private static readonly ConcurrentDictionary<string, Lazy<Task<GodotShaderTable>>> ShaderTables = new(StringComparer.Ordinal);

    /// <summary>The first Godot whose scripts can hear its logger, which the probe of its shading language reads.</summary>
    private static readonly Version Logged = new(4, 5);

    private static readonly Lazy<byte[]> ProbesScript = new(() =>
    {
        using Stream stream = typeof(GodotEngineApis).Assembly.GetManifestResourceStream("Godot/PassengerShaderProbes.gd")
            ?? throw new InvalidOperationException("Passenger carries no Godot/PassengerShaderProbes.gd");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        return bytes.ToArray();
    });

    /// <summary>The carried table of the shading language as the Godot Settings name or found takes it, with that Godot's
    /// version: each candidate's tiny shader (<see cref="GodotShaderTable.Probes"/>) compiled by it headless once per
    /// version and build, what it refused kept in the state's cache. The carried table, and no version, where no Godot of
    /// 4.5 or newer is found; the carried table where its probe fails.</summary>
    public Task<(GodotShaderTable Table, Version? Godot)> ShaderTableAsync()
    {
        string? chosen = setting();
        if (shaders is { } kept && kept.Setting == chosen && Stopwatch.GetElapsedTime(kept.At) < Refound && !kept.Table.IsFaulted)
        {
            return kept.Table;
        }
        Task<(GodotShaderTable Table, Version? Godot)> table = FindShaderTableAsync(chosen);
        shaders = (chosen, Stopwatch.GetTimestamp(), table);
        return table;
    }

    /// <summary>The shading language's table last asked for, by the setting it was asked with and when. A UI-thread field.</summary>
    private (string? Setting, long At, Task<(GodotShaderTable Table, Version? Godot)> Table)? shaders;

    private async Task<(GodotShaderTable Table, Version? Godot)> FindShaderTableAsync(string? chosen)
    {
        if (await KeyAsync(chosen) is not ({ } found, { } key) || VersionOf(key) is not { } version)
        {
            return (GodotShaderTable.Carried, null);
        }
        if (version < Logged)
        {
            return (GodotShaderTable.Carried, version);
        }
        string kept = $"{key}-{GodotShaderTable.Carried.Fingerprint}";
        return (await ShaderTables.GetOrAdd(kept, k => new Lazy<Task<GodotShaderTable>>(() => BackgroundWork.Run(() => LoadShaderTableAsync(found, k)))).Value, version);
    }

    private async Task<GodotShaderTable> LoadShaderTableAsync(GodotFound found, string key)
    {
        GodotShaderTable carried = GodotShaderTable.Carried;
        string kept = Path.Combine(ShaderFolder, $"{key}.json");
        if (!File.Exists(kept))
        {
            string working = Path.Combine(ShaderFolder, "probing-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(working);
            IReadOnlyList<GodotShaderProbe> probes = carried.Probes();
            AtomicFile.Write(Path.Combine(working, "project.godot"), "config_version=5\n"u8.ToArray());
            AtomicFile.Write(Path.Combine(working, "PassengerShaderProbes.gd"), ProbesScript.Value);
            string asked = Path.Combine(working, "probes.json");
            string refusedAt = Path.Combine(working, "refused.json");
            AtomicFile.Write(asked, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(probes.Select(p => p.Code).ToArray()));
            ProcessStartInfo start = ChildProcesses.Command(found.Executable, [GodotExecutables.Headless, "--path", working, "--script", "res://PassengerShaderProbes.gd", "--", asked, refusedAt], working);
            foreach ((string name, string value) in folders() ?? GodotUserFolders.Under(Path.Combine(Folder, "home")))
            {
                start.Environment[name] = value;
            }
            ChildResult result = await ChildProcesses.RunAsync(start, limit: Waits.Of(Dumping));
            if (!File.Exists(refusedAt))
            {
                IdeErrors.Write($"Godot ({found.Executable}) wrote no probe of its shading language: {ExitCodes.Said(result.ExitCode)}");
                return carried;
            }
            try
            {
                using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(refusedAt));
                string[] refused = [.. read.RootElement.EnumerateArray().Select(e => e.TryGetInt32(out int index) && index >= 0 && index < probes.Count ? probes[index].Key : null)
                    .OfType<string>().Order(StringComparer.Ordinal)];
                AtomicFile.Write(kept, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(refused));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
            {
                IdeErrors.Write($"Godot's probe of its shading language from {found.Executable} could not be read: {e.Message}");
                return carried;
            }
            Directory.Delete(working, recursive: true);
        }
        try
        {
            using System.Text.Json.JsonDocument read = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(kept));
            return carried.Without(new HashSet<string>(read.RootElement.EnumerateArray().Select(e => e.GetString()).OfType<string>(), StringComparer.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            IdeErrors.Write($"Godot's probe of its shading language kept at {kept} could not be read: {e.Message}");
            return carried;
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
