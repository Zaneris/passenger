using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Passenger.Editor;

namespace Passenger.App;

/// <summary>Every tag and badge beside what a row or a page says, made one way (Shell.axaml's <c>Border.tag</c>,
/// <c>Border.kindBadge</c> and <c>Border.initials</c>): a tag's words at the caption's step, muted unless it is given a
/// colour, on a ground only where it is given one, a method's alone bold; a symbol kind's letter on its colour; a solution's
/// initials. UiRulesTests refuses their classes named anywhere else.</summary>
public static class Tags
{
    /// <summary>How wide and tall a symbol kind's badge is, in every list.</summary>
    public const double KindSize = 16;

    /// <summary>A tag of words (transitive, a Git ref, a health, Not installed): muted, else in <paramref name="colour"/>'s
    /// token; on <paramref name="ground"/>'s token where it is given one. Its words end in an ellipsis where it is held
    /// narrower than they are.</summary>
    public static Border Of(string? words, string? colour = null, string? ground = null)
    {
        TextBlock text = new() { Classes = { StyleClasses.Caption }, Text = words, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        if (colour is null)
        {
            text.Classes.Add(StyleClasses.Muted);
        }
        else
        {
            text.Bind(TextBlock.ForegroundProperty, text.GetResourceObservable(colour));
        }
        Border tag = new() { Classes = { StyleClasses.Tag }, VerticalAlignment = VerticalAlignment.Center, Child = text };
        if (ground is not null)
        {
            tag.Bind(Border.BackgroundProperty, tag.GetResourceObservable(ground));
        }
        return tag;
    }

    /// <summary>An HTTP method's tag: its name in the code font, bold at the badge's step and centred, every method's tag one
    /// least width so the paths after them line up; its colour set by the row (<see cref="WordsOf"/>).</summary>
    public static Border Method(string method)
    {
        TextBlock text = new TextBlock { Classes = { StyleClasses.BadgeText }, Text = method, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }.InCodeFont();
        return new Border { Classes = { StyleClasses.Tag }, MinWidth = MethodWidth, VerticalAlignment = VerticalAlignment.Center, Child = text };
    }

    /// <summary>The least width of a method's tag: DELETE's.</summary>
    private const double MethodWidth = 52;

    /// <summary>The words a tag shows, for a row to change.</summary>
    public static TextBlock WordsOf(Border tag) => (TextBlock)tag.Child!;

    /// <summary>A symbol kind's badge (Go to Symbol, completion): its letter in the badges' text colour, on a ground the row
    /// gives by its kind.</summary>
    public static Border Kind(out TextBlock letter)
    {
        letter = new TextBlock { Classes = { StyleClasses.BadgeText }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        letter.Bind(TextBlock.ForegroundProperty, letter.GetResourceObservable(TokenKeys.BadgeText));
        return new Border { Width = KindSize, Height = KindSize, Classes = { StyleClasses.KindBadge }, VerticalAlignment = VerticalAlignment.Center, Child = letter };
    }

    /// <summary>A solution's initials on a badge (a recent one's on the Welcome screen).</summary>
    public static Border Initials(out TextBlock letters)
    {
        letters = new TextBlock { Classes = { StyleClasses.BadgeText } };
        return new Border { Classes = { StyleClasses.Initials }, VerticalAlignment = VerticalAlignment.Center, Child = letters };
    }
}
