using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Passenger.App;

/// <summary>A line of words over what it speaks of (an editor's file, a console's program), docked at its top on the
/// banner's tint with no rule under it, its links at its right, as tall as a control. Narrower than its words, it ends
/// them in an ellipsis and shows them whole when the pointer rests on them; one that must be read whole (a previewer's
/// error, why a tree shows nothing) wraps them instead (<see cref="Wrapping"/>). Hidden until it has something to say.
/// UiRulesTests fails a banner built by hand.</summary>
public sealed class Banner : Border
{
    private readonly TextBlock words = new() { Classes = { StyleClasses.Caption }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(Gaps.Wide, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DockPanel line = new();

    public Banner()
        : this(wraps: false)
    {
    }

    private Banner(bool wraps)
    {
        DockPanel.SetDock(this, Dock.Top);
        // A wrapping banner grows a line at a time from a control's height, its lines a small step in from its edges.
        this.Bind(wraps ? MinHeightProperty : HeightProperty, this.GetResourceObservable(TokenKeys.ControlHeight));
        if (wraps)
        {
            Padding = new Thickness(0, Gaps.Small);
            words.TextWrapping = TextWrapping.Wrap;
            words.TextTrimming = TextTrimming.None;
        }
        IsVisible = false;
        line.Children.Add(words);
        Child = line;
        this.Bind(BackgroundProperty, this.GetResourceObservable(TokenKeys.Banner));
    }

    /// <summary>A banner whose words wrap to lines of their own, in the error's colour where they say a failure.</summary>
    public static Banner Wrapping(bool error = false)
    {
        Banner banner = new(wraps: true);
        if (error)
        {
            banner.words.Classes.Add(StyleClasses.Error);
        }
        return banner;
    }

    /// <summary>Styled as the Border it is.</summary>
    protected override Type StyleKeyOverride => typeof(Border);

    /// <summary>What it says, null while it says nothing.</summary>
    public string? Said => IsVisible ? words.Text : null;

    /// <summary>Shows the banner saying <paramref name="said"/>, or hides it for null.</summary>
    public void Say(string? said)
    {
        words.Text = said;
        ToolTip.SetTip(words, words.TextWrapping == TextWrapping.Wrap ? null : said);
        IsVisible = said is not null;
    }

    /// <summary>A link at the banner's right, left of those before it, running <paramref name="run"/> when clicked.</summary>
    public Button Link(string? text, Action run)
    {
        Button link = new()
        {
            Classes = { StyleClasses.Link, StyleClasses.Caption },
            Content = text,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, Gaps.Wide, 0),
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
