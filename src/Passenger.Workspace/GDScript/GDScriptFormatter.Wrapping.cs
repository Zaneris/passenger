using System.Text;
using Passenger.Text;
using K = Passenger.Workspace.GDScript.GDScriptTokenKind;

namespace Passenger.Workspace.GDScript;

/// <summary>Line length ("Keep individual lines of code under 100 characters"): a line past the length asked is wrapped
/// inside a bracket it opens and closes, in the guide's own forms, where that makes it shorter. An array, dictionary or
/// enum puts an element to a line, one level in, its bracket closing on a line of its own (Indentation, Trailing comma); a
/// call's or a declaration's arguments run on at two levels in, as many to a line as fit (Indentation's
/// <c>interpolate_property</c>); a parenthesized expression puts its contents two levels in, breaking before <c>and</c>,
/// <c>or</c> and a conditional's <c>else</c>, its parenthesis closing on a line of its own (Multiline statements). Godot
/// reads no line break inside brackets, but where a lambda lies, which is left as written, as is a subscript.</summary>
public static partial class GDScriptFormatter
{
    /// <summary>Rounds of wrapping, each a bracket deeper on a line still too long.</summary>
    private const int MostWrapRounds = 12;

    /// <summary>A laid out script (its tokens moved to where the layout put them) with its long lines wrapped, then laid
    /// out again; null where no line was. The tokens are moved with each round's edits, never read again: the result is
    /// read once whole, after.</summary>
    private static string? Wrapped(string laid, List<GDScriptToken> tokens, GDScriptFormatOptions options)
    {
        string now = laid;
        for (int round = 0; round < MostWrapRounds && Longest(now, options.TabWidth) > options.LineLength; round++)
        {
            List<(int Start, int End)> breaks = Breaks(now, tokens, options);
            if (breaks.Count == 0)
            {
                break;
            }
            List<TextEdit> broken = [.. breaks.Select(gap => new TextEdit(gap.Start, gap.End - gap.Start, "\n"))];
            string text = Applied(now, broken);
            tokens = Shifted(tokens, broken);
            List<TextEdit> again = Layout(text, tokens, null, options);
            now = Applied(text, again);
            tokens = Shifted(tokens, again);
        }
        return now == laid ? null : now;
    }

    /// <summary>Tokens moved past edits made in the gaps between them, a trailing comma an edit begins with among them.
    /// Their newlines and indents stay in their order, which is all that is read of them.</summary>
    private static List<GDScriptToken> Shifted(List<GDScriptToken> tokens, IReadOnlyList<TextEdit> edits)
    {
        List<GDScriptToken> moved = new(tokens.Count + 8);
        int e = 0;
        int delta = 0;
        foreach (GDScriptToken token in tokens)
        {
            for (; e < edits.Count && edits[e].Start + edits[e].Length <= token.Start; e++)
            {
                if (edits[e].Text.StartsWith(','))
                {
                    moved.Add(new GDScriptToken(K.Comma, edits[e].Start + delta, edits[e].Start + delta + 1));
                }
                delta += edits[e].Text.Length - edits[e].Length;
            }
            moved.Add(token with { Start = token.Start + delta, End = token.End + delta });
        }
        return moved;
    }

    /// <summary>The widest line's columns, a tab to the next of its stops.</summary>
    private static int Longest(string text, int tabWidth)
    {
        int widest = 0;
        int column = 0;
        foreach (char ch in text)
        {
            if (ch == '\n')
            {
                widest = Math.Max(widest, column);
                column = 0;
            }
            else
            {
                column += ch == '\t' ? tabWidth - column % tabWidth : 1;
            }
        }
        return Math.Max(widest, column);
    }

    /// <summary>The gaps between tokens wrapping breaks, in order: on each line too long, at its own level inside the
    /// brackets it lies in, else those of its outermost bracket (the widest of those alike) whose wrapping makes it
    /// narrower, each line made wrapped again in turn.</summary>
    private static List<(int Start, int End)> Breaks(string text, List<GDScriptToken> scanned, GDScriptFormatOptions options)
    {
        List<GDScriptToken> t = [.. scanned.Where(x => !IsVirtual(x.Kind))];
        int[] lines = LineStarts(text);
        int n = t.Count;
        int[] line = new int[n];
        int[] depth = new int[n];
        int[] partner = new int[n];
        int[] enclosing = new int[n];
        bool[] lambda = new bool[n];
        // Each line's first and last token, and whether a token runs onto it or past it (a multi-line string).
        int[] firstOn = new int[lines.Length];
        int[] lastOn = new int[lines.Length];
        bool[] runOn = new bool[lines.Length];
        Array.Fill(firstOn, -1);
        List<int> open = [];
        int at = 0;
        for (int i = 0; i < n; i++)
        {
            while (at + 1 < lines.Length && lines[at + 1] <= t[i].Start)
            {
                at++;
            }
            int ends = at;
            while (ends + 1 < lines.Length && lines[ends + 1] < t[i].End)
            {
                ends++;
            }
            line[i] = at;
            if (firstOn[at] < 0)
            {
                firstOn[at] = i;
            }
            lastOn[at] = i;
            for (int l = at; l <= ends && ends > at; l++)
            {
                runOn[l] = true;
            }
            partner[i] = -1;
            depth[i] = open.Count;
            enclosing[i] = open.Count > 0 ? open[^1] : -1;
            switch (t[i].Kind)
            {
                case K.ParenthesisOpen or K.BracketOpen or K.BraceOpen:
                    open.Add(i);
                    break;
                case K.ParenthesisClose or K.BracketClose or K.BraceClose when open.Count > 0:
                    partner[open[^1]] = i;
                    partner[i] = open[^1];
                    open.RemoveAt(open.Count - 1);
                    depth[i] = open.Count;
                    enclosing[i] = open.Count > 0 ? open[^1] : -1;
                    break;
                case K.Func:
                    open.ForEach(o => lambda[o] = true);
                    break;
            }
        }
        bool[] inLambda = new bool[n];
        for (int o = 0; o < n; o++)
        {
            for (int k = o; lambda[o] && k <= partner[o]; k++)
            {
                inLambda[k] = true;
            }
        }
        int unit = options.UseTabs ? options.TabWidth : options.IndentSize;
        List<int> before = [];
        for (int l = 0; l < lines.Length; l++)
        {
            int length = (l + 1 < lines.Length ? lines[l + 1] - 1 : text.Length) - lines[l];
            if (firstOn[l] >= 0 && !runOn[l] && length > options.LineLength / options.TabWidth)
            {
                Wrap(new Run(firstOn[l], lastOn[l], Columns(text, lines[l], t[firstOn[l]].Start, options.TabWidth)));
            }
        }
        before.Sort();
        return [.. before.Select(i => (t[i - 1].End, t[i].Start))];

        int Col(int i, Run run) => run.Indent + t[i].Start - t[run.First].Start;
        int End(int i, Run run) => run.Indent + t[i].End - t[run.First].Start;

        // A line too long wrapped at the shallowest level that makes it narrower, its lines then each wrapped in turn.
        void Wrap(Run run)
        {
            int width = End(run.Last, run);
            if (width <= options.LineLength)
            {
                return;
            }
            Plan? plan = Continued(run) is { } running && running.Widest < width ? running : null;
            IEnumerable<int> candidates = Enumerable.Range(run.First, run.Last - run.First + 1)
                .Where(o => plan is null && partner[o] > o + 1 && partner[o] <= run.Last && !inLambda[o] && Wraps(t, o))
                .OrderBy(o => depth[o]).ThenByDescending(o => t[partner[o]].End - t[o].Start);
            foreach (int o in candidates)
            {
                if (t[o].Kind == K.ParenthesisOpen && t[o + 1].Kind is K.BracketOpen or K.BraceOpen && partner[o + 1] == partner[o] - 1
                    && Exploded(o + 1, run) is { } hugged && hugged.Widest < width)
                {
                    // A call of one array or dictionary hugs it: the brackets inside open its lines.
                    plan = hugged;
                    break;
                }
                Plan? made = t[o].Kind != K.ParenthesisOpen ? Exploded(o, run) : IsCall(t, o) ? Filled(o, run) : Grouped(o, run);
                if (made is not null && made.Widest < width)
                {
                    plan = made;
                    break;
                }
            }
            if (plan is null)
            {
                return;
            }
            before.AddRange(plan.Lines.Skip(1).Select(part => part.First));
            foreach (Run part in plan.Lines)
            {
                Wrap(part);
            }
        }

        // The tokens inside a bracket at its own level that split it: commas, or the words a condition breaks before.
        IEnumerable<int> Splits(int o, Func<GDScriptToken, bool> splits) =>
            Enumerable.Range(o + 1, partner[o] - o - 1).Where(k => depth[k] == depth[o] + 1 && splits(t[k]));

        // Runs of tokens from each start to the next, as many to a line as fit, a line after the first indented so.
        Plan? Filling(Run run, List<int> starts, int from, int continued, bool always)
        {
            List<Run> made = [];
            int first = run.First;
            int indent = run.Indent;
            int width = from;
            bool items = false;
            for (int j = 0; j < starts.Count; j++)
            {
                int length = (j + 1 < starts.Count ? End(starts[j + 1] - 1, run) : End(run.Last, run)) - Col(starts[j], run);
                if (items || always ? width + (items ? 1 : 0) + length > options.LineLength && (items || continued < width) : false)
                {
                    made.Add(new Run(first, starts[j] - 1, indent));
                    first = starts[j];
                    indent = continued;
                    width = continued + length;
                }
                else
                {
                    width += (items ? 1 : 0) + length;
                }
                items = true;
            }
            made.Add(new Run(first, run.Last, indent));
            return made.Count == 1 ? null : new Plan(made, made.Max(Width));
        }

        int Width(Run run) => End(run.Last, run);

        // A line inside brackets opened above it runs on at its own level, as many of its items to a line as fit.
        Plan? Continued(Run run)
        {
            int e = enclosing[run.First];
            if (e < 0 || inLambda[run.First] || !Wraps(t, e))
            {
                return null;
            }
            bool commas = t[e].Kind != K.ParenthesisOpen || IsCall(t, e);
            List<int> starts = [];
            for (int k = run.First + 1; k <= run.Last; k++)
            {
                if (depth[k] == depth[e] + 1 && (commas ? t[k - 1].Kind == K.Comma && k != partner[e] : t[k].Kind is K.And or K.Or or K.AmpersandAmpersand or K.PipePipe or K.Else))
                {
                    starts.Add(k);
                }
            }
            return Filling(run, [run.First, .. starts], run.Indent, run.Indent, always: false);
        }

        Plan Exploded(int o, Run run)
        {
            int c = partner[o];
            List<Run> made = [new Run(run.First, o, run.Indent)];
            int widest = End(o, run);
            int start = o + 1;
            foreach (int comma in Splits(o, x => x.Kind == K.Comma).Append(c))
            {
                if (comma == c && start == c)
                {
                    break;
                }
                int last = comma == c ? c - 1 : comma;
                Run item = new(start, last, run.Indent + unit);
                made.Add(item);
                widest = Math.Max(widest, Width(item) + (comma == c && options.TrailingCommas ? 1 : 0));
                start = comma + 1;
            }
            Run closing = new(c, run.Last, run.Indent);
            made.Add(closing);
            return new Plan(made, Math.Max(widest, Width(closing)));
        }

        Plan? Filled(int o, Run run) =>
            Filling(run, [o + 1, .. Splits(o, x => x.Kind == K.Comma).Select(k => k + 1).Where(k => k < partner[o])], End(o, run), run.Indent + 2 * unit, always: true);

        Plan Grouped(int o, Run run)
        {
            int c = partner[o];
            int continued = run.Indent + 2 * unit;
            Run inside = new(o + 1, c - 1, continued);
            List<int> starts = [.. Splits(o, x => x.Kind is K.And or K.Or or K.AmpersandAmpersand or K.PipePipe or K.Else)];
            List<Run> contents = Filling(inside, [o + 1, .. starts], continued, continued, always: false)?.Lines.ToList() ?? [inside];
            List<Run> made = [new Run(run.First, o, run.Indent), .. contents, new Run(c, run.Last, run.Indent)];
            return new Plan(made, made.Max(Width));
        }
    }

    /// <summary>A line's tokens from the first to the last, at its indentation's columns.</summary>
    private readonly record struct Run(int First, int Last, int Indent);

    /// <summary>A wrapping of one line: the lines it makes, and the widest of them.</summary>
    private sealed record Plan(IReadOnlyList<Run> Lines, int Widest);

    /// <summary>Whether a bracket may be wrapped: not a subscript (<c>a[0]</c>, <c>Array[int]</c>), nor a lambda's
    /// parameters.</summary>
    private static bool Wraps(List<GDScriptToken> t, int o) => t[o].Kind switch
    {
        K.BracketOpen => o == 0 || !Subscripts(t[o - 1]),
        K.ParenthesisOpen => o == 0 || t[o - 1].Kind != K.Func,
        K.BraceOpen => true,
        _ => false,
    };

    /// <summary>Whether a parenthesis opens a call's or a declaration's arguments, as the token before it shows.</summary>
    private static bool IsCall(List<GDScriptToken> t, int o) =>
        o > 0 && (t[o - 1].IsIdentifier || t[o - 1].Kind is K.Self or K.Super or K.Preload or K.Assert or K.Annotation or K.ParenthesisClose
            or K.BracketClose or K.Void);

    /// <summary>The columns from a line's start to an offset on it, a tab to the next of its stops.</summary>
    private static int Columns(string text, int from, int to, int tabWidth)
    {
        int column = 0;
        for (int i = from; i < to; i++)
        {
            column += text[i] == '\t' ? tabWidth - column % tabWidth : 1;
        }
        return column;
    }

    /// <summary>Edits making a script into its laid out text, gap by gap between the tokens they share (a trailing comma
    /// added before a closing bracket among them); null where their tokens part.</summary>
    private static List<TextEdit>? Aligned(string was, List<GDScriptToken> scanned, string now, List<GDScriptToken> after)
    {
        List<GDScriptToken> x = [.. scanned.Where(token => !IsVirtual(token.Kind))];
        List<GDScriptToken> y = [.. after.Where(token => !IsVirtual(token.Kind))];
        List<TextEdit> edits = [];
        int j = 0;
        int fromX = 0;
        int fromY = 0;
        for (int i = 0; i <= x.Count; i++)
        {
            while (j < y.Count && y[j].Kind == K.Comma && (i == x.Count || x[i].Kind != K.Comma))
            {
                j++;
            }
            if (i < x.Count ? j >= y.Count || y[j].Kind != x[i].Kind : j < y.Count)
            {
                return null;
            }
            int endX = i < x.Count ? x[i].Start : was.Length;
            int endY = j < y.Count ? y[j].Start : now.Length;
            if (!was.AsSpan(fromX, endX - fromX).SequenceEqual(now.AsSpan(fromY, endY - fromY)))
            {
                edits.Add(new TextEdit(fromX, endX - fromX, now[fromY..endY]));
            }
            if (i < x.Count)
            {
                fromX = x[i].End;
                fromY = y[j].End;
                j++;
            }
        }
        return edits;
    }
}
