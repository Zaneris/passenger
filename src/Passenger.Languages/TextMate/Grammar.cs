using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Passenger.Editor;
using Tint;
using Tint.Loading;

namespace Passenger.Languages.TextMate;

/// <summary>A language's TextMate grammar (VS Code's, or Razor's and C#'s from dotnet/roslyn, carried in this assembly
/// under <c>TextMate/Grammars</c>) with the grammars it embeds, in a registry of its own. Documents of this scope
/// share a tokenizer, whose mutable scratch buffers and scanners are protected by <see cref="Gate"/>, and one for long
/// lines behind <see cref="LongGate"/>. Different scopes use separate registries and tokenizers.</summary>
internal sealed class Grammar
{
    private static readonly Dictionary<string, Lazy<Grammar>> Loaded = [];

    /// <summary>The theme every registry shares: each rule's foreground is the kind it colours, as #0000NN.</summary>
    private static readonly string Theme = ThemeText();

    private readonly Lazy<(GrammarRegistry Registry, Tint.Theme Theme)> compiled;
    private readonly Lazy<Tokenizer> rules;
    private readonly Lazy<Tokenizer> longRules;
    private TokenKind[] kinds = [];

    private Grammar(string scope, Tags tags)
    {
        Tags = tags;
        compiled = new(() => Load(scope), LazyThreadSafetyMode.ExecutionAndPublication);
        rules = new(() => compiled.Value.Registry.CreateTokenizer(scope, compiled.Value.Theme), LazyThreadSafetyMode.ExecutionAndPublication);
        longRules = new(() => compiled.Value.Registry.CreateTokenizer(scope, compiled.Value.Theme), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>A language's grammar, made once and shared by every file of every language it colours.</summary>
    public static Grammar For(GrammarFile file)
    {
        lock (Loaded)
        {
            if (!Loaded.TryGetValue(file.Scope, out Lazy<Grammar>? grammar))
            {
                Loaded[file.Scope] = grammar = new Lazy<Grammar>(() => new Grammar(file.Scope, file.Tags));
            }
            return grammar.Value;
        }
    }

    /// <summary>Held around everything done with <see cref="Rules"/>.</summary>
    public object Gate { get; } = new();

    public Tags Tags { get; }

    /// <summary>The compiled rules, loaded on first use (off the UI thread: a grammar is read and its includes with it).</summary>
    public Tokenizer Rules => rules.Value;

    /// <summary>Held around everything done with <see cref="LongRules"/>.</summary>
    public object LongGate { get; } = new();

    /// <summary>The same rules in a tokenizer of their own, for lines too long to colour, so reading one holds no other
    /// file's lines behind <see cref="Gate"/>.</summary>
    public Tokenizer LongRules => longRules.Value;

    /// <summary>Whether <see cref="LongRules"/> has read a line's start, its first patterns parsed; set under <see cref="LongGate"/>.</summary>
    public bool LongRulesWarm { get; set; }

    /// <summary>The kind a token's foreground colour id stands for.</summary>
    public TokenKind KindOf(int foreground) => foreground < kinds.Length ? kinds[foreground] : TokenKind.Text;

    private (GrammarRegistry Registry, Tint.Theme Theme) Load(string scope)
    {
        GrammarRegistry registry = new();
        HashSet<string> loaded = new(StringComparer.Ordinal);
        Add(scope);
        using MemoryStream themeStream = new(Encoding.UTF8.GetBytes(Theme));
        Tint.Theme theme = Tint.Theme.FromSource(ThemeLoader.Load(themeStream));
        kinds = new TokenKind[theme.ColorMap.Count];
        for (int id = 1; id < kinds.Length; id++)
        {
            string colour = theme.ColorMap.GetColor(id);
            int value = int.Parse(colour.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            kinds[id] = Enum.IsDefined((TokenKind)value) ? (TokenKind)value : TokenKind.Text;
        }
        return (registry, theme);

        void Add(string name)
        {
            if (!loaded.Add(name) || Language.GrammarFileOf(name) is not { } file)
            {
                return;
            }
            using Stream resource = Resource(file);
            JsonNode node = JsonNode.Parse(resource) ?? throw new InvalidOperationException($"Empty grammar {file}");
            Tidy(node);
            // A grammar embedded under another scope than its own (Markdown's jsonc blocks read by JSON's) is registered as that scope.
            node["scopeName"] = name;
            if (name.StartsWith("source.yaml", StringComparison.Ordinal))
            {
                KeysAreKeys(node);
            }
            else if (name == "source.shell")
            {
                HeredocsEndAlone(node);
            }
            else if (name == "source.cs")
            {
                RawStringsEndAsOpened(node);
            }
            else if (name == "source.python")
            {
                StringsEndAsPythonEndsThem(node);
            }
            LoadedGrammar grammar = GrammarLoader.Load(Encoding.UTF8.GetBytes(node.ToJsonString()).AsSpan());
            registry.Add(grammar);
            foreach (string dependency in grammar.Info.Dependencies)
            {
                Add(dependency);
            }
        }
    }

    private static string ThemeText()
    {
        StringBuilder json = new("{\"name\":\"Passenger\",\"settings\":[{\"settings\":{\"foreground\":\"#000000\"}}");
        foreach ((string selector, TokenKind kind) in ScopeColours.Rules)
        {
            json.Append(CultureInfo.InvariantCulture, $",{{\"scope\":\"{selector}\",\"settings\":{{\"foreground\":\"#{(int)kind:X6}\"}}}}");
        }
        return json.Append("]}").ToString();
    }

    private static Stream Resource(string file) =>
        typeof(Grammar).Assembly.GetManifestResourceStream($"Grammars/{file}") ?? throw new InvalidOperationException($"No grammar {file} in {typeof(Grammar).Assembly.GetName().Name}");

    /// <summary>A plain key that reads as a typed scalar (on, true, null, 200) is a key, in a block mapping and a flow
    /// mapping (<c>{ on: 1 }</c>) alike, in each YAML version's grammar: a key's rule reads no implicit type.</summary>
    private static void KeysAreKeys(JsonNode? node)
    {
        if (node is JsonObject rule)
        {
            if (rule["name"]?.GetValue<string>() is { } scopes && scopes.Contains("entity.name.tag.yaml", StringComparison.Ordinal)
                && rule["patterns"] is JsonArray patterns)
            {
                for (int i = patterns.Count - 1; i >= 0; i--)
                {
                    if (patterns[i] is JsonObject include && include["include"]?.GetValue<string>() is { } named
                        && named[(named.IndexOf('#', StringComparison.Ordinal) + 1)..].StartsWith("tag-implicit-plain", StringComparison.Ordinal))
                    {
                        patterns.RemoveAt(i);
                    }
                }
            }
            foreach ((string _, JsonNode? value) in rule)
            {
                KeysAreKeys(value);
            }
        }
        else if (node is JsonArray list)
        {
            foreach (JsonNode? item in list)
            {
                KeysAreKeys(item);
            }
        }
    }

    /// <summary>A heredoc ends only at a line that is its delimiter alone (after tabs for <c>&lt;&lt;-</c>), as bash ends
    /// one: never at a line that starts with the delimiter and goes on (<c>END suffix</c>).</summary>
    private static void HeredocsEndAlone(JsonNode grammar)
    {
        if (grammar["repository"]?["heredoc"]?["patterns"] is not JsonArray rules)
        {
            return;
        }
        foreach (JsonObject rule in rules.OfType<JsonObject>())
        {
            if (rule["end"]?.GetValue<string>() is { } end)
            {
                rule["end"] = end.Replace(@"(?=\s|;|&|$)", "$", StringComparison.Ordinal);
            }
        }
    }

    /// <summary>A C# raw string opened with five or more quotes (three or more after three dollars) ends at a run of as
    /// many quotes as it opened with, as C# ends one, never at a shorter run inside it.</summary>
    private static void RawStringsEndAsOpened(JsonNode grammar)
    {
        if (grammar["repository"] is not JsonObject repository)
        {
            return;
        }
        foreach (JsonObject rule in repository.Select(entry => entry.Value).OfType<JsonObject>())
        {
            if (rule["begin"]?.GetValue<string>() is { } begin && rule["end"]?.GetValue<string>() is { Length: > 1 } end
                && end[^1] == '+' && end[..^1].All(c => c == '"') && begin.EndsWith(end, StringComparison.Ordinal))
            {
                rule["begin"] = $"{begin[..^end.Length]}({end})";
                rule["end"] = @"\1";
            }
        }
    }

    /// <summary>A Python string ends where Python ends it, whatever its text holds. An f-string field's format spec is text up
    /// to the field's closing brace, its quotes and brackets opening nothing; a comment in a field over lines of a triple-quoted
    /// f-string hides what it says; a statement's opening string runs on past a backslash ending its line, as any other does.</summary>
    private static void StringsEndAsPythonEndsThem(JsonNode grammar)
    {
        if (grammar["repository"] is not JsonObject repository)
        {
            return;
        }
        foreach (string spec in (string[])["fstring-terminator-single-tail", "fstring-terminator-multi-tail"])
        {
            if (repository[spec] is JsonObject rule && rule["begin"]?.GetValue<string>() is { } begin)
            {
                rule["begin"] = begin.Replace("(?=.*?{)", string.Empty, StringComparison.Ordinal);
            }
        }
        if (repository["fstring-multi-brace"]?["patterns"] is JsonArray field)
        {
            field.Add(new JsonObject { ["include"] = "#comments-base" });
        }
        if (repository["docstring"]?["patterns"] is JsonArray openings)
        {
            foreach (JsonObject rule in openings.OfType<JsonObject>())
            {
                if (rule["end"]?.GetValue<string>() is { } end)
                {
                    rule["end"] = end.Replace(@"|(\n)", @"|((?<!\\)\n)", StringComparison.Ordinal);
                }
            }
        }
    }

    /// <summary>Preserves Passenger's handling of malformed captures and open-ended begins. The source assets stay unchanged.</summary>
    private static void Tidy(JsonNode? node)
    {
        if (node is JsonObject rule)
        {
            if (rule.ContainsKey("begin") && !rule.ContainsKey("end") && !rule.ContainsKey("while"))
            {
                rule["end"] = "\uFFFF";
            }
            foreach ((string key, JsonNode? value) in rule)
            {
                if (key is "captures" or "beginCaptures" or "endCaptures" or "whileCaptures" && value is JsonObject captures)
                {
                    foreach (string capture in captures.Where(c => c.Value is not JsonObject).Select(c => c.Key).ToList())
                    {
                        captures.Remove(capture);
                    }
                }
                Tidy(value);
            }
        }
        else if (node is JsonArray list)
        {
            foreach (JsonNode? item in list)
            {
                Tidy(item);
            }
        }
    }
}

/// <summary>A grammar carried here: the scope it names, its file under <c>TextMate/Grammars</c>, and how its tags pair.</summary>
internal sealed record GrammarFile(string Scope, string File, Tags Tags = Tags.None);

/// <summary>Whether a language's tags pair, and how: HTML's void elements (<c>br</c>, <c>img</c>) never close.</summary>
internal enum Tags
{
    None,
    Xml,
    Html,
}
