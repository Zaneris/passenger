using Passenger.Text;

namespace Passenger.Workspace.GDScript;

/// <summary>One of a script's own members (a function, variable, constant, signal, enum, inner class, or an
/// <c>@export_group</c> annotation) and the offset its node's positions count from.</summary>
public readonly record struct GDScriptMember(GDScriptNode Node, int Offset)
{
    public int Start => Offset + Node.Start;

    public int End => Offset + Node.End;
}

/// <summary>A GDScript file parsed as Godot parses it: its class's first lines, its members and Godot's own errors,
/// immutable. It is kept as runs of members, each begun at a clean line start (the script's members are parsed between
/// them, <see cref="GDScriptParser"/>), so <see cref="Reparse"/> after an edit parses only from the run the edit is in
/// until it reaches a run start it already had, and takes the runs after it as they are, their offsets moved.</summary>
public sealed class GDScriptTree
{
    /// <summary>One run: the text from one clean member start to the next, its members and errors counted from its
    /// start, and the indentation character the file had chosen there.</summary>
    private sealed record Run(int Length, IReadOnlyList<GDScriptNode> Members, IReadOnlyList<GDScriptError> Errors, IReadOnlyList<GDScriptAnnotation> Regions, char IndentChar,
        TextRange? Stopped = null);

    private readonly Run[] runs;
    private readonly int[] starts;
    private IReadOnlyList<GDScriptMember>? members;
    private IReadOnlyList<GDScriptError>? allErrors;

    private GDScriptTree(string text, GDScriptClass @class, Run[] runs, int parsed)
    {
        Text = text;
        RunsParsed = parsed;
        Class = @class;
        this.runs = runs;
        starts = new int[runs.Length];
        for (int i = 1; i < runs.Length; i++)
        {
            starts[i] = starts[i - 1] + runs[i - 1].Length;
        }
    }

    public string Text { get; }

    /// <summary>The script's own class as its first lines declare it (annotations, <c>class_name</c>, <c>extends</c>),
    /// positions counted from the file's start; its members are <see cref="Members"/>.</summary>
    public GDScriptClass Class { get; }

    /// <summary>How many runs it holds, each parsed apart.</summary>
    public int Runs => runs.Length;

    /// <summary>How many of its runs the parse that made it parsed; the rest it took from the tree before.</summary>
    public int RunsParsed { get; }

    /// <summary>The script's own members in the order written.</summary>
    public IReadOnlyList<GDScriptMember> Members => members ??= [.. runs.SelectMany((run, i) => run.Members.Select(m => new GDScriptMember(m, starts[i])))];

    /// <summary>Godot's errors for the script, in the order its parser finds them, then members named twice and warning
    /// regions begun or ended amiss.</summary>
    public IReadOnlyList<GDScriptError> Errors => allErrors ??= [.. runs.SelectMany((run, i) => run.Errors.Select(e => e with { Start = Clamped(e.Start + starts[i]), End = Clamped(e.End + starts[i]) }))
        .Concat(GDScriptParser.Duplicates(Members.Select(m => (m.Node, m.Offset))))
        .Concat(GDScriptParser.RegionErrors(Text, runs.SelectMany((run, i) => run.Regions.Select(a => (a, starts[i])))))];

    /// <summary>Where the parser stopped, nested past what it reads (<see cref="GDScriptParser.TooDeep"/>), the text after it
    /// unread and its members unknown; null for a script read to its end.</summary>
    public TextRange? Stopped => runs[^1].Stopped is { } stopped ? TextRange.FromBounds(Clamped(stopped.Start + starts[^1]), Clamped(stopped.End + starts[^1])) : null;

    private int Clamped(int offset) => Math.Clamp(offset, 0, Text.Length);

    /// <summary>The <c>@warning_ignore_start</c> and <c>@warning_ignore_restore</c> annotations in the order written, each
    /// with the offset its positions count from.</summary>
    internal IEnumerable<(GDScriptAnnotation Annotation, int Offset)> Regions => runs.SelectMany((run, i) => run.Regions.Select(a => (a, starts[i])));

    public static GDScriptTree Parse(string text)
    {
        GDScriptParser parser = GDScriptParser.Of(text);
        GDScriptClass @class = parser.ParsePreamble();
        List<Run> made = [];
        ParseRuns(parser, text, 0, '\0', made, null);
        return new GDScriptTree(text, @class, [.. made], made.Count);
    }

    /// <summary>The tree of the text a document's change made of this tree's text, its edits as the change gives them
    /// (sorted and apart, in this tree's text): parsed again from the run the first reaches.</summary>
    public GDScriptTree Reparse(string text, IReadOnlyList<TextEdit> edits) =>
        edits.Count == 0 && text.Length == Text.Length ? this : Reparse(text, GDScriptTextChange.Of(edits).Edit(text));

    /// <summary>The tree of the text one edit of this tree's text made: the runs before the edit's run kept, its run and
    /// those after parsed again until a run start lands where one of this tree's did, the file's indentation character
    /// as it was there, and the runs from there on kept with their offsets moved.</summary>
    public GDScriptTree Reparse(string text, TextEdit edit)
    {
        int first = 0;
        for (int i = runs.Length - 1; i > 0; i--)
        {
            if (starts[i] < edit.Start)
            {
                first = i;
                break;
            }
        }
        if (first == 0)
        {
            return Parse(text);
        }
        int delta = edit.Text.Length - edit.Length;
        int editedEnd = edit.Start + edit.Text.Length;
        Dictionary<int, int> laterStarts = [];
        for (int i = first + 1; i < runs.Length; i++)
        {
            if (starts[i] >= edit.End)
            {
                laterStarts[starts[i] + delta] = i;
            }
        }
        List<Run> made = [.. runs.Take(first)];
        GDScriptParser parser = GDScriptParser.At(text, starts[first], runs[first].IndentChar);
        int kept = ParseRuns(parser, text, starts[first], runs[first].IndentChar, made,
            (at, indentChar) => at >= editedEnd && laterStarts.TryGetValue(at, out int old) && runs[old].IndentChar == indentChar ? old : -1);
        int parsed = made.Count - first;
        if (kept >= 0)
        {
            made.AddRange(runs.Skip(kept));
        }
        return new GDScriptTree(text, Class, [.. made], parsed);
    }

    /// <summary>Parses runs from a parser's start to the text's end, or to a run start <paramref name="keep"/> names a kept
    /// run for (that run's index, else -1), adding each run made; the kept run's index, else -1.</summary>
    private static int ParseRuns(GDScriptParser parser, string text, int from, char indentChar, List<Run> made, Func<int, char, int>? keep)
    {
        int runStart = from;
        char runIndent = indentChar;
        int kept = -1;
        parser.ParseBody(at =>
        {
            made.Add(new Run(at - runStart, parser.TakeMembers(), Relative(parser.TakeErrors(), runStart), parser.TakeRegions(), runIndent));
            if (keep?.Invoke(at, parser.IndentChar) is >= 0 and int old)
            {
                kept = old;
                return true;
            }
            runStart = at;
            runIndent = parser.IndentChar;
            parser.CountFrom(at);
            return false;
        });
        if (kept < 0)
        {
            parser.Finish();
            TextRange? stopped = parser.Stopped is { } at ? TextRange.FromBounds(at.Start - runStart, at.End - runStart) : null;
            made.Add(new Run(text.Length - runStart, parser.TakeMembers(), Relative(parser.TakeErrors(), runStart), parser.TakeRegions(), runIndent, stopped));
        }
        return kept;
    }

    private static List<GDScriptError> Relative(List<GDScriptError> errors, int start) =>
        [.. errors.Select(e => e with { Start = e.Start - start, End = e.End - start })];
}
