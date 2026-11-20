using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A tree row's chevron in its column, one way in every tree: the column <see cref="Column"/> wide and the row's
/// height, the chevron (<see cref="RowIcon.Chevron"/>) in its middle, turned down while the row is open, as the Explorer's
/// <see cref="RowGlyphs"/> draws it. A row with nothing under it keeps the column, its chevron hidden, so its words stand
/// in line with its siblings'. A press anywhere on the column opens or closes the row, a second soon after doing so again,
/// never the list's double click, which acts on the row chosen.</summary>
public sealed class RowExpander : Border
{
    /// <summary>The column's width, the chevron's square in its middle.</summary>
    public const double Column = 18;

    private readonly Action toggle;

    /// <summary>A column whose press calls <paramref name="toggled"/>.</summary>
    public RowExpander(Action toggled)
    {
        toggle = toggled;
        Width = Column;
        Height = ListRow.Height;
        VerticalAlignment = VerticalAlignment.Center;
        // Its whole column takes a press, not only the chevron's thin strokes.
        Background = Brushes.Transparent;
        Child = Chevron;
        DoubleTapped += (_, e) => e.Handled |= Expands;
    }

    /// <summary>A column whose press opens or closes the row it shows, the <typeparamref name="T"/> it holds as its data.</summary>
    public static RowExpander Of<T>(Action<T> toggle)
    {
        RowExpander made = null!;
        made = new RowExpander(() =>
        {
            if (made.DataContext is T row)
            {
                toggle(row);
            }
        });
        return made;
    }

    /// <summary>The chevron it draws.</summary>
    public RowIcon Chevron { get; } = RowIcon.Chevron();

    /// <summary>Whether the row has rows under it, so its chevron shows; the column stands either way.</summary>
    public bool Expands
    {
        get => Chevron.IsVisible;
        set => Chevron.IsVisible = value;
    }

    /// <summary>Whether the row is open, its chevron turned down.</summary>
    public bool IsOpen
    {
        get => Chevron.Turned;
        set => Chevron.Turned = value;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!Expands || e.Handled)
        {
            return;
        }
        // The press's capture let go first, or rows moved under the pointer would take the next press from the chevron.
        e.Pointer.Capture(null);
        toggle();
        e.Handled = true;
    }
}
