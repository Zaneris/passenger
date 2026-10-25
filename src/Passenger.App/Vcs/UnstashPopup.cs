using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Unstash Changes: the stashes, the newest first, each with when it was made; Pop (Enter) puts the
/// chosen one's changes back and drops it, Apply keeps it, Drop (Delete) throws it away once the person agrees. One runs
/// at a time, the buttons off meanwhile. Escape closes.</summary>
public sealed class UnstashPopup : UserControl
{
    private readonly ListBox list;
    private readonly Func<StashInfo, bool, Task<bool>> unstash;
    private readonly Func<StashInfo, Task<bool>> drop;
    private readonly Func<string, string, string, Task<bool>> confirm;
    private readonly Button[] actions;
    private bool running;

    /// <param name="confirm">Asks before a drop (its title, the question, the button that drops).</param>
    public UnstashPopup(IReadOnlyList<StashInfo> stashes, Func<StashInfo, bool, Task<bool>> unstash, Func<StashInfo, Task<bool>> drop,
        Func<string, string, string, Task<bool>> confirm)
    {
        this.unstash = unstash;
        this.drop = drop;
        this.confirm = confirm;
        Stashes = stashes;
        Width = PopupFrame.Medium;
        list = new ListBox
        {
            Classes = { "tree" },
            MaxHeight = ListRow.Height * 10,
            ItemsSource = stashes,
            SelectedIndex = stashes.Count > 0 ? 0 : -1,
            ItemTemplate = new FuncDataTemplate<StashInfo>((stash, _) => stash is null ? new TextBlock() : Row(stash), supportsRecycling: false),
        };
        list.DoubleTapped += (_, _) => IdeErrors.Observe(PopAsync());
        Button pop = Action("Pop", () => PopAsync(), primary: true);
        Button apply = Action("Apply", () => ApplyAsync());
        Button remove = Action("Drop", () => DropAsync());
        actions = [pop, apply, remove];
        Content = new Border
        {
            Classes = { "popup" },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { "popupTitle" }, Text = "Unstash Changes", Margin = new Thickness(12, 10, 12, 6) },
                    stashes.Count == 0
                        ? new TextBlock { Classes = { "muted" }, Text = "Nothing is stashed", Margin = new Thickness(12, 0, 12, 10) }
                        : list,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 6,
                        Margin = new Thickness(12, 8, 12, 10),
                        IsVisible = stashes.Count > 0,
                        Children = { remove, apply, pop },
                    },
                },
            },
        };
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(FocusRow, DispatcherPriority.Loaded);
    }

    /// <summary>The popup is done with: a stash was used or dropped, or it was dismissed.</summary>
    public event EventHandler? Done;

    public IReadOnlyList<StashInfo> Stashes { get; }

    public StashInfo? Selected
    {
        get => list.SelectedItem as StashInfo;
        set => list.SelectedItem = value;
    }

    public Task PopAsync() => Use(stash => unstash(stash, true));

    public Task ApplyAsync() => Use(stash => unstash(stash, false));

    /// <summary>Drop, asked first; a no leaves the popup open on the stash.</summary>
    public Task DropAsync() => Use(drop, stash => confirm(
        $"Remove Stash stash@{{{stash.Index}}}?", $"Do you want to remove stash@{{{stash.Index}}}?\n\"{stash.Message}\"", "Remove"));

    /// <summary>Runs an action on the chosen stash unless one is running: a stash is named by its index as the list was
    /// read, so a second drop begun before the first ends would drop the stash after it.</summary>
    private async Task Use(Func<StashInfo, Task<bool>> action, Func<StashInfo, Task<bool>>? ask = null)
    {
        if (running || Selected is not { } stash)
        {
            return;
        }
        Run(true);
        bool agreed;
        try
        {
            agreed = ask is null || await ask(stash);
            if (agreed)
            {
                await action(stash);
            }
        }
        finally
        {
            Run(false);
        }
        if (agreed)
        {
            Done?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            FocusRow();
        }
    }

    /// <summary>Gives the keys to the chosen row, as a click on it does: a ListBox takes no focus of its own, and the
    /// editor under the popup would take Enter and Delete.</summary>
    private void FocusRow()
    {
        if (list.SelectedIndex < 0)
        {
            return;
        }
        list.ScrollIntoView(list.SelectedIndex);
        list.UpdateLayout();
        list.ContainerFromIndex(list.SelectedIndex)?.Focus();
    }

    private void Run(bool now)
    {
        running = now;
        foreach (Button action in actions)
        {
            action.IsEnabled = !now;
        }
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Done?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                break;
            case Key.Enter:
                IdeErrors.Observe(PopAsync());
                e.Handled = true;
                break;
            case Key.Delete:
                // Not Backspace, the Mac's delete key, which is pressed in passing too easily to throw a stash away.
                IdeErrors.Observe(DropAsync());
                e.Handled = true;
                break;
        }
    }

    private static Button Action(string label, Func<Task> run, bool primary = false)
    {
        Button button = new() { Content = label };
        if (primary)
        {
            button.Classes.Add("primary");
        }
        button.Click += (_, _) => IdeErrors.Observe(run());
        return button;
    }

    private static Control Row(StashInfo stash)
    {
        TextBlock when = new() { Text = ToolWindows.GitLogView.When(stash.Time), VerticalAlignment = VerticalAlignment.Center, FontSize = 12, Margin = new Thickness(12, 0, 4, 0) };
        when.Classes.Add("muted");
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(8, 0) };
        DockPanel.SetDock(when, Dock.Right);
        row.Children.Add(when);
        row.Children.Add(new TextBlock { Text = stash.Message, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return row;
    }
}
