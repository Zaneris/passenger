using System.Text;
using Passenger.Text;

namespace Passenger.Editor;

/// <summary>Replace mode, typed over the text at every caret, and macros: keys recorded into a register by q and pressed
/// again by @.</summary>
public sealed partial class Vim
{
    /// <summary>The most keys one @ presses, its macros' own @s counted: a macro calling itself ends there.</summary>
    public const int MostReplayed = 100_000;

    /// <summary>What each caret of replace mode typed over, newest last: the text it replaced, empty where it went past
    /// the line's end, or a line break's length marked by a negative number.</summary>
    private List<List<string>> replaced = [];

    /// <summary>The replace session under way: the command that began it, its count and what it typed.</summary>
    private (Command Command, StringBuilder Typed)? replacing;

    /// <summary>The keys recorded so far and the register they go to, while q records.</summary>
    private (char Register, List<string> Keys)? recording;

    /// <summary>The register @ last pressed, for @@.</summary>
    private char? lastPlayed;

    /// <summary>The register keys are being recorded into, for the status bar; null while nothing records.</summary>
    public char? Recording => recording?.Register;

    private VimResult EnterReplace(Command command, SelectionSet selections)
    {
        Mode = VimMode.Replace;
        replaced = [.. selections.Items.Select(_ => new List<string>())];
        replacing = (command, new StringBuilder());
        return new VimResult(true, selections);
    }

    /// <summary>A key in replace mode: a character typed over the one at each caret (or added at a line's end), Enter a
    /// line break added, Backspace putting back what the last key typed over, Escape back to normal mode.</summary>
    private VimResult PressReplace(string key, TextSnapshot text, SelectionSet selections, IVimView view)
    {
        if (key == Escape)
        {
            return LeaveReplace(text, selections, view);
        }
        if (replaced.Count != selections.Count)
        {
            replaced = [.. selections.Items.Select(_ => new List<string>())];
        }
        if (key == "<BS>")
        {
            if (replacing is { } session && session.Typed.Length > 0)
            {
                session.Typed.Length -= Math.Max(1, char.IsLowSurrogate(session.Typed[^1]) ? 2 : 1);
            }
            return TakeBack(text, selections);
        }
        string? typed = key switch
        {
            "<CR>" => "\n",
            "<Tab>" => "\t",
            _ => key.Length == 1 || (key.Length == 2 && char.IsSurrogatePair(key[0], key[1])) ? key : null,
        };
        if (typed is null)
        {
            return key == "<Del>" ? VimResult.Held : VimResult.Left;
        }
        replacing?.Typed.Append(typed);
        return TypeOver(text, selections, typed, view);
    }

    private VimResult TypeOver(TextSnapshot text, SelectionSet selections, string typed, IVimView view)
    {
        List<TextEdit> edits = [];
        List<int> landings = [];
        for (int i = 0; i < selections.Count; i++)
        {
            int caret = selections.Items[i].Caret;
            LineSpan line = VimMotions.LineOf(text, caret);
            if (typed == "\n")
            {
                edits.Add(new TextEdit(caret, 0, view.LineEnding));
                replaced[i].Add("\n");
                landings.Add(view.LineEnding.Length);
                continue;
            }
            int end = caret < line.End ? TextNavigation.NextGrapheme(text, caret) : caret;
            replaced[i].Add(text.GetText(caret, end - caret));
            edits.Add(new TextEdit(caret, end - caret, typed));
            landings.Add(typed.Length);
        }
        return new VimResult(true, Edits: [Placed(text, edits, landings, selections.PrimaryIndex)]);
    }

    /// <summary>Backspace in replace mode: what the last character typed over put back at each caret, the caret before it;
    /// a caret with nothing typed steps back alone.</summary>
    private VimResult TakeBack(TextSnapshot text, SelectionSet selections)
    {
        List<TextEdit> edits = [];
        List<int> landings = [];
        List<Selection> moved = [];
        for (int i = 0; i < selections.Count; i++)
        {
            int caret = selections.Items[i].Caret;
            List<string> stack = replaced[i];
            if (stack.Count == 0)
            {
                moved.Add(Selection.At(Math.Max(VimMotions.StepBack(text, caret), VimMotions.LineOf(text, caret).Start)));
                continue;
            }
            string was = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            int back = VimMotions.StepBack(text, caret);
            if (was == "\n")
            {
                LineSpan before = text.GetLine(Math.Max(0, text.GetLineFromOffset(caret) - 1));
                edits.Add(new TextEdit(before.End, caret - before.End, string.Empty));
                landings.Add(0);
                continue;
            }
            edits.Add(new TextEdit(back, caret - back, was));
            landings.Add(0);
        }
        if (edits.Count == 0)
        {
            return new VimResult(true, SelectionSet.Create(moved, Math.Min(selections.PrimaryIndex, moved.Count - 1)));
        }
        return new VimResult(true, Edits: [Placed(text, edits, landings, selections.PrimaryIndex)]);
    }

    /// <summary>Edits made one step of the session's undo, each caret <paramref name="landings"/> past its edit's start.</summary>
    private static EditPlan Placed(TextSnapshot text, List<TextEdit> edits, List<int> landings, int primary)
    {
        List<(TextEdit Edit, int Landing)> sorted = [.. edits.Zip(landings).OrderBy(e => e.First.Start)];
        List<TextEdit> apart = [];
        List<int> carets = [];
        int shift = 0;
        foreach ((TextEdit edit, int landing) in sorted)
        {
            if (apart.Count > 0 && apart[^1].End > edit.Start)
            {
                continue;
            }
            apart.Add(edit);
            carets.Add(edit.Start + shift + landing);
            shift += edit.Text.Length - edit.Length;
        }
        return new EditPlan(apart, SelectionSet.Create([.. carets.Select(Selection.At)], Math.Min(primary, carets.Count - 1)), EditKind.Joined);
    }

    /// <summary>Escape in replace mode: a count types what was typed over again, the carets step back onto the text, and
    /// what was typed is kept for . to type over again.</summary>
    private VimResult LeaveReplace(TextSnapshot text, SelectionSet selections, IVimView view)
    {
        Mode = VimMode.Normal;
        (Command Command, StringBuilder Typed)? session = replacing;
        replacing = null;
        List<EditPlan> plans = [];
        string typed = session?.Typed.ToString() ?? string.Empty;
        if (session is { } s && s.Command.Count > 1 && typed.Length > 0)
        {
            int times = Math.Min(s.Command.Count - 1, MostRepeatedText / typed.Length);
            for (int i = 0; i < times; i++)
            {
                foreach (char c in typed)
                {
                    EditPlan plan = TypeOver(text, selections, c.ToString(), view).Edit!;
                    plans.Add(plan);
                    (text, selections) = (text.Apply(plan.Edits), plan.Selections);
                }
            }
        }
        if (session is { } begun && !replaying)
        {
            lastChange = new ChangeMade(begun.Command.Keys, begun.Command.Count, typed);
        }
        SelectionSet back = selections.With(c => Selection.At(Settled(text, Math.Max(VimMotions.StepBack(text, c.Caret), VimMotions.LineOf(text, c.Caret).Start))));
        if (plans.Count == 0)
        {
            return new VimResult(true, back);
        }
        plans[^1] = plans[^1] with { Selections = back };
        return new VimResult(true, Edits: plans);
    }

    /// <summary>q with a register begins recording keys into it, q again ends it; @ with a register presses its keys
    /// again (count times), @@ the register pressed last.</summary>
    private VimResult Macro(Command command)
    {
        if (command.Name == "q")
        {
            char name = command.Argument![0];
            recording = (name, []);
            return VimResult.Held;
        }
        char? register = command.Argument == "@" ? lastPlayed : command.Argument![0];
        if (register is not { } played || Registers.Get(played) is not { } macro)
        {
            return VimResult.Held;
        }
        lastPlayed = played;
        List<string> keys = KeysOf(macro.Text);
        int times = Math.Clamp(command.Count, 1, Math.Max(1, MostReplayed / Math.Max(1, keys.Count)));
        return new VimResult(true) { Replay = [.. Enumerable.Repeat(keys, times).SelectMany(k => k)] };
    }

    /// <summary>The key just pressed recorded, while q records; the q ending it ends the recording instead, keeping
    /// what was recorded in its register.</summary>
    private bool Recorded(string key)
    {
        if (recording is not { } r)
        {
            return false;
        }
        if (key == "q" && Mode == VimMode.Normal && pending.Count == 0 && commandLine is null)
        {
            Registers.Record(r.Register, TextOf(r.Keys));
            recording = null;
            return true;
        }
        if (key is not ("<Other>" or "<Up>" or "<Down>"))
        {
            r.Keys.Add(key);
        }
        return false;
    }

    /// <summary>Keys in Vim's notation as a register keeps them, named keys as the control characters Vim writes.</summary>
    private static string TextOf(IEnumerable<string> keys)
    {
        StringBuilder text = new();
        foreach (string key in keys)
        {
            text.Append(key switch
            {
                Escape => "\u001b",
                "<CR>" => "\r",
                "<BS>" => "\b",
                "<Tab>" => "\t",
                "<Del>" => "\u007f",
                "<C-r>" => "\u0012",
                "<C-v>" => "\u0016",
                _ => key,
            });
        }
        return text.ToString();
    }

    /// <summary>A register's text as the keys it presses: its control characters the named keys, a line break Enter.</summary>
    private static List<string> KeysOf(string text)
    {
        List<string> keys = [];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            keys.Add(c switch
            {
                '\u001b' => Escape,
                '\r' or '\n' => "<CR>",
                '\b' => "<BS>",
                '\t' => "<Tab>",
                '\u007f' => "<Del>",
                '\u0012' => "<C-r>",
                '\u0016' => "<C-v>",
                _ when char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) => text.Substring(i++, 2),
                _ => c.ToString(),
            });
        }
        return keys;
    }
}
