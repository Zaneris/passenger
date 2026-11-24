using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Passenger.Roslyn.Extension;

namespace Passenger.App.DependencyMap;

/// <summary>A folder's passenger-layers.json (docs/layers.md): read into its rules, which the extension checks each C# file
/// against and the map draws broken edges by, and written where a fix lets one layer use another.</summary>
public static class LayersFile
{
    /// <summary>The format a Passenger writes and reads; a later one is refused, saying so.</summary>
    public const int Format = 1;

    /// <summary>The file of a folder's rules.</summary>
    public static string PathIn(string folder) => Path.Combine(folder, LayerRules.FileName);

    /// <summary>A folder's rules as its file's text says them; none, saying why, for a file that cannot be read as rules.</summary>
    public static (LayerRules? Rules, string? Problem) Read(string folder, string text)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonElement root = read.RootElement;
            if (JsonMembers.Number(root, "passengerLayers") is > Format)
            {
                return (null, $"{LayerRules.FileName} is of a later format than this Passenger reads");
            }
            List<Layer> layers = [];
            foreach (JsonElement layer in JsonMembers.Items(root, "layers"))
            {
                if (JsonMembers.Text(layer, "name") is not { Length: > 0 } name)
                {
                    return (null, $"A layer of {LayerRules.FileName} has no name");
                }
                layers.Add(new Layer(name, JsonMembers.Texts(layer, "projects"), JsonMembers.Texts(layer, "namespaces"), JsonMembers.Texts(layer, "folders"),
                    JsonMembers.Texts(layer, "mayUse")));
            }
            return (new LayerRules(folder, layers), null);
        }
        catch (JsonException misread)
        {
            return (null, $"{LayerRules.FileName} cannot be read: {misread.Message}");
        }
    }

    /// <summary>A file's text with one layer let use another (added to its mayUse), written as JSON is written indented;
    /// null where the text has no such layer or it may already.</summary>
    public static string? LetUse(string text, string from, string to)
    {
        if (JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is not JsonObject root
            || root["layers"] is not JsonArray layers
            || layers.OfType<JsonObject>().FirstOrDefault(l => l["name"]?.GetValueKind() == JsonValueKind.String && (string?)l["name"] == from) is not { } layer)
        {
            return null;
        }
        if (layer["mayUse"] is not JsonArray may)
        {
            layer["mayUse"] = may = [];
        }
        if (may.Any(m => m?.GetValueKind() == JsonValueKind.String && (string?)m == to))
        {
            return null;
        }
        may.Add(to);
        return root.ToJsonString(Written) + (text.EndsWith('\n') ? "\n" : string.Empty);
    }

    private static readonly JsonSerializerOptions Written = new() { WriteIndented = true };
}
