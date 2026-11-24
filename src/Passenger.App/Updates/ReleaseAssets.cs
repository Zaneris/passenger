using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using Passenger.Core;

namespace Passenger.App.Updates;

/// <summary>A file of this Passenger's own release that it downloads the first time it needs it rather than carries (the
/// container tools, the debugger's 32-bit host): its version's release file, checked against that release's SHA256SUMS,
/// unpacked (a .zip, else a .tar.gz) into a folder of the state's cache through a temporary folder renamed into place, so a
/// folder there is whole or absent. One download a folder at a time in this process; another process's that lands first
/// is kept. Any thread: nothing here touches the window.</summary>
public static class ReleaseAssets
{
    /// <summary>The downloads under way, one a folder.</summary>
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Downloads = new(StringComparer.Ordinal);

    /// <summary>The folder holding a release file's contents, its <paramref name="marker"/> inside: there already, else
    /// downloaded from <paramref name="releases"/>' release of this version, <paramref name="say"/> telling its progress in
    /// words naming <paramref name="what"/> it is. Throws <see cref="ReleaseAssetException"/> saying why there is none, a
    /// download that failed ending with <paramref name="unreached"/> where given.</summary>
    public static async Task<string> FolderAsync(ReleaseFeed releases, string asset, string folder, string marker, string what, Action<string>? say,
        CancellationToken cancel, string? unreached = null)
    {
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
