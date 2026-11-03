using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Workspace;

/// <summary>What a global.json asks of the .NET SDK, as dotnet reads it to choose the SDK a folder's projects build with:
/// the file, its sdk section's version and rollForward, and the line (from zero) its version is on, where a person changes
/// what it asks for.</summary>
public sealed record GlobalJsonSdk(string Path, string? Version, string? RollForward, int Line)
{
    /// <summary>What the global.json at a path asks, read from the disk; null for no path, or a file that does not read.</summary>
    public static GlobalJsonSdk? Read(string? path)
    {
        if (path is null)
        {
            return null;
        }
        try
        {
            return Of(path, File.ReadAllText(path));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What a global.json's text asks (comments and trailing commas allowed, as dotnet allows them); null for one
    /// that is no JSON object.</summary>
    public static GlobalJsonSdk? Of(string path, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            using JsonDocument read = JsonDocument.Parse(bytes, LenientJson.Document);
            if (read.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            JsonElement? sdk = JsonMembers.Object(read.RootElement, "sdk");
            return new GlobalJsonSdk(path, JsonMembers.Text(sdk, "version"), JsonMembers.Text(sdk, "rollForward"), VersionLine(bytes));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The line the sdk section's version is on, else the section's own, else the first.</summary>
    private static int VersionLine(byte[] bytes)
    {
        Utf8JsonReader reader = new(bytes, LenientJson.Reader);
        long found = 0;
        bool inSdk = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }
            if (reader.CurrentDepth == 1)
            {
                inSdk = reader.ValueTextEquals("sdk");
                found = inSdk ? reader.TokenStartIndex : found;
            }
            else if (inSdk && reader.CurrentDepth == 2 && reader.ValueTextEquals("version"))
            {
                found = reader.TokenStartIndex;
                break;
            }
        }
        return bytes.AsSpan(0, (int)found).Count((byte)'\n');
    }
}
