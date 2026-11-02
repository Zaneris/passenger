using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Web;

/// <summary>The stylesheets a page links on the web (a CDN's Bootstrap): each downloaded only while Settings allow it, never
/// before, once, over HTTP or HTTPS, and kept in the state's cache by its address, where the editor shows it read-only; a
/// copy is used only while its bytes are what the link's integrity says. Called on any thread.</summary>
public sealed class RemoteStylesheets(Func<bool> download)
{
    /// <summary>A stylesheet is some hundreds of kilobytes, a framework's whole some megabytes.</summary>
    private static readonly HttpClient Http = OutboundHttp.Client(TimeSpan.FromSeconds(20), most: 16 * 1024 * 1024);

    private readonly ConcurrentDictionary<string, Task<string>> downloading = new(StringComparer.Ordinal);

    /// <summary>Each copy read and found to be what its link's integrity says, by its address and that integrity.</summary>
    private readonly ConcurrentDictionary<(string Address, string? Integrity), Fetched> read = new();

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
        if (read.TryGetValue((address.AbsoluteUri, integrity), out Fetched? known) && known.Path is { } kept && File.Exists(kept))
        {
            return known;
        }
        string path = CopyOf(address);
        if (!File.Exists(path))
        {
            if (!download())
            {
                return new Fetched(null, null, $"{address} is not downloaded: {SettingsDialog.PlaceOfPage(SettingsDialog.StylesheetsPage)} does not allow it");
            }
            Task<string> once = downloading.GetOrAdd(address.AbsoluteUri, _ => DownloadAsync(address, path));
            try
            {
                await once.WaitAsync(cancel);
                // The copy is what is kept: a download is shared only while it is under way.
                downloading.TryRemove(new KeyValuePair<string, Task<string>>(address.AbsoluteUri, once));
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
            {
                // Asked again next time: a network that comes back finds it.
                downloading.TryRemove(new KeyValuePair<string, Task<string>>(address.AbsoluteUri, once));
                return new Fetched(null, null, $"{address} could not be downloaded: {error.Message}");
            }
        }
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancel);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new Fetched(null, null, $"{address}'s copy could not be read: {error.Message}");
        }
        if (!PageLinks.Matches(integrity, bytes))
        {
            return new Fetched(null, null, $"{address} is not used: its bytes are not what its link's integrity says");
        }
        Fetched fetched = new(path, Encoding.UTF8.GetString(bytes), null);
        read[(address.AbsoluteUri, integrity)] = fetched;
        return fetched;
    }

    /// <summary>Where an address's copy is kept: a folder of its own named by its hash, holding a file of the name it ends in.</summary>
    private static string CopyOf(Uri address)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address.AbsoluteUri)))[..16];
        string name = Path.GetFileName(address.AbsolutePath);
        if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            name = "stylesheet.css";
        }
        return Path.Combine(Folder, hash, name);
    }

    private static async Task<string> DownloadAsync(Uri address, string path)
    {
        using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, address);
        using HttpResponseMessage response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        byte[] bytes = await response.Content.ReadAsByteArrayAsync();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Write(path, bytes);
        return path;
    }
}
