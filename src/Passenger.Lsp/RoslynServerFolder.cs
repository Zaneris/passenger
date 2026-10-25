using System.Diagnostics;
using System.Security.Cryptography;
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
        if (stateCache is null || (!InAppBundle(carried) && CanWrite(carried)))
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

    /// <summary>Whether a folder is inside a macOS app bundle (<c>X.app/Contents/…</c>), whose seal a file added breaks.</summary>
    private static bool InAppBundle(string folder)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }
        for (string? at = Path.GetDirectoryName(folder); at is not null; at = Path.GetDirectoryName(at))
        {
            if (Path.GetFileName(at) == "Contents" && Path.GetFileName(Path.GetDirectoryName(at)) is { } app
                && app.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

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
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(listed.ToString())))[..12];
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
