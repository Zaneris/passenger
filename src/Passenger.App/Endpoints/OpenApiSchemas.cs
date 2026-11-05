using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Endpoints;

/// <summary>Where a JSON value breaks its schema: the member, as a JSON pointer (<c>/items/0/price</c>; <c>/</c> for the
/// whole), and what is wrong there.</summary>
public sealed record SchemaBreak(string At, string Said)
{
    public override string ToString() => $"at {At}: {Said}";
}

/// <summary>An OpenAPI document's schemas put to use: a response's value held to one, each place it breaks it said
/// (<see cref="Check"/>), and a value made from one for a request's body (<see cref="Sample"/>). The parts read are those a
/// response's body is checked by: types (with 3.0's <c>nullable</c> and 3.1's lists), <c>enum</c>, a string's <c>pattern</c>,
/// an object's <c>required</c>, which a <c>writeOnly</c> member keeps to requests, <c>properties</c> and
/// <c>additionalProperties</c>, an array's <c>items</c>, and <c>allOf</c>, <c>anyOf</c> and <c>oneOf</c>; the rest (lengths,
/// bounds, formats) is not checked.</summary>
public static class OpenApiSchemas
{
    /// <summary>How deep into a value a check goes.</summary>
    private const int Deepest = 48;

    /// <summary>How many breaks a check says at most.</summary>
    private const int Most = 50;

    /// <summary>The places a response's value breaks its schema, in the order met; none where it keeps to it.</summary>
    public static IReadOnlyList<SchemaBreak> Check(OpenApiDocument document, JsonElement schema, JsonElement value)
    {
        Checking checking = new(document, value);
        checking.Walk(schema, string.Empty, value, string.Empty, 0, schema);
        return checking.Breaks;
    }

    /// <summary>One check of a value, each pair of a schema and a value beneath it judged once: a schema by where it lies (the
    /// <c>$ref</c> it was found by, else its path beneath the schema checked), a value by where it starts in the value
    /// checked. <see cref="Walk"/> says each break; <see cref="Fits"/> asks only whether a choice's option holds. Each is given
    /// the schema its value was first held to, whole, whose parts may declare what another part requires.</summary>
    private sealed class Checking(OpenApiDocument document, JsonElement checkedValue)
    {
        private readonly HashSet<(string Schema, int Value)> walked = [];
        private readonly Dictionary<(string Schema, int Value), bool> fitting = [];

        public List<SchemaBreak> Breaks { get; } = [];

        public void Walk(JsonElement written, string place, JsonElement value, string at, int depth, JsonElement whole)
        {
            (JsonElement schema, string key) = Resolved(written, place);
            if (depth <= Deepest && Breaks.Count < Most && walked.Add((key, Start(value))))
            {
                Judge(schema, key, value, at, depth, Breaks, whole);
            }
        }

        public bool Fits(JsonElement written, string place, JsonElement value, string at, int depth, JsonElement whole)
        {
            if (depth > Deepest)
            {
                return true;
            }
            (JsonElement schema, string key) = Resolved(written, place);
            (string, int) pair = (key, Start(value));
            if (fitting.TryGetValue(pair, out bool known))
            {
                return known;
            }
            // Asked again before it is answered, of the same value, a schema that refers to itself holds nothing more.
            fitting[pair] = true;
            return fitting[pair] = Judge(schema, key, value, at, depth, null, whole);
        }

        /// <summary>Whether the value keeps to the schema: each break said into <paramref name="breaks"/> where given, else
        /// the answer given at the first.</summary>
        private bool Judge(JsonElement schema, string key, JsonElement value, string at, int depth, List<SchemaBreak>? breaks, JsonElement whole)
        {
            if (schema.ValueKind == JsonValueKind.False)
            {
                return Broken(breaks, at, "nothing is allowed here");
            }
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return true;
            }
            bool kept = true;
            IReadOnlyList<JsonElement> parts = JsonMembers.Items(schema, "allOf");
            for (int part = 0; part < parts.Count && (kept || breaks is not null); part++)
            {
                kept &= Down(parts[part], $"{key}/allOf/{Numbered(part)}", value, at, depth, breaks, whole);
            }
            if (!kept && breaks is null)
            {
                return false;
            }
            foreach (string choice in (string[])["anyOf", "oneOf"])
            {
                if (Chosen(schema, key, choice, value, at, depth, whole) is { } said)
                {
                    return Broken(breaks, at, said);
                }
            }
            IReadOnlyList<string> types = TypesOf(schema);
            if (types.Count > 0 && !types.Any(type => IsA(value, type)))
            {
                return Broken(breaks, at, $"{Either([.. types.OrderBy(t => t == "null").Select(Named)])} was expected, not {Describe(value)}");
            }
            if (JsonMembers.Items(schema, "enum") is { Count: > 0 } allowed && !allowed.Any(item => JsonElement.DeepEquals(item, value)))
            {
                return Broken(breaks, at, $"{Describe(value)} is not one of {string.Join(", ", allowed.Take(8).Select(item => item.GetRawText()))}{(allowed.Count > 8 ? ", …" : string.Empty)}");
            }
            switch (value.ValueKind)
            {
                case JsonValueKind.String when JsonMembers.Text(schema, "pattern") is { } pattern && Matches(pattern, value.GetString()!) == false:
                    return Broken(breaks, at, $"{Describe(value)} does not match its pattern {pattern}");
                case JsonValueKind.Object:
                    return JudgeObject(schema, key, value, at, depth, breaks, whole) && kept;
                case JsonValueKind.Array when JsonMembers.Object(schema, "items") is { } items:
                    int index = 0;
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        if (!Down(items, $"{key}/items", item, $"{at}/{Numbered(index++)}", depth + 1, breaks, items) && breaks is null)
                        {
                            return false;
                        }
                    }
                    break;
            }
            return kept;
        }

        private bool JudgeObject(JsonElement schema, string key, JsonElement value, string at, int depth, List<SchemaBreak>? breaks, JsonElement whole)
        {
            bool kept = true;
            foreach (string required in JsonMembers.Texts(schema, "required"))
            {
                if (JsonMembers.Value(value, required) is null && !WriteOnly(whole, required))
                {
                    kept = Broken(breaks, $"{at}/{Escaped(required)}", "is missing, though its schema requires it");
                    if (breaks is null)
                    {
                        return false;
                    }
                }
            }
            JsonElement? properties = JsonMembers.Object(schema, "properties");
            bool closed = JsonMembers.Bool(schema, "additionalProperties") == false;
            JsonElement? others = JsonMembers.Object(schema, "additionalProperties");
            foreach (JsonProperty member in value.EnumerateObject())
            {
                string place = $"{at}/{Escaped(member.Name)}";
                bool held = JsonMembers.Value(properties, member.Name) is { } own
                    ? Down(own, $"{key}/properties/{Escaped(member.Name)}", member.Value, place, depth + 1, breaks, own)
                    : closed ? Broken(breaks, place, "is not a member its schema has")
                    : others is not { } kind || Down(kind, $"{key}/additionalProperties", member.Value, place, depth + 1, breaks, kind);
                if (!held && breaks is null)
                {
                    return false;
                }
                kept &= held;
            }
            return kept;
        }

        /// <summary>Whether any schema of those held over an object marks a member write-only, which is never in a
        /// response.</summary>
        private bool WriteOnly(JsonElement whole, string name) =>
            Joined(whole).Any(schema => JsonMembers.Value(JsonMembers.Object(schema, "properties"), name) is { } member
                && Joined(member).Any(part => JsonMembers.Bool(part, "writeOnly") == true));

        /// <summary>A schema and those held over the same value with it: what its <c>$ref</c> names, beside which 3.1 may write
        /// more, and its <c>allOf</c>'s, <c>anyOf</c>'s and <c>oneOf</c>'s, each reference followed once.</summary>
        private IEnumerable<JsonElement> Joined(JsonElement written)
        {
            HashSet<string> followed = [];
            Stack<JsonElement> waiting = new([written]);
            while (waiting.TryPop(out JsonElement schema))
            {
                if (JsonMembers.Text(schema, "$ref") is { } reference && followed.Add(reference) && document.Resolve(schema) is var named && !Same(named, schema))
                {
                    waiting.Push(named);
                }
                yield return schema;
                foreach (string joining in (string[])["allOf", "anyOf", "oneOf"])
                {
                    foreach (JsonElement part in JsonMembers.Items(schema, joining))
                    {
                        waiting.Push(part);
                    }
                }
            }
        }

        /// <summary>What a choice says of a value that keeps to none of its options, or to more than one of a
        /// <c>oneOf</c>'s; null where it keeps to it.</summary>
        private string? Chosen(JsonElement schema, string key, string choice, JsonElement value, string at, int depth, JsonElement whole)
        {
            IReadOnlyList<JsonElement> options = JsonMembers.Items(schema, choice);
            List<int> held = [];
            for (int option = 0; option < options.Count && held.Count < (choice == "oneOf" ? 2 : 1); option++)
            {
                if (Fits(options[option], $"{key}/{choice}/{Numbered(option)}", value, at, depth, whole))
                {
                    held.Add(option);
                }
            }
            return options.Count == 0 || held.Count == 1 ? null
                : held.Count == 0 ? $"{Describe(value)} is none of the {options.Count} kinds its schema's {choice} allows"
                : $"{Describe(value)} fits both {Option(options, held[0])} and {Option(options, held[1])} of its schema's oneOf, which allows one alone";
        }

        /// <summary>A schema beneath another walked, its breaks said, or asked whether it holds.</summary>
        private bool Down(JsonElement written, string place, JsonElement value, string at, int depth, List<SchemaBreak>? breaks, JsonElement whole)
        {
            if (breaks is null)
            {
                return Fits(written, place, value, at, depth, whole);
            }
            Walk(written, place, value, at, depth, whole);
            return true;
        }

        /// <summary>Where a value starts in the value checked, which no other value beneath it starts at.</summary>
        private int Start(JsonElement value) =>
            JsonMarshal.GetRawUtf8Value(checkedValue).Overlaps(JsonMarshal.GetRawUtf8Value(value), out int start) ? start : -1;

        /// <summary>A schema with its reference followed, and where it lies: by the reference where the document's own schemas
        /// gave it, else by its place.</summary>
        private (JsonElement Schema, string Key) Resolved(JsonElement written, string place)
        {
            JsonElement schema = document.Resolve(written);
            return (schema, JsonMembers.Text(written, "$ref") is { } reference && !Same(schema, written) ? reference : place);
        }

        private static bool Same(JsonElement one, JsonElement other) =>
            JsonMarshal.GetRawUtf8Value(one).Overlaps(JsonMarshal.GetRawUtf8Value(other), out int offset) && offset == 0;

        /// <summary>An option as a break names it: the schema its <c>$ref</c> names, else its place in the list.</summary>
        private static string Option(IReadOnlyList<JsonElement> options, int option) =>
            JsonMembers.Text(options[option], "$ref") is { } reference
                ? reference[(reference.LastIndexOf('/') + 1)..].Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)
                : $"option {Numbered(option + 1)}";

        private static bool Broken(List<SchemaBreak>? breaks, string at, string said)
        {
            if (breaks is { Count: < Most })
            {
                breaks.Add(new SchemaBreak(at.Length == 0 ? "/" : at, said));
            }
            return false;
        }

        private static string Numbered(int number) => number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The types a schema allows: its <c>type</c>, one or a list, with null where 3.0's <c>nullable</c> says.</summary>
    private static IReadOnlyList<string> TypesOf(JsonElement schema)
    {
        List<string> types = JsonMembers.Text(schema, "type") is { } type ? [type] : [.. JsonMembers.Texts(schema, "type")];
        if (types.Count > 0 && JsonMembers.Bool(schema, "nullable") == true && !types.Contains("null"))
        {
            types.Add("null");
        }
        return types;
    }

    private static bool IsA(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && (value.TryGetInt64(out _) || value.TryGetDecimal(out decimal whole) && whole == decimal.Truncate(whole)),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "array" => value.ValueKind == JsonValueKind.Array,
        "object" => value.ValueKind == JsonValueKind.Object,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    /// <summary>Kinds as a break names them: "a string", "a string or null", "a whole number, a string or null".</summary>
    private static string Either(IReadOnlyList<string> kinds) => kinds.Count == 1 ? kinds[0] : $"{string.Join(", ", kinds.Take(kinds.Count - 1))} or {kinds[^1]}";

    private static string Named(string type) => type switch
    {
        "null" => "null",
        "integer" => "a whole number",
        "number" => "a number",
        "string" => "a string",
        "boolean" => "true or false",
        "array" => "an array",
        "object" => "an object",
        _ => type,
    };

    /// <summary>A value as a break says it: <c>the string "cheap"</c>, <c>the number 42</c>, <c>an object</c>.</summary>
    private static string Describe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => $"the string {JsonSerializer.Serialize(Shortened(value.GetString()!))}",
        JsonValueKind.Number => $"the number {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "an array",
        _ => "an object",
    };

    private static string Shortened(string text) => text.Length > 40 ? text[..40] + "…" : text;

    /// <summary>Whether a string matches a schema's pattern, which ECMAScript writes and .NET's regex reads alike for the most
    /// part; null where the pattern cannot be read or takes too long, which the check passes over.</summary>
    private static bool? Matches(string pattern, string text)
    {
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        }
        catch (Exception unread) when (unread is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static string Escaped(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>A value made from a schema, as a request's body starts: its example or default where it gives one, else its
    /// first enum value, else of its first type (an object's every member, an array of one item, a string by its format,
    /// 0, false); null where the schema says nothing of what it is.</summary>
    public static JsonNode? Sample(OpenApiDocument document, JsonElement schema) => Make(document, schema, 0, []);

    /// <summary>A sample as an .http body or a dialog's: indented JSON.</summary>
    public static string SampleText(OpenApiDocument document, JsonElement schema) =>
        Sample(document, schema)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "{}";

    private static JsonNode? Make(OpenApiDocument document, JsonElement written, int depth, HashSet<string> referred)
    {
        string? reference = JsonMembers.Text(written, "$ref");
        if (depth > 8 || reference is not null && !referred.Add(reference))
        {
            return null;
        }
        try
        {
            JsonElement schema = document.Resolve(written);
            if (schema.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (string given in (string[])["example", "default"])
            {
                if (JsonMembers.Value(schema, given) is { } example)
                {
                    return JsonNode.Parse(example.GetRawText());
                }
            }
            if (JsonMembers.Items(schema, "examples") is [var first, ..])
            {
                return JsonNode.Parse(first.GetRawText());
            }
            if (JsonMembers.Items(schema, "enum") is [var listed, ..])
            {
                return JsonNode.Parse(listed.GetRawText());
            }
            if (JsonMembers.Items(schema, "allOf") is { Count: > 0 } parts)
            {
                JsonObject merged = [];
                foreach (JsonNode? part in parts.Select(p => Make(document, p, depth + 1, referred)))
                {
                    if (part is JsonObject members)
                    {
                        foreach ((string name, JsonNode? member) in members.ToList())
                        {
                            members.Remove(name);
                            merged[name] = member;
                        }
                    }
                }
                return merged;
            }
            if ((JsonMembers.Items(schema, "oneOf") is [var one, ..] ? one : JsonMembers.Items(schema, "anyOf") is [var any, ..] ? any : (JsonElement?)null) is { } choice)
            {
                return Make(document, choice, depth + 1, referred);
            }
            IReadOnlyList<string> types = TypesOf(schema);
            string? kind = ((string[])["object", "array", "integer", "number", "boolean", "string"]).FirstOrDefault(types.Contains)
                ?? (JsonMembers.Object(schema, "properties") is not null ? "object" : null);
            return kind switch
            {
                "object" => new JsonObject(JsonMembers.Properties(schema, "properties").Select(p => KeyValuePair.Create(p.Name, Make(document, p.Value, depth + 1, referred)))),
                "array" => JsonMembers.Object(schema, "items") is { } items ? new JsonArray(Make(document, items, depth + 1, referred)) : new JsonArray(),
                "integer" or "number" => JsonValue.Create(0),
                "boolean" => JsonValue.Create(false),
                "string" => JsonValue.Create(JsonMembers.Text(schema, "format") switch
                {
                    "date-time" => "2000-01-01T00:00:00Z",
                    "date" => "2000-01-01",
                    "time" => "00:00:00",
                    "uuid" => Guid.Empty.ToString(),
                    "email" => "name@example.com",
                    "uri" => "https://example.com/",
                    _ => "string",
                }),
                _ => null,
            };
        }
        finally
        {
            if (reference is not null)
            {
                referred.Remove(reference);
            }
        }
    }
}

/// <summary>The .http requests an app's OpenAPI document makes, written as the Endpoints window's generated ones are: per
/// operation a separator naming it, a variable for each path or required query parameter the file does not set yet (one
/// named for its operation where an earlier one of this writing holds another value), the method and the URL from the
/// project's address with each as a <c>{{variable}}</c>, an Accept header and each required header, and a JSON body made
/// from its request schema (<see cref="OpenApiSchemas.Sample"/>), else a form body from its form schema, else a multipart
/// body of a part a field, a file's a text file's part the person fills in.</summary>
public static partial class OpenApiRequests
{
    /// <summary>The requests for <paramref name="operations"/>, to go after <paramref name="existing"/>, the file's text: one
    /// made for them begins with the address variable <paramref name="host"/>, at <paramref name="address"/>.</summary>
    public static string Entries(OpenApiDocument document, IEnumerable<OpenApiOperation> operations, string existing, string host, string? address)
    {
        StringBuilder written = new();
        HashSet<string> set = [.. HttpFile.Parse(string.Empty, existing).Variables.Keys];
        if (existing.Trim().Length == 0)
        {
            written.Append('@').Append(host).Append(" = ").Append(address ?? "http://localhost:5000").Append("\n\n");
            set.Add(host);
        }
        else if (!existing.EndsWith('\n'))
        {
            written.Append('\n');
        }
        if (existing.Trim().Length > 0 && !existing.TrimEnd().EndsWith("###", StringComparison.Ordinal))
        {
            written.Append("###\n");
        }
        if (set.Add(host))
        {
            written.Append('@').Append(host).Append(" = ").Append(address ?? "http://localhost:5000").Append("\n\n");
        }
        // What this writing set each variable to: a later operation's parameter of the name but another value has its own.
        Dictionary<string, string> made = new(StringComparer.Ordinal);
        foreach (OpenApiOperation operation in operations)
        {
            written.Append("# OpenAPI: ").Append(operation.OperationId ?? operation.Title).Append(operation.Summary is { Length: > 0 } summary ? $" ({summary})" : string.Empty).Append('\n');
            List<OpenApiParameter> named = [.. operation.Parameters.Where(p => p.In == "path" || p.In == "query" && p.Required)];
            Dictionary<string, string> variables = new(StringComparer.Ordinal);
            foreach (OpenApiParameter parameter in named)
            {
                string value = ValueOf(document, parameter);
                string variable = parameter.Name;
                if (made.TryGetValue(variable, out string? was) && was != value)
                {
                    variable = $"{operation.OperationId ?? operation.Method.ToLowerInvariant()}_{parameter.Name}";
                    for (int more = 2; set.Contains(variable) && made.GetValueOrDefault(variable) != value; more++)
                    {
                        variable = $"{operation.OperationId ?? operation.Method.ToLowerInvariant()}_{parameter.Name}{more}";
                    }
                }
                variables[parameter.Name] = variable;
                if (set.Add(variable))
                {
                    made[variable] = value;
                    written.Append('@').Append(variable).Append(" = ").Append(value).Append('\n');
                }
            }
            string path = Braced().Replace(operation.Path, match => "{{" + variables.GetValueOrDefault(match.Groups[1].Value, match.Groups[1].Value) + "}}");
            List<string> query = [.. named.Where(p => p.In == "query").Select(p => $"{Uri.EscapeDataString(p.Name)}={{{{{variables[p.Name]}}}}}")];
            written.Append(operation.Method).Append(" {{").Append(host).Append("}}").Append(path).Append(query.Count > 0 ? "?" + string.Join("&", query) : string.Empty).Append('\n');
            written.Append("Accept: application/json\n");
            foreach (OpenApiParameter header in operation.Parameters.Where(p => p.In == "header" && p.Required))
            {
                written.Append(header.Name).Append(": ").Append(ValueOf(document, header)).Append('\n');
            }
            if (operation.RequestSchema is { } body)
            {
                written.Append("Content-Type: application/json\n\n").Append(OpenApiSchemas.SampleText(document, body).Replace("\r\n", "\n", StringComparison.Ordinal)).Append('\n');
            }
            else if (operation.FormSchema is { } form && OpenApiSchemas.Sample(document, form) is JsonObject fields)
            {
                written.Append("Content-Type: application/x-www-form-urlencoded\n\n")
                    .AppendJoin('&', fields.Select(field => $"{Uri.EscapeDataString(field.Key)}={Uri.EscapeDataString(TextOf(field.Value))}"))
                    .Append('\n');
            }
            else if (operation.MultipartSchema is { } multipart)
            {
                written.Append("Content-Type: multipart/form-data; boundary=").Append(Boundary).Append("\n\n");
                foreach ((string name, JsonElement field) in Fields(document, multipart))
                {
                    written.Append("--").Append(Boundary).Append('\n');
                    if (IsFile(document, field))
                    {
                        written.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"; filename=\"").Append(name).Append(".txt\"\n")
                            .Append("Content-Type: text/plain\n\n").Append("The file's text\n");
                    }
                    else
                    {
                        written.Append("Content-Disposition: form-data; name=\"").Append(name).Append("\"\n\n")
                            .Append(OpenApiSchemas.Sample(document, field) is { } sampled ? TextOf(sampled) : string.Empty).Append('\n');
                    }
                }
                written.Append("--").Append(Boundary).Append("--\n");
            }
            written.Append("\n###\n");
        }
        return written.ToString();
    }

    /// <summary>The operations of a document that none of a file's requests calls yet.</summary>
    public static IReadOnlyList<OpenApiOperation> Uncalled(OpenApiDocument document, string existing)
    {
        IReadOnlyList<EndpointRequest> requests = HttpFile.Parse(string.Empty, existing).Requests;
        return [.. document.Operations.Where(operation => !requests.Any(request => request.Method.Equals(operation.Method, StringComparison.OrdinalIgnoreCase)
            && RoutePath(request.Url) is { } path && document.OperationFor(operation.Method, path) == operation))];
    }

    /// <summary>The path a request's URL asks for, after its leading <c>{{variable}}</c> or <c>scheme://host</c>.</summary>
    public static string? RoutePath(string url)
    {
        string rest = url.Trim();
        if (rest.StartsWith("{{", StringComparison.Ordinal))
        {
            int close = rest.IndexOf("}}", StringComparison.Ordinal);
            return close < 0 ? null : "/" + rest[(close + 2)..].TrimStart('/');
        }
        if (rest.IndexOf("://", StringComparison.Ordinal) is int scheme and > 0)
        {
            int slash = rest.IndexOf('/', scheme + 3);
            return slash < 0 ? "/" : rest[slash..];
        }
        return rest.StartsWith('/') ? rest : null;
    }

    /// <summary>A parameter's value, made from its schema: a string as it is, an array's first item (a query parameter's
    /// array is the parameter written again for each), anything else as JSON writes it.</summary>
    private static string ValueOf(OpenApiDocument document, OpenApiParameter parameter) =>
        parameter.Schema is { } schema && OpenApiSchemas.Sample(document, schema) is { } made
            ? TextOf(made is JsonArray { Count: > 0 } items ? items[0] : made)
            : parameter.Name;

    /// <summary>The boundary between a multipart body's parts.</summary>
    private const string Boundary = "passenger";

    /// <summary>An object schema's fields in order, its <c>allOf</c> parts' too.</summary>
    private static IEnumerable<(string Name, JsonElement Schema)> Fields(OpenApiDocument document, JsonElement written)
    {
        JsonElement schema = document.Resolve(written);
        foreach (JsonElement part in JsonMembers.Items(schema, "allOf"))
        {
            foreach ((string Name, JsonElement Schema) field in Fields(document, part))
            {
                yield return field;
            }
        }
        foreach (JsonProperty property in JsonMembers.Properties(schema, "properties"))
        {
            yield return (property.Name, property.Value);
        }
    }

    /// <summary>Whether a field is a file (a string of binary format, as IFormFile's schema is), or files.</summary>
    private static bool IsFile(OpenApiDocument document, JsonElement written)
    {
        JsonElement schema = document.Resolve(written);
        return JsonMembers.Text(schema, "format") == "binary"
            || JsonMembers.Object(schema, "items") is { } items && JsonMembers.Text(document.Resolve(items), "format") == "binary";
    }

    /// <summary>A sample's value as a URL or form writes it: a string as it is, anything else as JSON writes it.</summary>
    private static string TextOf(JsonNode? made) => made is JsonValue value && value.TryGetValue(out string? text) ? text : made?.ToJsonString() ?? string.Empty;

    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex Braced();
}
