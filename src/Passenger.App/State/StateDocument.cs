using System.Collections.Concurrent;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.State;

/// <summary>What a kind of document knows of its own shape: the steps that bring a file an older build wrote up to
/// this build's, and how to bring into range a value read from a file it did not write (a hand edit, a later build's).</summary>
public interface IStateDocument<TSelf> where TSelf : class, IStateDocument<TSelf>, new()
{
    /// <summary>Each step takes a document from the version of its index to the next, the first from a file written
    /// before documents had versions; the version this build writes is their count.</summary>
    static abstract IReadOnlyList<Action<JsonObject>> Upgrades { get; }

    /// <summary>The properties (by their C# names) whose lists are sets, in no order and with no repeats: a change merges as
    /// what it added and took away, so what another side added or took away meanwhile stays. Any other list is one value,
    /// taken whole from the side that changed it, as its order may matter.</summary>
    static virtual IReadOnlyList<string> Sets => [];

    /// <summary>The value with whatever this build cannot use brought into range, or back to its default.</summary>
    TSelf Clamped();
}

/// <summary>One file of the store, read the first time it is wanted and clamped. A change is written a moment later,
/// off the UI thread, through a temporary file flushed to disk and moved over the old one; it is merged over what the
/// file holds then, so a value this build does not know, or one another Passenger changed, stays. A file that does not
/// read is set aside as <c>.bad</c>, logged and said in a window keeping its state there, never written over.</summary>
public sealed class StateDocument<T> where T : class, IStateDocument<T>, new()
{
    /// <summary>How long a change waits before it is written, so a burst of them (a drag, an edit moving breakpoints)
    /// is written once.</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

    private readonly string? formerly;
    private readonly object writing;
    private readonly object gate = new();
    private T? value;
    private bool kept;

    /// <summary>The value last read or set, as written: a change to the same is none.</summary>
    private string? latest;

    /// <summary>The file as this side last read or wrote it, held under <see cref="writing"/>: what differs from it in a
    /// change is what this side changed.</summary>
    private string? known;

    private string? pending;
    private bool scheduled;
    private bool readOnly;

    /// <summary>A document at a path; <paramref name="formerly"/> is the file an older build kept the same value in, read
    /// while this one does not exist yet and left where it is.</summary>
    internal StateDocument(string path, string? formerly = null)
    {
        Path = path;
        this.formerly = formerly;
        writing = StateFile.Lock(path);
    }

    public string Path { get; }

    /// <summary>The value: as read and clamped, the defaults when there is no file or it did not read, or as set last.</summary>
    public T Value
    {
        get
        {
            Load();
            return value!;
        }
    }

    /// <summary>Whether a value is kept, read from the file or set since, rather than only the defaults.</summary>
    public bool IsKept
    {
        get
        {
            Load();
            return kept;
        }
    }

    /// <summary>Keeps a value, written a moment later unless it is the one kept already.</summary>
    public void Set(T next)
    {
        Load();
        value = next;
        kept = true;
        string text = JsonSerializer.Serialize(next, StateFile.Options);
        if (text == latest)
        {
            return;
        }
        latest = text;
        lock (gate)
        {
            pending = text;
            if (scheduled)
            {
                return;
            }
            scheduled = true;
        }
        IdeErrors.Observe(Task.Delay(Delay).ContinueWith(_ => Write(), TaskScheduler.Default));
    }

    /// <summary>Writes a change still waiting, now, on the caller's thread: as the window closes, since the process does
    /// not wait for a timer.</summary>
    public void Flush() => Write();

    /// <summary>Reads the file again, once a change still waiting is written: what another Passenger wrote since comes
    /// in. A file gone or not reading leaves the value as it is (this is no first read, which sets a bad one aside).
    /// Whether the value changed.</summary>
    public bool Reread()
    {
        Load();
        Write();
        lock (writing)
        {
            if (readOnly || !File.Exists(Path))
            {
                return false;
            }
            T read;
            try
            {
                read = (Upgraded(File.ReadAllText(Path)).Document.Deserialize<T>(StateFile.Options) ?? throw new JsonException("it holds null")).Clamped();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException || StateFile.DoesNotRead(error))
            {
                return false;
            }
            string text = JsonSerializer.Serialize(read, StateFile.Options);
            known = text;
            if (text == latest)
            {
                return false;
            }
            latest = text;
            value = read;
            kept = true;
            return true;
        }
    }

    /// <summary>The file's value as read and clamped now, on any thread, without taking it in.</summary>
    public T Peek() => ReadNow().Value;

    /// <summary>Changes the value as the file holds it now and writes the change at once, merged over the file as a
    /// <see cref="Set"/>'s is, on any thread: for a document no window keeps. A document a window keeps is changed through
    /// <see cref="Set"/>.</summary>
    public void ChangeNow(Func<T, T> change)
    {
        lock (writing)
        {
            (T read, _, string text) = ReadNow();
            known = text;
            string mine = JsonSerializer.Serialize(change(read).Clamped(), StateFile.Options);
            if (mine == text)
            {
                return;
            }
            lock (gate)
            {
                pending = mine;
            }
            Write();
        }
    }

    /// <summary>The value, read off the UI thread the first time and taken in on it, for a document large enough that
    /// reading it there would hold the window up (a solution's project caches, megabytes each).</summary>
    public async Task<T> ValueAsync()
    {
        UiThread.Check();
        if (value is null)
        {
            (T Value, bool Kept, string Known) read = await BackgroundWork.Run(ReadNow);
            if (value is null)
            {
                Take(read);
            }
        }
        return value!;
    }

    private void Load()
    {
        // The value and what was set last are the UI thread's; only the writing crosses to another.
        UiThread.Check();
        if (value is null)
        {
            Take(ReadNow());
        }
    }

    /// <summary>The file's value as read and clamped, whether one was kept, and its text: on any thread.</summary>
    private (T Value, bool Kept, string Known) ReadNow()
    {
        lock (writing)
        {
            string? file = File.Exists(Path) ? Path : formerly is not null && File.Exists(formerly) ? formerly : null;
            T? read = file is null ? null : Read(file);
            T clamped = (read ?? new T()).Clamped();
            return (clamped, read is not null, JsonSerializer.Serialize(clamped, StateFile.Options));
        }
    }

    private void Take((T Value, bool Kept, string Known) read)
    {
        lock (writing)
        {
            kept = read.Kept;
            value = read.Value;
            known = read.Known;
            latest = known;
        }
    }

    /// <summary>A file's value, or null when it did not read: it is then set aside, or when it cannot be, left alone and
    /// not written over this run.</summary>
    private T? Read(string file)
    {
        try
        {
            return Upgraded(File.ReadAllText(file)).Document.Deserialize<T>(StateFile.Options) ?? throw new JsonException("it holds null");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            readOnly |= file == Path;
            StateFile.Log($"{file} could not be read ({error.Message}): the defaults stand, and it is not written this run");
            return null;
        }
        catch (Exception error) when (StateFile.DoesNotRead(error))
        {
            readOnly |= !StateFile.SetAside(file, error.Message) && file == Path;
            return null;
        }
    }

    /// <summary>Writes the change waiting, if there is one, merged over the file as it is now.</summary>
    private void Write()
    {
        lock (writing)
        {
            string? mine;
            lock (gate)
            {
                mine = pending;
                pending = null;
                scheduled = false;
            }
            if (mine is null || readOnly)
            {
                return;
            }
            try
            {
                if (OnDisk() is not { } disk)
                {
                    return;
                }
                StateFile.Replace(Path, StateFile.Merged(disk.Document, Math.Max(disk.Version, T.Upgrades.Count), known, mine, T.Sets));
                known = mine;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                StateFile.Log($"{Path} could not be written ({error.Message})");
            }
        }
    }

    /// <summary>The file as it is now, brought up to this build's version, with the version it was written at: an empty
    /// one when there is none, and null when it did not read and could not be set aside.</summary>
    private (JsonObject Document, int Version)? OnDisk()
    {
        if (!File.Exists(Path))
        {
            return ([], 0);
        }
        string text = File.ReadAllText(Path);
        try
        {
            return Upgraded(text);
        }
        catch (Exception error) when (StateFile.DoesNotRead(error))
        {
            if (StateFile.SetAside(Path, error.Message))
            {
                return ([], 0);
            }
            readOnly = true;
            return null;
        }
    }

    /// <summary>A file's text as a document of this build's version, with the version it was written at.</summary>
    private static (JsonObject Document, int Version) Upgraded(string text)
    {
        if (JsonNode.Parse(text, documentOptions: StateFile.HandEdited) is not JsonObject document)
        {
            throw new JsonException("it is not a JSON object");
        }
        int version = document["version"] switch
        {
            null => 0,
            JsonValue number when number.TryGetValue(out int read) && read >= 0 => read,
            _ => throw new JsonException("its version is not a whole number"),
        };
        for (int step = version; step < T.Upgrades.Count; step++)
        {
            T.Upgrades[step](document);
        }
        return (document, version);
    }
}

/// <summary>What every document shares: its JSON, its file's lock in this process, the writing and setting aside of
/// its file, and the merge of a change over what the file holds.</summary>
internal static class StateFile
{
    /// <summary>Readable JSON: indented, camelCase, enums by name, characters as they are.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new EnumNames() },
    };

    /// <summary>What a person editing a file by hand may leave in it and still have it read.</summary>
    public static readonly JsonDocumentOptions HandEdited = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);

    /// <summary>The one lock for a file in this process, however many documents (windows) keep it.</summary>
    public static object Lock(string path) => Locks.GetOrAdd(path, _ => new object());

    public static void Log(string line) => IdeErrors.Write($"State: {line}");

    /// <summary>Whether an exception is a file's not reading: its JSON, its shape, or a value of the wrong kind.</summary>
    public static bool DoesNotRead(Exception error) =>
        error is JsonException or InvalidOperationException or ArgumentException or NotSupportedException or FormatException;

    /// <summary>The files set aside that no window has said yet, each with what a window says of it and where it is kept.</summary>
    private static readonly List<(string File, string Said, string Kept)> Unsaid = [];

    /// <summary>Raised on any thread once a file is set aside, for a window keeping its state there to say so.</summary>
    public static event Action? SetAsideNow;

    /// <summary>What a window keeping its state in a folder says of the files set aside there that no window has said
    /// yet, which are then said, with the .bad file each is kept as: the person's settings in a .bad file, the defaults
    /// standing, are never news only to the log.</summary>
    public static IReadOnlyList<(string Said, string Kept)> TakeUnsaid(string root)
    {
        lock (Unsaid)
        {
            List<(string File, string Said, string Kept)> taken = [.. Unsaid.Where(u => PathNames.IsUnder(u.File, Path.GetFullPath(root)))];
            Unsaid.RemoveAll(taken.Contains);
            return [.. taken.Select(t => (t.Said, t.Kept))];
        }
    }

    /// <summary>Moves a file that did not read to its <c>.bad</c> name (numbered past one there already), and logs it;
    /// false when it could not be moved.</summary>
    public static bool SetAside(string file, string reason)
    {
        string bad = file + ".bad";
        for (int n = 2; File.Exists(bad); n++)
        {
            bad = $"{file}.{n}.bad";
        }
        try
        {
            File.Move(file, bad);
            Log($"{file} did not read ({reason}): set aside as {Path.GetFileName(bad)}, and the defaults stand");
            lock (Unsaid)
            {
                Unsaid.Add((Path.GetFullPath(file), $"{Path.GetFileName(file)} did not read, so its defaults stand: what it held is kept as {bad}", bad));
            }
            SetAsideNow?.Invoke();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Log($"{file} did not read ({reason}) and could not be set aside ({error.Message}): it is not written this run");
            return false;
        }
    }

    /// <summary>Writes a document through a temporary file beside it, flushed to disk and moved over it, so a crash or
    /// a full disk leaves the old file whole; a symlink (settings.json kept in a dotfiles folder) is written through to
    /// its target, which keeps its permissions, rather than replaced by a file of its own.</summary>
    public static void Replace(string path, JsonObject document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string target = new FileInfo(path).LinkTarget is not null ? File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path : path;
        if (FileText.IsReadOnly(target))
        {
            // Made read-only to keep it as it is: logged as a write that failed, never moved over.
            throw new ReadOnlyFileException(path);
        }
        UnixFileMode? mode = !OperatingSystem.IsWindows() && File.Exists(target) ? File.GetUnixFileMode(target) : null;
        AtomicFile.Write(target, Encoding.UTF8.GetBytes(document.ToJsonString(Options) + "\n"), mode);
    }

    /// <summary>The file's document, its version first, with each value this side changed since it last read or wrote
    /// the file (<paramref name="known"/>) taken from <paramref name="mine"/>, and every other as the file has it; a list
    /// among <paramref name="sets"/> (<see cref="IStateDocument{TSelf}.Sets"/>) merged as a set.</summary>
    public static JsonObject Merged(JsonObject disk, int version, string? known, string mine, IReadOnlyList<string> sets)
    {
        disk.Remove("version");
        JsonObject written = new() { ["version"] = version };
        MergeInto(written, disk, known is null ? [] : (JsonObject)JsonNode.Parse(known)!, (JsonObject)JsonNode.Parse(mine)!, Named(sets));
        return written;
    }

    /// <summary>A value with what changed between two others put over it, value by value: what a window changed from the
    /// settings it had, over the settings as they are now.</summary>
    public static T Merged<T>(T now, T from, T to) where T : class, IStateDocument<T>, new()
    {
        JsonObject merged = [];
        MergeInto(merged, Node(now), Node(from), Node(to), Named(T.Sets));
        return (merged.Deserialize<T>(Options) ?? new T()).Clamped();

        static JsonObject Node(T value) => (JsonObject)JsonSerializer.SerializeToNode(value, Options)!;
    }

    /// <summary>Properties' C# names as the document's JSON names them.</summary>
    private static HashSet<string> Named(IReadOnlyList<string> properties) =>
        [.. properties.Select(p => Options.PropertyNamingPolicy?.ConvertName(p) ?? p)];

    /// <summary>Merges one object's values: the document's own, whose lists among <paramref name="sets"/> merge as sets,
    /// or (with none) an object inside it. In an object inside the document (a dictionary: shortcuts, recent solutions),
    /// an entry this side has unchanged that the file no longer has was removed by another side, and stays removed.</summary>
    private static void MergeInto(JsonObject into, JsonObject disk, JsonObject before, JsonObject after, HashSet<string>? sets)
    {
        bool nested = sets is null;
        foreach ((string name, JsonNode? there) in disk)
        {
            if (after.TryGetPropertyValue(name, out JsonNode? now))
            {
                into[name] = !before.TryGetPropertyValue(name, out JsonNode? was) ? now?.DeepClone()
                    : sets?.Contains(name) == true ? MergeSet(there, was, now)
                    : MergeValue(there, was, now);
            }
            else if (!before.ContainsKey(name))
            {
                // A value this build does not know (a later build's), or one another Passenger added.
                into[name] = there?.DeepClone();
            }
        }
        foreach ((string name, JsonNode? now) in after)
        {
            if (!disk.ContainsKey(name) && !(nested && before.TryGetPropertyValue(name, out JsonNode? was) && JsonNode.DeepEquals(was, now)))
            {
                into[name] = now?.DeepClone();
            }
        }
    }

    private static JsonNode? MergeValue(JsonNode? there, JsonNode? was, JsonNode? now)
    {
        if (there is JsonObject thereObject && was is JsonObject wasObject && now is JsonObject nowObject)
        {
            JsonObject merged = [];
            MergeInto(merged, thereObject, wasObject, nowObject, sets: null);
            return merged;
        }
        return (JsonNode.DeepEquals(was, now) ? there : now)?.DeepClone();
    }

    /// <summary>A set's merge: the file's members in its order, less those this side took away, then those it added.</summary>
    private static JsonNode? MergeSet(JsonNode? there, JsonNode? was, JsonNode? now)
    {
        if (there is not JsonArray theirs || was is not JsonArray before || now is not JsonArray after)
        {
            return MergeValue(there, was, now);
        }
        JsonArray merged = [];
        foreach (JsonNode? member in theirs.Concat(after))
        {
            bool kept = Has(theirs, member) ? !Has(before, member) || Has(after, member) : !Has(before, member);
            if (kept && !Has(merged, member))
            {
                merged.Add(member?.DeepClone());
            }
        }
        return merged;

        static bool Has(JsonArray set, JsonNode? member) => set.Any(m => JsonNode.DeepEquals(m, member));
    }

    /// <summary>Enums by name; a name this build does not have (a later build's) reads as no value of the enum, which
    /// the document's Clamped puts back to its default, rather than failing the whole file.</summary>
    private sealed class EnumNames : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(typeof(EnumName<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class EnumName<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && Enum.TryParse(reader.GetString(), ignoreCase: true, out TEnum named) && Enum.IsDefined(named))
            {
                return named;
            }
            reader.Skip();
            return (TEnum)Enum.ToObject(typeof(TEnum), -1);
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
