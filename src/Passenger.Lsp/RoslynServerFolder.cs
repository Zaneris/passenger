using System.Diagnostics;
using System.Text;
using Passenger.Core;

namespace Passenger.Lsp;

/// <summary>Where Roslyn's server runs from. It writes its MEF caches in cache/ beside its own assemblies and takes no other
/// folder for them, so where the app's roslyn/ is inside a macOS app bundle (a file added there breaks the bundle's seal,
/// and Gatekeeper then calls a downloaded copy damaged) or cannot be written (a system-wide Linux install), the
/// server runs from a copy in the state's cache, one per build of it (<see cref="Stamp"/>); elsewhere from roslyn/ itself.
/// Passenger's own reads of roslyn/ (the Razor design-time targets) stay with the app's.</summary>
public static class RoslynServerFolder
{
    /// <summary>The server's assembly, which dotnet runs.</summary>
    public const string Assembly = "Microsoft.CodeAnalysis.LanguageServer.dll";

    /// <summary>Where the app carries the server.</summary>
    public static string Carried => Path.Combine(AppContext.BaseDirectory, "roslyn");

    /// <summary>The variable a program on macOS and Linux takes its temporary folder from.</summary>
    public const string TemporaryVariable = "TMPDIR";

    /// <summary>The folder holding each server's temporary folder (<see cref="TemporaryOf"/>), where it writes what Go to
    /// Declaration opens of a library (MetadataAsSource): off Windows, a folder of the person's own for the state's cache
    /// (<see cref="UserTemporary.Of"/>, made theirs by <see cref="UserTemporary.Own"/>), so another user's folder on a
    /// shared /tmp neither takes its writes nor fails it; null on Windows, where each server takes Passenger's own, the
    /// person's alone, or where none of theirs could be made.</summary>
    public static string? Temporaries(string stateCache)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            return UserTemporary.Own(UserTemporary.Of(stateCache, TemporariesKind));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"The C# language server takes the shared temporary folder: {error.Message}");
            return null;
        }
    }

    /// <summary>A server's own temporary folder (TMPDIR) in <see cref="Temporaries"/>, named for this process first, those
    /// of a process no longer running removed as it is made: a server clears away, as it first writes a library's code,
    /// every other's code it cannot see running, and on macOS and Linux sees none in another session, where Passenger
    /// starts each. Null where there is no <see cref="Temporaries"/>.</summary>
    public static string? TemporaryOf(string stateCache)
    {
        if (Temporaries(stateCache) is not { } temporaries)
        {
            return null;
        }
        foreach (string left in Directory.EnumerateDirectories(temporaries))
        {
            string name = Path.GetFileName(left);
            int dash = name.IndexOf('-', StringComparison.Ordinal);
            if (dash > 0 && int.TryParse(name.AsSpan(0, dash), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int owner)
                && owner != Environment.ProcessId && !DotnetProcesses.Runs(owner))
            {
                try
                {
                    Directory.Delete(left, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    PassengerLog.Write($"A C# language server's temporary folder stays, {left}: {error.Message}");
                }
            }
        }
        try
        {
            return UserTemporary.Own(Path.Combine(temporaries, $"{Environment.ProcessId}-{Interlocked.Increment(ref temporariesMade)}"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PassengerLog.Write($"The C# language server takes the state's temporary folder: {error.Message}");
            return temporaries;
        }
    }

    private static int temporariesMade;

    /// <summary>The kind of a state's own temporary folder (<see cref="UserTemporary.Of"/>) that holds its servers'.</summary>
    public const string TemporariesKind = "roslyn";

    /// <summary>The folder the server writes its caches to, under the one it runs from: never part of a copy or its stamp.</summary>
    private const string Written = "cache";

    /// <summary>The mark in a copy whose time says when a server last ran from it.</summary>
    private const string UsedMark = ".used";

    /// <summary>A copy of another build whose mark is this old is no running server's, which marks its own every
    /// <see cref="Keep"/>, in this Passenger or another, and is removed, as is a copy left half made as long.</summary>
    private static readonly TimeSpan Unused = TimeSpan.FromDays(1);

    private static readonly TimeSpan Keep = TimeSpan.FromHours(1);

    /// <summary>The copies this process's servers run from, marked used while each runs.</summary>
    private static readonly List<(string Copy, LanguageServer Server)> running = [];
    private static Timer? keeping;

    /// <summary>The copy the server is to run from, given the state's cache folder, where the server may not write in the
    /// carried folder: under the cache's roslyn/, made if this build has none yet (an APFS clone on macOS, else a plain
    /// copy, either made in a temporary folder renamed into place, so a Passenger starting meanwhile never runs half a
    /// copy), copies of other builds left unused for a day removed. Null where it runs from the carried folder.</summary>
    internal static async Task<string?> CopyAsync(string? stateCache, CancellationToken cancel)
    {
        string carried = PathNames.Canonical(Carried);
        // A file added inside a macOS app bundle breaks its seal.
        if (stateCache is null || (!(OperatingSystem.IsMacOS() && MacBundles.Holds(carried)) && CanWrite(carried)))
        {
            return null;
        }
        string copies = Path.Combine(PathNames.Canonical(stateCache), "roslyn");
        string copy = Path.Combine(copies, Stamp(carried));
        try
        {
            Directory.CreateDirectory(copies);
            if (!File.Exists(Path.Combine(copy, Assembly)))
            {
                await MakeAsync(carried, copies, copy, cancel).ConfigureAwait(false);
            }
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"The C# language server could not be copied out of the app to {copy}: {failed.Message}", failed);
        }
        UsedCache cache = Copies(copies);
        cache.Touch(copy);
        cache.Prune(DateTime.UtcNow, kept: copy);
        return copy;
    }

    /// <summary>Keeps marking a copy a server runs from as used, while it runs.</summary>
    internal static void Running(string copy, LanguageServer server)
    {
        lock (running)
        {
            running.Add((copy, server));
            keeping ??= new Timer(_ => MarkRunning(), null, Keep, Keep);
        }
    }

    private static void MarkRunning()
    {
        string[] copies;
        lock (running)
        {
            running.RemoveAll(each => each.Server.HasExited);
            copies = [.. running.Select(each => each.Copy).Distinct(PathNames.Comparer)];
        }
        foreach (string copy in copies)
        {
            Copies(Path.GetDirectoryName(copy)!).Touch(copy);
        }
    }

    /// <summary>The copies under the state's cache, each a folder kept by its mark.</summary>
    private static UsedCache Copies(string copies) => new(copies, UsedEntries.Folders(1, UsedMark), new UsedLimits(Unused, Unused, null, Unused));

    /// <summary>Whether the server can write its caches beside itself: a file can be made where it writes them.</summary>
    private static bool CanWrite(string carried)
    {
        string written = Path.Combine(carried, Written);
        string folder = Directory.Exists(written) ? written : carried;
        try
        {
            using FileStream probe = new(Path.Combine(folder, $".{Guid.NewGuid():N}{AtomicFile.Temporary}"), FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception refused) when (refused is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The name of a build's copy: the server's version, and a hash of every file's path, length and time, so a
    /// build that changes any file (an update to the same server, other assemblies left out) takes a copy of its own.</summary>
    private static string Stamp(string carried)
    {
        FileVersionInfo about = FileVersionInfo.GetVersionInfo(Path.Combine(carried, Assembly));
        string version = (about.ProductVersion ?? about.FileVersion) is { Length: > 0 } said ? said.Split('+')[0] : "server";
        StringBuilder listed = new();
        foreach (string file in Files(carried).Order(StringComparer.Ordinal))
        {
            FileInfo each = new(Path.Combine(carried, file));
            listed.Append(file).Append('\0').Append(each.Length).Append('\0').Append(each.LastWriteTimeUtc.Ticks).Append('\n');
        }
        string hash = FileNames.Hash(listed.ToString(), 12);
        return $"{new string([.. version.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')])}-{hash}";
    }

    /// <summary>The carried files, by their paths from its folder, but what a server run there wrote.</summary>
    private static IEnumerable<string> Files(string carried) =>
        Directory.EnumerateFiles(carried, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(carried, file))
            .Where(file => !file.StartsWith(Written + Path.DirectorySeparatorChar, StringComparison.Ordinal));

    /// <summary>Makes the copy in a temporary folder of its own, then renames it into place; another Passenger's that lands
    /// first is kept, being the same build's.</summary>
    private static async Task MakeAsync(string carried, string copies, string copy, CancellationToken cancel)
    {
        string making = Path.Combine(copies, $".{Guid.NewGuid():N}{AtomicFile.Temporary}");
        try
        {
            if (!OperatingSystem.IsMacOS() || !await CloneAsync(carried, making, cancel).ConfigureAwait(false))
            {
                CopyFiles(carried, making);
            }
            cancel.ThrowIfCancellationRequested();
            UsedCache.OwnFolders(making);
            if (Directory.Exists(Path.Combine(making, Written)))
            {
                Directory.Delete(Path.Combine(making, Written), recursive: true);
            }
            UsedCache cache = Copies(copies);
            cache.Touch(making);
            if (Directory.Exists(copy) && !File.Exists(Path.Combine(copy, Assembly)))
            {
                // A copy that lost its server (a person's clean-up gone wrong) is no copy any server can run from.
                cache.Remove(copy);
            }
            try
            {
                Directory.Move(making, copy);
            }
            catch (IOException) when (File.Exists(Path.Combine(copy, Assembly)))
            {
                // Another Passenger's copy of this build landed while this one was made: theirs stays, and this one goes below.
            }
        }
        finally
        {
            // Gone once moved into place; else what is left goes now, or with the next prune a day on.
            Copies(copies).Remove(making);
        }
    }

    /// <summary>An APFS clone (<c>cp -c</c>): no time and no space, the files sharing the app's blocks; false where it could
    /// not be made (another volume's file system), with nothing left of it.</summary>
    private static async Task<bool> CloneAsync(string carried, string making, CancellationToken cancel)
    {
        try
        {
            ChildResult cloned = await ChildProcesses.RunAsync(ChildProcesses.Command("/bin/cp", ["-c", "-R", carried, making]),
                limit: TimeSpan.FromMinutes(2), cancel: cancel).ConfigureAwait(false);
            if (cloned.Succeeded)
            {
                return true;
            }
        }
        catch (Exception failed) when (failed is TimeoutException or System.ComponentModel.Win32Exception)
        {
            // Copied file by file below.
        }
        Copies(Path.GetDirectoryName(making)!).Remove(making);
        return false;
    }

    private static void CopyFiles(string carried, string making)
    {
        Directory.CreateDirectory(making);
        foreach (string file in Files(carried))
        {
            string to = Path.Combine(making, file);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(Path.Combine(carried, file), to);
        }
    }
}
