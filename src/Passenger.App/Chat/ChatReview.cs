using Passenger.Core;
using Passenger.Text;

namespace Passenger.App.Chat;

/// <summary>The files the agent changed in this chat that the person has neither kept nor undone, as Cursor and Stoat
/// review them: each file's bytes as they were before the agent's first change to it (none for a file it made), and its
/// text once the agent's last change to it was done. Taken on the agent's connection's thread as the agent names the
/// file, which it does before it writes; read and changed on the window's.</summary>
public sealed class ChatReview
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, Entry> files = new(StringComparer.Ordinal);

    /// <summary>A file's bytes were taken before the agent changed it: on the agent's connection's thread.</summary>
    public event Action<string>? Taken;

    /// <summary>The files to review, in the order the agent first changed them.</summary>
    public IReadOnlyList<ReviewedFile> Files
    {
        get
        {
            lock (gate)
            {
                return [.. files.Values.Where(entry => entry.After is not null).OrderBy(entry => entry.Order).Select(entry => entry.Shown())];
            }
        }
    }

    /// <summary>Takes a file's bytes before the agent changes it, once until it is kept or undone: a later change of the
    /// agent's adds to what the first one began.</summary>
    public void Before(string path)
    {
        string full = PathNames.Canonical(path);
        lock (gate)
        {
            if (files.TryGetValue(full, out Entry? known))
            {
                known.Edits++;
                return;
            }
            files[full] = new Entry(full, File.Exists(full) ? File.ReadAllBytes(full) : null, files.Count);
        }
        Taken?.Invoke(full);
    }

    /// <summary>The agent's change to a file is done: its text now, and how many lines it added and removed in all.</summary>
    public void After(string path)
    {
        string full = PathNames.Canonical(path);
        string? now = File.Exists(full) ? File.ReadAllText(full) : null;
        lock (gate)
        {
            if (files.TryGetValue(full, out Entry? entry))
            {
                entry.After = now ?? string.Empty;
                entry.Deleted = now is null;
            }
        }
    }

    /// <summary>An edit the person answered before it was written, accepted or rejected, is reviewed already: it leaves
    /// the file's review, and the file with it where no other edit of the agent's waits there.</summary>
    public void Answered(string path)
    {
        string full = PathNames.Canonical(path);
        lock (gate)
        {
            if (files.TryGetValue(full, out Entry? entry) && --entry.Edits <= 0)
            {
                files.Remove(full);
            }
        }
    }

    /// <summary>The file is kept, or was undone: it leaves the review.</summary>
    public void Done(string path)
    {
        lock (gate)
        {
            files.Remove(PathNames.Canonical(path));
        }
    }

    /// <summary>A file in review: its bytes then, text then and now, and what is told of it.</summary>
    private sealed class Entry(string path, byte[]? before, int order)
    {
        public int Order { get; } = order;

        public int Edits { get; set; } = 1;

        public string? After { get; set; }

        public bool Deleted { get; set; }

        public ReviewedFile Shown()
        {
            string then = before is null ? string.Empty : Decoded(before);
            IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(LineDiff.Lines(then), LineDiff.Lines(After ?? string.Empty));
            return new ReviewedFile(path, before, then, After ?? string.Empty, hunks.Sum(h => h.NewCount), hunks.Sum(h => h.OldCount), Edits, Deleted);
        }

        private static string Decoded(byte[] bytes)
        {
            using StreamReader reader = new(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }
}

/// <summary>A file the agent changed: its bytes before (null where the agent made it), its text before and after, the
/// lines added and removed, how many of the agent's edits made it so, and whether the agent deleted it.</summary>
public sealed record ReviewedFile(string Path, byte[]? Before, string BeforeText, string AfterText, int Added, int Removed, int Edits, bool Deleted)
{
    public bool IsNew => Before is null;
}
