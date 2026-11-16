using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>The agent's to-do list above the box, as Cursor pins it: "To-dos 2/5" and the step it is on, then each step
/// with a ring while it is worked on, a check once done (its words struck through) and a circle while it waits; Show and
/// Hide fold the list. Each update of the agent's replaces the whole list; an empty one hides the bar.</summary>
public sealed class TodoBar : Border
{
    private readonly TextBlock count = new() { FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock current = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(8, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button fold = new() { Classes = { StyleClasses.Link }, Content = "Hide", VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel steps = new() { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
    private IReadOnlyList<PlanEntry> entries = [];

    public TodoBar()
    {
        Classes.Add(StyleClasses.Framed);
        Padding = new Thickness(8, 6);
        Margin = new Thickness(12, 0, 12, 6);
        IsVisible = false;
        fold.Click += (_, _) =>
        {
            steps.IsVisible = !steps.IsVisible;
            fold.Content = steps.IsVisible ? "Hide" : "Show";
        };
        DockPanel heading = new();
        DockPanel.SetDock(fold, Dock.Right);
        DockPanel.SetDock(count, Dock.Left);
        heading.Children.Add(fold);
        heading.Children.Add(count);
        heading.Children.Add(current);
        Child = new StackPanel { Children = { heading, new ScrollViewer { MaxHeight = 160, Content = steps } } };
    }

    /// <summary>What its heading says: "To-dos 2/5".</summary>
    public string Count => count.Text ?? string.Empty;

    /// <summary>The steps it lists, each with its status.</summary>
    public IReadOnlyList<PlanEntry> Entries => entries;

    /// <summary>The agent's list as it is now.</summary>
    public void Show(IReadOnlyList<PlanEntry> list)
    {
        entries = list;
        IsVisible = list.Count > 0;
        int done = list.Count(entry => entry.Status == "completed");
        count.Text = $"To-dos {done}/{list.Count}";
        PlanEntry? working = list.FirstOrDefault(entry => entry.Status == "in_progress") ?? list.FirstOrDefault(entry => entry.Status == "pending");
        current.Text = working is null ? "All done" : working.Content;
        steps.Children.Clear();
        foreach (PlanEntry entry in list)
        {
            steps.Children.Add(Step(entry));
        }
    }

    private static DockPanel Step(PlanEntry entry)
    {
        Control mark = entry.Status switch
        {
            "completed" => new RowIcon { DataKey = IconKeys.Check, ForegroundKey = TokenKeys.Vcs_Added, Width = 14, Height = 14 },
            "in_progress" => new WorkingRing { Width = 12, Height = 12 },
            _ => new TextBlock { Classes = { StyleClasses.Muted }, Text = "○", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center },
        };
        mark.Width = 14;
        mark.Margin = new Thickness(0, 0, 8, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(mark, Dock.Left);
        TextBlock words = new() { Text = entry.Content, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (entry.Status == "completed")
        {
            words.Classes.Add(StyleClasses.Muted);
            words.TextDecorations = TextDecorations.Strikethrough;
        }
        return new DockPanel { Children = { mark, words } };
    }

    /// <summary>Framed as Shell.axaml frames a Border, which styles a Border's subclass only through its key.</summary>
    protected override Type StyleKeyOverride => typeof(Border);
}
