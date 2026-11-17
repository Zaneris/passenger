using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>The agent's to-do list pinned at the top of the conversation, as Cursor pins it while it builds: "Build" and
/// the plan's title once a plan is built, then "1 of 3 To-dos Completed" and the step it is on, which a click opens into
/// each step with a ring while it is worked on, a check once done (its words struck through) and a circle while it
/// waits. Each update of the agent's replaces the whole list; an empty one hides it.</summary>
public sealed class TodoBar : Border
{
    private readonly TextBlock built = new() { FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly DockPanel building;
    private readonly TextBlock count = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock current = new() { Classes = { StyleClasses.Muted }, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly RowIcon chevron = new() { DataKey = IconKeys.ChevronRight, ForegroundKey = TokenKeys.TextMuted, Width = 12, Height = 12, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel steps = new() { Spacing = 2, Margin = new Thickness(18, 4, 0, 0) };
    private readonly ScrollViewer list;
    private IReadOnlyList<PlanEntry> entries = [];

    public TodoBar()
    {
        Classes.Add(StyleClasses.Framed);
        Padding = new Thickness(8, 6);
        Margin = new Thickness(12, 0, 12, 6);
        IsVisible = false;
        TextBlock word = new() { Text = "Build", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        RowIcon icon = new() { DataKey = IconKeys.DocumentText, ForegroundKey = TokenKeys.TextMuted, Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(word, Dock.Left);
        DockPanel.SetDock(icon, Dock.Left);
        building = new DockPanel { IsVisible = false, Margin = new Thickness(0, 0, 0, 4), Children = { word, icon, built } };
        list = new ScrollViewer { MaxHeight = 160, Content = steps, IsVisible = false };
        DockPanel.SetDock(chevron, Dock.Left);
        DockPanel.SetDock(count, Dock.Left);
        Button fold = new()
        {
            Classes = { StyleClasses.TextLink },
            Content = new DockPanel { Children = { chevron, count, current } },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        fold.Click += (_, _) => Open(!list.IsVisible);
        Child = new StackPanel { Children = { building, fold, list } };
    }

    /// <summary>What its line says: "1 of 3 To-dos Completed".</summary>
    public string Count => count.Text ?? string.Empty;

    /// <summary>The plan being built, as its line names it; null while none is.</summary>
    public string? Built => building.IsVisible ? built.Text : null;

    /// <summary>The steps it lists, each with its status.</summary>
    public IReadOnlyList<PlanEntry> Entries => entries;

    /// <summary>Whether it lists its steps, rather than its one line.</summary>
    public bool IsOpen => list.IsVisible;

    /// <summary>A plan is being built: its title over the steps, as Cursor's "Build · Seed a second User account".</summary>
    public void Building(string title)
    {
        built.Text = title;
        building.IsVisible = true;
    }

    /// <summary>Opens the list of steps, or folds it to its one line.</summary>
    public void Open(bool listed)
    {
        list.IsVisible = listed;
        chevron.DataKey = listed ? IconKeys.ChevronDown : IconKeys.ChevronRight;
    }

    /// <summary>The agent's list as it is now.</summary>
    public void Show(IReadOnlyList<PlanEntry> list)
    {
        entries = list;
        IsVisible = list.Count > 0;
        int done = list.Count(entry => entry.Status == "completed");
        count.Text = $"{done} of {list.Count} To-dos Completed";
        PlanEntry? working = list.FirstOrDefault(entry => entry.Status == "in_progress") ?? list.FirstOrDefault(entry => entry.Status == "pending");
        current.Text = working?.Content;
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
