using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Debugging;

/// <summary>A web app's static web assets as its build's development manifest maps them (the map the app serves them by while
/// it is developed), each file by the URL the app serves it at: where the SDK's browser tools it serves were written, a
/// Blazor Web App's by its WebAssembly client's build. Read from the manifest's text alone.</summary>
public sealed class DevelopmentAssets
{
    /// <summary>The manifest's file, in the app's intermediate output folder.</summary>
    public const string FileName = "staticwebassets.development.json";

    /// <summary>How the SDK's browser tools' configuration module is named: it pins the public half of the key their build
    /// wrote beside it.</summary>
    private const string BrowserToolsConfig = ".BrowserTools.Config.js";

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

    /// <summary>A node's asset and its children's, at the URL it is reached by, as deep as a manifest goes.</summary>
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
            if (BrowserTools is null && url.StartsWith("_framework/", StringComparison.Ordinal) && url.EndsWith(BrowserToolsConfig, StringComparison.Ordinal))
            {
                BrowserTools = Path.GetDirectoryName(file);
            }
        }
    }

    private static string? RootOf(JsonElement entry, IReadOnlyList<string> roots) =>
        JsonMembers.Number(entry, "ContentRootIndex") is int index && index >= 0 && index < roots.Count && Path.IsPathFullyQualified(roots[index]) ? roots[index] : null;
}
