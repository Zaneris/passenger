using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Passenger.Core;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Vim mode in the editor: keys through <see cref="Vim"/> before any keymap, carets standing on a character in
/// normal mode however they were put there, the input method off in normal mode so each key reaches it as a command, and
/// what insert mode types one step of undo.</summary>
public sealed partial class CodeEditor
{
    private Vim? vim;

    /// <summary>Set while the editor does what Vim asked, so the selections it sets are Vim's own.</summary>
    private bool followingVim;

    private VimRegisters vimRegisters = new();

    private EditorVimView? vimView;

    private IVimView VimView => vimView ??= new EditorVimView(this);

    /// <summary>Whether keys go through Vim's modes first (<see cref="PressVim"/>); turned off, the editor is modeless again.</summary>
    public bool UsesVim
    {
        get => vim is not null;
        set
        {
            if (UsesVim != value)
            {
                vim = value ? new Vim(vimRegisters) : null;
                FollowVimMode();
                SetSelections(selections, reveal: false);
            }
        }
    }

    /// <summary>The registers Vim mode yanks into and puts from: the editor's own until its host gives the set its editors
    /// share (a window's), so what one editor yanks another of the same host puts.</summary>
    public VimRegisters VimRegisters
    {
        get => vimRegisters;
        set
        {
            vimRegisters = value;
            if (vim is not null)
            {
                vim.Registers = value;
            }
        }
    }

    /// <summary>Vim's mode now, or null with Vim mode off: for the status bar.</summary>
    public VimMode? VimModeShown => vim?.Mode;

    /// <summary>The keys of a Vim command typed so far ("2d"), empty with none or Vim mode off.</summary>
    public string VimPending => vim?.Pending ?? string.Empty;

    /// <summary>Vim's mode or its half-typed command changed.</summary>
    public event EventHandler? VimModeChanged;

    /// <summary>A key through Vim mode before any keymap: true when Vim took it, with <paramref name="action"/> (one of
    /// <see cref="EditorActions"/>) for the host to run <paramref name="repeat"/> times as its keymap would, so undo stays
    /// the window's. A modifier pressed alone and every key while an input method composes are left to the keymap.</summary>
    public bool PressVim(KeyEventArgs e, out string? action, out int repeat)
    {
        action = null;
        repeat = 0;
        if (vim is null || KeymapDispatcher.IsModifierOnly(e.Key) || preedit.Length > 0)
        {
            // A modifier pressed on its own comes before the key it changes, so it must not end a count or operator.
            return false;
        }
        VimMode was = vim.Mode;
        string pendingWas = vim.Pending;
        VimResult result = vim.Press(VimKey(e), document.Snapshot, selections, VimView);
        FollowVim(result, was);
        action = result.Action;
        repeat = action is null ? 0 : result.Repeat;
        if (vim.Mode == was && vim.Pending != pendingWas)
        {
            VimModeChanged?.Invoke(this, EventArgs.Empty);
        }
        return result.Taken;
    }

    /// <summary>Leaves insert mode as Escape does, where something else heard the Escape first (the code's choices it
    /// closed); false with Vim mode off or already in normal mode.</summary>
    public bool EndVimInsert()
    {
        if (vim is not { Mode: VimMode.Insert } || preedit.Length > 0)
        {
            return false;
        }
        FollowVim(vim.Press(Vim.Escape, document.Snapshot, selections, VimView), VimMode.Insert);
        return true;
    }

    /// <summary>What Vim made of a key, done: entering insert mode first so the edit that begins it joins its step of
    /// undo, leaving it last so the edits that end it do too; several edits of normal mode one step; the clipboard written
    /// or read for + and *.</summary>
    private void FollowVim(VimResult result, VimMode was)
    {
        followingVim = true;
        try
        {
            FollowVimResult(result, was);
        }
        finally
        {
            followingVim = false;
        }
    }

    private void FollowVimResult(VimResult result, VimMode was)
    {
        if (Vim.IsVisual(was) && result.Edits is { Count: > 0 })
        {
            // Undo puts back carets at the visual stretches' starts, never the stretches themselves.
            Selections = selections.With(s => Selection.At(s.Start));
        }
        bool entering = was != VimMode.Insert && vim?.Mode == VimMode.Insert;
        if (entering)
        {
            FollowVimMode();
        }
        IReadOnlyList<EditPlan> edits = result.Edits ?? [];
        bool oneStep = !entering && was is not (VimMode.Insert or VimMode.Replace) && edits.Count > 1;
        if (oneStep)
        {
            document.BreakUndoGroup();
        }
        foreach (EditPlan plan in edits)
        {
            Apply(plan);
        }
        if (oneStep)
        {
            document.BreakUndoGroup();
        }
        if (edits.Count == 0 && result.Selections is { } moved)
        {
            Selections = moved;
        }
        if (!entering && vim?.Mode != was)
        {
            FollowVimMode();
        }
        if (result.Clipboard is { } copied)
        {
            SetClipboard(LineEndings.In(copied.Text, document.LineEnding), copied.Linewise);
        }
        if (result.ReadsClipboard)
        {
            BackgroundWork.Track(PutFromClipboardAsync());
        }
    }

    /// <summary>A put from + or *: the system clipboard's text, read as Paste reads it, put as Vim puts it.</summary>
    private async Task PutFromClipboardAsync()
    {
        string? pasted = TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        if (vim is not null)
        {
            FollowVim(vim.PutClipboard(pasted ?? string.Empty, document.Snapshot, selections, VimView), vim.Mode);
        }
    }

    /// <summary>A key press in Vim's notation: a character typed with no modifier but Shift (or the AltGr a layout types
    /// it with), Escape, Backspace, Enter, Delete, Tab, Ctrl+R; anything else a name Vim mode leaves to the keymap.</summary>
    private static string VimKey(KeyEventArgs e)
    {
        KeyModifiers held = e.KeyModifiers & ~KeyModifiers.Shift;
        return held switch
        {
            KeyModifiers.None when e.Key == Key.Escape => Vim.Escape,
            // Named so normal mode takes them: on to the keymap they would type a line break or a tab, or delete.
            KeyModifiers.None when e.Key == Key.Back => "<BS>",
            KeyModifiers.None when e.Key == Key.Enter => "<CR>",
            KeyModifiers.None when e.Key == Key.Delete => "<Del>",
            KeyModifiers.None when e.Key == Key.Tab => "<Tab>",
            KeyModifiers.Control when e.Key == Key.R => "<C-r>",
            KeyModifiers.Control when e.Key == Key.V => "<C-v>",
            KeyModifiers.None when e.KeySymbol is { Length: 1 } typed && !char.IsControl(typed[0]) => typed,
            _ when KeymapDispatcher.IsAltGrTyping(e) && e.KeySymbol is { Length: 1 } typed => typed,
            // A press whose platform gives no symbol: letters and digits by their key, as punctuation's differs by layout.
            KeyModifiers.None when e.KeySymbol is null && e.Key is >= Key.A and <= Key.Z =>
                ((char)((e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 'A' : 'a') + (e.Key - Key.A))).ToString(),
            KeyModifiers.None when e.KeySymbol is null && e.Key is >= Key.D0 and <= Key.D9 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) =>
                ((char)('0' + (e.Key - Key.D0))).ToString(),
            _ => "<Other>",
        };
    }

    /// <summary>What a change of Vim's mode changes: the caret's shape, whether an input method composes (never in normal
    /// mode, whose keys are commands), and where an insert session's step of undo begins and ends.</summary>
    private void FollowVimMode()
    {
        document.BreakUndoGroup();
        Avalonia.Input.InputMethod.SetIsInputMethodEnabled(this, vim is null or { Mode: VimMode.Insert });
        VimModeChanged?.Invoke(this, EventArgs.Empty);
        RedrawOverlay();
    }

    /// <summary>The kind of undo step an edit makes: in insert mode every edit the editor makes joins one step, Vim's.</summary>
    private EditKind StepOf(EditKind kind) => vim is { Mode: VimMode.Insert } ? EditKind.Joined : kind;

    /// <summary>Carets as normal mode lets them stand, on a character, wherever they were put (a click, the keymap's End,
    /// an undo); a selection put there from outside Vim's keys (a drag, Select All) begins visual mode, and visual mode
    /// ends where its selections went.</summary>
    private SelectionSet VimSettled(SelectionSet carets)
    {
        if (vim is null)
        {
            return carets;
        }
        if (!followingVim && vim.FollowSelections(document.Snapshot, carets, VimView))
        {
            VimModeChanged?.Invoke(this, EventArgs.Empty);
            Avalonia.Input.InputMethod.SetIsInputMethodEnabled(this, vim.Mode == VimMode.Insert);
        }
        return vim.Mode == VimMode.Normal ? Vim.Settled(document.Snapshot, carets) : carets;
    }

    /// <summary>Where visual mode's block carets stand: its cursors, while the selections shown are its own.</summary>
    private IReadOnlyList<int> VisualCursors => vim is { } v && Vim.IsVisual(v.Mode) ? v.VisualCursors(selections) : [];

    /// <summary>Normal mode's block over the character at the caret, as wide as it is drawn (a tab's stretch, a wide
    /// character's two cells), one cell at a line's end.</summary>
    private void DrawBlockCaret(DrawingContext context, TextSnapshot text, int caret, int row, double x, double y, double left)
    {
        double width = metrics.CharWidth;
        int next = TextNavigation.NextGrapheme(text, caret);
        if (next > caret && next <= text.GetLine(text.GetLineFromOffset(caret)).End)
        {
            (int nextRow, double nextX) = Place(text, next, beforeWrap: true);
            if (nextRow == row && nextX > x)
            {
                width = nextX - x;
            }
        }
        Rect block = new(Math.Round(left + x), y, Math.Max(1, Math.Round(left + x + width) - Math.Round(left + x)), metrics.LineHeight);
        context.FillRectangle(palette.BlockCaret, block);
        context.DrawRectangle(null, palette.PenOf(palette.Caret, 1), block.Deflate(0.5));
    }

    /// <summary>The editor as Vim mode sees it: its indent and line breaks, its rows for gj and gk, and the rows on screen
    /// for H, M and L.</summary>
    private sealed class EditorVimView(CodeEditor editor) : IVimView
    {
        /// <summary>Each document's marks, held by anchors so edits carry them, shared by every editor showing it.</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextDocument, Dictionary<char, Anchor>> Marks = new();

        public IndentOptions Indent => editor.Indent;

        public string LineEnding => editor.document.LineEnding;

        public MarkupKind Markup => editor.Markup;

        public int RowsAway(TextSnapshot text, int offset, int rows)
        {
            (int row, double x) = editor.Place(text, offset, editor.LeansBack(offset));
            int target = Math.Clamp(row + rows, 0, Math.Max(0, editor.RowCount - 1));
            return target == row ? offset : editor.OffsetAtRow(text, target, x, out _);
        }

        public int? Mark(char name, TextSnapshot text) =>
            Marks.TryGetValue(editor.document, out Dictionary<char, Anchor>? held) && held.TryGetValue(name, out Anchor? anchor) && anchor.IsLive
                ? Math.Min(anchor.Offset, text.Length)
                : null;

        public void SetMark(char name, int offset)
        {
            Dictionary<char, Anchor> held = Marks.GetOrCreateValue(editor.document);
            if (held.TryGetValue(name, out Anchor? before))
            {
                before.Remove();
            }
            held[name] = editor.document.AnchorAt(Math.Clamp(offset, 0, editor.document.Snapshot.Length), AnchorGravity.Before);
        }

        public int LineShown(TextSnapshot text, VimScreenLine at, int count)
        {
            double height = editor.metrics.LineHeight;
            int first = (int)Math.Ceiling(editor.scroll.Y / height);
            int last = Math.Min((int)Math.Floor((editor.scroll.Y + editor.Bounds.Height) / height) - 1, editor.RowCount - 1);
            last = Math.Max(first, last);
            int row = at switch
            {
                VimScreenLine.Top => (int)Math.Min(first + (long)count - 1, last),
                VimScreenLine.Bottom => (int)Math.Max(last - (long)count + 1, first),
                _ => (first + last) / 2,
            };
            return editor.Rows.LineOfRow(row).Line;
        }
    }
}
