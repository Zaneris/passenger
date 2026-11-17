using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Passenger.Acp;

namespace Passenger.UnitTests.Acp;

/// <summary>An ACP agent in memory: a test reads what the session sent and writes what a real agent would answer, often
/// lines the Claude adapter sent in a real run. No process, no network, and the same on every run.</summary>
public sealed class StandInAgent : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private readonly Pipe toSession = new();
    private readonly Pipe fromSession = new();
    private readonly StreamReader reader;
    private readonly StreamWriter writer;

    public StandInAgent()
    {
        Session = AcpSession.Over(toSession.Reader.AsStream(), fromSession.Writer.AsStream());
        UTF8Encoding utf8 = new(encoderShouldEmitUTF8Identifier: false);
        reader = new StreamReader(fromSession.Reader.AsStream(), utf8);
        writer = new StreamWriter(toSession.Writer.AsStream(), utf8) { NewLine = "\n", AutoFlush = true };
    }

    public AcpSession Session { get; }

    /// <summary>The next message the session sent.</summary>
    public async Task<JsonNode> ReceiveAsync()
    {
        string line = await reader.ReadLineAsync().WaitAsync(Patience) ?? throw new EndOfStreamException();
        return JsonNode.Parse(line)!;
    }

    public async Task SendAsync(string json) => await writer.WriteLineAsync(json);

    /// <summary>Answers the session's next request, which must be <paramref name="method"/>, with <paramref name="result"/>.</summary>
    public async Task<JsonNode> AnswerAsync(string method, string result)
    {
        JsonNode request = await ReceiveAsync();
        Assert.Equal(method, request["method"]!.GetValue<string>());
        await SendAsync($$$"""{"jsonrpc":"2.0","id":{{{request["id"]!.ToJsonString()}}},"result":{{{result}}}}""");
        return request;
    }

    /// <summary>Plays initialize and session/new as the Claude adapter answered them.</summary>
    public async Task StartSessionAsync()
    {
        Task<AgentInfo> initialized = Session.InitializeAsync();
        await AnswerAsync("initialize", """{"protocolVersion":1,"agentInfo":{"name":"@agentclientprotocol/claude-agent-acp","title":"Claude Agent","version":"0.107.3"},"authMethods":[]}""");
        await initialized.WaitAsync(Patience);
        Task<string> created = Session.NewSessionAsync("/work/demo");
        await AnswerAsync("session/new", """{"sessionId":"sess_1","modes":{"currentModeId":"auto","availableModes":[{"id":"default","name":"Manual"},{"id":"plan","name":"Plan"}]}}""");
        await created.WaitAsync(Patience);
    }

    /// <summary>Ends the agent's side, as an agent that crashed would.</summary>
    public void CloseOutput() => writer.Dispose();

    public async ValueTask DisposeAsync()
    {
        writer.Dispose();
        await Session.DisposeAsync();
        reader.Dispose();
    }
}
