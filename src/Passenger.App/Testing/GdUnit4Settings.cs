using System.Xml.Linq;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Testing;

/// <summary>GdUnit4's part of a run's settings, its <c>GdUnit4</c> element, whose <c>Parameters</c> are the arguments its
/// adapter gives the Godot it starts for the tests that need the engine; and the test runner its adapter writes into the
/// project, which compiles into the game.</summary>
public static class GdUnit4Settings
{
    /// <summary>The runner's scene in a Godot project: the adapter writes it, and then builds the game and runs Godot's
    /// headless editor on the project, where it is not there.</summary>
    public static string RunnerScene(string folder) => Path.Combine(folder, IgnoredFolders.GdUnit4Runner, "GdUnit4TestRunnerScene.cs");

    /// <summary>Has the Godot GdUnit4 starts take the arguments Settings gives each start of Godot, ahead of those a run's
    /// settings give, and run headless where <paramref name="headless"/> says (<c>--headless</c> after them, unless they run
    /// it headless already).</summary>
    public static void Given(XElement settings, IReadOnlyList<string> arguments, bool headless)
    {
        XElement gdUnit = settings.Element("GdUnit4") ?? Added(settings, "GdUnit4");
        XElement parameters = gdUnit.Element("Parameters") ?? Added(gdUnit, "Parameters");
        string given = parameters.Value.Trim();
        List<string> words = [.. arguments, .. CommandLine.Split(given)];
        string ahead = arguments.Count == 0 ? string.Empty : WindowsCommandLine.Of(arguments);
        string all = ahead.Length == 0 ? given : given.Length == 0 ? ahead : $"{ahead} {given}";
        parameters.Value = headless && !GodotExecutables.RunsHeadless(words) ? all.Length == 0 ? GodotExecutables.Headless : $"{all} {GodotExecutables.Headless}" : all;
    }

    private static XElement Added(XElement parent, string name)
    {
        XElement child = new(name);
        parent.Add(child);
        return child;
    }
}
