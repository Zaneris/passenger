using Avalonia.Controls;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.App.Settings;

/// <summary>A window's first-time questions (<see cref="FirstTimeQuestion"/>): each asked the first time it matters in its
/// scope, the repository of the window's folder or Passenger's state, and never again there once answered, as that scope's
/// state keeps (<see cref="FirstTimeAnswersState"/>); its answer the setting Settings shows, or what its Yes did. A question
/// comes on its own, so it is shown without taking the keys and with no default button, a stray Enter answering nothing;
/// never in a window nobody sees, nor for an agent's call, which waits for the person's own first time. A UI-thread
/// object.</summary>
public sealed class FirstTimeQuestions(Func<StateStore> store, Func<bool> unseen, Func<AppSettings> settings, Action<AppSettings> apply) : IDisposable
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

    /// <summary>The answer a question that keeps its own was given in its scope; null where none was, or the window has no
    /// folder for a repository's.</summary>
    public bool? Answered(FirstTimeQuestion question)
    {
        UiThread.Check();
        return Of(question)?.Value.Answers.TryGetValue(question.Id, out bool answer) == true ? answer : null;
    }

    /// <summary>Whether the question was answered in its scope, whatever the answer was.</summary>
    public bool WasAsked(FirstTimeQuestion question) =>
        Answered(question) is not null || Of(question)?.Value.Asked.Contains(question.Id, StringComparer.Ordinal) == true;

    /// <summary>What the question stands at: its settings all on, else its answer, else its value until answered.</summary>
    public bool IsOn(FirstTimeQuestion question) =>
        question.Settings.Count > 0 ? question.Settings.All(s => s.Of(settings())) : Answered(question) ?? question.Default;

    /// <summary>Keeps an answer in the question's scope, as the question, its row in Settings or its action gives it.</summary>
    public void Set(FirstTimeQuestion question, bool on)
    {
        UiThread.Check();
        if (!question.KeepsAnswer || Of(question) is not { } answers || Answered(question) == on)
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

    /// <summary>Whether the question would be shown now: it was not answered in its scope, it is not showing already, its
    /// settings are not all on, the window is one the person sees and no agent's call asks it.</summary>
    public bool WouldAsk(FirstTimeQuestion question) =>
        !unseen() && !AgentAsks.Beside && !AgentAsks.Inputting && Of(question) is not null && !WasAsked(question) && !showing.ContainsKey(question.Id)
        && (question.Settings.Count == 0 || !IsOn(question));

    /// <summary>Asks the question over <paramref name="owner"/> where it <see cref="WouldAsk"/>, with what its moment found;
    /// true where it is shown now. Its answer is kept as it is given: its own, its settings each as its box says on a Yes, or
    /// what its Yes does; closing it unanswered leaves it to be asked again.</summary>
    public bool Ask(FirstTimeQuestion question, Window owner, QuestionMoment? moment = null)
    {
        UiThread.Check();
        if (!WouldAsk(question))
        {
            return false;
        }
        moment ??= new QuestionMoment();
        List<(ToggleSetting Setting, CheckBox? Box)> chosen = [.. question.Settings.Select(s => (s, question.Settings.Count > 1 && !moment.Decided.ContainsKey(s)
            ? new CheckBox { [ShownWords.ContentProperty] = s.Row?.Label, IsChecked = true } : null))];
        StackPanel detail = new() { Spacing = Gaps.Small };
        foreach ((ToggleSetting setting, CheckBox? box) in chosen)
        {
            if (box is not null)
            {
                detail.Children.Add(box);
            }
            else if (moment.Decided.TryGetValue(setting, out string? decided))
            {
                detail.Children.Add(Said(decided));
            }
        }
        if (moment.Detail is { } more)
        {
            detail.Children.Add(more);
        }
        ConfirmDialog asking = new(question.Title, moment.Asked ?? question.Asked, moment.YesWords ?? question.Yes, question.No,
            detail.Children.Count > 0 ? detail : null) { EnterAgrees = false };
        showing[question.Id] = asking;
        asking.Closed += (_, _) =>
        {
            if (showing.Remove(question.Id) && asking.Answered is { } answer)
            {
                Answer(question, answer, [.. chosen.Where(c => !moment.Decided.ContainsKey(c.Setting) && (c.Box is null || c.Box.IsChecked == true)).Select(c => c.Setting)]);
                if (answer && moment.Yes is { } yes)
                {
                    IdeErrors.Observe(yes());
                }
            }
        };
        // It comes as the person works, so the keys stay where they are and no key meant for the editor answers it.
        asking.ShowActivated = false;
        asking.Show(owner);
        return true;
    }

    /// <summary>An answer given: kept where the question keeps its own, else the question kept as asked in its scope and, on a
    /// Yes, the settings ticked turned on.</summary>
    private void Answer(FirstTimeQuestion question, bool answer, IReadOnlyList<ToggleSetting> ticked)
    {
        if (question.KeepsAnswer)
        {
            Set(question, answer);
            return;
        }
        if (Of(question) is { } answers && !answers.Value.Asked.Contains(question.Id, StringComparer.Ordinal))
        {
            answers.Set(answers.Value with { Asked = [.. answers.Value.Asked, question.Id] });
        }
        if (answer && ticked.Count > 0)
        {
            apply(ticked.Aggregate(settings(), (s, setting) => setting.With(s, true)));
        }
        Changed?.Invoke(this, question);
    }

    /// <summary>The question's settings a file's .editorconfig decides (<see cref="EditorConfigOver"/>), each with the words
    /// saying so, which its question shows in place of the setting's box.</summary>
    public static IReadOnlyDictionary<ToggleSetting, string> DecidedBy(FirstTimeQuestion question, Func<string, string?> conventions)
    {
        Dictionary<ToggleSetting, string> decided = [];
        foreach (ToggleSetting setting in question.Settings)
        {
            if (setting.Row is { Over: { } over } row && over.SetIn(conventions) is { } set)
            {
                decided[setting] = $"{row.Label.English}: this file's .editorconfig sets {set}, which every save follows.";
            }
        }
        return decided;
    }

    /// <summary>Words under a question saying why one of its settings is not its to choose.</summary>
    private static TextBlock Said(string words)
    {
        TextBlock said = MutedText.Of(words);
        said.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        return said;
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
