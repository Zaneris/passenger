namespace Passenger.Languages.CSharp;

/// <summary>How many colours rainbow braces take turns in: three to six, as many as the theme in force gives, a bracket's
/// nesting depth cycling through exactly those. Read by each classification as it starts, so a key costs no more however
/// many there are; set as a theme is laid. Pure.</summary>
public static class BraceLevels
{
    /// <summary>The fewest levels, which every theme gives.</summary>
    public const int Least = 3;

    /// <summary>The most levels a theme may give.</summary>
    public const int Most = 6;

    private static int inForce = Least;

    /// <summary>The levels the theme in force gives.</summary>
    public static int InForce
    {
        get => Volatile.Read(ref inForce);
        set => Volatile.Write(ref inForce, Math.Clamp(value, Least, Most));
    }

    /// <summary>The level, from 0, of a bracket at a nesting depth (0 outermost) among <paramref name="levels"/>.</summary>
    public static int Of(int depth, int levels) => depth % Math.Clamp(levels, Least, Most);

    /// <summary>The levels given by whether each level past the third is given (<paramref name="gives"/> of 4, 5 and 6), each
    /// counting only after those before it.</summary>
    public static int Given(Func<int, bool> gives)
    {
        int levels = Least;
        while (levels < Most && gives(levels + 1))
        {
            levels++;
        }
        return levels;
    }
}
