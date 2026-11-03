using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>The person's Godot editor open on a project, asked through its IDE messaging server for the names a script's
/// strings may take that only it knows (an input action not yet saved, the nodes of the scene it edits): found by the meta
/// file the editor writes into the project as it opens, connected once and kept while that file names it, never an editor
/// started for it. Passenger's own headless editors are never asked (<see cref="GodotScripts.Headless"/>): they read the
/// same files Passenger reads and edit no scene of the person's. A list waits for an answer only <see cref="Limit"/>; a
/// later one is kept for the next list. Held by the session, ended with it.</summary>
internal sealed class GodotIdeLink : IAsyncDisposable
{
    /// <summary>How long a list waits for the editor, whose answer comes on its next frame.</summary>
    internal static readonly TimeSpan Limit = Waits.Of(TimeSpan.FromMilliseconds(400));

    /// <summary>How long a connection's handshake, and an answer kept for a later list, are waited for.</summary>
    private static readonly TimeSpan Patience = Waits.Of(TimeSpan.FromSeconds(5));

    /// <summary>How long a meta file whose editor did not answer is passed over before it is tried again: an editor busy
    /// importing as it opens shakes no hands until it is done.</summary>
    private static readonly TimeSpan Again = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim connecting = new(1, 1);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Linked> linked = new(PathNames.Comparer);
    private readonly Dictionary<(string Project, GodotIdeKind Kind, string Script), IReadOnlyList<string>> kept = [];
    private bool ended;

    /// <summary>A project's editor as last found: what its meta file said, the connection made to it (null where none
    /// answered, not tried again until the file changes or <see cref="Again"/> has passed), and when it was tried.</summary>
    private sealed record Linked(string Meta, GodotIdeClient? Client, long Tried);

    /// <summary>The names the editor open on a project answers for a kind of string in a script (res://); none where no
    /// editor is open on it, or where it answers neither within <see cref="Limit"/> nor ever before while it stays connected.</summary>
    public async Task<IReadOnlyList<string>> AskAsync(string project, string script, GodotIdeKind kind, CancellationToken cancel)
    {
        (string, GodotIdeKind, string) key = (project, kind, script);
        Task<IReadOnlyList<string>?> fresh = BackgroundWork.Run(() => FreshAsync(project, script, kind));
        IdeErrors.Observe(KeepAsync(key, fresh));
        try
        {
            if (await fresh.WaitAsync(Limit, cancel) is { } answered)
            {
                return answered;
            }
        }
        catch (TimeoutException)
        {
            // Slow to answer: the list goes on with what it answered before.
        }
        lock (gate)
        {
            // An answer kept from an editor that has gone since would offer what no editor holds now.
            return linked.TryGetValue(project, out Linked? editor) && editor.Client is { Open: true } ? kept.GetValueOrDefault(key) ?? [] : [];
        }
    }

    /// <summary>An answer kept for the next list once it comes; every answer of a project dropped once no editor is open on it.</summary>
    private async Task KeepAsync((string Project, GodotIdeKind Kind, string Script) key, Task<IReadOnlyList<string>?> fresh)
    {
        IReadOnlyList<string>? answered = await fresh;
        lock (gate)
        {
            if (answered is not null)
            {
                kept[key] = answered;
            }
            else if (!linked.TryGetValue(key.Project, out Linked? editor) || editor.Client is null)
            {
                foreach ((string, GodotIdeKind, string) gone in kept.Keys.Where(k => PathNames.Same(k.Project, key.Project)).ToList())
                {
                    kept.Remove(gone);
                }
            }
        }
    }

    /// <summary>The editor's answer now, waited for at most <see cref="Patience"/>; null where none is open or it answers none.</summary>
    private async Task<IReadOnlyList<string>?> FreshAsync(string project, string script, GodotIdeKind kind)
    {
        if (await EditorAsync(project) is not { } client)
        {
            return null;
        }
        using CancellationTokenSource within = new(Patience);
        try
        {
            GodotIdeMessage? answer = await client.AskAsync(GodotIdeMessaging.CodeCompletion, GodotIdeMessaging.CompletionAsked(kind, script), within.Token);
            return answer is { Request: false, Status: GodotIdeMessaging.Ok } ? GodotIdeMessaging.CompletionAnswered(answer.Body) : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The connection to the editor a project's meta file names: the one kept while the file says the same, else a
    /// new one; null where the file is not there (no editor open), names one of Passenger's own headless editors, or names a
    /// port where no Godot editor answers (one gone without taking its file away, or not ready yet).</summary>
    private async Task<GodotIdeClient?> EditorAsync(string project)
    {
        string? meta;
        try
        {
            string path = GodotIdeMessaging.MetaPath(project);
            meta = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            meta = null;
        }
        await connecting.WaitAsync();
        try
        {
            Linked? before;
            lock (gate)
            {
                if (ended)
                {
                    return null;
                }
                linked.TryGetValue(project, out before);
            }
            if (before is { } same && same.Meta == meta && (same.Client is { Open: true } || same.Client is null && System.Diagnostics.Stopwatch.GetElapsedTime(same.Tried) < Again))
            {
                return same.Client;
            }
            if (before?.Client is { } old)
            {
                await old.DisposeAsync();
            }
            GodotIdeClient? client = meta is not null && GodotIdeMessaging.MetaOf(meta) is ({ } port, _) && !GodotScripts.Headless(port)
                ? await GodotIdeClient.ConnectAsync(port, Patience, CancellationToken.None) : null;
            if (client is not null)
            {
                IdeErrors.Write($"Godot: the editor open on {project} answers on port {client.Port}");
            }
            lock (gate)
            {
                if (ended)
                {
                    IdeErrors.Observe(client?.DisposeAsync().AsTask() ?? Task.CompletedTask);
                    return null;
                }
                if (meta is null)
                {
                    linked.Remove(project);
                }
                else
                {
                    linked[project] = new Linked(meta, client, System.Diagnostics.Stopwatch.GetTimestamp());
                }
            }
            return client;
        }
        finally
        {
            connecting.Release();
        }
    }

    /// <summary>Whether the person's editor is open on a project and answers.</summary>
    public async Task<bool> IsOpenAsync(string project) => await BackgroundWork.Run(() => EditorAsync(project)) is not null;

    /// <summary>Asks the editor open on a project to do what a request says (play, stop): null once it answered as asked, else
    /// why not, in words that follow "Godot's editor".</summary>
    public async Task<string?> RequestAsync(string project, string id, string body)
    {
        if (await BackgroundWork.Run(() => EditorAsync(project)) is not { } client)
        {
            return "is not open on the project";
        }
        using CancellationTokenSource within = new(Patience);
        try
        {
            GodotIdeMessage? answer = await client.AskAsync(id, body, within.Token);
            return answer is { Request: false, Status: GodotIdeMessaging.Ok } ? null
                : answer is null ? "closed before it answered" : $"answered {answer.Status}";
        }
        catch (OperationCanceledException)
        {
            return $"did not answer within {Patience.TotalSeconds:0} s";
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<GodotIdeClient> clients;
        lock (gate)
        {
            ended = true;
            clients = [.. linked.Values.Select(l => l.Client).OfType<GodotIdeClient>()];
            linked.Clear();
        }
        foreach (GodotIdeClient client in clients)
        {
            await client.DisposeAsync();
        }
    }
}
