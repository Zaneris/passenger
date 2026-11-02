using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App;

/// <summary>What a person's launch names to open, handed to a running Passenger (<see cref="LaunchHandOff"/>): a folder,
/// the workspace over it, files in it and the caret in the last, every path whole and as the disk spells it. One line of
/// JSON each way, <c>{"launch": {...}}</c> and <c>{"taken": FOLDER}</c> or <c>{"refused": WHY}</c>, written and read here.</summary>
public sealed record HandedLaunch(string? Folder, string? Workspace, IReadOnlyList<string> Files, TextPosition? Caret)
{
    /// <summary>The line asking a running Passenger to take a launch, its newline left to the writer; a caret counted from 1.</summary>
    public string Message()
    {
        JsonObject asked = new()
        {
            ["folder"] = Folder,
            ["workspace"] = Workspace,
            ["files"] = new JsonArray([.. Files.Select(f => (JsonNode?)JsonValue.Create(f))]),
        };
        if (Caret is { } caret)
        {
            asked["caret"] = new JsonObject { ["line"] = caret.Line + 1, ["column"] = caret.Column + 1 };
        }
        return new JsonObject { ["launch"] = asked }.ToJsonString();
    }

    /// <summary>The launch a message's <c>launch</c> member asks for, or null for a message that asks none; a
    /// <see cref="FormatException"/> saying why for one malformed: a path that is not whole, a file that is no path, a caret not
    /// counted from 1 or with no file to go in.</summary>
    public static HandedLaunch? Read(JsonElement message)
    {
        if (JsonMembers.Object(message, "launch") is not { } asked)
        {
            return null;
        }
        string? folder = Whole(JsonMembers.Text(asked, "folder"), "folder");
        string? workspace = Whole(JsonMembers.Text(asked, "workspace"), "workspace");
        if (JsonMembers.Items(asked, "files").Any(f => f.ValueKind != JsonValueKind.String))
        {
            throw new FormatException("a launch's files are paths");
        }
        List<string> files = [.. JsonMembers.Texts(asked, "files").Select(f => Whole(f, "file")!)];
        TextPosition? caret = null;
        if (JsonMembers.Object(asked, "caret") is { } place)
        {
            if (JsonMembers.Number(place, "line") is not { } line || JsonMembers.Number(place, "column") is not { } column || line < 1 || column < 1)
            {
                throw new FormatException("a launch's caret is its line and column, each counted from 1");
            }
            if (files.Count == 0)
            {
                throw new FormatException("a launch's caret needs a file to go in");
            }
            caret = new TextPosition(line - 1, column - 1);
        }
        return new HandedLaunch(folder, workspace, files, caret);

        static string? Whole(string? path, string what) => path is null || Path.IsPathFullyQualified(path) ? path
            : throw new FormatException($"a launch's {what} is a whole path, not '{path}'");
    }

    /// <summary>The line a running Passenger answers a launch with: the folder of the window that took it (empty for one
    /// with none), or why it would not.</summary>
    public static string Answer(string? taken, string? refused) =>
        (refused is null ? new JsonObject { ["taken"] = taken ?? string.Empty } : new JsonObject { ["refused"] = refused }).ToJsonString();

}
