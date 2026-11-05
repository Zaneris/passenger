namespace Passenger.App.Run;

/// <summary>The copies' names and own arguments, apart from the plans they make.</summary>
public static partial class GameCopies
{
    /// <summary>The most copies one configuration starts.</summary>
    public const int Most = 16;

    /// <summary>A copy's name, from one: the configuration's own for the first.</summary>
    public static string Name(string configuration, int copy) => copy <= 1 ? configuration : $"{configuration} #{copy}";

    /// <summary>The configuration a copy's name is of, and its place from one: a name of no copy past the first is its own, the first.</summary>
    public static (string Configuration, int Copy) Of(string name)
    {
        int mark = name.LastIndexOf(" #", StringComparison.Ordinal);
        return mark > 0 && int.TryParse(name.AsSpan(mark + 2), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int copy) && copy >= 2
            ? (name[..mark], copy)
            : (name, 1);
    }

    /// <summary>A copy's own arguments (from one), its line of the configuration's, none for one past them.</summary>
    public static IReadOnlyList<string> Own(IReadOnlyList<string> copyArguments, int copy) =>
        CommandLine.Split(copy >= 1 && copy <= copyArguments.Count ? copyArguments[copy - 1] : string.Empty);
}
