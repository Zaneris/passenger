using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Passenger.Dap;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests.Debugging;

[Collection(DebugSession.Collection)]
public class ProtocolTests(ITestOutputHelper testOutputHelper)
{
    [Theory]
    [InlineData("{\"type\":\"response\",\"request_seq\":1,\"success\":\"true\"}")]
    [InlineData("{\"type\":\"response\",\"request_seq\":1,\"success\":null}")]
    [InlineData("{\"type\":\"response\",\"request_seq\":2147483648,\"success\":true}")]
    [InlineData("{")]
    public async Task Malformed_responses_fail_pending_requests_without_orphaning_them(string response)
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, TestContext.Current.CancellationToken);
        using TcpClient peer = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);
        await using DapConnection connection = new(client.GetStream(), client.GetStream());
        Task<JsonElement> pending = connection.RequestAsync(DapCommands.Initialize, null, TestContext.Current.CancellationToken);
        connection.Start();
        byte[] body = Encoding.UTF8.GetBytes(response);
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), TestContext.Current.CancellationToken);
        await peer.GetStream().WriteAsync(body, TestContext.Current.CancellationToken);
        DapException failed = await Assert.ThrowsAsync<DapException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5).Scaled(), TestContext.Current.CancellationToken));
        Assert.Equal(DapCommands.Initialize, failed.Command);
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5).Scaled(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_request_the_adapter_does_not_support_is_refused_naming_it_and_the_next_is_still_answered()
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);

        DapException unimplementedError = Assert.Throws<DapException>(() => host.Request("setVariable", new { variablesReference = 0, name = "value", value = "1" }));
        Assert.Equal("Request 'setVariable' is not supported.", unimplementedError.Reason);

        DapException unknownError = Assert.Throws<DapException>(() => host.Request("unknownRequest"));
        Assert.Equal("Request 'unknownRequest' is not supported.", unknownError.Reason);

        DapCapabilities capabilities = host.Request<DapCapabilities>(DapCommands.Initialize, DebugSession.InitializeArguments);
        Assert.True(capabilities.SupportsConfigurationDoneRequest);
    }

    /// <summary>A client that sends what no message of the protocol is (a header that never ends, or a Content-Length past
    /// the 256 MB a message may be, or past any number) ends the conversation, the adapter saying why in its log; it never
    /// waits to take its memory for it.</summary>
    [Theory]
    [InlineData("header")]
    [InlineData("268435457")]
    [InlineData("99999999999999999999")]
    public void A_message_too_large_to_read_ends_the_conversation_saying_so(string sent)
    {
        using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
        bool whole = host.SendRaw(Encoding.ASCII.GetBytes(sent == "header"
            ? string.Concat(Enumerable.Repeat($"X-Filler: {new string('x', 1000)}\r\n", 80))
            : $"Content-Length: {sent}\r\n\r\n"));
        // The 80 KB header may end the conversation before the rest is written.
        Assert.True(whole || sent == "header", $"The adapter stopped reading before the {sent} header was written whole: {host.EngineLog()}");
        Assert.True(host.Ended(TimeSpan.FromSeconds(20)), $"The adapter was still reading what it was sent ({sent}) 20 s later: {host.EngineLog()}");
        string logged = host.EngineLog();
        Assert.True(logged.Contains("DAP read ended", StringComparison.Ordinal)
            && logged.Contains(sent == "header" ? $"ran past {DapConnection.MostHeader} bytes" : $"over the {DapConnection.MostBody} bytes", StringComparison.Ordinal),
            $"The adapter's log does not say why the conversation ended: {logged}");
    }
}
