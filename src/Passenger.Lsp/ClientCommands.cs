using System.Text.Json;

namespace Passenger.Lsp;

/// <summary>The commands Roslyn's server leaves to its client: a completion's code written whole (an override's body, the
/// async an await needs), a code action's nested actions to choose from, and a fix-all's scopes. Roslyn registers no
/// command of its own (workspace/executeCommand), so any other command of its is one Passenger cannot run, and says so;
/// another server's runs on it where it lists it (<see cref="LanguageServer.Runs"/>).</summary>
public static class ClientCommands
{
    public const string CompletionComplexEdit = "roslyn.client.completionComplexEdit";

    public const string NestedCodeAction = "roslyn.client.nestedCodeAction";

    public const string FixAllCodeAction = "roslyn.client.fixAllCodeAction";

    /// <summary>Between the steps of a nested choice's words.</summary>
    public const string Step = " → ";

    /// <summary>A completion's edit made by its command in place of its text, answered for the text as it was resolved
    /// (its arguments: the document, the edit, whether it is a snippet, the caret's offset after it or -1); null for an
    /// item without one.</summary>
    public static ComplexEdit? CompletionEdit(CompletionItem item)
    {
        if (item.Command is not { Command: CompletionComplexEdit, Arguments: [_, JsonElement edit, _, JsonElement caret, ..] }
            || edit.Deserialize<LspTextEdit>(Json.Options) is not { } made)
        {
            return null;
        }
        return new ComplexEdit(made, caret.ValueKind == JsonValueKind.Number && caret.GetInt32() >= 0 ? caret.GetInt32() : null);
    }

    /// <summary>What choosing a code action offers before it changes anything: a fix-all's scopes, or the actions nested
    /// under it (a nested fix-all's scopes each a choice of its own); none for an action that is its own change.</summary>
    public static IReadOnlyList<ActionChoice> Choices(CodeAction action)
    {
        if (Scopes(action) is { Count: > 0 } scopes)
        {
            return [.. scopes.Select(scope => new ActionChoice(Words(scope), action, scope))];
        }
        if (action.Command is not { Command: NestedCodeAction, Arguments: [JsonElement data, ..] }
            || !data.TryGetProperty("NestedCodeActions", out JsonElement nested) || nested.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        List<ActionChoice> choices = [];
        foreach (CodeAction inner in nested.Deserialize<List<CodeAction>>(Json.Options) ?? [])
        {
            string text = Path(inner);
            IReadOnlyList<string> innerScopes = Scopes(inner);
            if (innerScopes.Count == 0)
            {
                choices.Add(new ActionChoice(text, inner, null));
            }
            else
            {
                choices.AddRange(innerScopes.Select(scope => new ActionChoice($"Fix All: {text}{Step}{Words(scope)}", inner, scope)));
            }
        }
        return choices;
    }

    /// <summary>The request for a fix-all's change over a scope, with its data as it came, named as Roslyn names it.</summary>
    public static FixAllCodeActionParams FixAll(CodeAction action, string scope)
    {
        JsonElement data = FixAllData(action) ?? throw new ArgumentException($"'{action.Title}' is not a fix-all", nameof(action));
        string title = data.TryGetProperty("UniqueIdentifier", out JsonElement id) && id.GetString() is { } named ? named : action.Title;
        return new FixAllCodeActionParams(title, data, scope);
    }

    /// <summary>The scopes a fix-all may cover (Document, Project, Solution, ContainingMember...), none for another action.</summary>
    private static IReadOnlyList<string> Scopes(CodeAction action) =>
        FixAllData(action) is { } data && data.TryGetProperty("FixAllFlavors", out JsonElement flavors) && flavors.ValueKind == JsonValueKind.Array
            ? [.. flavors.EnumerateArray().Select(f => f.GetString()).OfType<string>()]
            : [];

    /// <summary>A fix-all's data: its command's argument at the top, its own data when nested, where Roslyn puts it.</summary>
    private static JsonElement? FixAllData(CodeAction action)
    {
        JsonElement? data = action.Command is { Command: FixAllCodeAction, Arguments: [JsonElement argument, ..] } ? argument : action.Data;
        return data is { ValueKind: JsonValueKind.Object } found && found.TryGetProperty("FixAllFlavors", out _) ? found : null;
    }

    /// <summary>A nested action's words: the steps under the one it was chosen from ("Configure IDE0005 severity → Warning").</summary>
    private static string Path(CodeAction inner)
    {
        if (inner.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("CodeActionPath", out JsonElement path)
            && path.ValueKind == JsonValueKind.Array && path.GetArrayLength() > 1)
        {
            return string.Join(Step, path.EnumerateArray().Skip(1).Select(step => step.GetString()));
        }
        return inner.Title;
    }

    /// <summary>A scope in words: "ContainingMember" as "Containing member".</summary>
    private static string Words(string scope) =>
        string.Concat(scope.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
}

/// <summary>A completion's code written whole by the server: its edit, and the caret's offset in the text after it (null
/// to leave the caret after the edit).</summary>
public sealed record ComplexEdit(LspTextEdit Edit, int? Caret);

/// <summary>One of a code action's choices: its words, the action it applies, and the scope for a fix-all's.</summary>
public sealed record ActionChoice(string Text, CodeAction Action, string? Scope);
