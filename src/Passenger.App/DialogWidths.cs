namespace Passenger.App;

/// <summary>How wide a dialog opens, one step for each kind, as a popup's are (<see cref="PopupFrame.Widths"/>): so a form
/// is one width wherever it opens and its fields end where the next form's do. UiRulesTests fails a dialog's width of its
/// own.</summary>
public static class DialogWidths
{
    /// <summary>A question, a pick from a short list, About.</summary>
    public const double Question = 520;

    /// <summary>A form of fields, a refactoring's, a publish's, a feed's.</summary>
    public const double Form = 560;

    /// <summary>A table to edit or a form with a list beside it.</summary>
    public const double Wide = 760;

    /// <summary>A workspace of a list and its page: Settings, Run Configurations, New Project.</summary>
    public const double Workspace = 980;

    public static IReadOnlyList<double> All { get; } = [Question, Form, Wide, Workspace];
}
