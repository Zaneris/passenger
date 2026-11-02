using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Testing;

/// <summary>What a Godot project's tests run with: GdUnit4's adapter starts the tests that need the engine on the Godot its
/// test host's GODOT_BIN names, which is the one Passenger runs the game on unless the person names their own (in the IDE's
/// environment, a launch profile or the run settings), and Godot keeps its user folders where a test gives them.</summary>
public static class GodotTestRuns
{
    /// <summary>The variable GdUnit4's adapter takes Godot's executable from.</summary>
    public const string BinaryVariable = "GODOT_BIN";

    /// <summary>The variables a Godot project's test hosts are given, the user folders a test gives Godot among them: GODOT_BIN
    /// naming any Godot 4 found, which runs GUT's GDScript; and why none would run GdUnit4's C# where it would not. A worker's:
    /// finding Godot reads the disk and the login shell's PATH.</summary>
    public static (IReadOnlyDictionary<string, string> Variables, string? NoCSharp) Variables(string? setting, IReadOnlyDictionary<string, string> userFolders)
    {
        Dictionary<string, string> variables = new(userFolders, StringComparer.Ordinal);
        if (Environment.GetEnvironmentVariable(BinaryVariable) is { Length: > 0 })
        {
            return (variables, null);
        }
        GodotFound? found = GodotLaunch.Found(setting);
        if (found is { WhyNotScripts: null })
        {
            variables[BinaryVariable] = found.Executable;
        }
        return (variables, found is { WhyNot: null } ? null : $"GdUnit4's tests that need Godot's engine cannot run: {found?.WhyNot ?? GodotLaunch.NotFound}");
    }

    /// <summary>A test project's environment with the Godot variables it does not set itself.</summary>
    public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> godot)
    {
        if (godot.Keys.All(environment.ContainsKey))
        {
            return environment;
        }
        Dictionary<string, string> merged = new(environment, StringComparer.Ordinal);
        foreach ((string name, string value) in godot)
        {
            merged.TryAdd(name, value);
        }
        return merged;
    }
}
