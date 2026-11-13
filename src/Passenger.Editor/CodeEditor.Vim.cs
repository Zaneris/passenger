using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Vim mode in the editor: keys through <see cref="Vim"/> before any keymap, carets standing on a character in
/// normal mode however they were put there, the input method off in normal mode so each key reaches it as a command, and
/// what insert mode types one step of undo.</summary>
public sealed partial class CodeEditor
{
    private Vim? vim;

    /// <summary>Whether keys go through Vim's modes first (<see cref="PressVim"/>); turned off, the editor is modeless again.</summary>
    public bool UsesVim
    {
        get => vim is not null;
        set
        {
            if (UsesVim != value)
            {
                vim = value ? new Vim() : null;
                FollowVimMode();
                SetSelections(selections, reveal: false);
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
        VimResult result = vim.Press(VimKey(e), document.Snapshot, selections);
        if (vim.Mode != was)
        {
            FollowVimMode();
        }
        if (result.Edit is { } edit)
        {
            Apply(edit);
        }
        else if (result.Selections is { } moved)
        {
            Selections = moved;
        }
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
        VimResult result = vim.Press(Vim.Escape, document.Snapshot, selections);
        FollowVimMode();
        if (result.Selections is { } moved)
        {
            Selections = moved;
        }
        return true;
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
        Avalonia.Input.InputMethod.SetIsInputMethodEnabled(this, vim is not { Mode: VimMode.Normal });
        VimModeChanged?.Invoke(this, EventArgs.Empty);
        RedrawOverlay();
    }

    /// <summary>The kind of undo step an edit makes: in insert mode every edit the editor makes joins one step, Vim's.</summary>
    private EditKind StepOf(EditKind kind) => vim is { Mode: VimMode.Insert } ? EditKind.Joined : kind;

    /// <summary>Carets as normal mode lets them stand, on a character, wherever they were put (a click, the keymap's End,
    /// an undo); a selection stays as it is.</summary>
    private SelectionSet VimSettled(SelectionSet carets) =>
        vim is { Mode: VimMode.Normal } ? Vim.Settled(document.Snapshot, carets) : carets;

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
}
