using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Passenger.Editor;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;

namespace Passenger.Languages.TextMate;

/// <summary>A language's TextMate grammar (VS Code's, or Razor's and C#'s from dotnet/roslyn, carried in this assembly
/// under <c>TextMate/Grammars</c>) with the grammars it embeds, in a registry of its own: TextMateSharp compiles rules as
/// it meets them and keeps no lock, so everything done with one grammar's rules holds <see cref="Gate"/>, and two
/// grammars never share any.</summary>
internal sealed partial class Grammar
{
    private static readonly Dictionary<string, Lazy<Grammar>> Loaded = [];

    /// <summary>The theme every registry shares: each rule's foreground is the kind it colours, as #0000NN.</summary>
    private static readonly string Theme = ThemeText();

    private readonly Lazy<IGrammar> rules;
    private TokenKind[] kinds = [];

    private Grammar(string scope, Tags tags)
    {
        Tags = tags;
        rules = new Lazy<IGrammar>(() => Load(scope), LazyThreadSafetyMode.ExecutionAndPublication);
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
    public IGrammar Rules => rules.Value;

    /// <summary>The kind a token's foreground colour id stands for.</summary>
    public TokenKind KindOf(int foreground) => foreground < kinds.Length ? kinds[foreground] : TokenKind.Text;

    private IGrammar Load(string scope)
    {
        Registry registry = new(new Locator());
        Theme theme = registry.GetTheme();
        // Colour ids count from 1; 0 is none.
        List<TokenKind> byId = [TokenKind.Text];
        for (int id = 1; theme.GetColor(id) is { } colour; id++)
        {
            int value = int.Parse(colour.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byId.Add(Enum.IsDefined((TokenKind)value) ? (TokenKind)value : TokenKind.Text);
        }
        kinds = [.. byId];
        return registry.LoadGrammar(scope) ?? throw new InvalidOperationException($"The {scope} grammar did not load");
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

    /// <summary>Where TextMateSharp finds a grammar by scope: this assembly's, none for a scope it does not carry (a
    /// Markdown fence in a language without one, which then stays code).</summary>
    private sealed partial class Locator : IRegistryOptions
    {
        public IRawTheme GetTheme(string scopeName) => null!;

        public IRawTheme GetDefaultTheme()
        {
            using StreamReader reader = new(new MemoryStream(Encoding.UTF8.GetBytes(Theme)));
            return ThemeReader.ReadThemeSync(reader);
        }

        public IRawGrammar GetGrammar(string scopeName)
        {
            if (Language.GrammarFileOf(scopeName) is not { } file)
            {
                return null!;
            }
            using StreamReader reader = new(Resource(file));
            IRawGrammar grammar = GrammarReader.ReadGrammarSync(reader);
            Tidy(grammar);
            if (scopeName == "source.yaml")
            {
                KeysAreKeys((IDictionary<string, object>)grammar);
            }
            return grammar;
        }

        /// <summary>A YAML key that reads as a boolean, a number or null (<c>on:</c>, a GitHub workflow's first key) is
        /// coloured as a key: the grammar's rule for a block mapping's key stops trying a key as a typed scalar.</summary>
        private static void KeysAreKeys(IDictionary<string, object> grammar)
        {
            if (grammar.TryGetValue("repository", out object? found) && found is IDictionary<string, object> repository
                && repository.TryGetValue("block-pair", out object? pair) && pair is IDictionary<string, object> blockPair
                && blockPair.TryGetValue("patterns", out object? patterns) && patterns is IList<object> rules)
            {
                foreach (IDictionary<string, object> rule in rules.OfType<IDictionary<string, object>>())
                {
                    if (rule.TryGetValue("patterns", out object? inner) && inner is IList<object> keyRules)
                    {
                        foreach (object typed in keyRules.Where(r => r is IDictionary<string, object> include
                            && include.TryGetValue("include", out object? name) && name is "#flow-scalar-plain-out-implicit-type").ToList())
                        {
                            keyRules.Remove(typed);
                        }
                    }
                }
            }
        }

        public ICollection<string> GetInjections(string scopeName) => null!;

        /// <summary>A class of every character past one, <c>[^\x{00}-\x{7F}]</c>: Onigwrap's Oniguruma reads it inside
        /// another class (Less's identifiers, <c>[[-\w][^\x{00}-\x{7F}]]</c>) as every character, where the range past
        /// it reads the same everywhere.</summary>
        [GeneratedRegex(@"\[\^\\x\{0+\}-\\x\{([0-9A-Fa-f]{1,5})\}\]")]
        private static partial Regex PastCharacter();

        /// <summary>Mends what TextMateSharp's reader fails on: a capture that is not a rule is left out (XML's
        /// comments name their own end and scope among their captures, YAML's tag handles a comment), a rule that
        /// begins with no end or while never ends (its end is U+FFFF), and a class of every character past one is
        /// written as the range it is (<see cref="PastCharacter"/>).</summary>
        private static void Tidy(object node)
        {
            if (node is IDictionary<string, object> rule)
            {
                if (rule.ContainsKey("begin") && !rule.ContainsKey("end") && !rule.ContainsKey("while"))
                {
                    rule["end"] = "\uFFFF";
                }
                foreach (string key in (string[])["match", "begin", "end", "while"])
                {
                    if (rule.TryGetValue(key, out object? found) && found is string pattern && pattern.Contains("[^\\x{0", StringComparison.Ordinal))
                    {
                        rule[key] = PastCharacter().Replace(pattern, past =>
                            $"[\\x{{{int.Parse(past.Groups[1].ValueSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture) + 1:X}}}-\\x{{10FFFF}}]");
                    }
                }
                foreach ((string key, object value) in rule)
                {
                    if (key is "captures" or "beginCaptures" or "endCaptures" or "whileCaptures" && value is IDictionary<string, object> captures)
                    {
                        foreach (string capture in captures.Where(c => c.Value is not IDictionary<string, object>).Select(c => c.Key).ToList())
                        {
                            captures.Remove(capture);
                        }
                    }
                    Tidy(value);
                }
            }
            else if (node is IList<object> list)
            {
                foreach (object item in list)
                {
                    Tidy(item);
                }
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
