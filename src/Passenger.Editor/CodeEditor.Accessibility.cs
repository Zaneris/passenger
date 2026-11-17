using Avalonia;
using Avalonia.Automation.Peers;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>The editor as a screen reader reads it (<see cref="AccessibleText"/>): the file's text and its lines, whatever
/// soft wrap or folds show of them, read from the document's snapshot as asked; made the first time a reader asks, so an
/// editor no reader reads tells nothing as keys change its text.</summary>
public sealed partial class CodeEditor
{
    private EditorText? accessibleText;

    /// <summary>The text as each system's screen-reader bridge reads it.</summary>
    public AccessibleText AccessibleText => accessibleText ??= new EditorText(this);

    protected override AutomationPeer OnCreateAutomationPeer() => new AccessibleTextPeer(this, AccessibleText);

    private sealed class EditorText : AccessibleText
    {
        private readonly CodeEditor editor;

        /// <summary>The document whose changes are heard, which the editor may show another in place of.</summary>
        private TextDocument heard;

        /// <summary>The text before the change being told, while it is told.</summary>
        private TextSnapshot? before;

        public EditorText(CodeEditor editor)
        {
            this.editor = editor;
            heard = editor.document;
            heard.Changed += Heard;
            editor.SelectionChanged += (_, _) => FollowCaret();
        }

        private TextSnapshot Snapshot => editor.document.Snapshot;

        public override int Length => Snapshot.Length;

        public override int LineCount => Snapshot.LineCount;

        public override LineSpan Line(int line) => Snapshot.GetLine(Math.Clamp(line, 0, Snapshot.LineCount - 1));

        public override int LineOf(int offset) => Snapshot.GetLineFromOffset(Math.Clamp(offset, 0, Snapshot.Length));

        public override string Read(int start, int length)
        {
            TextSnapshot text = Snapshot;
            int from = Math.Clamp(start, 0, text.Length);
            return text.GetText(from, Math.Clamp(length, 0, text.Length - from));
        }

        public override int Caret => Math.Min(editor.selections.Primary.Caret, Snapshot.Length);

        public override IReadOnlyList<TextRange> Selected =>
            [.. editor.selections.Items.Select(s => TextRange.FromBounds(s.Start, s.End)).OrderBy(r => r.Start)];

        public override bool IsReadOnly => editor.IsReadOnly;

        public override void Replace(TextRange range, string text)
        {
            int start = Math.Clamp(range.Start, 0, Snapshot.Length);
            editor.ApplyEdits([new TextEdit(start, Math.Clamp(range.Length, 0, Snapshot.Length - start), text)]);
        }

        public override IReadOnlyList<Rect> BoundsOf(TextRange range)
        {
            TextSnapshot text = Snapshot;
            int start = Math.Clamp(range.Start, 0, text.Length);
            int end = Math.Clamp(range.End, start, text.Length);
            List<Rect> rows = [];
            for (int line = text.GetLineFromOffset(start), last = text.GetLineFromOffset(end); line <= last; line++)
            {
                LineSpan span = text.GetLine(line);
                if (!editor.IsHidden(line))
                {
                    RowsOf(text, Math.Max(start, span.Start), Math.Min(end, span.End), rows);
                }
            }
            Rect shown = new(editor.Bounds.Size);
            return [.. rows.Where(r => r.Intersects(shown) || (r.Width == 0 && shown.Contains(r.TopLeft)))];
        }

        public override int OffsetAt(Point point) => editor.OffsetAt(point);

        public override TextRange Shown
        {
            get
            {
                (int first, int last) = editor.LinesShown;
                return TextRange.FromBounds(Line(first).Start, Line(last).EndIncludingBreak);
            }
        }

        public override void Select(TextRange range)
        {
            int start = Math.Clamp(range.Start, 0, Snapshot.Length);
            editor.Select(TextRange.FromBounds(start, Math.Clamp(range.End, start, Snapshot.Length)));
        }

        public override void Show(TextRange range)
        {
            int start = Math.Clamp(range.Start, 0, Snapshot.Length);
            editor.Reveal(Math.Clamp(range.End, start, Snapshot.Length));
            editor.Reveal(start);
        }

        /// <summary>A rectangle for each row soft wrap makes of a piece of one line, an empty piece's as wide as nothing.</summary>
        private void RowsOf(TextSnapshot text, int from, int to, List<Rect> rows)
        {
            double height = editor.metrics.LineHeight;
            int at = from;
            while (true)
            {
                (RowSpan span, int row) = editor.RowOfOffset(text, at, beforeWrap: false);
                int rowEnd = Math.Min(to, span.End);
                double left = editor.Place(text, at, beforeWrap: false).X;
                double right = editor.Place(text, rowEnd, beforeWrap: true).X;
                rows.Add(new Rect(editor.TextLeft - editor.scroll.X + left, (row * height) - editor.scroll.Y, Math.Max(0, right - left), height));
                if (rowEnd >= to || span.IsLast || span.End <= at)
                {
                    return;
                }
                at = span.End;
            }
        }

        /// <summary>Tells the document's changes, last to first as each is valid in the text the one before left.</summary>
        private void Heard(object? sender, DocumentChangedEventArgs e)
        {
            before = e.Before;
            try
            {
                for (int i = e.Edits.Count - 1; i >= 0; i--)
                {
                    TextEdit edit = e.Edits[i];
                    OnChanged(edit.Start, edit.Length, edit.Text.Length);
                }
            }
            finally
            {
                before = null;
            }
        }

        /// <summary>Read from the text before the change, where each edit's place is as the document's change gives it.</summary>
        public override string? ReadRemoved(AccessibleTextChange change, int most)
        {
            if (before is not { } text)
            {
                return null;
            }
            int start = Math.Clamp(change.Start, 0, text.Length);
            return text.GetText(start, Math.Clamp(Math.Min(change.Removed, most), 0, text.Length - start));
        }

        /// <summary>Tells the caret's move, and another document shown as its whole text changed.</summary>
        private void FollowCaret()
        {
            if (!ReferenceEquals(heard, editor.document))
            {
                int was = heard.Snapshot.Length;
                heard.Changed -= Heard;
                heard = editor.document;
                heard.Changed += Heard;
                OnChanged(0, was, heard.Snapshot.Length);
            }
            OnCaretMoved();
        }
    }
}
