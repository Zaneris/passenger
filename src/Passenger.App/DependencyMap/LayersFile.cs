using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Passenger.Core;
using Passenger.Roslyn.Extension;
using Passenger.Text;

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

    /// <summary>What a use breaking the rules says, wherever it is marked: the layers, what is used, and what the using layer
    /// may use.</summary>
    public static string Said(LayerRules rules, string from, string to, string used)
    {
        IReadOnlyList<string> may = rules.Layers.FirstOrDefault(l => l.Name == from)?.MayUse ?? [];
        return $"The {from} layer may not use the {to} layer, which {used} is in: {LayerRules.FileName} lets it use {(may.Count == 0 ? "no other layer" : Plural.Listed(may))}";
    }

    /// <summary>A file's text with one layer let use another, the name put into its mayUse where it is written (a mayUse made
    /// after its last member where it has none), on a line of its own where the list or layer is written over lines; the
    /// rest of the text, its comments among it, as it was. Null where the text has no such layer, it may already, or the text
    /// is no JSON.</summary>
    public static string? LetUse(string text, string from, string to)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        WrittenLayer? layer = null;
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return null;
            }
            while (layer is null && reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool layers = reader.ValueTextEquals("layers");
                reader.Read();
                if (!layers || reader.TokenType != JsonTokenType.StartArray)
                {
                    reader.Skip();
                    continue;
                }
                while (layer is null && reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }
                    WrittenLayer read = ReadLayer(ref reader);
                    layer = read.Name == from ? read : null;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        if (layer is null || layer.May?.Any(m => m.Value == to) == true || layer.LastValueEnd < 0)
        {
            return null;
        }
        string name = JsonSerializer.Serialize(to, Quoted);
        string lineBreak = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        int At(long place) => Encoding.UTF8.GetCharCount(bytes, 0, (int)place);
        bool Over(long start, long end) => text.AsSpan(At(start), At(end) - At(start)).Contains('\n');
        string IndentAt(long place) => TextNavigation.IndentBefore(text, At(place));
        (long where, string put) = layer.May switch
        {
            [.., var last] => (last.End, Over(layer.MayOpen, layer.MayClose) ? $",{lineBreak}{IndentAt(last.Start)}{name}" : $", {name}"),
            not null => (layer.MayOpen + 1, name),
            null => (layer.LastValueEnd, Over(layer.Start, layer.LastValueEnd) ? $",{lineBreak}{IndentAt(layer.LastNameAt)}\"mayUse\": [{name}]" : $", \"mayUse\": [{name}]"),
        };
        return text.Insert(At(where), put);
    }

    /// <summary>A layer as its file writes it, by UTF-8 places: its name, where it starts, where its last member's name starts
    /// and its value ends, and its mayUse's brackets and items, each with its value where it is a string.</summary>
    private sealed record WrittenLayer(string? Name, long Start, long LastNameAt, long LastValueEnd, List<(string? Value, long Start, long End)>? May, long MayOpen, long MayClose);

    /// <summary>The layer object the reader stands at the start of, read to its end.</summary>
    private static WrittenLayer ReadLayer(ref Utf8JsonReader reader)
    {
        long start = reader.TokenStartIndex;
        string? name = null;
        long lastNameAt = -1;
        long lastValueEnd = -1;
        List<(string?, long, long)>? may = null;
        long mayOpen = -1;
        long mayClose = -1;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            lastNameAt = reader.TokenStartIndex;
            string member = reader.GetString() ?? string.Empty;
            reader.Read();
            if (member == "name" && reader.TokenType == JsonTokenType.String)
            {
                name = reader.GetString();
            }
            else if (member == "mayUse" && reader.TokenType == JsonTokenType.StartArray)
            {
                may = [];
                mayOpen = reader.TokenStartIndex;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    long itemStart = reader.TokenStartIndex;
                    string? value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    reader.Skip();
                    may.Add((value, itemStart, reader.BytesConsumed));
                }
                mayClose = reader.TokenStartIndex;
            }
            else
            {
                reader.Skip();
            }
            lastValueEnd = reader.BytesConsumed;
        }
        return new WrittenLayer(name, start, lastNameAt, lastValueEnd, may, mayOpen, mayClose);
    }

    /// <summary>A name written into the file as JSON writes a string, its letters as they are.</summary>
    private static readonly JsonSerializerOptions Quoted = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
