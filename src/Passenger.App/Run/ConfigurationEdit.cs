using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Run;

/// <summary>A generated run configuration changed in place: the fields the person changed from what its source makes (a
/// launch profile, a compose file, a Dockerfile, the project), each by the store's name for it with its value, and a
/// dictionary's (the environment, the services' actions and projects) entry by entry, null for one taken out. Put over the
/// source it gives the configuration the person sees, so a field they never touched follows the source as it changes.</summary>
public static class ConfigurationEdit
{
    /// <summary>What makes a generated configuration the one it is, its link to its source: changed only by a copy.</summary>
    private static readonly HashSet<string> Identity = new(StringComparer.Ordinal) { "name", "kind", "project", "launchProfile" };

    /// <summary>The fields that hold a path or paths (a dictionary's values for <c>serviceProjects</c>), which the store
    /// keeps relative to the folder.</summary>
    private static readonly HashSet<string> PathFields = new(StringComparer.Ordinal) { "dockerfile", "context", "composeFiles", "envFiles", "serviceProjects", "linkedAppHosts" };

    /// <summary>The fields of <paramref name="edited"/> that differ from <paramref name="source"/>, as an edit; null when
    /// none does.</summary>
    public static JsonObject? Between(RunConfiguration source, RunConfiguration edited)
    {
        JsonObject before = Node(source);
        JsonObject edit = [];
        foreach ((string field, JsonNode? now) in Node(edited))
        {
            JsonNode? was = before[field];
            if (Identity.Contains(field) || JsonNode.DeepEquals(was, now))
            {
                continue;
            }
            if (was is JsonObject entriesBefore && now is JsonObject entriesNow)
            {
                JsonObject entries = [];
                foreach ((string key, JsonNode? value) in entriesNow)
                {
                    if (!entriesBefore.TryGetPropertyValue(key, out JsonNode? had) || !JsonNode.DeepEquals(had, value))
                    {
                        entries[key] = value?.DeepClone();
                    }
                }
                foreach ((string key, JsonNode? _) in entriesBefore)
                {
                    if (!entriesNow.ContainsKey(key))
                    {
                        entries[key] = null;
                    }
                }
                edit[field] = entries;
            }
            else
            {
                edit[field] = now?.DeepClone();
            }
        }
        return edit.Count == 0 ? null : edit;
    }

    /// <summary>The source with the edit put over it, marked as made from that source; a field this build does not know,
    /// or an identity field, is passed over, and an edit that does not read leaves the source as it is.</summary>
    public static RunConfiguration Over(RunConfiguration source, JsonObject edit)
    {
        JsonObject node = Node(source);
        foreach ((string field, JsonNode? value) in edit)
        {
            if (Identity.Contains(field) || !node.ContainsKey(field))
            {
                continue;
            }
            if (value is JsonObject entries && node[field] is JsonObject current)
            {
                foreach ((string key, JsonNode? entry) in entries)
                {
                    if (entry is null)
                    {
                        current.Remove(key);
                    }
                    else
                    {
                        current[key] = entry.DeepClone();
                    }
                }
            }
            else
            {
                node[field] = value?.DeepClone();
            }
        }
        RunConfiguration? made;
        try
        {
            made = node.Deserialize<RunConfiguration>(StateFile.Options);
        }
        catch (Exception unreadable) when (StateFile.DoesNotRead(unreadable))
        {
            return source;
        }
        return made is null ? source : made.Clamped() with
        {
            Generated = source.Generated,
            Origin = source.Origin,
            Unsupported = source.Unsupported,
            Source = source,
        };
    }

    /// <summary>The store's names of the fields that differ between the two, in the configuration's order.</summary>
    public static IReadOnlyList<string> Changed(RunConfiguration source, RunConfiguration edited) =>
        Between(source, edited) is { } edit ? [.. edit.Select(f => f.Key)] : [];

    /// <summary>Whether the two hold the same values, every field as the store keeps it.</summary>
    public static bool Same(RunConfiguration one, RunConfiguration other) => JsonNode.DeepEquals(Node(one), Node(other));

    /// <summary>An edit with each path it names mapped, as the store keeps them relative to the folder and the window full.</summary>
    public static JsonObject Mapped(JsonObject edit, Func<string, string> path)
    {
        JsonObject mapped = [];
        foreach ((string field, JsonNode? value) in edit)
        {
            mapped[field] = !PathFields.Contains(field) ? value?.DeepClone() : value switch
            {
                JsonValue one when one.TryGetValue(out string? text) => JsonValue.Create(path(text)),
                JsonArray many => new JsonArray([.. many.Select(p => p is JsonValue each && each.TryGetValue(out string? text) ? JsonValue.Create(path(text)) : p?.DeepClone())]),
                JsonObject entries => new JsonObject(entries.Select(e => KeyValuePair.Create(e.Key,
                    e.Value is JsonValue each && each.TryGetValue(out string? text) ? (JsonNode?)JsonValue.Create(path(text)) : e.Value?.DeepClone()))),
                _ => value?.DeepClone(),
            };
        }
        return mapped;
    }

    private static JsonObject Node(RunConfiguration configuration) => (JsonObject)JsonSerializer.SerializeToNode(configuration, StateFile.Options)!;
}
