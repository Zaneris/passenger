using Passenger.App.Settings;
using Passenger.App.Workspace;

namespace Passenger.App;

/// <summary>The window's first-time questions (<see cref="FirstTimeQuestions"/>), each asked at its moment: continuous
/// testing at the person's first run of tests in a repository, a save's tidying at their first save of a C# file there, and
/// Hot Reload at their first Run or Debug there of a program it reaches.</summary>
public partial class MainWindow
{
    private FirstTimeQuestions? questions;

    /// <summary>The window's first-time questions, each asked once in its scope.</summary>
    public FirstTimeQuestions Questions => questions ??= MakeQuestions();

    private FirstTimeQuestions MakeQuestions()
    {
        FirstTimeQuestions made = new(Store, () => Unseen, () => settings, ApplySettings);
        made.Changed += (_, question) =>
        {
            if (question == Declared.ContinuousTesting)
            {
                session.Continuous.Turn(made.IsOn(question));
            }
        };
        return made;
    }

    /// <summary>The person's own run of tests asks, the first time in the folder's repository, whether to test continuously.</summary>
    private void AskContinuousTesting() => Questions.Ask(Declared.ContinuousTesting, this);

    /// <summary>The person's first save of a C# file in the folder's repository asks whether a save tidies it, what the saved
    /// file's .editorconfig decides said as its.</summary>
    private void AskSaveFormatting(IEnumerable<OpenDocument> saved)
    {
        if (saved.FirstOrDefault(d => d.Language == Languages.Language.CSharp && !d.IsModified) is { } csharp)
        {
            Questions.Ask(Declared.SaveFormatting, this, new QuestionMoment { Decided = FirstTimeQuestions.DecidedBy(Declared.SaveFormatting, key => csharp.Conventions[key]) });
        }
    }

    /// <summary>The person's first Run or Debug in the folder's repository of a program Hot Reload reaches asks whether Run
    /// runs with it.</summary>
    private void AskHotReload()
    {
        if (Configuration is { } chosen && buildRun.ReachesHotReload(chosen))
        {
            Questions.Ask(Declared.RunWithHotReload, this);
        }
    }
}
