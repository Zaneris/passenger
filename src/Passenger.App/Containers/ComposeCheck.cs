using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.App.Run;
using Passenger.App.Web;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Containers;

/// <summary>A compose file as Compose itself reads it, for the files it reads unasked in a folder (the first base, and the
/// first override beside it): the file's text as typed, the other's from disk, the folder's .env as Compose reads it.
/// <c>docker compose config</c>'s error is the file's problem where its message names a place (else on the first line), or
/// its override's; each <c>${VAR}</c> gets a hint of what Compose makes of it, and in the base file a scalar the override
/// changes gets the value Compose ends with. A file Compose reads only with -f, and a value not surely mapped to the text
/// (one Compose normalizes, a flow collection's insides), are given nothing.</summary>
internal sealed partial class ComposeCheck(EngineChecks owner, OpenDocument document, WebDocument web) : EngineCheck(owner, document, web)
{
    private static readonly string[] Bases = ["compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml"];

    private static readonly string[] Overrides = ["compose.override.yml", "compose.override.yaml", "docker-compose.override.yml", "docker-compose.override.yaml"];

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(20);

    /// <summary>The most of a value a hint shows.</summary>
    private const int Longest = 48;

    /// <summary>The folder's code as the check under way reads it, taken as it starts.</summary>
    private volatile ComposeCode? code;

    protected override bool AsTyped => true;

    protected override void Starting() => code = Owner.Code?.Invoke();

    protected override bool Hints => true;

    protected override async Task<Outcome> CheckAsync(ContainerEngine engine, string path, TextSnapshot snapshot, string text, CancellationToken cancel)
    {
        EngineState state = await engine.StateAsync(cancel: cancel);
        if (!state.IsReady)
        {
            return Outcome.None;
        }
        string folder = Path.GetDirectoryName(path)!;
        if (ReadWith(path) is not { } readWith)
        {
            return Outcome.None;
        }
        (bool isBase, string? partner) = readWith;
        string[] files = partner is null ? ["-"] : isBase ? ["-", partner] : [partner, "-"];
        ComposeText read = ComposeText.Of(text);
        IReadOnlyList<(int Start, int End, string Expression)> interpolations = read.Interpolations(text);
        string[] expressions = [.. interpolations.Select(i => i.Expression).Distinct(StringComparer.Ordinal)];
        Task<ChildResult> project = ConfigAsync(engine, folder, files, text, cancel);
        Task<ChildResult>? alone = partner is not null ? ConfigAsync(engine, folder, ["-"], text, cancel) : null;
        Task<IReadOnlyDictionary<string, string>> values = ValuesAsync(engine, folder, expressions, cancel);
        // All three awaited together, so none that fails goes unobserved.
        Task[] all = alone is null ? [project, values] : [project, alone, values];
        await Task.WhenAll(all);
        ChildResult model = await project;
        ChildResult? self = alone is null ? null : await alone;
        List<Finding> found = [];
        List<InlayHint> hints = [];
        if (!model.Succeeded && !Error(model, folder).Contains(RemoteRefused, StringComparison.Ordinal))
        {
            string said = Error(model, folder);
            (string? named, string message) = Named(said);
            string owner;
            if (named is not null)
            {
                owner = named == "-" ? path : PathNames.Canonical(Path.IsPathRooted(named) ? named : Path.Combine(folder, named));
            }
            else
            {
                // Named by neither: the file's own when the file alone fails the same way, else its partner's.
                owner = partner is null || self is null || (!self.Succeeded && Error(self, folder) == said) ? path : partner;
            }
            bool own = PathNames.Same(owner, path);
            found.Add(Place($"Compose: {message}", owner, own, message, own ? snapshot : Disk(owner)));
        }
        IReadOnlyList<ComposeGap> gaps = [];
        if (model.Succeeded && code is { } reading)
        {
            (IReadOnlyList<Finding> connections, gaps) = await ComposeConnections.FindAsync(reading, folder, path, snapshot, model.Output, cancel);
            found.AddRange(connections);
        }
        IReadOnlyDictionary<string, string> resolved = await values;
        foreach ((int start, int end, string expression) in interpolations)
        {
            if (resolved.TryGetValue(expression, out string? value))
            {
                bool secret = Names(expression).Any(ContainerSecrets.Secret) || NamesBefore(read, text, start).Any(ContainerSecrets.Secret) || InPassword(text, start, end);
                hints.Add(new InlayHint(end, $"= {Shown(value, secret)}", PadLeft: true, Kind: InlayHintKind.Value));
            }
        }
        if (isBase && partner is not null && model.Succeeded && self is { Succeeded: true })
        {
            hints.AddRange(Overridden(read, self.Output, model.Output, Path.GetFileName(partner)));
        }
        return new Outcome(found, [.. hints.OrderBy(h => h.Offset)]) { Gaps = gaps };
    }

    /// <summary>Whether Compose reads a file unasked in its folder (the first base there, or the first override beside it),
    /// and the other file it reads with it (canonical); null for a file it reads only when named with -f.</summary>
    internal static (bool IsBase, string? Partner)? ReadWith(string path)
    {
        string folder = Path.GetDirectoryName(path)!;
        string? first = Found(folder, Bases);
        string? second = first is null ? null : Found(folder, Overrides);
        bool isBase = first is not null && PathNames.Same(first, path);
        return isBase || (second is not null && PathNames.Same(second, path)) ? (isBase, isBase ? second : first) : null;
    }

    /// <summary>The files Compose reads unasked in a folder: its first base, and the first override beside that.</summary>
    internal static IReadOnlyList<string> Unasked(string folder) =>
        Found(folder, Bases) is { } first ? Found(folder, Overrides) is { } second ? [first, second] : [first] : [];

    /// <summary>The first of the names a folder has, as the disk names it.</summary>
    private static string? Found(string folder, string[] names) =>
        names.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists) is { } there ? PathNames.Canonical(there) : null;

    /// <summary><c>docker compose config</c> on the files given, <c>-</c> the text typed, in the file's folder as the project's.</summary>
    private static Task<ChildResult> ConfigAsync(ContainerEngine engine, string folder, string[] files, string text, CancellationToken cancel)
    {
        List<string> words = ["compose", "--project-directory", folder];
        foreach (string file in files)
        {
            words.AddRange(["-f", file]);
        }
        words.AddRange(["config", "--format", "json", "--no-path-resolution"]);
        return Offline(engine, words, folder, files.Contains("-") ? text : null, cancel);
    }

    /// <summary>A Compose command that reaches no network: an <c>include</c> of a Git repository or an OCI artifact, which
    /// Compose fetches by default, is refused (<see cref="RemoteRefused"/>), so a file is checked on this computer alone.</summary>
    private static Task<ChildResult> Offline(ContainerEngine engine, IEnumerable<string> words, string folder, string? input, CancellationToken cancel)
    {
        System.Diagnostics.ProcessStartInfo start = engine.Command(words, folder);
        start.Environment["COMPOSE_EXPERIMENTAL_GIT_REMOTE"] = "false";
        start.Environment["COMPOSE_EXPERIMENTAL_OCI_REMOTE"] = "false";
        return ChildProcesses.RunAsync(start, input, Limit, cancel, answers: [1]);
    }

    /// <summary>What Compose says of a remote include it was told not to fetch: no problem of the file's, so nothing is marked.</summary>
    private const string RemoteRefused = "remote resource is disabled";

    /// <summary>What Compose's own interpolation makes of each expression, asked of it with the folder's .env as the
    /// project's: an extension's list it interpolates as it does every value. A required variable that is unset fails it
    /// whole, so the rest are asked again without such ones.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> ValuesAsync(ContainerEngine engine, string folder, string[] expressions, CancellationToken cancel)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        if (expressions.Length == 0)
        {
            return values;
        }
        if (!await AskAsync(expressions) && expressions.Any(Required))
        {
            await AskAsync([.. expressions.Where(e => !Required(e))]);
        }
        return values;

        async Task<bool> AskAsync(string[] asked)
        {
            if (asked.Length == 0)
            {
                return true;
            }
            JsonObject probe = new() { ["services"] = new JsonObject(), [Probe] = new JsonArray([.. asked.Select(e => (JsonNode?)JsonValue.Create(e))]) };
            ChildResult said = await Offline(engine, ["compose", "--project-directory", folder, "-f", "-", "config", "--format", "json", "--no-path-resolution"],
                folder, probe.ToJsonString(), cancel);
            if (!said.Succeeded)
            {
                return false;
            }
            try
            {
                using JsonDocument read = JsonDocument.Parse(said.Output);
                if (!read.RootElement.TryGetProperty(Probe, out JsonElement list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != asked.Length)
                {
                    return false;
                }
                for (int i = 0; i < asked.Length; i++)
                {
                    if (Scalar(list[i]) is { } value)
                    {
                        values[asked[i]] = value;
                    }
                }
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }

    private const string Probe = "x-passenger-values";

    private static bool Required(string expression) => RequiredExpression().IsMatch(expression);

    [GeneratedRegex(@"^\$\{[A-Za-z_][A-Za-z0-9_]*:?\?")]
    private static partial Regex RequiredExpression();

    /// <summary>The hints in the base file where the override changes a scalar: Compose's final value, after the
    /// override's name, wherever the base's own model has the scalar the text has (so a value Compose normalizes is left out).</summary>
    private static IEnumerable<InlayHint> Overridden(ComposeText read, string own, string merged, string overrideName)
    {
        List<InlayHint> hints = [];
        try
        {
            using JsonDocument before = JsonDocument.Parse(own);
            using JsonDocument after = JsonDocument.Parse(merged);
            foreach (ComposeEntry entry in read.Entries)
            {
                if (entry.Scalar is not { } written || At(before.RootElement, entry.Path) is not { } was || Scalar(was) is not { } value
                    || (value != written && !written.Contains('$', StringComparison.Ordinal)))
                {
                    continue;
                }
                if (At(after.RootElement, entry.Path) is { } now && Scalar(now) is { } ends && ends != value)
                {
                    bool secret = entry.Path.Any(ContainerSecrets.Secret) || (ItemName().Match(written) is { Success: true } item && ContainerSecrets.Secret(item.Groups["name"].Value));
                    hints.Add(new InlayHint(entry.ValueEnd, $"{overrideName}: {Shown(ends, secret)}", PadLeft: true, Kind: InlayHintKind.Value));
                }
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return hints;
    }

    /// <summary>The element at a path of keys and indexes, or null.</summary>
    private static JsonElement? At(JsonElement root, IReadOnlyList<string> path)
    {
        JsonElement at = root;
        foreach (string step in path)
        {
            if (at.ValueKind == JsonValueKind.Object && at.TryGetProperty(step, out JsonElement inner))
            {
                at = inner;
            }
            else if (at.ValueKind == JsonValueKind.Array && int.TryParse(step, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
                && index < at.GetArrayLength())
            {
                at = at[index];
            }
            else
            {
                return null;
            }
        }
        return at;
    }

    private static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
        _ => null,
    };

    /// <summary>Every variable an expression reads, its default's included (<c>${A:-${DB_PASSWORD}}</c>).</summary>
    private static IEnumerable<string> Names(string expression) => VariableName().Matches(expression).Select(m => m.Groups[1].Value);

    /// <summary>The names a value at an offset is given: the key it is written under, a list item's <c>KEY=</c>
    /// (environment's list form, a flow sequence's item), and a name just before it (a connection string's
    /// <c>Password=</c>, an option's <c>--password=</c>).</summary>
    private static IEnumerable<string> NamesBefore(ComposeText read, string text, int offset)
    {
        if (read.Entries.LastOrDefault(e => e.KeyStart <= offset) is { Path: [.., string key] } && !key.All(char.IsAsciiDigit))
        {
            yield return key;
        }
        int lineStart = text.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        int item = offset;
        while (item > lineStart && text[item - 1] is not ('[' or ','))
        {
            item--;
        }
        if (ItemName().Match(text[item..offset]) is { Success: true } named)
        {
            yield return named.Groups["name"].Value;
        }
        if (NameJustBefore().Match(text[lineStart..offset]) is { Success: true } just)
        {
            yield return just.Groups["name"].Value;
        }
    }

    /// <summary>Whether an expression stands where a URL's password does (<c>postgres://user:${PGPASS}@db</c>).</summary>
    private static bool InPassword(string text, int start, int end)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
        int lineEnd = text.IndexOf('\n', end) is int next and >= 0 ? next : text.Length;
        return ContainerSecrets.AtUrlPassword(text[lineStart..start], text[end..lineEnd]);
    }

    /// <summary>A value as a hint shows it, by Passenger's one rule for secrets (<see cref="ContainerSecrets"/>): a
    /// secret's as its dots; else with a URL's password and a connection string's so masked, its first line, cut at
    /// <see cref="Longest"/>, an empty one as "".</summary>
    private static string Shown(string value, bool secret)
    {
        if (secret)
        {
            return ContainerSecrets.Masked;
        }
        string bare = ContainerSecrets.InValue(value);
        int line = bare.IndexOfAny(['\r', '\n']);
        string shown = line < 0 ? bare : bare[..line] + "…";
        return shown.Length == 0 ? "\"\"" : shown.Length > Longest ? shown[..(Longest - 1)] + "…" : shown;
    }

    [GeneratedRegex(@"\$\{?([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex VariableName();

    [GeneratedRegex(@"^\s*(?:-\s+)?[""']?(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)\s*=")]
    private static partial Regex ItemName();

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_.\-]*)\s*[=:]\s*[""']?$")]
    private static partial Regex NameJustBefore();

    /// <summary>What Compose said of the failure: its last line that is not one of its log's warnings, its secrets masked
    /// (what it interpolated from the folder's .env among them).</summary>
    private static string Error(ChildResult result, string folder) =>
        result.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => !l.StartsWith("time=", StringComparison.Ordinal) && !ContainerEngine.ProviderFailed(l)) is { } last
            ? ContainerSecrets.InSaid(last, folder) : ContainerEngine.Said(result, folder);

    /// <summary>The file a message names (its schema's check, its parse), "-" for the text typed, and the message after it.</summary>
    private static (string? File, string Message) Named(string said)
    {
        foreach (string lead in (string[])["validating ", "failed to parse "])
        {
            if (said.StartsWith(lead, StringComparison.Ordinal) && said.IndexOf(": ", lead.Length, StringComparison.Ordinal) is int colon and > 0)
            {
                return (said[lead.Length..colon], said[(colon + 2)..]);
            }
        }
        return (null, said);
    }

    /// <summary>A file's text from disk, for a place in it; null when it cannot be read.</summary>
    private static TextSnapshot? Disk(string path)
    {
        try
        {
            return TextSnapshot.From(File.ReadAllText(path));
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A problem where Compose's message puts it: the name a service uses that the file does not declare (its
    /// <see cref="ComposeFault"/>, which Passenger's own fixes take), a parser's line and column, a line, or a path of its
    /// model found in the text (a key, or the scalar a path to a value names); else the first line. Marked when it is the
    /// file checked (<paramref name="own"/>, <paramref name="text"/> as checked); the other file's (its disk's text) only placed.</summary>
    private static Finding Place(string message, string file, bool own, string said, TextSnapshot? text)
    {
        if (text is null)
        {
            return new Finding(ProblemSeverity.Error, message, file, new TextPosition(0, 0), null);
        }
        if (ComposeFixes.Read(said) is { } fault && ComposeFixes.Place(text.ToString(), fault) is { } named)
        {
            return new Finding(ProblemSeverity.Error, message, file, text.GetPosition(named.Start), own ? named : null)
            {
                Fixes = own ? now => ComposeFixes.For(now, fault) : null,
            };
        }
        TextRange range = Lines(text, 0, 0);
        if (ParserPlace().Match(said) is { Success: true } parsed)
        {
            int line = Math.Clamp(int.Parse(parsed.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) - 1, 0, text.LineCount - 1);
            int column = int.Parse(parsed.Groups[2].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) - 1;
            TextRange whole = Lines(text, line, line);
            int at = text.GetOffset(new TextPosition(line, column));
            range = at < whole.End ? TextRange.FromBounds(Math.Max(at, whole.Start), whole.End) : whole;
        }
        else if (LinePlace().Matches(said) is { Count: > 0 } lines)
        {
            int line = int.Parse(lines[^1].Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) - 1;
            range = Lines(text, line, line);
        }
        else if (ModelPath(said) is { } path && Deepest(ComposeText.Of(text.ToString()), path) is { } entry)
        {
            range = entry.Path.Count == path.Count && entry.ValueStart >= 0
                ? TextRange.FromBounds(entry.ValueStart, entry.ValueEnd) : TextRange.FromBounds(entry.KeyStart, entry.KeyEnd);
        }
        return new Finding(ProblemSeverity.Error, message, file, text.GetPosition(range.Start), own ? range : null);
    }

    /// <summary>The entry of the longest part of a path the text has.</summary>
    private static ComposeEntry? Deepest(ComposeText read, IReadOnlyList<string> path)
    {
        for (int length = path.Count; length > 0; length--)
        {
            if (read.At([.. path.Take(length)]) is { } entry)
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>The path of Compose's model a message names: a property the schema has not (after the path it is under), a
    /// value it could not interpolate, a path it begins with, or a service and what of it refers to nothing.</summary>
    private static IReadOnlyList<string>? ModelPath(string said)
    {
        if (Interpolating().Match(said) is { Success: true } interpolating)
        {
            return Steps(interpolating.Groups[1].Value);
        }
        if (Leading().Match(said) is { Success: true } leading)
        {
            List<string> path = [.. Steps(leading.Groups[1].Value)];
            if (Additional().Match(said) is { Success: true } additional)
            {
                path.Add(additional.Groups[1].Value);
            }
            return path;
        }
        if (Service().Match(said) is { Success: true } service)
        {
            string name = service.Groups[1].Value;
            string? what = said.Contains("depends on", StringComparison.Ordinal) ? "depends_on"
                : said.Contains("undefined network", StringComparison.Ordinal) ? "networks"
                : said.Contains("undefined volume", StringComparison.Ordinal) ? "volumes"
                : said.Contains("undefined secret", StringComparison.Ordinal) ? "secrets"
                : said.Contains("undefined config", StringComparison.Ordinal) ? "configs" : null;
            return what is null ? ["services", name] : ["services", name, what];
        }
        return null;

        static string[] Steps(string dotted) => [.. dotted.Split('.').Where(s => s.Length > 0 && s != "[]")];
    }

    [GeneratedRegex(@"\bL(\d+)\.C(\d+)")]
    private static partial Regex ParserPlace();

    [GeneratedRegex(@"(?<![\w.])line (\d+):")]
    private static partial Regex LinePlace();

    [GeneratedRegex(@"^error while interpolating ([^\s:]+):")]
    private static partial Regex Interpolating();

    [GeneratedRegex(@"^((?:services|networks|volumes|configs|secrets|include|models)(?:\.[^\s.:]+)+)\b")]
    private static partial Regex Leading();

    [GeneratedRegex(@"additional propert(?:y|ies) '([^']+)'")]
    private static partial Regex Additional();

    [GeneratedRegex(@"\bservice ""([^""]+)""")]
    private static partial Regex Service();
}
