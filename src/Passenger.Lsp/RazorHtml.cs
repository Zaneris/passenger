using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.Core;

namespace Passenger.Lsp;

/// <summary>Razor's questions about the HTML in a .razor or .cshtml file. Razor, cohosted in Roslyn's server, gives the
/// client each open Razor file's HTML (<c>razor/updateHtml</c>: the file with its C# blanked out, so places are the
/// file's own) and asks it what HTML's language service says at a place. With an HTML language server
/// (<see cref="Server"/>), each file's HTML is a document of its own there (<see cref="VirtualSuffix"/>), and every
/// question goes to it, so the markup, a <c>&lt;style&gt;</c>'s CSS and a <c>&lt;script&gt;</c>'s JavaScript are
/// answered as in an HTML file. Without one
/// (no Node.js), Passenger answers HTML's element names where a tag's name is typed and its global attributes inside a
/// start tag, and nothing for the rest, so Razor's own answers (components and their parameters, directives, tag helpers,
/// the C#) stand on their own. Called on any thread.</summary>
internal sealed class RazorHtml
{
    /// <summary>The name Razor gives a Razor file's HTML after the file's own URI, which its definitions map back.</summary>
    private const string VirtualSuffix = "__virtual.html";

    /// <summary>The HTML server, asked for as each question comes (it may be started again meanwhile), or null with none.</summary>
    public Func<CancellationToken, Task<LanguageServer?>>? Server { get; set; }

    /// <summary>Each Razor file's HTML as the HTML server has it: in which server, at which version, which of Razor's
    /// updates it holds (<see cref="given"/>), by the file's URI.</summary>
    private readonly Dictionary<string, (LanguageServer Server, int Version, long Given)> opened = new(StringComparer.Ordinal);

    /// <summary>How many times Razor has given each file's HTML, by the file's URI.</summary>
    private readonly Dictionary<string, long> given = new(StringComparer.Ordinal);

    /// <summary>One file's HTML sent to the HTML server at a time, so a change never overtakes another.</summary>
    private readonly SemaphoreSlim sending = new(1, 1);

    /// <summary>The HTML server to ask, or null when there is none or it could not start.</summary>
    private async Task<LanguageServer?> HtmlAsync()
    {
        if (Server is not { } server)
        {
            return null;
        }
        try
        {
            return await server(CancellationToken.None).ConfigureAwait(false) is { HasExited: false } running ? running : null;
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
    }

    /// <summary>Keeps a Razor file's HTML as Razor gave it, answered at once: Razor holds every question of the file that
    /// needs its HTML until this is answered, so it never waits for the HTML server to start (Node.js on a loaded machine,
    /// #1233). The HTML server's document of it is brought in step apart, and again by a question of it that comes first.</summary>
    public void UpdateNow(JsonElement update)
    {
        Update(update);
        if (Uri(update) is { } uri && Server is not null)
        {
            UnawaitedWork.Observe(BackgroundWork.Run(() => SyncLaterAsync(uri)));
        }
    }

    private async Task SyncLaterAsync(string uri)
    {
        if (await HtmlAsync().ConfigureAwait(false) is { } html)
        {
            await SyncAsync(html, uri).ConfigureAwait(false);
        }
    }

    /// <summary>Whether the HTML server holds a file's HTML as Razor last gave it.</summary>
    private bool Current(LanguageServer html, string uri)
    {
        lock (texts)
        {
            return opened.TryGetValue(uri, out (LanguageServer Server, int Version, long Given) sent) && sent.Server == html
                && sent.Given == given.GetValueOrDefault(uri);
        }
    }

    /// <summary>Sends the HTML server a file's HTML as last given: opened in it the first time (or in a server started
    /// again), its whole text after.</summary>
    private async Task SyncAsync(LanguageServer html, string uri)
    {
        await sending.WaitAsync().ConfigureAwait(false);
        try
        {
            string? text;
            (LanguageServer Server, int Version, long Given) was;
            bool known;
            long update;
            lock (texts)
            {
                text = texts.GetValueOrDefault(uri);
                known = opened.TryGetValue(uri, out was);
                update = given.GetValueOrDefault(uri);
            }
            if (text is null || known && was.Server == html && was.Given == update)
            {
                return;
            }
            string virtualUri = uri + VirtualSuffix;
            int version = known && was.Server == html ? was.Version + 1 : 1;
            if (version == 1)
            {
                await html.NotifyAsync(LspMethods.DidOpen, new DidOpenTextDocumentParams(new TextDocumentItem(virtualUri, "html", version, text))).ConfigureAwait(false);
            }
            else
            {
                await html.NotifyAsync(LspMethods.DidChange, new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(virtualUri, version),
                    [new TextDocumentContentChangeEvent(null, text)])).ConfigureAwait(false);
            }
            lock (texts)
            {
                opened[uri] = (html, version, update);
            }
        }
        catch (LanguageServerFailed)
        {
            // Gone: a server started again is sent the file afresh.
        }
        finally
        {
            sending.Release();
        }
    }

    /// <summary>A question Razor forwarded, asked of the HTML server about the file's HTML, and its answer with the HTML's
    /// name taken back to the file's; null when there is no HTML server to ask, or it could not answer, for the caller's
    /// own answer.</summary>
    public async Task<JsonElement?> ForwardAsync(string method, JsonElement forwarded)
    {
        if (forwarded.ValueKind != JsonValueKind.Object || !forwarded.TryGetProperty("request", out JsonElement request)
            || await HtmlAsync().ConfigureAwait(false) is not { } html)
        {
            return null;
        }
        JsonNode asked = JsonNode.Parse(request.GetRawText())!;
        string? uri = Uri(forwarded);
        if (uri is not null)
        {
            if (!Current(html, uri))
            {
                await SyncAsync(html, uri).ConfigureAwait(false);
            }
            if (asked is JsonObject named && named["textDocument"] is JsonObject document)
            {
                document["uri"] = uri + VirtualSuffix;
            }
        }
        try
        {
            JsonElement answer = await html.RequestAsync<JsonElement>(method, asked).ConfigureAwait(false);
            if (uri is null || answer.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return answer.ValueKind == JsonValueKind.Undefined ? null : answer.Clone();
            }
            string raw = answer.GetRawText().Replace(JsonSerializer.Serialize(uri + VirtualSuffix), JsonSerializer.Serialize(uri), StringComparison.Ordinal);
            return JsonDocument.Parse(raw).RootElement.Clone();
        }
        catch (LanguageServerFailed)
        {
            return null;
        }
    }

    /// <summary>The HTML server's diagnostics of a Razor file's HTML (a <c>&lt;style&gt;</c>'s unknown property, a
    /// <c>&lt;script&gt;</c>'s syntax error), as Razor answers only Visual Studio for them: those on lines the HTML has
    /// as the file does, so nothing is said of HTML that stands where C# was blanked out. None without an HTML server or
    /// the file's HTML there.</summary>
    public async Task<IReadOnlyList<Diagnostic>> DiagnosticsAsync(string path, string file, CancellationToken cancel)
    {
        string? uri;
        string? text = null;
        lock (texts)
        {
            // Razor spells the file's URI its own way: found by its path.
            uri = texts.Keys.FirstOrDefault(known => DocumentUris.ToPath(known) == path);
            if (uri is not null)
            {
                text = texts[uri];
            }
        }
        if (uri is null || text is null || await HtmlAsync().ConfigureAwait(false) is not { } html)
        {
            return [];
        }
        if (!Current(html, uri))
        {
            await SyncAsync(html, uri).ConfigureAwait(false);
        }
        lock (texts)
        {
            text = texts.GetValueOrDefault(uri) ?? text;
        }
        DocumentDiagnosticReport? report;
        try
        {
            report = await html.RequestAsync<DocumentDiagnosticReport>(LspMethods.DocumentDiagnostic,
                new DocumentDiagnosticParams(new TextDocumentIdentifier(uri + VirtualSuffix)), cancel).ConfigureAwait(false);
        }
        catch (LanguageServerFailed)
        {
            return [];
        }
        string[] htmlLines = text.Split('\n');
        string[] fileLines = file.Split('\n');
        return [.. (report?.Items ?? []).Where(d => Same(d.Range.Start.Line) && Same(d.Range.End.Line))];

        bool Same(int line) => line < htmlLines.Length && line < fileLines.Length && htmlLines[line].TrimEnd('\r') == fileLines[line].TrimEnd('\r');
    }
    /// <summary>The HTML living standard's elements, less the obsolete.</summary>
    private static readonly string[] Elements =
    [
        "a", "abbr", "address", "area", "article", "aside", "audio", "b", "base", "bdi", "bdo", "blockquote", "body", "br",
        "button", "canvas", "caption", "cite", "code", "col", "colgroup", "data", "datalist", "dd", "del", "details", "dfn",
        "dialog", "div", "dl", "dt", "em", "embed", "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3",
        "h4", "h5", "h6", "head", "header", "hgroup", "hr", "html", "i", "iframe", "img", "input", "ins", "kbd", "label",
        "legend", "li", "link", "main", "map", "mark", "menu", "meta", "meter", "nav", "noscript", "object", "ol",
        "optgroup", "option", "output", "p", "picture", "pre", "progress", "q", "rp", "rt", "ruby", "s", "samp", "script",
        "search", "section", "select", "slot", "small", "source", "span", "strong", "style", "sub", "summary", "sup",
        "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead", "time", "title", "tr", "track", "u", "ul",
        "var", "video", "wbr",
    ];

    /// <summary>The attributes every HTML element takes; <see cref="Flags"/> are on by being there, and take no value.</summary>
    private static readonly string[] GlobalAttributes =
    [
        "accesskey", "autocapitalize", "autofocus", "class", "contenteditable", "dir", "draggable", "enterkeyhint", "hidden",
        "id", "inert", "inputmode", "lang", "nonce", "popover", "role", "slot", "spellcheck", "style", "tabindex", "title",
        "translate",
    ];

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "autofocus", "hidden", "inert" };

    private static readonly object Nothing = new { isIncomplete = false, items = Array.Empty<object>() };

    private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);

    /// <summary>Keeps a Razor file's HTML as Razor last gave it.</summary>
    public void Update(JsonElement update)
    {
        if (Uri(update) is { } uri && update.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
        {
            lock (texts)
            {
                texts[uri] = text.GetString()!;
                given[uri] = given.GetValueOrDefault(uri) + 1;
            }
        }
    }

    /// <summary>A file's HTML as Razor last gave it, by its path: Razor spells a file's URI its own way.</summary>
    public string? TextOf(string path)
    {
        lock (texts)
        {
            return texts.FirstOrDefault(known => DocumentUris.ToPath(known.Key) == path).Value;
        }
    }

    /// <summary>Lets go of a closed file's HTML, by its path: Razor spells a file's URI its own way. The HTML server's
    /// document of it is closed too.</summary>
    public void Forget(string path)
    {
        List<(LanguageServer Server, string Uri)> closing = [];
        lock (texts)
        {
            foreach (string uri in texts.Keys.Concat(opened.Keys).Distinct().Where(uri => DocumentUris.ToPath(uri) == path).ToList())
            {
                texts.Remove(uri);
                given.Remove(uri);
                if (opened.Remove(uri, out (LanguageServer Server, int Version, long Given) sent))
                {
                    closing.Add((sent.Server, uri + VirtualSuffix));
                }
            }
        }
        foreach ((LanguageServer server, string uri) in closing.Where(c => !c.Server.HasExited))
        {
            server.Notify(LspMethods.DidClose, new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri)));
        }
    }

    /// <summary>HTML's completion at the place Razor asks about: element names after a tag's <c>&lt;</c>, the global
    /// attributes after a space inside a start tag; an empty list anywhere else, which Razor merges with its own.</summary>
    public object Completion(JsonElement forwarded)
    {
        string? html = null;
        if (Uri(forwarded) is { } uri)
        {
            lock (texts)
            {
                html = texts.GetValueOrDefault(uri);
            }
        }
        if (html is null
            || !forwarded.TryGetProperty("request", out JsonElement request) || !request.TryGetProperty("position", out JsonElement position)
            || Offset(html, position.GetProperty("line").GetInt32(), position.GetProperty("character").GetInt32()) is not { } at)
        {
            return Nothing;
        }
        int start = at;
        while (start > 0 && (char.IsAsciiLetterOrDigit(html[start - 1]) || html[start - 1] is '-' or ':'))
        {
            start--;
        }
        if (start > 0 && html[start - 1] == '<')
        {
            return new { isIncomplete = false, items = Elements.Select(name => new { label = name, kind = 10, insertText = name, insertTextFormat = 1 }).ToArray() };
        }
        if (start > 0 && char.IsWhiteSpace(html[start - 1]) && InStartTag(html, start))
        {
            return new
            {
                isIncomplete = false,
                items = GlobalAttributes.Select(name => Flags.Contains(name)
                    ? new { label = name, kind = 10, insertText = name, insertTextFormat = 1 }
                    : new { label = name, kind = 10, insertText = name + "=\"$0\"", insertTextFormat = 2 }).ToArray(),
            };
        }
        return Nothing;
    }

    /// <summary>Whether a place is inside a start tag, past its name: a '&lt;' and a letter before it, with no '&gt;'
    /// between, outside a quoted value.</summary>
    private static bool InStartTag(string html, int at)
    {
        char? quote = null;
        for (int i = at - 1; i >= 0; i--)
        {
            char c = html[i];
            if (quote is { } open)
            {
                if (c == open)
                {
                    quote = null;
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return false;
            }
            else if (c == '<')
            {
                return i + 1 < at && char.IsAsciiLetter(html[i + 1]);
            }
        }
        return false;
    }

    /// <summary>The offset of a line and a UTF-16 column, lines ended by '\n' as Razor's HTML keeps the file's own
    /// breaks; null past the text's end.</summary>
    private static int? Offset(string text, int line, int character)
    {
        int at = 0;
        for (int l = 0; l < line; l++)
        {
            int next = text.IndexOf('\n', at);
            if (next < 0)
            {
                return null;
            }
            at = next + 1;
        }
        int end = text.IndexOf('\n', at);
        int length = (end < 0 ? text.Length : end) - at;
        return character <= length ? at + character : null;
    }

    private static string? Uri(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("textDocument", out JsonElement document)
        && document.TryGetProperty("uri", out JsonElement uri) ? uri.GetString() : null;
}
