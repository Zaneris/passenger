using System.Diagnostics;
using System.Text;
using Passenger.App.Xaml;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Passenger's own requests as a host sees them, Source Link's to a symbol server standing in for NuGet's: nothing
/// in them but the request and a User-Agent naming Passenger, no cookie an earlier answer set and no trace header from
/// the activity current as they are made; and an answer read whole held to the most its reader takes. Not beside the
/// window tests: it names the symbol server in the process's environment.</summary>
public sealed class OutboundHttpTests
{
    /// <summary>What leaves the IDE carries no secret: values shown to a person or an agent are masked through the one
    /// mask (a quoted password holding an escaped quote whole, a signed URL's signature, a command's parameter meeting
    /// a secret's column), and Passenger's own requests carry a User-Agent naming it but no cookie an earlier answer
    /// set and no trace header, an answer read whole held to the most its reader takes.</summary>
    [Fact]
    public async Task What_leaves_the_ide_carries_no_secret()
    {
        Flow.StepApart<SecretValuesTests>("a quoted password holding an escaped quote is masked whole", t => t.A_quoted_password_holding_an_escaped_quote_is_masked_whole());
        Flow.StepApart<SecretValuesTests>("a signed URL's signature is masked in a URL offered, a variable's value and what a program says", t => t.A_signed_urls_signature_is_masked_in_a_url_offered_a_variables_value_and_what_a_program_says());
        Flow.StepApart<SecretValuesTests>("a command's parameter meeting a secret's column anywhere in it is masked", t => t.A_commands_parameter_meeting_a_secrets_column_anywhere_in_it_is_masked());
        await Flow.StepApartAsync("a Source Link request carries Passenger's User Agent and no cookie or trace header", () => new OutboundHttpTests(), t => t.A_Source_Link_request_carries_Passengers_User_Agent_and_no_cookie_or_trace_header());
        await Flow.StepApartAsync("an answer read whole is held to the most its reader takes whether or not it says its length", () => new OutboundHttpTests(), t => t.An_answer_read_whole_is_held_to_the_most_its_reader_takes_whether_or_not_it_says_its_length());
    }

    private async Task A_Source_Link_request_carries_Passengers_User_Agent_and_no_cookie_or_trace_header()
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

    private async Task An_answer_read_whole_is_held_to_the_most_its_reader_takes_whether_or_not_it_says_its_length()
    {
        byte[] large = new byte[1_300_000];
        await using StandInHttp host = new(async exchange =>
        {
            if (exchange.Path == "/untold")
            {
                // No length said: the body runs on until the connection closes.
                await exchange.Stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n"), exchange.Ending);
                await exchange.Stream.WriteAsync(large, exchange.Ending);
                return;
            }
            await (exchange.Path switch
            {
                "/told" => exchange.ReplyAsync(200, "application/octet-stream", large),
                "/small" => exchange.ReplyAsync(200, "application/json", "{}"),
                _ => exchange.ReplyAsync(404, "text/plain", "none"),
            });
        });
        using HttpClient client = OutboundHttp.Client(TimeSpan.FromSeconds(30), most: 1024 * 1024);
        async Task<(byte[]? Bytes, string? Why)> Read(string path) =>
            await OutboundHttp.ReadAsync(client, new Uri($"{host.Root}{path}"), 1024 * 1024, path[1..], "it is not there", TestContext.Current.CancellationToken);

        Assert.Equal((null, "told is 1.2 MB, more than the 1 MB Passenger reads"), await Read("/told"));
        Assert.Equal((null, "untold is more than the 1 MB Passenger reads"), await Read("/untold"));
        Assert.Equal((null, "it is not there"), await Read("/gone"));
        Assert.Equal("{}", Encoding.UTF8.GetString((await Read("/small")).Bytes!));
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
