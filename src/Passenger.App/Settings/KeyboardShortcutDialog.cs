using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>The Keyboard Shortcut dialog (Add Keyboard Shortcut): the action and where it is, the first keystroke's
/// field, Second stroke and its field, and below them, as the keys are pressed, the actions the shortcut is "Already
/// assigned to". OK (or Enter once the first field holds a keystroke) closes it with the shortcut; Cancel or Escape
/// with null.</summary>
public sealed class KeyboardShortcutDialog : Window
{
    private readonly Func<Shortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf;
    private readonly StackPanel conflictsList = new() { Spacing = 2 };
    private readonly Control conflictsPanel;

    public KeyboardShortcutDialog(string action, string where, Func<Shortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf)
    {
        this.conflictsOf = conflictsOf;
        Title = "Keyboard Shortcut";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        Keys = new ShortcutPanel("Second stroke:");
        Keys.Changed += (_, _) => Update();
        OkButton = new Button { Content = "OK", IsDefault = true, Classes = { "primary" }, IsEnabled = false };
        Button cancel = new() { Content = "Cancel", IsCancel = true };
        OkButton.Click += (_, _) =>
        {
            if (Keys.Shortcut is { } shortcut)
            {
                Close(shortcut);
            }
        };
        cancel.Click += (_, _) => Close(null);

        PathIcon warning = new() { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) };
        warning.Bind(PathIcon.DataProperty, warning.GetResourceObservable(IconKeys.Problems));
        warning.Bind(PathIcon.ForegroundProperty, warning.GetResourceObservable(TokenKeys.Warning));
        DockPanel conflicts = new() { IsVisible = false, Margin = new Thickness(0, 4, 0, 0) };
        DockPanel.SetDock(warning, Dock.Left);
        conflicts.Children.Add(warning);
        conflicts.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = "Already assigned to:" }, new ScrollViewer { MaxHeight = 120, Content = conflictsList } },
        });
        conflictsPanel = conflicts;

        Content = DialogFooter.Page(new StackPanel
        {
            Margin = new Thickness(16, 14, 16, 16),
            Spacing = 10,
            Children =
            {
                Named(action, where),
                Keys,
                conflictsPanel,
            },
        }, DialogFooter.Bar(DialogFooter.Of(OkButton, cancel)));
        Opened += (_, _) => Dispatcher.UIThread.Post(() => Keys.First.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The first and second keystrokes' fields and the box between them.</summary>
    public ShortcutPanel Keys { get; }

    /// <summary>Keeps the shortcut; enabled once the first field holds a keystroke.</summary>
    public Button OkButton { get; }

    /// <summary>The actions the shortcut is already assigned to, each as it is listed ("Comment in Main Menu | Code").</summary>
    public IReadOnlyList<string> Conflicts => [.. conflictsList.Children.OfType<Panel>()
        .Select(row => string.Concat(row.Children.OfType<TextBlock>().Select(t => t.Text)))];

    private void Update()
    {
        Shortcut? shortcut = Keys.Shortcut;
        OkButton.IsEnabled = shortcut is not null;
        conflictsList.Children.Clear();
        if (shortcut is not null)
        {
            foreach ((string title, string where) in conflictsOf(shortcut))
            {
                conflictsList.Children.Add(Named(title, where));
            }
        }
        conflictsPanel.IsVisible = conflictsList.Children.Count > 0;
    }

    /// <summary>An action as the dialog names one: its name, then where it is, greyed ("Comment in Main Menu |
    /// Code").</summary>
    private static DockPanel Named(string title, string where)
    {
        TextBlock name = new() { Text = title };
        TextBlock place = new() { Text = $" in {where}", TextTrimming = TextTrimming.CharacterEllipsis };
        place.Classes.Add("muted");
        DockPanel.SetDock(name, Dock.Left);
        return new DockPanel { Children = { name, place } };
    }
}
