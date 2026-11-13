using Passenger.Text;

namespace Passenger.Editor;

public enum VimMode
{
    Normal,
    Insert,
}

/// <summary>What a key did in Vim mode: taken, or left to the keymap; and what the editor is to do with it: carets to set,
/// an edit to apply as one step of undo, or one of <see cref="EditorActions"/> to run <see cref="Repeat"/> times.</summary>
public sealed record VimResult(bool Taken, SelectionSet? Selections = null, EditPlan? Edit = null, string? Action = null, int Repeat = 1)
{
    public static readonly VimResult Left = new(false);

    public static readonly VimResult Held = new(true);
}

/// <summary>Vim's normal and insert modes as a function from keys to what the editor does, knowing nothing of the window.
/// Keys come in Vim's notation ("w", "G", "&lt;Esc&gt;", "&lt;C-r&gt;"); every caret moves alike; a key normal mode
/// doesn't take (a shortcut with Cmd, an arrow) is left to the keymap, so the IDE's own keys keep working.</summary>
public sealed class Vim
{
    private const string Escape = "<Esc>";

    // ponytail: a count of undos or redos past this is cut to it, each queued apart in the window; one deep undo if that's too few.
    private const int MostRepeated = 1000;

    private string pending = string.Empty;

    /// <summary>The columns j and k aim for, one per caret of <see cref="wantedFor"/>, so a short line passed over
    /// doesn't pull the caret left for good.</summary>
    private int[] wanted = [];

    private SelectionSet? wantedFor;

    public VimMode Mode { get; private set; } = VimMode.Normal;

    /// <summary>The count and keys typed toward a command not yet complete ("2d"), for the status bar.</summary>
    public string Pending => pending;

    public VimResult Press(string key, TextSnapshot text, SelectionSet selections)
    {
        if (Mode == VimMode.Insert)
        {
            if (key != Escape)
            {
                return VimResult.Left;
            }
            Mode = VimMode.Normal;
            return new VimResult(true, selections.With(s => Selection.At(Settled(text, StepBack(text, s.Caret)))));
        }
        if (key == Escape)
        {
            bool waiting = pending.Length > 0;
            pending = string.Empty;
            return waiting ? VimResult.Held : VimResult.Left;
        }
        if (key == "<C-r>")
        {
            int redo = Count(pending, out _);
            pending = string.Empty;
            return new VimResult(true, Action: EditorActions.Redo, Repeat: Math.Clamp(redo, 1, MostRepeated));
        }
        if (key.Length != 1 && key is not ("<BS>" or "<CR>" or "<Del>" or "<Tab>"))
        {
            pending = string.Empty;
            return VimResult.Left;
        }
        pending += key;
        int count = Count(pending, out string command);
        if (command is "" or "g" or "d")
        {
            return VimResult.Held;
        }
        string typed = pending;
        pending = string.Empty;
        int n = Math.Max(count, 1);
        switch (command)
        {
            case "h":
                return Move(text, selections, c => Repeated(n, c, p => Math.Max(StepBack(text, p), LineOf(text, c).Start)));
            case "<BS>":
                // Backspace crosses into the line before, as Vim's whichwrap has it by default.
                return Move(text, selections, c => Repeated(n, c, p => p > LineOf(text, p).Start ? StepBack(text, p) : Settled(text, StepBack(text, p))));
            case "<CR>":
                return Move(text, selections, c =>
                {
                    int from = text.GetLineFromOffset(c);
                    int to = (int)Math.Min((long)from + n, LastLine(text));
                    return to == from ? c : FirstNonBlank(text, to);
                });
            case "<Del>" when count > 0:
                // Delete after a count takes back the count's last digit, as Vim does.
                pending = typed[..(typed.Length - command.Length - 1)];
                return VimResult.Held;
            case "<Del>":
                return DeleteCharacters(text, selections, 1);
            case "l":
                return Move(text, selections, c => Repeated(n, c, p => Math.Min(TextNavigation.NextGrapheme(text, p), LineOf(text, c).End)));
            case "j":
                return Vertically(text, selections, n);
            case "k":
                return Vertically(text, selections, -n);
            case "0":
                return Move(text, selections, c => LineOf(text, c).Start);
            case "^":
                return Move(text, selections, c => TextNavigation.IndentEnd(text, LineOf(text, c)));
            case "$":
                return ToLineEnd(text, selections, n);
            case "w":
                return Move(text, selections, c => Repeated(n, c, p => WordStart(text, p)));
            case "b":
                return Move(text, selections, c => Repeated(n, c, p => WordBack(text, p)));
            case "e":
                return Move(text, selections, c => Repeated(n, c, p => WordEnd(text, p)));
            case "gg":
                return Move(text, selections, _ => FirstNonBlank(text, count == 0 ? 0 : Math.Min(count - 1, LastLine(text))));
            case "G":
                return Move(text, selections, _ => FirstNonBlank(text, count == 0 ? LastLine(text) : Math.Min(count - 1, LastLine(text))));
            case "x":
                return DeleteCharacters(text, selections, n);
            case "dd":
                return DeleteLines(text, selections, n);
            case "i":
                return Insert(selections);
            case "a":
                return Insert(selections.With(s => Selection.At(Math.Min(TextNavigation.NextGrapheme(text, s.Caret), LineOf(text, s.Caret).End))));
            case "I":
                return Insert(selections.With(s => Selection.At(TextNavigation.IndentEnd(text, LineOf(text, s.Caret)))));
            case "A":
                return Insert(selections.With(s => Selection.At(LineOf(text, s.Caret).End)));
            case "o":
                Mode = VimMode.Insert;
                return new VimResult(true, Action: EditorActions.StartNewLine);
            case "O":
                Mode = VimMode.Insert;
                return new VimResult(true, Action: EditorActions.StartNewLineBefore);
            case "u":
                return new VimResult(true, Action: EditorActions.Undo, Repeat: Math.Min(n, MostRepeated));
            default:
                // A command Vim mode doesn't know yet is swallowed whole, so its keys never type into the text.
                return VimResult.Held;
        }
    }

    /// <summary>Where a normal-mode caret may stand nearest the offset: on a character of its line, never past its last
    /// one, but at the start of an empty line.</summary>
    public static int Settled(TextSnapshot text, int offset)
    {
        LineSpan line = LineOf(text, offset);
        return line.Length == 0 || offset < line.End ? Math.Max(offset, line.Start) : Math.Max(StepBack(text, line.End), line.Start);
    }

    /// <summary>The count typed before a command and the command after it; 0 when none was typed (a leading 0 is the
    /// command), the largest int when it overflows.</summary>
    private static int Count(string typed, out string command)
    {
        int digits = 0;
        while (digits < typed.Length && char.IsAsciiDigit(typed[digits]) && !(digits == 0 && typed[0] == '0'))
        {
            digits++;
        }
        command = typed[digits..];
        return digits == 0 ? 0 : int.TryParse(typed.AsSpan(0, digits), out int count) ? count : int.MaxValue;
    }

    private VimResult Insert(SelectionSet selections)
    {
        Mode = VimMode.Insert;
        return new VimResult(true, selections);
    }

    private static VimResult Move(TextSnapshot text, SelectionSet selections, Func<int, int> to) =>
        new(true, selections.With(s => Selection.At(Settled(text, to(s.Caret)))));

    private VimResult Vertically(TextSnapshot text, SelectionSet selections, int lines)
    {
        int[] columns = wantedFor is { } known && known.SameAs(selections)
            ? wanted
            : selections.Items.Select(s => s.Caret - LineOf(text, s.Caret).Start).ToArray();
        List<Selection> moved = [];
        for (int i = 0; i < selections.Count; i++)
        {
            int from = text.GetLineFromOffset(selections.Items[i].Caret);
            int to = (int)Math.Clamp((long)from + lines, 0, LastLine(text));
            LineSpan line = text.GetLine(to);
            // ponytail: columns count UTF-16 chars, not cells, so a tab or wide character above shifts the column; count cells when it matters.
            moved.Add(Selection.At(Settled(text, line.Start + Math.Min(columns[i], line.Length))));
        }
        return Aimed(SelectionSet.Create(moved, selections.PrimaryIndex), columns);
    }

    private VimResult ToLineEnd(TextSnapshot text, SelectionSet selections, int n)
    {
        SelectionSet moved = selections.With(s =>
        {
            int line = (int)Math.Min((long)text.GetLineFromOffset(s.Caret) + n - 1, LastLine(text));
            return Selection.At(Settled(text, text.GetLine(line).End));
        });
        return Aimed(moved, [.. Enumerable.Repeat(int.MaxValue, moved.Count)]);
    }

    private VimResult Aimed(SelectionSet moved, int[] columns)
    {
        bool kept = moved.Count == columns.Length;
        wantedFor = kept ? moved : null;
        wanted = kept ? columns : [];
        return new VimResult(true, moved);
    }

    private static VimResult DeleteCharacters(TextSnapshot text, SelectionSet selections, int n)
    {
        SelectionSet spans = selections.With(s =>
        {
            int end = LineOf(text, s.Caret).End;
            return new Selection(s.Caret, Repeated(n, s.Caret, p => Math.Min(TextNavigation.NextGrapheme(text, p), end)));
        });
        return spans.Items.All(s => s.IsEmpty) || TextEditing.Delete(text, spans) is not { } plan
            ? VimResult.Held
            : Settle(text, plan with { Kind = EditKind.Other }, p => p);
    }

    private static VimResult DeleteLines(TextSnapshot text, SelectionSet selections, int n)
    {
        SelectionSet spans = selections.With(s =>
        {
            int first = text.GetLineFromOffset(s.Caret);
            int last = (int)Math.Min((long)first + n - 1, LastLine(text));
            return new Selection(text.GetLine(first).Start, text.GetLine(last).End);
        });
        EditPlan plan = TextEditing.DeleteLines(text, spans);
        return Settle(text, plan, null);
    }

    /// <summary>The plan with its carets settled where normal mode lets them stand in the text after it: each where
    /// <paramref name="at"/> puts it, or at its line's first non-blank when null.</summary>
    private static VimResult Settle(TextSnapshot text, EditPlan plan, Func<int, int>? at)
    {
        TextSnapshot after = text.Apply(plan.Edits);
        SelectionSet settled = plan.Selections.With(s => Selection.At(at is null
            ? FirstNonBlank(after, Math.Min(after.GetLineFromOffset(s.Caret), LastLine(after)))
            : Settled(after, at(s.Caret))));
        return new VimResult(true, Edit: plan with { Selections = settled });
    }

    /// <summary>A step taken <paramref name="n"/> times from <paramref name="from"/>, ending early once it stops moving,
    /// so a count of millions costs no more than the text is long.</summary>
    private static int Repeated(int n, int from, Func<int, int> step)
    {
        int at = from;
        for (int i = 0; i < n; i++)
        {
            int next = step(at);
            if (next == at)
            {
                break;
            }
            at = next;
        }
        return at;
    }

    private static int StepBack(TextSnapshot text, int offset) => offset == 0 ? 0 : TextNavigation.PreviousGrapheme(text, offset);

    private static LineSpan LineOf(TextSnapshot text, int offset) => text.GetLine(text.GetLineFromOffset(offset));

    private static int FirstNonBlank(TextSnapshot text, int line) => Settled(text, TextNavigation.IndentEnd(text, text.GetLine(line)));

    /// <summary>The last line Vim would count: the empty one after a final line break is the file's end, not a line.</summary>
    private static int LastLine(TextSnapshot text)
    {
        int last = text.LineCount - 1;
        return last > 0 && text.GetLine(last).Length == 0 ? last - 1 : last;
    }

    /// <summary>A character's kind for Vim's word motions: 0 blank or line break, 1 punctuation, 2 a word's.</summary>
    private static int Kind(char c) => char.IsWhiteSpace(c) ? 0 : TextNavigation.IsWordChar(c) ? 2 : 1;

    private static bool EmptyLineAt(TextSnapshot text, int offset)
    {
        LineSpan line = LineOf(text, offset);
        return line.Length == 0 && line.Start == offset;
    }

    /// <summary>Vim's w: the start of the next word or run of punctuation, an empty line counting as a word.</summary>
    private static int WordStart(TextSnapshot text, int offset)
    {
        int p = offset;
        int n = text.Length;
        if (p < n && Kind(text[p]) is var kind and not 0)
        {
            while (p < n && Kind(text[p]) == kind)
            {
                p++;
            }
        }
        while (p < n && Kind(text[p]) == 0)
        {
            p++;
            if (text[p - 1] == '\n' && EmptyLineAt(text, p))
            {
                return p;
            }
        }
        return p;
    }

    /// <summary>Vim's e: the last character of this word if the caret is before it, else of the next.</summary>
    private static int WordEnd(TextSnapshot text, int offset)
    {
        int n = text.Length;
        int p = offset + 1;
        while (p < n && Kind(text[p]) == 0)
        {
            p++;
        }
        if (p >= n)
        {
            return offset;
        }
        int kind = Kind(text[p]);
        while (p + 1 < n && Kind(text[p + 1]) == kind)
        {
            p++;
        }
        return p;
    }

    /// <summary>Vim's b: the start of this word if the caret is past it, else of the one before; an empty line stops it.</summary>
    private static int WordBack(TextSnapshot text, int offset)
    {
        if (offset == 0)
        {
            return 0;
        }
        int p = offset - 1;
        while (p > 0 && Kind(text[p]) == 0)
        {
            if (EmptyLineAt(text, p))
            {
                return p;
            }
            p--;
        }
        int kind = Kind(text[p]);
        while (p > 0 && kind != 0 && Kind(text[p - 1]) == kind)
        {
            p--;
        }
        return p;
    }
}
