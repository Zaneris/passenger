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
    /// <summary>A message too large to read ends the conversation saying so: a header that never ends, the row of the
    /// table FramingTests reads in full.</summary>
    [Fact]
    public async Task A_message_too_large_to_read_ends_the_adapters_conversation_saying_so()
    {
        await Flow.StepApartAsync<ProtocolTests>("a header that never ends ends the conversation saying so", () => new ProtocolTests(testOutputHelper), t => t.A_message_too_large_to_read_ends_the_conversation_saying_so("header"));
    }

    /// <summary>A client that sends what no message of the protocol is (a header that never ends, or a Content-Length past
    /// the 256 MB a message may be, or past any number) ends the conversation, the adapter saying why in its log; it never
    /// waits to take its memory for it.</summary>
    private async Task A_message_too_large_to_read_ends_the_conversation_saying_so(string sent)
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
            && logged.Contains(sent == "header" ? $"ran past {Passenger.Core.Numbers.Said(DapConnection.MostHeader)} bytes" : $"over the {Passenger.Core.Numbers.Said(DapConnection.MostBody)} bytes", StringComparison.Ordinal),
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
                VariablesReference = 1000 + i, PresentationHint = i % 20 == 0 ? new DapPresentationHint(PresentationHintKinds.Data) : null },
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
