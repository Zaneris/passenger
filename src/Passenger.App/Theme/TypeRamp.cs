namespace Passenger.App;

/// <summary>The one type ramp: every size the window's words take is one of these steps, each a class of Shell.axaml that
/// says its role (a caption, a badge's letters, code in a pane, a heading), never a number of its own, so that like words
/// stand alike in every window. Words given none are the body's, Passenger.BodySize, which Fluent's controls take too.
/// Theme/'s styles write the steps' numbers; C# names a step only where a control draws its own text and takes no class
/// (a table's cells, an editor shown in a pane). UiRulesTests refuses any other size.</summary>
public static class TypeRamp
{
    /// <summary>A badge's letters (<c>badgeText</c>, bold): a kind's, a method's, a project's initials.</summary>
    public const double Badge = 11;

    /// <summary>The muted note under a field, a row's detail, a tag's words, the status bar's (<c>caption</c>).</summary>
    public const double Caption = 12;

    /// <summary>Code shown in a pane (<c>code</c>, in the code font): a console's, a card's, a value's, a list of names.
    /// A step of the pane's, not the editor's size, so a pane's rows hold it whatever size the person reads code at.</summary>
    public const double Code = 12;

    /// <summary>A pane's or a popup's heading (<c>paneTitle</c>, semibold), and the words of a tool window's dense
    /// controls: its tabs, its search field, a table's cell.</summary>
    public const double Dense = 13;

    /// <summary>The window's words and a dialog's title (<c>dialogTitle</c>, semibold).</summary>
    public const double Body = 13;

    /// <summary>A tool window's title (<c>toolTitle</c>, semibold), a step above its tabs and its panes' headings.</summary>
    public const double Title = 14;

    /// <summary>A page's heading (<c>pageTitle</c>, semibold): a Settings page's, a template's, a package's.</summary>
    public const double Page = 16;

    /// <summary>The title of a screen beside Passenger's mark (<c>heroTitle</c>, semibold): the Welcome screen's, What's
    /// New's, About's, the editors' with nothing open.</summary>
    public const double Hero = 28;
}
