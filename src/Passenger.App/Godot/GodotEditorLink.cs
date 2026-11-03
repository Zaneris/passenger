using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Containers;
using Passenger.Core;

namespace Passenger.App.Godot;

/// <summary>A request of Passenger's addon refused, in the words the addon is answered with.</summary>
internal sealed class GodotEditorRefused(string words) : Exception(words);

/// <summary>The window's door for Passenger's addon in Godot's editor (<c>Godot/Addon/passenger.gd</c>): a listener on
/// loopback, named while it listens by a file in Passenger's state only the person reads (<see cref="Listings"/>), with
/// the project's folder, the port and two secrets. A connection's first line must be the first secret, checked before
/// anything else is read, as Aspire's bearer token is; Passenger then says the second, so the addon knows the Passenger
/// the file names answered, and each side asks the other in lines of JSON: a request's id, method and params, answered
/// by its id with a result or an error's words, a message with no id only told. Requests are answered on the window's
/// thread by their method's handler (<see cref="Handle"/>); Passenger asks an editor through
/// <see cref="GodotEditorPeer.AskAsync"/>.</summary>
internal sealed class GodotEditorLink : IAsyncDisposable
{
    /// <summary>The folder of Passenger's state that lists the windows listening, which the addon reads.</summary>
    public const string ListingFolder = "godot-editors";

    /// <summary>How long a connection has to say its secret.</summary>
    private static readonly TimeSpan Greeting = TimeSpan.FromSeconds(10);

    private readonly Dictionary<string, Func<GodotEditorPeer, JsonElement, Task<JsonObject>>> requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<GodotEditorPeer, JsonElement>> told = new(StringComparer.Ordinal);
    private readonly List<GodotEditorPeer> peers = [];
    private readonly CancellationTokenSource ending = new();
    private TcpListener? listener;
    private byte[] tokenHash = [];
    private string reply = string.Empty;
    private Task? accepting;

    /// <summary>The Godot project listened for, while it is; null before and after.</summary>
    public string? Project { get; private set; }

    /// <summary>The file naming this listener in Passenger's state, while it listens.</summary>
    public string? Listing { get; private set; }

    /// <summary>The editors connected and taken, on the window's thread.</summary>
    public IReadOnlyList<GodotEditorPeer> Editors
    {
        get
        {
            lock (peers)
            {
                return [.. peers.Where(p => p.Welcomed)];
            }
        }
    }

    /// <summary>An editor taken or gone, on the window's thread.</summary>
    public event EventHandler? EditorsChanged;

    /// <summary>An editor taken went, on the window's thread: what it relayed ends with it.</summary>
    public event EventHandler<GodotEditorPeer>? EditorGone;

    /// <summary>Where every window listening is listed: Passenger's own state, as the addon finds it.</summary>
    public static string Listings => Path.Combine(State.StateStore.DefaultRoot, ListingFolder);

    /// <summary>A request's handler by its method, answered with what it returns, or the words of a <see cref="GodotEditorRefused"/>.</summary>
    public void Handle(string method, Func<GodotEditorPeer, JsonElement, Task<JsonObject>> handler) => requests[method] = handler;

    /// <summary>A told message's handler by its method.</summary>
    public void Hear(string method, Action<GodotEditorPeer, JsonElement> handler) => told[method] = handler;

    /// <summary>Listens for the editors of a Godot project (its folder, and other spellings of it the addon may know it by),
    /// listing the listener in Passenger's state; one listening for another project stops first. Off the window's thread
    /// for the file.</summary>
    public async Task ListenAsync(string project, IReadOnlyList<string> spellings)
    {
        UiThread.Check();
        if (Project is { } listened && PathNames.Same(listened, project) || ending.IsCancellationRequested)
        {
            return;
        }
        Stop();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        reply = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        TcpListener started = new(IPAddress.Loopback, 0);
        started.Start();
        listener = started;
        Project = project;
        int port = ((IPEndPoint)started.LocalEndpoint).Port;
        string listing = Path.Combine(Listings, $"{Environment.ProcessId}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(4))}.json");
        Listing = listing;
        string text = GodotEditorWire.ListingText([project, .. spellings], port, token, reply, Environment.ProcessId);
        await BackgroundWork.Run(() =>
        {
            OwnFiles.Folder(Listings);
            Sweep(Listings);
            OwnFiles.Write(listing, text);
        });
        // Unheld: it accepts for as long as the folder is open, which no wait for the IDE to be quiet may wait out.
        accepting = BackgroundWork.Unheld(() => AcceptAsync(started, ending.Token));
    }

    /// <summary>The listings of Passengers no longer running, deleted.</summary>
    private static void Sweep(string folder)
    {
        foreach (string file in Directory.EnumerateFiles(folder, "*.json"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (int.TryParse(name.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture, out int process) && process != Environment.ProcessId
                && !DotnetProcesses.Runs(process))
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
                {
                    // A listing that cannot be deleted names a port no one answers, which the addon passes over.
                }
            }
        }
    }

    private async Task AcceptAsync(TcpListener from, CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await from.AcceptTcpClientAsync(cancel);
            }
            catch (Exception stopped) when (stopped is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            IdeErrors.Observe(ServeAsync(client, cancel));
        }
    }

    /// <summary>One connection: its secret checked, then its lines read until it ends.</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken cancel)
    {
        GodotEditorPeer? peer = null;
        try
        {
            NetworkStream stream = client.GetStream();
            if (!await GodotEditorWire.GreetedAsync(stream, tokenHash, reply, Waits.Of(Greeting), cancel))
            {
                client.Dispose();
                return;
            }
            peer = new GodotEditorPeer(client, stream);
            lock (peers)
            {
                peers.Add(peer);
            }
            await peer.ReadAsync(message => Heard(peer, message), cancel);
        }
        catch (Exception ended) when (ended is IOException or ObjectDisposedException or SocketException or OperationCanceledException)
        {
            // The editor went, or the folder closed: its connection ends.
        }
        finally
        {
            if (peer is not null)
            {
                bool welcomed = peer.Welcomed;
                lock (peers)
                {
                    peers.Remove(peer);
                }
                peer.Dispose();
                if (welcomed)
                {
                    UiThread.Post(() =>
                    {
                        EditorGone?.Invoke(this, peer);
                        EditorsChanged?.Invoke(this, EventArgs.Empty);
                    });
                }
            }
            client.Dispose();
        }
    }

    /// <summary>A message heard: an answer given to its request, a told one handed to its handler, a request answered by its
    /// handler on the window's thread apart from the reading, so a long one (a build) holds up no answer the editor sends
    /// meanwhile; the first request must be hello, naming this project, before which nothing else is taken.</summary>
    private async Task Heard(GodotEditorPeer peer, JsonElement message)
    {
        string? method = JsonMembers.Text(message, "method");
        int? id = JsonMembers.Number(message, "id");
        if (method is null)
        {
            if (id is int answered)
            {
                peer.Answered(answered, message.Clone());
            }
            return;
        }
        JsonElement parameters = (JsonMembers.Object(message, "params") ?? default).Clone();
        if (!peer.Welcomed && method != "hello")
        {
            throw new IOException($"Godot's editor asked {method} before hello");
        }
        if (id is not int asked)
        {
            if (told.TryGetValue(method, out Action<GodotEditorPeer, JsonElement>? hear))
            {
                await UiThread.InvokeAsync(() =>
                {
                    hear(peer, parameters);
                    return true;
                });
            }
            return;
        }
        if (method == "hello")
        {
            await AnswerAsync(peer, asked, () => UiThread.InvokeAsync(() => Hello(peer, parameters)));
            if (!peer.Welcomed)
            {
                throw new IOException("Godot's editor named another project");
            }
            return;
        }
        IdeErrors.Observe(AnswerAsync(peer, asked, () => requests.TryGetValue(method, out Func<GodotEditorPeer, JsonElement, Task<JsonObject>>? handle)
            ? UiThread.InvokeAsync(() => handle(peer, parameters))
            : throw new GodotEditorRefused($"Passenger takes no {method} from Godot's editor")));
    }

    /// <summary>A request answered with what its handler returns, or the words it was refused in.</summary>
    private static async Task AnswerAsync(GodotEditorPeer peer, int id, Func<Task<JsonObject>> handle)
    {
        JsonObject answer;
        try
        {
            answer = new JsonObject { ["id"] = id, ["result"] = await handle() };
        }
        catch (GodotEditorRefused refused)
        {
            answer = new JsonObject { ["id"] = id, ["error"] = refused.Message };
        }
        await peer.SendAsync(answer);
    }

    /// <summary>The editor's hello: taken where it names this project, its process and versions kept.</summary>
    private JsonObject Hello(GodotEditorPeer peer, JsonElement hello)
    {
        UiThread.Check();
        string? folder = JsonMembers.Text(hello, "folder");
        if (Project is not { } project || folder is null || !PathNames.Same(PathNames.Canonical(folder), project))
        {
            throw new GodotEditorRefused($"this Passenger window is open on {(Project is null ? "no Godot project" : PathNames.Shown(Project, null))}");
        }
        peer.Editor = JsonMembers.Number(hello, "editor") ?? 0;
        peer.Godot = JsonMembers.Text(hello, "godot");
        peer.Addon = JsonMembers.Number(hello, "addon") ?? 0;
        peer.Welcomed = true;
        EditorsChanged?.Invoke(this, EventArgs.Empty);
        return new JsonObject
        {
            ["passenger"] = Product.Version,
            ["said"] = "Play builds this project's C# in Passenger, and Passenger's debugger attaches to each game Play starts",
        };
    }

    /// <summary>Stops listening: its listing deleted, every editor's connection ended.</summary>
    private void Stop()
    {
        listener?.Stop();
        listener = null;
        Project = null;
        if (Listing is { } listing)
        {
            Listing = null;
            try
            {
                File.Delete(listing);
            }
            catch (Exception kept) when (kept is IOException or UnauthorizedAccessException)
            {
                // Left, it names a port no one answers, which the addon passes over and the next Passenger sweeps.
            }
        }
        lock (peers)
        {
            foreach (GodotEditorPeer peer in peers)
            {
                peer.Dispose();
            }
            peers.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ending.CancelAsync();
        Stop();
        if (accepting is { } accept)
        {
            await accept.Ended();
        }
        ending.Dispose();
    }
}

/// <summary>One Godot editor connected to the window through Passenger's addon: its process, Godot's version and the
/// addon's, its requests of Passenger answered and Passenger's of it asked.</summary>
internal sealed class GodotEditorPeer : IDisposable
{
    private readonly TcpClient client;
    private readonly Stream stream;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Dictionary<int, TaskCompletionSource<JsonElement?>> asked = [];
    private int lastAsked;

    public GodotEditorPeer(TcpClient client, Stream stream)
    {
        this.client = client;
        this.stream = stream;
    }

    /// <summary>The editor's process, which its games name as theirs.</summary>
    public int Editor { get; set; }

    /// <summary>Godot's version as the editor says it.</summary>
    public string? Godot { get; set; }

    /// <summary>The addon's version.</summary>
    public int Addon { get; set; }

    /// <summary>Whether its hello named the window's project.</summary>
    public bool Welcomed { get; set; }

    /// <summary>The games its sessions play, relayed into the window by session id, on the window's thread.</summary>
    public Dictionary<int, GodotGame> Games { get; } = [];

    /// <summary>The scene the editor edits, as it last said, which its games' live editing is set back to.</summary>
    public string EditedScene { get; set; } = string.Empty;

    /// <summary>The edits made since Play's last build, kept for each game it starts held, which takes its own copy.</summary>
    public Debugging.BuiltEdits? Kept { get; set; }

    /// <summary>Each line read handed on, until the connection ends.</summary>
    public async Task ReadAsync(Func<JsonElement, Task> heard, CancellationToken cancel)
    {
        using StreamReader reader = new(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(cancel) is { } line)
            {
                JsonDocument read;
                try
                {
                    read = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (read)
                {
                    if (read.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        await heard(read.RootElement);
                    }
                }
            }
        }
        finally
        {
            End();
        }
    }

    /// <summary>Asks the editor: its answer's result, or null where it refused, went, or did not answer within the limit.</summary>
    public async Task<JsonElement?> AskAsync(string method, JsonObject parameters, TimeSpan limit, CancellationToken cancel = default)
    {
        TaskCompletionSource<JsonElement?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (asked)
        {
            id = ++lastAsked;
            asked[id] = answer;
        }
        await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters });
        try
        {
            JsonElement? message = await answer.Task.WaitAsync(limit, cancel);
            return message is { } got && JsonMembers.Object(got, "result") is { } result ? result : null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (asked)
            {
                asked.Remove(id);
            }
        }
    }

    /// <summary>Tells the editor what needs no answer.</summary>
    public Task TellAsync(string method, JsonObject parameters) => SendAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    internal void Answered(int id, JsonElement message)
    {
        TaskCompletionSource<JsonElement?>? waiting;
        lock (asked)
        {
            asked.Remove(id, out waiting);
        }
        waiting?.TrySetResult(message);
    }

    /// <summary>A message written whole, one at a time.</summary>
    internal async Task SendAsync(JsonObject message)
    {
        byte[] line = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await writing.WaitAsync();
        try
        {
            await stream.WriteAsync(line);
        }
        catch (Exception gone) when (gone is IOException or ObjectDisposedException or SocketException)
        {
            // The editor went: what it was told is lost with it.
        }
        finally
        {
            writing.Release();
        }
    }

    /// <summary>Every request left ends unanswered.</summary>
    private void End()
    {
        lock (asked)
        {
            foreach (TaskCompletionSource<JsonElement?> waiting in asked.Values)
            {
                waiting.TrySetResult(null);
            }
            asked.Clear();
        }
    }

    public void Dispose()
    {
        End();
        client.Dispose();
    }
}
