namespace Passenger.App.Godot;

/// <summary>An error or warning Godot wrote to its console, as the console folds it: its text as first seen (its header line
/// and the indented lines after it), whether it is a warning, how many times it came, and when it last did.</summary>
public sealed class GodotFoldEntry(string text, bool warning, DateTime first)
{
    private string written = text;

    public string Text
    {
        get => written;
        internal set
        {
            written = value;
            Key = GodotConsoleFold.KeyOf(value);
        }
    }

    /// <summary>What tells it from another entry (<see cref="GodotConsoleFold.KeyOf"/>).</summary>
    internal string Key { get; private set; } = GodotConsoleFold.KeyOf(text);

    public bool Warning { get; } = warning;

    public int Count { get; internal set; } = 1;

    public DateTime Last { get; internal set; } = first;

    /// <summary>Its header line, without its line break.</summary>
    public string Header => Text.Split('\n')[0].TrimEnd('\r');
}

/// <summary>What a console shows of a batch of a game's output: the stretches of the batch's text it keeps, in order, after
/// <see cref="Held"/>; the entries the batch saw first, each with where it starts among the text shown; and those it saw
/// again. Held is the start of an entry an earlier batch ended in, taken then for one seen, that this batch showed to be
/// another: shown first, as it came before the rest.</summary>
public sealed record GodotFolded(IReadOnlyList<(int Start, int Length)> Kept, IReadOnlyList<(GodotFoldEntry Entry, int At)> Made, IReadOnlyList<GodotFoldEntry> Again,
    string Held);

/// <summary>Godot's errors and warnings in a console folded (<see cref="Take"/>): each entry (an <c>ERROR:</c> or
/// <c>WARNING:</c> line and the indented lines after it, its stack among them) shown where it was first seen and only
/// counted when it comes again, however Godot's own frames came (<see cref="KeyOf"/>), with when it last did; every other
/// line kept as written. An entry a batch ends in may go on in the next: one that may yet be an entry seen is counted as
/// that one until the next batch says what it is.</summary>
public sealed class GodotConsoleFold
{
    private static readonly string[] Headers = ["ERROR: ", "WARNING: ", "SCRIPT ERROR: ", "SCRIPT WARNING: ", "USER ERROR: ", "USER WARNING: ",
        "SHADER ERROR: ", "SHADER WARNING: "];

    private const string Backtrace = " backtrace (most recent call first):";

    private const string SharpBacktrace = "C#" + Backtrace;

    private readonly List<GodotFoldEntry> entries = [];
    private readonly Dictionary<string, GodotFoldEntry> byKey = new(StringComparer.Ordinal);

    /// <summary>The entry the last batch ended in where it was shown (seen first), which the next batch's indented lines at its
    /// start go on.</summary>
    private GodotFoldEntry? open;

    /// <summary>The entry the last batch ended in where it was counted as one seen: its text so far, when it came, and the
    /// last time of the one it was counted as before that.</summary>
    private (GodotFoldEntry Seen, string Text, DateTime When, DateTime SeenLast)? carried;

    /// <summary>Whether the last batch ended inside a line of the entry it ended in, which the next batch's first line ends.</summary>
    private bool midLine;

    /// <summary>The entries in the order first seen.</summary>
    public IReadOnlyList<GodotFoldEntry> Entries => entries;

    /// <summary>Whether a line starts an entry of Godot's.</summary>
    public static bool IsHeader(ReadOnlySpan<char> line)
    {
        foreach (string header in Headers)
        {
            if (line.StartsWith(header, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>What tells an entry from another, so that one error is one entry however Godot's own frames come: its lines,
    /// each trimmed and a frame's number left out, but the frames of Godot's own C#, which the JIT may inline into one
    /// another as a game runs, and, where a C# backtrace follows, an <c>at:</c> line naming a C# method, which Godot finds
    /// by counting those frames.</summary>
    internal static string KeyOf(string text) => string.Join('\n', Keyed(text, whole: true).Lines);

    /// <summary>An entry's lines as <see cref="KeyOf"/> keeps them. Of one that may go on (not <paramref name="whole"/>): a
    /// last line cut short left out, and where its C# <c>at:</c> line stands while no C# backtrace has come to leave it out.</summary>
    private static (List<string> Lines, int Undecided) Keyed(string text, bool whole)
    {
        List<string> lines = [];
        int at = -1;
        bool sharp = false;
        bool backtraced = false;
        int start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0 && !whole)
            {
                break;
            }
            int end = newline < 0 ? text.Length : newline;
            ReadOnlySpan<char> line = text.AsSpan(start, end - start).Trim();
            start = end + 1;
            if (line.IsEmpty)
            {
                continue;
            }
            if (lines.Count > 0 && at < 0 && line.StartsWith("at: ", StringComparison.Ordinal) && NamesMethod(line[4..]))
            {
                at = lines.Count;
            }
            else if (line.EndsWith(Backtrace, StringComparison.Ordinal))
            {
                sharp = line.Equals(SharpBacktrace, StringComparison.Ordinal);
                backtraced |= sharp;
            }
            else if (Numbered(line, out ReadOnlySpan<char> frame))
            {
                if (sharp && IsGodots(frame))
                {
                    continue;
                }
                line = frame;
            }
            else if (line.StartsWith("at ", StringComparison.Ordinal) && IsGodots(line[3..]))
            {
                continue;
            }
            lines.Add(line.ToString());
        }
        if (backtraced && at >= 0)
        {
            lines.RemoveAt(at);
            at = -1;
        }
        return (lines, at);
    }

    /// <summary>Whether an <c>at:</c> line's place names a C# method ("void Main._Process(double) (res://Main.cs:10)") rather
    /// than the engine's function or a script's.</summary>
    private static bool NamesMethod(ReadOnlySpan<char> place)
    {
        int file = place.IndexOf(" (", StringComparison.Ordinal);
        return (file < 0 ? place : place[..file]).Contains('(');
    }

    /// <summary>Whether a line is a backtrace's frame, given without its number ("[2] ").</summary>
    private static bool Numbered(ReadOnlySpan<char> line, out ReadOnlySpan<char> frame)
    {
        int close = line.Length > 3 && line[0] == '[' ? line.IndexOf("] ", StringComparison.Ordinal) : -1;
        bool numbered = close > 1 && int.TryParse(line[1..close], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _);
        frame = numbered ? line[(close + 2)..] : default;
        return numbered;
    }

    /// <summary>Whether a C# frame ("void Godot.GD.PushError(string) (…)", or .NET's "Godot.Node.Call(…) in …") is a method of
    /// Godot's own, its type in the Godot namespace: what comes before its parameters, past a return type.</summary>
    private static bool IsGodots(ReadOnlySpan<char> frame)
    {
        int parameters = frame.IndexOf('(');
        if (parameters < 0)
        {
            return false;
        }
        ReadOnlySpan<char> named = frame[..parameters];
        int space = named.IndexOf(' ');
        int generic = named.IndexOf('<');
        if (space >= 0 && (generic < 0 || space < generic))
        {
            named = named[(space + 1)..];
        }
        return named.StartsWith("Godot.", StringComparison.Ordinal);
    }

    /// <summary>An entry seen that one a batch ends in may yet be: its whole lines begin that entry's key, with its C# <c>at:</c>
    /// line or, where that entry's C# backtrace may still come, without it; a header it ends inside begins that entry's.</summary>
    private GodotFoldEntry? MayBe(string written)
    {
        (List<string> lines, int undecided) = Keyed(written, whole: false);
        if (lines.Count == 0)
        {
            string header = written.TrimEnd();
            return entries.FirstOrDefault(e => e.Key.StartsWith(header, StringComparison.Ordinal));
        }
        string with = string.Join('\n', lines);
        string? without = undecided < 0 ? null : string.Join('\n', lines.Where((_, i) => i != undecided));
        return entries.FirstOrDefault(e => Begins(e.Key, with)
            || (without is not null && Begins(e.Key, without) && e.Key.Contains("\n" + SharpBacktrace, StringComparison.Ordinal)));

        static bool Begins(string key, string lines) =>
            key.StartsWith(lines, StringComparison.Ordinal) && (key.Length == lines.Length || key[lines.Length] == '\n');
    }

    /// <summary>A batch of output folded, as of <paramref name="now"/>. Where <paramref name="errors"/> tells which lines
    /// (by where they start) Godot wrote to its errors' stream, only those make entries: the game's own lines, which the
    /// other stream may put between an entry's, are kept as written and break no entry.</summary>
    public GodotFolded Take(string text, DateTime now, Func<int, bool>? errors = null)
    {
        List<(int Start, int Length)> pieces = [];
        HashSet<int> dropped = [];
        List<(GodotFoldEntry Entry, int Piece)> madeAt = [];
        List<GodotFoldEntry> again = [];
        List<int> current = [];
        System.Text.StringBuilder? reading = null;
        DateTime began = now;
        string? earlier = null;
        string held = string.Empty;
        if (carried is { } going)
        {
            // The entry the last batch ended in goes on here: counted once what it is is known.
            going.Seen.Count--;
            going.Seen.Last = going.SeenLast;
            again.Add(going.Seen);
            reading = new System.Text.StringBuilder(going.Text);
            earlier = going.Text;
            began = going.When;
            carried = null;
        }
        bool atStart = true;
        bool cut = midLine;
        int at = 0;
        int last = 0;
        while (at < text.Length)
        {
            last = at;
            int newline = text.IndexOf('\n', at);
            int end = newline < 0 ? text.Length : newline + 1;
            ReadOnlySpan<char> line = text.AsSpan(at, end - at);
            if (errors?.Invoke(at) == false)
            {
                pieces.Add((at, end - at));
                at = end;
                continue;
            }
            bool indented = line.Length > 0 && line[0] is ' ' or '\t' && !line.Trim().IsEmpty;
            bool rest = cut;
            cut = false;
            if (rest && reading is not null)
            {
                // The rest of the line the last batch's entry ended inside.
                reading.Append(line);
                current.Add(pieces.Count);
                pieces.Add((at, end - at));
            }
            else if (rest && open is { } shown)
            {
                Grow(shown, text[at..end]);
                pieces.Add((at, end - at));
            }
            else if (IsHeader(line))
            {
                Finish();
                reading = new System.Text.StringBuilder().Append(line);
                began = now;
                current.Add(pieces.Count);
                pieces.Add((at, end - at));
            }
            else if (indented && reading is not null)
            {
                reading.Append(line);
                current.Add(pieces.Count);
                pieces.Add((at, end - at));
            }
            else if (indented && atStart && open is { } growing)
            {
                // The entry the last batch ended in, shown, goes on: it shows these too.
                Grow(growing, text[at..end]);
                pieces.Add((at, end - at));
            }
            else
            {
                Finish();
                open = null;
                atStart = false;
                pieces.Add((at, end - at));
            }
            at = end;
        }
        Finish(atEnd: true);
        midLine = (text.Length > 0 ? text[^1] != '\n' && errors?.Invoke(last) != false : cut) && (open is not null || carried is not null);
        List<(int Start, int Length)> kept = [];
        List<(GodotFoldEntry, int)> made = [.. madeAt.Where(m => m.Piece < 0).Select(m => (m.Entry, 0))];
        int shownAt = held.Length;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (dropped.Contains(i))
            {
                continue;
            }
            foreach ((GodotFoldEntry entry, int piece) in madeAt.Where(m => m.Piece == i))
            {
                made.Add((entry, shownAt));
            }
            (int start, int length) = pieces[i];
            if (kept.Count > 0 && kept[^1].Start + kept[^1].Length == start)
            {
                kept[^1] = (kept[^1].Start, kept[^1].Length + length);
            }
            else
            {
                kept.Add((start, length));
            }
            shownAt += length;
        }
        return new GodotFolded(kept, made, again, held);

        // An entry the batch ends in may go on in the next: one that may yet be an entry seen is counted as that one meanwhile.
        void Finish(bool atEnd = false)
        {
            if (reading is null)
            {
                return;
            }
            string written = reading.ToString();
            atStart = false;
            GodotFoldEntry? seen = byKey.GetValueOrDefault(KeyOf(written));
            if (seen is null && atEnd)
            {
                seen = MayBe(written);
            }
            if (seen is not null)
            {
                if (atEnd)
                {
                    carried = (seen, written, began, seen.Last);
                }
                seen.Count++;
                seen.Last = began;
                if (!again.Contains(seen))
                {
                    again.Add(seen);
                }
                dropped.UnionWith(current);
                open = null;
            }
            else
            {
                GodotFoldEntry entry = new(written, written.AsSpan(0, written.IndexOf(": ", StringComparison.Ordinal)).EndsWith("WARNING", StringComparison.Ordinal), began);
                entries.Add(entry);
                byKey.TryAdd(entry.Key, entry);
                if (earlier is not null)
                {
                    // Taken for one seen as the last batch ended, its start was not shown: it shows before this batch's text.
                    held = earlier;
                    madeAt.Add((entry, -1));
                }
                else
                {
                    madeAt.Add((entry, current[0]));
                }
                open = entry;
            }
            reading = null;
            earlier = null;
            current = [];
        }
    }

    /// <summary>A shown entry gone on: its text, and so its key, takes the lines that came.</summary>
    private void Grow(GodotFoldEntry entry, string more)
    {
        if (byKey.TryGetValue(entry.Key, out GodotFoldEntry? kept) && kept == entry)
        {
            byKey.Remove(entry.Key);
        }
        entry.Text += more;
        byKey.TryAdd(entry.Key, entry);
    }

    /// <summary>An entry the console no longer holds (trimmed away, or cleared): it shows anew when it next comes.</summary>
    public void Forget(GodotFoldEntry entry)
    {
        entries.Remove(entry);
        if (byKey.TryGetValue(entry.Key, out GodotFoldEntry? kept) && kept == entry)
        {
            byKey.Remove(entry.Key);
        }
        if (open == entry)
        {
            open = null;
        }
    }

    /// <summary>Every entry forgotten, as the console is cleared.</summary>
    public void Clear()
    {
        entries.Clear();
        byKey.Clear();
        open = null;
        carried = null;
        midLine = false;
    }
}
