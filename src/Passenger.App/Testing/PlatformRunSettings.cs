using System.Xml.Linq;

namespace Passenger.App.Testing;

/// <summary>Run settings as a Testing Platform test host is given them. Its command line's --settings is known only to
/// frameworks on the platform's bridge to VSTest's adapters, and a host of any other refuses it, so they go by the
/// variable the bridge reads where none is named, which the rest pass over; their environment variables, which reach a
/// test however it is run, go straight into the host's environment.</summary>
public static class PlatformRunSettings
{
    /// <summary>The run settings a bridge reads from a test host's environment, in place of a file another variable names;
    /// without them it says no test's file or line.</summary>
    private const string Variable = "TESTINGPLATFORM_EXPERIMENTAL_VSTEST_RUNSETTINGS";

    private const string FileVariable = "TESTINGPLATFORM_VSTESTBRIDGE_RUNSETTINGS_FILE";

    /// <summary>What a test host is started with: the run's environment, the variables of its project's run settings (none
    /// with null) where that sets none of their names, and the rest for a bridge, each test asked to say where it is unless
    /// they say otherwise when a host is <paramref name="looking"/> for tests. A bridge's run settings the run's environment
    /// or the one a host inherits names stand, with nothing of the project's beside them.</summary>
    public static IReadOnlyDictionary<string, string>? Given(XElement? settings, bool looking, IReadOnlyDictionary<string, string>? environment, Func<string, string?> inherited)
    {
        if (settings is null && !looking || Named(Variable, environment, inherited) || Named(FileVariable, environment, inherited))
        {
            return environment;
        }
        Dictionary<string, string> given = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            given[name] = value;
        }
        XElement root = settings is null ? new XElement("RunSettings") : new XElement(settings);
        if (root.Element("RunConfiguration") is not { } configuration)
        {
            root.Add(configuration = new XElement("RunConfiguration"));
        }
        if (configuration.Element("EnvironmentVariables") is { } variables)
        {
            foreach (XElement variable in variables.Elements())
            {
                given.TryAdd(variable.Name.LocalName, variable.Value);
            }
            // Left in, a bridge starts the tests in a process of its own to set them, out of a debugger's sight.
            variables.Remove();
        }
        if (looking && configuration.Element("CollectSourceInformation") is null)
        {
            configuration.Add(new XElement("CollectSourceInformation", true));
        }
        given[Variable] = root.ToString(SaveOptions.DisableFormatting);
        return given;
    }

    private static bool Named(string variable, IReadOnlyDictionary<string, string>? environment, Func<string, string?> inherited) =>
        (environment?.GetValueOrDefault(variable) ?? inherited(variable)) is { Length: > 0 };
}
