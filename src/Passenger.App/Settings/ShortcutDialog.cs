using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App.Settings;

/// <summary>A shortcut dialog's frame (Add Keyboard Shortcut, Add Mouse Shortcut): the action and where it is, the field the
/// shortcut is given in, and below it the actions the shortcut is "Already assigned to". OK closes it with the shortcut once
/// there is one (<see cref="Chosen"/>); Cancel or Escape with null.</summary>
public abstract class ShortcutDialog : IdeWindow
{
    private readonly StackPanel conflictsList = new() { Spacing = 2 };
    private readonly DockPanel conflictsPanel = new() { IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };

    protected ShortcutDialog(string title)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        OkButton = DialogFooter.Default();
        OkButton.IsEnabled = false;
        OkButton.Click += (_, _) =>
        {
            if (Chosen is { } chosen)
            {
                Close(chosen);
            }
        };

        PathIcon warning = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable(IconKeys.Problems));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable(TokenKeys.Warning));
        DockPanel.SetDock(warning, Dock.Left);
        conflictsPanel.Children.Add(warning);
        conflictsPanel.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = "Already assigned to:" }, new ScrollViewer { MaxHeight = 120, Content = conflictsList } },
        });
    }

    /// <summary>Keeps the shortcut; enabled once there is one.</summary>
    public Button OkButton { get; }

    /// <summary>The actions the shortcut is already assigned to, each as it is listed ("Comment in Main Menu | Code").</summary>
    public IReadOnlyList<string> Conflicts => [.. conflictsList.Children.OfType<Panel>()
        .Select(row => string.Concat(row.Children.OfType<TextBlock>().Select(t => t.Text)))];

    /// <summary>The shortcut OK closes the dialog with, or null while there is none.</summary>
    protected abstract object? Chosen { get; }

    /// <summary>Lays the dialog out: the action it is for, then the <paramref name="field"/> its shortcut is given in.</summary>
    protected void Frame(string action, string where, Control field) => Content = DialogFooter.Page(new StackPanel
    {
        Margin = new Thickness(16, 14, 16, 16),
        Spacing = 10,
        Children = { Named(action, where), field, conflictsPanel },
    }, DialogFooter.OkCancel(OkButton, () => Close(null)));

    /// <summary>The shortcut changed: OK takes it where there is one, and the actions it is already assigned to are listed.</summary>
    protected void ShortcutChanged(IReadOnlyList<(string Title, string Where)> conflicts)
    {
        OkButton.IsEnabled = Chosen is not null;
        conflictsList.Children.Clear();
        foreach ((string title, string where) in conflicts)
        {
            conflictsList.Children.Add(Named(title, where));
        }
        conflictsPanel.IsVisible = conflictsList.Children.Count > 0;
    }

    /// <summary>An action as the dialog names one: its name, then where it is, greyed ("Comment in Main Menu |
    /// Code").</summary>
    private static DockPanel Named(string title, string where)
    {
        TextBlock name = new() { Text = title };
        TextBlock place = new() { Text = $" in {where}", TextTrimming = TextTrimming.CharacterEllipsis };
        place.Classes.Add(StyleClasses.Muted);
        DockPanel.SetDock(name, Dock.Left);
        return new DockPanel { Children = { name, place } };
    }
}
