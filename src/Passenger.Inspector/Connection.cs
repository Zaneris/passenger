using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Passenger.Core;

namespace Passenger.Inspector;

/// <summary>The app's end of the socket Passenger listens on: requests read a line at a time, and replies and events written
/// by a thread of its own, so the app's UI thread never waits on Passenger. Answers go before every event waiting, and an
/// event waits merged with what is still waiting of its kind: a binding failure's newer count in place of its older, one
/// word that the tree or the values changed, the last pick, the newest of what is untold. What could grow without end (the
/// inspector's own errors, each kept once by its words) waits only up to <see cref="MostWaiting"/>; past it the rest are
/// left out, counted and said as untold. Nothing here touches Avalonia or WPF.</summary>
internal sealed class Connection : IDisposable
{
    /// <summary>The unmerged events that may wait: past it another is left out. Those merged are bounded already, by their
    /// kinds, and binding failures by the most distinct ones the inspector sends.</summary>
    private const int MostWaiting = 1024;

    private readonly Socket socket;
    private readonly StreamReader reader;
    private readonly Stream stream;
    private readonly Thread writer;
    private readonly object gate = new();

    /// <summary>Answers to Passenger's requests, as many as it asked.</summary>
    private readonly Queue<Reply> answers = new();

    /// <summary>Events in the order first told, each in a slot a later one of its kind writes over until the writer takes it.</summary>
    private readonly Queue<Slot> events = new();
    private readonly Dictionary<string, Slot> waiting = new(StringComparer.Ordinal);

    /// <summary>The slots waiting that count towards <see cref="MostWaiting"/>.</summary>
    private int bounded;

    /// <summary>The binding failures the inspector counted but does not list, as it last said.</summary>
    private int untoldBindings;

    /// <summary>The events left out while <see cref="MostWaiting"/> waited.</summary>
    private int leftOut;

    private bool ending;

    private Connection(Socket socket)
    {
        this.socket = socket;
        stream = new NetworkStream(socket, ownsSocket: true);
        reader = new StreamReader(stream, new UTF8Encoding(false));
        Configure();
        writer = ApartThreads.Start("Passenger inspector writer", Write);
    }

    /// <summary>Has System.Text.Json make every type the wire carries ready now, before the app's Main, rather than on
    /// first use under its one lock, which the writer, stopped there by the debugger, would hold from the binding
    /// values evaluated at the stop.</summary>
    private static void Configure()
    {
        foreach (PropertyInfo property in typeof(WireJson).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(JsonTypeInfo).IsAssignableFrom(property.PropertyType))
            {
                _ = property.GetValue(WireJson.Default);
            }
        }
    }

    /// <summary>Connects to Passenger's socket, or null, having said why, when it cannot: a path past what a Unix socket's
    /// address holds, a folder gone, a socket refused (Passenger gone, the run not its). Never throws: the app runs on
    /// without the inspector.</summary>
    public static Connection? Open(string path)
    {
        if (path.StartsWith(Wire.Listens))
        {
            return Accept(path[1..]);
        }
        if (!SocketFolders.Fits(path))
        {
            NotIn(SocketFolders.TooLong(path));
            return null;
        }
        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return new Connection(socket);
        }
        catch (Exception error) when (error is SocketException or ArgumentException or IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidOperationException)
        {
            socket?.Dispose();
            NotIn($"it could not reach Passenger at {path}: {error.Message}");
            return null;
        }
    }

    /// <summary>How long an app on a device waits for Passenger to reach its socket before it runs on uninspected.</summary>
    private static readonly TimeSpan AcceptLimit = TimeSpan.FromSeconds(60);

    /// <summary>An app on a device listening on an abstract socket of this name, which Passenger reaches through adb's
    /// forward, taking the first client whose first line is the launch's token (<see cref="Wire.TokenVariable"/>); null,
    /// having said why, once none came in time. The app's start waits for it, so nothing it says before then is lost.</summary>
    private static Connection? Accept(string name)
    {
        string? token = Environment.GetEnvironmentVariable(Wire.TokenVariable);
        OwnEnvironment.Set(Wire.TokenVariable, null);
        if (token is not { Length: > 0 })
        {
            NotIn($"it was given no token for its socket {name}");
            return null;
        }
        using Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint("\0" + name));
            listener.Listen(1);
            long until = Environment.TickCount64 + (long)AcceptLimit.TotalMilliseconds;
            while (Environment.TickCount64 < until)
            {
                if (!listener.Poll(TimeSpan.FromMilliseconds(250), SelectMode.SelectRead))
                {
                    continue;
                }
                Socket client = listener.Accept();
                if (Said(client, token))
                {
                    return new Connection(client);
                }
                client.Dispose();
            }
            NotIn($"Passenger did not reach its socket {name} within {AcceptLimit.TotalSeconds:0} s");
            return null;
        }
        catch (Exception error) when (error is SocketException or IOException or ArgumentException)
        {
            NotIn($"it could not listen on its socket {name}: {error.Message}");
            return null;
        }
    }

    /// <summary>Whether a client's first line, read within a few seconds, is the token.</summary>
    private static bool Said(Socket client, string token)
    {
        client.ReceiveTimeout = 5000;
        List<byte> line = [];
        byte[] one = new byte[1];
        try
        {
            while (line.Count <= token.Length && client.Receive(one) == 1)
            {
                if (one[0] == (byte)'\n')
                {
                    client.ReceiveTimeout = 0;
                    return Encoding.UTF8.GetString([.. line]) == token;
                }
                line.Add(one[0]);
            }
        }
        catch (SocketException)
        {
            // Silent past its time, or gone: not Passenger.
        }
        return false;
    }

    /// <summary>Why the inspector is not in the app, in the app's own error output (its run's console) and the trace.</summary>
    private static void NotIn(string why)
    {
        string said = $"Passenger's inspector is not in this app, which runs on without it: {why}";
        InspectorTrace.Step(said);
        try
        {
            Console.Error.WriteLine(said);
        }
        catch (IOException)
        {
            // An app with no error output to write to has only the trace.
        }
    }

    /// <summary>Tells Passenger why the inspector cannot serve this app, and goes.</summary>
    public static void Refuse(string path, Version? avalonia, string problem)
    {
        using Connection? connection = Open(path);
        connection?.Send(new Reply { Event = Wire.Hello, Hello = new Hello(Wire.Version, Environment.ProcessId, avalonia?.ToString(), problem) });
        connection?.Drain();
    }

    /// <summary>The next request, or null once Passenger has gone.</summary>
    public Request? Read()
    {
        try
        {
            // A line this version cannot read is passed over; Passenger's own version check refuses a mismatch first.
            return JsonLines.Read(reader, WireJson.Default.Request, static (_, _) => { });
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Queues a reply or an event; from any thread, never waiting on Passenger, and nothing once it has gone.</summary>
    public void Send(Reply reply)
    {
        lock (gate)
        {
            if (ending)
            {
                // Passenger went while the app's thread was saying something (a binding failing).
                return;
            }
            if (reply.Id is not null)
            {
                answers.Enqueue(reply);
            }
            else if (!Tell(reply))
            {
                leftOut++;
                Tell(UntoldNow());
            }
            Monitor.Pulse(gate);
        }
    }

    /// <summary>Says how many binding failures the inspector has counted but does not list, in all; from any thread.</summary>
    public void Untold(int bindings)
    {
        lock (gate)
        {
            if (ending)
            {
                return;
            }
            untoldBindings = bindings;
            Tell(UntoldNow());
            Monitor.Pulse(gate);
        }
    }

    private Reply UntoldNow() => new() { Event = Wire.Untold, Untold = new Untold(untoldBindings, leftOut) };

    /// <summary>Puts an event in its kind's waiting slot, or in a new one; false when it is to be left out.</summary>
    private bool Tell(Reply told)
    {
        (string key, bool counts) = KindOf(told);
        if (waiting.TryGetValue(key, out Slot? slot))
        {
            slot.Reply = told;
            return true;
        }
        if (counts && bounded >= MostWaiting)
        {
            return false;
        }
        slot = new Slot(key, counts, told);
        waiting[key] = slot;
        events.Enqueue(slot);
        bounded += counts ? 1 : 0;
        return true;
    }

    /// <summary>What an event is merged by while it waits, and whether it counts towards <see cref="MostWaiting"/>.</summary>
    private static (string Key, bool Counts) KindOf(Reply told) => told switch
    {
        { Event: Wire.BindingFailed, Binding: { } failure } => ($"{Wire.BindingFailed} {failure.Id}", false),
        { Event: Wire.Picked or Wire.PickEnded } => (Wire.Picked, false),
        { Event: Wire.Hello or Wire.TreeChanged or Wire.ValuesChanged or Wire.Untold } => (told.Event, false),
        _ => ($"{told.Event}\n{told.Error}", true),
    };

    /// <summary>The next to write, answers first, waiting for one; null once ending with nothing left.</summary>
    private Reply? Next()
    {
        lock (gate)
        {
            while (true)
            {
                if (answers.TryDequeue(out Reply? answer))
                {
                    return answer;
                }
                if (events.TryDequeue(out Slot? slot))
                {
                    waiting.Remove(slot.Key);
                    bounded -= slot.Counts ? 1 : 0;
                    return slot.Reply;
                }
                if (ending)
                {
                    return null;
                }
                Monitor.Wait(gate);
            }
        }
    }

    /// <summary>Waits a moment for what is queued to be written, as the inspector gives up on an app it cannot serve.</summary>
    internal void Drain()
    {
        End();
        writer.Join(TimeSpan.FromSeconds(1));
    }

    /// <summary>Takes nothing more; the writer writes what waits and goes.</summary>
    private void End()
    {
        lock (gate)
        {
            ending = true;
            Monitor.Pulse(gate);
        }
    }

    private void Write()
    {
        try
        {
            while (Next() is { } reply)
            {
                byte[] line;
                try
                {
                    line = JsonSerializer.SerializeToUtf8Bytes(reply, WireJson.Default.Reply);
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    // What could not be written is said as the request's failure, rather than ending what the inspector says.
                    line = JsonSerializer.SerializeToUtf8Bytes(new Reply { Id = reply.Id, Event = reply.Event, Error = $"The inspector could not write its answer: {error.Message}" }, WireJson.Default.Reply);
                }
                stream.Write(line);
                stream.WriteByte((byte)'\n');
                stream.Flush();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
        {
            // Passenger went: the reader sees it too.
        }
    }

    public void Dispose()
    {
        End();
        try
        {
            socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        reader.Dispose();
    }

    /// <summary>An event waiting, written over by a later one of its kind until the writer takes it.</summary>
    private sealed class Slot(string key, bool counts, Reply reply)
    {
        public string Key { get; } = key;

        public bool Counts { get; } = counts;

        public Reply Reply { get; set; } = reply;
    }
}
