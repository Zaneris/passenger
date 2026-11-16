using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Settings;

/// <summary>Settings told apart as settings.json keeps them: the members whose values differ, each by its path from the
/// file's top, a member of an object after a dot ("xamlFormat.attributeStyle").</summary>
public static class SettingMembers
{
    public static IReadOnlyList<string> Changed(AppSettings one, AppSettings other)
    {
        List<string> changed = [];
        Compare(Node(one), Node(other), null, changed);
        return changed;
    }

    private static JsonObject Node(AppSettings settings) => (JsonObject)JsonSerializer.SerializeToNode(settings, StateFile.Options)!;

    private static void Compare(JsonNode? one, JsonNode? other, string? path, List<string> changed)
    {
        if (one is JsonObject first && other is JsonObject second)
        {
            foreach (string name in first.Select(m => m.Key).Union(second.Select(m => m.Key), StringComparer.Ordinal))
            {
                Compare(first[name], second[name], path is null ? name : $"{path}.{name}", changed);
            }
        }
        else if (!JsonNode.DeepEquals(one, other))
        {
            changed.Add(path ?? string.Empty);
        }
    }
}
