using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Passenger.App.Endpoints;

/// <summary>What a response is held to, one criterion a line, written in an .http file as <c># @expect …</c> and in the
/// Interactive Test dialog as the same text without the prefix:
/// <c>status 2xx</c>, <c>time &lt; 500</c>, <c>header Content-Type contains json</c>, <c>body contains "id"</c>,
/// <c>json $.items[0].name equals "Ada"</c>. A status or time takes 200, 2xx, 200-299, 200,201, &lt;400 or &gt;=200;
/// a header, body or JSON value takes exists, missing, empty, equals, not-equals, contains, not-contains or matches.</summary>
public sealed partial record Expectation(string Kind, string Subject, string Op, string Value)
{
    /// <summary>A response with no criteria of its own passes when it came and is no error (below 400).</summary>
    public static readonly Expectation Answered = new("status", string.Empty, string.Empty, "<400");

    private static readonly string[] Kinds = ["status", "time", "header", "body", "json"];

    private static readonly string[] Ops = ["exists", "missing", "empty", "equals", "not-equals", "contains", "not-contains", "matches"];

    /// <summary>A criterion from its text, or null for text that is none, which <see cref="Problem"/> says why.</summary>
    public static Expectation? Parse(string text) => TryParse(text, out Expectation? expectation, out _) ? expectation : null;

    /// <summary>Why a line is no criterion, or null when it is one.</summary>
    public static string? Problem(string text) => TryParse(text, out _, out string? why) ? null : why;

    /// <summary>A criterion from its text, or for text that is none one standing for it (<see cref="Malformed"/>), which
    /// fails every response saying why.</summary>
    public static Expectation Read(string text) => TryParse(text, out Expectation? expectation, out string? why)
        ? expectation!
        : new Expectation(string.Empty, string.Empty, string.Empty, text.Trim()) { Malformed = why };

    /// <summary>Why the text this stands for, its <see cref="Value"/>, is no criterion; null for a criterion.</summary>
    public string? Malformed { get; init; }

    private static bool TryParse(string text, out Expectation? expectation, out string? why)
    {
        expectation = null;
        why = null;
        string trimmed = text.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        string kind = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        string rest = space < 0 ? string.Empty : trimmed[(space + 1)..].Trim();
        if (!Kinds.Contains(kind))
        {
            why = $"\"{kind}\" is no criterion: begin with status, time, header, body or json";
            return false;
        }
        if (kind is "status" or "time")
        {
            string pattern = rest.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (kind == "time")
            {
                pattern = pattern.EndsWith("ms", StringComparison.OrdinalIgnoreCase) ? pattern[..^2] : pattern;
                pattern = int.TryParse(pattern, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? "<=" + pattern : pattern;
            }
            if (pattern.Length == 0 || NumberMatches(0, pattern) is null)
            {
                why = $"{kind} takes a number, 2xx, a range (200-299), a list (200,201) or a comparison (<500)";
                return false;
            }
            expectation = new Expectation(kind, string.Empty, string.Empty, pattern);
            return true;
        }
        string subject = string.Empty;
        if (kind is "header" or "json")
        {
            (subject, rest) = Word(rest);
            if (subject.Length == 0)
            {
                why = kind == "header" ? "header takes the header's name" : "json takes a path, as $.items[0].name";
                return false;
            }
            if (kind == "json" && Steps(subject) is null)
            {
                why = $"\"{subject}\" is no path: each step a .name, an [index] or a [\"name\"], as $.items[0].name";
                return false;
            }
        }
        (string op, string value) = Word(rest);
        op = op.ToLowerInvariant() switch
        {
            "" => "exists",
            "==" or "=" or "is" => "equals",
            "!=" => "not-equals",
            "~" or "regex" => "matches",
            string other => other,
        };
        if (!Ops.Contains(op))
        {
            why = $"\"{op}\" is no check: use {string.Join(", ", Ops)}";
            return false;
        }
        value = Unquote(value);
        if (op == "matches" && !ValidPattern(value))
        {
            why = $"\"{value}\" is no regular expression";
            return false;
        }
        expectation = new Expectation(kind, subject, op, value);
        return true;
    }

    /// <summary>The criterion as it is written, which parses back to itself.</summary>
    public override string ToString() => Malformed is not null ? Value : Kind switch
    {
        "status" or "time" => $"{Kind} {Value}",
        _ => string.Join(" ", new[] { Kind, Subject, Op, Op is "exists" or "missing" or "empty" ? string.Empty : Quote(Value) }.Where(p => p.Length > 0)),
    };

    /// <summary>Whether the response meets the criterion, and what it had.</summary>
    public ExpectationResult Check(EndpointResponse response)
    {
        if (Malformed is { } why)
        {
            return new ExpectationResult(this, false, $"no criterion: {why}");
        }
        if (response.Error is { } error)
        {
            return new ExpectationResult(this, false, $"no response: {error}");
        }
        switch (Kind)
        {
            case "status":
                return new ExpectationResult(this, NumberMatches(response.Status, Value) == true, response.Status.ToString(CultureInfo.InvariantCulture));
            case "time":
                int ms = (int)Math.Round(response.Elapsed.TotalMilliseconds);
                return new ExpectationResult(this, NumberMatches(ms, Value) == true, $"{ms} ms");
            case "header":
                string[] values = [.. response.Headers.Where(h => h.Key.Equals(Subject, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value)];
                return Text(values.Length == 0 ? null : string.Join(", ", values));
            case "body":
                return Text(response.Body);
            default:
                return Json(response.Body);
        }
    }

    private ExpectationResult Text(string? actual)
    {
        if (Op == "matches" && actual is not null)
        {
            try
            {
                return new ExpectationResult(this, Regex.IsMatch(actual, Value, RegexOptions.None, TimeSpan.FromSeconds(1)), Shortened(actual));
            }
            catch (RegexMatchTimeoutException)
            {
                return new ExpectationResult(this, false, "no match within 1 s, the pattern given up on");
            }
        }
        bool passed = Op switch
        {
            "exists" => actual is not null,
            "missing" => actual is null,
            "empty" => string.IsNullOrEmpty(actual),
            "equals" => actual == Value,
            "not-equals" => actual != Value,
            "contains" => actual?.Contains(Value, StringComparison.Ordinal) == true,
            "not-contains" => actual?.Contains(Value, StringComparison.Ordinal) != true,
            // matches, with nothing to match.
            _ => false,
        };
        return new ExpectationResult(this, passed, actual is null ? "none" : Shortened(actual));
    }

    private ExpectationResult Json(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return new ExpectationResult(this, false, "the body is no JSON");
        }
        using (document)
        {
            if (Walk(document.RootElement, Subject) is not { } found)
            {
                return Text(null);
            }
            return Text(found.ValueKind == JsonValueKind.String ? found.GetString() : found.GetRawText());
        }
    }

    /// <summary>The element a path names ($.a.b[0], a.b[0], or $ for the root), or null where there is none or the path is
    /// none (<see cref="Steps"/>).</summary>
    internal static JsonElement? Walk(JsonElement root, string path)
    {
        if (Steps(path) is not { } steps)
        {
            return null;
        }
        JsonElement at = root;
        foreach ((string? name, int index) in steps)
        {
            if (name is null)
            {
                if (at.ValueKind != JsonValueKind.Array || index < 0 || index >= at.GetArrayLength())
                {
                    return null;
                }
                at = at[index];
                continue;
            }
            if (at.ValueKind != JsonValueKind.Object || !at.TryGetProperty(name, out JsonElement next))
            {
                return null;
            }
            at = next;
        }
        return at;
    }

    /// <summary>A path's steps, a name (.name, ["name"], ['name']) or an index ([0]) each, which must cover it whole; null
    /// for a path with anything else in it. An index too large for any array is -1, which finds nothing.</summary>
    private static IReadOnlyList<(string? Name, int Index)>? Steps(string path)
    {
        string rest = path.StartsWith('$') ? path[1..] : "." + path;
        List<(string? Name, int Index)> steps = [];
        for (int at = 0; at < rest.Length;)
        {
            Match step = PathStep().Match(rest, at);
            if (!step.Success)
            {
                return null;
            }
            steps.Add(step.Groups[2].Success
                ? (null, int.TryParse(step.Groups[2].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int index) ? index : -1)
                : (step.Groups[1].Success ? step.Groups[1].Value : step.Groups[3].Success ? step.Groups[3].Value : step.Groups[4].Value, 0));
            at += step.Length;
        }
        return steps;
    }

    [GeneratedRegex(@"\G(?:\.([^.\[\]]+)|\[(\d+)\]|\[""([^""]*)""\]|\['([^']*)'\])")]
    private static partial Regex PathStep();

    /// <summary>Whether a number meets a pattern (200, 2xx, 200-299, 200,201, &lt;400, &gt;=200, !=500), or null when the
    /// pattern is none of those.</summary>
    internal static bool? NumberMatches(int actual, string pattern)
    {
        bool any = false;
        foreach (string part in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool? matches = One(part);
            if (matches is null)
            {
                return null;
            }
            any |= matches.Value;
        }
        return pattern.Length == 0 ? null : any;

        bool? One(string part)
        {
            foreach (string op in new[] { "<=", ">=", "!=", "<", ">" })
            {
                if (part.StartsWith(op, StringComparison.Ordinal))
                {
                    return Number(part[op.Length..]) is not { } n ? null : op switch
                    {
                        "<=" => actual <= n,
                        ">=" => actual >= n,
                        "!=" => actual != n,
                        "<" => actual < n,
                        _ => actual > n,
                    };
                }
            }
            if (part.IndexOf('-') is > 0 and int dash)
            {
                return Number(part[..dash]) is { } low && Number(part[(dash + 1)..]) is { } high ? actual >= low && actual <= high : null;
            }
            if (part.Contains('x', StringComparison.OrdinalIgnoreCase))
            {
                string digits = actual.ToString(CultureInfo.InvariantCulture);
                return part.All(c => char.IsAsciiDigit(c) || c is 'x' or 'X') ? digits.Length == part.Length && part.Zip(digits).All(p => p.First is 'x' or 'X' || p.First == p.Second) : null;
            }
            return Number(part) is { } exact ? actual == exact : null;
        }

        static int? Number(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static (string Word, string After) Word(string text)
    {
        string trimmed = text.Trim();
        int space = trimmed.IndexOfAny([' ', '\t']);
        return space < 0 ? (trimmed, string.Empty) : (trimmed[..space], trimmed[(space + 1)..].Trim());
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal) : value;

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) || value.StartsWith('"') ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;

    private static bool ValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Shortened(string text) => text.Length <= 80 ? text : text[..77] + "…";
}

/// <summary>A criterion checked: whether the response met it, and what the response had in its place.</summary>
public sealed record ExpectationResult(Expectation Expectation, bool Passed, string Actual);
