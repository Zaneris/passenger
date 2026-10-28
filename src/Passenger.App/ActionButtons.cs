using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App;

/// <summary>A button that runs one of the window's actions (the toolbar's Build, Run, Debug, Stop): a click asks for the
/// action as its keys do, and from the moment its work begins until it is done the button shows it, a ring turning round
/// its icon and its tip saying the stage. One call per button, so a new button gets it all.</summary>
public static class ActionButtons
{
    /// <summary>How far the ring stands out from the icon it surrounds on every side.</summary>
    private const double RingOutset = 5;

    /// <summary>The id of the action a button runs, as <see cref="Bind"/> set it.</summary>
    public static readonly AttachedProperty<string?> ActionProperty =
        AvaloniaProperty.RegisterAttached<Button, string?>("Action", typeof(ActionButtons));

    public static string? GetAction(Button button) => button.GetValue(ActionProperty);

    /// <summary>Whether the button shows its action's work under way.</summary>
    public static bool IsWorking(Button button) => button.Classes.Contains("working");

    /// <summary>Makes the button run the action through <paramref name="request"/> (the window's, which says why one cannot
    /// run) and show its work, its tip naming the action's keys (<see cref="ActionTips"/>).</summary>
    public static void Bind(Button button, string id, ActionRegistry actions, Func<string, bool> request)
    {
        button.Click += (_, _) => request(id);
        ActionTips.Set(button, id, ToolTip.GetTip(button) as string ?? actions[id]?.Title ?? id);
        Show(button, id, actions);
    }

    /// <summary>Marks a tool window's button as running an action it asks for its own way, so the window shows that
    /// action's work on it (<see cref="ShowMarked"/>).</summary>
    public static void Mark(Button button, string id) => button.SetValue(ActionProperty, id);

    /// <summary>Shows each marked button's action's work on it, in what a window hosts.</summary>
    public static void ShowMarked(Avalonia.LogicalTree.ILogical within, ActionRegistry actions)
    {
        foreach (Button button in Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(within).OfType<Button>().ToList())
        {
            if (GetAction(button) is { } id)
            {
                Show(button, id, actions);
            }
        }
    }

    /// <summary>Has the button show its action's work: a ring round its icon and the stage in its tip while it works.</summary>
    private static void Show(Button button, string id, ActionRegistry actions)
    {
        button.SetValue(ActionProperty, id);
        string? tip = ToolTip.GetTip(button) as string;
        Panel? shown = null;
        void Show()
        {
            Working? under = actions.Work.Of(actions[id]?.WorkId ?? id);
            button.Classes.Set("working", under is not null);
            // A tip naming the action's keys names those of the keymap in force now.
            string? words = ActionTips.Of(button) ?? tip;
            ToolTip.SetTip(button, under is null ? words : $"{words}: {under.Stage}");
            if (under is not null && shown is null && button.Content is Control icon)
            {
                // Over the icon, standing out past it without growing the button, so nothing on the toolbar moves.
                WorkingRing ring = new() { Margin = new Thickness(-RingOutset), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                button.Content = null;
                shown = new Panel { Children = { icon, ring } };
                button.Content = shown;
            }
            else if (under is null && shown is not null)
            {
                Control own = shown.Children[0];
                shown.Children.Clear();
                button.Content = own;
                shown = null;
            }
        }
        actions.Work.Changed += (_, change) =>
        {
            if (change.Work.Id == (actions[id]?.WorkId ?? id))
            {
                Show();
            }
        };
    }
}
