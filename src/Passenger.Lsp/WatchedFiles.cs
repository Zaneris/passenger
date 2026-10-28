using System.Text.Json;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Lsp;

/// <summary>The files a server registered to hear of as they change on disk (<c>workspace/didChangeWatchedFiles</c>),
/// which the client watches for it in place of a watcher of the server's own: each registration's watchers, a glob from a
/// folder (or over whole paths) with the kinds of change it asks for, until the server unregisters it. What is told
/// (<see cref="Tell"/>) goes to the server as one notification of the changes some watcher covers, in the order told;
/// one told while the server loads projects that no watcher covers yet is kept for the registration its load sends, which
/// may arrive after it. Every registration and notification is written in the server's log. Thread-safe;
/// <see cref="Changed"/> is raised on the thread that registered or unregistered, <see cref="Sent"/> on the telling one.</summary>
public sealed class WatchedFiles
{
    /// <summary>How many changes are kept for a registration still to come: past it they are let go, said in the log.</summary>
    private const int KeptLimit = 100_000;

    private readonly Lock gate = new();
    private readonly Dictionary<string, IReadOnlyList<FileWatch>> registered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileChangeType> kept = new(PathNames.Comparer);
    private readonly LanguageServer server;
    private IReadOnlyList<FileWatch> watches = [];

    /// <summary>The loads the server was asked for that it has not yet said ended.</summary>
    private int loading;

    /// <summary>Whether changes were let go past <see cref="KeptLimit"/> in this load, said once.</summary>
    private bool overflowed;

    internal WatchedFiles(LanguageServer server) => this.server = server;

    /// <summary>When the server started: a file written since may have changed after it read it.</summary>
    public DateTime Started { get; } = DateTime.UtcNow;

    /// <summary>The watchers registered changed: one came or went.</summary>
    public event EventHandler? Changed;

    /// <summary>A notification sent the server, with the changes it told.</summary>
    public event EventHandler<IReadOnlyList<FileEvent>>? Sent;

    /// <summary>Every watcher registered now, of every registration.</summary>
    public IReadOnlyList<FileWatch> Watches
    {
        get
        {
            lock (gate)
            {
                return watches;
            }
        }
    }

    /// <summary>A registration's watchers taken as the server gave them (one with none it can read kept as none), and the
    /// changes kept for it told now.</summary>
    internal void Register(string id, JsonElement? options)
    {
        List<FileWatch> read = [];
        if (options is { ValueKind: JsonValueKind.Object } given && given.TryGetProperty("watchers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement watcher in list.EnumerateArray())
            {
                if (FileWatch.Read(watcher) is { } watch)
                {
                    read.Add(watch);
                }
            }
        }
        List<FileEvent> waited;
        lock (gate)
        {
            registered[id] = read;
            watches = [.. registered.Values.SelectMany(w => w)];
            waited = [.. kept.Select(k => new FileEvent(k.Key, k.Value)).Where(change => read.Any(watch => watch.Covers(change)))];
            foreach (FileEvent change in waited)
            {
                kept.Remove(change.Path);
            }
            server.Note($"Files watched for the server, registration {id}: {string.Join("; ", read.Select(w => w.Shown))}");
            Send(waited, "heard before it registered them");
        }
        Changed?.Invoke(this, EventArgs.Empty);
        Said(waited);
    }

    internal void Unregister(string id)
    {
        lock (gate)
        {
            if (!registered.Remove(id))
            {
                return;
            }
            watches = [.. registered.Values.SelectMany(w => w)];
            server.Note($"Files no longer watched for the server, registration {id}");
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The server was asked to load projects, whose registrations may follow changes told meanwhile.</summary>
    internal void Loading()
    {
        lock (gate)
        {
            loading++;
        }
    }

    /// <summary>The server said a load ended, after every registration it sent for it: once none is under way, changes no
    /// watcher covered are never asked for.</summary>
    internal void Loaded()
    {
        lock (gate)
        {
            loading = Math.Max(0, loading - 1);
            if (loading == 0)
            {
                kept.Clear();
                overflowed = false;
            }
        }
    }

    /// <summary>Tells the server of the changes some watcher covers, by their canonical paths, in one notification, keeping
    /// the rest while it loads projects; how many it was told of. Each is matched against every watcher, so the caller
    /// tells a batch from off the UI thread.</summary>
    public int Tell(IReadOnlyCollection<FileEvent> events)
    {
        List<FileEvent> covered = [];
        lock (gate)
        {
            foreach (FileEvent change in events)
            {
                if (watches.Any(watch => watch.Covers(change)))
                {
                    covered.Add(change);
                }
                else if (loading > 0 && (kept.Count < KeptLimit || kept.ContainsKey(change.Path)))
                {
                    kept[change.Path] = change.Change;
                }
                else if (loading > 0 && !overflowed)
                {
                    overflowed = true;
                    server.Note($"Changes on disk no registration covered were let go past {KeptLimit} as the server loaded projects");
                }
            }
            Send(covered, null);
        }
        Said(covered);
        return covered.Count;
    }

    /// <summary>The notification itself, under the gate, so what is told reaches the server in the order told.</summary>
    private void Send(List<FileEvent> changes, string? why)
    {
        if (changes.Count == 0)
        {
            return;
        }
        server.Notify(LspMethods.DidChangeWatchedFiles, new { changes = changes.Select(c => new { uri = new Uri(c.Path).AbsoluteUri, type = (int)c.Change }).ToList() });
        server.Note($"Told the server of {Plural.Of(changes.Count, "file")} changed on disk{(why is null ? string.Empty : ", " + why)}, {changes[0].Path} first");
    }

    private void Said(List<FileEvent> changes)
    {
        if (changes.Count > 0)
        {
            Sent?.Invoke(this, changes);
        }
    }
}

/// <summary>A change on disk as the protocol numbers it.</summary>
public enum FileChangeType
{
    Created = 1,
    Changed = 2,
    Deleted = 3,
}

/// <summary>A file or folder changed on disk, by its canonical path.</summary>
public readonly record struct FileEvent(string Path, FileChangeType Change);

/// <summary>One watcher a server registered: the paths under <see cref="Folder"/> whose names from it match
/// <see cref="Pattern"/> (with no folder, whole paths that match it), for the changes of <see cref="Kinds"/>.</summary>
public sealed record FileWatch(string? Folder, string Pattern, WatchKinds Kinds)
{
    /// <summary>The watcher as a log shows it.</summary>
    public string Shown => $"{Folder}{(Folder is null ? "" : " ")}{Pattern} ({Kinds})";

    /// <summary>Whether the pattern reaches into the folders under <see cref="Folder"/>, where it can match a path of more
    /// than one name.</summary>
    public bool Recursive => Pattern.Contains('/', StringComparison.Ordinal) || Pattern.Contains("**", StringComparison.Ordinal);

    /// <summary>Whether the watcher asks for this change of this path.</summary>
    public bool Covers(FileEvent change)
    {
        WatchKinds kind = change.Change switch
        {
            FileChangeType.Created => WatchKinds.Create,
            FileChangeType.Deleted => WatchKinds.Delete,
            _ => WatchKinds.Change,
        };
        if ((Kinds & kind) == 0)
        {
            return false;
        }
        string named;
        if (Folder is null)
        {
            named = change.Path;
        }
        else if (PathNames.IsUnder(change.Path, Folder))
        {
            named = PathNames.Below(change.Path, Folder).ToString();
        }
        else
        {
            return false;
        }
        string relative = "/" + named.Replace('\\', '/').TrimStart('/');
        string glob = "/" + Pattern.TrimStart('/');
        // Where the file system takes names in any letter case, so does the glob.
        return PathNames.Comparison == StringComparison.OrdinalIgnoreCase
            ? EditorConfig.Matches(glob.ToLowerInvariant(), relative.ToLowerInvariant())
            : EditorConfig.Matches(glob, relative);
    }

    /// <summary>A watcher as the protocol gives it: its glob a string, or a pattern relative to a folder or workspace
    /// folder's URI; its kinds every change unless it names them. Null for one that cannot be read.</summary>
    internal static FileWatch? Read(JsonElement watcher)
    {
        if (watcher.ValueKind != JsonValueKind.Object || !watcher.TryGetProperty("globPattern", out JsonElement glob))
        {
            return null;
        }
        WatchKinds kinds = watcher.TryGetProperty("kind", out JsonElement kind) && kind.TryGetInt32(out int given) ? (WatchKinds)given : WatchKinds.All;
        if (glob.ValueKind == JsonValueKind.String)
        {
            return new FileWatch(null, glob.GetString()!, kinds);
        }
        if (glob.ValueKind != JsonValueKind.Object || !glob.TryGetProperty("pattern", out JsonElement pattern) || pattern.ValueKind != JsonValueKind.String
            || !glob.TryGetProperty("baseUri", out JsonElement place))
        {
            return null;
        }
        string? uri = place.ValueKind switch
        {
            JsonValueKind.String => place.GetString(),
            JsonValueKind.Object when place.TryGetProperty("uri", out JsonElement folder) && folder.ValueKind == JsonValueKind.String => folder.GetString(),
            _ => null,
        };
        return uri is not null && Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) && parsed.IsFile
            ? new FileWatch(DocumentUris.ToPath(uri), pattern.GetString()!, kinds)
            : null;
    }
}

/// <summary>The kinds of change a watcher asks for, as the protocol's flags.</summary>
[Flags]
public enum WatchKinds
{
    None = 0,
    Create = 1,
    Change = 2,
    Delete = 4,
    All = Create | Change | Delete,
}
