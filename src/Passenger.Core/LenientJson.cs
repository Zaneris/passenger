using System.Text.Json;

namespace Passenger.Core;

/// <summary>JSON a person may write by hand (launchSettings.json, appsettings.json, a solution filter, a tool manifest,
/// Passenger's settings) read as .NET's own readers of such files take it: comments passed over and trailing commas
/// allowed.</summary>
public static class LenientJson
{
    /// <summary>For a <see cref="JsonDocument"/> or a <c>JsonNode</c>.</summary>
    public static readonly JsonDocumentOptions Document = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>For a <see cref="Utf8JsonReader"/>.</summary>
    public static readonly JsonReaderOptions Reader = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
}
