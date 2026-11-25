using Avalonia.Controls;

namespace Passenger.App.Settings;

/// <summary>The first-time questions' rows: each the question's answer in the window's scope (its repository's, or
/// Passenger's), its value until answered where none was given, changed as Apply and OK put the other settings in force.</summary>
public sealed partial class SettingsDialog
{
    private readonly FirstTimeQuestions? questions;

    /// <summary>Each question's answer as its row has it, where changed since the dialog opened or last applied.</summary>
    private readonly Dictionary<FirstTimeQuestion, bool> answering = [];

    /// <summary>A question's row, where it keeps its answer: a check box of its answer, greyed, saying why, where its scope is
    /// a repository and the window has no folder.</summary>
    private void Add(FirstTimeQuestion question)
    {
        if (question.Row is not { } row)
        {
            return;
        }
        CheckBox box = new() { IsChecked = questions?.IsOn(question) ?? question.Default };
        box.IsCheckedChanged += (_, _) => answering[question] = box.IsChecked == true;
        if (question.Scope == QuestionScope.Repository && questions?.Repository is null)
        {
            ToolTip.SetShowOnDisabled(box, true);
            ToolButtons.Grey(box, "Open a folder to choose this for its repository");
        }
        Add(row.Page, row.Label, box, row.Words, row.Note);
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
