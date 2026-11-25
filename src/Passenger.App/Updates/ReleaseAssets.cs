using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>A file of this Passenger's own release that it downloads the first time it needs it rather than carries (the
/// container tools, the debugger's 32-bit host): its version's release file, checked against that release's SHA256SUMS,
/// unpacked (a .zip, else a .tar.gz) into a folder of a cache of the state's, one a version (<see cref="FolderOf"/>), through
/// a temporary folder renamed into place, so a folder there is whole or absent. One download a folder at a time in this
/// process; another process's that lands first is kept. A cache keeps only the versions of the Passengers running: each
/// holds its own (<see cref="Hold"/>), and every other is removed once a process (<see cref="PruneOnce"/>). Any thread:
/// nothing here touches the window.</summary>
public static class ReleaseAssets
{
    /// <summary>The downloads under way, one a folder.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads = new(StringComparer.Ordinal);

    /// <summary>The folder of a cache's holding a release file of this version: <paramref name="name"/> in its version's.</summary>
    public static string FolderOf(string cache, string name) => Path.Combine(cache, Product.Version, name);

    /// <summary>The folder holding a release file's contents (<see cref="FolderOf"/> <paramref name="cache"/> and
    /// <paramref name="name"/>), its <paramref name="marker"/> inside: there already, else downloaded from
    /// <paramref name="releases"/>' release of this version, <paramref name="say"/> telling its progress in words naming
    /// <paramref name="what"/> it is. Throws <see cref="ReleaseAssetException"/> saying why there is none, a download that
    /// failed ending with <paramref name="unreached"/> where given.</summary>
    public static async Task<string> FolderAsync(ReleaseFeed releases, string asset, string cache, string name, string marker, string what, Action<string>? say,
        CancellationToken cancel, string? unreached = null)
    {
        string folder = FolderOf(cache, name);
        // Held before it is read or written, so another version's prune leaves it be; waited for off the window's thread.
        await BackgroundWork.Run(() => Hold(cache)).ConfigureAwait(false);
        if (File.Exists(Path.Combine(folder, marker)))
        {
            return folder;
        }
        SemaphoreSlim one = Downloads.GetOrAdd(folder, _ => new SemaphoreSlim(1, 1));
        await one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            return File.Exists(Path.Combine(folder, marker)) ? folder
                : await DownloadAsync(releases, asset, folder, marker, what, say, unreached, cancel).ConfigureAwait(false);
        }
        finally
        {
            one.Release();
        }
    }

    /// <summary>The release file downloaded, checked and unpacked into <paramref name="folder"/>.</summary>
    private static async Task<string> DownloadAsync(ReleaseFeed releases, string asset, string folder, string marker, string what, Action<string>? say,
        string? unreached, CancellationToken cancel)
    {
        string tag = $"v{Product.Version}";
        Uri release = new(releases.Downloads, $"{tag}/");
        string parent = Path.GetDirectoryName(folder)!;
        string archive = Path.Combine(parent, $".{asset}.{Guid.NewGuid():N}");
        string unpacking = Path.Combine(parent, $".{Path.GetFileName(folder)}.{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(parent);
            say?.Invoke($"Downloading {what}");
            string sums = await Releases.ReadTextAsync(new ReleaseFile("SHA256SUMS", new Uri(release, "SHA256SUMS"), 0), cancel).ConfigureAwait(false);
            string? expected = sums.Split('\n').Select(line => line.Trim().Split(' ', 2, StringSplitOptions.TrimEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1].TrimStart('*') == asset)?[0].ToLowerInvariant();
            if (expected is null)
            {
                throw new ReleaseAssetException($"{what} could not be downloaded: the {tag} release's SHA256SUMS lists no {asset}");
            }
            string got = await Releases.DownloadAsync(new ReleaseFile(asset, new Uri(release, asset), 0), archive,
                percent => say?.Invoke($"Downloading {what}: {percent}%"), cancel).ConfigureAwait(false);
            if (got != expected)
            {
                IdeLog.Write(IdeLog.Debugger, $"{asset} of {tag} has SHA-256 {got}, where SHA256SUMS says {expected}: refused");
                throw new ReleaseAssetException($"{what} downloaded from the {tag} release did not match its SHA256SUMS, and nothing of it was used");
            }
            say?.Invoke($"Unpacking {what}");
            Directory.CreateDirectory(unpacking);
            await UnpackAsync(archive, asset, unpacking, cancel).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(unpacking, marker)))
            {
                throw new ReleaseAssetException($"{what} could not be unpacked: {asset} of {tag} holds no {marker}");
            }
            if (Directory.Exists(folder) && !File.Exists(Path.Combine(folder, marker)))
            {
                // Something left without what it should hold, never a copy another Passenger put there whole.
                Directory.Delete(folder, recursive: true);
            }
            try
            {
                Directory.Move(unpacking, folder);
            }
            catch (IOException) when (File.Exists(Path.Combine(folder, marker)))
            {
                // Another Passenger's download landed while this one unpacked: theirs stays, and this one goes below.
            }
            return folder;
        }
        catch (Exception failed) when (failed is UpdateFailedException or UpdateRefusedException)
        {
            throw new ReleaseAssetException($"{what} could not be downloaded ({failed.Message}){(unreached is null ? string.Empty : $"; {unreached}")}");
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ReleaseAssetException($"{what} could not be unpacked: {failed.Message}");
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }
            if (Directory.Exists(unpacking))
            {
                Directory.Delete(unpacking, recursive: true);
            }
        }
    }

    /// <summary>Where a cache keeps its versions' holds, a file a version.</summary>
    private const string Running = ".running";

    /// <summary>How long a hold waits out another process's prune taking its version's file a moment.</summary>
    private static readonly TimeSpan HoldWaits = TimeSpan.FromSeconds(5);

    /// <summary>This process's holds on its version in each cache, open until it ends; null where none could be had.</summary>
    private static readonly ConcurrentDictionary<string, Lazy<FileStream?>> Holds = new(PathNames.Comparer);

    /// <summary>The caches this process has pruned.</summary>
    private static readonly ConcurrentDictionary<string, bool> Pruned = new(PathNames.Comparer);

    /// <summary>Holds this version's downloads in a cache against every other Passenger's prune until this process ends;
    /// whether they are held.</summary>
    public static bool Hold(string cache) => Holds.GetOrAdd(cache, each => new Lazy<FileStream?>(() => Held(each, Product.Version))).Value is not null;

    /// <summary>A version's downloads in a cache held against a prune while the stream is open, as a running Passenger of that
    /// version holds its own: its file under <see cref="Running"/> open sharing all, which a prune's open sharing nothing is
    /// refused while any holds it (the system lets go of it with a process however it ends). Null where it could not be had,
    /// the downloads then left to a prune.</summary>
    public static FileStream? Held(string cache, string version)
    {
        string path = Path.Combine(cache, Running, version);
        Deadline until = Deadline.In(HoldWaits);
        while (true)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                FileStream held = new(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                // A prune that took the file away as this opened it leaves this holding no file: it is had again.
                if (File.Exists(path))
                {
                    return held;
                }
                held.Dispose();
            }
            catch (IOException refused) when (State.StateFile.HeldElsewhere(refused) && !until.Passed)
            {
                // A prune of another version's Passenger has it for a moment.
            }
            catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
            {
                IdeErrors.Write($"Release files: {version}'s in {cache} could not be held, so another Passenger may remove them: {failed.Message}");
                return null;
            }
            Thread.Sleep(5);
        }
    }

    /// <summary>Once a process for each cache, off the window's thread: this version's downloads held, then every other
    /// version's that no running Passenger holds removed, with what an earlier removal left.</summary>
    public static void PruneOnce(string cache)
    {
        if (Pruned.TryAdd(cache, true))
        {
            IdeErrors.Observe(BackgroundWork.Run(() => Prune(cache)));
        }
    }

    private static void Prune(string cache)
    {
        if (!Directory.Exists(cache))
        {
            return;
        }
        Hold(cache);
        UsedCache folders = new(cache, UsedEntries.Folders(1), new UsedLimits(TimeSpan.MaxValue, TimeSpan.Zero, null, TimeSpan.MaxValue));
        try
        {
            string running = Path.Combine(cache, Running);
            List<string> versions = [.. Directory.EnumerateDirectories(cache).Select(Path.GetFileName).OfType<string>()
                .Concat(Directory.Exists(running) ? Directory.EnumerateFiles(running).Select(Path.GetFileName).OfType<string>() : [])
                .Where(v => v != Product.Version && IsVersion(v)).Distinct(StringComparer.Ordinal)];
            foreach (string version in versions)
            {
                (FileStream? unheld, string? kept) = Unheld(Path.Combine(running, version));
                using FileStream? taken = unheld;
                string folder = Path.Combine(cache, version);
                if (kept is not null)
                {
                    IdeErrors.Write($"Release files: {version}'s in {cache} are kept, {kept}");
                }
                else if (Directory.Exists(folder))
                {
                    IdeErrors.Write(folders.Remove(folder) ? $"Release files: {version}'s removed from {cache}, no Passenger of that version running"
                        : $"Release files: {version}'s in {cache} could not be removed now, and wait for the next start");
                }
            }
            // What a removal cut short left.
            foreach (string left in Directory.EnumerateDirectories(cache, "*" + UsedCache.Going))
            {
                folders.Remove(left);
            }
        }
        catch (Exception failed) when (failed is IOException or UnauthorizedAccessException)
        {
            IdeErrors.Write($"Release files: {cache} could not be pruned: {failed.Message}");
        }
    }

    /// <summary>A version's hold file had sharing nothing, gone once let go; else why its version is kept: a Passenger of it
    /// holds it, or it could not be had at all.</summary>
    private static (FileStream? Unheld, string? Kept) Unheld(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose), null);
        }
        catch (IOException held) when (State.StateFile.HeldElsewhere(held))
        {
            return (null, "held by a Passenger of that version still running");
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            return (null, $"their hold could not be read: {refused.Message}");
        }
    }

    /// <summary>Whether a cache's folder or hold is a version's, as Passenger's are named (0.5.0, 0.5.0-beta.1), never another
    /// folder that may stand there.</summary>
    private static bool IsVersion(string name) => System.Version.TryParse(name.Split('-', 2)[0], out System.Version? version) && version.Build >= 0;

    /// <summary>An archive's files into a folder, by its name's kind: a zip, else a gzipped tar. Neither writes outside the
    /// folder, whatever an entry's name says.</summary>
    private static async Task UnpackAsync(string archive, string asset, string into, CancellationToken cancel)
    {
        await using FileStream file = File.OpenRead(archive);
        if (asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(file, into, overwriteFiles: false, cancel).ConfigureAwait(false);
            return;
        }
        await using GZipStream unzipped = new(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(unzipped, into, overwriteFiles: false, cancel).ConfigureAwait(false);
    }
}

/// <summary>A release file Passenger downloads when it needs it could not be had: no download, a checksum that did not
/// match, an archive that would not unpack. Its message says so in a line, naming what it is first.</summary>
public sealed class ReleaseAssetException(string message) : Exception(message);
