using System.Text;
using Passenger.Text;

namespace Passenger.UnitTests.Text;

/// <summary>Text with its carets written in it: '|' a caret, '«' a selection's anchor before its caret, '»' one after it
/// ("«word|" selects a word forwards, "|word»" backwards). The last caret written is the primary.</summary>
internal static class Marked
{
    public static (TextSnapshot Text, SelectionSet Selections) Parse(string marked)
    {
        StringBuilder text = new(marked.Length);
        List<(char Mark, int At)> marks = [];
        foreach (char c in marked)
        {
            if (c is '|' or '«' or '»')
            {
                marks.Add((c, text.Length));
            }
            else
            {
                text.Append(c);
            }
        }
        List<Selection> selections = [];
        for (int i = 0; i < marks.Count; i++)
        {
            (char mark, int at) = marks[i];
            if (mark == '«')
            {
                selections.Add(new Selection(at, marks[++i].At));
            }
            else if (i + 1 < marks.Count && marks[i + 1].Mark == '»')
            {
                selections.Add(new Selection(marks[++i].At, at));
            }
            else
            {
                selections.Add(Selection.At(at));
            }
        }
        return (TextSnapshot.From(text.ToString()), SelectionSet.Create(selections, selections.Count - 1));
    }

    public static string Show(TextSnapshot text, SelectionSet selections)
    {
        StringBuilder shown = new(text.ToString());
        foreach (Selection s in selections.Items.Reverse())
        {
            if (s.IsEmpty)
            {
                shown.Insert(s.Caret, '|');
            }
            else if (s.Caret > s.Anchor)
            {
                shown.Insert(s.Caret, '|').Insert(s.Anchor, '«');
            }
            else
            {
                shown.Insert(s.Anchor, '»').Insert(s.Caret, '|');
            }
        }
        return shown.ToString();
    }

    /// <summary>The text and carets a command's plan leaves, its edits checked as a document takes them (sorted, apart, in
    /// the text); null for a command with nothing to do.</summary>
    public static string? After(TextSnapshot text, EditPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }
        for (int i = 0; i < plan.Edits.Count; i++)
        {
            TextEdit edit = plan.Edits[i];
            Assert.InRange(edit.Start, 0, text.Length - edit.Length);
            Assert.True(i == 0 || plan.Edits[i - 1].End <= edit.Start, $"The command's edits overlap or are out of order: {string.Join(", ", plan.Edits)}");
        }
        TextSnapshot after = text.Apply(plan.Edits);
        foreach (Selection s in plan.Selections.Items)
        {
            Assert.InRange(s.Anchor, 0, after.Length);
            Assert.InRange(s.Caret, 0, after.Length);
        }
        return Show(after, plan.Selections);
    }
}
