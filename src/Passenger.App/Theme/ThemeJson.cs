using System.Text.Json;
using Passenger.Core;

namespace Passenger.App;

/// <summary>A theme file's JSON read one way, Passenger's format (<see cref="ThemeFiles"/>) or VS Code's
/// (<see cref="VsCodeThemes"/>): held to <see cref="ThemeFiles.MostBytes"/>, a byte order mark passed over, read with its
/// reader's options, its root an object, then what the reader makes of it; else why it is refused whole, naming the file.
/// Pure.</summary>
public static class ThemeJson
{
    /// <summary>A file read: <paramref name="readAs"/> names the reader in what a refusal says ("format 1"), and
    /// <paramref name="shape"/> says what a file of it is, where it is no object; <paramref name="read"/> may refuse the file
    /// by throwing a <see cref="FormatException"/> whose words follow its name.</summary>
    public static ThemeRead<T> Read<T>(string file, ReadOnlyMemory<byte> bytes, JsonDocumentOptions options, string readAs, string? shape,
        Func<JsonElement, List<string>, T> read) where T : class
    {
        if (bytes.Length > ThemeFiles.MostBytes)
        {
            return new ThemeRead<T>(null, $"{file} is {Sizes.Said(bytes.Length)}, more than the {Sizes.Said(ThemeFiles.MostBytes)} a theme file may hold", []);
        }
        List<string> problems = [];
        try
        {
            // A byte order mark, as Windows' editors may save one, is no JSON of its own.
            ReadOnlyMemory<byte> text = bytes.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? bytes[3..] : bytes;
            using JsonDocument document = JsonDocument.Parse(text, options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException(shape is null ? "is no JSON object" : $"is no JSON object: {shape}");
            }
            return new ThemeRead<T>(read(document.RootElement, problems), null, problems);
        }
        catch (JsonException e)
        {
            return new ThemeRead<T>(null, $"{file} is not JSON as {readAs} reads it: {e.Message}", []);
        }
        catch (FormatException e)
        {
            return new ThemeRead<T>(null, $"{file} {e.Message}", []);
        }
    }

    /// <summary>A value as the file writes it, cut short past 40 characters.</summary>
    public static string Shown(JsonElement value)
    {
        string raw = value.GetRawText();
        return raw.Length <= 40 ? raw : string.Concat(raw.AsSpan(0, 39), "…");
    }
}
