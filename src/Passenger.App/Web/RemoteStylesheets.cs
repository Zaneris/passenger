using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The stylesheets a page links on the web (a CDN's Bootstrap): each downloaded only while Settings allow it, never
/// before, once for its address and its link's integrity however many ask at once, over HTTP or HTTPS, and kept in the
/// state's cache under both, where the editor shows it read-only: a page that moves to another integrity is given what the
/// address serves now. A copy is kept and used only where its bytes are what the integrity says. One whose link gives no
/// integrity says nothing of what the address serves now, so it is asked again each time the folder is opened, the copy
/// standing while it can't be. Called on any thread.</summary>
public sealed class RemoteStylesheets(Func<bool> download)
{
    /// <summary>A stylesheet is some hundreds of kilobytes, a framework's whole some megabytes.</summary>
    private static readonly HttpClient Http = OutboundHttp.Client(TimeSpan.FromSeconds(20), most: 16 * 1024 * 1024);

    /// <summary>How long a download that was not what its link's integrity says is left before asking again, as a CDN and a
    /// page may move to a new stylesheet at different times, where every keystroke's check would download it.</summary>
    private static readonly TimeSpan RefusedFor = TimeSpan.FromMinutes(10);

    /// <summary>Each download under way, by its copy, every window's ask taking it; let go only once it ended, never by an
    /// ask that stopped waiting, so the next ask finds the copy it wrote.</summary>
    private static readonly Dictionary<string, Task<byte[]?>> Downloading = new(StringComparer.Ordinal);

    /// <summary>Each link's fetch for the folder, by its address and integrity: one under way, or one that gave a copy still
    /// there, is the one every ask takes; one that gave none is made again by the next ask.</summary>
    private readonly Dictionary<(string Address, string? Integrity), Task<Fetched>> fetches = [];

    /// <summary>When each download last was not what its link's integrity says, by its address and that integrity.</summary>
    private readonly ConcurrentDictionary<(string Address, string? Integrity), long> refused = new();

    /// <summary>The copies' folder as last made of the state's root, swapped whole as any thread may read it.</summary>
    private static (string Root, string Folder)? known;

    /// <summary>A stylesheet's copy and its text, or why there is none.</summary>
    public sealed record Fetched(string? Path, string? Text, string? Why);

    /// <summary>The folder the copies are kept in, as the file system names it.</summary>
    public static string Folder
    {
        get
        {
            string root = State.StateStore.DefaultRoot;
            if (known is not { } kept || kept.Root != root)
            {
                known = kept = (root, PathNames.Canonical(Path.Combine(root, "cache", "stylesheets")));
            }
            return kept.Folder;
        }
    }

    /// <summary>Whether a file is a stylesheet's copy, which the editor shows read-only.</summary>
    public static bool IsCopy(string path) => PathNames.IsUnder(path, Folder);

    /// <summary>The stylesheet at an address, as its copy has it: kept, else downloaded now where Settings allow it; why
    /// not where it cannot be had or is not what the link's integrity says. A cancelled ask leaves the fetch to the others.</summary>
    public async Task<Fetched> FetchAsync(Uri address, string? integrity, CancellationToken cancel)
    {
        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
        {
            return new Fetched(null, null, $"Passenger reads stylesheets from http and https addresses, not {address}");
        }
        (string, string?) link = (address.AbsoluteUri, integrity);
        Task<Fetched> fetch;
        lock (fetches)
        {
            if (!fetches.TryGetValue(link, out Task<Fetched>? held) || held.IsCompleted && !Kept(held))
            {
                held = fetches[link] = BackgroundWork.Run(() => FetchOnceAsync(address, integrity, link));
            }
            fetch = held;
        }
        return await fetch.WaitAsync(cancel);
    }

    /// <summary>Whether a fetch ended with a copy that is still there.</summary>
    private static bool Kept(Task<Fetched> fetch) => fetch.IsCompletedSuccessfully && fetch.Result.Path is { } kept && File.Exists(kept);

    /// <summary>A link's copy, read, else downloaded where Settings allow it, for every ask of the link: never cancelled.</summary>
    private async Task<Fetched> FetchOnceAsync(Uri address, string? integrity, (string, string?) link)
    {
        if (refused.TryGetValue(link, out long since) && Environment.TickCount64 - since < RefusedFor.TotalMilliseconds)
        {
            return NotWhatItSays(address);
        }
        string path = CopyOf(address, integrity);
        byte[]? bytes;
        try
        {
            bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Fetched(null, null, $"{address}'s copy could not be read: {error.Message}");
        }
        bool unsaid = bytes is not null && integrity is null;
        if (bytes is null || !PageLinks.Matches(integrity, bytes) || unsaid && download())
        {
            if (!download())
            {
                return new Fetched(null, null, $"{address} is not downloaded: {SettingsDialog.PlaceOfPage(SettingsDialog.StylesheetsPage)} does not allow it");
            }
            try
            {
                bytes = await DownloadOnceAsync(address, integrity, path);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                // Asked again next time: a network that comes back finds it.
                if (!unsaid)
                {
                    return new Fetched(null, null, $"{address} could not be downloaded: {error.Message}");
                }
            }
            if (bytes is null)
            {
                refused[link] = Environment.TickCount64;
                return NotWhatItSays(address);
            }
        }
        refused.TryRemove(link, out _);
        return new Fetched(path, Encoding.UTF8.GetString(bytes), null);
    }

    /// <summary>The download of a link's copy under way, else one begun now.</summary>
    private static async Task<byte[]?> DownloadOnceAsync(Uri address, string? integrity, string path)
    {
        Task<byte[]?> once;
        lock (Downloading)
        {
            if (!Downloading.TryGetValue(path, out Task<byte[]?>? under))
            {
                under = Downloading[path] = BackgroundWork.Run(() => DownloadAsync(address, integrity, path));
            }
            once = under;
        }
        try
        {
            return await once;
        }
        finally
        {
            // The copy is what is kept: a download is shared only while it is under way.
            lock (Downloading)
            {
                if (Downloading.TryGetValue(path, out Task<byte[]?>? under) && under == once)
                {
                    Downloading.Remove(path);
                }
            }
        }
    }

    private static Fetched NotWhatItSays(Uri address) => new(null, null, $"{address} is not used: its bytes are not what its link's integrity says");

    /// <summary>Where a link's copy is kept: a folder of its own named by the hash of its address and integrity, holding a file
    /// of the name the address ends in.</summary>
    private static string CopyOf(Uri address, string? integrity)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{address.AbsoluteUri} {integrity}")))[..16];
        string name = Path.GetFileName(address.AbsolutePath);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            name = "stylesheet.css";
        }
        return Path.Combine(Folder, hash, name);
    }

    /// <summary>What an address serves now, kept as the link's copy where it is what the integrity says; null where it is not.</summary>
    private static async Task<byte[]?> DownloadAsync(Uri address, string? integrity, string path)
    {
        using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, address);
        using HttpResponseMessage response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        if (!PageLinks.Matches(integrity, bytes))
        {
            return null;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, bytes);
        return bytes;
    }
}
