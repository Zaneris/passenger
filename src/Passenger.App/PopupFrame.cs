using Avalonia;
using Avalonia.Controls;

namespace Passenger.App;

/// <summary>What every popup over the window shares (Find Action, Go to File, Find in Files, a Git choice, a
/// breakpoint's properties): the frame (Shell.axaml's <c>Border.popup</c>), a width from one scale, the title's look
/// (<c>TextBlock.popupTitle</c>), and its list's rows of <see cref="ListRow.Height"/>. A popup takes its width from
/// here, never a number of its own.</summary>
public static class PopupFrame
{
    /// <summary>A name to type (New Branch, Stash Changes).</summary>
    public const double Narrow = 360;

    /// <summary>A short list to choose from or a few fields (a Git choice, Unstash, a breakpoint's properties).</summary>
    public const double Medium = 560;

    /// <summary>A search over the solution (Search Everywhere's Find Action, Go to File and Symbol, Recent Locations).</summary>
    public const double Wide = 720;

    /// <summary>A search with a preview or a list beside its details (Find in Files, Breakpoints).</summary>
    public const double Widest = 860;

    /// <summary>Every width a popup may have.</summary>
    public static IReadOnlyList<double> Widths { get; } = [Narrow, Medium, Wide, Widest];

    /// <summary>A popup as it shows, in the frame: itself where it is a frame or holds one as its content, else in a frame
    /// made round it (the one made when it was shown before).</summary>
    public static Control Framed(Control popup) =>
        IsFrame(popup) || popup is ContentControl { Content: Control held } && IsFrame(held) ? popup
        : popup.Parent is Border made && IsFrame(made) ? made
        : new Border { Classes = { StyleClasses.Popup }, Padding = new Thickness(12, 10), Child = popup };

    /// <summary>The frame a popup shows in, as <see cref="Framed"/> has it shown: itself, its content, or what holds it.</summary>
    public static Border? FrameOf(Control popup) =>
        popup as Border is { } frame && IsFrame(frame) ? frame
        : popup is ContentControl { Content: Border held } && IsFrame(held) ? held
        : popup.Parent as Border is { } round && IsFrame(round) ? round
        : null;

    private static bool IsFrame(Control control) => control is Border && control.Classes.Contains(StyleClasses.Popup);
}
