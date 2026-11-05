using Passenger.Lsp;

namespace Passenger.UnitTests.Lsp;

/// <summary>What an HTML server holds of Razor's files, against a stand-in that takes in what it is sent only as a held send
/// ends: a file closed while its HTML is on its way is closed there behind it, and reopened is sent its new text, never
/// taken to hold it; a server started again has the file opened afresh, and one that goes as a send does asks nothing more.</summary>
public class RazorHtmlCopiesTests
{
    private static readonly string BellUri = DocumentUris.FromPath(Path.Combine(Path.GetTempPath(), "Shop", "Bell.razor"));

    /// <summary>The file's path as its closing names it.</summary>
    private static readonly string Bell = DocumentUris.ToPath(BellUri);

    /// <summary>A server holding each document's text and version as it took them in, each send held while a test asks.</summary>
    private sealed class StandIn : IHtmlDocuments
    {
        private TaskCompletionSource? hold;

        public Dictionary<string, (int Version, string Text)> Documents { get; } = [];

        public List<string> Sent { get; } = [];

        public bool HasExited { get; set; }

        public bool Refuses { get; set; }

        /// <summary>Holds the next send on its way until the task given back is told to end.</summary>
        public TaskCompletionSource HoldNext() => hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private async Task TakenAsync()
        {
            if (Interlocked.Exchange(ref hold, null) is { } held)
            {
                await held.Task;
            }
            if (Refuses)
            {
                throw new LanguageServerFailed("the language server stopped before it answered");
            }
        }

        public async Task OpenAsync(string uri, int version, string text)
        {
            await TakenAsync();
            Assert.False(Documents.ContainsKey(uri), $"The server was told to open {uri}, which it has open");
            Sent.Add($"open {version}");
            Documents[uri] = (version, text);
        }

        public async Task ChangeAsync(string uri, int version, string text)
        {
            await TakenAsync();
            Assert.True(Documents.ContainsKey(uri), $"The server was sent a change to {uri}, which it does not have open");
            Sent.Add($"change {version}");
            Documents[uri] = (version, text);
        }

        public async Task CloseAsync(string uri)
        {
            await TakenAsync();
            Assert.True(Documents.Remove(uri), $"The server was told to close {uri}, which it does not have open");
            Sent.Add("close");
        }
    }

    [Fact]
    public async Task A_file_reopened_while_its_html_is_on_its_way_is_not_taken_to_be_held_and_is_sent_its_new_text()
    {
        StandIn server = new();
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "before");
        TaskCompletionSource held = server.HoldNext();
        Task sent = copies.SyncAsync(server, BellUri);
        copies.Forget(Bell);
        Task closed = copies.CloseAsync(Bell);
        copies.Give(BellUri, "after");
        held.SetResult();
        await Task.WhenAll(sent, closed);

        Assert.False(copies.Current(server, BellUri), "The HTML sent before the file closed was taken for what Razor gave as it reopened, so completion and diagnostics would answer from old text");
        await copies.SyncAsync(server, BellUri);
        Assert.True(server.Documents[BellUri].Text == "after", $"The server was left with '{server.Documents[BellUri].Text}' after the file reopened");
        Assert.True(copies.Current(server, BellUri), "The HTML sent after the file reopened was not known to be held");
    }

    [Fact]
    public async Task A_file_reopened_before_its_html_reached_the_server_keeps_one_document_there()
    {
        StandIn server = new();
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "before");
        TaskCompletionSource held = server.HoldNext();
        Task sent = copies.SyncAsync(server, BellUri);
        copies.Forget(Bell);
        copies.Give(BellUri, "after");
        Task closed = copies.CloseAsync(Bell);
        Task again = copies.SyncAsync(server, BellUri);
        held.SetResult();
        await Task.WhenAll(sent, closed, again);

        Assert.True(server.Sent.SequenceEqual(["open 1", "change 2"]), $"The server was sent: {string.Join(", ", server.Sent)}");
        Assert.True(server.Documents[BellUri] == (2, "after"), $"The server holds {server.Documents[BellUri]}");
    }

    [Fact]
    public async Task A_file_closed_while_its_html_is_on_its_way_is_closed_behind_it()
    {
        StandIn server = new();
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "before");
        TaskCompletionSource held = server.HoldNext();
        Task sent = copies.SyncAsync(server, BellUri);
        copies.Forget(Bell);
        Task closed = copies.CloseAsync(Bell);
        held.SetResult();
        await Task.WhenAll(sent, closed);

        Assert.True(server.Sent.SequenceEqual(["open 1", "close"]), $"The server would keep a document of a closed file, having been sent: {string.Join(", ", server.Sent)}");
        Assert.Null(copies.UriOf(Bell));
    }

    [Fact]
    public async Task A_change_on_its_way_as_the_file_closes_leaves_the_file_to_open_afresh()
    {
        StandIn server = new();
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "first");
        await copies.SyncAsync(server, BellUri);
        copies.Give(BellUri, "second");
        TaskCompletionSource held = server.HoldNext();
        Task sent = copies.SyncAsync(server, BellUri);
        copies.Forget(Bell);
        Task closed = copies.CloseAsync(Bell);
        held.SetResult();
        await Task.WhenAll(sent, closed);
        copies.Give(BellUri, "third");
        await copies.SyncAsync(server, BellUri);

        Assert.True(server.Sent.SequenceEqual(["open 1", "change 2", "close", "open 1"]), $"The server was sent: {string.Join(", ", server.Sent)}");
        Assert.True(server.Documents[BellUri] == (1, "third"), $"The server holds {server.Documents[BellUri]}");
    }

    [Fact]
    public async Task A_file_given_again_after_it_closed_is_never_the_giving_before_it()
    {
        StandIn server = new();
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "before");
        await copies.SyncAsync(server, BellUri);
        copies.Forget(Bell);
        copies.Give(BellUri, "after");

        Assert.False(copies.Current(server, BellUri), "A file's first giving after it reopened was taken for its first giving before it closed");
    }

    [Fact]
    public async Task A_server_started_again_opens_the_file_afresh()
    {
        StandIn first = new();
        StandIn second = new();
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "text");
        await copies.SyncAsync(first, BellUri);

        Assert.False(copies.Current(second, BellUri), "A server started again was taken to hold what the one before it held");
        await copies.SyncAsync(second, BellUri);
        Assert.True(second.Documents[BellUri] == (1, "text"), "A server started again was not sent the file afresh");
    }

    [Fact]
    public async Task A_send_the_server_cannot_take_is_sent_again_and_a_close_to_a_server_gone_is_not_sent()
    {
        StandIn server = new() { Refuses = true };
        RazorHtmlCopies copies = new();
        copies.Give(BellUri, "text");
        await copies.SyncAsync(server, BellUri);
        Assert.False(copies.Current(server, BellUri), "HTML the server never took was taken to be held");

        server.Refuses = false;
        await copies.SyncAsync(server, BellUri);
        Assert.True(copies.Current(server, BellUri), "HTML sent again was not known to be held");

        server.HasExited = true;
        copies.Forget(Bell);
        await copies.CloseAsync(Bell);
        Assert.True(server.Documents.ContainsKey(BellUri), "A server that has ended was sent a close");
    }
}
