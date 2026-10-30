using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Passenger.Core;

namespace Passenger.App.Endpoints;

/// <summary>What a request takes from the person's computer beyond the open folder, which the folder must allow before
/// it goes: an environment variable (<see cref="Missing"/> when it is not set, so it goes empty) or a file outside the
/// folder.</summary>
public sealed record EndpointRead(bool IsFile, string Name, bool Missing = false)
{
    /// <summary>The read allowed for a host, as the folder's state keeps it: "localhost:5000 env API_TOKEN".</summary>
    public string Key(string host) => $"{host} {(IsFile ? "file" : "env")} {Name}";

    /// <summary>What the question says of it.</summary>
    public override string ToString() => IsFile ? $"the file {Name}"
        : Missing ? $"the environment variable {Name}, which is not set, so it goes empty" : $"the environment variable {Name}";
}

/// <summary>A request ready to send: every variable resolved, and the file its body is (<c>&lt; file</c>), read only as it
/// is sent; what it reads from the person's computer to get there; and the host it goes to.</summary>
public sealed record EndpointCall(string Method, string Url, IReadOnlyList<KeyValuePair<string, string>> Headers, string Body)
{
    /// <summary>The file whose text is the body, read as it is sent, or null when <see cref="Body"/> is it.</summary>
    public string? BodyFile { get; init; }

    public IReadOnlyList<EndpointRead> Reads { get; init; } = [];

    /// <summary>Where it goes: the URL's host and port, as the question names it and the folder's state keeps it.</summary>
    public string Host => new Uri(Url).Authority;
}

/// <summary>How a test sends: how long it waits, whether it follows a redirect, and whether it takes a certificate the
/// system does not trust (a development server's own).</summary>
public sealed record SendOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public bool FollowRedirects { get; init; } = true;
    public bool AcceptAnyCertificate { get; init; }
}

/// <summary>What came back, or why nothing did (<see cref="Error"/>); headers are the response's and its content's.</summary>
public sealed record EndpointResponse
{
    public int Status { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
    public string Body { get; init; } = string.Empty;
    public long Size { get; init; }

    /// <summary>Whether the body is only the first <see cref="EndpointClient.MostRead"/> bytes of a longer answer.</summary>
    public bool Cut { get; init; }

    /// <summary>What the person should know of how it came (a redirect followed without the request's headers, or not
    /// followed), one line each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }

    public string? ContentType => Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>The most of a body shown, in characters: a text box laid out over more holds the UI thread up.</summary>
    public const int MostShown = 256 * 1024;

    /// <summary>The body as it reads best: JSON indented, anything else as it came; a longer one's first
    /// <see cref="MostShown"/> characters as they came, saying how much more there is.</summary>
    public string Pretty
    {
        get
        {
            if (Body.Length > MostShown)
            {
                return $"{Body[..MostShown]}\n… {Body.Length - MostShown:N0} more characters not shown";
            }
            string trimmed = Body.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    using JsonDocument document = JsonDocument.Parse(Body);
                    return JsonSerializer.Serialize(document.RootElement, Indented);
                }
                catch (JsonException)
                {
                }
            }
            return Body;
        }
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}

/// <summary>Sends one request on a client of its own: each test makes its handler and <see cref="HttpClient"/>, sends,
/// reads the whole response and disposes both before it answers, so no connection, cookie or pooled socket outlives
/// the test and one test never sees another's state.</summary>
public static class EndpointClient
{
    /// <summary>The most of an answer's body read, decompressed: the rest is left unread, so a huge, endless or
    /// highly compressed answer neither fills memory nor holds the test.</summary>
    public const int MostRead = 16 * 1024 * 1024;

    /// <summary>The most of a body's file sent, in characters, so a device or a disk image named as one is refused.</summary>
    public const int MostSent = 16 * 1024 * 1024;

    /// <summary>The most redirects followed, as a browser stops after twenty.</summary>
    public const int MostRedirects = 20;

    /// <summary>The headers .NET keeps on a request's content rather than on the request.</summary>
    private static readonly HashSet<string> ContentHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Allow", "Content-Disposition", "Content-Encoding", "Content-Language", "Content-Length", "Content-Location",
        "Content-MD5", "Content-Range", "Content-Type", "Expires", "Last-Modified",
    };

    /// <summary>A request of a file made ready in an environment's variables, or why it cannot be sent; what it reads of
    /// the person's environment, and a body's file outside <paramref name="root"/>, are its <see cref="EndpointCall.Reads"/>.
    /// Reads env files' values already given and no file, so it can run anywhere.</summary>
    public static (EndpointCall? Call, string? Problem) Prepare(EndpointRequest request, string method, string url,
        IEnumerable<KeyValuePair<string, string>> headers, string body, IReadOnlyDictionary<string, string> variables, string? root)
    {
        HttpVariables.Filling filling = new();
        string resolvedUrl = HttpVariables.Resolve(url, variables, filling).Trim();
        List<KeyValuePair<string, string>> resolvedHeaders = [.. headers.Select(h => new KeyValuePair<string, string>(
            HttpVariables.Resolve(h.Key, variables, filling), HttpVariables.Resolve(h.Value, variables, filling)))];
        string resolvedBody = HttpVariables.Resolve(body, variables, filling);
        if (filling.Overflowed)
        {
            return (null, $"The request's variables fill in more than {HttpVariables.MostFilledIn:N0} characters: does one name another over and over?");
        }
        HashSet<string> unresolved = filling.Unresolved;
        if (unresolved.Count > 0)
        {
            return (null, $"No value for {string.Join(", ", unresolved.Select(u => "{{" + u + "}}"))}: set it in the file (@{unresolved.First()} = …) or in {HttpVariables.PublicFile}");
        }
        List<EndpointRead> reads = [.. filling.Environment.Select(name => new EndpointRead(IsFile: false, name, filling.Unset.Contains(name)))];
        // A body "< ./file.json" alone is that file's text; one outside the folder (a link's too) is the person's to allow.
        string? bodyFile = null;
        if (resolvedBody.Trim() is ['<', ' ', .. string included] && !included.Contains('\n'))
        {
            bodyFile = PathNames.Canonical(Path.GetFullPath(included.Trim(), Path.GetDirectoryName(request.File)!));
            if (root is null || !PathNames.IsUnder(bodyFile, PathNames.Canonical(root)))
            {
                reads.Add(new EndpointRead(IsFile: true, bodyFile));
            }
            resolvedBody = string.Empty;
        }
        if (!resolvedUrl.Contains("://", StringComparison.Ordinal))
        {
            resolvedUrl = "http://" + resolvedUrl;
        }
        if (!Uri.TryCreate(resolvedUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
        {
            return (null, $"\"{resolvedUrl}\" is no http or https URL");
        }
        return (new EndpointCall(method.Trim().ToUpperInvariant(), uri.AbsoluteUri, resolvedHeaders, resolvedBody) { BodyFile = bodyFile, Reads = reads }, null);
    }

    /// <summary>Sends the call, its body's file read first, within the timeout and the stop. A redirect is followed (when
    /// the options say so) with the request's headers only to the same origin (scheme, host and port): to another, it goes
    /// without them, and not at all when it would carry the body there; each such turn is one of the answer's
    /// <see cref="EndpointResponse.Notes"/>.</summary>
    public static async Task<EndpointResponse> SendAsync(EndpointCall call, SendOptions options, CancellationToken cancel)
    {
        Stopwatch clock = Stopwatch.StartNew();
        // The timeout holds the whole exchange, the body's file and the answer's reading included, which HttpClient's own does not.
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(options.Timeout);
        string body = call.Body;
        if (call.BodyFile is { } file)
        {
            // Not held, on a thread of its own: opening a FIFO nothing writes, or a file of a share that never answers, waits for good.
            Task<(string? Text, string? Error)> reading = BackgroundWork.UnheldThread(() => ReadText(file));
            try
            {
                (string? text, string? error) = await reading.WaitAsync(limit.Token).ConfigureAwait(false);
                if (text is null)
                {
                    return new EndpointResponse { Error = $"The body's file {file} could not be read: {error}", Elapsed = clock.Elapsed };
                }
                body = text;
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                return new EndpointResponse { Error = $"The body's file {file} was not read within {options.Timeout.TotalSeconds:0.#} s", Elapsed = clock.Elapsed };
            }
        }
        try
        {
            using SocketsHttpHandler handler = new()
            {
                // Followed here, since .NET's own takes every header but Authorization to whatever host it is sent to.
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = options.Timeout,
            };
            if (options.AcceptAnyCertificate)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }
            using HttpClient client = new(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            Uri first = new(call.Url);
            Uri at = first;
            string method = call.Method;
            bool withHeaders = true;
            List<string> notes = [];
            HttpResponseMessage response;
            for (int redirects = 0; ; redirects++)
            {
                (HttpRequestMessage? message, string? refused) = Message(method, at, withHeaders ? call.Headers : [], body);
                if (message is null)
                {
                    return new EndpointResponse { Error = refused, Elapsed = clock.Elapsed };
                }
                using (message)
                {
                    response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, limit.Token).ConfigureAwait(false);
                }
                if (!options.FollowRedirects || Redirect(response, at) is not { } next)
                {
                    break;
                }
                int status = (int)response.StatusCode;
                // A 303, and a 301 or 302 after a POST, goes on as a GET with no body; the rest keep both.
                bool asGet = status == 303 ? method != "HEAD" : status is 301 or 302 && method == "POST";
                // By origin, as what a request reads is allowed to one server: another port of the same host is another server.
                bool sameOrigin = next.Scheme == first.Scheme && next.Port == first.Port && string.Equals(next.Host, first.Host, StringComparison.OrdinalIgnoreCase);
                string? stop = redirects == MostRedirects ? $"Not followed to {next}: {MostRedirects} redirects were followed already"
                    : at.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps ? $"Not followed to {next}: it leaves HTTPS"
                    : !sameOrigin && !asGet && body.Length > 0 ? $"Not followed to {Origin(next)}: a {status} would send the request's body there"
                    : null;
                if (stop is not null)
                {
                    notes.Add(stop);
                    break;
                }
                if (!sameOrigin && withHeaders && call.Headers.Count > 0)
                {
                    notes.Add($"Redirected to {Origin(next)}, another origin, without the request's headers");
                    withHeaders = false;
                }
                if (asGet)
                {
                    method = "GET";
                    body = string.Empty;
                }
                response.Dispose();
                at = next;
            }
            using (response)
            {
                (byte[] bytes, bool cut) = await ReadAtMostAsync(response.Content, limit.Token).ConfigureAwait(false);
                return new EndpointResponse
                {
                    Status = (int)response.StatusCode,
                    Reason = response.ReasonPhrase ?? string.Empty,
                    Version = $"HTTP/{response.Version}",
                    Headers = [.. Flatten(response.Headers), .. Flatten(response.Content.Headers)],
                    Body = Decode(bytes, response.Content.Headers.ContentType),
                    Size = bytes.LongLength,
                    Cut = cut,
                    Notes = notes,
                    Elapsed = clock.Elapsed,
                };
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new EndpointResponse { Error = $"No answer within {options.Timeout.TotalSeconds:0.#} s", Elapsed = clock.Elapsed };
        }
        catch (HttpRequestException error)
        {
            return new EndpointResponse { Error = error.InnerException?.Message ?? error.Message, Elapsed = clock.Elapsed };
        }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or FormatException or IOException)
        {
            return new EndpointResponse { Error = error.Message, Elapsed = clock.Elapsed };
        }
    }

    /// <summary>A URL's scheme, host and port, as a note names where a redirect went; never its user name or password.</summary>
    private static string Origin(Uri url) => $"{url.Scheme}://{url.Authority}";

    /// <summary>One request of the exchange, or why its headers cannot be sent.</summary>
    private static (HttpRequestMessage? Message, string? Refused) Message(string method, Uri url, IReadOnlyList<KeyValuePair<string, string>> headers, string body)
    {
        HttpRequestMessage message = new(new HttpMethod(method), url);
        if (body.Length > 0 || headers.Any(h => ContentHeaders.Contains(h.Key)))
        {
            message.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        }
        foreach ((string name, string value) in headers)
        {
            bool added = ContentHeaders.Contains(name) && message.Content is not null
                ? message.Content.Headers.TryAddWithoutValidation(name, value)
                : message.Headers.TryAddWithoutValidation(name, value);
            if (!added)
            {
                message.Dispose();
                return (null, $"The header {name} could not be sent as \"{value}\"");
            }
        }
        return (message, null);
    }

    /// <summary>Where a redirect answer sends the request next, or null for an answer that is none.</summary>
    private static Uri? Redirect(HttpResponseMessage response, Uri from) =>
        (int)response.StatusCode is 300 or 301 or 302 or 303 or 307 or 308 && response.Headers.Location is { } location
            && (location.IsAbsoluteUri ? location : new Uri(from, location)) is { Scheme: "http" or "https" } next
            ? next
            : null;

    /// <summary>A body's file as text, or why it could not be read: past <see cref="MostSent"/> characters too.</summary>
    private static (string? Text, string? Error) ReadText(string file)
    {
        try
        {
            using StreamReader reader = new(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
                new FileStreamOptions { Access = FileAccess.Read, Share = FileShare.ReadWrite | FileShare.Delete });
            StringBuilder text = new();
            char[] chunk = new char[64 * 1024];
            int count;
            while ((count = reader.Read(chunk, 0, chunk.Length)) > 0)
            {
                text.Append(chunk, 0, count);
                if (text.Length > MostSent)
                {
                    return (null, $"it is over {MostSent / 1024 / 1024} MB");
                }
            }
            return (text.ToString(), null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return (null, error.Message);
        }
    }

    private static async Task<(byte[] Bytes, bool Cut)> ReadAtMostAsync(HttpContent content, CancellationToken cancel)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using MemoryStream read = new();
        byte[] buffer = new byte[81920];
        while (read.Length <= MostRead)
        {
            int count = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (count == 0)
            {
                return (read.ToArray(), false);
            }
            read.Write(buffer, 0, count);
        }
        return (read.GetBuffer()[..MostRead], true);
    }

    private static IEnumerable<KeyValuePair<string, string>> Flatten(HttpHeaders headers) =>
        headers.Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)));

    private static string Decode(byte[] bytes, MediaTypeHeaderValue? type)
    {
        Encoding encoding = Encoding.UTF8;
        if (type?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }
        return encoding.GetString(bytes);
    }
}
