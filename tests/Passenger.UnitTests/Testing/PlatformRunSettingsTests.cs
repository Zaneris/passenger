using System.Xml.Linq;
using Passenger.App.Testing;

namespace Passenger.UnitTests.Testing;

/// <summary>What a Testing Platform test host starts with for its project's run settings: read by a bridge from the one
/// variable, their environment variables in its own environment, its search for tests still saying where each test is, and
/// whatever a person gave a bridge left standing.</summary>
public class PlatformRunSettingsTests
{
    private const string Variable = "TESTINGPLATFORM_EXPERIMENTAL_VSTEST_RUNSETTINGS";

    private const string FileVariable = "TESTINGPLATFORM_VSTESTBRIDGE_RUNSETTINGS_FILE";

    private const string Settled = """
        <RunSettings>
          <RunConfiguration>
            <EnvironmentVariables>
              <SETTLED_BY>the run settings</SETTLED_BY>
              <BOTH>the run settings</BOTH>
            </EnvironmentVariables>
          </RunConfiguration>
          <TestRunParameters>
            <Parameter name="who" value="the parameter" />
          </TestRunParameters>
        </RunSettings>
        """;

    private static IReadOnlyDictionary<string, string>? Given(string? settings, bool looking, IReadOnlyDictionary<string, string>? environment = null, string? inherited = null) =>
        PlatformRunSettings.Given(settings is null ? null : XElement.Parse(settings), looking, environment, name => name == Variable ? inherited : null);

    private static XElement Read(IReadOnlyDictionary<string, string>? given) => XElement.Parse(given![Variable]);

    private static string? Sources(XElement settings) => settings.Element("RunConfiguration")?.Element("CollectSourceInformation")?.Value;

    [Fact]
    public void Run_settings_reach_a_Testing_Platform_host_as_its_project_and_the_person_give_them() => Cases.All(
        ("a run of a project naming no run settings asks nothing of its host", () =>
        {
            Dictionary<string, string> profile = new() { ["PROFILED_BY"] = "the profile" };
            Assert.Same(profile, Given(null, looking: false, profile));
            Assert.Null(Given(null, looking: false));
        }),
        ("a host looking for tests is asked where each is with no run settings named", () =>
        {
            Assert.Equal("true", Sources(Read(Given(null, looking: true))));
        }),
        ("a host looking for tests keeps what its project's run settings say and is asked where each is", () =>
        {
            XElement read = Read(Given(Settled, looking: true));
            Assert.Equal("true", Sources(read));
            Assert.Equal("the parameter", read.Element("TestRunParameters")?.Element("Parameter")?.Attribute("value")?.Value);
        }),
        ("run settings saying no test is to say where it is stand", () =>
        {
            const string Unsaid = "<RunSettings><RunConfiguration><CollectSourceInformation>false</CollectSourceInformation></RunConfiguration></RunSettings>";
            Assert.Equal("false", Sources(Read(Given(Unsaid, looking: true))));
        }),
        ("a run is given its project's run settings with nothing asked of where tests are", () =>
        {
            XElement read = Read(Given(Settled, looking: false));
            Assert.Null(Sources(read));
            Assert.NotNull(read.Element("TestRunParameters"));
        }),
        ("run settings variables go to the host's environment under the run's own and never to a bridge", () =>
        {
            IReadOnlyDictionary<string, string>? given = Given(Settled, looking: false, new Dictionary<string, string> { ["BOTH"] = "the profile", ["PROFILED_BY"] = "the profile" });
            Assert.Equal(("the run settings", "the profile", "the profile"), (given!["SETTLED_BY"], given["BOTH"], given["PROFILED_BY"]));
            Assert.True(Read(given).Element("RunConfiguration")!.Element("EnvironmentVariables") is null,
                "A bridge given run settings with variables starts its tests in a process of its own, where a debugger never sees them");
        }),
        ("the file a project names is left as it was read", () =>
        {
            XElement settings = XElement.Parse(Settled);
            PlatformRunSettings.Given(settings, looking: true, null, _ => null);
            Assert.True(XNode.DeepEquals(XElement.Parse(Settled), settings), "Reading what a host is given changed the settings read, which the next host of the project takes");
        }),
        ("run settings a person gave a bridge stand in the place of the project's", () =>
        {
            Assert.Null(Given(Settled, looking: true, inherited: "<RunSettings />"));
            Dictionary<string, string> content = new() { [Variable] = "<RunSettings />" };
            Assert.Same(content, Given(Settled, looking: true, content));
            Dictionary<string, string> file = new() { [FileVariable] = "their.runsettings" };
            Assert.Same(file, Given(Settled, looking: false, file));
        }),
        ("a bridge's variable set empty gives it no run settings so the project's are given", () =>
        {
            Assert.NotNull(Read(Given(Settled, looking: false, inherited: string.Empty)).Element("TestRunParameters"));
        }));

}
