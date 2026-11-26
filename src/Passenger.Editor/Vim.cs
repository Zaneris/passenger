using Passenger.Text;

namespace Passenger.Editor;

public enum VimMode
{
    Normal,
    Insert,
    Visual,
    VisualLine,
    VisualBlock,
    Replace,
}

/// <summary>What a key did in Vim mode: taken, or left to the keymap; and what the editor is to do with it: carets to set,
/// edits to apply in turn as one step of undo, or one of <see cref="EditorActions"/> to run <see cref="Repeat"/> times.</summary>
public sealed record VimResult(bool Taken, SelectionSet? Selections = null, IReadOnlyList<EditPlan>? Edits = null, string? Action = null, int Repeat = 1)
{
    public static readonly VimResult Left = new(false);

    public static readonly VimResult Held = new(true);

    /// <summary>Text a yank or delete wrote to + or *, for the editor to put on the system clipboard.</summary>
    public VimRegister? Clipboard { get; init; }

    /// <summary>A put from + or *: the editor reads the system clipboard and hands it to <see cref="Vim.PutClipboard"/>.</summary>
    public bool ReadsClipboard { get; init; }

    /// <summary>Keys for the editor to press in turn, as typed (a macro's): insert mode's typing its own to do.</summary>
    public IReadOnlyList<string>? Replay { get; init; }

    /// <summary>A search's matches for the editor to show by its own find; an empty query to show none (:noh); null to
    /// leave what is shown.</summary>
    public SearchQuery? Highlight { get; init; }

    /// <summary>Words for the host's status bar: a search's wrap or failure, an ex command's answer or refusal.</summary>
    public string? Message { get; init; }

    public bool MessageIsError { get; init; }

    /// <summary>What only the host can do: save, close or open a file, or step through its places.</summary>
    public VimRequest? Request { get; init; }

    /// <summary>Where the caret jumped from, for the host's places (Back and Forward).</summary>
    public int? JumpedFrom { get; init; }

    /// <summary>The last of <see cref="Edits"/>, or null with none.</summary>
    public EditPlan? Edit => Edits is { Count: > 0 } edits ? edits[^1] : null;
}

/// <summary>Vim's normal and insert modes as a function from keys to what the editor does, knowing nothing of the window.
/// Keys come in Vim's notation ("w", "G", "&lt;Esc&gt;", "&lt;C-r&gt;"); counts, registers, operators with motions and text
/// objects, puts and . act at every caret alike; a key normal mode doesn't take (a shortcut with Cmd, an arrow) is left to
/// the keymap, so the IDE's own keys keep working.</summary>
public sealed partial class Vim(VimRegisters registers)
{
    /// <summary>The keys with Ctrl normal and visual mode take, and what each does there, which Settings may give to the
    /// keymap instead.</summary>
    public static readonly IReadOnlyList<(string Key, string Does)> CtrlKeys =
        [(VimKeys.CtrlR, "Redo"), (VimKeys.CtrlV, "Visual block mode"), (VimKeys.CtrlO, "Back to the place before"), (VimKeys.CtrlI, "On to the place after")];

    /// <summary>The most undos or redos a count asks for at once: the window queues each apart.</summary>
    private const int MostRepeated = 1000;

    /// <summary>The most characters a count's repeats of a put or an insert make at once.</summary>
    private const int MostRepeatedText = 10_000_000;

    private static readonly IVimView PlainView = new VimTextView(new IndentOptions());

    private readonly List<string> pending = [];

    /// <summary>The display columns j and k aim for, one per caret of <see cref="wantedFor"/>, so a short line passed
    /// over doesn't pull the caret left for good.</summary>
    private int[] wanted = [];

    private SelectionSet? wantedFor;

    /// <summary>The last f, F, t or T, for ; and , to repeat.</summary>
    private (string Target, bool Back, bool Till)? lastFind;

    /// <summary>The last change, for . to make again.</summary>
    private ChangeMade? lastChange;

    /// <summary>The insert session under way: the command that began it and how, where its first caret began, the text's
    /// length and the carets then, and the count to repeat its text by.</summary>
    private (IReadOnlyList<string> Keys, int Count, string How, int First, int Length, int Carets, int Top)? inserting;

    /// <summary>A put from + or * waiting for the clipboard's text.</summary>
    private (bool Before, int Count, bool CursorAfter)? clipboardPut;

    /// <summary>Set while . makes the last change again, which records nothing.</summary>
    private bool replaying;

    /// <summary>Set while . makes the last change again with a count of its own.</summary>
    private bool replayingCounted;

    /// <summary>Whether the file keeps a final line break (<see cref="IVimView.KeepsFinalLineBreak"/>), as the view said
    /// for what is being done: where it keeps none, a final line break begins an empty last line.</summary>
    private bool breakEnds = true;

    /// <summary>Where the operator's object or motion that just failed left its caret, a count cut short; null where it stays.</summary>
    private int? ranOutAt;

    /// <summary>Set where an operator acted, whatever it changed (x on an empty line, gU over capitals), which is a change
    /// made, not one that failed.</summary>
    private bool actedOnNothing;

    /// <summary>The line an exclusive motion just ended at the start of, which the operator's stretch leaves out; null with none.</summary>
    private int? reachedPast;

    /// <summary>Set while a command writes into a text a delete emptied (<see cref="OnOneEmptyLine"/>), whose one empty line
    /// a delete or change takes nothing of, as Vim's empty buffer gives its registers nothing.</summary>
    private bool intoEmptied;

    /// <summary>Whether the insert session under way began on an emptied text's one line, which it leaves emptied where it
    /// types nothing.</summary>
    private bool typingIntoEmptied;

    public Vim()
        : this(new VimRegisters())
    {
    }

    public VimMode Mode { get; private set; } = VimMode.Normal;

    /// <summary>The count and keys typed toward a command not yet complete ("2d"), and toward a mapping, for the status bar.</summary>
    public string Pending => string.Concat(pending) + string.Concat(typedAhead);

    /// <summary>The registers yanks, deletes and puts use: the set the editor's host gives, shared by the editors it
    /// holds, and a set of the Vim's own where none is given.</summary>
    public VimRegisters Registers { get; set; } = registers;

    /// <summary>Set while the host presses a macro's or :normal's keys, which q records none of.</summary>
    public bool Playing { get; set; }

    public VimResult Press([IdentifierOf(typeof(VimKeys))] string key, TextSnapshot text, SelectionSet selections, IVimView? view = null)
    {
        view ??= PlainView;
        breakEnds = view.KeepsFinalLineBreak;
        if (!replaying && !Playing && Recorded(key))
        {
            return VimResult.Held;
        }
        bool unmapped = unmappedLeft > 0;
        if (unmapped)
        {
            unmappedLeft--;
        }
        if (!replaying && !unmapped && !key.StartsWith(VimKeys.Go, StringComparison.Ordinal) && Mapped(key) is { } mapped)
        {
            return mapped;
        }
        if (commandLine is not null)
        {
            return PressCommandLine(key, text, selections, view);
        }
        if (key.StartsWith(VimKeys.Go, StringComparison.Ordinal) && int.TryParse(key.AsSpan(VimKeys.Go.Length), System.Globalization.CultureInfo.InvariantCulture, out int goLine))
        {
            Mode = VimMode.Normal;
            pending.Clear();
            return new VimResult(true, SelectionSet.Single(text.GetLineStart(Math.Clamp(VimMotions.LastLine(text, breakEnds) - goLine, 0, VimMotions.LastLine(text, breakEnds)))));
        }
        return Take(key, text, selections, view);
    }

    /// <summary>A key in any mode but the command line's.</summary>
    private VimResult Take([IdentifierOf(typeof(VimKeys))] string key, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (Mode == VimMode.Replace)
        {
            return PressReplace(key, text, selections, view);
        }
        if (Mode == VimMode.Insert)
        {
            if (key == VimKeys.Enter)
            {
                FollowLineBreak(text, selections, view);
            }
            return key == VimKeys.Escape ? LeaveInsert(text, selections, view) : VimResult.Left;
        }
        if (IsVisual(Mode))
        {
            return PressVisual(key, text, selections, view);
        }
        if (key == VimKeys.Escape)
        {
            bool waiting = pending.Count > 0;
            pending.Clear();
            return waiting ? VimResult.Held : VimResult.Left;
        }
        if (key == VimKeys.CtrlR)
        {
            int redo = CountOnly();
            pending.Clear();
            return view.Refuses is { } why ? Refused(why) : new VimResult(true, Action: EditorActions.Redo, Repeat: Math.Clamp(redo, 1, MostRepeated));
        }
        if (key == VimKeys.Delete && pending.Count > 0)
        {
            // Delete after a count takes back the count's last digit, as Vim does.
            bool counting = pending.All(k => k.Length == 1 && char.IsAsciiDigit(k[0]));
            if (counting)
            {
                pending.RemoveAt(pending.Count - 1);
            }
            else
            {
                pending.Clear();
            }
            return VimResult.Held;
        }
        if (!IsVimKey(key))
        {
            pending.Clear();
            return VimResult.Left;
        }
        if (key is ":" or "/" or "?" && TakesCommandLine(pending, search: key != ":"))
        {
            return OpenCommandLine(key[0], string.Empty);
        }
        pending.Add(key == VimKeys.Delete ? "x" : key);
        Command? command = Parse(pending, out bool incomplete);
        if (incomplete)
        {
            return VimResult.Held;
        }
        pending.Clear();
        searchSaid = null;
        // A command Vim mode doesn't know is swallowed whole, so its keys never type into the text.
        SelectionSet? aimedFor = wantedFor;
        VimResult result = command is null ? VimResult.Held
            : WritesIntoEmpty(command, text, view) ? OnOneEmptyLine(command, text, view)
            : Run(command, text, selections, view);
        if ((command is { Operator: null, Name: "." or "p" or "P" or "gp" or "gP" or "~" or "r" or "h" or "l" or "f" or "F" or "t" or "T" or ";" or "," or "*" or "#"
                or "S" or "Y" or "s" or "x" or "X" or "J" or "gJ" or "%" or "+" or "-" or "_" }
            && (ReferenceEquals(result, VimResult.Held) || result is { Edits: null, Selections: null, ReadsClipboard: false }))
            || (command is { Operator: not null, Name: not ("i" or "a" or "$" or "(" or ")" or "{" or "}" or "b" or "B" or "ge" or "gE" or "n" or "N") }
                && ReferenceEquals(result, VimResult.Held)))
        {
            // One that can't act (nothing to repeat or put, nowhere to go, its operator's motion failing but by sentence, paragraph, word back or search) keeps j and k's column.
            wantedFor = aimedFor;
        }
        if (command is { Operator: not null, Name: "$" } or { Operator: null, Name: "C" or "D" } && ReferenceEquals(result, VimResult.Held))
        {
            // An operator's $ cut short aims j and k at the line's end all the same.
            (wantedFor, wanted) = (selections, [.. Enumerable.Repeat(int.MaxValue, selections.Count)]);
        }
        return Told(result, command?.Name);
    }

    /// <summary>Whether a command writes into a text left empty in a file that keeps its final line break: a change, an
    /// insert or a put, which there writes into the one empty line Vim's empty buffer holds.</summary>
    private bool WritesIntoEmpty(Command command, TextSnapshot text, IVimView view) =>
        text.Length == 0 && breakEnds && view.Refuses is null && (command.Operator == "c"
            || command is { Operator: null, Name: "C" or "s" or "S" or "i" or "a" or "I" or "gI" or "A" or "o" or "O" or "p" or "P" or "gp" or "gP" or "R" });

    /// <summary>A command that writes into an empty text run on the one empty line Vim's empty buffer holds, given its line
    /// break first, so what it writes keeps the file's final line break as Vim's 'eol' does; one that writes nothing (a put
    /// of an empty register) leaves the text empty.</summary>
    private VimResult OnOneEmptyLine(Command command, TextSnapshot text, IVimView view) =>
        OnOneEmptyLine((lined, carets) => Run(command, lined, carets, view), text, view);

    /// <summary>What <paramref name="act"/> does on the one empty line an emptied text holds, given its line break first,
    /// as <see cref="OnOneEmptyLine(Command, TextSnapshot, IVimView)"/> runs a command there.</summary>
    private VimResult OnOneEmptyLine(Func<TextSnapshot, SelectionSet, VimResult> act, TextSnapshot text, IVimView view)
    {
        EditPlan line = new([new TextEdit(0, 0, view.LineEnding)], SelectionSet.Single(0), EditKind.Joined);
        intoEmptied = true;
        VimResult result;
        try
        {
            result = act(text.Apply(line.Edits), line.Selections);
        }
        finally
        {
            intoEmptied = false;
        }
        typingIntoEmptied = Mode == VimMode.Insert;
        if (result.Edits is { Count: > 0 } made)
        {
            return result with { Edits = [line, .. made.Select(p => p with { Kind = EditKind.Joined })] };
        }
        return Mode is VimMode.Insert or VimMode.Replace ? result with { Edits = [line with { Selections = result.Selections ?? line.Selections }] } : result;
    }

    /// <summary>A result with what a search asks shown: its matches, and its wrap or failure in words.</summary>
    private VimResult Told(VimResult result, string? name)
    {
        if (name is "n" or "N" or "*" or "#" or "g*" or "g#" && result.Taken)
        {
            result = result with { Highlight = SearchShown() };
        }
        return searchSaid is { } said && result.Message is null ? result with { Message = said.Text, MessageIsError = said.Error } : result;
    }

    /// <summary>A change the text refuses (a file shown read-only), as Vim refuses a change where the buffer may not be
    /// changed: nothing changed, kept for . or written to a register, its words said, and the carets where given (at the
    /// start of what an operator took, as Vim leaves them).</summary>
    private static VimResult Refused(string why, SelectionSet? carets = null) => new(true, carets) { Message = why.Length > 0 ? why : null, MessageIsError = true };

    /// <summary>The put a p or P from + or * asked for, with the clipboard's text the editor read.</summary>
    public VimResult PutClipboard(string clipboard, TextSnapshot text, SelectionSet selections, IVimView? view = null)
    {
        if (clipboardPut is not { } put || clipboard.Length == 0)
        {
            clipboardPut = null;
            return VimResult.Held;
        }
        clipboardPut = null;
        view ??= PlainView;
        breakEnds = view.KeepsFinalLineBreak;
        return Put(VimRegisters.FromClipboard(clipboard), put.Before, put.Count, text, selections, view, put.CursorAfter);
    }

    /// <summary>Where a normal-mode caret may stand nearest the offset: on a character of its line, never past its last
    /// one, but at the start of an empty line, never on the empty line after a final line break that ends the text
    /// (<paramref name="breakEnds"/>, the file keeping one).</summary>
    public static int Settled(TextSnapshot text, int offset, bool breakEnds) => VimMotions.Settled(text, offset, breakEnds);

    /// <summary>Whether a file read or saved as this text keeps a final line break, as Vim sets 'eol' reading it: an
    /// empty file keeps one.</summary>
    public static bool KeepsFinalLineBreak(TextSnapshot saved) => saved.Length == 0 || saved[saved.Length - 1] == '\n';

    /// <summary>The carets as normal mode lets them stand, each on a character (<see cref="Settled(TextSnapshot, int, bool)"/>)
    /// with the column it aims for kept; a selection stays as it is, and the set itself where every caret already stands.</summary>
    public static SelectionSet Settled(TextSnapshot text, SelectionSet carets, bool breakEnds)
    {
        IReadOnlyList<Selection> items = carets.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsEmpty && Settled(text, items[i].Caret, breakEnds) != items[i].Caret)
            {
                return carets.With(s => s.IsEmpty ? s with { Anchor = Settled(text, s.Caret, breakEnds), Caret = Settled(text, s.Caret, breakEnds) } : s);
            }
        }
        return carets;
    }

    private static bool IsVimKey(string key) =>
        VimKeys.IsTyped(key) || key is VimKeys.Backspace or VimKeys.Enter or VimKeys.Delete or VimKeys.Tab or VimKeys.CtrlV or VimKeys.CtrlO or VimKeys.CtrlI;

    /// <summary>The count typed so far where nothing else is, 0 with none.</summary>
    private int CountOnly()
    {
        int count = 0;
        foreach (string key in pending)
        {
            if (key.Length != 1 || !char.IsAsciiDigit(key[0]))
            {
                return 0;
            }
            count = (int)Math.Min((long)count * 10 + (key[0] - '0'), int.MaxValue);
        }
        return count;
    }

    private VimResult Insert(TextSnapshot text, SelectionSet carets, Command command, string how)
    {
        Mode = VimMode.Insert;
        Begin(command, how, carets, text.Length);
        RememberBlock(text, carets);
        return new VimResult(true, carets);
    }

    /// <summary>An edit that ends in insert mode, the insert session beginning where it leaves the carets.</summary>
    private VimResult InsertAfter(TextSnapshot text, EditPlan plan, Command command, string how)
    {
        Mode = VimMode.Insert;
        Begin(command, how, plan.Selections, text.Length + plan.Edits.Sum(e => e.Text.Length - e.Length));
        RememberBlock(text.Apply(plan.Edits), plan.Selections);
        return new VimResult(true, Edits: [plan]);
    }

    /// <summary>A block's insert or change remembered as it begins (<see cref="blockStart"/>).</summary>
    private void RememberBlock(TextSnapshot text, SelectionSet carets)
    {
        int[] pads = blockPads ?? [];
        blockPads = null;
        blockStart = blockCorner is not null || blockChange
            ? (text, [.. carets.Items.Select(c => text.GetLineFromOffset(c.Caret)).Order()], pads)
            : null;
    }

    /// <summary>Each line of a block but its first as the session found it, where the session typed line breaks: the
    /// text with a line break goes on the first line alone, and a short line keeps no padding. Null where it typed none.</summary>
    private static EditPlan? OnFirstLineAlone(TextSnapshot text, SelectionSet carets, (TextSnapshot Text, int[] Lines, int[] Pads) start)
    {
        int count = start.Lines.Length;
        int added = text.LineCount - start.Text.LineCount;
        if (count < 2 || carets.Count != count || added <= 0 || added % count != 0)
        {
            return null;
        }
        // Each line a caret typed on became as many lines as every other did.
        int breaks = added / count;
        List<TextEdit> edits = [];
        for (int i = 1; i < count; i++)
        {
            LineSpan from = text.GetLine(start.Lines[i] + (breaks * i));
            LineSpan to = text.GetLine(start.Lines[i] + (breaks * i) + breaks);
            LineSpan was = start.Text.GetLine(start.Lines[i]);
            int pad = i < start.Pads.Length ? start.Pads[i] : 0;
            edits.Add(new TextEdit(from.Start, to.End - from.Start, start.Text.GetText(was.Start, was.Length - pad)));
        }
        return new EditPlan(edits, SelectionSet.Single(carets.Items.Min(c => c.Caret)), EditKind.Joined);
    }

    /// <summary>An insert session begun; only i, a, I, gI, A, o and O repeat what it types by their count.</summary>
    private void Begin(Command command, string how, SelectionSet carets, int length)
    {
        inserting = (command.Keys, command.Count, how, carets.Items[0].Caret, length, carets.Count, carets.Items.Min(c => c.Caret));
        lineBreaks = carets.Count == 1 ? [] : null;
    }

    /// <summary>Each line break Enter made in the insert session of one caret, where it put it and the indent it wrote after
    /// it; null where the session's text is kept as it stands (several carets, a break that wrote more, the host's edits).</summary>
    private List<(int At, string Indent)>? lineBreaks;

    /// <summary>Enter in an insert session, as the editor makes it: the blanks it takes after the carets leave the text
    /// shorter than what was typed, so the session's length goes down by them, and a break that writes only its indent is
    /// kept (<see cref="lineBreaks"/>).</summary>
    private void FollowLineBreak(TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (inserting is not { } session)
        {
            return;
        }
        EditPlan plan = TextEditing.NewLine(text, selections, view.LineEnding, view.Indent, view.Markup);
        inserting = session with { Length = session.Length - plan.Edits.Sum(e => e.Length) };
        if (lineBreaks is not null && plan is { Split: [], Edits: [{ } edit] } && edit.Text.StartsWith(view.LineEnding, StringComparison.Ordinal)
            && edit.Text.AsSpan(view.LineEnding.Length).Trim(" \t").IsEmpty && (lineBreaks.Count == 0 || lineBreaks[^1].At < edit.Start))
        {
            lineBreaks.Add((edit.Start, edit.Text[view.LineEnding.Length..]));
            return;
        }
        lineBreaks = null;
    }

    /// <summary>Edits the host made in an insert session of one caret at <paramref name="caret"/> (its formatting of what was
    /// typed, of a block left), sorted and apart in the text before them: what they change before the session's start moves
    /// it, and after the caret is no typing, so . types again what the session's keys did; one taking in that start makes
    /// what it writes typed from there.</summary>
    public void FollowEdits(IReadOnlyList<TextEdit> edits, int caret)
    {
        if (inserting is not { Carets: 1 } session)
        {
            return;
        }
        lineBreaks = null;
        int first = session.First;
        int length = session.Length;
        int moved = 0;
        foreach (TextEdit edit in edits)
        {
            if (edit.End <= session.First)
            {
                moved += edit.Text.Length - edit.Length;
            }
            else if (edit.Start >= caret)
            {
                length += edit.Text.Length - edit.Length;
            }
            else if (edit.Start < session.First)
            {
                length -= session.First - edit.Start;
                first = edit.Start;
            }
        }
        inserting = (session.Keys, session.Count, session.How, first + moved, length + moved, session.Carets, first + moved);
    }

    /// <summary>Escape in insert mode: the carets step back onto the text, a line the session indented and left blank loses
    /// its indent, a count repeats what it typed, and what it typed is kept for . to type again.</summary>
    private VimResult LeaveInsert(TextSnapshot text, SelectionSet selections, IVimView view)
    {
        Mode = VimMode.Normal;
        (IReadOnlyList<string> Keys, int Count, string How, int First, int Length, int Carets, int Top)? session = inserting;
        inserting = null;
        string? typed = null;
        if (session is { } s && selections.Count == s.Carets && text.Length >= s.Length && (text.Length - s.Length) % s.Carets == 0
            && s.First + ((text.Length - s.Length) / s.Carets) <= text.Length)
        {
            typed = text.GetText(s.First, (text.Length - s.Length) / s.Carets).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        else if (session is { } trimmed && selections.Count == trimmed.Carets && selections.Items.Min(c => c.Caret) is int top && top >= trimmed.Top)
        {
            // A line break that took the blanks after it makes the lengths differ: what the first caret typed stands before it.
            typed = text.GetText(trimmed.Top, top - trimmed.Top).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        List<(int At, string Indent)>? breaks = session is { Carets: 1 } one && typed is not null ? BreaksIn(text, one.First, typed) : null;
        List<EditPlan> plans = [];
        if (blockStart is { } start && text.LineCount > start.Text.LineCount)
        {
            // A block's session that typed a line break leaves its cursor where typing ended.
            if (OnFirstLineAlone(text, selections, start) is { } alone)
            {
                plans.Add(alone);
                (text, selections) = (text.Apply(alone.Edits), alone.Selections);
            }
            (blockCorner, blockChange) = (null, false);
        }
        if (blockStart is { } padded && typed is "" && selections.Count == padded.Pads.Length && padded.Pads.Skip(1).Any(pad => pad > 0))
        {
            // A block's A that typed nothing pads only its first line.
            List<TextEdit> unpadded = [.. selections.Items.Skip(1).Select((c, i) => (c.Caret, Pad: padded.Pads[i + 1])).Where(p => p.Pad > 0)
                .Select(p => new TextEdit(p.Caret - p.Pad, p.Pad, string.Empty))];
            EditPlan kept = new(unpadded, TextEditing.MapThrough(selections, unpadded), EditKind.Joined);
            plans.Add(kept);
            (text, selections) = (text.Apply(kept.Edits), kept.Selections);
        }
        blockStart = null;
        if (typingIntoEmptied && typed is "" && text.Length == view.LineEnding.Length && text.GetText(0, text.Length) == view.LineEnding)
        {
            // Typing nothing into an emptied text leaves it emptied.
            EditPlan emptied = new([new TextEdit(0, text.Length, string.Empty)], SelectionSet.Single(0), EditKind.Joined);
            plans.Add(emptied);
            (text, selections) = (text.Apply(emptied.Edits), emptied.Selections);
        }
        typingIntoEmptied = false;
        // A line the session indented and left blank loses its indent, as Vim's autoindent takes back.
        bool indents = session is { How: "o" or "O" or "cc" } && typed == string.Empty || typed is not null && BlankAfterBreak(typed);
        int[]? indentAims = null;
        if (indents && Unindented(text, selections) is { } unindent)
        {
            // j and k go on aiming for the column the indent took the caret to.
            indentAims = [.. selections.Items.Select(c => TextNavigation.VisualColumn(text, c.Caret, view.Indent.TabWidth))];
            plans.Add(unindent);
            (text, selections) = (text.Apply(unindent.Edits), unindent.Selections);
            typed = typed is null ? null : TrimLastIndent(typed);
        }
        if (session is { Count: > 1, How: not ("c" or "cc") } counted && typed is not null && (typed.Length > 0 || counted.How is "o" or "O"))
        {
            (string Text, int[] Breaks)? entered = breaks is { Count: > 0 } ? WithoutIndents(typed, breaks) : null;
            for (int i = 1; i < Math.Min(counted.Count, Math.Max(2, MostRepeatedText / Math.Max(1, typed.Length))); i++)
            {
                if (counted.How is "o" or "O")
                {
                    // Each line again as o opens it, indented only where text goes on it.
                    EditPlan line = typed.Length == 0
                        ? TextEditing.Insert(text, selections.With(c => Selection.At(VimMotions.LineOf(text, c.Caret).End)), view.LineEnding, EditKind.Joined)
                        : TextEditing.StartNewLine(text, selections, view.LineEnding, view.Indent, above: false, view.Markup) with { Kind = EditKind.Joined };
                    plans.Add(line);
                    (text, selections) = (text.Apply(line.Edits), line.Selections);
                }
                if (typed.Length > 0 && entered is ({ } made, { } at))
                {
                    // Each line break Enter made is made again as Enter makes it, as . does.
                    int from = 0;
                    foreach (int place in at.Append(made.Length))
                    {
                        if (place > from)
                        {
                            EditPlan part = TextEditing.Insert(text, selections, made[from..place], EditKind.Joined);
                            plans.Add(part);
                            (text, selections) = (text.Apply(part.Edits), part.Selections);
                        }
                        if (place < made.Length)
                        {
                            EditPlan broken = TextEditing.NewLine(text, selections, view.LineEnding, view.Indent, view.Markup) with { Kind = EditKind.Joined };
                            plans.Add(broken);
                            (text, selections) = (text.Apply(broken.Edits), broken.Selections);
                        }
                        from = place + 1;
                    }
                }
                else if (typed.Length > 0)
                {
                    EditPlan again = TextEditing.Insert(text, selections, LineEndings.In(typed, view.LineEnding), EditKind.Joined);
                    plans.Add(again);
                    (text, selections) = (text.Apply(again.Edits), again.Selections);
                }
            }
            if (entered is not null && indents && Unindented(text, selections) is { } blankAgain)
            {
                // The last line the count's Enter indented and left blank loses its indent too.
                plans.Add(blankAgain);
                (text, selections) = (text.Apply(blankAgain.Edits), blankAgain.Selections);
            }
        }
        if (session is { } begun && !replaying)
        {
            lastChange = typed is not null && breaks is { Count: > 0 } && WithoutIndents(typed, breaks) is ({ } made, { } at)
                ? new ChangeMade(begun.Keys, begun.Count, made, changeExtent) { Breaks = at }
                : new ChangeMade(begun.Keys, begun.Count, typed ?? string.Empty, changeExtent);
        }
        changeExtent = null;
        SelectionSet stepped = selections.With(c => Selection.At(Settled(text, Math.Max(VimMotions.StepBack(text, c.Caret), VimMotions.LineOf(text, c.Caret).Start), breakEnds)));
        // A block's insert that typed nothing steps back from its first line's place, as any insert's Escape does.
        SelectionSet back = blockCorner is { } corner && corner <= text.Length
            ? typed is "" ? SelectionSet.Single(stepped.Items.MinBy(c => c.Caret)) : SelectionSet.Single(Settled(text, corner, breakEnds))
            : stepped;
        if (blockChange)
        {
            // A block's change leaves its one cursor where typing ended on the first line it changed.
            back = SelectionSet.Single(back.Items.MinBy(c => c.Caret));
        }
        (blockCorner, blockChange) = (null, false);
        if (indentAims is { } columns && plans.Count == 1 && columns.Length == back.Count)
        {
            (wantedFor, wanted) = (back, columns);
        }
        if (plans.Count == 0)
        {
            return new VimResult(true, back);
        }
        plans[^1] = plans[^1] with { Selections = back };
        return new VimResult(true, Edits: [.. plans.Select(p => p with { Kind = EditKind.Joined })]);
    }

    /// <summary>The blanks of each line a caret ends on at its end, where the line holds nothing else; null with none.</summary>
    private static EditPlan? Unindented(TextSnapshot text, SelectionSet carets)
    {
        List<TextEdit> edits = [];
        foreach (Selection caret in carets.Items)
        {
            LineSpan line = VimMotions.LineOf(text, caret.Caret);
            if (line.Length > 0 && caret.Caret == line.End && TextNavigation.IndentEnd(text, line) == line.End && (edits.Count == 0 || edits[^1].Start != line.Start))
            {
                edits.Add(new TextEdit(line.Start, line.Length, string.Empty));
            }
        }
        return edits.Count == 0 ? null : new EditPlan(edits, TextEditing.MapThrough(carets, edits), EditKind.Joined);
    }

    /// <summary>The session's line breaks (<see cref="lineBreaks"/>) as places in what it typed, read from the text at
    /// <paramref name="first"/>; null where the text no longer holds a break at each, in order.</summary>
    private List<(int At, string Indent)>? BreaksIn(TextSnapshot text, int first, string typed)
    {
        if (lineBreaks is not { Count: > 0 } kept)
        {
            return null;
        }
        List<(int At, string Indent)> found = [];
        foreach ((int at, string indent) in kept)
        {
            // What was typed reads each "\r\n" as one "\n".
            int crlf = at > first ? text.GetText(first, at - first).Split("\r\n").Length - 1 : 0;
            int place = at - first - crlf;
            if (place < 0 || place >= typed.Length || typed[place] != '\n' || found.Count > 0 && found[^1].At >= place)
            {
                return null;
            }
            found.Add((place, indent));
        }
        return found;
    }

    /// <summary>What a session typed with the indent each of its line breaks wrote taken out, and where those breaks stand in
    /// it; null where one's indent is no longer as written (typed over, taken back by hand), which . then types as it stands.
    /// An indent the session's end took from a blank last line is taken out with it.</summary>
    private static (string Text, int[] Breaks)? WithoutIndents(string typed, List<(int At, string Indent)> breaks)
    {
        System.Text.StringBuilder made = new(typed.Length);
        List<int> places = [];
        int from = 0;
        foreach ((int at, string indent) in breaks)
        {
            made.Append(typed, from, at - from);
            places.Add(made.Length);
            made.Append('\n');
            from = at + 1;
            int lineEnd = typed.IndexOf('\n', from) is int next and >= 0 ? next : typed.Length;
            if (typed.AsSpan(from).StartsWith(indent, StringComparison.Ordinal))
            {
                from += indent.Length;
            }
            else if (lineEnd == typed.Length && typed.AsSpan(from).Trim(" \t").IsEmpty && indent.StartsWith(typed[from..], StringComparison.Ordinal))
            {
                from = typed.Length;
            }
            else
            {
                return null;
            }
        }
        made.Append(typed, from, typed.Length - from);
        return (made.ToString(), [.. places]);
    }

    /// <summary>Whether what was typed ends with a line break and blanks alone after it.</summary>
    private static bool BlankAfterBreak(string typed)
    {
        int end = typed.LastIndexOf('\n');
        return end >= 0 && typed.AsSpan(end + 1).Trim(" \t").IsEmpty;
    }

    private static string TrimLastIndent(string typed)
    {
        int end = typed.Length;
        while (end > 0 && typed[end - 1] is ' ' or '\t')
        {
            end--;
        }
        return end == 0 || typed[end - 1] == '\n' ? typed[..end] : typed;
    }

    /// <summary>. : the last change again, with the count given in place of its own, as one step of undo.</summary>
    private VimResult Again(int count, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (lastChange is not { } change)
        {
            return VimResult.Held;
        }
        List<string> keys = [.. change.Keys];
        if (keys.Count >= 2 && keys[0] == "\"" && keys[1] is [>= '1' and <= '8'] digit)
        {
            // A put from a numbered register puts the next one up each time it is repeated.
            keys[1] = ((char)(digit[0] + 1)).ToString();
            lastChange = change with { Keys = keys };
        }
        int times = count > 0 ? count : change.Count;
        ChangeMade kept = lastChange!;
        if (count > 0 && change.Visual is null)
        {
            // A count given replaces the change's own for each . after it too, where the change is made.
            lastChange = kept with { Count = count };
        }
        List<EditPlan> plans = [];
        SelectionSet carets = selections;
        VimRequest? asked = null;
        IReadOnlyList<string>? pressedAfter = null;
        replaying = true;
        replayingCounted = count > 0;
        try
        {
            if (change.Visual is { Again: not InsertAgain.None } again)
            {
                // I or A over characters or lines types again from the carets, taking no stretch.
                Follow(InsertedAgain(text, carets, again, new Command(keys, 0, null, null, "visual", null)));
                keys = [];
                times = 0;
            }
            else if (change.Visual is { } extent)
            {
                // A visual change takes as much again from each caret, its count its own.
                times = extent.Mode == VimMode.VisualBlock || keys.Contains(">") || keys.Contains("<") ? change.Count : 0;
                Mode = extent.Mode;
                IReadOnlyList<Selection> from = extent.Mode == VimMode.VisualBlock ? [carets.Primary] : carets.Items;
                regions = [.. from.Select(c => FromExtent(text, c.Caret, extent, view))];
                visualPrimary = extent.Mode == VimMode.VisualBlock ? 0 : carets.PrimaryIndex;
                visualAims = [.. regions.Select(_ => -1)];
                carets = ShowVisual(text, view).Selections!;
            }
            List<string> typed = times > 0 ? [.. times.ToString(System.Globalization.CultureInfo.InvariantCulture).Select(c => c.ToString())] : [];
            actedOnNothing = false;
            foreach (string key in typed.Concat(keys))
            {
                Follow(Press(key, text, carets, view));
            }
            // A change that fails keeps its own count; one whose operator finds nothing to change is made all the same.
            if (plans.Count == 0 && !actedOnNothing && Mode == VimMode.Normal && change.Visual is null)
            {
                // A change failing again keeps its count, and what it typed is pressed as normal mode's keys, as Vim's are.
                lastChange = kept with { JoinedNothing = lastChange?.JoinedNothing ?? kept.JoinedNothing };
                pressedAfter = change.Inserted.Length > 0 ? [.. VimKeys.Pressed(change.Inserted), VimKeys.Escape] : null;
            }
            if (Mode == VimMode.Replace)
            {
                foreach (string over in VimKeys.Pressed(change.Inserted))
                {
                    Follow(Press(over, text, carets, view));
                }
                Follow(Press(VimKeys.Escape, text, carets, view));
            }
            if (Mode == VimMode.Insert)
            {
                int from = 0;
                foreach (int at in change.Breaks)
                {
                    // Each line break Enter made is made again as Enter makes it here, its blanks taken and its indent this line's.
                    Type(change.Inserted[from..at]);
                    FollowLineBreak(text, carets, view);
                    Follow(new VimResult(true, Edits: [TextEditing.NewLine(text, carets, view.LineEnding, view.Indent, view.Markup) with { Kind = EditKind.Joined }]));
                    from = at + 1;
                }
                Type(change.Inserted[from..]);
                Follow(LeaveInsert(text, carets, view));
            }
        }
        finally
        {
            replaying = false;
            replayingCounted = false;
            blockAgain = null;
            pending.Clear();
        }
        if (change.Visual is { Mode: VimMode.VisualLine, Again: InsertAgain.None } && change.Keys.Count > 0 && change.Keys[^1] is "u" or "U" or "~")
        {
            // A change of case over lines made again leaves the carets where it began, as Vim's does.
            carets = selections;
        }
        if (plans.Count == 0)
        {
            return new VimResult(true, carets) { Request = asked, Replay = pressedAfter };
        }
        plans[^1] = plans[^1] with { Selections = carets };
        return new VimResult(true, Edits: [.. plans.Select(p => p with { Kind = EditKind.Joined })]) { Request = asked };

        void Type(string typed)
        {
            if (typed.Length > 0)
            {
                Follow(new VimResult(true, Edits: [TextEditing.Insert(text, carets, LineEndings.In(typed, view.LineEnding), EditKind.Joined)]));
            }
        }

        void Follow(VimResult result)
        {
            asked ??= result.Request;
            foreach (EditPlan plan in result.Edits ?? [])
            {
                plans.Add(plan);
                text = text.Apply(plan.Edits);
                carets = plan.Selections;
            }
            if (result.Selections is { } moved)
            {
                carets = moved;
            }
        }
    }
}
