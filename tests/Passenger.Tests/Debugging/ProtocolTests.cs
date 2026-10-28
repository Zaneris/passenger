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
        Task pending = connection.RequestAsync(DapCommands.Initialize, null, TestContext.Current.CancellationToken);
        connection.Start();
        byte[] body = Encoding.UTF8.GetBytes(response);
        await peer.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), TestContext.Current.CancellationToken);
        await peer.GetStream().WriteAsync(body, TestContext.Current.CancellationToken);
        DapException failed = await Assert.ThrowsAsync<DapException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5).Scaled(), TestContext.Current.CancellationToken));
        Assert.Equal(DapCommands.Initialize, failed.Command);
        await connection.Completion.WaitAsync(TimeSpan.FromSeconds(5).Scaled(), TestContext.Current.CancellationToken);
    }

    [Fact]
    [Gate]
    public async Task A_request_the_adapter_does_not_support_is_refused_naming_it_and_the_next_is_still_answered()
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);

        DapException unimplementedError = await Assert.ThrowsAsync<DapException>(() => host.RequestAsync("setVariable", new { variablesReference = 0, name = "value", value = "1" }));
        Assert.Equal("Request 'setVariable' is not supported.", unimplementedError.Reason);

        DapException unknownError = await Assert.ThrowsAsync<DapException>(() => host.RequestAsync("unknownRequest"));
        Assert.Equal("Request 'unknownRequest' is not supported.", unknownError.Reason);

        DapCapabilities capabilities = await host.RequestAsync<DapCapabilities>(DapCommands.Initialize, DebugSession.InitializeArguments);
        Assert.True(capabilities.SupportsConfigurationDoneRequest);
    }

    /// <summary>A client that sends what no message of the protocol is (a header that never ends, or a Content-Length past
    /// the 256 MB a message may be, or past any number) ends the conversation, the adapter saying why in its log; it never
    /// waits to take its memory for it.</summary>
    [Theory]
    [InlineData("header")]
    [InlineData("268435457")]
    [InlineData("99999999999999999999")]
    public async Task A_message_too_large_to_read_ends_the_conversation_saying_so(string sent)
    {
        await using DebugSession host = DebugSession.ForLaunch(testOutputHelper);
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

/// <summary>What a DAP message costs to read, counted across the process, so the class runs alone.</summary>
[Collection(Alone.Name)]
public class ProtocolBytesTests(ITestOutputHelper testOutput)
{
    /// <summary>A variables answer of 2,000 variables (two large collections opened) is read straight from the bytes it came
    /// in as the variables it holds, a type name they share read once, costing at most twice its JSON across both ends of the
    /// conversation; least of six. Every variable reads as it was sent.</summary>
    [Fact]
    public async Task A_variables_answer_of_2000_variables_costs_at_most_twice_its_JSON()
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        using TcpClient client = new();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, cancel);
        using TcpClient peer = await listener.AcceptTcpClientAsync(cancel);
        VariablesResponse sent = new([.. Enumerable.Range(0, 2000).Select(i => (i % 4) switch
        {
            0 => new DapVariable { Name = $"[{i}]", Value = $"{{Order {i}, Shipped = {i % 7 == 0}}}", Type = "Shop.Orders.Order", EvaluateName = $"orders[{i}]",
                VariablesReference = 1000 + i, PresentationHint = i % 20 == 0 ? new DapPresentationHint("data") : null },
            1 => new DapVariable { Name = $"[{i}]", Value = "Count = 3", Type = i == 1 ? "Café.Menu" : "System.Collections.Generic.List<Shop.Orders.Line>",
                EvaluateName = $"lines[{i}]", VariablesReference = 5000 + i },
            _ => new DapVariable { Name = $"[{i}]", Value = $"\"customer-{i:D6}@example.com\"", Type = "string", EvaluateName = $"emails[{i}]" },
        })]);
        long json = JsonSerializer.SerializeToUtf8Bytes(sent, DapConnection.Options).Length;
        await using DapConnection adapter = new(peer.GetStream(), peer.GetStream());
        adapter.Handle<VariablesArguments, VariablesResponse>(DapCommands.Variables, _ => Task.FromResult(sent));
        adapter.Start();
        await using DapConnection ide = new(client.GetStream(), client.GetStream());
        ide.Start();
        long least = long.MaxValue;
        VariablesResponse? read = null;
        for (int time = 0; time < 6; time++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            read = await ide.RequestAsync<VariablesResponse>(DapCommands.Variables, new VariablesArguments(1), cancel)
                .WaitAsync(TimeSpan.FromSeconds(10).Scaled(), cancel);
            least = Math.Min(least, GC.GetTotalAllocatedBytes(precise: true) - before);
        }
        Assert.Equivalent(sent.Variables, read!.Variables, strict: true);
        testOutput.WriteLine($"{json:N0} bytes of JSON, {least:N0} allocated ({(double)least / json:0.00}×)");
        Assert.True(least <= json * 2,
            $"Reading a variables answer of {json:N0} bytes of JSON allocated {least:N0} bytes ({(double)least / json:0.0}× its JSON): opening a large collection in the debugger costs the IDE that much more memory");
    }
}
