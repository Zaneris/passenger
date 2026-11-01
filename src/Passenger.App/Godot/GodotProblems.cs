using Passenger.App.Run;
using Passenger.Text;

namespace Passenger.App.Godot;

/// <summary>A Godot game's errors and warnings as Problems lists them: each at its place among the project's files
/// (<see cref="GodotError.PlaceIn"/>), or at none for an engine error, under the game's name.</summary>
public static class GodotProblems
{
    public static Problem Of(GodotError error, GodotGame game)
    {
        (string? path, int line) = error.PlaceIn(game.Folder, game.PathOf);
        return new Problem(error.Warning ? ProblemSeverity.Warning : ProblemSeverity.Error, error.Said, null, path,
            new TextPosition(Math.Max(0, line - 1), 0), game.Name);
    }
}
