namespace Passenger.App.Endpoints;

/// <summary>A request written in an .http or .rest file: its method, URL, headers and body as written,
/// <c>{{variables}}</c> still in them (<see cref="HttpVariables"/> resolves them when it is sent), and the criteria its
/// response is held to (<c># @expect</c> lines). <see cref="Line"/> is its request line's, from 0.</summary>
public sealed record EndpointRequest(
    string File,
    int Line,
    string Name,
    string Method,
    string Url,
    IReadOnlyList<KeyValuePair<string, string>> Headers,
    string Body,
    IReadOnlyList<Expectation> Expectations)
{
    /// <summary>What a row shows: the request's name, else its URL.</summary>
    public string Title => Name.Length > 0 ? Name : Url;
}

/// <summary>A line of an .http or .rest file that does not read as what it says (an <c># @expect</c> that is no criterion):
/// its line and the columns of what does not read, from 0, and why.</summary>
public sealed record HttpFileProblem(int Line, int Column, int Length, string Why);

/// <summary>An .http or .rest file read: its requests in order and the variables it sets (<c>@host = localhost</c>).
/// What the file cannot mean (a block with no request line) is passed over; a criterion that does not read is one of its
/// <see cref="Problems"/>, and its request's criterion still, which fails.</summary>
public sealed record HttpFile(string Path, IReadOnlyDictionary<string, string> Variables, IReadOnlyList<EndpointRequest> Requests)
{
    public IReadOnlyList<HttpFileProblem> Problems { get; init; } = [];

    /// <summary>Whether a file is one of HTTP requests, by its extension.</summary>
    public static bool Is(string path) =>
        System.IO.Path.GetExtension(path) is { } extension
        && (extension.Equals(".http", StringComparison.OrdinalIgnoreCase) || extension.Equals(".rest", StringComparison.OrdinalIgnoreCase));

    /// <summary>The methods offered by name, QUERY (RFC 9110's safe method with a body) among them.</summary>
    public static readonly IReadOnlyList<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE", "QUERY", "HEAD", "OPTIONS", "TRACE", "CONNECT"];

    /// <summary>A request line's first word read as its method: one of <see cref="Methods"/> in any case, as that one is
    /// named, or another written as a method is (capitals and hyphens: PROPFIND) as written; null for no method.</summary>
    public static string? MethodOf(string word) =>
        Methods.FirstOrDefault(m => m.Equals(word, StringComparison.OrdinalIgnoreCase))
        ?? (word is [>= 'A' and <= 'Z', ..] && word.All(c => char.IsAsciiLetterUpper(c) || c == '-') ? word : null);

    public static HttpFile Read(string path) => Parse(path, File.ReadAllText(path));

    public static HttpFile Parse(string path, string text)
    {
        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        Dictionary<string, string> variables = new(StringComparer.Ordinal);
        List<EndpointRequest> requests = [];
        List<HttpFileProblem> problems = [];
        int start = 0;
        string separatorName = string.Empty;
        for (int i = 0; i <= lines.Length; i++)
        {
            if (i == lines.Length || lines[i].StartsWith("###", StringComparison.Ordinal))
            {
                if (Block(path, lines, start, i, separatorName, variables, problems) is { } request)
                {
                    requests.Add(request);
                }
                if (i < lines.Length)
                {
                    separatorName = lines[i][3..].Trim();
                    start = i + 1;
                }
            }
        }
        return new HttpFile(path, variables, requests) { Problems = problems };
    }

    /// <summary>One block between separators: comments, variables and criteria, the request line and the URL's
    /// continuation lines, headers up to a blank line, then the body, less any response handler after it.</summary>
    private static EndpointRequest? Block(string path, string[] lines, int from, int to, string name, Dictionary<string, string> variables,
        List<HttpFileProblem> problems)
    {
        List<Expectation> expectations = [];
        int i = from;
        for (; i < to; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (Comment(line) is { } comment)
            {
                if (Tagged(comment, "@name") is { } named)
                {
                    name = named;
                }
                else if (Tagged(comment, "@expect") is { } expected)
                {
                    Expectation expectation = Expectation.Read(expected);
                    expectations.Add(expectation);
                    if (expectation.Malformed is { } why)
                    {
                        problems.Add(new HttpFileProblem(i, lines[i].TrimEnd().Length - expected.Length, expected.Length, $"No criterion, so the request fails its check: {why}"));
                    }
                }
                continue;
            }
            if (line.StartsWith('@') && line.IndexOf('=') is > 1 and int equals)
            {
                variables[line[1..equals].Trim()] = line[(equals + 1)..].Trim();
                continue;
            }
            break;
        }
        if (i >= to)
        {
            return null;
        }
        int requestLine = i;
        (string method, string url) = RequestLine(lines[i].Trim());
        i++;
        // A long URL goes on over lines that start with whitespace and a ? or &.
        for (; i < to && lines[i].Length > 0 && char.IsWhiteSpace(lines[i][0]) && lines[i].TrimStart() is ['?' or '&', ..] more; i++)
        {
            url += more.Trim();
        }
        List<KeyValuePair<string, string>> headers = [];
        for (; i < to && lines[i].Trim().Length > 0; i++)
        {
            string line = lines[i].Trim();
            if (Comment(line) is not null)
            {
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
        }
        List<string> body = [];
        bool script = false;
        for (i++; i < to; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();
            if (script)
            {
                script = !trimmed.EndsWith("%}", StringComparison.Ordinal);
                continue;
            }
            // A response handler (> {% … %} or > file.js) and a response reference (<> file) are no part of the body.
            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed.StartsWith(">{%", StringComparison.Ordinal) || trimmed.StartsWith("<> ", StringComparison.Ordinal))
            {
                script = trimmed.Contains("{%", StringComparison.Ordinal) && !trimmed.EndsWith("%}", StringComparison.Ordinal);
                continue;
            }
            body.Add(line);
        }
        while (body.Count > 0 && body[^1].Trim().Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }
        return new EndpointRequest(path, requestLine, name, method, url, headers, string.Join("\n", body), expectations);
    }

    /// <summary>"POST https://x/y HTTP/1.1", or a URL alone, which is a GET.</summary>
    internal static (string Method, string Url) RequestLine(string line)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int first = 0;
        string method = "GET";
        if (parts.Length > 1 && MethodOf(parts[0]) is { } written)
        {
            method = written;
            first = 1;
        }
        int last = parts.Length;
        if (last - first > 1 && parts[^1].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
        {
            last--;
        }
        return (method, string.Join(" ", parts[first..last]));
    }

    private static string? Comment(string line) =>
        line.StartsWith('#') ? line[1..].Trim() : line.StartsWith("//", StringComparison.Ordinal) ? line[2..].Trim() : null;

    private static string? Tagged(string comment, string tag) =>
        comment.StartsWith(tag, StringComparison.Ordinal) && (comment.Length == tag.Length || char.IsWhiteSpace(comment[tag.Length]))
            ? comment[tag.Length..].Trim()
            : null;
}
