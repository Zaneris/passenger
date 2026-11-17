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

    /// <summary>The lines a card shows until Show more is pressed, so a new file of a hundred lines stays a card.</summary>
    public const int FirstLines = 12;

    private const int Around = 2;

    /// <summary>How many lines the change adds and removes.</summary>
    public static (int Added, int Removed) Count(ChatChange change)
    {
        IReadOnlyList<DiffHunk> hunks = LineDiff.Hunks(LineDiff.Lines(change.Before), LineDiff.Lines(change.After));
        return (hunks.Sum(hunk => hunk.NewCount), hunks.Sum(hunk => hunk.OldCount));
    }

    /// <summary>The most lines a snippet of a done edit shows.</summary>
    public const int SnippetLines = 4;

    /// <summary>A done edit as Cursor shows it in the reply: the file's icon, name and what it added and removed, then the
    /// first lines it changed, each with its number and its added or deleted ground, in the editor's font. The whole of it
    /// is a button, which opens the file.</summary>
    public static Button Snippet(ChatChange change, int added, int removed)
    {
        string name = System.IO.Path.GetFileName(change.Path);
        RowIcon icon = new()
        {
            Data = Explorer.FileIcons.ForFile(change.Path),
            Foreground = Explorer.FileIcons.BrushForFile(change.Path),
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        StackPanel heading = new() { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6, Margin = new Thickness(8, 4) };
        heading.Children.Add(icon);
        heading.Children.Add(new TextBlock { Text = name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
        heading.Children.Add(Coloured($"+{added}", TokenKeys.Vcs_Added));
        if (removed > 0)
        {
            heading.Children.Add(Coloured($"−{removed}", TokenKeys.Vcs_Deleted));
        }
        StackPanel lines = new();
        string[] before = LineDiff.Lines(change.Before);
        string[] after = LineDiff.Lines(change.After);
        if (LineDiff.Hunks(before, after).FirstOrDefault() is { } hunk)
        {
            int shown = 0;
            if (hunk.OldStart > 0 && hunk.NewStart > 0)
            {
                Row(hunk.NewStart, " ", after[hunk.NewStart - 1], null);
            }
            for (int i = 0; i < hunk.OldCount && shown < SnippetLines; i++)
            {
                Row(hunk.OldStart + i + 1, "-", before[hunk.OldStart + i], TokenKeys.Diff_Deleted);
            }
            for (int i = 0; i < hunk.NewCount && shown < SnippetLines; i++)
            {
                Row(hunk.NewStart + i + 1, "+", after[hunk.NewStart + i], TokenKeys.Diff_Added);
            }

            void Row(int number, string mark, string text, string? ground)
            {
                shown++;
                TextBlock place = new TextBlock { Classes = { StyleClasses.Muted }, Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture), Width = 36, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 8, 0), FontSize = 12 }.InCodeFont();
                DockPanel.SetDock(place, Dock.Left);
                TextBlock marked = new TextBlock { Text = mark, Width = 14, FontSize = 12 }.InCodeFont();
                DockPanel.SetDock(marked, Dock.Left);
                TextBlock words = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis }.InCodeFont();
                DockPanel row = new() { Children = { place, marked, words } };
                if (ground is not null)
                {
                    row.Bind(Panel.BackgroundProperty, row.GetResourceObservable(ground));
                }
                lines.Children.Add(row);
            }
        }
        Border frame = new()
        {
            Classes = { StyleClasses.Framed },
            Child = new StackPanel { Children = { heading, new Border { Classes = { StyleClasses.Code }, Child = lines, IsVisible = lines.Children.Count > 0 } } },
        };
        Button snippet = new()
        {
            Classes = { StyleClasses.TextLink },
            Content = frame,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
        };
        ToolTip.SetTip(snippet, $"Open {change.Path} with the agent's changes marked");
        return snippet;
    }

    private static TextBlock Coloured(string text, string colour)
    {
        TextBlock counted = new() { Text = text, FontSize = 11, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        counted.Bind(TextBlock.ForegroundProperty, counted.GetResourceObservable(colour));
        return counted;
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
        int folded = Math.Min(shown, MostLines) - FirstLines;
        if (folded <= 0)
        {
            return new Border { Classes = { StyleClasses.Code }, Child = lines };
        }
        foreach (Control line in lines.Children.Skip(FirstLines))
        {
            line.IsVisible = false;
        }
        Button unfold = new() { Classes = { StyleClasses.Link }, Content = $"Show {folded} more lines", Margin = new Thickness(8, 2) };
        unfold.Click += (_, _) =>
        {
            foreach (Control line in lines.Children)
            {
                line.IsVisible = true;
            }
            unfold.IsVisible = false;
        };
        return new Border { Classes = { StyleClasses.Code }, Child = new StackPanel { Children = { lines, unfold } } };

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
