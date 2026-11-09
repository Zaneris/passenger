using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace.GDScript;

/// <summary>Godot's class reference as the Godot found carries it (its <c>--dump-extension-api-with-docs</c>): each engine
/// class's and built-in type's brief and description, and each of its methods', properties', signals', constants' (an
/// enum's values among them) and enums' descriptions, the utility functions and global enums under
/// <see cref="GlobalScope"/>, in Godot's BBCode (<see cref="GodotDocText"/>). Kept as the bytes of its own compact file
/// (<see cref="Reduce"/>), each text decoded only as a card asks for it. Pure.</summary>
public sealed class GodotClassReference
{
    /// <summary>What the API calls the owner of its utility functions and global enums.</summary>
    public const string GlobalScope = "@GlobalScope";

    private readonly byte[] bytes;
    private readonly Dictionary<string, (int Start, int Length)> places;

    private GodotClassReference(byte[] bytes, Dictionary<string, (int Start, int Length)> places)
    {
        this.bytes = bytes;
        this.places = places;
    }

    /// <summary>A member's description: by its owner (<c>Node</c>, <c>Vector2</c>, <see cref="GlobalScope"/>), what it is
    /// (<c>func</c>, <c>var</c>, <c>signal</c>, <c>const</c>, <c>enum</c>) and its name; null where the reference has none.</summary>
    public string? Of(string owner, string kind, string name) => Text($"{owner}:{kind} {name}");

    /// <summary>A class's brief description; null for none.</summary>
    public string? Brief(string owner) => Text($"{owner}:brief");

    /// <summary>A class's description; null for none.</summary>
    public string? Description(string owner) => Text(owner);

    private string? Text(string key)
    {
        if (!places.TryGetValue(key, out (int Start, int Length) at))
        {
            return null;
        }
        Utf8JsonReader reader = new(bytes.AsSpan(at.Start, at.Length));
        return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
    }

    /// <summary>The compact file of a dump's descriptions: one object of each text by its key, the empty ones left out.</summary>
    public static byte[] Reduce(ReadOnlySpan<byte> dump)
    {
        Utf8JsonReader reader = new(dump, LenientJson.Reader);
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        JsonElement root = document.RootElement;
        using MemoryStream kept = new();
        using (Utf8JsonWriter writer = new(kept))
        {
            writer.WriteStartObject();
            void Write(string key, JsonElement owner)
            {
                if (JsonMembers.Text(owner, "description") is { Length: > 0 } text)
                {
                    writer.WriteString(key, text);
                }
            }
            foreach (JsonElement type in JsonMembers.Items(root, "classes").Concat(JsonMembers.Items(root, "builtin_classes")))
            {
                string name = JsonMembers.Text(type, "name") ?? string.Empty;
                Write(name, type);
                if (JsonMembers.Text(type, "brief_description") is { Length: > 0 } brief)
                {
                    writer.WriteString($"{name}:brief", brief);
                }
                foreach ((string list, string kind) in new[] { ("methods", "func"), ("properties", "var"), ("members", "var"), ("signals", "signal"), ("constants", "const"), ("enums", "enum") })
                {
                    foreach (JsonElement member in JsonMembers.Items(type, list))
                    {
                        Write($"{name}:{kind} {JsonMembers.Text(member, "name")}", member);
                        foreach (JsonElement value in kind == "enum" ? JsonMembers.Items(member, "values") : [])
                        {
                            Write($"{name}:const {JsonMembers.Text(value, "name")}", value);
                        }
                    }
                }
            }
            foreach (JsonElement function in JsonMembers.Items(root, "utility_functions"))
            {
                Write($"{GlobalScope}:func {JsonMembers.Text(function, "name")}", function);
            }
            foreach (JsonElement global in JsonMembers.Items(root, "global_enums"))
            {
                Write($"{GlobalScope}:enum {JsonMembers.Text(global, "name")}", global);
                foreach (JsonElement value in JsonMembers.Items(global, "values"))
                {
                    Write($"{GlobalScope}:const {JsonMembers.Text(value, "name")}", value);
                }
            }
            writer.WriteEndObject();
        }
        return kept.ToArray();
    }

    /// <summary>The reference a compact file (<see cref="Reduce"/>) holds, each text's place in it found, none decoded.</summary>
    public static GodotClassReference Read(byte[] compact)
    {
        Dictionary<string, (int Start, int Length)> places = new(StringComparer.Ordinal);
        Utf8JsonReader reader = new(compact);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Godot's class reference kept is no object");
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            string key = reader.GetString() ?? string.Empty;
            if (!reader.Read() || reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException($"Godot's class reference kept holds no text for {key}");
            }
            places[key] = ((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2);
        }
        return new GodotClassReference(compact, places);
    }
}
