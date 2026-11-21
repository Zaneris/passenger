using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App;

/// <summary>A tool window's drop-down, every one made here: a bar's chooser (NuGet's feeds, the Debug window's threads,
/// a migration's project), a popup's scope (Find in Files'), a value chosen in a row (a Godot property's). Its words stand
/// at the dense step of the search field beside it, as tall as the bar's other controls (Shell.axaml's
/// <c>ComboBox.compact</c>), at least <see cref="Least"/> where its row has the room and, at its own width, never wider
/// than a form's drop-down (Passenger.DropDownWidth), its choice ending in an ellipsis there; holding no choice, it says
/// what it would hold (its placeholder). <see cref="Labelled"/> puts words before it that a screen reader says for it.</summary>
public class ToolDropDown : ComboBox
{
    /// <summary>The narrowest it stands where its row has the room, so a short or missing choice leaves its bar as it was.</summary>
    public const double Least = 120;

    /// <param name="placeholder">What it says while it holds no choice.</param>
    public ToolDropDown(string? placeholder = null)
    {
        Classes.Add(StyleClasses.Compact);
        PlaceholderText = placeholder;
    }

    /// <summary>Styled as the drop-down it is.</summary>
    protected override Type StyleKeyOverride => typeof(ComboBox);

    /// <summary>Words before a drop-down, which name it to a screen reader; with too little room the drop-down gives way.</summary>
    public static Control Labelled(string label, ComboBox box)
    {
        TextBlock words = new() { Text = label, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLabeledBy(box, words);
        DockPanel.SetDock(words, Dock.Left);
        return new DockPanel { HorizontalSpacing = Gaps.Small, Children = { words, box } };
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double room = availableSize.Width;
        // Asked no wider than a form's drop-down; a stretched one is still laid out across its cell or row.
        if (this.TryFindResource(TokenKeys.DropDownWidth, out object? found) && found is double most)
        {
            availableSize = availableSize.WithWidth(Math.Min(room, most));
        }
        Size own = base.MeasureOverride(availableSize);
        return own.WithWidth(Math.Max(own.Width, Math.Min(Least, room)));
    }
}
