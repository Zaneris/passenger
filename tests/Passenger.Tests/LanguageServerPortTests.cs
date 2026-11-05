using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Passenger.Lsp;

namespace Passenger.Tests;

/// <summary>A language server reached over TCP on a loopback port, as Godot's editor serves GDScript, against a stand-in
/// that answers with the bytes Godot 4.7.2's server sent: the client's frames carry their Content-Length alone (Godot
/// reads the length at the header's start), it is told the folder's path as well as its URI, and a server that says it
/// works on another folder is known to; with nothing listening, the start fails as a server that ended; and a start that
/// fails or is given up holds no server after it.</summary>
[Collection(Beside.Name)]
public sealed class LanguageServerPortTests
{
    /// <summary>Godot's answer to initialize, as its server sent it.</summary>
    private const string Initialized = """{"id":1,"jsonrpc":"2.0","result":{"capabilities":{"codeActionProvider":false,"colorProvider":false,"completionProvider":{"resolveProvider":true,"triggerCharacters":[".","$","'","\""]},"declarationProvider":true,"definitionProvider":true,"documentFormattingProvider":false,"documentHighlightProvider":true,"documentLinkProvider":{"resolveProvider":false},"documentOnTypeFormattingProvider":{"firstTriggerCharacter":"","moreTriggerCharacter":[]},"documentRangeFormattingProvider":false,"documentSymbolProvider":true,"executeCommandProvider":{"commands":[]},"foldingRangeProvider":false,"hoverProvider":true,"implementationProvider":false,"referencesProvider":true,"renameProvider":{"prepareProvider":true},"signatureHelpProvider":{"triggerCharacters":[",","("]},"textDocumentSync":{"change":1,"openClose":true,"save":{"includeText":true},"willSave":false,"willSaveWaitUntil":true},"typeDefinitionProvider":false,"workspace":{},"workspaceSymbolProvider":false}}}""";

    [Fact]
    public async Task A_server_on_a_port_is_sent_bare_frames_and_the_folders_path_and_takes_whole_texts()
    {
        string folder = TestFolders.Create("lsp-port-");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task<(string Header, JsonElement Asked)> served = ServeAsync(listener, other: null);
        await using LanguageSession session = await LanguageSession.StartAsync(Start(folder, listener), TestContext.Current.CancellationToken);
        (string header, JsonElement asked) = await served;
        Assert.Matches(@"^Content-Length: \d+$", header);
        Assert.Equal(folder, asked.GetProperty("params").GetProperty("rootPath").GetString());
        Assert.True(session.Server.TakesWholeTexts, "A server whose sync is by whole texts (Godot's) is taken as one that reads edits");
        Assert.Null(session.Server.OtherFolder);
        Assert.Equal(0, session.Server.ProcessId);
    }

    [Fact]
    public async Task A_server_that_says_it_works_on_another_folder_as_it_is_initialized_is_known_to()
    {
        string folder = TestFolders.Create("lsp-port-");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        Task<(string Header, JsonElement Asked)> served = ServeAsync(listener, other: "/projects/elsewhere");
        await using LanguageSession session = await LanguageSession.StartAsync(Start(folder, listener), TestContext.Current.CancellationToken);
        await served;
        Assert.Equal("/projects/elsewhere", session.Server.OtherFolder);
    }

    [Fact]
    public async Task Nothing_listening_on_the_port_fails_the_start_as_a_server_that_ended()
    {
        string folder = TestFolders.Create("lsp-port-");
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        ServerStart start = Start(folder, listener);
        listener.Stop();
        LanguageServerFailed failed = await Assert.ThrowsAsync<LanguageServerFailed>(() => LanguageSession.StartAsync(start, TestContext.Current.CancellationToken));
        Assert.True(failed.Ended, $"A port nothing listens on failed the start as a refusal: {failed.Reason}");
        Assert.Contains("nothing listens on port", failed.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_start_the_server_fails_leaves_no_server_held()
    {
        string folder = TestFolders.Create("lsp-port-");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        ServerStart start = Start(folder, listener);
        Task<LanguageSession> starting = LanguageSession.StartAsync(start, TestContext.Current.CancellationToken);
        using (TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken))
        {
            await ReadAsync(client.GetStream());
        }
        await Assert.ThrowsAsync<LanguageServerFailed>(() => starting);
        Assert.True(LanguageServer.LiveIn(start.LogFolder) == 0, "A server that ended as it was initialized is held for the rest of the IDE's life");
    }

    [Fact]
    public async Task A_start_given_up_leaves_no_server_held()
    {
        string folder = TestFolders.Create("lsp-port-");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        ServerStart start = Start(folder, listener);
        using CancellationTokenSource giving = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<LanguageSession> starting = LanguageSession.StartAsync(start, giving.Token);
        using TcpClient client = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await ReadAsync(client.GetStream());
        await giving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
        Assert.True(LanguageServer.LiveIn(start.LogFolder) == 0, "A server whose start was given up is held for the rest of the IDE's life");
    }

    private static ServerStart Start(string folder, TcpListener listener) =>
        new(null, folder, Path.Combine(folder, "logs"), ClientCapabilities.Web) { Port = ((IPEndPoint)listener.LocalEndpoint).Port, TellsRootPath = true };

    /// <summary>The stand-in: takes the one connection, reads initialize (its header and message), answers as Godot does,
    /// first saying it works on <paramref name="other"/> where one is given, then reads the client's initialized.</summary>
    private static async Task<(string Header, JsonElement Asked)> ServeAsync(TcpListener listener, string? other)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync();
        NetworkStream stream = client.GetStream();
        (string header, string body) = await ReadAsync(stream);
        JsonElement asked = JsonDocument.Parse(body).RootElement.Clone();
        if (other is not null)
        {
            await WriteAsync(stream, JsonSerializer.Serialize(new { jsonrpc = "2.0", method = LspMethods.GodotChangeWorkspace, @params = new { path = other } }));
        }
        // Answered by the id it was asked by, as Godot answers.
        await WriteAsync(stream, Initialized.Replace("\"id\":1,", $"\"id\":{asked.GetProperty("id").GetRawText()},", StringComparison.Ordinal));
        (_, string initialized) = await ReadAsync(stream);
        Assert.Contains("\"initialized\"", initialized, StringComparison.Ordinal);
        return (header, asked);
    }

    private static async Task<(string Header, string Body)> ReadAsync(NetworkStream stream)
    {
        List<byte> head = [];
        byte[] one = new byte[1];
        while (head.Count < 4 || !(head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
        {
            await stream.ReadExactlyAsync(one);
            head.Add(one[0]);
        }
        string header = Encoding.ASCII.GetString([.. head])[..^4];
        byte[] body = new byte[int.Parse(header.Split(':')[1].Trim().Split('\r')[0], System.Globalization.CultureInfo.InvariantCulture)];
        await stream.ReadExactlyAsync(body);
        return (header, Encoding.UTF8.GetString(body));
    }

    private static async Task WriteAsync(NetworkStream stream, string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"));
        await stream.WriteAsync(body);
    }
}
