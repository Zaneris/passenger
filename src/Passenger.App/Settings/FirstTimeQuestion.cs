using System.Text.Json.Nodes;
using Avalonia.Controls;
using Passenger.App.State;

namespace Passenger.App.Settings;

/// <summary>Where a first-time question's answer stands: for the repository a folder is in (keyed by its top, as
/// <see cref="Passenger.Core.WorkspaceTrust.FolderToTrust"/> finds it, a folder in none its own), or for the whole of
/// Passenger's state.</summary>
public enum QuestionScope
{
    Repository,
    State,
}

/// <summary>A choice Passenger asks the person about the first time it matters in its scope (<see cref="FirstTimeQuestions"/>),
/// declared once in <see cref="Declared"/>: its id, under which it is kept as asked, its scope, and the question as asked: its
/// title, its words, and its two answers. Its answer is one of three: kept by the question itself, its value until answered
/// <see cref="Default"/> and its row in Settings <see cref="Row"/>; the settings of <see cref="Settings"/>, whose own rows show
/// it; or, with neither, what its Yes does where it is asked (<see cref="QuestionMoment.Yes"/>).</summary>
public sealed record FirstTimeQuestion(string Id, QuestionScope Scope, string Title, string Asked, string Yes, string No)
{
    /// <summary>The answer's value until it is given, where the question keeps it.</summary>
    public bool Default { get; init; }

    /// <summary>The row in Settings that shows and changes the answer, where the question keeps it.</summary>
    public SettingRow? Row { get; init; }

    /// <summary>The settings the answer is: Yes turns on each its box leaves ticked (one setting alone has no box), No leaves
    /// them as they stand; asked only while one of them is off.</summary>
    public IReadOnlyList<ToggleSetting> Settings { get; init; } = [];

    /// <summary>Whether the question keeps its answer itself, rather than in settings or by what its Yes does.</summary>
    public bool KeepsAnswer => Row is not null;
}

/// <summary>What a question is asked with at its moment, beyond what it declares: its words where they name what was found
/// (a key, a file), its Yes's where they differ, a control under them, the settings a file's .editorconfig decides (each
/// with the words that say so, in place of its box), and what Yes does beyond keeping the answer.</summary>
public sealed record QuestionMoment
{
    public string? Asked { get; init; }

    public string? YesWords { get; init; }

    public Control? Detail { get; init; }

    public IReadOnlyDictionary<ToggleSetting, string> Decided { get; init; } = new Dictionary<ToggleSetting, string>();

    public Func<Task>? Yes { get; init; }
}

/// <summary>The first-time questions answered in one scope: each kept answer by its question's id, and the ids of those asked
/// whose answer is a setting or an act: a repository's, kept in its top folder's state, or Passenger's own.</summary>
public sealed record FirstTimeAnswersState : IStateDocument<FirstTimeAnswersState>
{
    public Dictionary<string, bool> Answers { get; init; } = [];

    public List<string> Asked { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public FirstTimeAnswersState Clamped() => this with { Answers = Answers ?? [], Asked = Asked ?? [] };
}
