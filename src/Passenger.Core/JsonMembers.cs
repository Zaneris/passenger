using System.Text.Json;

namespace Passenger.Core;

/// <summary>A JSON object's member read as the kind it should be: what another program or a person wrote (an engine's
/// answer, a launch profile, a server's message) may hold anything anywhere, so each read takes the element of any kind,
/// or none, and refuses a member of another kind (null, or nothing for a list) where System.Text.Json's own reads throw.
/// A name is matched as written, or in any letter case where <c>anyCase</c> says so.</summary>
public static class JsonMembers
{
    /// <summary>A member of whatever kind it is, for a reader that takes each kind itself.</summary>
    public static JsonElement? Value(JsonElement? element, string name, bool anyCase = false) => Member(element, name, anyCase);

    /// <summary>A string member's value.</summary>
    public static string? Text(JsonElement? element, string name, bool anyCase = false) =>
        Member(element, name, anyCase) is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <summary>A string member's value, or a number's, <c>true</c>'s or <c>false</c>'s as JSON writes it ("8080",
    /// "true"), for a program that writes one kind or the other.</summary>
    public static string? Scalar(JsonElement? element, string name, bool anyCase = false) => Member(element, name, anyCase) switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } value => value.GetRawText(),
        _ => null,
    };

    /// <summary>A <c>true</c> or <c>false</c> member's value.</summary>
    public static bool? Bool(JsonElement? element, string name, bool anyCase = false) => Member(element, name, anyCase)?.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    /// <summary>A number member's value where it is a whole number an int holds.</summary>
    public static int? Number(JsonElement? element, string name, bool anyCase = false) =>
        Member(element, name, anyCase) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int value) ? value : null;

    /// <summary>An object member, to read members of in turn.</summary>
    public static JsonElement? Object(JsonElement? element, string name, bool anyCase = false) =>
        Member(element, name, anyCase) is { ValueKind: JsonValueKind.Object } found ? found : null;

    /// <summary>An object member's own members, in their order; none where it is no object.</summary>
    public static IReadOnlyList<JsonProperty> Properties(JsonElement? element, string name, bool anyCase = false) =>
        Object(element, name, anyCase) is { } found ? [.. found.EnumerateObject()] : [];

    /// <summary>An array member's items, in their order; none where it is no array.</summary>
    public static IReadOnlyList<JsonElement> Items(JsonElement? element, string name, bool anyCase = false) =>
        Member(element, name, anyCase) is { ValueKind: JsonValueKind.Array } array ? [.. array.EnumerateArray()] : [];

    /// <summary>An array member's string items, in their order, any other item passed over.</summary>
    public static IReadOnlyList<string> Texts(JsonElement? element, string name, bool anyCase = false) =>
        [.. Items(element, name, anyCase).Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!)];

    /// <summary>The JSON object a program wrote among the other lines of its words, to read members of: from a line holding
    /// its opening brace alone to the next holding its closing one alone, as indented JSON's outer braces stand, or one
    /// line holding it whole. Lines beside it (a warning, the program's other words) are passed over, braces and all; the
    /// first object that reads is taken, null where none does.</summary>
    public static JsonElement? ObjectIn(string words)
    {
        for (int line = 0; line < words.Length; line = NextLine(words, line))
        {
            ReadOnlySpan<char> text = LineAt(words, line);
            int end = text is "{" ? ClosingLine(words, NextLine(words, line)) : text.StartsWith('{') && text.EndsWith('}') ? line + text.Length : -1;
            if (end < 0)
            {
                continue;
            }
            try
            {
                using JsonDocument read = JsonDocument.Parse(words.AsMemory(line, end - line));
                if (read.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return read.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Words shaped like an object that are none: the object may come later.
            }
        }
        return null;
    }

    /// <summary>The end of the first line from <paramref name="line"/> on that holds a closing brace alone; -1 where none does.</summary>
    private static int ClosingLine(string words, int line)
    {
        for (; line < words.Length; line = NextLine(words, line))
        {
            if (LineAt(words, line) is "}")
            {
                return line + 1;
            }
        }
        return -1;
    }

    /// <summary>The line starting at <paramref name="start"/>, without its line break or the spaces after it.</summary>
    private static ReadOnlySpan<char> LineAt(string words, int start)
    {
        int end = words.IndexOf('\n', start);
        return words.AsSpan(start, (end < 0 ? words.Length : end) - start).TrimEnd();
    }

    private static int NextLine(string words, int start) => words.IndexOf('\n', start) is int end and >= 0 ? end + 1 : words.Length;

    private static JsonElement? Member(JsonElement? element, string name, bool anyCase)
    {
        if (element is not { ValueKind: JsonValueKind.Object } holder)
        {
            return null;
        }
        if (!anyCase)
        {
            return holder.TryGetProperty(name, out JsonElement found) ? found : null;
        }
        foreach (JsonProperty property in holder.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }
}
