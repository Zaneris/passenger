using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.App.Chat;

/// <summary>A proposed change's lines as a card shows them: each changed stretch with two lines around it, removed lines
/// on the theme's deleted colour and added ones on its added colour, as Git's diff marks them; a long one cut short.</summary>
public static class ChatDiff
{
    /// <summary>The most lines a card shows of one change.</summary>
    public const int MostLines = 80;

    private const int Around = 2;

    /// <summary>How many lines the change adds and removes.</summary>
    public static (int Added, int Removed) Count(ChatChange change)
    {
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(LineDiff.Lines(change.Before), LineDiff.Lines(change.After));
        return (hunks.Sum(hunk => hunk.NewCount), hunks.Sum(hunk => hunk.OldCount));
    }

    /// <summary>The change's lines, framed in the editor's font.</summary>
    public static Control View(ChatChange change)
    {
        string[] before = LineDiff.Lines(change.Before);
        string[] after = LineDiff.Lines(change.After);
        StackPanel lines = new();
        int shown = 0;
        int more = 0;
        int lastOld = -1;
        foreach (DiffHunk hunk in LineDiff.Hunks(before, after))
        {
            int from = Math.Max(hunk.OldStart - Around, lastOld < 0 ? 0 : lastOld);
            if (lastOld >= 0 && from > lastOld)
            {
                Add(" ⋯", null);
            }
            for (int i = from; i < hunk.OldStart; i++)
            {
                Add("  " + before[i], null);
            }
            for (int i = hunk.OldStart; i < hunk.OldStart + hunk.OldCount; i++)
            {
                Add("- " + before[i], TokenKeys.Diff_Deleted);
            }
            for (int i = hunk.NewStart; i < hunk.NewStart + hunk.NewCount; i++)
            {
                Add("+ " + after[i], TokenKeys.Diff_Added);
            }
            lastOld = Math.Min(hunk.OldStart + hunk.OldCount + Around, before.Length);
            for (int i = hunk.OldStart + hunk.OldCount; i < lastOld; i++)
            {
                Add("  " + before[i], null);
            }
        }
        if (more > 0)
        {
            lines.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, Text = $"… {more} more lines", Margin = new Thickness(8, 4) });
        }
        return new Border { Classes = { StyleClasses.Code }, Child = lines };

        void Add(string text, string? ground)
        {
            if (shown++ >= MostLines)
            {
                more++;
                return;
            }
            TextBlock line = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap }.InCodeFont();
            if (ground is not null)
            {
                line.Bind(TextBlock.BackgroundProperty, line.GetResourceObservable(ground));
            }
            lines.Children.Add(line);
        }
    }
}
