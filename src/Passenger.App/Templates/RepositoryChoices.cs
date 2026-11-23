using System.Text.Json.Nodes;
using Passenger.App.State;

namespace Passenger.App.Templates;

/// <summary>What New Solution makes for Git with a solution, as the person last made one: a repository, and in it each of
/// the files a repository is given (<see cref="RepositoryFiles"/>), every one of them made until the person says not.</summary>
public sealed record RepositoryChoices : IStateDocument<RepositoryChoices>
{
    public bool Repository { get; init; } = true;

    public bool GitIgnore { get; init; } = true;

    public bool GitAttributes { get; init; } = true;

    public bool EditorConfig { get; init; } = true;

    public static IReadOnlyList<Action<JsonObject>> Upgrades { get; } = [StateStore.Unversioned];

    /// <summary>Whether the repository is given the file.</summary>
    public bool Gives(RepositoryFile file) => file switch
    {
        RepositoryFile.GitIgnore => GitIgnore,
        RepositoryFile.GitAttributes => GitAttributes,
        _ => EditorConfig,
    };

    /// <summary>With the repository given the file or not.</summary>
    public RepositoryChoices Giving(RepositoryFile file, bool given) => file switch
    {
        RepositoryFile.GitIgnore => this with { GitIgnore = given },
        RepositoryFile.GitAttributes => this with { GitAttributes = given },
        _ => this with { EditorConfig = given },
    };

    public RepositoryChoices Clamped() => this;
}
