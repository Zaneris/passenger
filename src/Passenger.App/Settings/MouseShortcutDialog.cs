using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>The Mouse Shortcut dialog (Add Mouse Shortcut): the action and where it is, a box the gesture is pressed into
/// (any button, the modifiers held, a double-click taken as one), the gesture as it is shown, and the actions it is
/// "Already assigned to". OK closes it with the gesture once one is pressed; Cancel or Escape with null.</summary>
public sealed class MouseShortcutDialog : IdeWindow
{
    private readonly Func<MouseShortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf;
    private readonly StackPanel conflictsList = new() { Spacing = 2 };
    private readonly Control conflictsPanel;
    private readonly TextBlock shown = new() { HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeight.SemiBold };

    public MouseShortcutDialog(string action, string where, Func<MouseShortcut, IReadOnlyList<(string Title, string Where)>> conflictsOf)
    {
        this.conflictsOf = conflictsOf;
        Title = "Mouse Shortcut";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        OkButton = DialogFooter.Default();
        OkButton.IsEnabled = false;
        OkButton.Click += (_, _) =>
        {
            if (Gesture is { } gesture)
            {
                Close(gesture);
            }
        };

        TextBlock asked = new() { Text = "Click or double-click here with the modifiers held", HorizontalAlignment = HorizontalAlignment.Center };
        asked.Classes.Add(StyleClasses.Muted);
        // Transparent, so a press anywhere in the box is the box's.
        Pad = new Border
        {
            Classes = { StyleClasses.Box },
            Height = 88,
            Background = Brushes.Transparent,
            Child = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { asked, shown } },
        };
        Pad.PointerPressed += (_, e) =>
        {
            if (MouseShortcut.Of(e) is { Button: not MouseButton.None } pressed)
            {
                Take(pressed);
            }
            e.Handled = true;
        };

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
                KeyboardShortcutDialog.Named(action, where),
                Pad,
                conflictsPanel,
            },
        }, DialogFooter.OkCancel(OkButton, () => Close(null)));
    }

    /// <summary>The box a gesture is pressed into.</summary>
    public Border Pad { get; }

    /// <summary>Keeps the gesture; enabled once one is pressed.</summary>
    public Button OkButton { get; }

    /// <summary>The gesture pressed last, or null before any.</summary>
    public MouseShortcut? Gesture { get; private set; }

    /// <summary>The gesture as the box shows it ("⌥Click"), empty before any.</summary>
    public string Shown => shown.Text ?? string.Empty;

    /// <summary>The actions the gesture is already assigned to, each as it is listed ("Go to Declaration in Main Menu |
    /// Navigate").</summary>
    public IReadOnlyList<string> Conflicts => [.. conflictsList.Children.OfType<Panel>()
        .Select(row => string.Concat(row.Children.OfType<TextBlock>().Select(t => t.Text)))];

    /// <summary>A gesture pressed in the box: shown, its conflicts said, and OK let take it. A second click of a double-click
    /// comes as the double-click.</summary>
    private void Take(MouseShortcut pressed)
    {
        Gesture = pressed with { Clicks = Math.Min(2, pressed.Clicks) };
        shown.Text = Gesture.Value.Display();
        OkButton.IsEnabled = true;
        conflictsList.Children.Clear();
        foreach ((string title, string where) in conflictsOf(Gesture.Value))
        {
            conflictsList.Children.Add(KeyboardShortcutDialog.Named(title, where));
        }
        conflictsPanel.IsVisible = conflictsList.Children.Count > 0;
    }
}
