using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Acp;

namespace Passenger.App.Chat;

/// <summary>The agent's slash commands and skills over the box, as Claude Code's terminal and Cursor offer them: typing
/// "/" lists them, the letters after it narrow the list (a name starting with them first), Up and Down choose, Enter or
/// Tab writes the chosen one into the box with a space after it, and Escape closes the list.</summary>
public sealed class CommandMenu
{
    private const int MostShown = 60;

    private readonly TextBox input;
    private readonly ListBox list = new() { MaxHeight = 260, MinWidth = 280 };
    private IReadOnlyList<AgentCommand> commands = [];
    private List<AgentCommand> shown = [];

    public CommandMenu(TextBox input)
    {
        this.input = input;
        Popup = new Popup
        {
            PlacementTarget = input,
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border { Classes = { StyleClasses.Popup }, Child = list },
        };
        input.TextChanged += (_, _) => Follow();
        list.DoubleTapped += (_, _) => Take();
    }

    /// <summary>The list's popup, which a panel of the box's holds.</summary>
    public Popup Popup { get; }

    /// <summary>Whether the list shows.</summary>
    public bool IsOpen => Popup.IsOpen;

    /// <summary>The names it lists now, in order.</summary>
    public IReadOnlyList<string> Shown => [.. shown.Select(command => command.Name)];

    /// <summary>The commands the agent offers now.</summary>
    public IReadOnlyList<AgentCommand> Commands
    {
        get => commands;
        set
        {
            commands = value;
            if (IsOpen)
            {
                Follow();
            }
        }
    }

    /// <summary>A key pressed in the box while the list shows: moving through it, taking a command, or closing it.
    /// Whether it was the list's.</summary>
    public bool Press(KeyEventArgs e)
    {
        if (!IsOpen || shown.Count == 0)
        {
            return false;
        }
        switch (e.Key)
        {
            case Key.Down:
                list.SelectedIndex = Math.Min(list.SelectedIndex + 1, shown.Count - 1);
                list.ScrollIntoView(list.SelectedIndex);
                return true;
            case Key.Up:
                list.SelectedIndex = Math.Max(list.SelectedIndex - 1, 0);
                list.ScrollIntoView(list.SelectedIndex);
                return true;
            case Key.Enter or Key.Tab when e.KeyModifiers == KeyModifiers.None:
                Take();
                return true;
            case Key.Escape:
                Popup.IsOpen = false;
                return true;
            default:
                return false;
        }
    }

    /// <summary>Lists the commands the box's text names, while it is a slash and the start of a name.</summary>
    private void Follow()
    {
        string text = input.Text ?? string.Empty;
        if (text.Length == 0 || text[0] != '/' || text.Contains(' ', StringComparison.Ordinal) || commands.Count == 0)
        {
            Popup.IsOpen = false;
            return;
        }
        string typed = text[1..];
        shown = [.. commands
            .Where(command => command.Name.Contains(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name.StartsWith(typed, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(command => command.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MostShown)];
        list.ItemsSource = shown.Select(Row).ToList();
        list.SelectedIndex = shown.Count > 0 ? 0 : -1;
        Popup.IsOpen = shown.Count > 0;
    }

    private void Take()
    {
        if (list.SelectedIndex < 0 || list.SelectedIndex >= shown.Count)
        {
            return;
        }
        string text = $"/{shown[list.SelectedIndex].Name} ";
        Popup.IsOpen = false;
        input.Text = text;
        input.CaretIndex = text.Length;
    }

    private static StackPanel Row(AgentCommand command)
    {
        StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = "/" + command.Name, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (command.Description is { } what)
        {
            row.Children.Add(new TextBlock { Classes = { StyleClasses.Muted }, Text = what, MaxWidth = 320, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        }
        ToolTip.SetTip(row, command.Hint is { } hint ? $"/{command.Name} {hint}" : null);
        return row;
    }
}
