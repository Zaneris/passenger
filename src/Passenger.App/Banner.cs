using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A line of words over what it speaks of (an editor's file, a console's program), docked at its top on the
/// banner's tint with no rule under it, its links at its right. Narrower than its words, it ends them in an ellipsis and
/// shows them whole when the pointer rests on them. Hidden until it has something to say. UiRulesTests fails a banner
/// built by hand.</summary>
public sealed class Banner : Border
{
    private readonly TextBlock words = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DockPanel line = new();

    public Banner()
    {
        DockPanel.SetDock(this, Dock.Top);
        Height = 28;
        IsVisible = false;
        line.Children.Add(words);
        Child = line;
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Banner));
    }

    /// <summary>Styled as the Border it is.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>What it says, null while it says nothing.</summary>
    public string? Said => IsVisible ? words.Text : null;

    /// <summary>Shows the banner saying <paramref name="said"/>, or hides it for null.</summary>
    public void Say(string? said)
    {
        words.Text = said;
        ToolTip.SetTip(words, said);
        IsVisible = said is not null;
    }

    /// <summary>A link at the banner's right, left of those before it, running <paramref name="run"/> when clicked.</summary>
    public Button Link(string? text, Action run)
    {
        Button link = new()
        {
            Classes = { StyleClasses.Link },
            Content = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            [DockPanel.DockProperty] = Dock.Right,
        };
        link.Click += (_, e) =>
        {
            e.Handled = true;
            run();
        };
        line.Children.Insert(line.Children.Count - 1, link);
        return link;
    }
}
