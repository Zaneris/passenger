using Avalonia.Controls;

namespace Passenger.App;

/// <summary>Asks which of several ways to go on: what each does, a button for each and Cancel after them
/// (DialogFooter.Listed), a way that cannot be taken greyed saying why, what it applies to under the question where it
/// says (a checkout's files), and, where it offers one, Don't ask again; closing with the words of the button chosen, or
/// null for Cancel. Escape cancels.</summary>
public sealed class ChoiceDialog : IdeWindow
{
    private readonly CheckBox? notAgain;

    /// <param name="chosen">The choice Enter takes, or null for Cancel, since either answer may change the
    /// file.</param>
    /// <param name="detail">What follows the question's words: what it applies to, a box choosing more of the way.</param>
    /// <param name="refused">Why each way that cannot be taken cannot, by its words.</param>
    public ChoiceDialog(string title, string text, IReadOnlyList<string> choices, string? chosen, bool offersNotAgain = false,
        IReadOnlyList<Control>? detail = null, IReadOnlyDictionary<string, string>? refused = null)
    {
        List<Button> answers = [];
        foreach (string choice in choices)
        {
            Button button = new() { Content = choice, IsDefault = choice == chosen };
            button.Click += (_, _) => Close(choice);
            ToolButtons.Grey(button, refused?.GetValueOrDefault(choice));
            answers.Add(button);
        }
        Button cancel = DialogFooter.Cancel(() => Close(null));
        cancel.IsDefault = chosen is null;
        answers.Add(cancel);
        foreach (Button button in answers.Where(b => b.IsDefault))
        {
            button.Classes.Add(StyleClasses.Primary);
        }
        notAgain = offersNotAgain ? DialogFooter.DontAskAgain() : null;
        DialogFooter.Question(this, title, [DialogFooter.Paragraph(text), .. detail ?? []], DialogFooter.Listed([.. answers]), notAgain);
    }

    /// <summary>Whether Don't ask again is ticked, which the asker keeps only as a way on is chosen, never on a Cancel.</summary>
    public bool NotAgain => notAgain?.IsChecked == true;
}
