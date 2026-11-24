using System.Text.Json.Nodes;
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
/// declared once in <see cref="Declared"/>: its id, under which its answer is kept, its scope, its value until answered,
/// its row in Settings, which shows and changes the answer, and the question as asked: its title, its words, and its two
/// answers.</summary>
public sealed record FirstTimeQuestion(string Id, QuestionScope Scope, bool Default, SettingRow Row, string Title, string Asked, string Yes, string No);

/// <summary>The first-time questions answered in one scope, each answer by its question's id: a repository's, kept in its
/// top folder's state, or Passenger's own.</summary>
public sealed record FirstTimeAnswersState : IStateDocument<FirstTimeAnswersState>
{
    public Dictionary<string, bool> Answers { get; init; } = [];

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    public FirstTimeAnswersState Clamped() => this with { Answers = Answers ?? [] };
}
