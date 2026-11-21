using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A filter in force, shown as a chip beside what it narrows (the branch the Git log is narrowed to, the shortcut
/// the Keymap page is): its words on the chosen ground and an × that clears it, named by what it clears. Hidden while no
/// filter is in force.</summary>
public sealed class FilterChip : Border
{
    private readonly TextBlock words = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };

    /// <param name="clears">What the × is named and tipped (Clear Branch Filter).</param>
    public FilterChip(string clears)
    {
        Classes.Add(StyleClasses.Chip);
        IsVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Clear = ToolButtons.Small(IconKeys.Close, clears, () => Cleared?.Invoke(this, EventArgs.Empty));
        Clear.Margin = new Thickness(Gaps.Small, 0, 0, 0);
        Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { words, Clear } };
    }

    /// <summary>Styled as the Border it is.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>The × was pressed: the filter is to go.</summary>
    public event EventHandler? Cleared;

    /// <summary>The × that clears the filter.</summary>
    public Button Clear { get; }

    /// <summary>What the filter is, shown while it is in force; null hides the chip.</summary>
    public string? Words
    {
        get => words.Text;
        set
        {
            words.Text = value;
            IsVisible = value is not null;
        }
    }
}
