using Avalonia.Controls;

namespace Passenger.App;

/// <summary>The Open Solution question, asked as a solution or folder is opened from a window that has one: "Where
/// would you like to open the solution 'Name'?", This Window (the default), New Window and Cancel, in that order, and
/// Don't ask again, which makes the answer Settings' Open project in. Escape cancels.</summary>
public sealed class OpenProjectDialog : IdeWindow
{
    public const string ThisWindow = "This Window";
    public const string NewWindow = "New Window";

    private readonly CheckBox remember = new() { Content = "Don't ask again" };

    public OpenProjectDialog(string name)
    {
        Question = $"Where would you like to open the solution '{name}'?";
        Button here = DialogFooter.Default(ThisWindow);
        Button apart = new() { Content = NewWindow };
        Button cancel = DialogFooter.Cancel(Close);
        here.Click += (_, _) => Choose(OpenTarget.ThisWindow);
        apart.Click += (_, _) => Choose(OpenTarget.NewWindow);
        DialogFooter.Question(this, "Open Solution", [DialogFooter.Paragraph(Question)], DialogFooter.Answers(here, apart, cancel), remember);
    }

    /// <summary>What it asks.</summary>
    public string Question { get; }

    /// <summary>Where the person chose to open it, or null once cancelled (or while it is still asking).</summary>
    public OpenTarget? Chosen { get; private set; }

    /// <summary>Whether Don't ask again is ticked, which keeps the answer, never a Cancel.</summary>
    public bool Remember => remember.IsChecked == true;

    private void Choose(OpenTarget target)
    {
        Chosen = target;
        Close();
    }
}
