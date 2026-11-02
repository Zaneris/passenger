namespace Passenger.App.Godot;

/// <summary>An error or warning Godot wrote to its console, as the console folds it: its text as first seen (its header line
/// and the indented lines after it), whether it is a warning, how many times it came, and when it last did.</summary>
public sealed class GodotFoldEntry(string text, bool warning, DateTime first)
{
    public string Text { get; internal set; } = text;

    public bool Warning { get; } = warning;

    public int Count { get; internal set; } = 1;

    public DateTime Last { get; internal set; } = first;

    /// <summary>Its header line, without its line break.</summary>
    public string Header => Text.Split('\n')[0].TrimEnd('\r');
}

/// <summary>What a console shows of a batch of a game's output: the stretches of the batch's text it keeps, in order; the
/// entries the batch saw first, each with where it starts among the text kept; and those it saw again.</summary>
public sealed record GodotFolded(IReadOnlyList<(int Start, int Length)> Kept, IReadOnlyList<(GodotFoldEntry Entry, int At)> Made, IReadOnlyList<GodotFoldEntry> Again);

/// <summary>Godot's errors and warnings in a console folded (<see cref="Take"/>): each entry (an <c>ERROR:</c> or
/// <c>WARNING:</c> line and the indented lines after it, its stack among them) shown where it was first seen and only
/// counted when it comes again, with when it last did; every other line kept as written. An entry a batch ends in may go
/// on in the next, whose indented lines at its start are that entry's.</summary>
public sealed class GodotConsoleFold
{
    private static readonly string[] Headers = ["ERROR: ", "WARNING: ", "SCRIPT ERROR: ", "SCRIPT WARNING: ", "USER ERROR: ", "USER WARNING: ",
        "SHADER ERROR: ", "SHADER WARNING: "];

    private readonly List<GodotFoldEntry> entries = [];
    private readonly Dictionary<string, GodotFoldEntry> byText = new(StringComparer.Ordinal);

    /// <summary>The entry the last batch ended in, and whether it was shown (seen first) rather than counted, which the next
    /// batch's indented lines at its start go on.</summary>
    private (GodotFoldEntry Entry, bool Shown)? open;

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
        bool atStart = true;
        int at = 0;
        while (at < text.Length)
        {
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
            if (midLine && at == 0 && open is { } cut)
            {
                // The rest of the line the last batch's entry ended inside.
                if (cut.Shown)
                {
                    byText.Remove(cut.Entry.Text);
                    cut.Entry.Text += text[at..end];
                    byText.TryAdd(cut.Entry.Text, cut.Entry);
                    pieces.Add((at, end - at));
                }
            }
            else if (IsHeader(line))
            {
                Finish();
                reading = new System.Text.StringBuilder().Append(line);
                current.Add(pieces.Count);
                pieces.Add((at, end - at));
            }
            else if (indented && reading is not null)
            {
                reading.Append(line);
                current.Add(pieces.Count);
                pieces.Add((at, end - at));
            }
            else if (indented && atStart && open is { } going)
            {
                // The entry the last batch ended in goes on: shown, it shows these too; counted, they are counted with it.
                if (going.Shown)
                {
                    byText.Remove(going.Entry.Text);
                    going.Entry.Text += text[at..end];
                    byText.TryAdd(going.Entry.Text, going.Entry);
                    pieces.Add((at, end - at));
                }
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
        midLine = text.Length > 0 && text[^1] != '\n' && open is not null;
        List<(int Start, int Length)> kept = [];
        List<(GodotFoldEntry, int)> made = [];
        int shown = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            if (dropped.Contains(i))
            {
                continue;
            }
            foreach ((GodotFoldEntry entry, int piece) in madeAt.Where(m => m.Piece == i))
            {
                made.Add((entry, shown));
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
            shown += length;
        }
        return new GodotFolded(kept, made, again);

        // An entry the batch ends in may be cut short: one an entry seen begins with is that one come again.
        void Finish(bool atEnd = false)
        {
            if (reading is null)
            {
                return;
            }
            string written = reading.ToString();
            atStart = false;
            if (byText.TryGetValue(written, out GodotFoldEntry? seen)
                || (atEnd && (seen = entries.FirstOrDefault(e => e.Text.StartsWith(written, StringComparison.Ordinal))) is not null))
            {
                seen.Count++;
                seen.Last = now;
                if (!again.Contains(seen))
                {
                    again.Add(seen);
                }
                dropped.UnionWith(current);
                open = (seen, false);
            }
            else
            {
                GodotFoldEntry entry = new(written, written.AsSpan(0, written.IndexOf(": ", StringComparison.Ordinal)).EndsWith("WARNING", StringComparison.Ordinal), now);
                entries.Add(entry);
                byText[written] = entry;
                madeAt.Add((entry, current[0]));
                open = (entry, true);
            }
            reading = null;
            current = [];
        }
    }

    /// <summary>An entry the console no longer holds (trimmed away, or cleared): it shows anew when it next comes.</summary>
    public void Forget(GodotFoldEntry entry)
    {
        entries.Remove(entry);
        if (byText.TryGetValue(entry.Text, out GodotFoldEntry? kept) && kept == entry)
        {
            byText.Remove(entry.Text);
        }
        if (open is { } going && going.Entry == entry)
        {
            open = null;
        }
    }

    /// <summary>Every entry forgotten, as the console is cleared.</summary>
    public void Clear()
    {
        entries.Clear();
        byText.Clear();
        open = null;
    }
}
