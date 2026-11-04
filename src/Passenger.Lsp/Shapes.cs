using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.Lsp;

/// <summary>Reads the protocol's either-or fields: a result that may be a list or an object, a
/// location or a link, a command or a code action, a string or marked-up content.</summary>
public static class Shapes
{
    public static CompletionList? CompletionList(JsonElement result) => result.ValueKind switch
    {
        JsonValueKind.Array => new CompletionList { Items = result.Deserialize<List<CompletionItem>>(Json.Options) ?? [] },
        JsonValueKind.Object => result.Deserialize<CompletionList>(Json.Options),
        _ => null,
    };

    /// <summary>A definition's answer as plain locations: one location, several, or links (whose
    /// target selection is the name itself).</summary>
    public static IReadOnlyList<LspLocation> Locations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object)
        {
            return [Location(result)];
        }
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return result.EnumerateArray().Select(Location).ToList();
    }

    /// <summary>Inlay hints, each label as one string whether the server sent it whole or in parts.</summary>
    public static IReadOnlyList<LspInlayHint> InlayHints(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<LspInlayHint> hints = [];
        foreach (JsonElement item in result.EnumerateArray())
        {
            if (JsonMembers.Value(item, "position") is not { } position || JsonMembers.Value(item, "label") is not { } label)
            {
                continue;
            }
            string text = label.ValueKind switch
            {
                JsonValueKind.Array => string.Concat(label.EnumerateArray().Select(part => JsonMembers.Text(part, "value"))),
                JsonValueKind.String => label.GetString()!,
                _ => string.Empty,
            };
            hints.Add(new LspInlayHint(
                position.Deserialize<LspPosition>(Json.Options)!,
                text,
                JsonMembers.Number(item, "kind") is { } kind ? (InlayHintKind)kind : null,
                JsonMembers.Bool(item, "paddingLeft") == true,
                JsonMembers.Bool(item, "paddingRight") == true,
                JsonMembers.Value(item, "data")?.Clone()));
        }
        return hints;
    }

    public static IReadOnlyList<CodeAction> CodeActions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<CodeAction> actions = [];
        foreach (JsonElement item in result.EnumerateArray())
        {
            // A bare command has a string "command"; a code action's "command" (if any) is an object.
            if (JsonMembers.Text(item, "command") is not null)
            {
                LspCommand bare = item.Deserialize<LspCommand>(Json.Options)!;
                actions.Add(new CodeAction { Title = bare.Title, Command = bare });
            }
            else if (item.Deserialize<CodeAction>(Json.Options) is { } action)
            {
                actions.Add(action);
            }
        }
        return actions;
    }

    /// <summary>Text to show from a string, marked-up content, or a list of marked strings.</summary>
    public static string Text(JsonElement? content)
    {
        if (content is not { } value)
        {
            return string.Empty;
        }
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Object:
                return JsonMembers.Text(value, "value") ?? string.Empty;
            case JsonValueKind.Array:
                StringBuilder joined = new();
                foreach (JsonElement part in value.EnumerateArray())
                {
                    if (joined.Length > 0)
                    {
                        joined.Append("\n\n");
                    }
                    joined.Append(Text(part));
                }
                return joined.ToString();
            default:
                return string.Empty;
        }
    }

    /// <summary>A workspace edit as the edits it makes to each file, whichever of its two forms it
    /// came in. File creations, renames and deletions are reported apart.</summary>
    public static IReadOnlyList<FileEdits> WorkspaceEdit(JsonElement edit)
    {
        List<FileEdits> files = [];
        if (JsonMembers.Value(edit, "documentChanges") is { ValueKind: JsonValueKind.Array } changes)
        {
            foreach (JsonElement change in changes.EnumerateArray())
            {
                if (JsonMembers.Value(change, "textDocument") is { } document && JsonMembers.Value(change, "edits") is { } edits)
                {
                    int? version = JsonMembers.Number(document, "version");
                    files.Add(new FileEdits(Needed(document, "uri"), edits.Deserialize<List<LspTextEdit>>(Json.Options) ?? [], null) { Version = version });
                }
                else if (JsonMembers.Value(change, "kind") is not null)
                {
                    files.Add(new FileEdits(JsonMembers.Text(change, "uri") ?? Needed(change, "oldUri"), [], change.Clone()));
                }
            }
        }
        else
        {
            foreach (JsonProperty file in JsonMembers.Properties(edit, "changes"))
            {
                files.Add(new FileEdits(file.Name, file.Value.Deserialize<List<LspTextEdit>>(Json.Options) ?? [], null));
            }
        }
        return files;
    }

    private static LspLocation Location(JsonElement item)
    {
        if (JsonMembers.Value(item, "targetUri") is not null)
        {
            LspRange range = (JsonMembers.Value(item, "targetSelectionRange") ?? throw new JsonException("A language server's location link has no targetSelectionRange"))
                .Deserialize<LspRange>(Json.Options)!;
            return new LspLocation(Needed(item, "targetUri"), range);
        }
        return item.Deserialize<LspLocation>(Json.Options)!;
    }

    /// <summary>A string a server's answer cannot go without (an edit's or a location's uri): missing or of another kind, the
    /// answer does not read.</summary>
    private static string Needed(JsonElement holder, string name) =>
        JsonMembers.Text(holder, name) ?? throw new JsonException($"A language server's {name} is missing or no string");
}

/// <summary>A workspace edit's change to one file: its edits (or an operation: create, rename, delete), and the version of
/// the document they were made for, when the server says.</summary>
public sealed record FileEdits(string Uri, IReadOnlyList<LspTextEdit> Edits, JsonElement? Operation)
{
    public int? Version { get; init; }
}
