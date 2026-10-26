using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Navigation;

/// <summary>Find Action: every action by name, the recently run ones first, narrowed as words are typed, each with its
/// group and shortcut and greyed where it cannot run; Enter or a double-click runs the chosen one, Escape gives
/// up.</summary>
public sealed class ActionPopup : SearchPopup<IdeAction>
{
    private readonly ActionRegistry actions;
    private readonly ActionContext context;
    private readonly IReadOnlyList<string> recent;

    public ActionPopup(ActionRegistry actions, ActionContext context, IReadOnlyList<string> recent, string? initialQuery = null)
        : base("Actions", "Type an action's name", PopupFrame.Wide, initialQuery)
    {
        this.actions = actions;
        this.context = context;
        this.recent = recent;
        QueryChanged();
    }

    protected override void QueryChanged()
    {
        IReadOnlyList<IdeAction> found = actions.Find(Query);
        if (Query.Trim().Length == 0)
        {
            found = [.. recent.Select(id => found.FirstOrDefault(a => a.Id == id)).OfType<IdeAction>(), .. found.Where(a => !recent.Contains(a.Id))];
        }
        Show(found, "No actions match");
    }

    /// <summary>An action's row: its title, greyed where it cannot run, its group, and its shortcut at the right.</summary>
    protected override Control Row()
    {
        TextBlock title = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        TextBlock group = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        group.Classes.Add("muted");
        TextBlock keys = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 4, 0) };
        keys.Classes.Add("muted");
        DockPanel row = new() { Height = ListRow.Height, Margin = new Thickness(4, 0) };
        DockPanel.SetDock(keys, Dock.Right);
        row.Children.Add(keys);
        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { title, group },
        });
        return row.Showing<IdeAction>(action =>
        {
            title.Text = action.Title;
            // Listed, so it can be found, but greyed: it cannot run here.
            title.Classes.Set("muted", !action.CanRun(context));
            group.Text = action.Group;
            keys.Text = actions.Shortcut(action.Id) ?? string.Empty;
        });
    }
}
