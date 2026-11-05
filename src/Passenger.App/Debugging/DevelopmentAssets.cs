using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>A web app's static web assets as its build's development manifest maps them (the map the app serves them by while
/// it is developed): each file by the URL the app serves it at, a referenced project's under its own (a Razor class
/// library's at <c>_content/Name/</c>), each folder whose files it serves as they come, and where the SDK's browser tools
/// it serves were written, a Blazor Web App's by its WebAssembly client's build. A file changed on disk is so told to a
/// page by the URL the page asks for it by. Read from the manifest's text alone.</summary>
public sealed class DevelopmentAssets
{
    /// <summary>The manifest's file, in the app's intermediate output folder.</summary>
    public const string FileName = "staticwebassets.development.json";

    /// <summary>How the SDK's browser tools' configuration module is named: it pins the public half of the key their build
    /// wrote beside it.</summary>
    private const string BrowserToolsConfig = ".BrowserTools.Config.js";

    /// <summary>How a referenced project's scoped CSS, bundled by its build, is named after it.</summary>
    private const string ScopedBundle = ".bundle.scp.css";

    /// <summary>How the app's own scoped CSS, with what it takes in of those it references, is named after it.</summary>
    private const string AppBundle = ".styles.css";

    /// <summary>Each file the app serves, by the shortest URL it serves it at.</summary>
    private readonly Dictionary<string, string> urls = new(PathNames.Comparer);

    /// <summary>The folders whose every file the app serves, with the URL each is served under.</summary>
    private readonly List<(string Folder, string Url)> folders = [];

    private DevelopmentAssets()
    {
    }

    /// <summary>The folder the SDK's browser tools the app serves were written in, their build's key with them; null where
    /// its build made none.</summary>
    public string? BrowserTools { get; private set; }

    /// <summary>The assets a manifest names; null for text that is no manifest.</summary>
    public static DevelopmentAssets? Read(ReadOnlySpan<byte> manifest)
    {
        JsonDocument read;
        try
        {
            read = JsonDocument.Parse(manifest.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
        using (read)
        {
            IReadOnlyList<string> roots = JsonMembers.Texts(read.RootElement, "ContentRoots");
            if (roots.Count == 0 || JsonMembers.Object(read.RootElement, "Root") is not { } top)
            {
                return null;
            }
            DevelopmentAssets assets = new();
            assets.Take(top, string.Empty, roots, 0);
            return assets;
        }
    }

    /// <summary>A node's asset, folder and children's, at the URL it is reached by, as deep as a manifest goes.</summary>
    private void Take(JsonElement node, string url, IReadOnlyList<string> roots, int depth)
    {
        if (depth > 64)
        {
            return;
        }
        foreach (JsonProperty child in JsonMembers.Properties(node, "Children"))
        {
            Take(child.Value, url.Length == 0 ? child.Name : $"{url}/{child.Name}", roots, depth + 1);
        }
        if (JsonMembers.Object(node, "Asset") is { } asset && url.Length > 0 && RootOf(asset, roots) is { } root && JsonMembers.Text(asset, "SubPath") is { Length: > 0 } below)
        {
            string file = Path.Join(root, below.Replace('/', Path.DirectorySeparatorChar));
            if (!urls.TryGetValue(file, out string? known) || url.Length < known.Length)
            {
                urls[file] = url;
            }
            if (BrowserTools is null && url.StartsWith("_framework/", StringComparison.Ordinal) && url.EndsWith(BrowserToolsConfig, StringComparison.Ordinal))
            {
                BrowserTools = Path.GetDirectoryName(file);
            }
        }
        foreach (JsonElement pattern in JsonMembers.Items(node, "Patterns"))
        {
            if (JsonMembers.Text(pattern, "Pattern") == "**" && RootOf(pattern, roots) is { } folder)
            {
                folders.Add((Path.TrimEndingDirectorySeparator(folder), url));
            }
        }
    }

    private static string? RootOf(JsonElement entry, IReadOnlyList<string> roots) =>
        JsonMembers.Number(entry, "ContentRootIndex") is int index && index >= 0 && index < roots.Count && Path.IsPathFullyQualified(roots[index]) ? roots[index] : null;

    /// <summary>The URL the app serves a file at, from its root (<c>_content/Kit/kit.css</c>); null for a file it does not
    /// serve.</summary>
    public string? UrlOf(string path)
    {
        if (urls.TryGetValue(path, out string? url))
        {
            return url;
        }
        foreach ((string folder, string under) in folders)
        {
            if (PathNames.IsUnder(path, folder))
            {
                string relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
                return under.Length == 0 ? relative : $"{under}/{relative}";
            }
        }
        return null;
    }

    /// <summary>The URL the app serves a referenced project's scoped CSS at, all of it one bundle the app's own takes in,
    /// without the fingerprint the manifest may give it (<c>_content/Kit/Kit.bundle.scp.css</c>); null where the app's build
    /// took in none of it.</summary>
    public string? BundleUrlOf(string project)
    {
        string bundle = Path.GetFileNameWithoutExtension(project) + ScopedBundle;
        foreach ((string file, string url) in urls)
        {
            if (string.Equals(Path.GetFileName(file), bundle, StringComparison.OrdinalIgnoreCase))
            {
                int slash = url.LastIndexOf('/');
                return slash < 0 ? Path.GetFileName(file) : $"{url[..slash]}/{Path.GetFileName(file)}";
            }
        }
        return null;
    }

    /// <summary>The file the app serves a project's scoped CSS from: its own bundle, or that of a project it references;
    /// null where it serves none.</summary>
    public string? BundleFileOf(string project, bool own)
    {
        string bundle = Path.GetFileNameWithoutExtension(project) + (own ? AppBundle : ScopedBundle);
        return urls.Keys.FirstOrDefault(file => string.Equals(Path.GetFileName(file), bundle, StringComparison.OrdinalIgnoreCase));
    }
}
