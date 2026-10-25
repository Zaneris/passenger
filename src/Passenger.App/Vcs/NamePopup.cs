using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Passenger.App.Vcs;

/// <summary>Asks for a name: Enter tries it, and when that fails the reason shows under the box and the name stays to
/// correct; Escape gives up. With kinds to choose from (New Class/Interface), they are listed under the box, the first
/// chosen, and Up and Down choose another while the name is typed. With an option (a file's Rename type), it is a check
/// box under the box, clicked while the keys stay with the name.</summary>
public sealed class NamePopup : UserControl, IAnswers<bool>
{
    private readonly TextBox name;
    private readonly TextBlock problem;
    private readonly ListBox? kinds;
    private readonly CheckBox? option;
    private readonly IReadOnlyList<string> kindNames;
    private readonly Func<string, Task<string?>> attempt;
    private bool trying;

    /// <param name="attempt">Tries the name: null when it worked, else why not.</param>
    /// <param name="kinds">What can be made, each with its icon, the first chosen; none to ask for the name alone.</param>
    /// <param name="option">A check box's words and whether it starts checked; none for no option.</param>
    public NamePopup(string title, string initial, Func<string, Task<string?>> attempt, IReadOnlyList<(string Name, Control Icon)>? kinds = null,
        (string Text, bool On)? option = null)
    {
        this.attempt = attempt;
        Width = PopupFrame.Narrow;
        name = new TextBox { Classes = { "search" }, Text = initial };
        problem = new TextBlock { Classes = { "muted" }, FontSize = 12, IsVisible = false, TextWrapping = TextWrapping.Wrap };
        StackPanel body = new() { Spacing = 8, Children = { new TextBlock { Classes = { "popupTitle" }, Text = title }, name } };
        if (option is { } asked)
        {
            this.option = new CheckBox { Content = asked.Text, IsChecked = asked.On, Focusable = false, FontSize = 12, MinHeight = 0 };
            this.option.IsCheckedChanged += (_, _) => OptionChanged?.Invoke(this, this.option.IsChecked == true);
            body.Children.Add(this.option);
        }
        body.Children.Add(problem);
        kindNames = [.. (kinds ?? []).Select(k => k.Name)];
        if (kinds is { Count: > 0 })
        {
            this.kinds = new ListBox
            {
                Classes = { "tree" },
                Focusable = false,
                ItemsSource = kinds.Select(k => new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Height = ListRow.Height,
                    Margin = new Thickness(4, 0),
                    Children = { k.Icon, new TextBlock { Text = k.Name, VerticalAlignment = VerticalAlignment.Center } },
                }).ToList(),
                SelectedIndex = 0,
            };
            // A kind clicked is chosen, and the keys stay with the name.
            this.kinds.SelectionChanged += (_, _) => name.Focus();
            body.Children.Add(this.kinds);
        }
        Content = new Border
        {
            Classes = { "popup" },
            Padding = new Thickness(12, 10),
            Child = body,
        };
        name.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Dispatcher.UIThread.Post(() =>
        {
            name.Focus();
            name.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Raised once the name worked (true) or was given up (false).</summary>
    public event EventHandler<bool>? Done;

    public string Text
    {
        get => name.Text ?? string.Empty;
        set => name.Text = value;
    }

    /// <summary>Why the last name tried did not work, or null.</summary>
    public string? Problem => problem.IsVisible ? problem.Text : null;

    /// <summary>The kind chosen, or null when there are none to choose from.</summary>
    public string? Kind => kinds is { SelectedIndex: >= 0 and var at } ? kindNames[at] : null;

    /// <summary>The option's check box, or null when there is none.</summary>
    public CheckBox? Option => option;

    /// <summary>The option checked or cleared.</summary>
    public event EventHandler<bool>? OptionChanged;

    /// <summary>Tries the name typed, as Enter does; Enter again while it is being tried (a stash running) does nothing.</summary>
    public async Task SubmitAsync()
    {
        if (trying)
        {
            return;
        }
        string typed = Text.Trim();
        trying = true;
        string? failed;
        try
        {
            failed = typed.Length == 0 ? "A name is needed" : await attempt(typed);
        }
        finally
        {
            trying = false;
        }
        if (failed is null)
        {
            Done?.Invoke(this, true);
            return;
        }
        problem.Text = failed;
        problem.IsVisible = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Done?.Invoke(this, false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            IdeErrors.Observe(SubmitAsync());
            e.Handled = true;
        }
        else if (kinds is not null && ListKeys.Move(kinds, e))
        {
            e.Handled = true;
        }
    }
}
