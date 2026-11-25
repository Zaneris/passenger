using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Vcs;

namespace Passenger.App.Vcs;

/// <summary>Unstash Changes: the stashes, the newest first, each with when it was made; Pop puts the chosen one's changes
/// back and drops it, Apply keeps it, Drop (Delete) throws it away once the person agrees. One runs at a time, the buttons
/// off meanwhile. Enter or a double click shows the chosen one's files beside the working tree's, applying nothing, as the
/// popup closes. Escape closes.</summary>
public sealed class UnstashPopup : UserControl
{
    private readonly ListBox list;
    private readonly Func<StashInfo, bool, Task<bool>> unstash;
    private readonly Func<StashInfo, Task<bool>> drop;
    private readonly Func<string, string, string, Task<bool>> confirm;
    private readonly Action<StashInfo> show;
    private readonly Button[] actions;
    private bool running;

    /// <param name="confirm">Asks before a drop (its title, the question, the button that drops).</param>
    /// <param name="show">Shows a stash's files beside the working tree's.</param>
    public UnstashPopup(IReadOnlyList<StashInfo> stashes, Func<StashInfo, bool, Task<bool>> unstash, Func<StashInfo, Task<bool>> drop,
        Func<string, string, string, Task<bool>> confirm, Action<StashInfo> show)
    {
        this.unstash = unstash;
        this.drop = drop;
        this.confirm = confirm;
        this.show = show;
        Stashes = stashes;
        Width = PopupFrame.Medium;
        list = new ListBox
        {
            Classes = { StyleClasses.Tree },
            MaxHeight = ListRow.Height * 10,
            ItemsSource = stashes,
            SelectedIndex = stashes.Count > 0 ? 0 : -1,
        };
        ReusedRows.Use(list, Row);
        ListKeys.Act(list, new ListActs { Open = ShowFiles });
        Button pop = Action("Pop", () => PopAsync(), primary: true);
        Button apply = Action("Apply", () => ApplyAsync());
        Button remove = Action("Drop", () => DropAsync());
        actions = [pop, apply, remove];
        Content = new Border
        {
            Classes = { StyleClasses.Popup },
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Classes = { StyleClasses.PaneTitle }, Text = "Unstash Changes", Margin = new Thickness(Gaps.Wide, Gaps.Wide, Gaps.Wide, Gaps.Small) },
                    stashes.Count == 0
                        ? new TextBlock { Classes = { StyleClasses.Muted }, Text = "Nothing is stashed", Margin = new Thickness(Gaps.Wide, 0, Gaps.Wide, Gaps.Wide) }
                        : list,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = Gaps.Small,
                        Margin = new Thickness(Gaps.Wide, Gaps.Normal, Gaps.Wide, Gaps.Wide),
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

    /// <summary>The chosen stash's files beside the working tree's, nothing applied; the popup closes for them, unless a
    /// pop, apply or drop is running.</summary>
    public void ShowFiles()
    {
        if (running || Selected is not { } stash)
        {
            return;
        }
        Done?.Invoke(this, EventArgs.Empty);
        show(stash);
    }

    public Task PopAsync() => Use(stash => unstash(stash, true));

    public Task ApplyAsync() => Use(stash => unstash(stash, false));

    /// <summary>Drop, asked first; a no leaves the popup open on the stash.</summary>
    public Task DropAsync() => Use(drop, stash => confirm(
        $"Remove Stash stash@{{{stash.Index}}}?", $"Do you want to remove stash@{{{stash.Index}}}?\n\"{stash.Message}\"", "Remove"));

    /// <summary>Runs an action on the chosen stash unless one is running, so a second press waits for the first to end.</summary>
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
    private void FocusRow() => ListKeys.Hold(list);

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
            button.Classes.Add(StyleClasses.Primary);
        }
        button.Click += (_, _) => IdeErrors.Observe(run());
        return button;
    }

    /// <summary>A stash's row: its message, and when it was made at the right, muted.</summary>
    private static Control Row()
    {
        TextBlock when = ListRow.DetailText();
        TextBlock message = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel row = new() { Height = ListRow.Height, Margin = ListRow.Inset };
        row.Children.Add(when);
        row.Children.Add(message);
        return row.Showing<StashInfo>(stash =>
        {
            when.Text = ToolWindows.GitLogView.When(stash.Time);
            message.Text = stash.Message;
        });
    }
}
