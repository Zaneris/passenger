using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Passenger.App.Mcp;

/// <summary>The process's socket for agents, beside its windows' in <see cref="McpHost.SocketFolder"/>
/// (<c>PID.open</c>): an agent's <c>--mcp-attach</c> whose call finds no window over its folder asks the running Passenger
/// through it to open that folder (<see cref="PassengerApp.OpenProject"/>, a window of its own unless the one in front has
/// nothing open), then joins that window's own socket, which asks the person first. It reads one line, <c>{"open":
/// PATH}</c>, and answers one, <c>{"opened": FOLDER}</c> or <c>{"refused": WHY}</c>; nothing else is ever done through it.
/// Open while a window of the process listens for agents. UI thread only, but for its listening.</summary>
internal sealed class AppSocket : IDisposable
{
    /// <summary>The process's one, while a window listens for agents.</summary>
    private static AppSocket? open;

    private readonly Socket listener;
    private readonly CancellationTokenSource closed = new();

    private AppSocket(string path, Socket listener)
    {
        Path = path;
        this.listener = listener;
        // Not held: it listens for as long as the process has a window agents reach.
        IdeErrors.Observe(Task.Run(AcceptAsync));
    }

    public string Path { get; }

    /// <summary>The file name a process's socket has.</summary>
    internal static string NameOf(int processId) => $"{processId}.open";

    /// <summary>Listens for agents asking the process to open a folder, unless it does already; a socket that cannot
    /// open leaves agents to start a Passenger of their own on it.</summary>
    internal static void Listen()
    {
        UiThread.Check();
        if (open is not null)
        {
            return;
        }
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        string path = NameOf(Environment.ProcessId);
        try
        {
            path = System.IO.Path.Combine(McpHost.OwnSocketFolder(), path);
            File.Delete(path);
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(4);
        }
        catch (Exception error) when (error is IOException or SocketException or UnauthorizedAccessException or ArgumentException)
        {
            listener.Dispose();
            IdeErrors.Write($"Agents: Passenger cannot listen for agents asking it to open a folder: {error.Message}");
            return;
        }
        open = new AppSocket(path, listener);
    }

    /// <summary>Stops listening once no window of the process does.</summary>
    internal static void Close()
    {
        UiThread.Check();
        open?.Dispose();
        open = null;
    }

    public void Dispose()
    {
        if (closed.IsCancellationRequested)
        {
            return;
        }
        closed.Cancel();
        listener.Dispose();
        try
        {
            File.Delete(Path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Left behind, it is taken away as stale by the next Passenger to look.
        }
    }

    /// <summary>Asks the running Passengers, through their sockets, to open a folder, the newest first: the folder it
    /// opened in one, or null when none is running or none would. Each request has 30 seconds, including connection.</summary>
    internal static async Task<string?> AskAsync(string folder, CancellationToken cancel)
    {
        if (McpHost.FoundSocketFolder() is not { } sockets)
        {
            return null;
        }
        foreach (string path in Directory.EnumerateFiles(sockets, "*.open").OrderByDescending(File.GetLastWriteTimeUtc))
        {
            using Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                limit.CancelAfter(TimeSpan.FromSeconds(30));
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), limit.Token);
                await using NetworkStream stream = new(socket, ownsSocket: false);
                byte[] asked = Encoding.UTF8.GetBytes(new JsonObject { ["open"] = folder }.ToJsonString() + "\n");
                await stream.WriteAsync(asked, limit.Token);
                using StreamReader reader = new(stream, Encoding.UTF8);
                if (await reader.ReadLineAsync(limit.Token) is { } line && JsonNode.Parse(line) is JsonObject answer
                    && answer["opened"]?.GetValue<string>() is { } opened)
                {
                    return opened;
                }
            }
            catch (Exception error) when (error is SocketException or IOException or JsonException or InvalidOperationException
                || (error is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                // A Passenger that went, or one that would not: the next is asked.
            }
        }
        return null;
    }

    private async Task AcceptAsync()
    {
        while (!closed.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(closed.Token);
            }
            catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            IdeErrors.Observe(AnswerAsync(client));
        }
    }

    private async Task AnswerAsync(Socket client)
    {
        try
        {
            await using NetworkStream stream = new(client, ownsSocket: true);
            using StreamReader reader = new(stream, Encoding.UTF8);
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(closed.Token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            JsonObject answer;
            try
            {
                string? line = await reader.ReadLineAsync(limit.Token);
                answer = line is not null && line.Length < 8192 && JsonNode.Parse(line) is JsonObject asked && asked["open"]?.GetValue<string>() is { Length: > 0 } path
                    ? await UiThread.InvokeAsync(() => Open(path))
                    : new JsonObject { ["refused"] = "Passenger takes {\"open\": FOLDER} here and nothing else" };
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
            {
                answer = new JsonObject { ["refused"] = error.Message };
            }
            await stream.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n"), limit.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Opens a folder as the system's Open does, but where it is asked: in the window in front when that has
    /// nothing open, else in a window of its own, or where it is open already.</summary>
    private static JsonObject Open(string path)
    {
        UiThread.Check();
        if (!System.IO.Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            return new JsonObject { ["refused"] = $"No folder at {path}" };
        }
        if (PassengerApp.Front is not { IsStarted: true, AgentsAllowed: true } from)
        {
            return new JsonObject { ["refused"] = "Passenger has no window ready to open it from" };
        }
        string folder = Core.PathNames.Canonical(path);
        IdeErrors.Write($"Agents: an agent asked Passenger to open {folder}");
        MainWindow into = PassengerApp.OpenProject(folder, OpenTarget.NewWindow, from);
        return new JsonObject { ["opened"] = into.Folder ?? folder };
    }
}
