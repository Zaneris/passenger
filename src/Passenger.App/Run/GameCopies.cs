namespace Passenger.App.Run;

/// <summary>The copies of a Godot game one configuration starts side by side (<see cref="RunConfiguration.GodotCopies"/>), and
/// one more launched beside those running: each a run of its own, named for its configuration and its place among them ("Arena",
/// "Arena #2"), followed in the Live Visual Tree and debugged in a tab of its own, with its own console; the first keeps
/// the configuration's name, so one game alone is as it always was. Stop ends the copies together.</summary>
public static partial class GameCopies
{
    /// <summary>How many copies a configuration's launch starts: its own count for a Godot project's game, else one.</summary>
    public static int Count(RunConfiguration configuration, LaunchPlan plan) =>
        plan.Godot is not null && !plan.GodotEditor ? Math.Clamp(configuration.GodotCopies, 1, Most) : 1;

    /// <summary>A copy's plan (from one): the configuration's, named for the copy, its own arguments after the configuration's.</summary>
    public static LaunchPlan Plan(RunConfiguration configuration, LaunchPlan plan, int copy)
    {
        IReadOnlyList<string> own = Own(configuration.CopyArguments, copy);
        return copy <= 1 && own.Count == 0 ? plan : plan with { Name = Name(configuration.Name, copy), Arguments = [.. plan.Arguments, .. own] };
    }
}
