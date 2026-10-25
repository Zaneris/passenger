using System.Text.Json.Nodes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Roslyn.Extension;
using Passenger.Text;
using Passenger.Workspace;
using Passenger.Xaml.Features;

namespace Passenger.App.CSharp;

/// <summary>A settings file's configuration, known from what the app reads (the extension's one reading of a project's
/// keys, through <see cref="Handlers.ConnectionReads"/>): completion of a section's keys and of a value's choices in
/// appsettings*.json and the project's secrets.json, merged ahead of the JSON server's items; the hover card's type of a
/// key; and Go to Declaration from a key there to the options property or the code that reads it.</summary>
internal sealed class SettingsAssist(AssistContext context, CodeCompletion completion, CodeNavigation navigation) : LanguageAssist
{
    /// <summary>The projects a settings file or a secrets.json configures (the session's <see cref="ProjectSession.ConfiguredBy"/>).</summary>
    public Func<string, IReadOnlyList<ConfigurationProject>>? ConfiguredBy { get; set; }

    /// <summary>The settings file shown, a JSON file that configures some of the folder's projects; null for any other.</summary>
    private (WebDocument Json, OpenDocument Document, IReadOnlyList<ConfigurationProject> Projects)? Shown =>
        context.WebCurrent is { Kind: WebServerKind.Json } json && context.Editors.Active is { } settings && ConfiguredBy?.Invoke(settings.Path) is { Count: > 0 } projects
            ? (json, settings, projects) : null;

    /// <summary>The keys and values the code reads, ahead of the JSON server's items; alone where no JSON server runs.</summary>
    public override async Task<bool> CompleteAsync(string? trigger, bool incomplete)
    {
        if (Shown is not ({ } web, _, _))
        {
            return false;
        }
        if (web.Lsp is not { } lsp)
        {
            await StartSettingsCompletion(web);
            return true;
        }
        await completion.ServerListAsync(lsp, trigger, incomplete, web,
            async (text, caret, cancel) => await SettingsCompletionAsync(web, text, caret, cancel) is { } own ? new CodeCompletion.OwnItems(own.Start, own.Items, own.Tail) : null);
        return true;
    }

    /// <summary>The JSON server's card, then the type the code reads the key as and what declares it.</summary>
    public override async Task<bool> HoverAsync(InfoCard card, int offset, CancellationToken cancel)
    {
        if (Shown is not ({ } web, { } document, { } projects))
        {
            return false;
        }
        if (web.Lsp is { } lsp)
        {
            await QuickInfo.ServerHoverAsync(lsp, card, offset, cancel);
        }
        if (await SettingsHoverAsync(document, projects, offset, cancel) is { } configured)
        {
            cancel.ThrowIfCancellationRequested();
            card.AddText(configured);
        }
        return true;
    }

    public override async Task<bool> DefinitionAsync(int offset) =>
        Shown is (_, { } document, { } projects) && await SettingsGoTo(document, projects, offset);

    /// <summary>What the hover card says of a key in a settings file: the type the code reads it as and what declares it.</summary>
    private async Task<string?> SettingsHoverAsync(OpenDocument document, IReadOnlyList<ConfigurationProject> projects, int offset, CancellationToken cancel)
    {
        if (SettingsKeyAt(document.Text.Snapshot.ToString(), offset) is not { } key)
        {
            return null;
        }
        string[] segments = key.Split(':');
        ConfigurationKey? read = (await KeysOfAsync(context.CSharp, projects, cancel)).Where(k => k.Kind == ConfigurationKinds.Option ? ConfigurationFiles.Matches(k.Key, segments)
            : k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).OrderBy(k => k.Kind == ConfigurationKinds.Option ? 0 : 1).FirstOrDefault();
        return read is null ? null : $"{key}: {read.TypeName}" + (read.Owner is { } owner ? $" ({owner})" : read.Kind == ConfigurationKinds.Read ? " (read by the code)" : string.Empty)
            + (read.Values is { Count: > 0 } names ? $"\nOne of {string.Join(", ", names)}" : string.Empty);
    }

    /// <summary>The kind a configuration key is listed as: a property's.</summary>
    private const CompletionItemKind KeyItemKind = CompletionItemKind.Property;

    /// <summary>Every configuration key the projects' code reads or binds, asked of Passenger's extension for the code as the server
    /// holds it; none without the server.</summary>
    internal static async Task<IReadOnlyList<ConfigurationKey>> KeysOfAsync(CSharpService? csharp, IReadOnlyList<ConfigurationProject> projects, CancellationToken cancel = default)
    {
        if (csharp?.Session is not { } roslyn || projects.Count == 0)
        {
            return [];
        }
        try
        {
            return (await roslyn.ExtensionAsync<ConnectionReadsFound>(Handlers.ConnectionReads,
                new ConnectionReadsRequest([.. projects.Select(p => p.Project)], Keys: true), cancel)).Keys ?? [];
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
    }

    /// <summary>What completion offers in a settings file or a project's secrets.json where a key or a value is typed: the
    /// section's keys the code reads or binds (an options class's properties with their types), those the object has already
    /// left out, or a bool's and an enum's values; with where they go and the text after the caret they replace (a name's
    /// closing quote). Null in any other file, or where the code reads nothing there.</summary>
    private async Task<(int Start, IReadOnlyList<CompletionItem> Items, string Tail)?> SettingsCompletionAsync(WebDocument web, TextSnapshot text, int caret,
        CancellationToken cancel)
    {
        if (web.Kind != WebServerKind.Json || context.Editors.Active is not { } document || ConfiguredBy?.Invoke(document.Path) is not { Count: > 0 } projects)
        {
            return null;
        }
        string written = text.ToString();
        if (JsonSpot.At(written, caret) is not { } spot)
        {
            return null;
        }
        IReadOnlyList<ConfigurationKey> keys = await KeysOfAsync(context.CSharp, projects, cancel);
        string[] path = [.. spot.Path.SelectMany(name => name.Split(':'))];
        IReadOnlyList<CompletionItem> items = spot.AtName ? KeyItems(keys, path, spot, written) : ValueItems(keys, [.. path, .. (spot.Property ?? string.Empty).Split(':')]);
        return items.Count == 0 ? null : (spot.Start, items, caret < spot.End ? written[caret..spot.End] : string.Empty);
    }

    /// <summary>A settings file's keys and values listed with no JSON server to merge them with (none runs without Node.js).</summary>
    private async Task StartSettingsCompletion(WebDocument web)
    {
        CancellationTokenSource cancel = completion.Asking();
        int caret = context.Caret;
        TextSnapshot asked = context.Editor.Document.Snapshot;
        if (await SettingsCompletionAsync(web, asked, caret, cancel.Token) is not { } own || cancel.IsCancellationRequested || context.Editor.Document.Snapshot != asked)
        {
            return;
        }
        completion.Open(new CodeCompletion.Completion(own.Items, own.Start, [], false)
        {
            Settings = true,
            Tail = own.Tail,
            Lead = own.Start < caret ? asked.GetText(own.Start, caret - own.Start) : string.Empty,
        });
    }

    /// <summary>The keys the code reads or binds one level under a section, each once: its type and what declares it where the
    /// key holds a value, else a section; a name the object has already left out.</summary>
    private static IReadOnlyList<CompletionItem> KeyItems(IReadOnlyList<ConfigurationKey> keys, string[] path, JsonSpot spot, string written)
    {
        Dictionary<string, ConfigurationKey?> offered = new(StringComparer.OrdinalIgnoreCase);
        foreach (ConfigurationKey key in keys)
        {
            string[] segments = key.Key.Split(':');
            if (key.Key.Length == 0 || segments.Length <= path.Length || segments[path.Length] == "*"
                || !segments.Take(path.Length).Zip(path).All(p => p.First == "*" || p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            string name = segments[path.Length];
            bool leaf = segments.Length == path.Length + 1;
            // The options property's own words over a read's or a section's.
            if (!offered.TryGetValue(name, out ConfigurationKey? known) || leaf && (known is null || known.Kind != ConfigurationKinds.Option && key.Kind == ConfigurationKinds.Option))
            {
                offered[name] = leaf ? key : known;
            }
        }
        string typed = spot.Typed(written, Math.Min(spot.End, written.Length));
        int after = spot.End;
        while (after < written.Length && char.IsWhiteSpace(written[after]))
        {
            after++;
        }
        bool named = after < written.Length && written[after] == ':';
        bool more = after < written.Length && written[after] == '"';
        return [.. offered.Where(o => !spot.Names.Any(n => n.Equals(o.Key, StringComparison.OrdinalIgnoreCase) && !n.Equals(typed, StringComparison.Ordinal)))
            .OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase).Select(o => new CompletionItem
            {
                Label = o.Key,
                Kind = KeyItemKind,
                Detail = o.Value is { } key ? key.Owner is { } owner ? $"{key.TypeName} — {owner}" : $"{key.TypeName} — read by the code" : "section",
                InsertText = named ? Quoted(o.Key) : $"{Quoted(o.Key)}: {Placeholder(o.Value)}{(more ? "," : string.Empty)}",
                InsertTextFormat = InsertTextFormat.Snippet,
                SortText = " " + o.Key,
            })];

        static string Quoted(string name) => System.Text.Json.JsonSerializer.Serialize(name, JsonText.Written).Replace("$", "\\$", StringComparison.Ordinal);

        static string Placeholder(ConfigurationKey? key) => key?.Type switch
        {
            ConfigurationTypes.Boolean => "${1:false}",
            ConfigurationTypes.Number => "${1:0}",
            ConfigurationTypes.Enum when key.Values is [{ } first, ..] => $"\"${{1:{first}}}\"",
            ConfigurationTypes.Array => "[$1]",
            ConfigurationTypes.Object or ConfigurationTypes.Dictionary or null => "{$1}",
            _ => "\"$1\"",
        };
    }

    /// <summary>A key's values where its type has few: a bool's true and false, an enum's names.</summary>
    private static IReadOnlyList<CompletionItem> ValueItems(IReadOnlyList<ConfigurationKey> keys, string[] key)
    {
        ConfigurationKey? typed = keys.Where(k => k.Type is ConfigurationTypes.Boolean or ConfigurationTypes.Enum && ConfigurationFiles.Matches(k.Key, key))
            .OrderBy(k => k.Kind == ConfigurationKinds.Option ? 0 : 1).FirstOrDefault();
        return typed switch
        {
            { Type: ConfigurationTypes.Boolean } => [.. new[] { "true", "false" }.Select(v => new CompletionItem { Label = v, Kind = CompletionItemKind.Value, Detail = typed.TypeName, InsertText = v, SortText = " " + v })],
            { Type: ConfigurationTypes.Enum, Values: { } names } => [.. names.Select(v => new CompletionItem
            {
                Label = v,
                Kind = CompletionItemKind.EnumMember,
                Detail = typed.TypeName,
                InsertText = System.Text.Json.JsonSerializer.Serialize(v, JsonText.Written),
                SortText = " " + v,
            })],
            _ => [],
        };
    }

    /// <summary>Go to Declaration from a key in a settings file or a secrets.json: the options property it sets, else the code
    /// that reads it or binds its section; false where the caret is on no key, for the JSON server's.</summary>
    private async Task<bool> SettingsGoTo(OpenDocument document, IReadOnlyList<ConfigurationProject> projects, int offset)
    {
        if (await SettingsDeclaredAsync(document, projects, offset) is not (string key, IReadOnlyList<ConfigurationKey> declared))
        {
            return false;
        }
        if (context.Editors.Active != document)
        {
            return true;
        }
        string names = string.Join(" or ", projects.Select(p => p.Name));
        navigation.Go([.. declared.Select(k => new LspLocation(DocumentUris.FromPath(k.Path!), new LspRange(new LspPosition(k.At!.Line, k.At.Character), new LspPosition(k.End!.Line, k.End.Character))))],
            offset, "Declarations", $"No code of {names} reads {key}", [.. declared.Select(k => (string?)(k.Owner ?? $"{k.Key} ({k.Kind})"))]);
        return true;
    }

    /// <summary>The key at an offset of a settings file and what declares it in the projects' code: the options properties it
    /// sets first, then the bindings of its section, then the reads; null where the offset is on no key.</summary>
    private async Task<(string Key, IReadOnlyList<ConfigurationKey> Declared)?> SettingsDeclaredAsync(OpenDocument document, IReadOnlyList<ConfigurationProject> projects, int offset)
    {
        if (SettingsKeyAt(document.Text.Snapshot.ToString(), offset) is not { } key)
        {
            return null;
        }
        IReadOnlyList<ConfigurationKey> found = await KeysOfAsync(context.CSharp, projects);
        string[] segments = key.Split(':');
        return (key, [.. found.Where(k => k.Path is not null && k.At is not null && k.End is not null && k.Key.Length > 0
                && (k.Kind == ConfigurationKinds.Option ? ConfigurationFiles.Matches(k.Key, segments) : k.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(k => k.Kind switch { ConfigurationKinds.Option => 0, ConfigurationKinds.Binding => 1, _ => 2 })
            .DistinctBy(k => (k.Path, k.At!.Line, k.At.Character))]);
    }

    /// <summary>Where Go to Declaration goes from a key of a settings file or a secrets.json, at the editor's lines; null for
    /// any other file or where no key is. Shared with the agents' definition.</summary>
    internal async Task<IReadOnlyList<(string Path, TextPosition Position)>?> SettingsDeclarationsAsync(OpenDocument document, int offset)
    {
        if (ConfiguredBy?.Invoke(document.Path) is not { Count: > 0 } projects
            || await SettingsDeclaredAsync(document, projects, offset) is not (_, IReadOnlyList<ConfigurationKey> declared))
        {
            return null;
        }
        Func<string, TextSnapshot?> texts = ServerPlaces.Texts(context.Editors);
        return [.. declared.Select(k => (k.Path!, ServerPlaces.PositionOf(texts(k.Path!), new LspPosition(k.At!.Line, k.At.Character))))];
    }

    /// <summary>The key whose name, or whose value, an offset of a configuration file is on, its sections joined by ':'.</summary>
    internal static string? SettingsKeyAt(string text, int offset)
    {
        if (JsonSpot.At(text, offset) is not { } spot)
        {
            return null;
        }
        string? name = spot.AtName
            ? spot.End > spot.Start && text[spot.Start] == '"' ? Unquoted(text[spot.Start..spot.End]) : null
            : spot.Property;
        return name is { Length: > 0 } ? string.Join(':', [.. spot.Path, name]) : null;

        static string Unquoted(string written)
        {
            try
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(written.EndsWith('"') && written.Length > 1 ? written : written + "\"") ?? string.Empty;
            }
            catch (System.Text.Json.JsonException)
            {
                return written.Trim('"');
            }
        }
    }
}
