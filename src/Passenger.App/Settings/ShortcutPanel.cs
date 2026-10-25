using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>The keyboard shortcut panel of the Keyboard Shortcut dialog and the Find Shortcut popup: a field the first
/// keystroke is pressed into, the Second stroke check box, and the second keystroke's field, which takes keys only
/// while the box is checked. The keys go on to the second field once the first holds a keystroke and the box is
/// checked.</summary>
public sealed class ShortcutPanel : StackPanel
{
    public ShortcutPanel(string secondLabel)
    {
        Spacing = 6;
        First = new StrokeField(passesDialogKeys: true);
        Second = new StrokeField(passesDialogKeys: false) { IsEnabled = false };
        SecondStroke = new CheckBox { Content = secondLabel, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        DockPanel second = new();
        DockPanel.SetDock(SecondStroke, Dock.Left);
        second.Children.Add(SecondStroke);
        second.Children.Add(Second);
        Children.Add(First);
        Children.Add(second);

        First.StrokeChanged += (_, _) =>
        {
            if (SecondStroke.IsChecked == true && First.Stroke is not null)
            {
                Second.Focus();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        };
        Second.StrokeChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        SecondStroke.IsCheckedChanged += (_, _) =>
        {
            bool on = SecondStroke.IsChecked == true;
            Second.IsEnabled = on;
            (on && First.Stroke is not null ? Second : First).Focus();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The first keystroke's field.</summary>
    public StrokeField First { get; }

    /// <summary>The "Second stroke" box, which makes the shortcut a chord.</summary>
    public CheckBox SecondStroke { get; }

    /// <summary>The second keystroke's field, which takes every key, Escape, Enter and Tab too.</summary>
    public StrokeField Second { get; }

    /// <summary>The shortcut the fields hold: none until the first has a keystroke, and that keystroke alone while the box
    /// is clear or the second field is empty.</summary>
    public Shortcut? Shortcut => First.Stroke is { } first ? new Shortcut(first, SecondStroke.IsChecked == true ? Second.Stroke : null) : null;

    /// <summary>A keystroke was pressed into a field, or the box changed.</summary>
    public event EventHandler? Changed;
}

/// <summary>A shortcut's text field: a key pressed in it, with the modifiers held, is the keystroke it shows, never
/// typing. The first stroke's field lets Tab and Shift+Tab move on, plain Escape cancel and plain Enter confirm once it
/// holds a keystroke, and its + sets those keys instead; the second's takes every key.</summary>
public sealed class StrokeField : TextBox
{
    private readonly bool passesDialogKeys;
    private Keystroke? stroke;

    public StrokeField(bool passesDialogKeys)
    {
        this.passesDialogKeys = passesDialogKeys;
        IsReadOnly = true;
        Classes.Add("search");
        if (passesDialogKeys)
        {
            InnerRightContent = SpecialKeys();
        }
    }

    /// <summary>The keystroke pressed into it, or null before one is.</summary>
    public Keystroke? Stroke
    {
        get => stroke;
        set
        {
            stroke = value;
            Text = value?.Display() ?? string.Empty;
            CaretIndex = Text.Length;
            StrokeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? StrokeChanged;

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>The key is the keystroke, not an edit of the text: the text box's own handling never sees it.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool plain = e.KeyModifiers == KeyModifiers.None;
        if (passesDialogKeys && ((plain && (e.Key == Key.Escape || e.Key == Key.Tab || (e.Key == Key.Enter && stroke is not null)))
            || (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)))
        {
            return;
        }
        e.Handled = true;
        if (e.Key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.None)
        {
            return;
        }
        Stroke = Keystroke.From(e);
    }

    protected override void OnTextInput(TextInputEventArgs e) => e.Handled = true;

    /// <summary>The + in the field: "Set ⏎" and the rest, for the keys the field passes on rather than takes.</summary>
    private Button SpecialKeys()
    {
        Keystroke[] keys = [new(Key.Enter, KeyModifiers.None), new(Key.Escape, KeyModifiers.None), new(Key.Tab, KeyModifiers.None), new(Key.Tab, KeyModifiers.Shift)];
        PathIcon plus = new() { Width = 12, Height = 12 };
        plus.Bind(PathIcon.DataProperty, plus.GetResourceObservable(IconKeys.Add));
        Button set = new() { Classes = { "tabPin" }, Content = plus, Focusable = false, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(set, $"Set shortcuts with {string.Join(", ", keys.Where(k => k.Modifiers == KeyModifiers.None).Select(k => k.Display()))} keys");
        List<MenuItem> items = [];
        foreach (Keystroke key in keys)
        {
            MenuItem item = new() { Header = $"Set {key.Display()}" };
            item.Click += (_, _) =>
            {
                Stroke = key;
                Focus();
            };
            items.Add(item);
        }
        ContextMenu menu = new() { ItemsSource = items };
        set.Click += (_, _) => menu.Open(set);
        return set;
    }
}
