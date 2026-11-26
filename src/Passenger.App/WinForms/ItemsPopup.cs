using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App.WinForms;

/// <summary>A list of words' items edited as text, a line an item (a ListBox's or a ComboBox's Items), in a popup beside its
/// row: Enter starts a new line, OK gives the list the lines, Cancel or Escape leaves it as it was.</summary>
public sealed class ItemsPopup : Border
{
    public ItemsPopup(string name, IReadOnlyList<string> lines)
    {
        Classes.Add(StyleClasses.Popup);
        Width = PopupFrame.Narrow;
        Padding = PopupFrame.Inset;
        Lines = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            Height = 180,
            Text = string.Join("\n", lines),
            [Avalonia.Automation.AutomationProperties.NameProperty] = $"{name}, a line an item",
        };
        Button ok = DialogFooter.Default();
        ok.Click += (_, _) => Chosen?.Invoke(this, Lines.Text ?? string.Empty);
        StackPanel answers = DialogFooter.Popup(ok, () => Chosen?.Invoke(this, null));
        answers.HorizontalAlignment = HorizontalAlignment.Right;
        Child = new StackPanel
        {
            Spacing = Gaps.Normal,
            Children = { MutedText.Caption($"{name}: a line an item"), Lines, answers },
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape)
            {
                e.Handled = true;
                Chosen?.Invoke(this, null);
            }
        };
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Lines.Focus());
    }

    /// <summary>The box the items are written in.</summary>
    public TextBox Lines { get; }

    /// <summary>The lines given (OK), or none (Cancel, Escape).</summary>
    public event EventHandler<string?>? Chosen;
}
