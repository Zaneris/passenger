using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Passenger.UnitTests.Schemas;

/// <summary>The JSON schemas Passenger carries under web/schemas, compiled in, and a check of a value against a part of one
/// by the keywords of JSON Schema's drafts 7 and 2020-12 that judge a value: a <c>$ref</c> followed within its schema and
/// to another carried one by its file's name, its neighbours applying too, a <c>pattern</c>'s leading <c>(?i)</c> ignoring
/// case, and <c>format</c> passed over. A keyword it does not know throws, so nothing passes
/// unjudged.</summary>
internal static class CarriedSchemas
{
    /// <summary>The carried schemas by file name, each read once.</summary>
    public static IReadOnlyDictionary<string, JsonElement> All { get; } = Read();

    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    /// <summary>Keywords that say something of a value without judging it.</summary>
    private static readonly HashSet<string> Annotations = new(StringComparer.Ordinal)
    {
        "$schema", "$id", "id", "$comment", "$defs", "definitions", "title", "description", "markdownDescription", "default", "examples",
        "deprecated", "deprecationMessage", "markdownDeprecationMessage", "enumDescriptions", "markdownEnumDescriptions", "defaultSnippets",
        "doNotSuggest", "errorMessage", "patternErrorMessage", "readOnly", "writeOnly", "format", "tsType", "contentMediaType", "contentEncoding",
    };

    /// <summary>Whether the value is one the schema part takes: the reasons it is not, empty where it is.</summary>
    public static List<string> Refusals(JsonElement value, string file, JsonElement schema)
    {
        List<string> refused = [];
        Check(value, file, schema, "", refused);
        return refused;
    }

    /// <summary>The part of a carried schema a reference names: <c>#/pointer</c> in the same file, else another by its name
    /// (SchemaStore's URL or the file's own) with a pointer into it.</summary>
    public static (string File, JsonElement Schema) Resolve(string file, string reference)
    {
        int hash = reference.IndexOf('#');
        string target = hash < 0 ? reference : reference[..hash];
        string pointer = hash < 0 ? "" : Uri.UnescapeDataString(reference[(hash + 1)..]);
        if (target.Length > 0)
        {
            file = target[(target.LastIndexOf('/') + 1)..];
            file = file.EndsWith(".json", StringComparison.Ordinal) ? file : file + ".json";
        }
        if (!All.TryGetValue(file, out JsonElement node))
        {
            throw new InvalidOperationException($"'{reference}' names {file}, which is not carried");
        }
        foreach (string part in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string name = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            node = node.ValueKind == JsonValueKind.Array ? node[int.Parse(name, System.Globalization.CultureInfo.InvariantCulture)] : node.GetProperty(name);
        }
        return (file, node);
    }

    /// <summary>Every part of a carried schema that is a schema, with its JSON pointer: the schema itself, then what its
    /// keywords hold, never inside a value (a default, an enum, an example).</summary>
    public static IEnumerable<(string Pointer, JsonElement Schema)> Parts(string file)
    {
        Stack<(string, JsonElement)> walking = new([("", All[file])]);
        while (walking.TryPop(out (string Pointer, JsonElement Schema) part))
        {
            if (part.Schema.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            yield return part;
            foreach (JsonProperty keyword in part.Schema.EnumerateObject())
            {
                string at = $"{part.Pointer}/{keyword.Name}";
                switch (keyword.Name)
                {
                    case "properties" or "patternProperties" or "definitions" or "$defs" or "dependencies" or "dependentSchemas":
                        foreach (JsonProperty named in keyword.Value.EnumerateObject())
                        {
                            walking.Push(($"{at}/{named.Name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)}", named.Value));
                        }
                        break;
                    case "anyOf" or "oneOf" or "allOf" or "prefixItems" or "items" when keyword.Value.ValueKind == JsonValueKind.Array:
                        int i = 0;
                        foreach (JsonElement branch in keyword.Value.EnumerateArray())
                        {
                            walking.Push(($"{at}/{i++}", branch));
                        }
                        break;
                    case "items" or "additionalItems" or "additionalProperties" or "contains" or "propertyNames" or "not" or "if" or "then" or "else":
                        walking.Push((at, keyword.Value));
                        break;
                }
            }
        }
    }

    private static Dictionary<string, JsonElement> Read()
    {
        Dictionary<string, JsonElement> read = new(StringComparer.Ordinal);
        foreach (string name in typeof(CarriedSchemas).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("schemas/", StringComparison.Ordinal)))
        {
            using Stream stream = typeof(CarriedSchemas).Assembly.GetManifestResourceStream(name)!;
            read[name["schemas/".Length..]] = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }).RootElement.Clone();
        }
        return read;
    }

    private static void Check(JsonElement value, string file, JsonElement schema, string at, List<string> refused)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }
        if (schema.ValueKind == JsonValueKind.False)
        {
            refused.Add($"{At(at)}: the schema takes nothing here");
            return;
        }
        foreach (JsonProperty keyword in schema.EnumerateObject())
        {
            JsonElement rule = keyword.Value;
            switch (keyword.Name)
            {
                case "$ref":
                    (string inFile, JsonElement target) = Resolve(file, rule.GetString()!);
                    Check(value, inFile, target, at, refused);
                    break;
                case "type":
                    string[] types = rule.ValueKind == JsonValueKind.Array ? [.. rule.EnumerateArray().Select(t => t.GetString()!)] : [rule.GetString()!];
                    if (!types.Any(t => IsOfType(value, t)))
                    {
                        refused.Add($"{At(at)}: {value.ValueKind} where the schema takes {string.Join(" or ", types)}");
                    }
                    break;
                case "enum":
                    if (!rule.EnumerateArray().Any(e => JsonElement.DeepEquals(e, value)))
                    {
                        refused.Add($"{At(at)}: {value.GetRawText()} is none of {rule.GetRawText()}");
                    }
                    break;
                case "const":
                    if (!JsonElement.DeepEquals(rule, value))
                    {
                        refused.Add($"{At(at)}: {value.GetRawText()} is not {rule.GetRawText()}");
                    }
                    break;
                case "pattern":
                    if (value.ValueKind == JsonValueKind.String && !Pattern(rule.GetString()!).IsMatch(value.GetString()!))
                    {
                        refused.Add($"{At(at)}: {value.GetRawText()} does not match {rule.GetString()}");
                    }
                    break;
                case "minLength" or "maxLength":
                    if (value.ValueKind == JsonValueKind.String && !Within(value.GetString()!.Length, keyword.Name, rule))
                    {
                        refused.Add($"{At(at)}: {value.GetRawText()} breaks {keyword.Name} {rule}");
                    }
                    break;
                case "minimum" or "maximum" or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf":
                    if (value.ValueKind == JsonValueKind.Number && !Within(value.GetDouble(), keyword.Name, rule))
                    {
                        refused.Add($"{At(at)}: {value} breaks {keyword.Name} {rule}");
                    }
                    break;
                case "minItems" or "maxItems":
                    if (value.ValueKind == JsonValueKind.Array && !Within(value.GetArrayLength(), keyword.Name, rule))
                    {
                        refused.Add($"{At(at)}: {value.GetArrayLength()} items break {keyword.Name} {rule}");
                    }
                    break;
                case "minProperties" or "maxProperties":
                    if (value.ValueKind == JsonValueKind.Object && !Within(value.EnumerateObject().Count(), keyword.Name, rule))
                    {
                        refused.Add($"{At(at)}: its properties break {keyword.Name} {rule}");
                    }
                    break;
                case "uniqueItems":
                    if (value.ValueKind == JsonValueKind.Array && rule.ValueKind == JsonValueKind.True)
                    {
                        JsonElement[] items = [.. value.EnumerateArray()];
                        if (items.Where((item, i) => items.Take(i).Any(earlier => JsonElement.DeepEquals(earlier, item))).Any())
                        {
                            refused.Add($"{At(at)}: its items are not unique");
                        }
                    }
                    break;
                case "items" or "prefixItems" or "additionalItems" or "contains":
                    CheckItems(value, file, schema, keyword.Name, rule, at, refused);
                    break;
                case "required":
                    foreach (JsonElement name in rule.EnumerateArray())
                    {
                        if (value.ValueKind == JsonValueKind.Object && !value.TryGetProperty(name.GetString()!, out _))
                        {
                            refused.Add($"{At(at)}: no {name.GetString()}, which the schema requires");
                        }
                    }
                    break;
                case "properties" or "patternProperties" or "additionalProperties" or "propertyNames" or "dependencies" or "dependentRequired" or "dependentSchemas":
                    CheckProperties(value, file, schema, keyword.Name, rule, at, refused);
                    break;
                case "anyOf" or "oneOf":
                    int matched = rule.EnumerateArray().Count(branch => Refusals(value, file, branch).Count == 0);
                    if (matched == 0 || (keyword.Name == "oneOf" && matched > 1))
                    {
                        refused.Add($"{At(at)}: {matched} of its {keyword.Name} branches take {Shortened(value)}");
                    }
                    break;
                case "allOf":
                    foreach (JsonElement branch in rule.EnumerateArray())
                    {
                        Check(value, file, branch, at, refused);
                    }
                    break;
                case "not":
                    if (Refusals(value, file, rule).Count == 0)
                    {
                        refused.Add($"{At(at)}: {Shortened(value)} is what the schema's not refuses");
                    }
                    break;
                case "if":
                    bool holds = Refusals(value, file, rule).Count == 0;
                    if (schema.TryGetProperty(holds ? "then" : "else", out JsonElement branchTaken))
                    {
                        Check(value, file, branchTaken, at, refused);
                    }
                    break;
                case "then" or "else":
                    break;
                default:
                    if (!Annotations.Contains(keyword.Name) && !keyword.Name.StartsWith("x-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"{file} uses {keyword.Name}, a keyword this check does not judge");
                    }
                    break;
            }
        }
    }

    private static void CheckItems(JsonElement value, string file, JsonElement schema, string keyword, JsonElement rule, string at, List<string> refused)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        JsonElement[] items = [.. value.EnumerateArray()];
        switch (keyword)
        {
            case "items" when rule.ValueKind == JsonValueKind.Array:
            case "prefixItems":
                JsonElement[] tuple = [.. rule.EnumerateArray()];
                for (int i = 0; i < Math.Min(tuple.Length, items.Length); i++)
                {
                    Check(items[i], file, tuple[i], $"{at}/{i}", refused);
                }
                break;
            case "items":
                int first = schema.TryGetProperty("prefixItems", out JsonElement prefix) ? prefix.GetArrayLength() : 0;
                for (int i = first; i < items.Length; i++)
                {
                    Check(items[i], file, rule, $"{at}/{i}", refused);
                }
                break;
            case "additionalItems":
                if (schema.TryGetProperty("items", out JsonElement listed) && listed.ValueKind == JsonValueKind.Array)
                {
                    for (int i = listed.GetArrayLength(); i < items.Length; i++)
                    {
                        Check(items[i], file, rule, $"{at}/{i}", refused);
                    }
                }
                break;
            case "contains":
                if (!items.Any(item => Refusals(item, file, rule).Count == 0))
                {
                    refused.Add($"{At(at)}: no item is what its contains takes");
                }
                break;
        }
    }

    private static void CheckProperties(JsonElement value, string file, JsonElement schema, string keyword, JsonElement rule, string at, List<string> refused)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        foreach (JsonProperty property in value.EnumerateObject())
        {
            string inside = $"{at}/{property.Name}";
            switch (keyword)
            {
                case "properties":
                    if (rule.TryGetProperty(property.Name, out JsonElement named))
                    {
                        Check(property.Value, file, named, inside, refused);
                    }
                    break;
                case "patternProperties":
                    foreach (JsonProperty pattern in rule.EnumerateObject().Where(p => Pattern(p.Name).IsMatch(property.Name)))
                    {
                        Check(property.Value, file, pattern.Value, inside, refused);
                    }
                    break;
                case "additionalProperties":
                    bool listed = schema.TryGetProperty("properties", out JsonElement properties) && properties.TryGetProperty(property.Name, out _);
                    bool matched = schema.TryGetProperty("patternProperties", out JsonElement patterns) && patterns.EnumerateObject().Any(p => Pattern(p.Name).IsMatch(property.Name));
                    if (!listed && !matched)
                    {
                        Check(property.Value, file, rule, inside, refused);
                    }
                    break;
                case "propertyNames":
                    using (JsonDocument name = JsonDocument.Parse(JsonSerializer.Serialize(property.Name)))
                    {
                        Check(name.RootElement, file, rule, inside, refused);
                    }
                    break;
                case "dependencies" or "dependentRequired" or "dependentSchemas":
                    if (!rule.TryGetProperty(property.Name, out JsonElement depends))
                    {
                        break;
                    }
                    if (depends.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement needed in depends.EnumerateArray().Where(n => !value.TryGetProperty(n.GetString()!, out _)))
                        {
                            refused.Add($"{At(at)}: {property.Name} without {needed.GetString()}, which it depends on");
                        }
                    }
                    else
                    {
                        Check(value, file, depends, at, refused);
                    }
                    break;
            }
        }
    }

    private static bool IsOfType(JsonElement value, string type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && Math.Floor(value.GetDouble()) == value.GetDouble(),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException($"'{type}' is no JSON type"),
    };

    private static bool Within(double measured, string keyword, JsonElement rule)
    {
        double limit = rule.GetDouble();
        return keyword switch
        {
            "minLength" or "minItems" or "minProperties" or "minimum" => measured >= limit,
            "maxLength" or "maxItems" or "maxProperties" or "maximum" => measured <= limit,
            "exclusiveMinimum" => measured > limit,
            "exclusiveMaximum" => measured < limit,
            _ => Math.Abs(Math.IEEERemainder(measured, limit)) < 1e-9,
        };
    }

    /// <summary>A schema's pattern, made once, a leading <c>(?i)</c> making it ignore case.</summary>
    private static Regex Pattern(string pattern) => Patterns.GetOrAdd(pattern, p => new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)));

    private static string At(string at) => at.Length == 0 ? "the value" : at;

    private static string Shortened(JsonElement value)
    {
        string text = value.GetRawText();
        return text.Length > 60 ? text[..60] + "…" : text;
    }
}
