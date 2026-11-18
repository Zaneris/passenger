namespace Passenger.App;

/// <summary>The one spacing scale: every margin, padding and spacing between the window's controls is one of these steps
/// (or 0, or a step's negative pulling a control back by it), never a number of its own, so that like things stand alike
/// in every window. A step is named by its size, its comment saying the role it takes first. The steps are the same in
/// every theme: a theme's look is its colours and its few shapes (Tokens.axaml), while the window's least widths are
/// measured at these. C# names them; Theme/'s styles write their numbers. UiRulesTests refuses any other number given
/// to a margin, padding, spacing or border.</summary>
public static class Gaps
{
    /// <summary>Between the lines of one item (a name over its detail) and between a bar's buttons.</summary>
    public const double Hair = 2;

    /// <summary>Between a field and what is its own (its label above it, its button beside it), and a row's ends.</summary>
    public const double Tight = 4;

    /// <summary>Between an icon and its words, and between the rows of a list of options.</summary>
    public const double Small = 6;

    /// <summary>Between controls in a row, and a list's rows' inset from its edge.</summary>
    public const double Normal = 8;

    /// <summary>Between a form's fields and a row's groups; a popup's and a card's inset.</summary>
    public const double Wide = 12;

    /// <summary>Between a page's sections, and a tool window's page's inset.</summary>
    public const double Group = 16;

    /// <summary>A dialog's page's edges.</summary>
    public const double Page = 20;

    /// <summary>The edges of a screen standing alone (the Welcome screen, the first start's).</summary>
    public const double Screen = 32;

    /// <summary>A tree's indent for each level below its top.</summary>
    public const double Indent = Group;
}
