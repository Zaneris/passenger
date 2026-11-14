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

    /// <summary>Set once Vim took the key just pressed in insert mode (a mapping holding it), so its text does not type.</summary>
    private bool vimTookText;

    private VimRegisters vimRegisters = new();

    /// <summary>The wait for more keys toward a mapping (timeoutlen), and the length it waits.</summary>
    private (Debounce Wait, int Length)? vimTimeout;

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
                FollowVimTimeout();
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

    /// <summary>The keys of <see cref="Vim.CtrlKeys"/> left to the keymap rather than taken by Vim mode, by Vim's names.</summary>
    public IReadOnlySet<string> VimLeavesKeys { get; set; } = new HashSet<string>();

    /// <summary>The width Vim's gq wraps at where textwidth is not set (the file's max_line_length); 0 for none.</summary>
    public int LineLength { get; set; }

    /// <summary>Vim's mode now, or null with Vim mode off: for the status bar.</summary>
    public VimMode? VimModeShown => vim?.Mode;

    /// <summary>The keys of a Vim command typed so far ("2d"), and the register q records into, empty with none or Vim
    /// mode off.</summary>
    public string VimPending => vim is null ? string.Empty : vim.Recording is { } register ? $"{vim.Pending} recording @{register}".TrimStart() : vim.Pending;

    /// <summary>Vim's mode, its half-typed command or its command line changed.</summary>
    public event EventHandler? VimModeChanged;

    /// <summary>The command line Vim mode has open (":s/a/b", "/foo") for the host's status bar; null with none.</summary>
    public string? VimCommandLine => vim?.CommandLine;

    /// <summary>Vim mode has words for the host's status bar: a search's wrap or failure, an ex command's answer.</summary>
    public event EventHandler<VimMessage>? VimSaid;

    /// <summary>Vim mode asks what only the host can do: save, close or open a file, or step through its places.</summary>
    public event EventHandler<VimRequest>? VimAsked;

    /// <summary>A Vim jump left a place (the offset given), for the host's history of places.</summary>
    public event EventHandler<int>? VimJumped;

    /// <summary>A key through Vim mode before any keymap: true when Vim took it, with <paramref name="action"/> (one of
    /// <see cref="EditorActions"/>) for the host to run <paramref name="repeat"/> times as its keymap would, so undo stays
    /// the window's. A modifier pressed alone and every key while an input method composes are left to the keymap.</summary>
    public bool PressVim(KeyEventArgs e, out string? action, out int repeat)
    {
        action = null;
        repeat = 0;
        vimTookText = false;
        if (vim is null || KeymapDispatcher.IsModifierOnly(e.Key) || Preedit.Length > 0)
        {
            // A modifier pressed on its own comes before the key it changes, so it must not end a count or operator.
            return false;
        }
        string key = VimKey(e);
        if (VimLeavesKeys.Contains(key))
        {
            return false;
        }
        VimMode was = vim.Mode;
        string pendingWas = VimPending;
        string? lineWas = vim.CommandLine;
        VimResult result = vim.Press(key, document.Snapshot, selections, VimView);
        vimTookText = result.Taken && was is VimMode.Insert or VimMode.Replace;
        FollowVim(result, was);
        action = result.Action;
        repeat = action is null ? 0 : result.Repeat;
        if (vim?.Mode == was && (VimPending != pendingWas || vim.CommandLine != lineWas))
        {
            VimModeChanged?.Invoke(this, EventArgs.Empty);
        }
        FollowVimTimeout();
        return result.Taken;
    }

    /// <summary>Keys held toward a mapping wait timeoutlen for more, begun again at each key, then mean themselves; with
    /// timeout off, or none held, nothing waits.</summary>
    private void FollowVimTimeout()
    {
        VimOptions options = vimRegisters.Options;
        if (vim is not { AwaitsMapping: true } || !options.Timeout)
        {
            vimTimeout?.Wait.Stop();
            return;
        }
        if (vimTimeout is not { } timeout || timeout.Length != options.TimeoutLength)
        {
            vimTimeout?.Wait.Stop();
            timeout = (new Debounce(TimeSpan.FromMilliseconds(options.TimeoutLength), VimTimedOutAsync), options.TimeoutLength);
            vimTimeout = timeout;
        }
        timeout.Wait.Restart();
    }

    /// <summary>timeoutlen passed with keys held toward a mapping: they are pressed as what they come to.</summary>
    private Task VimTimedOutAsync()
    {
        if (vim is { AwaitsMapping: true } held)
        {
            FollowVim(held.TimedOut(), held.Mode);
            VimModeChanged?.Invoke(this, EventArgs.Empty);
            FollowVimTimeout();
        }
        return Task.CompletedTask;
    }

    /// <summary>A command line run as if typed after ':' (one of :bufdo's, in the file shown), what it does done and asked of
    /// the host; the words it said, null for none or with Vim mode off.</summary>
    public VimMessage? RunVimCommand(string line)
    {
        if (vim is null)
        {
            return null;
        }
        VimResult result = vim.RunCommand(line, document.Snapshot, selections, VimView);
        FollowVim(result, vim.Mode);
        return result.Message is { } said ? new VimMessage(said, result.MessageIsError) : null;
    }

    /// <summary>Leaves insert mode as Escape does, where something else heard the Escape first (the code's choices it
    /// closed); false with Vim mode off or already in normal mode.</summary>
    public bool EndVimInsert()
    {
        if (vim is not { Mode: VimMode.Insert } || Preedit.Length > 0)
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
        bool entering = was is not (VimMode.Insert or VimMode.Replace) && vim?.Mode is VimMode.Insert or VimMode.Replace;
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
        if (result.Highlight is { } search)
        {
            ShowVimSearch(search);
        }
        if (result.Message is { } said)
        {
            VimSaid?.Invoke(this, new VimMessage(said, result.MessageIsError));
        }
        if (result.JumpedFrom is { } from)
        {
            VimJumped?.Invoke(this, from);
        }
        if (result.Replay is { } keys)
        {
            ReplayVim(keys);
        }
        if (result.Request is { } asked)
        {
            VimAsked?.Invoke(this, asked);
        }
    }

    /// <summary>A Vim search's matches shown as the editor's own find shows them, the one at the caret the current one;
    /// none for an empty query.</summary>
    private void ShowVimSearch(SearchQuery search)
    {
        if (search.IsEmpty)
        {
            SetHighlights([], -1);
            return;
        }
        IReadOnlyList<TextRange> matches = TextSearch.FindAll(document.Snapshot, search, TextSearch.AtOnce).Matches;
        int caret = selections.Primary.Caret;
        int current = -1;
        for (int i = 0; i < matches.Count; i++)
        {
            if (matches[i].Start == caret || (matches[i].Start <= caret && caret < matches[i].End))
            {
                current = i;
                break;
            }
        }
        SetHighlights(matches, current);
    }

    /// <summary>A macro's keys pressed in turn as typed, insert mode's typing done as the keymap's Enter, Backspace and
    /// Tab do; a macro's own @ pressing its keys in their place, up to <see cref="Vim.MostReplayed"/> keys in all.</summary>
    private void ReplayVim(IReadOnlyList<string> keys)
    {
        List<string> queue = [.. keys];
        if (vim is not null)
        {
            vim.Playing = true;
        }
        try
        {
            PressInTurn(queue);
        }
        finally
        {
            if (vim is not null)
            {
                vim.Playing = false;
            }
        }
        VimModeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PressInTurn(List<string> queue)
    {
        for (int i = 0; vim is not null && i < queue.Count && i < Vim.MostReplayed; i++)
        {
            string key = queue[i];
            VimMode was = vim.Mode;
            VimResult result = vim.Press(key, document.Snapshot, selections, VimView);
            if (result.Replay is { } more)
            {
                queue.InsertRange(i + 1, more.Take(Vim.MostReplayed - queue.Count));
                result = result with { Replay = null };
            }
            FollowVimResult(result, was);
            for (int r = 0; result.Action is { } action && r < result.Repeat; r++)
            {
                TryExecute(action);
            }
            if (!result.Taken && vim.Mode == VimMode.Insert)
            {
                string? action = key switch { "<CR>" => EditorActions.NewLine, "<BS>" => EditorActions.Backspace, "<Tab>" => EditorActions.Indent, _ => null };
                if (action is not null)
                {
                    TryExecute(action);
                }
                else if (key.Length <= 2 && !key.StartsWith('<'))
                {
                    Type(key);
                }
            }
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
            KeyModifiers.Control when e.Key == Key.O => "<C-o>",
            KeyModifiers.Control when e.Key == Key.I => "<C-i>",
            KeyModifiers.Control when e.Key == Key.U => "<C-u>",
            KeyModifiers.Control when e.Key == Key.W => "<C-w>",
            // Any other Ctrl and a letter by its name, which a mapping may take and Vim mode otherwise leaves to the keymap.
            KeyModifiers.Control when e.Key is >= Key.A and <= Key.Z => $"<C-{(char)('a' + (e.Key - Key.A))}>",
            KeyModifiers.None when e.Key == Key.Up => "<Up>",
            KeyModifiers.None when e.Key == Key.Down => "<Down>",
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

        public string? LineComment => editor.LineCommentPrefix;

        public int LineLength => editor.LineLength;

        public string? Refuses => editor.IsReadOnly ? editor.ReadOnlyWhy ?? string.Empty : null;

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
