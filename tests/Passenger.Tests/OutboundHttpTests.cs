using System.Diagnostics;
using Passenger.App.Xaml;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Passenger's own requests as a host sees them, Source Link's to a symbol server standing in for NuGet's: nothing
/// in them but the request and a User-Agent naming Passenger, no cookie an earlier answer set and no trace header from
/// the activity current as they are made. Not beside the window tests: it names the symbol server in the process's
/// environment.</summary>
public sealed class OutboundHttpTests
{
    [Fact]
    public async Task A_Source_Link_request_carries_Passengers_User_Agent_and_no_cookie_or_trace_header()
    {
        List<StandInHttp.Exchange> asked = [];
        await using StandInHttp symbols = new(async exchange =>
        {
            lock (asked)
            {
                asked.Add(exchange);
            }
            await exchange.ReplyAsync(404, "text/plain", "no such PDB", "Set-Cookie: session=kept; Path=/\r\n");
        });
        string? was = Environment.GetEnvironmentVariable(SourceLink.Variable);
        Environment.SetEnvironmentVariable(SourceLink.Variable, symbols.Root + "/");
        Heard said = new();
        string assembly = typeof(SourceLink).Assembly.Location;
        string name = Path.GetFileNameWithoutExtension(assembly);
        try
        {
            using Activity current = new Activity("source-link-test").Start();
            SourceLink sources = new();
            // Twice: the second request is made after an answer that set a cookie.
            for (int i = 0; i < 2; i++)
            {
                string? file = await sources.FileAsync(assembly, name, "/Nothing.xaml", said, TestContext.Current.CancellationToken);
                Assert.True(file is null, $"Source Link gave {file} where the symbol server has no PDB");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(SourceLink.Variable, was);
            TestFolders.Delete(Path.Combine(LibrarySources.Folder, "SourceLink", name));
        }
        StandInHttp.Exchange[] requests;
        lock (asked)
        {
            requests = [.. asked];
        }
        Assert.True(requests.Length == 2, $"Source Link asked the symbol server {requests.Length} times, not once for each file asked for; it said: {said}");
        foreach (StandInHttp.Exchange request in requests)
        {
            Assert.True(request.Header("User-Agent") == $"Passenger/{Product.Version}",
                $"Source Link's request named its maker as '{request.Header("User-Agent")}', not Passenger/{Product.Version}:\n{request.Head}");
            Assert.True(request.Header("traceparent") is null && request.Header("tracestate") is null,
                $"Source Link's request carried the trace of the activity current as it was made, telling the host of the IDE's own work:\n{request.Head}");
        }
        Assert.True(requests[1].Header("Cookie") is null,
            $"Source Link's second request sent back the cookie the symbol server's first answer set, so a host can follow one IDE's requests:\n{requests[1].Head}");
    }

    /// <summary>What Source Link said of each file it could not give, on the thread it said it.</summary>
    private sealed class Heard : IProgress<string>
    {
        private readonly List<string> lines = [];

        public void Report(string value)
        {
            lock (lines)
            {
                lines.Add(value);
            }
        }

        public override string ToString()
        {
            lock (lines)
            {
                return string.Join("; ", lines);
            }
        }
    }
}
