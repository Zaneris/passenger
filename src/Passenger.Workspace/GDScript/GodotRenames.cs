using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace.GDScript;

/// <summary>The names Godot 4 renamed from Godot 3's, which Godot's analyzer hints at where a name is not found (its
/// renamed_in_godot_4_hint, on by default): an identifier's (a property, an enum value, a colour, a class, a built-in
/// type) and a call's (a function, a built-in type), as the carried GodotRenames.json lists them (<c>just godot-renames</c>).</summary>
internal static class GodotRenames
{
    private const string Resource = "Passenger.Workspace.GodotRenames.json";

    private static readonly Lazy<(Dictionary<string, string> Identifiers, Dictionary<string, string> Calls)> Carried = new(() =>
    {
        using Stream stream = typeof(GodotRenames).Assembly.GetManifestResourceStream(Resource) ?? throw new InvalidOperationException($"Passenger carries no {Resource}");
        using JsonDocument table = JsonDocument.Parse(stream);
        return (Of(table.RootElement, "identifiers"), Of(table.RootElement, "calls"));
    });

    private static Dictionary<string, string> Of(JsonElement root, string map)
    {
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        if (JsonMembers.Object(root, map) is { } names)
        {
            foreach (JsonProperty name in names.EnumerateObject())
            {
                if (name.Value.ValueKind == JsonValueKind.String && name.Value.GetString() is { } now)
                {
                    renamed[name.Name] = now;
                }
            }
        }
        return renamed;
    }

    /// <summary>The hint after an identifier's error: <c> Did you mean to use "new"?</c>, or nothing.</summary>
    public static string Identifier(string name) =>
        Carried.Value.Identifiers.TryGetValue(name, out string? renamed) ? $" Did you mean to use \"{renamed}\"?" : string.Empty;

    /// <summary>The hint after a call's error: <c> Did you mean to use "new()"?</c>, or nothing.</summary>
    public static string Call(string name) =>
        Carried.Value.Calls.TryGetValue(name, out string? renamed) ? $" Did you mean to use \"{renamed}()\"?" : string.Empty;
}
