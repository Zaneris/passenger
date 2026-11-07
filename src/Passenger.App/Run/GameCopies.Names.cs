namespace Passenger.App.Run;

/// <summary>A copy of a game: the configuration that starts it, by its name, and its place among the copies, from one.</summary>
public sealed record GameCopy(string Configuration, int Place);

/// <summary>The copies' names and own arguments, apart from the plans they make.</summary>
public static partial class GameCopies
{
    /// <summary>The most copies one configuration starts.</summary>
    public const int Most = 16;

    /// <summary>A copy's name, from one: the configuration's own for the first, then its place, said at more length where
    /// another configuration goes by that name (<paramref name="others"/>), as a run's tab is found by its name.</summary>
    public static string Name(string configuration, int copy, IEnumerable<string>? others = null)
    {
        string name = copy <= 1 ? configuration : $"{configuration} #{copy}";
        HashSet<string> taken = [.. others ?? []];
        for (string longer = $"{configuration} (copy {copy})"; copy > 1 && taken.Contains(name); longer += $" (copy {copy})")
        {
            name = longer;
        }
        return name;
    }

    /// <summary>The copy a program of a name was started as: what its plan says, else the first of the configuration its name
    /// is. Never read from the name, which a person's own configuration may also end in a number.</summary>
    public static GameCopy Of(GameCopy? told, string name) => told ?? new GameCopy(name, 1);

    /// <summary>A copy's own arguments (from one), its line of the configuration's, none for one past them.</summary>
    public static IReadOnlyList<string> Own(IReadOnlyList<string> copyArguments, int copy) =>
        CommandLine.Split(copy >= 1 && copy <= copyArguments.Count ? copyArguments[copy - 1] : string.Empty);
}
