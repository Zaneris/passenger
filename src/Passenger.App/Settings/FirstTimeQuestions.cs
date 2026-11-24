using Avalonia.Controls;
using Passenger.App.State;
using Passenger.Core;

namespace Passenger.App.Settings;

/// <summary>A window's first-time questions (<see cref="FirstTimeQuestion"/>): each asked the first time it matters in its
/// scope, the repository of the window's folder or Passenger's state, and never again there once answered; its answer kept in
/// that scope's state (<see cref="FirstTimeAnswersState"/>) and shown and changed by its row in Settings, which is the
/// setting itself. A question comes on its own, so it is shown without taking the keys and with no default button, a stray
/// Enter answering nothing; never in a window nobody sees, nor for an agent's call, which waits for the person's own first
/// time. A UI-thread object.</summary>
public sealed class FirstTimeQuestions(Func<StateStore> store, Func<bool> unseen) : IDisposable
{
    /// <summary>The repository top the window's folder is in, and its documents, held while the window has the folder.</summary>
    private (string Top, ProjectState Documents)? repository;

    /// <summary>Counts the folders opened, so an opening overtaken by the next lands nothing.</summary>
    private int opened;

    /// <summary>Each question showing now, which is not asked again while it shows.</summary>
    private readonly Dictionary<string, ConfirmDialog> showing = new(StringComparer.Ordinal);

    /// <summary>An answer was given or changed, in this window or its Settings.</summary>
    public event EventHandler<FirstTimeQuestion>? Changed;

    /// <summary>The top of the repository whose answers the window keeps, its folder's own where it is in none; null with no folder.</summary>
    public string? Repository => repository?.Top;

    /// <summary>The window's folder's repository, its answers read off the window's thread; the one before let go.</summary>
    public async Task OpenAsync(string? folder)
    {
        UiThread.Check();
        LetGo();
        int opening = ++opened;
        if (folder is null)
        {
            return;
        }
        string top = await BackgroundWork.Run(() => WorkspaceTrust.FolderToTrust(folder));
        if (opening != opened)
        {
            return;
        }
        ProjectState documents = store().Hold(top);
        repository = (top, documents);
        await documents.FirstTimeAnswers.ValueAsync();
        foreach (FirstTimeQuestion question in Declared.Questions.Where(q => q.Scope == QuestionScope.Repository))
        {
            Changed?.Invoke(this, question);
        }
    }

    /// <summary>The answer given in the question's scope; null where none was, or the window has no folder for a repository's.</summary>
    public bool? Answered(FirstTimeQuestion question)
    {
        UiThread.Check();
        return Of(question)?.Value.Answers.TryGetValue(question.Id, out bool answer) == true ? answer : null;
    }

    /// <summary>What the question stands at: its answer, else its value until answered.</summary>
    public bool IsOn(FirstTimeQuestion question) => Answered(question) ?? question.Default;

    /// <summary>Keeps an answer in the question's scope, as the question, its row in Settings or its action gives it.</summary>
    public void Set(FirstTimeQuestion question, bool on)
    {
        UiThread.Check();
        if (Of(question) is not { } answers || Answered(question) == on)
        {
            return;
        }
        answers.Set(answers.Value with { Answers = new(answers.Value.Answers, StringComparer.Ordinal) { [question.Id] = on } });
        if (showing.Remove(question.Id, out ConfirmDialog? asking))
        {
            asking.Close();
        }
        Changed?.Invoke(this, question);
    }

    /// <summary>Asks the question over <paramref name="owner"/> where it has not been answered in its scope, it is not showing
    /// already, the window is one the person sees and no agent's call asks it; true where it is shown now. Its answer is kept as
    /// it is given; closing it unanswered leaves it to be asked again.</summary>
    public bool Ask(FirstTimeQuestion question, Window owner)
    {
        UiThread.Check();
        if (unseen() || AgentAsks.Beside || AgentAsks.Inputting || Of(question) is null || Answered(question) is not null || showing.ContainsKey(question.Id))
        {
            return false;
        }
        ConfirmDialog asking = new(question.Title, question.Asked, question.Yes, question.No) { EnterAgrees = false };
        showing[question.Id] = asking;
        asking.Closed += (_, _) =>
        {
            if (showing.Remove(question.Id) && asking.Answered is { } answer)
            {
                Set(question, answer);
            }
        };
        // It comes as the person works, so the keys stay where they are and no key meant for the editor answers it.
        asking.ShowActivated = false;
        asking.Show(owner);
        return true;
    }

    /// <summary>The question showing now, if it is.</summary>
    public ConfirmDialog? Showing(FirstTimeQuestion question) => showing.GetValueOrDefault(question.Id);

    /// <summary>Where the question's answer is kept: its repository's top folder's state, or Passenger's.</summary>
    private StateDocument<FirstTimeAnswersState>? Of(FirstTimeQuestion question) =>
        question.Scope == QuestionScope.State ? store().FirstTimeAnswers : repository?.Documents.FirstTimeAnswers;

    private void LetGo()
    {
        foreach (ConfirmDialog asking in showing.Values.ToArray())
        {
            asking.Close();
        }
        showing.Clear();
        if (repository is { } was)
        {
            repository = null;
            store().LetGo(was.Top);
        }
    }

    public void Dispose() => LetGo();
}
