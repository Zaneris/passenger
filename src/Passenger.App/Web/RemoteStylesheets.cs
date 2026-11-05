using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The stylesheets a page links on the web (a CDN's Bootstrap): each downloaded only while Settings allow it, never
/// before, once for its address and its link's integrity, over HTTP or HTTPS, and kept in the state's cache under both,
/// where the editor shows it read-only: a page that moves to another integrity is given what the address serves now. A
/// copy is kept and used only where its bytes are what the integrity says. Called on any thread.</summary>
public sealed class RemoteStylesheets(Func<bool> download)
{
    /// <summary>A stylesheet is some hundreds of kilobytes, a framework's whole some megabytes.</summary>
    private static readonly HttpClient Http = OutboundHttp.Client(TimeSpan.FromSeconds(20), most: 16 * 1024 * 1024);

    /// <summary>How long a download that was not what its link's integrity says is left before asking again, as a CDN and a
    /// page may move to a new stylesheet at different times, where every keystroke's check would download it.</summary>
    private static readonly TimeSpan RefusedFor = TimeSpan.FromMinutes(10);

    /// <summary>Each download under way, its bytes where they are what its link's integrity says, by its copy.</summary>
    private readonly ConcurrentDictionary<string, Task<byte[]?>> downloading = new(StringComparer.Ordinal);

    /// <summary>Each copy read and found to be what its link's integrity says, by its address and that integrity.</summary>
    private readonly ConcurrentDictionary<(string Address, string? Integrity), Fetched> read = new();

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
    /// not where it cannot be had or is not what the link's integrity says.</summary>
    public async Task<Fetched> FetchAsync(Uri address, string? integrity, CancellationToken cancel)
    {
        if (address.Scheme != Uri.UriSchemeHttps && address.Scheme != Uri.UriSchemeHttp)
        {
            return new Fetched(null, null, $"Passenger reads stylesheets from http and https addresses, not {address}");
        }
        (string, string?) link = (address.AbsoluteUri, integrity);
        if (read.TryGetValue(link, out Fetched? known) && known.Path is { } kept && File.Exists(kept))
        {
            return known;
        }
        if (refused.TryGetValue(link, out long since) && Environment.TickCount64 - since < RefusedFor.TotalMilliseconds)
        {
            return NotWhatItSays(address);
        }
        string path = CopyOf(address, integrity);
        byte[]? bytes;
        try
        {
            bytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, cancel) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Fetched(null, null, $"{address}'s copy could not be read: {error.Message}");
        }
        if (bytes is null || !PageLinks.Matches(integrity, bytes))
        {
            if (!download())
            {
                return new Fetched(null, null, $"{address} is not downloaded: {SettingsDialog.PlaceOfPage(SettingsDialog.StylesheetsPage)} does not allow it");
            }
            Task<byte[]?> once = downloading.GetOrAdd(path, _ => DownloadAsync(address, integrity, path));
            try
            {
                bytes = await once.WaitAsync(cancel);
                // The copy is what is kept: a download is shared only while it is under way.
                downloading.TryRemove(new KeyValuePair<string, Task<byte[]?>>(path, once));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                // Asked again next time: a network that comes back finds it.
                downloading.TryRemove(new KeyValuePair<string, Task<byte[]?>>(path, once));
                return new Fetched(null, null, $"{address} could not be downloaded: {error.Message}");
            }
            if (bytes is null)
            {
                refused[link] = Environment.TickCount64;
                return NotWhatItSays(address);
            }
        }
        refused.TryRemove(link, out _);
        Fetched fetched = new(path, Encoding.UTF8.GetString(bytes), null);
        read[link] = fetched;
        return fetched;
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
