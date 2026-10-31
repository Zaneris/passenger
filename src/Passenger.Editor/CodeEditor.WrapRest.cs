using System.Runtime.CompilerServices;
using Avalonia;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Soft wrap's lines off screen: wrapped on the thread pool once the lines on screen are, at a new width or look
/// or for a text first shown, the rows kept as they were until the new ones land on the window's thread, carried through
/// the edits made meanwhile. A line too short to wrap with every character as wide as a tab or the widest yet measured
/// is taken to fit unread; graphemes the thread pool has no width for are measured on the window's thread, and the lines
/// wrapped again.</summary>
public sealed partial class CodeEditor
{
    /// <summary>The wrapping on the thread pool under way, if any.</summary>
    private WrapJob? wrapping;

    /// <summary>The wraps of texts no longer shown, by the text, for when it is shown again unchanged.</summary>
    private readonly ConditionalWeakTable<TextSnapshot, WrapMap> keptWraps = new();

    /// <summary>Where the host had the view in the rows of every line wrapped, while the lines above it are not yet.</summary>
    private double? awaitedY;

    /// <summary>Wraps on the thread pool the lines a map has yet to wrap at its width, unless that is under way.</summary>
    private void WrapRest(WrapMap map)
    {
        if (map.Settled)
        {
            StopWrapping();
            awaitedY = null;
            return;
        }
        if (wrapping is { } going && going.From.Width == map.Width && going.From.Look == map.Look)
        {
            return;
        }
        StopWrapping();
        WrapJob job = new(map, Measures().Copy(), RowHints.Taken(this), wrapIndent, decorations.InlayHints);
        wrapping = job;
        UnawaitedWork.Observe(WrapRestAsync(job));
    }

    private void StopWrapping()
    {
        wrapping?.Stopping.Cancel();
        wrapping = null;
    }

    private async Task WrapRestAsync(WrapJob job)
    {
        Wrapped? done;
        try
        {
            done = await BackgroundWork.Run(job.Run, job.Stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (done is not null && ReferenceEquals(wrapping, job))
        {
            wrapping = null;
            Land(job, done);
        }
    }

    /// <summary>What the thread pool wrapped, taken in place of the rows kept meanwhile, the line at the top kept there (or
    /// the view put where the host had it); or the graphemes it had no width for measured, and the lines wrapped again.</summary>
    private void Land(WrapJob job, Wrapped done)
    {
        if (!softWrap || wraps is not { } now || now.Width != job.From.Width || now.Look != job.From.Look)
        {
            return;
        }
        if (done.Map is not { } landed)
        {
            Advances measure = Measures();
            foreach (string grapheme in done.Lacking!)
            {
                measure.Of(grapheme);
            }
            WrapRest(now);
            return;
        }
        if (!ReferenceEquals(landed.Text, now.Text))
        {
            if (job.Hinted)
            {
                // The hints its rows made room for have moved with the edits since.
                WrapRest(now);
                return;
            }
            landed = Merged(now, landed, job.Moves);
        }
        rowShape++;
        if (awaitedY is { } y && landed.Settled)
        {
            wraps = landed;
            awaitedY = null;
            scroll = new Vector(scroll.X, Math.Clamp(y, 0, MaxScrollY));
            InvalidateVisual();
            FollowRows();
        }
        else
        {
            TakeWraps(landed);
        }
        if (ReferenceEquals(landed.Text, job.From.Text) && !ReferenceEquals(job.HintsAtStart, wrappedHints))
        {
            FollowHints(job.HintsAtStart, wrappedHints);
        }
        if (wraps is { } kept)
        {
            WrapRest(kept);
        }
    }

    /// <summary>A map the thread pool wrapped from an earlier text, carried to the one now: the lines edits since have
    /// wrapped again kept as they are now, the rest taken from it where they stand now.</summary>
    private static WrapMap Merged(WrapMap now, WrapMap landed, List<(int First, int Last, int Added)> moves)
    {
        WrapBuilder made = new(now.RunCount + landed.RunCount);
        for (int k = 0; k < now.RunCount; k++)
        {
            (int first, WrapRun run) = now.RunAt(k);
            int was = run.Fresh ? -1 : LineBefore(first, moves);
            if (was < 0 || LineBefore(first + run.Count - 1, moves) != was + run.Count - 1)
            {
                made.Add(run);
            }
            else
            {
                landed.CopyTo(made, was, run.Count, keepFresh: true);
            }
        }
        return made.Build(now.Text, now.Width, now.Look);
    }

    /// <summary>Where a line now was before the edits, or -1 for one they wrapped again.</summary>
    private static int LineBefore(int line, List<(int First, int Last, int Added)> moves)
    {
        for (int m = moves.Count - 1; m >= 0; m--)
        {
            (int first, int last, int added) = moves[m];
            if (line > last)
            {
                line -= added;
            }
            else if (line >= first)
            {
                return -1;
            }
        }
        return line;
    }

    /// <summary>Keeps the wraps of the text shown for when it is shown again unchanged (another tab and back, soft wrap
    /// off and on).</summary>
    private void KeepWraps()
    {
        if (wraps is { } map && ReferenceEquals(map.Text, document.Snapshot))
        {
            keptWraps.AddOrUpdate(map.Text, map);
        }
    }

    /// <summary>The wraps kept for a text, taken: the edits made from here on carry them, so they are no longer that text's.</summary>
    private WrapMap? KeptWraps(TextSnapshot text)
    {
        if (!keptWraps.TryGetValue(text, out WrapMap? kept))
        {
            return null;
        }
        keptWraps.Remove(text);
        return kept;
    }

    /// <summary>What the thread pool wrapped: a map of every line, or the graphemes it had no width for.</summary>
    private sealed record Wrapped(WrapMap? Map, HashSet<string>? Lacking);

    /// <summary>The lines a map has yet to wrap at its width, wrapped on the thread pool from its text, with the widths
    /// measured and the hints shown when it began; the runs of lines edits since have wrapped again, in the text after
    /// each (their first and last line and the lines they added), for it to land through.</summary>
    private sealed class WrapJob(WrapMap from, Advances measure, RowHints hints, bool indentWraps, HintList hintsAtStart)
    {
        /// <summary>The lines in a run of the map it makes: few runs, as a map's runs are copied with each edit.</summary>
        private const int RunLength = 4096;

        public WrapMap From => from;

        public CancellationTokenSource Stopping { get; } = new();

        public List<(int First, int Last, int Added)> Moves { get; } = [];

        /// <summary>Whether a hint was shown when it began, whose room its rows depend on.</summary>
        public bool Hinted => hints.Any;

        public HintList HintsAtStart => hintsAtStart;

        /// <summary>The map with every line wrapped, or the graphemes it had no width for; null once stopped.</summary>
        public Wrapped? Run()
        {
            TextSnapshot text = from.Text;
            WrapReading chars = new(null);
            List<int> rows = [];
            WrapBuilder made = new(runLength: RunLength);
            Int128 room = Fixed(from.Width);
            Int128 widest = Fixed(Math.Max(measure.Tab, measure.Widest)) + 1;
            for (int k = 0; k < from.RunCount && !measure.Full; k++)
            {
                (int first, WrapRun run) = from.RunAt(k);
                if (run.Fresh)
                {
                    made.Share(run);
                    continue;
                }
                for (int i = 0; i < run.Count && !measure.Full; i++)
                {
                    if (i % 1024 == 0 && Stopping.IsCancellationRequested)
                    {
                        return null;
                    }
                    LineSpan line = text.GetLine(first + i);
                    LineWrap? wrap = line.Length == 0 ? null
                        : line.Length * widest <= room && !hints.On(line) ? LineWrap.Assumed
                        : WrapLine(chars, text, line, from.Width, measure, hints, indentWraps, rows);
                    if (!measure.Lacked)
                    {
                        made.Add(wrap, fresh: true);
                    }
                }
            }
            return measure.Lacked ? new Wrapped(null, measure.Lacking) : new Wrapped(made.Build(text, from.Width, from.Look), null);
        }
    }

    /// <summary>The hints a row shares its room with: the editor's as they stand, or for the thread pool those shown when it
    /// began, each with the room it takes.</summary>
    private sealed class RowHints
    {
        private readonly CodeEditor? editor;
        private readonly int[] offsets = [];
        private readonly Int128[] rooms = [];

        private RowHints(CodeEditor editor) => this.editor = editor;

        private RowHints(int[] offsets, Int128[] rooms)
        {
            this.offsets = offsets;
            this.rooms = rooms;
        }

        public static RowHints Live(CodeEditor editor) => new(editor);

        /// <summary>The hints shown now and their rooms, for the thread pool.</summary>
        public static RowHints Taken(CodeEditor editor)
        {
            HintList hints = editor.decorations.InlayHints;
            List<int> offsets = [];
            List<Int128> rooms = [];
            for (int h = editor.ShownHintFrom(hints, 0); h < hints.Count; h = editor.ShownHintFrom(hints, h + 1))
            {
                offsets.Add(hints[h].Offset);
                rooms.Add(Fixed(editor.HintWidth(hints[h], out _)));
            }
            return new([.. offsets], [.. rooms]);
        }

        /// <summary>Whether a taken hint is shown.</summary>
        public bool Any => offsets.Length > 0;

        /// <summary>The first hint shown at or after an offset.</summary>
        public int From(int offset)
        {
            if (editor is { } live)
            {
                return live.ShownHintFrom(live.decorations.InlayHints, live.FirstHintFrom(offset));
            }
            return Sorted.FirstAtOrAfter(offsets, offset);
        }

        /// <summary>The next hint shown after one.</summary>
        public int After(int index) => editor is { } live ? live.ShownHintFrom(live.decorations.InlayHints, index + 1) : index + 1;

        /// <summary>The column a hint stands at on a line, past the last one int.MaxValue.</summary>
        public int ColumnOf(int index, int lineStart)
        {
            if (editor is { } live)
            {
                HintList hints = live.decorations.InlayHints;
                return index < hints.Count ? hints[index].Offset - lineStart : int.MaxValue;
            }
            return index < offsets.Length ? offsets[index] - lineStart : int.MaxValue;
        }

        /// <summary>The room a hint takes on its row, in <see cref="Unit"/>s.</summary>
        public Int128 RoomOf(int index) => editor is { } live ? Fixed(live.HintWidth(live.decorations.InlayHints[index], out _)) : rooms[index];

        /// <summary>Whether a hint shown stands on a line.</summary>
        public bool On(LineSpan line) => ColumnOf(From(line.Start), line.Start) <= line.Length;
    }
}
