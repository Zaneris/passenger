using System.Reflection;
using System.Runtime.InteropServices;
using Passenger.App.Updates;

namespace Passenger.App.Database;

/// <summary>Where DuckDB's engine, its native library, comes from: a build of one's own carries it beside the app, where its
/// driver's package puts it; a release carries none, its tens of megabytes kept out of every person's download, and the
/// first time a DuckDB connection opens downloads its own version's release file for its system into the state's cache
/// (<see cref="ReleaseAssets"/>), which the driver then loads. One for the process, as the library loads once in it.</summary>
public sealed record DuckDbNative(string Carried, ReleaseFeed Releases, string Cache)
{
    private static readonly AsyncLocal<Action<string>?> Telling = new();
    private static string? downloaded;
    private static int resolving;

    /// <summary>What the person is told it is.</summary>
    public const string Named = "DuckDB's engine";

    /// <summary>A source a test gives in place of this version's release; null for none.</summary>
    public static DuckDbNative? Given { get; set; }

    /// <summary>The source every DuckDB connection of the process takes: <see cref="Given"/>, else this version's release.</summary>
    public static DuckDbNative Current => Given ?? Default(ReleaseFeed.Default);

    /// <summary>The system the engine is built for, as the release names its file.</summary>
    public static string Rid => RuntimeInformation.RuntimeIdentifier;

    /// <summary>The release's file of the engine for this system, as the packaging scripts name it: a zip for Windows, else a
    /// gzipped tar.</summary>
    public static string Asset => $"Passenger-duckdb-{Rid}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";

    /// <summary>The engine's file, by the name the driver's package gives it on this system.</summary>
    public static string Library => OperatingSystem.IsWindows() ? "duckdb.dll" : OperatingSystem.IsMacOS() ? "libduckdb.dylib" : "libduckdb.so";

    /// <summary>The app's own folder, and the state's cache/duckdb, downloaded from <paramref name="releases"/>.</summary>
    public static DuckDbNative Default(ReleaseFeed releases) =>
        new(AppContext.BaseDirectory, releases, Path.Combine(State.StateStore.DefaultRoot, "cache", "duckdb"));

    /// <summary>The folder this version's engine is downloaded into.</summary>
    public string Downloaded => ReleaseAssets.FolderOf(Cache, Rid);

    /// <summary>The engine a build carries, beside the app or under the driver's runtimes folder; null for a release's.</summary>
    public string? CarriedLibrary => Beside(Carried);

    /// <summary>Tells the stages of a download as the operation that opens a connection runs, on any thread.</summary>
    public static IDisposable Tell(Action<string> say)
    {
        Telling.Value = say;
        return new Told();
    }

    /// <summary>Ends once the engine is here: carried, else downloaded now (its stages told where an operation asked), and
    /// loaded from the cache where the app carries none. Throws <see cref="ReleaseAssetException"/> saying why there is none.</summary>
    public async Task ReadyAsync(CancellationToken cancel)
    {
        if (CarriedLibrary is not null)
        {
            return;
        }
        string folder = await ReleaseAssets.FolderAsync(Releases, Asset, Cache, Rid, Library, $"{Named} for {Rid}", Telling.Value, cancel).ConfigureAwait(false);
        // A build that carries its own loads that one: two copies of the engine in one process would share no state.
        if (Beside(AppContext.BaseDirectory) is null)
        {
            downloaded = Path.Combine(folder, Library);
            if (Interlocked.Exchange(ref resolving, 1) == 0)
            {
                NativeLibrary.SetDllImportResolver(typeof(DuckDB.NET.Native.DuckDBType).Assembly, Resolve);
            }
        }
    }

    /// <summary>The engine beside a folder, where a build puts it: its own folder, or the driver's runtimes folder for this
    /// system or its family (macOS's one library for both processors).</summary>
    private static string? Beside(string folder) =>
        new[] { Path.Combine(folder, Library), Path.Combine(folder, "runtimes", Rid, "native", Library), Path.Combine(folder, "runtimes", "osx", "native", Library) }
            .FirstOrDefault(File.Exists);

    private static IntPtr Resolve(string name, Assembly from, DllImportSearchPath? path) =>
        name == "duckdb" && downloaded is { } file ? NativeLibrary.Load(file) : IntPtr.Zero;

    private sealed class Told : IDisposable
    {
        public void Dispose() => Telling.Value = null;
    }
}
