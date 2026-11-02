using System.Xml.Linq;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>GdUnit4's part of a run's settings, its <c>GdUnit4</c> element, whose <c>Parameters</c> are the arguments its
/// adapter gives the Godot it starts for the tests that need the engine.</summary>
public static class GdUnit4Settings
{
    /// <summary>Has the Godot GdUnit4 starts run headless: <c>--headless</c> after the parameters a run's settings give, unless
    /// they run it headless already.</summary>
    public static void Headless(XElement settings)
    {
        XElement gdUnit = settings.Element("GdUnit4") ?? Added(settings, "GdUnit4");
        XElement parameters = gdUnit.Element("Parameters") ?? Added(gdUnit, "Parameters");
        string given = parameters.Value.Trim();
        if (!GodotExecutables.RunsHeadless(CommandLine.Split(given)))
        {
            parameters.Value = given.Length == 0 ? GodotExecutables.Headless : $"{given} {GodotExecutables.Headless}";
        }
    }

    private static XElement Added(XElement parent, string name)
    {
        XElement child = new(name);
        parent.Add(child);
        return child;
    }
}
