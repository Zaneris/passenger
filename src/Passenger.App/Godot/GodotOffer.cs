using System.Text.Json;
using System.Text.Json.Serialization;
using Passenger.Roslyn.Extension;

namespace Passenger.App.Godot;

/// <summary>What a Godot check's finding offers to mend it, carried with its mark: the texts its place may become, nearest
/// first (a node's path, a defined name); a member made in a script's code, or a GetNode call's type changed, by Passenger's
/// extension (<see cref="Make"/>, with what its fix's title shows of it); and the settings or groups that would define the name
/// it names (<see cref="Defines"/>).</summary>
internal sealed record GodotOffer
{
    [JsonPropertyName("near")]
    public IReadOnlyList<string> Near { get; init; } = [];

    [JsonPropertyName("make")]
    public GodotFixRequest? Make { get; init; }

    [JsonPropertyName("shown")]
    public string? Shown { get; init; }

    [JsonPropertyName("defines")]
    public IReadOnlyList<GodotDefine> Defines { get; init; } = [];

    /// <summary>Whether it offers anything.</summary>
    [JsonIgnore]
    public bool Any => Near.Count > 0 || Make is not null || Defines.Count > 0;

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this);

    /// <summary>An offer read back from its mark's data; null for data of none.</summary>
    public static GodotOffer? Of(JsonElement? data)
    {
        if (data is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }
        try
        {
            return json.Deserialize<GodotOffer>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>A name of a kind defined where Godot looks for it: an input action or autoload as a setting of project.godot's
/// <see cref="Section"/> (an autoload's file's res://, <see cref="Value"/>), a group given to a node of a scene (by its path in
/// the scene, <see cref="NodePath"/>, and its name, <see cref="NodeName"/>); <see cref="File"/> the res:// file written.</summary>
internal sealed record GodotDefine(
    [property: JsonPropertyName("kind")] GodotStringKind Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("file")] string File)
{
    [JsonPropertyName("section")]
    public string? Section { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("nodePath")]
    public string? NodePath { get; init; }

    [JsonPropertyName("nodeName")]
    public string? NodeName { get; init; }
}
