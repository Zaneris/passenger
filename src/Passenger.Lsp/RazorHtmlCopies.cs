namespace Passenger.Lsp;

/// <summary>The HTML language server as Razor's files reach it, each file's HTML a document there by the file's URI: a send
/// it can no longer take throws <see cref="LanguageServerFailed"/>.</summary>
internal interface IHtmlDocuments
{
    bool HasExited { get; }

    Task OpenAsync(string uri, int version, string text);

    Task ChangeAsync(string uri, int version, string text);

    Task CloseAsync(string uri);
}

/// <summary>Each open Razor file's HTML as Razor last gave it, and what the HTML server holds of it. Everything the server is
/// sent goes one at a time, so <see cref="opened"/> says what it was last sent: a close takes its turn behind a send on its
/// way, closing what that send opened, or, the file reopened meanwhile, leaving it open for its new text. Every giving has
/// a number no other ever had, so HTML sent before a close is never taken for what Razor gave after it. Called on any
/// thread.</summary>
internal sealed class RazorHtmlCopies
{
    private readonly Lock gate = new();

    private readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);

    /// <summary>The number of each file's last giving, by the file's URI.</summary>
    private readonly Dictionary<string, long> given = new(StringComparer.Ordinal);

    /// <summary>The last giving's number, of any file.</summary>
    private long gaveLast;

    /// <summary>Each file's HTML as the HTML server has it: in which server, at which version, of which giving, by the file's
    /// URI. Changes only in a send's turn (<see cref="sending"/>).</summary>
    private readonly Dictionary<string, (IHtmlDocuments Server, int Version, long Given)> opened = new(StringComparer.Ordinal);

    /// <summary>One message sent to the HTML server at a time, so a change never overtakes another, nor a close a change.</summary>
    private readonly SemaphoreSlim sending = new(1, 1);

    /// <summary>Keeps a Razor file's HTML as Razor gave it.</summary>
    public void Give(string uri, string text)
    {
        lock (gate)
        {
            texts[uri] = text;
            given[uri] = ++gaveLast;
        }
    }

    /// <summary>A file's HTML as Razor last gave it; null with none.</summary>
    public string? Text(string uri)
    {
        lock (gate)
        {
            return texts.GetValueOrDefault(uri);
        }
    }

    /// <summary>The URI Razor gave a file's HTML under, by its path: Razor spells a file's URI its own way. Null with none.</summary>
    public string? UriOf(string path)
    {
        lock (gate)
        {
            return texts.Keys.FirstOrDefault(known => DocumentUris.ToPath(known) == path);
        }
    }

    /// <summary>Whether the HTML server holds a file's HTML as Razor last gave it.</summary>
    public bool Current(IHtmlDocuments html, string uri)
    {
        lock (gate)
        {
            return opened.TryGetValue(uri, out (IHtmlDocuments Server, int Version, long Given) sent) && sent.Server.Equals(html)
                && sent.Given == given.GetValueOrDefault(uri);
        }
    }

    /// <summary>Sends the HTML server a file's HTML as last given: opened in it the first time (or in a server started
    /// again), its whole text after.</summary>
    public async Task SyncAsync(IHtmlDocuments html, string uri)
    {
        await sending.WaitAsync().ConfigureAwait(false);
        try
        {
            string? text;
            (IHtmlDocuments Server, int Version, long Given) was;
            bool known;
            long giving;
            lock (gate)
            {
                text = texts.GetValueOrDefault(uri);
                known = opened.TryGetValue(uri, out was) && was.Server.Equals(html);
                giving = given.GetValueOrDefault(uri);
            }
            if (text is null || known && was.Given == giving)
            {
                return;
            }
            int version = known ? was.Version + 1 : 1;
            await (known ? html.ChangeAsync(uri, version, text) : html.OpenAsync(uri, version, text)).ConfigureAwait(false);
            lock (gate)
            {
                opened[uri] = (html, version, giving);
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

    /// <summary>Lets go of a closed file's HTML, by its path, at once: <see cref="CloseAsync"/> closes the server's document.</summary>
    public void Forget(string path)
    {
        lock (gate)
        {
            foreach (string uri in texts.Keys.Where(uri => DocumentUris.ToPath(uri) == path).ToList())
            {
                texts.Remove(uri);
                given.Remove(uri);
            }
        }
    }

    /// <summary>Closes the HTML server's documents of a file let go of, in their turn; one reopened since stays open, to be
    /// sent its new text.</summary>
    public async Task CloseAsync(string path)
    {
        await sending.WaitAsync().ConfigureAwait(false);
        try
        {
            List<(IHtmlDocuments Server, string Uri)> closing = [];
            lock (gate)
            {
                foreach (string uri in opened.Keys.Where(uri => !texts.ContainsKey(uri) && DocumentUris.ToPath(uri) == path).ToList())
                {
                    closing.Add((opened[uri].Server, uri));
                    opened.Remove(uri);
                }
            }
            foreach ((IHtmlDocuments server, string uri) in closing.Where(c => !c.Server.HasExited))
            {
                await server.CloseAsync(uri).ConfigureAwait(false);
            }
        }
        catch (LanguageServerFailed)
        {
            // Gone: it holds no document to close.
        }
        finally
        {
            sending.Release();
        }
    }
}
