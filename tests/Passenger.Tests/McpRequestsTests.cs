using System.Net.Http;
using System.Text.Json;
using ModelContextProtocol.Client;
using Passenger.App.Mcp;

namespace Passenger.Tests;

/// <summary>An agent reads a running web app's requests and the SQL each issued: its own Passenger (<c>--mcp</c>) runs
/// LiveRequestsTests' shop, and output with requests follows it and says what it served.</summary>
public sealed class McpRequestsTests : McpRig
{
    [Fact]
    public async Task An_agent_reads_the_running_apps_requests_with_their_routes_and_commands()
    {
        using HeldPort port = new();
        string shop = TestFolders.Create("mcprequests-");
        DeleteAtEnd(shop);
        LiveRequestsTests.WriteShop(shop, port.Port);
        await using McpClient client = await Start(["--mcp", shop]);
        port.LetGo(Path.Combine(shop, "gate"));
        await Call(client, McpTools.Run, new() { ["configuration"] = "Shop: http", ["waitSeconds"] = 5 });
        try
        {
            // The first ask follows the app, so what it serves from then on is heard.
            string following = (await Call(client, McpTools.Output, new() { ["requests"] = true })).GetString()!;
            Assert.True(following.StartsWith("Following Shop: http (process", StringComparison.Ordinal), $"output with requests did not follow the shop: {following}");
            using HttpClient http = new();
            Assert.True(await Waiting.UntilAsync(() =>
            {
                try
                {
                    return LiveRequestsTests.Get(http, $"http://127.0.0.1:{port.Port}/orders") == "Ada: 0, Grace: 0, Linus: 0";
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            }, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken), "The shop never answered its orders");
            string said = string.Empty;
            Assert.True(await Waiting.UntilAsync(async () =>
            {
                said = (await Call(client, McpTools.Output, new() { ["requests"] = true })).GetString()!;
                return said.Split('\n').Any(l => l.StartsWith("GET /orders 200", StringComparison.Ordinal) && l.Contains("->", StringComparison.Ordinal)) && said.Split('\n').Length >= 6;
            }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), $"The agent never read the orders' request and its commands: {said}");
            string request = said.Split('\n').First(l => l.StartsWith("GET /orders 200", StringComparison.Ordinal));
            Assert.Contains("-> GET /orders, lambda (Program.cs:", request, StringComparison.Ordinal);
            Assert.EndsWith(" N+1", request, StringComparison.Ordinal);
            Assert.Equal(3, said.Split('\n').Count(l => l.Contains("run 3 times in this request", StringComparison.Ordinal)));
            Assert.Contains("FROM \"Orders\"", said, StringComparison.Ordinal);

            // A sign-up's command parameters (@p0, @p1…) fill or meet the Password column, so their values read as dots.
            Assert.Equal("signed up", LiveRequestsTests.Get(http, $"http://127.0.0.1:{port.Port}/sign-up/ada"));
            string[] signUp = [];
            Assert.True(await Waiting.UntilAsync(async () =>
            {
                said = (await Call(client, McpTools.Output, new() { ["requests"] = true })).GetString()!;
                string[] lines = said.Split('\n');
                int at = Array.FindIndex(lines, l => l.StartsWith("GET /sign-up/ada 200", StringComparison.Ordinal));
                signUp = at < 0 ? [] : [.. lines.Skip(at + 1).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal))];
                return signUp.Length == 3 && signUp.All(l => l.Contains("='", StringComparison.Ordinal));
            }, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), $"The agent never read the sign-up's three commands with their parameters: {said}");
            Assert.All(signUp, l => Assert.Contains($"='{Passenger.App.Containers.ContainerSecrets.Masked}'", l, StringComparison.Ordinal));
            Assert.True(!said.Contains("ada-first", StringComparison.Ordinal) && !said.Contains("ada-second", StringComparison.Ordinal),
                $"The agent read a value of the Password column in the clear: {string.Join('\n', signUp)}");
        }
        finally
        {
            await Call(client, McpTools.Stop);
        }
    }
}
