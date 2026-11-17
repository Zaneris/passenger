using System.Text;
using Avalonia;
using Avalonia.Automation.Peers;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Terminal;

/// <summary>The terminal as a screen reader reads it (<see cref="AccessibleText"/>): the scrollback's lines and the
/// screen's rows, a row a line, each line's text as copying gives it (trailing blanks left out), read from the lines as
/// they stand when asked, the caret the cursor; made the first time a reader asks, so a terminal no reader reads tells
/// nothing as output comes.</summary>
public sealed partial class TerminalView
{
    private TerminalText? accessibleText;

    /// <summary>The text as each system's screen-reader bridge reads it.</summary>
    public AccessibleText AccessibleText => accessibleText ??= new TerminalText(this);

    protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleTextPeer(this, AccessibleText);

    /// <summary>Tells a reader what output, a selection or a session shown changed.</summary>
    private void FollowAccessibleText() => accessibleText?.Follow();

    private sealed class TerminalText : AccessibleText
    {
        private readonly TerminalView view;

        /// <summary>Each line's start, from the oldest kept, and the text's length and a break past the last; found again as
        /// the screen changes, the first time it is read after.</summary>
        private int[] starts = [0, 1];

        private int lineCount = 1;

        /// <summary>The screen, its version and its oldest line the starts were found for.</summary>
        private (TerminalScreen? Screen, int Version, long First) indexed = (null, -1, -1);

        /// <summary>The lines as last told: their starts then, the screen's top row, and each row's line and how often it had
        /// been written.</summary>
        private Told told = new([0, 1], 1, null, 0, 0, []);

        /// <summary>The starts told before last, which the next lines' starts are taken into.</summary>
        private int[] spare = [];

        /// <summary>The caret and what was selected as last told.</summary>
        private (int Caret, TextRange? Selected) toldCaret = (-1, null);

        private sealed record Told(int[] Starts, int Count, TerminalScreen? Screen, long First, long ScreenTop, (TerminalLine Line, long Writes)[] Rows)
        {
            public int Length => Starts[Count] - 1;

            /// <summary>A line's start by its number, the text's end past the last.</summary>
            public int StartOf(long number) => Math.Min(Starts[(int)Math.Clamp(number - First, 0, Count)], Length);
        }

        public TerminalText(TerminalView view)
        {
            this.view = view;
            told = Seen();
        }

        private TerminalScreen? Screen => view.session?.Screen;

        public override int Length
        {
            get
            {
                Index();
                return starts[lineCount] - 1;
            }
        }

        public override int LineCount
        {
            get
            {
                Index();
                return lineCount;
            }
        }

        public override LineSpan Line(int line)
        {
            Index();
            int at = Math.Clamp(line, 0, lineCount - 1);
            int breakLength = at < lineCount - 1 ? 1 : 0;
            return new LineSpan(at, starts[at], starts[at + 1] - starts[at] - 1, breakLength);
        }

        public override int LineOf(int offset)
        {
            Index();
            return Math.Clamp(Sorted.FirstAfter(starts.AsSpan(0, lineCount), offset) - 1, 0, lineCount - 1);
        }

        public override string Read(int start, int length)
        {
            if (Screen is not { } screen || length <= 0)
            {
                return string.Empty;
            }
            Index();
            int from = Math.Clamp(start, 0, Length);
            int to = Math.Clamp(start + length, from, Length);
            StringBuilder text = new(to - from);
            for (int line = LineOf(from); line < lineCount && starts[line] < to; line++)
            {
                string row = screen.LineAt(screen.FirstLine + line).Text() + (line < lineCount - 1 ? "\n" : string.Empty);
                int pieceFrom = Math.Max(0, from - starts[line]);
                int pieceTo = Math.Min(row.Length, to - starts[line]);
                text.Append(row, pieceFrom, pieceTo - pieceFrom);
            }
            return text.ToString();
        }

        public override int Caret
        {
            get
            {
                if (Screen is not { } screen)
                {
                    return 0;
                }
                return OffsetOf(screen, screen.ScreenTop + screen.CursorRow, screen.CursorColumn);
            }
        }

        public override IReadOnlyList<TextRange> Selected
        {
            get
            {
                if (Screen is not { } screen || view.Ordered() is not var ((startLine, startColumn), (endLine, endColumn)))
                {
                    return [];
                }
                int start = OffsetOf(screen, startLine, startColumn);
                return [TextRange.FromBounds(start, Math.Max(start, OffsetOf(screen, endLine, endColumn + 1)))];
            }
        }

        /// <summary>What the terminal shows is the programs' to change; typing goes to the program, never into the text.</summary>
        public override bool IsReadOnly => true;

        public override void Replace(TextRange range, string text) => throw new InvalidOperationException("The terminal's text is its programs' to change");

        public override IReadOnlyList<Rect> BoundsOf(TextRange range)
        {
            if (Screen is not { } screen)
            {
                return [];
            }
            Index();
            List<Rect> rows = [];
            int first = LineOf(range.Start);
            int last = LineOf(Math.Max(range.Start, range.End));
            long top = view.TopLine;
            for (int line = first; line <= last; line++)
            {
                long number = screen.FirstLine + line;
                if (number < top || number >= top + screen.Rows)
                {
                    continue;
                }
                TerminalLine row = screen.LineAt(number);
                int from = row.ColumnOf(Math.Max(0, range.Start - starts[line]));
                int to = row.ColumnOf(Math.Max(0, Math.Min(range.End, starts[line + 1] - 1) - starts[line]));
                rows.Add(new Rect(Pad + (from * view.cellWidth), Pad + ((number - top) * view.cellHeight), Math.Max(0, to - from) * view.cellWidth, view.cellHeight));
            }
            return rows;
        }

        public override int OffsetAt(Point point)
        {
            if (Screen is not { } screen)
            {
                return 0;
            }
            (long line, int column) = view.CellAt(point);
            return OffsetOf(screen, line, column);
        }

        /// <summary>Tells what changed since last told: the oldest lines gone, then the lines from the first written since
        /// to the end (the scrollback's lines are never written once scrolled off); then the caret's move or another
        /// selection.</summary>
        public void Follow()
        {
            Told was = told;
            Told now = Seen();
            if (now.Screen is not { } screen || !ReferenceEquals(screen, was.Screen) || now.First < was.First)
            {
                OnChanged(0, was.Length, now.Length);
            }
            else
            {
                int gone = was.StartOf(now.First);
                if (gone > 0)
                {
                    OnChanged(0, gone, 0);
                }
                long changed = Math.Max(now.First, was.ScreenTop);
                while (changed - was.ScreenTop < was.Rows.Length && changed < now.ScreenTop + now.Rows.Length
                    && was.Rows[changed - was.ScreenTop] is var (line, writes) && ReferenceEquals(line, screen.LineAt(changed)) && writes == line.Writes)
                {
                    changed++;
                }
                int from = now.StartOf(changed);
                int removed = was.Length - gone - from;
                if (removed != 0 || now.Length != from)
                {
                    OnChanged(from, Math.Max(0, removed), now.Length - from);
                }
            }
            spare = was.Starts;
            told = now;
            (int Caret, TextRange? Selected) caret = (Caret, Selected is [var selected] ? selected : null);
            if (caret != toldCaret)
            {
                toldCaret = caret;
                OnCaretMoved();
            }
        }

        /// <summary>The lines as they stand, their starts copied into the array told before last.</summary>
        private Told Seen()
        {
            Index();
            int[] kept = spare.Length >= lineCount + 1 ? spare : new int[starts.Length];
            Array.Copy(starts, kept, lineCount + 1);
            if (Screen is not { } screen)
            {
                return new Told(kept, lineCount, null, 0, 0, []);
            }
            return new Told(kept, lineCount, screen, screen.FirstLine, screen.ScreenTop, [.. screen.Lines.Select(l => (l, l.Writes))]);
        }

        /// <summary>A place among the lines, by a line's number and a column, as an offset in the text.</summary>
        private int OffsetOf(TerminalScreen screen, long number, int column)
        {
            Index();
            int line = (int)Math.Clamp(number - screen.FirstLine, 0, lineCount - 1);
            int length = starts[line + 1] - starts[line] - 1;
            return starts[line] + Math.Min(length, screen.LineAt(screen.FirstLine + line).IndexOf(column));
        }

        /// <summary>Finds each line's start again where the screen changed since they were found.</summary>
        private void Index()
        {
            TerminalScreen? screen = Screen;
            if (screen is null)
            {
                (starts, lineCount, indexed) = ([0, 1], 1, (null, -1, -1));
                return;
            }
            if (ReferenceEquals(indexed.Screen, screen) && indexed.Version == screen.Version && indexed.First == screen.FirstLine)
            {
                return;
            }
            int count = (int)(screen.ScreenTop + screen.Rows - screen.FirstLine);
            if (starts.Length < count + 1)
            {
                starts = new int[count + 1];
            }
            int at = 0;
            for (int line = 0; line < count; line++)
            {
                starts[line] = at;
                at += screen.LineAt(screen.FirstLine + line).TextLength() + 1;
            }
            starts[count] = at;
            lineCount = count;
            indexed = (screen, screen.Version, screen.FirstLine);
        }
    }
}
