using Passenger.App.CSharp;
using Passenger.Lsp;
using Passenger.Text;

namespace Passenger.App.Debugging;

/// <summary>Where the lines of the build a session debugs stand in the files edited since it began, and back, until a
/// rebuild. The debugger knows only the built lines, as the compiler counted them: a breakpoint moved by an edit is still
/// sent at its built line, so an edit that only moves breakpoints sends nothing, and a stop is shown where its code now is.
/// A line goes as a breakpoint does (<see cref="TextEditing.MapLine"/>), and back through each edit's inverse. A UI-thread
/// object.</summary>
public sealed class BuiltLines
{
    private readonly Dictionary<string, Lines> files = new(StringComparer.Ordinal);

    /// <summary>Each file's built text's lines as its compiler counted them into the PDB (<see cref="ServerPlaces.Compiled"/>:
    /// Roslyn's, XAML's an XML reader's) where they are not the editor's, else null, as in most files.</summary>
    private readonly Dictionary<string, ServerLines?> compiled = new(StringComparer.Ordinal);

    /// <summary>A file's text as the editor has it, else as on disk: the built text until the file's first edit.</summary>
    private readonly Func<string, TextSnapshot?> texts;

    /// <summary>While a mark is kept (<see cref="Mark"/>), each file edited since: its lines then mapped to its lines now, and
    /// its lines then as its compiler counts them.</summary>
    private Dictionary<string, (Lines Lines, ServerLines? Compiled)>? marked;

    /// <summary>Each file Hot Reload has given the program since the build: the build's lines carried on through its edits,
    /// and as its compiler counted them, for a program restarted on the build (<see cref="FromBuild"/>).</summary>
    private readonly Dictionary<string, (Lines Lines, ServerLines? Compiled)> ofBuild = new(StringComparer.Ordinal);

    public BuiltLines(Func<string, TextSnapshot?> texts) => this.texts = texts;

    /// <summary>Carries a file's lines through an edit to it.</summary>
    public void Follow(string path, DocumentChangedEventArgs change)
    {
        if (change.Edits.Count == 0)
        {
            return;
        }
        if (ServerPlaces.CountedApart(path) && !compiled.ContainsKey(path))
        {
            compiled[path] = ServerPlaces.Compiled(path, change.Before);
        }
        if (!files.TryGetValue(path, out Lines? lines))
        {
            lines = new Lines(change.Before.LineCount);
            files[path] = lines;
        }
        lines.Follow(change);
        if (ofBuild.TryGetValue(path, out (Lines Lines, ServerLines? Compiled) build))
        {
            build.Lines.Follow(change);
        }
        if (marked is not null)
        {
            if (!marked.TryGetValue(path, out (Lines Lines, ServerLines? Compiled) since))
            {
                since = (new Lines(change.Before.LineCount), ServerPlaces.CountedApart(path) ? ServerPlaces.Compiled(path, change.Before) : null);
                marked[path] = since;
            }
            since.Lines.Follow(change);
        }
    }

    /// <summary>The files edited since the build.</summary>
    public IReadOnlyCollection<string> Files => files.Keys;

    /// <summary>Keeps the files' lines as they are now apart from their edits to come, until <see cref="Unmark"/>: the text
    /// Hot Reload's update is being made of.</summary>
    public void Mark() => marked = new Dictionary<string, (Lines Lines, ServerLines? Compiled)>(StringComparer.Ordinal);

    /// <summary>Ends the mark: each file's lines follow its edits as one map again.</summary>
    public void Unmark() => marked = null;

    /// <summary>A file's lines at the mark (or now, with none kept) are the built lines, its edits since carried on from
    /// them: Hot Reload has given the program its code as the update was made of it.</summary>
    public void Reset(string path)
    {
        if (!ofBuild.ContainsKey(path) && files.TryGetValue(path, out Lines? sinceBuild))
        {
            ofBuild[path] = (sinceBuild, compiled.GetValueOrDefault(path));
        }
        files.Remove(path);
        compiled.Remove(path);
        if (marked is not null && marked.Remove(path, out (Lines Lines, ServerLines? Compiled) since))
        {
            files[path] = since.Lines;
            compiled[path] = since.Compiled;
        }
    }

    /// <summary>The build's lines as they stand in the files now, for a program restarted on the build without the edits Hot
    /// Reload gave the last: a map of its own, which follows the edits it is given from now.</summary>
    public BuiltLines FromBuild()
    {
        BuiltLines carried = new(texts);
        foreach ((string path, Lines lines) in files.Where(f => !ofBuild.ContainsKey(f.Key)))
        {
            carried.files[path] = lines.Copy();
            if (compiled.TryGetValue(path, out ServerLines? counted))
            {
                carried.compiled[path] = counted;
            }
        }
        foreach ((string path, (Lines lines, ServerLines? counted)) in ofBuild)
        {
            carried.files[path] = lines.Copy();
            if (counted is not null)
            {
                carried.compiled[path] = counted;
            }
        }
        return carried;
    }

    /// <summary>Where a built line (from zero, the compiler's) of a file is now, as the editor counts lines.</summary>
    public int ToCurrent(string path, int line) => ToCurrent(path, line, 0).Line;

    /// <summary>Where a built place (line and column from zero, the compiler's) of a file is now: its line where that line's
    /// code now is, its column the one in the editor's line it was built on.</summary>
    public TextPosition ToCurrent(string path, int line, int column)
    {
        TextPosition built = Compiled(path) is { } lines ? lines.Text.GetPosition(lines.OffsetOf(line, column)) : new TextPosition(line, column);
        return files.TryGetValue(path, out Lines? edited) ? built with { Line = edited.ToCurrent(built.Line) } : built;
    }

    /// <summary>The built line (from zero, the compiler's) a line of a file was; for one written since, the built line its
    /// code goes before.</summary>
    public int ToBuilt(string path, int line) => ToBuilt(path, line, 0).Line;

    /// <summary>The built place (line and column from zero, the compiler's) a place in a file now was: its line's built line,
    /// at that column in it, where a break only the compiler counts may have begun another line.</summary>
    public TextPosition ToBuilt(string path, int line, int column)
    {
        int built = files.TryGetValue(path, out Lines? edited) ? edited.ToBuilt(line) : line;
        if (Compiled(path) is not { } lines)
        {
            return new TextPosition(built, column);
        }
        LineSpan span = lines.Text.GetLine(Math.Clamp(built, 0, lines.Text.LineCount - 1));
        LspPosition at = lines.PositionOf(span.Start + Math.Clamp(column, 0, span.Length));
        return new TextPosition(at.Line, at.Character);
    }

    /// <summary>Whether a line's code is the build's, rather than written since: no breakpoint can stop on one that is not.</summary>
    public bool IsBuilt(string path, int line) => ToCurrent(path, ToBuilt(path, line)) == line;

    /// <summary>A file's built lines as its compiler counted them where they are not the editor's, each file read once.</summary>
    private ServerLines? Compiled(string path)
    {
        if (!ServerPlaces.CountedApart(path))
        {
            return null;
        }
        if (!compiled.TryGetValue(path, out ServerLines? lines))
        {
            compiled[path] = lines = ServerPlaces.Compiled(path, texts(path));
        }
        return lines;
    }

    /// <summary>A file's built lines and its lines now, each mapped to the other as runs (<see cref="LineRuns"/>): an edit
    /// carries one by one only the lines it reaches and moves the runs past it whole, so a key costs what it changed, not a
    /// look at every line of the file and, as it adds or takes a line away, an array of them.</summary>
    private sealed class Lines
    {
        /// <summary>Each built line's line now.</summary>
        private readonly LineRuns current;

        /// <summary>Each line's built line.</summary>
        private readonly LineRuns built;

        public Lines(int count)
        {
            current = new LineRuns(count);
            built = new LineRuns(count);
        }

        private Lines(LineRuns current, LineRuns built)
        {
            this.current = current;
            this.built = built;
        }

        /// <summary>A map of its own, as this one is now.</summary>
        public Lines Copy() => new(current.Copy(), built.Copy());

        public int ToCurrent(int line) => current.At(line);

        public int ToBuilt(int line) => built.At(line);

        /// <summary>Built lines now before the edit's first line stay, those after its last move by the lines it added or took
        /// away, and those in it are carried one by one; each line now in it is carried back through the edit's inverse.</summary>
        public void Follow(DocumentChangedEventArgs change)
        {
            int first = change.Before.GetLineFromOffset(change.Edits[0].Start);
            int last = change.Before.GetLineFromOffset(change.Edits[^1].End);
            int added = change.After.LineCount - change.Before.LineCount;
            current.CarryValues(change, first, last, added);
            built.CarryLines(change, first, last, added);
        }
    }

    /// <summary>A map of a file's lines to lines, kept as runs over its lines in order: each run a stretch of lines mapped to a
    /// stretch one for one, or all to one line (those an edit took away), a line's run found by halving. An edit splits
    /// only the runs it reaches; those past it move whole.</summary>
    private sealed class LineRuns
    {
        private List<Stretch> runs;

        /// <summary>The list an edit's runs are made into, swapped with <see cref="runs"/>: no list made for each key.</summary>
        private List<Stretch> spare = [];

        public LineRuns(int count) => runs = [new Stretch(0, Math.Max(count, 1), 0, Flat: false)];

        private LineRuns(List<Stretch> runs) => this.runs = runs;

        public LineRuns Copy() => new([.. runs]);

        private int Count => runs[^1].End;

        /// <summary>The line a line is mapped to; a line past the ends as the nearest one.</summary>
        public int At(int line)
        {
            int at = Math.Clamp(line, 0, Count - 1);
            int low = 0;
            int high = runs.Count - 1;
            while (low < high)
            {
                int middle = (low + high + 1) / 2;
                if (runs[middle].Start <= at)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            return runs[low].At(at);
        }

        /// <summary>The lines mapped to carried through an edit of the text they are lines of: those before its first line
        /// kept, those after its last moved by the lines it added, those in it carried one by one as a breakpoint is.</summary>
        public void CarryValues(DocumentChangedEventArgs change, int first, int last, int added)
        {
            spare.Clear();
            foreach (Stretch run in runs)
            {
                if (run.Last < first)
                {
                    Add(spare, run);
                }
                else if (run.Value > last)
                {
                    Add(spare, run with { Value = run.Value + added });
                }
                else if (run.Flat)
                {
                    Add(spare, run with { Value = TextEditing.MapLine(change.Before, change.Edits, change.After, run.Value) });
                }
                else
                {
                    int before = Math.Clamp(first - run.Value, 0, run.Length);
                    int after = Math.Clamp(run.Last - last, 0, run.Length);
                    Add(spare, new Stretch(run.Start, before, run.Value, Flat: false));
                    for (int line = run.Start + before; line < run.End - after; line++)
                    {
                        Add(spare, new Stretch(line, 1, TextEditing.MapLine(change.Before, change.Edits, change.After, run.At(line)), Flat: false));
                    }
                    Add(spare, new Stretch(run.End - after, after, run.At(run.End - after) + added, Flat: false));
                }
            }
            (runs, spare) = (spare, runs);
        }

        /// <summary>The map carried through an edit of the text its own lines are of: the lines before the edit's first keep
        /// what they map to, those after its last move with their lines, and each line the edit's lines are now maps to what
        /// the line it was carried back to did.</summary>
        public void CarryLines(DocumentChangedEventArgs change, int first, int last, int added)
        {
            spare.Clear();
            int k = 0;
            for (; k < runs.Count && runs[k].End <= first; k++)
            {
                Add(spare, runs[k]);
            }
            if (k < runs.Count && runs[k].Start < first)
            {
                Add(spare, runs[k] with { Length = first - runs[k].Start });
            }
            for (int line = first; line <= last + added; line++)
            {
                Add(spare, new Stretch(line, 1, At(Was(change, line)), Flat: false));
            }
            int past = last + 1;
            while (k < runs.Count && runs[k].End <= past)
            {
                k++;
            }
            if (k < runs.Count && runs[k].Start < past)
            {
                Stretch cut = runs[k++];
                Add(spare, new Stretch(past + added, cut.End - past, cut.At(past), cut.Flat));
            }
            for (; k < runs.Count; k++)
            {
                Add(spare, runs[k] with { Start = runs[k].Start + added });
            }
            (runs, spare) = (spare, runs);
        }

        /// <summary>A run put after the last, as part of it where it goes on from it: one for one, or to the one line.</summary>
        private static void Add(List<Stretch> into, Stretch run)
        {
            if (run.Length <= 0)
            {
                return;
            }
            Stretch previous = into.Count > 0 ? into[^1] : default;
            if (into.Count > 0 && previous.End == run.Start)
            {
                if (!previous.Flat && !run.Flat && previous.Value + previous.Length == run.Value)
                {
                    into[^1] = previous with { Length = previous.Length + run.Length };
                    return;
                }
                if ((previous.Flat || previous.Length == 1) && (run.Flat || run.Length == 1) && previous.Last == run.Value)
                {
                    into[^1] = new Stretch(previous.Start, previous.Length + run.Length, run.Value, Flat: true);
                    return;
                }
            }
            into.Add(run);
        }

        /// <summary>The line before an edit a line after it was, as <see cref="TextEditing.MapLine"/> carries it through the
        /// edit's inverse: its start carried back, a start inside text the edit put in going to the end of what it replaced.</summary>
        private static int Was(DocumentChangedEventArgs change, int line)
        {
            int offset = change.After.GetLineStart(Math.Min(line, change.After.LineCount - 1));
            int shift = 0;
            for (int i = 0; i < change.Edits.Count; i++)
            {
                TextEdit edit = change.Edits[i];
                int start = edit.Start + shift;
                int end = start + edit.Text.Length;
                if (end <= offset)
                {
                    shift += edit.Text.Length - edit.Length;
                    continue;
                }
                if (start < offset && offset < end)
                {
                    return change.Before.GetLineFromOffset(Math.Min(edit.End, change.Before.Length));
                }
                break;
            }
            return change.Before.GetLineFromOffset(Math.Min(offset - shift, change.Before.Length));
        }
    }

    /// <summary>Lines from <see cref="Start"/> mapped one for one from <see cref="Value"/>, or each to it when flat.</summary>
    private readonly record struct Stretch(int Start, int Length, int Value, bool Flat)
    {
        public int End => Start + Length;

        public int Last => At(End - 1);

        public int At(int line) => Flat ? Value : Value + line - Start;
    }
}
