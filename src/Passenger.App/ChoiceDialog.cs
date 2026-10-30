using Avalonia.Controls;

namespace Passenger.App;

/// <summary>Asks which of several ways to go on: what each does, a button for each and Cancel after them
/// (DialogFooter.Listed), closing with the words of the button chosen, or null for Cancel. Escape cancels.</summary>
public sealed class ChoiceDialog : IdeWindow
{
    /// <param name="chosen">The choice Enter takes, or null for Cancel, since either answer may change the
    /// file.</param>
    public ChoiceDialog(string title, string text, IReadOnlyList<string> choices, string? chosen)
    {
        List<Button> answers = [];
        foreach (string choice in choices)
        {
            Button button = new() { Content = choice, IsDefault = choice == chosen };
            button.Click += (_, _) => Close(choice);
            answers.Add(button);
        }
        Button cancel = new() { Content = "Cancel", IsCancel = true, IsDefault = chosen is null };
        cancel.Click += (_, _) => Close(null);
        answers.Add(cancel);
        foreach (Button button in answers.Where(b => b.IsDefault))
        {
            button.Classes.Add("primary");
        }
        DialogFooter.Question(this, title, [DialogFooter.Paragraph(text)], DialogFooter.Listed([.. answers]));
    }
}
