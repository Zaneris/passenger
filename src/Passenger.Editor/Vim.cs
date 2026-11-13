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
    /// <summary>Escape in Vim's notation.</summary>
    public const string Escape = "<Esc>";

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
    private (IReadOnlyList<string> Keys, int Count, string How, int First, int Length, int Carets)? inserting;

    /// <summary>A put from + or * waiting for the clipboard's text.</summary>
    private (bool Before, int Count, bool CursorAfter)? clipboardPut;

    /// <summary>Set while . makes the last change again, which records nothing.</summary>
    private bool replaying;

    public Vim()
        : this(new VimRegisters())
    {
    }

    public VimMode Mode { get; private set; } = VimMode.Normal;

    /// <summary>The count and keys typed toward a command not yet complete ("2d"), for the status bar.</summary>
    public string Pending => string.Concat(pending);

    /// <summary>The registers yanks, deletes and puts use: the set the editor's host gives, shared by the editors it
    /// holds, and a set of the Vim's own where none is given.</summary>
    public VimRegisters Registers { get; set; } = registers;

    /// <summary>Set while the host presses a macro's or :normal's keys, which q records none of.</summary>
    public bool Playing { get; set; }

    public VimResult Press(string key, TextSnapshot text, SelectionSet selections, IVimView? view = null)
    {
        view ??= PlainView;
        if (!replaying && !Playing && Recorded(key))
        {
            return VimResult.Held;
        }
        if (commandLine is not null)
        {
            return PressCommandLine(key, text, selections, view);
        }
        if (key.StartsWith(GoKey, StringComparison.Ordinal) && int.TryParse(key.AsSpan(GoKey.Length), System.Globalization.CultureInfo.InvariantCulture, out int goLine))
        {
            Mode = VimMode.Normal;
            pending.Clear();
            return new VimResult(true, SelectionSet.Single(text.GetLineStart(Math.Clamp(VimMotions.LastLine(text) - goLine, 0, VimMotions.LastLine(text)))));
        }
        return Take(key, text, selections, view);
    }

    /// <summary>A key in any mode but the command line's.</summary>
    private VimResult Take(string key, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (Mode == VimMode.Replace)
        {
            return PressReplace(key, text, selections, view);
        }
        if (Mode == VimMode.Insert)
        {
            return key == Escape ? LeaveInsert(text, selections, view) : VimResult.Left;
        }
        if (IsVisual(Mode))
        {
            return PressVisual(key, text, selections, view);
        }
        if (key == Escape)
        {
            bool waiting = pending.Count > 0;
            pending.Clear();
            return waiting ? VimResult.Held : VimResult.Left;
        }
        if (key == "<C-r>")
        {
            int redo = CountOnly();
            pending.Clear();
            return new VimResult(true, Action: EditorActions.Redo, Repeat: Math.Clamp(redo, 1, MostRepeated));
        }
        if (key == "<Del>" && pending.Count > 0)
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
        pending.Add(key == "<Del>" ? "x" : key);
        Command? command = Parse(pending, out bool incomplete);
        if (incomplete)
        {
            return VimResult.Held;
        }
        pending.Clear();
        searchSaid = null;
        // A command Vim mode doesn't know is swallowed whole, so its keys never type into the text.
        VimResult result = command is null ? VimResult.Held : Run(command, text, selections, view);
        return Told(result, command?.Name);
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

    /// <summary>The put a p or P from + or * asked for, with the clipboard's text the editor read.</summary>
    public VimResult PutClipboard(string clipboard, TextSnapshot text, SelectionSet selections, IVimView? view = null)
    {
        if (clipboardPut is not { } put || clipboard.Length == 0)
        {
            clipboardPut = null;
            return VimResult.Held;
        }
        clipboardPut = null;
        return Put(VimRegisters.FromClipboard(clipboard), put.Before, put.Count, text, selections, view ?? PlainView, put.CursorAfter);
    }

    /// <summary>Where a normal-mode caret may stand nearest the offset: on a character of its line, never past its last
    /// one, but at the start of an empty line.</summary>
    public static int Settled(TextSnapshot text, int offset) => VimMotions.Settled(text, offset);

    /// <summary>The carets as normal mode lets them stand, each on a character (<see cref="Settled(TextSnapshot, int)"/>)
    /// with the column it aims for kept; a selection stays as it is, and the set itself where every caret already stands.</summary>
    public static SelectionSet Settled(TextSnapshot text, SelectionSet carets)
    {
        IReadOnlyList<Selection> items = carets.Items;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsEmpty && Settled(text, items[i].Caret) != items[i].Caret)
            {
                return carets.With(s => s.IsEmpty ? s with { Anchor = Settled(text, s.Caret), Caret = Settled(text, s.Caret) } : s);
            }
        }
        return carets;
    }

    private static bool IsVimKey(string key) =>
        key.Length == 1 || (key.Length == 2 && char.IsSurrogatePair(key[0], key[1])) || key is "<BS>" or "<CR>" or "<Del>" or "<Tab>" or "<C-v>" or "<C-o>" or "<C-i>";

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
        return new VimResult(true, carets);
    }

    /// <summary>An edit that ends in insert mode, the insert session beginning where it leaves the carets.</summary>
    private VimResult InsertAfter(TextSnapshot text, EditPlan plan, Command command, string how)
    {
        Mode = VimMode.Insert;
        Begin(command, how, plan.Selections, text.Length + plan.Edits.Sum(e => e.Text.Length - e.Length));
        return new VimResult(true, Edits: [plan]);
    }

    /// <summary>An insert session begun; only i, a, I, A, o and O repeat what it types by their count.</summary>
    private void Begin(Command command, string how, SelectionSet carets, int length) =>
        inserting = (command.Keys, command.Count, how, carets.Items[0].Caret, length, carets.Count);

    /// <summary>Escape in insert mode: the carets step back onto the text, a line the session indented and left blank loses
    /// its indent, a count repeats what it typed, and what it typed is kept for . to type again.</summary>
    private VimResult LeaveInsert(TextSnapshot text, SelectionSet selections, IVimView view)
    {
        Mode = VimMode.Normal;
        (IReadOnlyList<string> Keys, int Count, string How, int First, int Length, int Carets)? session = inserting;
        inserting = null;
        string? typed = null;
        if (session is { } s && selections.Count == s.Carets && text.Length >= s.Length && (text.Length - s.Length) % s.Carets == 0
            && s.First + ((text.Length - s.Length) / s.Carets) <= text.Length)
        {
            typed = text.GetText(s.First, (text.Length - s.Length) / s.Carets).Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        List<EditPlan> plans = [];
        // A line the session indented and left blank loses its indent, as Vim's autoindent takes back.
        bool indents = session is { How: "o" or "O" or "cc" } && typed == string.Empty || typed is not null && BlankAfterBreak(typed);
        if (indents && Unindented(text, selections) is { } unindent)
        {
            plans.Add(unindent);
            (text, selections) = (text.Apply(unindent.Edits), unindent.Selections);
            typed = typed is null ? null : TrimLastIndent(typed);
        }
        if (session is { Count: > 1, How: not ("c" or "cc") } counted && typed is not null && (typed.Length > 0 || counted.How is "o" or "O"))
        {
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
                if (typed.Length > 0)
                {
                    EditPlan again = TextEditing.Insert(text, selections, LineEndings.In(typed, view.LineEnding), EditKind.Joined);
                    plans.Add(again);
                    (text, selections) = (text.Apply(again.Edits), again.Selections);
                }
            }
        }
        if (session is { } begun && !replaying)
        {
            lastChange = new ChangeMade(begun.Keys, begun.Count, typed ?? string.Empty, changeExtent);
        }
        changeExtent = null;
        SelectionSet back = blockCorner is { } corner && corner <= text.Length
            ? SelectionSet.Single(Settled(text, corner))
            : selections.With(c => Selection.At(Settled(text, Math.Max(VimMotions.StepBack(text, c.Caret), VimMotions.LineOf(text, c.Caret).Start))));
        blockCorner = null;
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
        List<EditPlan> plans = [];
        SelectionSet carets = selections;
        replaying = true;
        try
        {
            if (change.Visual is { } extent)
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
            foreach (string key in typed.Concat(keys))
            {
                Follow(Press(key, text, carets, view));
            }
            if (Mode == VimMode.Replace)
            {
                foreach (string over in KeysOf(change.Inserted))
                {
                    Follow(Press(over, text, carets, view));
                }
                Follow(Press(Escape, text, carets, view));
            }
            if (Mode == VimMode.Insert)
            {
                if (change.Inserted.Length > 0)
                {
                    Follow(new VimResult(true, Edits: [TextEditing.Insert(text, carets, LineEndings.In(change.Inserted, view.LineEnding), EditKind.Joined)]));
                }
                Follow(LeaveInsert(text, carets, view));
            }
        }
        finally
        {
            replaying = false;
            pending.Clear();
        }
        if (plans.Count == 0)
        {
            return new VimResult(true, carets);
        }
        plans[^1] = plans[^1] with { Selections = carets };
        return new VimResult(true, Edits: [.. plans.Select(p => p with { Kind = EditKind.Joined })]);

        void Follow(VimResult result)
        {
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
