using System.Globalization;
using System.Net;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App;

/// <summary>How the theme explorer's search is ordered: Open VSX's own orders, and by name over what it found.</summary>
public enum OpenVsxOrder
{
    /// <summary>The most downloaded first.</summary>
    Downloads,

    /// <summary>The best rated first.</summary>
    Rating,

    /// <summary>The newest first.</summary>
    Newest,

    /// <summary>By name, A to Z, over the most downloaded found.</summary>
    Name,
}

/// <summary>An extension a search found: its namespace and name, the version it found, its name and words for a person, its
/// downloads, its average rating and how many rated it (none where nobody has), and where its <c>.vsix</c> is.</summary>
public sealed record OpenVsxEntry(string Namespace, string Name, string Version, string DisplayName, string Description, long Downloads,
    double? Rating, int Reviews, Uri? Download)
{
    /// <summary>Its id, <c>namespace.name</c>, as a theme's base names it.</summary>
    public string Id => $"{Namespace}.{Name}";
}

/// <summary>A search's answer: how many extensions it found in all, and those of the page asked for.</summary>
public sealed record OpenVsxPage(int Total, IReadOnlyList<OpenVsxEntry> Extensions);

/// <summary>Open VSX, the Eclipse Foundation's open registry of VS Code extensions, through its public API: a search of its
/// themes category, an extension's manifest, and its <c>.vsix</c>, its size checked before it is read; over
/// <see cref="OutboundHttp"/>, at <see cref="Variable"/>'s address where one is named. Asked only while the theme explorer
/// is open, or as a theme is picked or refreshed. Its answers are read as data (<see cref="ReadPage"/>). Called on any
/// thread.</summary>
public static class OpenVsx
{
    /// <summary>The variable naming the address Open VSX's API is asked at instead (a stand-in's).</summary>
    [IdentifierHome]
    public const string Variable = "PASSENGER_OPEN_VSX";

    /// <summary>The category its colour and icon themes are in.</summary>
    public const string Category = "Themes";

    /// <summary>How many extensions a page of the explorer's search holds.</summary>
    public const int PageSize = 24;

    private const int MostAnswerBytes = 4 * 1024 * 1024;
    private static readonly Uri Own = new("https://open-vsx.org/");
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);
    private static readonly HttpClient Http = OutboundHttp.Client(Limit, most: VsCodeThemes.MostVsixBytes);

    /// <summary>Where its API is asked: <see cref="Variable"/>'s address, else Open VSX's own. A value there that is no http
    /// or https address is fatal.</summary>
    public static Uri Registry
    {
        get
        {
            if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } named)
            {
                return Own;
            }
            if (WebAddress.Of(named) is not { } place || !WebAddress.Secure(place))
            {
                throw new FormatException($"{Variable} takes the https address (http on this machine) Open VSX's API is asked at instead, not '{named}'");
            }
            return place.AbsoluteUri.EndsWith('/') ? place : new Uri(place.AbsoluteUri + "/");
        }
    }

    /// <summary>Checks <see cref="Variable"/> as the IDE starts, a malformed one being fatal.</summary>
    public static void CheckVariable() => _ = Registry;

    /// <summary>The address of a search of its themes for some words (all of them for none), in an order, a page from
    /// <paramref name="offset"/>.</summary>
    public static Uri SearchAddress(string words, OpenVsxOrder order, int offset)
    {
        (string by, string direction) = order switch
        {
            OpenVsxOrder.Rating => ("rating", "desc"),
            OpenVsxOrder.Newest => ("timestamp", "desc"),
            _ => ("downloadCount", "desc"),
        };
        string query = words.Trim().Length == 0 ? string.Empty : $"query={Uri.EscapeDataString(words.Trim())}&";
        return new Uri(Registry, $"api/-/search?{query}category={Category}&sortBy={by}&sortOrder={direction}&size={PageSize}&offset={offset}");
    }

    /// <summary>A page of its themes for some words, in an order, or why not.</summary>
    public static async Task<(OpenVsxPage? Page, string? Why)> SearchAsync(string words, OpenVsxOrder order, int offset, CancellationToken cancel)
    {
        (byte[]? bytes, string? why) = await BytesAsync(SearchAddress(words, order, offset), MostAnswerBytes, cancel);
        if (bytes is null)
        {
            return (null, why);
        }
        OpenVsxPage? page = ReadPage(bytes, out string? unread);
        if (page is not null && order == OpenVsxOrder.Name)
        {
            page = page with { Extensions = [.. page.Extensions.OrderBy(e => e.DisplayName, StringComparer.CurrentCultureIgnoreCase)] };
        }
        return (page, unread);
    }

    /// <summary>A search's answer as data, or null with why where it is not one.</summary>
    public static OpenVsxPage? ReadPage(byte[] json, out string? why)
    {
        why = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || JsonMembers.Value(root, "extensions") is not { ValueKind: JsonValueKind.Array } found)
            {
                why = "Open VSX answered no list of extensions";
                return null;
            }
            List<OpenVsxEntry> entries = [];
            foreach (JsonElement entry in found.EnumerateArray())
            {
                if (Entry(entry) is { } read)
                {
                    entries.Add(read);
                }
            }
            return new OpenVsxPage(JsonMembers.Number(root, "totalSize") ?? entries.Count, entries);
        }
        catch (JsonException error)
        {
            why = $"Open VSX answered what is not JSON: {error.Message}";
            return null;
        }
    }

    /// <summary>An extension's metadata as data: its newest version (or the one asked for) and where its <c>.vsix</c> is.</summary>
    public static OpenVsxEntry? ReadExtension(byte[] json, out string? why)
    {
        why = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            if (Entry(document.RootElement) is { } entry)
            {
                return entry;
            }
            why = $"Open VSX answered no extension: {JsonMembers.Text(document.RootElement, "error") ?? "it names none"}";
            return null;
        }
        catch (JsonException error)
        {
            why = $"Open VSX answered what is not JSON: {error.Message}";
            return null;
        }
    }

    /// <summary>An extension's manifest, at the version a search found, or why not.</summary>
    public static async Task<(VsCodeExtension? Extension, string? Why)> ManifestAsync(OpenVsxEntry entry, CancellationToken cancel)
    {
        Uri address = new(Registry, $"api/{Uri.EscapeDataString(entry.Namespace)}/{Uri.EscapeDataString(entry.Name)}/{Uri.EscapeDataString(entry.Version)}/file/{VsCodeThemes.Manifest}");
        (byte[]? bytes, string? why) = await BytesAsync(address, ThemeFiles.MostBytes, cancel);
        if (bytes is null)
        {
            return (null, why);
        }
        ThemeRead<VsCodeExtension> read = VsCodeThemes.ReadManifest($"{entry.Id}'s {VsCodeThemes.Manifest}", bytes);
        return (read.Read, read.Refused);
    }

    /// <summary>An extension by its id, at a version (its newest for none), or why not.</summary>
    public static async Task<(OpenVsxEntry? Entry, string? Why)> ExtensionAsync(string id, string? version, CancellationToken cancel)
    {
        int dot = id.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == id.Length - 1)
        {
            return (null, $"{id} is no extension's id (publisher.name)");
        }
        string path = $"api/{Uri.EscapeDataString(id[..dot])}/{Uri.EscapeDataString(id[(dot + 1)..])}" + (version is null ? string.Empty : $"/{Uri.EscapeDataString(version)}");
        (byte[]? bytes, string? why) = await BytesAsync(new Uri(Registry, path), MostAnswerBytes, cancel);
        if (bytes is null)
        {
            return (null, why);
        }
        OpenVsxEntry? entry = ReadExtension(bytes, out string? unread);
        return (entry, unread);
    }

    /// <summary>An extension's <c>.vsix</c>, refused unread where its size is past the most Passenger reads.</summary>
    public static Task<(byte[]? Bytes, string? Why)> VsixAsync(OpenVsxEntry entry, CancellationToken cancel) =>
        entry.Download is { } download ? BytesAsync(download, VsCodeThemes.MostVsixBytes, cancel) : Task.FromResult<(byte[]?, string?)>((null, $"Open VSX names no .vsix for {entry.Id} {entry.Version}"));

    private static OpenVsxEntry? Entry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object || JsonMembers.Text(entry, "namespace") is not { Length: > 0 } space
            || JsonMembers.Text(entry, "name") is not { Length: > 0 } name || JsonMembers.Text(entry, "version") is not { Length: > 0 } version)
        {
            return null;
        }
        double? rating = JsonMembers.Value(entry, "averageRating") is { ValueKind: JsonValueKind.Number } rated && rated.TryGetDouble(out double average) ? average : null;
        long downloads = JsonMembers.Value(entry, "downloadCount") is { ValueKind: JsonValueKind.Number } count && count.TryGetInt64(out long many) ? many : 0;
        Uri? download = JsonMembers.Text(JsonMembers.Value(entry, "files"), "download") is { } at && WebAddress.Of(at) is { } address && WebAddress.Secure(address) ? address : null;
        return new OpenVsxEntry(space, name, version, JsonMembers.Text(entry, "displayName") is { Length: > 0 } shown ? shown : name,
            JsonMembers.Text(entry, "description") ?? string.Empty, downloads, rating, JsonMembers.Number(entry, "reviewCount") ?? 0, download);
    }

    /// <summary>What an address answers, at most <paramref name="most"/> bytes, or why not.</summary>
    private static async Task<(byte[]? Bytes, string? Why)> BytesAsync(Uri address, long most, CancellationToken cancel)
    {
        try
        {
            using HttpRequestMessage request = OutboundHttp.Request(HttpMethod.Get, address);
            using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, $"Open VSX has no {address.AbsolutePath}");
            }
            if (!response.IsSuccessStatusCode)
            {
                return (null, $"{address} answers {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            if (response.Content.Headers.ContentLength is > 0 and var length && length > most)
            {
                return (null, $"{address} is {length.ToString("N0", CultureInfo.InvariantCulture)} bytes, more than the {most.ToString("N0", CultureInfo.InvariantCulture)} Passenger reads");
            }
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancel);
            using MemoryStream read = new();
            byte[] chunk = new byte[81920];
            int got;
            while ((got = await stream.ReadAsync(chunk, cancel)) > 0)
            {
                if (read.Length + got > most)
                {
                    return (null, $"{address} is more than the {most.ToString("N0", CultureInfo.InvariantCulture)} bytes Passenger reads");
                }
                read.Write(chunk, 0, got);
            }
            return (read.ToArray(), null);
        }
        catch (Exception error) when (error is HttpRequestException or IOException || (error is TaskCanceledException && !cancel.IsCancellationRequested))
        {
            return (null, $"{address} could not be read: {(error is TaskCanceledException ? $"no answer in {Limit.TotalSeconds:0} s" : error.Message)}");
        }
    }
}
