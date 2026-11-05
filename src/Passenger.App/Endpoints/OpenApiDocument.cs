using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Passenger.Core;

namespace Passenger.App.Endpoints;

/// <summary>A parameter of an operation: its name, where it goes (path, query, header, cookie) and its schema.</summary>
public sealed record OpenApiParameter(string Name, string In, JsonElement? Schema, bool Required);

/// <summary>An operation of an app's OpenAPI document: its method and path template, its id and summary, its parameters,
/// the schema of its JSON request body, and the schema of each response's JSON body by its status (or <c>default</c>).</summary>
public sealed record OpenApiOperation(string Method, string Path, string? OperationId, string? Summary, IReadOnlyList<OpenApiParameter> Parameters,
    JsonElement? RequestSchema, IReadOnlyDictionary<string, JsonElement> Responses)
{
    /// <summary>The schema of its form body (<c>application/x-www-form-urlencoded</c>), where it takes one.</summary>
    public JsonElement? FormSchema { get; init; }

    /// <summary>The schema of its multipart body (<c>multipart/form-data</c>: a file's upload), where it takes one.</summary>
    public JsonElement? MultipartSchema { get; init; }

    /// <summary>The operation's title: "GET /items/{id}".</summary>
    public string Title => $"{Method} {Path}";

    /// <summary>Its path as a request's is held to it, segment by segment (<see cref="OpenApiDocument.OperationFor"/>).</summary>
    internal string[] Shape { get; } = OpenApiDocument.TemplateShape(Path);

    /// <summary>The schema a response of this status is held to: its own, its class's (<c>2XX</c>), else <c>default</c>'s;
    /// null where the document gives no JSON body for it.</summary>
    public JsonElement? ResponseSchema(int status)
    {
        string code = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Responses.TryGetValue(code, out JsonElement own) ? own
            : Responses.TryGetValue($"{code[0]}XX", out JsonElement range) ? range
            : Responses.TryGetValue("default", out JsonElement fallback) ? fallback
            : null;
    }
}

/// <summary>The parts of an app's OpenAPI document (3.0 or 3.1, and Swagger 2.0's) the Endpoints window reads: its operations,
/// and its schemas as written, a <c>$ref</c> followed into <c>components/schemas</c> (<c>definitions</c> in 2.0). Read as any
/// other program's JSON (<see cref="JsonMembers"/>), so a member of an unexpected kind is passed over, never thrown on.</summary>
public sealed partial class OpenApiDocument
{
    private static readonly string[] Methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    /// <summary>In a segment's shape, the first character of what a <c>{name}</c> or a <c>{{variable}}</c> holds, which
    /// takes at least one.</summary>
    private const char AnyOne = '\uFFFE';

    /// <summary>In a segment's shape, what else a <c>{name}</c> or a <c>{{variable}}</c> holds, of any length.</summary>
    private const char AnyMore = '\uFFFF';

    private readonly JsonElement root;

    private OpenApiDocument(JsonElement root)
    {
        this.root = root;
        Title = JsonMembers.Text(JsonMembers.Object(root, "info"), "title");
        List<OpenApiOperation> operations = [];
        foreach (JsonProperty path in JsonMembers.Properties(root, "paths"))
        {
            IReadOnlyList<JsonElement> shared = JsonMembers.Items(path.Value, "parameters");
            foreach (JsonProperty member in path.Value.ValueKind == JsonValueKind.Object ? path.Value.EnumerateObject() : Enumerable.Empty<JsonProperty>())
            {
                if (Methods.Contains(member.Name, StringComparer.OrdinalIgnoreCase) && member.Value.ValueKind == JsonValueKind.Object)
                {
                    operations.Add(Operation(member.Name.ToUpperInvariant(), path.Name, member.Value, shared));
                }
            }
        }
        Operations = operations;
    }

    /// <summary>The document's title, its <c>info.title</c>.</summary>
    public string? Title { get; }

    /// <summary>Its operations, in the order it lists them.</summary>
    public IReadOnlyList<OpenApiOperation> Operations { get; }

    /// <summary>A document read from its JSON text; null for text that is no JSON object naming <c>openapi</c> or <c>swagger</c>
    /// with its <c>paths</c>.</summary>
    public static OpenApiDocument? Parse(string json)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = read.RootElement;
            return (JsonMembers.Text(root, "openapi") ?? JsonMembers.Text(root, "swagger")) is not null && JsonMembers.Object(root, "paths") is not null
                ? new OpenApiDocument(root.Clone())
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The operation a request calls: of its method, its path fitting the operation's template, the text around each
    /// <c>{name}</c> alike in any case and the <c>{name}</c> taking what lies between (<c>/files/{id}.json</c> takes
    /// <c>/files/a.json</c>), a request's <c>{{variable}}</c> as much. Of several, the template shaped as the path is (the one
    /// a request was made from), then the one with fewer parameters, then with more text of its own; null where none fits.</summary>
    public OpenApiOperation? OperationFor(string method, string path)
    {
        string[] asked = [.. Segments(path).Select(segment => Shaped(segment, Variable(), Uri.UnescapeDataString))];
        return Operations.Where(o => string.Equals(o.Method, method, StringComparison.OrdinalIgnoreCase) && Fits(o.Shape, asked))
            .OrderBy(o => !o.Shape.SequenceEqual(asked, StringComparer.OrdinalIgnoreCase))
            .ThenBy(o => o.Path.Count(c => c == '{'))
            .ThenByDescending(o => o.Shape.Sum(segment => segment.Length))
            .FirstOrDefault();
    }

    /// <summary>An operation's path as <see cref="OperationFor"/> holds a request's to it.</summary>
    internal static string[] TemplateShape(string path) => [.. Segments(path).Select(segment => Shaped(segment, RouteRequests.Parameter(), text => text))];

    /// <summary>A schema with its <c>$ref</c> followed, as far as the document's own schemas go (a chain of them too); the
    /// schema as it is where its reference names none of them.</summary>
    public JsonElement Resolve(JsonElement schema)
    {
        for (int depth = 0; depth < 32 && JsonMembers.Text(schema, "$ref") is { } reference; depth++)
        {
            if (Named(reference) is not { } found)
            {
                return schema;
            }
            schema = found;
        }
        return schema;
    }

    /// <summary>The schema a local reference names (<c>#/components/schemas/Item</c>, <c>#/definitions/Item</c>), its
    /// pointer's escapes read; null for any other.</summary>
    private JsonElement? Named(string reference)
    {
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }
        JsonElement? at = root;
        foreach (string part in reference[2..].Split('/'))
        {
            if ((at = JsonMembers.Object(at, part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal))) is null)
            {
                return null;
            }
        }
        return at;
    }

    private OpenApiOperation Operation(string method, string path, JsonElement operation, IReadOnlyList<JsonElement> shared)
    {
        List<OpenApiParameter> parameters = [];
        JsonElement? bodyIn2 = null;
        foreach (JsonElement written in shared.Concat(JsonMembers.Items(operation, "parameters")))
        {
            JsonElement parameter = Resolve(written);
            if (JsonMembers.Text(parameter, "name") is not { } name || JsonMembers.Text(parameter, "in") is not { } where)
            {
                continue;
            }
            if (where == "body")
            {
                bodyIn2 = JsonMembers.Object(parameter, "schema");
                continue;
            }
            parameters.RemoveAll(p => p.Name == name && p.In == where);
            parameters.Add(new OpenApiParameter(name, where, JsonMembers.Object(parameter, "schema") ?? (JsonMembers.Text(parameter, "type") is not null ? parameter : null),
                JsonMembers.Bool(parameter, "required") == true || where == "path"));
        }
        JsonElement requestBody = Resolve(JsonMembers.Object(operation, "requestBody") ?? default);
        JsonElement? request = JsonOf(requestBody) ?? bodyIn2;
        Dictionary<string, JsonElement> responses = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty response in JsonMembers.Properties(operation, "responses"))
        {
            JsonElement written = Resolve(response.Value);
            if ((JsonOf(written) ?? JsonMembers.Object(written, "schema")) is { } schema)
            {
                responses[response.Name] = schema;
            }
        }
        return new OpenApiOperation(method, path, JsonMembers.Text(operation, "operationId"), JsonMembers.Text(operation, "summary"), parameters, request, responses)
        {
            FormSchema = JsonMembers.Properties(requestBody, "content").Where(c => c.Name.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                .Select(c => JsonMembers.Object(c.Value, "schema")).FirstOrDefault(),
            MultipartSchema = JsonMembers.Properties(requestBody, "content").Where(c => c.Name.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase))
                .Select(c => JsonMembers.Object(c.Value, "schema")).FirstOrDefault(),
        };
    }

    /// <summary>The schema of a request's or response's JSON body: its <c>application/json</c> content's, else that of the first
    /// content type that is JSON (<c>+json</c>, <c>text/json</c>).</summary>
    private static JsonElement? JsonOf(JsonElement holder)
    {
        IReadOnlyList<JsonProperty> content = JsonMembers.Properties(holder, "content");
        int at = Index(c => c.Name.StartsWith("application/json", StringComparison.OrdinalIgnoreCase));
        at = at >= 0 ? at : Index(c => c.Name.Contains("json", StringComparison.OrdinalIgnoreCase));
        return at >= 0 ? JsonMembers.Object(content[at].Value, "schema") : null;

        int Index(Func<JsonProperty, bool> which)
        {
            for (int i = 0; i < content.Count; i++)
            {
                if (which(content[i]))
                {
                    return i;
                }
            }
            return -1;
        }
    }

    private static string[] Segments(string path)
    {
        int end = path.IndexOfAny(['?', '#']);
        return (end < 0 ? path : path[..end]).Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>A segment with what stands for a value, as <paramref name="stands"/> finds it, in <see cref="AnyOne"/> and
    /// <see cref="AnyMore"/>, and its text around that as <paramref name="read"/> reads it.</summary>
    private static string Shaped(string segment, Regex stands, Func<string, string> read)
    {
        StringBuilder shape = new();
        int at = 0;
        foreach (Match value in stands.Matches(segment))
        {
            shape.Append(read(segment[at..value.Index])).Append(AnyOne).Append(AnyMore);
            at = value.Index + value.Length;
        }
        return shape.Append(read(segment[at..])).ToString();
    }

    /// <summary>Whether a path's segments fit a template's: as many, each of the two able to be one text.</summary>
    private static bool Fits(string[] template, string[] path)
    {
        if (template.Length != path.Length)
        {
            return false;
        }
        for (int i = 0; i < template.Length; i++)
        {
            if (!Meet(template[i], path[i]))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Whether two segments' shapes can be one text, either holding values, as a request's may: every pair of places
    /// in them the same text can reach, from their starts to their ends.</summary>
    private static bool Meet(string one, string other)
    {
        if (!one.Contains(AnyMore, StringComparison.Ordinal) && !other.Contains(AnyMore, StringComparison.Ordinal))
        {
            return string.Equals(one, other, StringComparison.OrdinalIgnoreCase);
        }
        bool[,] reached = new bool[one.Length + 1, other.Length + 1];
        reached[0, 0] = true;
        for (int i = 0; i <= one.Length; i++)
        {
            for (int j = 0; j <= other.Length; j++)
            {
                if (!reached[i, j])
                {
                    continue;
                }
                bool moreHere = i < one.Length && one[i] == AnyMore;
                bool moreThere = j < other.Length && other[j] == AnyMore;
                if (moreHere)
                {
                    reached[i + 1, j] = true;
                    reached[i, Math.Min(j + 1, other.Length)] = true;
                }
                if (moreThere)
                {
                    reached[i, j + 1] = true;
                    reached[Math.Min(i + 1, one.Length), j] = true;
                }
                if (!moreHere && !moreThere && i < one.Length && j < other.Length && SameCharacter(one[i], other[j]))
                {
                    reached[i + 1, j + 1] = true;
                }
            }
        }
        return reached[one.Length, other.Length];
    }

    private static bool SameCharacter(char one, char other) =>
        one == AnyOne || other == AnyOne || char.ToUpperInvariant(one) == char.ToUpperInvariant(other);

    /// <summary>A request's <c>{{variable}}</c>, to the end of its segment where it is left open.</summary>
    [GeneratedRegex(@"\{\{.*?(?:\}\}|$)")]
    private static partial Regex Variable();
}
