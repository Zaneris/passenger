using Avalonia.Controls;
using Avalonia.Layout;

namespace Passenger.App;

/// <summary>A form's rows of less weight folded under a link that opens and closes them ("More options (21)"), its
/// chevron turned down while they show. The rows go in <see cref="Rows"/>; the form adds <see cref="Opener"/> and
/// <see cref="Rows"/> where they belong, within its <see cref="FormRows.Scope"/> so the rows share its labels' column.</summary>
public sealed class FormFold
{
    private readonly RowIcon chevron = RowIcon.Chevron();
    private readonly TextBlock title = new();

    public FormFold()
    {
        chevron.ForegroundKey = TokenKeys.Link;
        Opener = new Button
        {
            Classes = { StyleClasses.Link },
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { chevron, title } },
        };
        Opener.Click += (_, _) => IsOpen = !IsOpen;
    }

    /// <summary>The link that opens and closes the rows.</summary>
    public Button Opener { get; }

    /// <summary>The folded rows, shown while open.</summary>
    public StackPanel Rows { get; } = new() { Spacing = 10, IsVisible = false };

    /// <summary>What the link says.</summary>
    public string Title
    {
        get => title.Text ?? string.Empty;
        set => title.Text = value;
    }

    public bool IsOpen
    {
        get => Rows.IsVisible;
        set
        {
            Rows.IsVisible = value;
            chevron.Turned = value;
        }
    }
}
