using System.Text;
using Passenger.Core;

namespace Passenger.App.Editors;

/// <summary>An HTML file's page as Passenger serves it to a web view that reads no folder for a page given as text
/// (WebView2): the host's root is the open folder, or the file's own where none is open or the file is outside it, so an
/// address climbing from the page (../css/site.css) reaches what the folder holds. The page stands at its path below that
/// root with the editor's text as it is now, and every other address is answered only from under the root (links
/// resolved), nothing else on the disk. The host is under .invalid, which no name server answers, so an address of it
/// never leaves the machine.</summary>
public sealed class ServedPage
{
    /// <summary>The host every page is served on.</summary>
    public const string Host = "preview.passenger.invalid";

    private readonly string page;
    private readonly string folder;
    private readonly byte[] text;

    /// <summary>The page of the file at <paramref name="path"/>, its text <paramref name="html"/>, with
    /// <paramref name="openFolder"/> open in the window, null for none.</summary>
    public ServedPage(string path, string html, string? openFolder)
    {
        page = PathNames.Canonical(path);
        string? open = openFolder is null ? null : PathNames.Canonical(openFolder);
        folder = open is not null && PathNames.IsUnder(page, open) ? open : Path.GetDirectoryName(page) ?? page;
        text = Encoding.UTF8.GetBytes(html);
        string below = PathNames.Below(page, folder).ToString();
        Address = new Uri($"https://{Host}/{string.Join('/', below.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString))}");
    }

    /// <summary>Where the web view is sent for the page.</summary>
    public Uri Address { get; }

    /// <summary>The file under the host's root an address on the host names, whether or not it is there; null for an
    /// address elsewhere, the root itself, or a place outside it.</summary>
    public string? FileOf(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort
            || !string.Equals(address.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        string below = Uri.UnescapeDataString(address.AbsolutePath).TrimStart('/');
        if (below.Length == 0 || below.Contains('\0'))
        {
            return null;
        }
        string file = PathNames.Canonical(Path.Join(folder, below));
        return PathNames.IsUnder(file, folder) ? file : null;
    }

    /// <summary>What a request of an address is answered with: the page's own text for the page, a file under the root
    /// that is there, and for anything else that nothing is there.</summary>
    public PageAnswer Answer(Uri address)
    {
        if (FileOf(address) is not { } file)
        {
            return PageAnswer.Missing;
        }
        if (PathNames.Same(file, page))
        {
            return new PageAnswer(200, null, text, "text/html; charset=utf-8");
        }
        return File.Exists(file) ? new PageAnswer(200, file, null, TypeOf(file)) : PageAnswer.Missing;
    }

    /// <summary>The media type a file is served as, from its extension; null where the web view is left to tell.</summary>
    public static string? TypeOf(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".xhtml" => "application/xhtml+xml",
        ".css" => "text/css",
        ".js" or ".mjs" or ".cjs" => "text/javascript",
        ".json" or ".map" => "application/json",
        ".webmanifest" => "application/manifest+json",
        ".wasm" => "application/wasm",
        ".xml" => "application/xml",
        ".txt" => "text/plain",
        ".csv" => "text/csv",
        ".vtt" => "text/vtt",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".ogv" => "video/ogg",
        ".mp3" => "audio/mpeg",
        ".ogg" or ".oga" => "audio/ogg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        _ => null,
    };
}

/// <summary>What a request on the page's host is answered with: its status, the file whose bytes are sent or the page's own
/// text, and the media type said, null where the web view tells.</summary>
public readonly record struct PageAnswer(int Status, string? File, byte[]? Text, string? Type)
{
    /// <summary>The answer for an address with nothing there.</summary>
    public static PageAnswer Missing { get; } = new(404, null, null, null);

    /// <summary>The status's words.</summary>
    public string Reason => Status == 200 ? "OK" : "Not Found";

    /// <summary>The headers sent, each read again at every request, so a page given again or a file changed on disk is
    /// never taken from a cache.</summary>
    public string Headers => Type is null ? "Cache-Control: no-store" : $"Content-Type: {Type}\r\nCache-Control: no-store";

    /// <summary>The bytes sent, read from the file as it is now; null for none. A file shares every access, so the person
    /// can write, rename or delete it while it is read.</summary>
    public Stream? Open() => Text is { } page ? new MemoryStream(page, writable: false)
        : File is { } file ? new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)
        : null;
}
