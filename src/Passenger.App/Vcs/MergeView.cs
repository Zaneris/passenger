using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>A change of the merge as it stands: its lines on each side, where it is in the result now, and which of its
/// sides are dealt with (taken by its arrow, or ignored by its X); resolved once both are.</summary>
public sealed record MergeChange(MergeChunk Chunk, int ResultStart, int ResultEnd, bool LeftResolved, bool RightResolved)
{
    public bool IsResolved => LeftResolved && RightResolved;

    public bool IsConflict => Chunk.IsConflict;

    public bool Resolved(MergeSide side) => side == MergeSide.Left ? LeftResolved : RightResolved;
}

/// <summary>The three-way merge: yours on the left and theirs on the right (read-only), the result between them, which
/// starts as the version both came from. Each change is tinted on its sides and joined to the result by a band whose
/// arrow takes that side's lines into the result (a conflict's second arrow adds them after the first's, and a
/// Ctrl+click on either resolves it with that side) and whose X ignores it; Apply All Non-Conflicting Changes takes
/// every change only one side made, and Resolve Simple Conflicts those and every conflict whose sides changed different
/// words. The result can be edited too; undo takes back a change taken. F7 and Shift+F7 (F8 and Shift+F8 too off the
/// Mac) go to the next and previous change; Accept Left Side and Accept Right Side take the one at the caret.</summary>
public sealed class MergeView : DockPanel
{
    private const double StripWidth = 48;

    private readonly string[] baseLines;
    private readonly string[] leftLines;
    private readonly string[] rightLines;

    /// <summary>The words each side changed inside each change's lines, once worked out.</summary>
    private ChunkWords[] words = [];
    private readonly string lineBreak;
    private readonly TextDocument result;
    private readonly TextSnapshot started;

    /// <summary>The changes as the comparison found them, and each conflict's lines merged word by word where that works.</summary>
    private readonly IReadOnlyList<MergeChunk> chunks;
    private readonly string[]?[] simple;

    /// <summary>Each change's place in the result and what is done with it, for every text the result has had, so an undo
    /// or a redo of the result finds the changes as they were with that text.</summary>
    private readonly Dictionary<TextSnapshot, Resolution[]> states = new(ReferenceEqualityComparer.Instance);
    private Resolution[] state;
    private Resolution[]? taking;

    private readonly DiffStrip leftStrip;
    private readonly DiffStrip rightStrip;
    private readonly TextBlock status;
    private readonly Button applyNonConflicting;
    private readonly Button resolveSimple;
    private bool following;
    private bool closed;

    public MergeView(MergeTexts texts, string name)
    {
        string[] common = LineDiff.Lines(texts.Base);
        baseLines = common;
        leftLines = LineDiff.Lines(texts.Left);
        rightLines = LineDiff.Lines(texts.Right);
        lineBreak = TextDocument.DetectLineEnding(texts.Base.Length > 0 ? texts.Base : texts.Left);
        chunks = ThreeWayMerge.Chunks(common, leftLines, rightLines);
        simple = [.. chunks.Select(c => c.IsConflict ? ThreeWayMerge.ResolveSimply(common[c.BaseStart..c.BaseEnd], leftLines[c.LeftStart..c.LeftEnd], rightLines[c.RightStart..c.RightEnd]) : null)];
        Texts = texts;

        // The IDE's keymap sends each its keys from the merge's window (IdeKeys).
        Left = new CodeEditor { IsReadOnly = true, ChangeBackgrounds = true };
        Left.Show(new TextDocument(texts.Left), SelectionSet.Single(0), default);
        result = new TextDocument(texts.Base);
        started = result.Snapshot;
        Result = new CodeEditor { ChangeBackgrounds = true };
        Result.Show(result, SelectionSet.Single(0), default);
        Right = new CodeEditor { IsReadOnly = true, ChangeBackgrounds = true };
        Right.Show(new TextDocument(texts.Right), SelectionSet.Single(0), default);
        state = [.. chunks.Select(c => new Resolution(c.BaseStart, c.BaseEnd, !c.LeftChanged, !c.RightChanged, false))];
        states[started] = state;
        leftStrip = Strip(MergeSide.Left);
        rightStrip = Strip(MergeSide.Right);

        Grid sides = new() { ColumnDefinitions = new ColumnDefinitions($"*,{StripWidth},*,{StripWidth},*"), RowDefinitions = new RowDefinitions("Auto,*") };
        sides.Children.Add(DiffLines.Title(texts.Sides.LeftTitle, 0));
        sides.Children.Add(DiffLines.Title("Result", 2));
        sides.Children.Add(DiffLines.Title(texts.Sides.RightTitle, 4));
        Control[] panes = [Left, leftStrip, Result, rightStrip, Right];
        for (int i = 0; i < panes.Length; i++)
        {
            Grid.SetRow(panes[i], 1);
            Grid.SetColumn(panes[i], i);
            sides.Children.Add(panes[i]);
        }

        status = new TextBlock();
        applyNonConflicting = ToolButtons.Of(IconKeys.ApplyNonConflicts, "Apply All Non-Conflicting Changes", ApplyNonConflicting);
        // Two arrows meeting in the middle: the vertical one turned on its side.
        applyNonConflicting.Content = new PathIcon { RenderTransform = new RotateTransform(90) };
        ((PathIcon)applyNonConflicting.Content).Bind(PathIcon.DataProperty, applyNonConflicting.GetResourceObservable(IconKeys.ApplyNonConflicts));
        resolveSimple = ToolButtons.Of(IconKeys.Wand, "Resolve Simple Conflicts", ResolveSimpleConflicts);
        Border rule = new() { Width = 1, Height = 16, Margin = new Thickness(6, 0) };
        rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable(TokenKeys.Divider));
        Children.Add(DiffLines.Bar(name, Step, status, [applyNonConflicting, resolveSimple, rule]));
        Children.Add(sides);

        Left.ScrollChanged += (_, _) => Follow(Left);
        Result.ScrollChanged += (_, _) => Follow(Result);
        Right.ScrollChanged += (_, _) => Follow(Right);
        result.Changed += OnResultChanged;
        // Closed, the editors let go of their documents and colours.
        DetachedFromVisualTree += (_, _) =>
        {
            closed = true;
            result.Changed -= OnResultChanged;
            Left.Detach();
            Result.Detach();
            Right.Detach();
        };
        // The window's keys, wherever in it they are: its toolbar's buttons and the window itself take them too.
        TopLevel? keys = null;
        AttachedToVisualTree += (_, _) =>
        {
            keys = TopLevel.GetTopLevel(this);
            keys?.AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        };
        DetachedFromVisualTree += (_, _) => keys?.RemoveHandler(KeyDownEvent, OnKeyDown);
        Show();
        IdeErrors.Observe(MarkWordsAsync());
    }

    /// <summary>The words each side changed inside its changes' lines, worked out off the window's thread, then shown.</summary>
    private async Task MarkWordsAsync()
    {
        IReadOnlyList<MergeChunk> all = chunks;
        string[] common = baseLines;
        string[] lefts = leftLines;
        string[] rights = rightLines;
        TextSnapshot leftText = Left.Document.Snapshot;
        TextSnapshot rightText = Right.Document.Snapshot;
        ChunkWords[] found = await BackgroundWork.Run(() => all.Select(c => ChunkWords.Of(c, common, lefts, rights, leftText, rightText)).ToArray());
        if (!closed)
        {
            words = found;
            Show();
        }
    }

    /// <summary>The versions merged, and the words for their sides.</summary>
    public MergeTexts Texts { get; }

    /// <summary>Yours.</summary>
    public CodeEditor Left { get; }

    /// <summary>The result, which Apply Changes writes to the file.</summary>
    public CodeEditor Result { get; }

    /// <summary>Theirs.</summary>
    public CodeEditor Right { get; }

    public string ResultText => result.Snapshot.ToString();

    /// <summary>Whether anything was done to the result since the window opened.</summary>
    public bool IsModified => !ReferenceEquals(result.Snapshot, started) || state.Where((r, i) => r.Left != !chunks[i].LeftChanged || r.Right != !chunks[i].RightChanged).Any();

    /// <summary>The changes as they stand, in order.</summary>
    public IReadOnlyList<MergeChange> Changes => [.. chunks.Select((c, i) => new MergeChange(c, state[i].Start, state[i].End, state[i].Left, state[i].Right))];

    /// <summary>The changes not dealt with yet that only one side made (or both alike), and the conflicts.</summary>
    public int UnresolvedChanges => Enumerable.Range(0, chunks.Count).Count(i => !chunks[i].IsConflict && !Done(i));

    public int UnresolvedConflicts => Enumerable.Range(0, chunks.Count).Count(i => chunks[i].IsConflict && !Done(i));

    /// <summary>A change's arrow: that side's lines into the result in place of what is there, or after what the
    /// conflict's other arrow put there. A conflict stays open for its other side's arrow or X, unless
    /// <paramref name="resolve"/> (a Ctrl+click) closes it with this side's lines.</summary>
    public void Accept(int index, MergeSide side, bool resolve = false)
    {
        MergeChunk chunk = chunks[index];
        Resolution now = state[index];
        if (!chunk.Changed(side) || now.Has(side))
        {
            return;
        }
        string[] lines = Lines(chunk, side);
        if (!chunk.IsConflict)
        {
            Take([(index, lines, now with { Left = true, Right = true })]);
            return;
        }
        string[] put = now.Taken ? [.. Current(now), .. lines] : lines;
        MergeSide other = side == MergeSide.Left ? MergeSide.Right : MergeSide.Left;
        // A conflict whose other side only took lines away has nothing left to add.
        bool both = resolve || chunk.Start(other) == chunk.End(other);
        Resolution next = (side == MergeSide.Left ? now with { Left = true } : now with { Right = true }) with { Taken = true };
        Take([(index, put, both ? next with { Left = true, Right = true } : next)]);
    }

    /// <summary>A change's X: that side left out; a change only one side made is then resolved as the base has it.</summary>
    public void Ignore(int index, MergeSide side)
    {
        Resolution now = state[index];
        if (!chunks[index].Changed(side) || now.Has(side))
        {
            return;
        }
        Resolution next = !chunks[index].IsConflict ? now with { Left = true, Right = true }
            : side == MergeSide.Left ? now with { Left = true } : now with { Right = true };
        Take([(index, null, next)]);
    }

    /// <summary>Apply All Non-Conflicting Changes: every change only one side made (or both alike) taken.</summary>
    public void ApplyNonConflicting() => Take([.. NonConflicting()]);

    /// <summary>Resolve Simple Conflicts: the non-conflicting changes, and each conflict whose sides changed different words
    /// of its lines, both sides' words taken.</summary>
    public void ResolveSimpleConflicts()
    {
        List<(int, string[]?, Resolution)> taken = [.. NonConflicting()];
        for (int i = 0; i < chunks.Count; i++)
        {
            if (chunks[i].IsConflict && !Done(i) && !state[i].Taken && simple[i] is { } merged)
            {
                taken.Add((i, merged, state[i] with { Left = true, Right = true }));
            }
        }
        Take(taken);
    }

    private IEnumerable<(int, string[]?, Resolution)> NonConflicting()
    {
        for (int i = 0; i < chunks.Count; i++)
        {
            if (!chunks[i].IsConflict && !Done(i))
            {
                yield return (i, Lines(chunks[i], chunks[i].LeftChanged ? MergeSide.Left : MergeSide.Right), state[i] with { Left = true, Right = true });
            }
        }
    }

    /// <summary>The next or previous change not dealt with yet from the result's caret, round past the ends.</summary>
    public void Step(int step)
    {
        List<int> open = [.. Enumerable.Range(0, chunks.Count).Where(i => !Done(i))];
        if (open.Count == 0)
        {
            return;
        }
        int line = Result.CaretPosition.Line;
        int target = step > 0
            ? open.FirstOrDefault(i => state[i].Start > line, open[0])
            : open.LastOrDefault(i => state[i].Start + Math.Max(1, state[i].End - state[i].Start) <= line, open[^1]);
        GoTo(target);
    }

    /// <summary>The caret to the first change not dealt with, as the window opens on it.</summary>
    public void GoToFirst()
    {
        if (Enumerable.Range(0, chunks.Count).Where(i => !Done(i)).Cast<int?>().FirstOrDefault() is int first)
        {
            GoTo(first);
        }
    }

    private void GoTo(int index)
    {
        Result.GoTo(new TextPosition(Math.Min(state[index].Start, result.Snapshot.LineCount - 1), 0));
        Result.Focus();
    }

    /// <summary>Where a change's arrow (or its X, with <paramref name="ignore"/>) is drawn on one side, in this view's
    /// coordinates; null while that side has nothing to take.</summary>
    public Point? IconAt(int index, MergeSide side, bool ignore)
    {
        DiffStrip strip = side == MergeSide.Left ? leftStrip : rightStrip;
        return strip.Icon(index, ignore) is { } rect ? strip.TranslatePoint(rect.Center, this) : null;
    }

    private bool Done(int index) => state[index].Left && state[index].Right;

    private string[] Lines(MergeChunk chunk, MergeSide side) => side == MergeSide.Left
        ? leftLines[chunk.LeftStart..chunk.LeftEnd]
        : rightLines[chunk.RightStart..chunk.RightEnd];

    /// <summary>A change's lines in the result as they are now.</summary>
    private string[] Current(Resolution at)
    {
        TextSnapshot text = result.Snapshot;
        return [.. Enumerable.Range(at.Start, at.End - at.Start).Select(text.GetLineText)];
    }

    /// <summary>Puts lines in the result in place of changes' (as one undo step) and marks what is done with each.</summary>
    private void Take(IReadOnlyList<(int Index, string[]? Lines, Resolution Next)> taken)
    {
        if (taken.Count == 0)
        {
            return;
        }
        Dictionary<int, (string[]? Lines, Resolution Next)> chosen = taken.ToDictionary(t => t.Index, t => (t.Lines, t.Next));
        TextSnapshot text = result.Snapshot;
        Resolution[] next = new Resolution[state.Length];
        List<TextEdit> edits = [];
        int shift = 0;
        for (int i = 0; i < state.Length; i++)
        {
            Resolution now = state[i];
            if (!chosen.TryGetValue(i, out (string[]? Lines, Resolution Next) change))
            {
                next[i] = now with { Start = now.Start + shift, End = now.End + shift };
                continue;
            }
            if (change.Lines is { } lines)
            {
                edits.Add(ThreeWayMerge.ReplaceLines(text, now.Start, now.End, lines, lineBreak));
                next[i] = change.Next with { Start = now.Start + shift, End = now.Start + shift + lines.Length };
                shift += lines.Length - (now.End - now.Start);
            }
            else
            {
                next[i] = change.Next with { Start = now.Start + shift, End = now.End + shift };
            }
        }
        taking = next;
        Result.ApplyEdits(edits);
        if (taking is not null)
        {
            // Nothing in the text changed: only what is done with the changes did.
            taking = null;
            state = next;
            states[result.Snapshot] = next;
            Show();
        }
    }

    private void OnResultChanged(object? sender, DocumentChangedEventArgs e)
    {
        if (taking is { } taken)
        {
            taking = null;
            state = taken;
        }
        else if (states.TryGetValue(e.After, out Resolution[]? known))
        {
            state = known;
        }
        else
        {
            state = Moved(state, e);
        }
        states[e.After] = state;
        Show();
    }

    /// <summary>The changes' places after an edit of the result's own: those below it move with the lines it added or
    /// took away, and one it falls in grows or shrinks with it.</summary>
    private static Resolution[] Moved(Resolution[] before, DocumentChangedEventArgs e)
    {
        Resolution[] after = [.. before];
        // Last to first, so each edit's lines are still as the text before them numbered them.
        for (int k = e.Edits.Count - 1; k >= 0; k--)
        {
            TextEdit edit = e.Edits[k];
            int first = e.Before.GetLineFromOffset(edit.Start);
            int last = e.Before.GetLineFromOffset(edit.End);
            int shift = edit.Text.AsSpan().Count('\n') - (last - first);
            for (int i = 0; i < after.Length; i++)
            {
                (int start, int end) = Moved(after[i].Start, after[i].End, first, last + 1, shift);
                after[i] = after[i] with { Start = start, End = end };
            }
        }
        int lines = ThreeWayMerge.LineCount(e.After);
        return [.. after.Select(r => r with { Start = Math.Clamp(r.Start, 0, lines), End = Math.Clamp(r.End, Math.Clamp(r.Start, 0, lines), lines) })];
    }

    private static (int Start, int End) Moved(int start, int end, int changeStart, int changeEnd, int shift)
    {
        if (end <= changeStart)
        {
            return (start, end);
        }
        if (start >= changeEnd)
        {
            return (start + shift, end + shift);
        }
        if (start <= changeStart && end >= changeEnd)
        {
            return (start, end + shift);
        }
        return (Math.Min(start, changeStart), Math.Max(end, changeEnd) + shift);
    }

    /// <summary>The tints, the bands, the counts and the tools as the changes stand.</summary>
    private void Show()
    {
        List<ChangeMark> left = [];
        List<ChangeMark> right = [];
        List<ChangeMark> middle = [];
        for (int i = 0; i < chunks.Count; i++)
        {
            MergeChunk chunk = chunks[i];
            Resolution now = state[i];
            if (chunk.LeftChanged && !now.Left)
            {
                left.Add(new ChangeMark(chunk.LeftStart, chunk.LeftEnd - chunk.LeftStart, Kind(chunk, MergeSide.Left)));
            }
            if (chunk.RightChanged && !now.Right)
            {
                right.Add(new ChangeMark(chunk.RightStart, chunk.RightEnd - chunk.RightStart, Kind(chunk, MergeSide.Right)));
            }
            if (!Done(i))
            {
                middle.Add(new ChangeMark(now.Start, now.End - now.Start, Kind(chunk, chunk.LeftChanged ? MergeSide.Left : MergeSide.Right)));
            }
        }
        Left.SetChangeMarks(left);
        Right.SetChangeMarks(right);
        Result.SetChangeMarks(middle);
        ShowWords();
        int changes = UnresolvedChanges;
        int conflicts = UnresolvedConflicts;
        status.Text = changes + conflicts == 0 ? "All conflicts resolved"
            : $"{Count(changes, "change", "No changes")}, {Count(conflicts, "conflict", "No conflicts")}";
        applyNonConflicting.IsEnabled = changes > 0;
        resolveSimple.IsEnabled = changes > 0 || Enumerable.Range(0, chunks.Count).Any(i => chunks[i].IsConflict && !Done(i) && !state[i].Taken && simple[i] is not null);
        leftStrip.InvalidateVisual();
        rightStrip.InvalidateVisual();

        static string Count(int count, string what, string none) => count switch
        {
            0 => none,
            _ => Core.Plural.Of(count, what),
        };
    }

    /// <summary>The changed words of each change not dealt with: on a side while that side's lines wait to be taken, and in
    /// the result while it still holds the base's lines there.</summary>
    private void ShowWords()
    {
        if (words.Length != chunks.Count)
        {
            return;
        }
        List<InlineChange> left = [];
        List<InlineChange> right = [];
        List<InlineChange> middle = [];
        TextSnapshot text = result.Snapshot;
        for (int i = 0; i < chunks.Count; i++)
        {
            MergeChunk chunk = chunks[i];
            Resolution now = state[i];
            if (chunk.LeftChanged && !now.Left)
            {
                left.AddRange(words[i].Left);
            }
            if (chunk.RightChanged && !now.Right)
            {
                right.AddRange(words[i].Right);
            }
            if (!Done(i) && !now.Taken && HoldsBase(text, chunk, now))
            {
                middle.AddRange(words[i].Base.Select(w => new InlineChange(TextRange.FromBounds(
                    text.GetLineStart(now.Start + w.Start.Line) + w.Start.Column, text.GetLineStart(now.Start + w.End.Line) + w.End.Column), w.Kind)));
            }
        }
        Left.SetInlineChanges(left);
        Right.SetInlineChanges(right);
        Result.SetInlineChanges(Union(middle));
    }

    /// <summary>Whether the result has a change's lines as the base has them, where its words can be marked.</summary>
    private bool HoldsBase(TextSnapshot text, MergeChunk chunk, Resolution now)
    {
        if (now.End - now.Start != chunk.BaseEnd - chunk.BaseStart || now.End > text.LineCount)
        {
            return false;
        }
        for (int line = now.Start; line < now.End; line++)
        {
            if (text.GetLineText(line) != baseLines[chunk.BaseStart + line - now.Start])
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The result's marks from both sides, those that overlap (a conflict's words both changed) made one.</summary>
    private static List<InlineChange> Union(List<InlineChange> marks)
    {
        List<InlineChange> joined = [];
        foreach (InlineChange mark in marks.OrderBy(m => m.Range.Start))
        {
            if (joined.Count > 0 && joined[^1].Range.End > mark.Range.Start)
            {
                InlineChange last = joined[^1];
                joined[^1] = last with { Range = TextRange.FromBounds(last.Range.Start, Math.Max(last.Range.End, mark.Range.End)) };
                continue;
            }
            joined.Add(mark);
        }
        return joined;
    }

    /// <summary>The words each side changed inside a change's lines against the base: on each side as offsets into its
    /// text, on the base as lines and columns from the change's first line, a conflict's all in its red.</summary>
    private sealed record ChunkWords(IReadOnlyList<InlineChange> Left, IReadOnlyList<InlineChange> Right,
        IReadOnlyList<(TextPosition Start, TextPosition End, ChangeMarkKind Kind)> Base)
    {
        public static ChunkWords Of(MergeChunk chunk, string[] common, string[] left, string[] right, TextSnapshot leftText, TextSnapshot rightText)
        {
            List<InlineChange> lefts = [];
            List<InlineChange> rights = [];
            List<(TextPosition, TextPosition, ChangeMarkKind)> bases = [];
            string[] before = common[chunk.BaseStart..chunk.BaseEnd];
            if (chunk.LeftChanged)
            {
                Side(chunk, before, left[chunk.LeftStart..chunk.LeftEnd], chunk.LeftStart, leftText, lefts, bases);
            }
            if (chunk.RightChanged)
            {
                Side(chunk, before, right[chunk.RightStart..chunk.RightEnd], chunk.RightStart, rightText, rights, bases);
            }
            return new ChunkWords(lefts, rights, bases);
        }

        private static void Side(MergeChunk chunk, string[] before, string[] after, int first, TextSnapshot text, List<InlineChange> side,
            List<(TextPosition, TextPosition, ChangeMarkKind)> bases)
        {
            if (before.Length == 0 || after.Length == 0 || WordDiff.Words(string.Join('\n', before), string.Join('\n', after)) is not { } found)
            {
                return;
            }
            foreach (InlineDiff change in found)
            {
                ChangeMarkKind kind = chunk.IsConflict ? ChangeMarkKind.Conflict
                    : change.IsInsertion ? ChangeMarkKind.Added : change.IsDeletion ? ChangeMarkKind.Deleted : ChangeMarkKind.Modified;
                if (!change.IsDeletion)
                {
                    side.Add(new InlineChange(TextRange.FromBounds(Offset(after, first, change.NewStart), Offset(after, first, change.NewEnd)), kind));
                }
                if (!change.IsInsertion)
                {
                    bases.Add((Position(before, change.OldStart), Position(before, change.OldEnd), kind));
                }
            }

            int Offset(string[] lines, int line, int offset)
            {
                TextPosition at = Position(lines, offset);
                return text.GetLineStart(line + at.Line) + at.Column;
            }
        }

        private static TextPosition Position(string[] lines, int offset)
        {
            (int line, int column) = FileDiff.Place(lines, offset);
            return new TextPosition(line, column);
        }
    }

    /// <summary>A change's colour on a side: a conflict's red, else lines added, taken away or changed against the base.</summary>
    private static ChangeMarkKind Kind(MergeChunk chunk, MergeSide side) =>
        chunk.IsConflict ? ChangeMarkKind.Conflict
        : chunk.BaseStart == chunk.BaseEnd ? ChangeMarkKind.Added
        : chunk.Start(side) == chunk.End(side) ? ChangeMarkKind.Deleted
        : ChangeMarkKind.Modified;

    /// <summary>Keeps the three sides level as any of them scrolls: a line maps through the changes above it.</summary>
    private void Follow(CodeEditor from)
    {
        leftStrip.InvalidateVisual();
        rightStrip.InvalidateVisual();
        if (following)
        {
            return;
        }
        following = true;
        double top = DiffLines.Top(from);
        double middle = from == Result ? top : Map(top, from == Left ? MergeSide.Left : MergeSide.Right, toResult: true);
        if (from != Result)
        {
            DiffLines.ScrollTo(Result, middle, Result);
        }
        if (from != Left)
        {
            DiffLines.ScrollTo(Left, Map(middle, MergeSide.Left, toResult: false), Left);
        }
        if (from != Right)
        {
            DiffLines.ScrollTo(Right, Map(middle, MergeSide.Right, toResult: false), Right);
        }
        following = false;
    }

    /// <summary>Where a line of a side (fractional, from zero) stands in the result, or a line of the result on a side.</summary>
    private double Map(double line, MergeSide side, bool toResult) => DiffLines.Map(line, chunks.Count, i =>
    {
        (int sideStart, int sideEnd) = (chunks[i].Start(side), chunks[i].End(side));
        return toResult
            ? new LinesAcross(sideStart, sideEnd - sideStart, state[i].Start, state[i].End - state[i].Start)
            : new LinesAcross(state[i].Start, state[i].End - state[i].Start, sideStart, sideEnd - sideStart);
    });

    /// <summary>Whether a key is the merge's own, which the IDE's keymap leaves to it: Next and Previous Difference, and
    /// Accept Left Side and Accept Right Side, Ctrl+Cmd+Right and Left on the Mac, Ctrl+Alt+R and A elsewhere.</summary>
    public static bool TakesKey(KeyEventArgs e) => DiffLines.Step(e) != 0 || IsAccept(e);

    private static bool IsAccept(KeyEventArgs e) => OperatingSystem.IsMacOS()
        ? e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Meta) && e.Key is Key.Right or Key.Left
        : e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt) && e.Key is Key.R or Key.A;

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DiffLines.Step(e) is not 0 and int step)
        {
            Step(step);
            e.Handled = true;
        }
        else if (IsAccept(e))
        {
            AcceptAtCaret(e.Key is Key.Right or Key.R ? MergeSide.Left : MergeSide.Right);
            e.Handled = true;
        }
    }

    /// <summary>Accept Left Side or Accept Right Side: the change at the caret of the side with the keys taken.</summary>
    public void AcceptAtCaret(MergeSide side)
    {
        CodeEditor editor = Left.IsFocused ? Left : Right.IsFocused ? Right : Result;
        int line = editor.CaretPosition.Line;
        for (int i = 0; i < chunks.Count; i++)
        {
            (int start, int end) = editor == Result ? (state[i].Start, state[i].End)
                : editor == Left ? (chunks[i].LeftStart, chunks[i].LeftEnd)
                : (chunks[i].RightStart, chunks[i].RightEnd);
            if (line >= start && (line < end || line == start))
            {
                Accept(i, side);
                return;
            }
        }
    }

    /// <summary>A change's place in the result, and its sides dealt with; <see cref="Taken"/> once a conflict's side is in.</summary>
    private readonly record struct Resolution(int Start, int End, bool Left, bool Right, bool Taken)
    {
        public bool Has(MergeSide side) => side == MergeSide.Left ? Left : Right;
    }

    /// <summary>The strip between a side and the result: each change of that side not dealt with is a band from its lines
    /// to the result's, with the arrow that takes it and the X that ignores it at the side's end.</summary>
    private DiffStrip Strip(MergeSide side) =>
        new(() => Bands(side), side, side == MergeSide.Left ? Left : Right, () => true, ignores: true, (band, ignore, keys) =>
        {
            if (ignore)
            {
                Ignore(band.Index, side);
            }
            else
            {
                Accept(band.Index, side, resolve: keys.HasFlag(KeyModifiers.Control));
            }
        })
        { Width = StripWidth };

    /// <summary>Each change of a side not dealt with, from its lines on the left of its strip to its lines on the right.</summary>
    private IEnumerable<DiffBand> Bands(MergeSide side)
    {
        CodeEditor editor = side == MergeSide.Left ? Left : Right;
        for (int i = 0; i < chunks.Count; i++)
        {
            MergeChunk chunk = chunks[i];
            Resolution now = state[i];
            if (!chunk.Changed(side) || now.Has(side))
            {
                continue;
            }
            double sideTop = (chunk.Start(side) * editor.LineHeight) - editor.ScrollOffset.Y;
            double sideBottom = sideTop + ((chunk.End(side) - chunk.Start(side)) * editor.LineHeight);
            double resultTop = (now.Start * Result.LineHeight) - Result.ScrollOffset.Y;
            double resultBottom = resultTop + ((now.End - now.Start) * Result.LineHeight);
            yield return side == MergeSide.Left
                ? new DiffBand(i, sideTop, sideBottom, resultTop, resultBottom, Kind(chunk, side))
                : new DiffBand(i, resultTop, resultBottom, sideTop, sideBottom, Kind(chunk, side));
        }
    }
}
