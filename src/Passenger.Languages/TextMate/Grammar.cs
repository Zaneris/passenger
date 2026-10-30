using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Passenger.Editor;
using Tint;
using Tint.Loading;

namespace Passenger.Languages.TextMate;

/// <summary>A language's TextMate grammar (VS Code's, or Razor's and C#'s from dotnet/roslyn, carried in this assembly
/// under <c>TextMate/Grammars</c>) with the grammars it embeds, in a registry of its own. Documents of this scope
/// share a tokenizer, whose mutable scratch buffers and scanners are protected by <see cref="Gate"/>. Different
/// scopes use separate registries and tokenizers.</summary>
internal sealed class Grammar
{
    private static readonly Dictionary<string, Lazy<Grammar>> Loaded = [];

    /// <summary>The theme every registry shares: each rule's foreground is the kind it colours, as #0000NN.</summary>
    private static readonly string Theme = ThemeText();

    private readonly Lazy<Tokenizer> rules;
    private TokenKind[] kinds = [];

    private Grammar(string scope, Tags tags)
    {
        Tags = tags;
        rules = new Lazy<Tokenizer>(() => Load(scope), LazyThreadSafetyMode.ExecutionAndPublication);
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

    /// <summary>The kind a token's foreground colour id stands for.</summary>
    public TokenKind KindOf(int foreground) => foreground < kinds.Length ? kinds[foreground] : TokenKind.Text;

    private Tokenizer Load(string scope)
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
        return registry.CreateTokenizer(scope, theme);

        void Add(string name)
        {
            if (!loaded.Add(name) || Language.GrammarFileOf(name) is not { } file)
            {
                return;
            }
            using Stream resource = Resource(file);
            JsonNode node = JsonNode.Parse(resource) ?? throw new InvalidOperationException($"Empty grammar {file}");
            Tidy(node);
            if (name == "source.yaml")
            {
                KeysAreKeys(node);
            }
            else if (name == "source.shell")
            {
                HeredocsEndAlone(node);
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

    /// <summary>Preserves Passenger's YAML key policy, including keys such as on, true and null, in a block mapping and a
    /// flow mapping (<c>{ on: 1 }</c>) alike.</summary>
    private static void KeysAreKeys(JsonNode grammar)
    {
        foreach ((string pair, string typed) in (ReadOnlySpan<(string, string)>)[("block-pair", "#flow-scalar-plain-out-implicit-type"), ("flow-pair", "#flow-scalar-plain-in-implicit-type")])
        {
            if (grammar["repository"]?[pair]?["patterns"] is not JsonArray rules)
            {
                continue;
            }
            foreach (JsonObject rule in rules.OfType<JsonObject>())
            {
                if (rule["patterns"] is JsonArray keyRules)
                {
                    for (int i = keyRules.Count - 1; i >= 0; i--)
                    {
                        if (keyRules[i] is JsonObject include && include["include"]?.GetValue<string>() == typed)
                        {
                            keyRules.RemoveAt(i);
                        }
                    }
                }
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
