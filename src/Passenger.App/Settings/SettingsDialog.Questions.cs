using Avalonia.Controls;

namespace Passenger.App.Settings;

/// <summary>The first-time questions' rows: each the question's answer in the window's scope (its repository's, or
/// Passenger's), its value until answered where none was given, changed as Apply and OK put the other settings in force.</summary>
public sealed partial class SettingsDialog
{
    private readonly FirstTimeQuestions? questions;

    /// <summary>Each question's answer as its row has it, where changed since the dialog opened or last applied.</summary>
    private readonly Dictionary<FirstTimeQuestion, bool> answering = [];

    /// <summary>A question's row: a check box of its answer, greyed, saying why, where its scope is a repository and the
    /// window has no folder.</summary>
    private void Add(FirstTimeQuestion question)
    {
        CheckBox box = new() { IsChecked = questions?.IsOn(question) ?? question.Default };
        box.IsCheckedChanged += (_, _) => answering[question] = box.IsChecked == true;
        if (question.Scope == QuestionScope.Repository && questions?.Repository is null)
        {
            box.IsEnabled = false;
            ToolTip.SetShowOnDisabled(box, true);
            ToolTip.SetTip(box, "Open a folder to choose this for its repository");
        }
        Add(question.Row.Page, question.Row.Label, box, question.Row.Words, question.Row.Note);
    }

    private void ApplyQuestions()
    {
        foreach ((FirstTimeQuestion question, bool on) in answering)
        {
            questions?.Set(question, on);
        }
        answering.Clear();
    }
}
