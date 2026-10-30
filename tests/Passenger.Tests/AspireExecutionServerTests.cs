using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Passenger.App.Aspire;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Passenger's execution server for Aspire's DCP over raw HTTP and WebSockets on loopback; with no window, beside the
/// window tests (<see cref="Beside"/>): its trickled bodies wait real seconds, which a busy machine only lengthens past the
/// server's deadline. A stand-in on its own, since only the orchestrator speaks to it, and nothing a person does sends it
/// malformed, ambiguous or abandoned requests, which it must refuse unharmed.</summary>
[Collection(Beside.Name)]
public sealed class AspireExecutionServerTests
{
    private const string Instance = "passenger-tests-abcdefghijkl";
    private const string InstanceHeader = "Microsoft-Developer-DCP-Instance-ID";
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;
    private static TimeSpan Limit => TimeSpan.FromSeconds(30).Scaled();
    private static string Body => JsonSerializer.Serialize(new
    {
        launch_configurations = new[] { new { type = "project", project_path = Path.GetFullPath("Wire.csproj"), mode = "NoDebug" } },
        env = new[] { new { name = "WIRE_VALUE", value = "kept" } },
        args = new[] { "one", "two" }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disconnecting_a_pending_complete_PUT_cancels_only_that_create(bool reset)
    {
        TaskCompletionSource<AspireProjectLaunch> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource canceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cleaned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> stopped = new();
        int calls = 0;
        string? abandoned = null;
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync(async (launch, token) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
            {
                abandoned = launch.SessionId;
                using CancellationTokenRegistration registration = token.Register(() => canceled.TrySetResult());
                entered.TrySetResult(launch);
                await release.Task.WaitAsync(Limit, Cancel);
            }
            return new(launch.SessionId, 123);
        }, (session, _) =>
        {
            stopped.Enqueue(session.Id);
            if (session.Id == abandoned)
            {
                cleaned.TrySetResult();
            }
            return Task.CompletedTask;
        }, Cancel);
        using ClientWebSocket notifications = await Subscribe(server);
        string sibling = await Create(server);
        using Wire pending = await Wire.Connect(server.Port);
        try
        {
            await pending.Write(Request(server, Body));
            await entered.Task.WaitAsync(Limit, Cancel);
            if (reset)
            {
                pending.Client.Client.LingerState = new LingerOption(true, 0);
                pending.Client.Dispose();
            }
            else
            {
                pending.Client.Client.Shutdown(SocketShutdown.Send);
            }
            await canceled.Task.WaitAsync(Limit, Cancel);
            Assert.True(server.NotifyServiceLog(Instance, sibling, "sibling survives"));
            Assert.Contains("sibling survives", await ReadUntil(notifications, "sibling survives"), StringComparison.Ordinal);
        }
        finally
        {
            release.TrySetResult();
        }
        await cleaned.Task.WaitAsync(Limit, Cancel);
        Assert.DoesNotContain(sibling, stopped);
        Assert.False(server.NotifyServiceLog(Instance, abandoned!, "gone"));
        string next = await Create(server);
        Assert.True(server.NotifyServiceLog(Instance, next, "gate released"));
        Assert.Contains("gate released", await ReadUntil(notifications, "gate released"), StringComparison.Ordinal);
        notifications.Abort();
    }

    /// <summary>An AppHost's services start at once: DCP's creates of three services are each under way while the
    /// others begin (a start that waits for the other two to begin, which one at a time would never see), and a delete
    /// of a started service is answered while their starts still wait. One service's own creates keep the order DCP
    /// sent them, the second beginning only once the first has ended.</summary>
    [Fact]
    public async Task Several_services_start_at_once_and_a_delete_waits_only_for_its_own_create()
    {
        const int together = 3;
        int begun = 0;
        TaskCompletionSource allBegun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource firstGoes = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConcurrentQueue<string> order = new();
        ConcurrentQueue<string> stopped = new();
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync(async (launch, token) =>
        {
            string service = launch.Environment.Single(variable => variable.Name == OtelVariables.ServiceName).Value;
            if (service.StartsWith("slow", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref begun) == together)
                {
                    allBegun.TrySetResult();
                }
                await allBegun.Task.WaitAsync(Limit, token);
                await release.Task.WaitAsync(Limit, token);
            }
            else if (service == "ordered")
            {
                order.Enqueue($"begins {launch.SessionId}");
                if (firstEntered.TrySetResult())
                {
                    await firstGoes.Task.WaitAsync(Limit, token);
                }
                order.Enqueue($"ends {launch.SessionId}");
            }
            return new(launch.SessionId, 7);
        }, (session, _) => { stopped.Enqueue(session.Id); return Task.CompletedTask; }, Cancel);
        using ClientWebSocket notifications = await Subscribe(server);
        string quick = await Create(server, body: Of("quick"));
        List<Wire> slow = [];
        try
        {
            for (int i = 0; i < together; i++)
            {
                Wire wire = await Wire.Connect(server.Port);
                slow.Add(wire);
                await wire.Write(Request(server, Of($"slow{i}")));
            }
            await allBegun.Task.WaitAsync(Limit, Cancel);
            using (Wire deleting = await Wire.Connect(server.Port))
            {
                await deleting.Write(Head(server, "DELETE", $"/run_session/{quick}?api-version={Version(server)}", ""));
                Assert.Equal(200, await deleting.Status());
            }
            Assert.Equal(quick, Assert.Single(stopped));
            release.TrySetResult();
            foreach (Wire wire in slow)
            {
                Assert.Equal(201, await wire.Status());
            }

            using Wire first = await Wire.Connect(server.Port);
            await first.Write(Request(server, Of("ordered")));
            await firstEntered.Task.WaitAsync(Limit, Cancel);
            using Wire second = await Wire.Connect(server.Port);
            await second.Write(Request(server, Of("ordered")));
            firstGoes.TrySetResult();
            Assert.Equal(201, await first.Status());
            Assert.Equal(201, await second.Status());
            string[] said = [.. order];
            Assert.True(said.Length == 4 && said[0].StartsWith("begins", StringComparison.Ordinal) && said[1].StartsWith("ends", StringComparison.Ordinal)
                && said[0][7..] == said[1][5..], $"One service's second create began before its first ended: {string.Join(", ", said)}");
        }
        finally
        {
            release.TrySetResult();
            firstGoes.TrySetResult();
            foreach (Wire wire in slow)
            {
                wire.Dispose();
            }
        }
        notifications.Abort();

        static string Of(string service) => JsonSerializer.Serialize(new
        {
            launch_configurations = new[] { new { type = "project", project_path = Path.GetFullPath("Wire.csproj"), mode = "NoDebug" } },
            env = new[] { new { name = OtelVariables.ServiceName, value = service } },
        });
    }

    [Fact]
    public async Task Committed_201_survives_normal_HTTP_close_and_remains_instance_owned()
    {
        ConcurrentQueue<string> stopped = new();
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync(
            (launch, _) => Task.FromResult(new AspireProjectSession(launch.SessionId, 42)),
            (session, _) => { stopped.Enqueue(session.Id); return Task.CompletedTask; }, Cancel);
        using ClientWebSocket notifications = await Subscribe(server);
        string id = await Create(server);
        Assert.True(server.NotifyServiceLog(Instance, id, "after close"));
        await ReadUntil(notifications, "after close");
        Assert.Empty(stopped);
        using Wire foreign = await Wire.Connect(server.Port);
        await foreign.Write(Head(server, "DELETE", $"/run_session/{id}?api-version={Version(server)}", "", "foreign-tests-abcdefghijkl"));
        Assert.Equal(204, await foreign.Status());
        Assert.Empty(stopped);
        Assert.True(server.NotifyServiceLog(Instance, id, "still owned"));
        using Wire deleting = await Wire.Connect(server.Port);
        await deleting.Write(Head(server, "DELETE", $"/run_session/{id}?api-version={Version(server)}", ""));
        Assert.Equal(200, await deleting.Status());
        Assert.Equal(id, Assert.Single(stopped));
        notifications.Abort();
    }

    [Theory]
    [InlineData("duplicate-length", 400)]
    [InlineData("comma-length", 400)]
    [InlineData("length-and-chunked", 400)]
    [InlineData("duplicate-encoding", 400)]
    [InlineData("encoding-list", 400)]
    [InlineData("bad-chunk", 400)]
    [InlineData("bad-chunk-ending", 400)]
    [InlineData("bad-extension", 400)]
    [InlineData("trailer-auth", 400)]
    [InlineData("duplicate-auth", 401)]
    [InlineData("wrong-auth", 401)]
    [InlineData("basic-auth", 401)]
    [InlineData("missing-auth", 401)]
    [InlineData("query-auth", 401)]
    [InlineData("oversized-auth", 401)]
    [InlineData("duplicate-instance", 400)]
    [InlineData("invalid-instance", 400)]
    [InlineData("duplicate-version", 400)]
    [InlineData("invalid-version", 400)]
    [InlineData("duplicate-host", 400)]
    [InlineData("duplicate-content-type", 415)]
    [InlineData("folded-header", 400)]
    [InlineData("request-line", 414)]
    [InlineData("header-count", 431)]
    [InlineData("header-bytes", 431)]
    [InlineData("body-limit", 413)]
    [InlineData("chunk-limit", 413)]
    [InlineData("json-depth", 400)]
    [InlineData("invalid-json", 400)]
    public async Task Malformed_or_ambiguous_wire_input_never_reaches_the_launch_callback(string kind, int expected)
    {
        int calls = 0;
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync((launch, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AspireProjectSession(launch.SessionId, 1));
        }, (_, _) => Task.CompletedTask, Cancel);
        using ClientWebSocket notifications = await Subscribe(server);
        string body = kind switch
        {
            "json-depth" => "{\"unknown\":" + new string('[', 20) + "0" + new string(']', 20) + "}",
            "invalid-json" => "{",
            _ => Body
        };
        string framing = kind switch
        {
            "duplicate-length" => "Content-Length: 0\r\nContent-Length: 0\r\n",
            "comma-length" => "Content-Length: 0, 0\r\n",
            "length-and-chunked" => "Content-Length: 0\r\nTransfer-Encoding: chunked\r\n",
            "duplicate-encoding" => "Transfer-Encoding: chunked\r\nTransfer-Encoding: chunked\r\n",
            "encoding-list" => "Transfer-Encoding: gzip, chunked\r\n",
            "bad-chunk" or "bad-chunk-ending" or "bad-extension" or "trailer-auth" or "chunk-limit" => "Transfer-Encoding: chunked\r\n",
            "body-limit" => "Content-Length: 1048577\r\n",
            _ => $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n"
        };
        body = kind switch
        {
            "bad-chunk" => "Z\r\n",
            "bad-chunk-ending" => "1\r\nx!!",
            "bad-extension" => "1;=bad\r\nx\r\n0\r\n\r\n",
            "trailer-auth" => "0\r\nAuthorization: Bearer stolen\r\n\r\n",
            "chunk-limit" => "100001\r\n",
            _ => body
        };
        string extra = kind switch
        {
            "duplicate-auth" => $"Authorization: Bearer {server.Token}\r\n",
            "duplicate-instance" => $"{InstanceHeader}: {Instance}\r\n",
            "duplicate-host" => "Host: localhost\r\n",
            "duplicate-content-type" => "Content-Type: application/json\r\n",
            "folded-header" => " X-Folded: forbidden\r\n",
            "header-count" => string.Concat(Enumerable.Range(0, 33).Select(i => $"X-{i}: value\r\n")),
            "header-bytes" => "X-Large: " + new string('x', 16400) + "\r\n",
            _ => ""
        };
        string path = $"/run_session?api-version={Version(server)}" + (kind == "duplicate-version" ? $"&api-version={Version(server)}" : "");
        if (kind == "invalid-version")
        {
            path = "/run_session?api-version=not-a-date";
        }
        if (kind == "request-line")
        {
            path = "/" + new string('x', 4096);
        }
        if (kind == "query-auth")
        {
            path += "&token=" + server.Token;
        }
        string request = Head(server, "PUT", path, "Content-Type: application/json\r\n" + framing + extra,
            kind == "invalid-instance" ? "bad" : Instance) + body;
        if (kind == "wrong-auth")
        {
            request = request.Replace(server.Token, "incorrect", StringComparison.Ordinal);
        }
        if (kind == "basic-auth")
        {
            request = request.Replace("Authorization: Bearer", "Authorization: Basic", StringComparison.Ordinal);
        }
        if (kind is "missing-auth" or "query-auth")
        {
            request = request.Replace($"Authorization: Bearer {server.Token}\r\n", "", StringComparison.Ordinal);
        }
        if (kind == "oversized-auth")
        {
            request = request.Replace(server.Token, new string('x', 257), StringComparison.Ordinal);
        }
        using Wire wire = await Wire.Connect(server.Port);
        await wire.Write(request);
        Assert.Equal(expected, await wire.Status());
        Assert.Equal(0, Volatile.Read(ref calls));
        notifications.Abort();
    }

    /// <summary>A refusal of a request the server read only in part reaches a client that reads it after the server is
    /// done with the connection, on Windows too, where a socket closed with input unread resets the
    /// connection.</summary>
    [Theory]
    [InlineData(414)]
    [InlineData(401)]
    public async Task A_refusal_of_a_request_read_in_part_reaches_a_client_that_reads_it_late(int expected)
    {
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync(
            (launch, _) => Task.FromResult(new AspireProjectSession(launch.SessionId, 1)), (_, _) => Task.CompletedTask, Cancel);
        // A request line past the limit, the rest of the request never read; or a token refused before the body is read.
        string request = expected == 414
            ? Head(server, "PUT", "/" + new string('x', 4096), "Content-Type: application/json\r\n") + Body
            : Request(server, Body).Replace(server.Token, "incorrect", StringComparison.Ordinal);
        using Wire wire = await Wire.Connect(server.Port);
        await wire.Write(request);
        Assert.True(await Waiting.UntilAsync(() => wire.Client.Available > 0, TimeSpan.FromSeconds(30), Cancel), "The server never answered the request it refused");
        await Waiting.ForAsync(TimeSpan.FromMilliseconds(200), "the server's end of the connection to come after its answer, before the client reads it", Cancel);
        Assert.Equal(expected, await wire.Status());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fragmented_valid_body_and_Expect_continue_preserve_launch_overlays(bool chunked)
    {
        TaskCompletionSource<AspireProjectLaunch> launched = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync((launch, _) =>
        {
            launched.TrySetResult(launch);
            return Task.FromResult(new AspireProjectSession(launch.SessionId, 42));
        }, (_, _) => Task.CompletedTask, Cancel);
        using ClientWebSocket notifications = await Subscribe(server);
        using Wire wire = await Wire.Connect(server.Port);
        string framing = chunked ? "Transfer-Encoding: chunked\r\n" : $"Content-Length: {Encoding.UTF8.GetByteCount(Body)}\r\n";
        string head = Head(server, "PUT", $"/run_session?api-version={Version(server)}", "Content-Type: application/json\r\nExpect: 100-continue\r\n" + framing);
        foreach (char character in head)
        {
            await wire.Write(character.ToString());
        }
        Assert.Equal(100, await wire.Status());
        string body = chunked ? $"{Encoding.UTF8.GetByteCount(Body):X};wire=valid\r\n{Body}\r\n0\r\nX-Checksum: ignored\r\n\r\n" : Body;
        foreach (char character in body)
        {
            await wire.Write(character.ToString());
        }
        Assert.Equal(201, await wire.Status());
        AspireProjectLaunch result = await launched.Task.WaitAsync(Limit, Cancel);
        Assert.Equal("NoDebug", result.Mode);
        Assert.Equal(Path.GetFullPath("Wire.csproj"), result.ProjectPath);
        Assert.Equal(new[] { "one", "two" }, result.Arguments!.Value.ToArray());
        Assert.Equal(new AspireEnvironmentVariable("WIRE_VALUE", "kept"), Assert.Single(result.Environment));
        notifications.Abort();
    }

    /// <summary>A body trickled a byte every four seconds, fixed in length on one connection and chunked on another at the same
    /// time, is refused once the ten seconds the server gives a whole body have passed, and nothing is launched.</summary>
    [Fact]
    public async Task Body_total_deadline_elapsed_rejects_fixed_and_chunked_trickles_after_ten_real_seconds()
    {
        int calls = 0;
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync((launch, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new AspireProjectSession(launch.SessionId, 1));
        }, (_, _) => Task.CompletedTask, Cancel);
        using Wire fixedLength = await Wire.Connect(server.Port);
        using Wire chunked = await Wire.Connect(server.Port);
        foreach ((Wire wire, string framing) in new[] { (fixedLength, "Content-Length: 100\r\n"), (chunked, "Transfer-Encoding: chunked\r\n") })
        {
            await wire.Write(Head(server, "PUT", $"/run_session?api-version={Version(server)}", "Content-Type: application/json\r\nExpect: 100-continue\r\n" + framing));
            Assert.Equal(100, await wire.Status());
        }
        for (int i = 0; i < 3; i++)
        {
            await fixedLength.Write("x");
            await chunked.Write("1\r\nx\r\n");
            await Waiting.ForAsync(TimeSpan.FromSeconds(4),
                "Trickle body bytes at four-second intervals across the production's ten-second total deadline", Cancel);
        }
        Assert.Equal(408, await fixedLength.Status());
        Assert.Equal(408, await chunked.Status());
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task Prefetched_masked_frame_is_preserved_and_unsolicited_WebSocket_payload_ends_only_its_instance()
    {
        TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        string? owned = null;
        await using AspireExecutionServer server = await AspireExecutionServer.StartAsync(
            (launch, _) => Task.FromResult(new AspireProjectSession(launch.SessionId, 42)),
            (session, _) =>
            {
                if (session.Id == owned)
                {
                    stopped.TrySetResult();
                }
                return Task.CompletedTask;
            }, Cancel);
        using ClientWebSocket siblingNotifications = await Subscribe(server, "sibling-tests-abcdefghijkl");
        string sibling = await Create(server, "sibling-tests-abcdefghijkl");
        using Wire wire = await Wire.Connect(server.Port);
        string upgrade = Head(server, "GET", $"/run_session/notify?api-version={Version(server)}",
            "Connection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n");
        await wire.WriteBytes([.. Encoding.ASCII.GetBytes(upgrade), 0x89, 0x81, 1, 2, 3, 4, (byte)('p' ^ 1)]);
        Assert.Equal(101, await wire.Status());
        Assert.Equal(new byte[] { 0x8a, 1, (byte)'p' }, await wire.ReadBytes(3));
        owned = await Create(server);
        await wire.WriteBytes([0x81, 0x81, 1, 2, 3, 4, (byte)('x' ^ 1)]);
        await stopped.Task.WaitAsync(Limit, Cancel);
        Assert.False(server.NotifyServiceLog(Instance, owned, "gone"));
        Assert.True(server.NotifyServiceLog("sibling-tests-abcdefghijkl", sibling, "unaffected"));
        await ReadUntil(siblingNotifications, "unaffected");
        siblingNotifications.Abort();
    }

    private static string Version(AspireExecutionServer server)
    {
        using JsonDocument info = JsonDocument.Parse(server.Environment[AspireVariables.DebugSessionInfo]);
        return info.RootElement.GetProperty("protocols_supported")[0].GetString()!;
    }

    private static string Head(AspireExecutionServer server, string method, string path, string headers, string instance = Instance) =>
        $"{method} {path} HTTP/1.1\r\nHost: localhost:{server.Port}\r\nAuthorization: Bearer {server.Environment[AspireVariables.DebugSessionToken]}\r\n{InstanceHeader}: {instance}\r\n{headers}\r\n";

    private static string Request(AspireExecutionServer server, string body, string instance = Instance) =>
        Head(server, "PUT", $"/run_session?api-version={Version(server)}", $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n", instance) + body;

    private static async Task<ClientWebSocket> Subscribe(AspireExecutionServer server, string instance = Instance)
    {
        ClientWebSocket socket = new();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + server.Token);
        socket.Options.SetRequestHeader(InstanceHeader, instance);
        try
        {
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/run_session/notify?api-version={Version(server)}"), Cancel).WaitAsync(Limit, Cancel);
            return socket;
        }
        catch { socket.Dispose(); throw; }
    }

    private static async Task<string> Create(AspireExecutionServer server, string instance = Instance, string? body = null)
    {
        using Wire wire = await Wire.Connect(server.Port);
        await wire.Write(Request(server, body ?? Body, instance));
        string head = await wire.ReadHead();
        Assert.StartsWith("HTTP/1.1 201 ", head, StringComparison.Ordinal);
        string location = Assert.Single(head.Split("\r\n"), line => line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase));
        return new Uri(location["Location:".Length..].Trim()).Segments[^1];
    }

    private static async Task<string> ReadUntil(ClientWebSocket socket, string marker)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
        deadline.CancelAfter(Limit);
        byte[] buffer = new byte[32768];
        for (int i = 0; i < 10; i++)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), deadline.Token);
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            string text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return text;
            }
        }
        throw new InvalidOperationException("The expected notification was not received: " + marker);
    }

    private sealed class Wire(TcpClient client) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public static async Task<Wire> Connect(int port)
        {
            TcpClient client = new();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port, Cancel).AsTask().WaitAsync(Limit, Cancel);
                return new(client);
            }
            catch { client.Dispose(); throw; }
        }
        public Task Write(string text) => WriteBytes(Encoding.UTF8.GetBytes(text));
        public async Task WriteBytes(byte[] bytes) => await Client.GetStream().WriteAsync(bytes, Cancel).AsTask().WaitAsync(Limit, Cancel);
        public async Task<int> Status() => int.Parse((await ReadHead()).Split(' ')[1], CultureInfo.InvariantCulture);
        public async Task<byte[]> ReadBytes(int count)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            deadline.CancelAfter(Limit);
            byte[] bytes = new byte[count];
            await Client.GetStream().ReadExactlyAsync(bytes, deadline.Token);
            return bytes;
        }
        public async Task<string> ReadHead()
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(Cancel);
            deadline.CancelAfter(Limit);
            StringBuilder head = new();
            byte[] one = new byte[1];
            while (head.Length < 32768)
            {
                int count = await Client.GetStream().ReadAsync(one, deadline.Token);
                Assert.Equal(1, count);
                head.Append((char)one[0]);
                if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n")
                {
                    return head.ToString();
                }
            }
            throw new InvalidOperationException("Response headers exceeded the test's bound");
        }
        public void Dispose() => Client.Dispose();
    }
}
