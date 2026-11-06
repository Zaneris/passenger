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
    public void A_run_of_a_project_naming_no_run_settings_asks_nothing_of_its_host()
    {
        Dictionary<string, string> profile = new() { ["PROFILED_BY"] = "the profile" };
        Assert.Same(profile, Given(null, looking: false, profile));
        Assert.Null(Given(null, looking: false));
    }

    [Fact]
    public void A_host_looking_for_tests_is_asked_where_each_is_with_no_run_settings_named()
    {
        Assert.Equal("true", Sources(Read(Given(null, looking: true))));
    }

    [Fact]
    public void A_host_looking_for_tests_keeps_what_its_projects_run_settings_say_and_is_asked_where_each_is()
    {
        XElement read = Read(Given(Settled, looking: true));
        Assert.Equal("true", Sources(read));
        Assert.Equal("the parameter", read.Element("TestRunParameters")?.Element("Parameter")?.Attribute("value")?.Value);
    }

    [Fact]
    public void Run_settings_saying_no_test_is_to_say_where_it_is_stand()
    {
        const string Unsaid = "<RunSettings><RunConfiguration><CollectSourceInformation>false</CollectSourceInformation></RunConfiguration></RunSettings>";
        Assert.Equal("false", Sources(Read(Given(Unsaid, looking: true))));
    }

    [Fact]
    public void A_run_is_given_its_projects_run_settings_with_nothing_asked_of_where_tests_are()
    {
        XElement read = Read(Given(Settled, looking: false));
        Assert.Null(Sources(read));
        Assert.NotNull(read.Element("TestRunParameters"));
    }

    [Fact]
    public void Run_settings_variables_go_to_the_hosts_environment_under_the_runs_own_and_never_to_a_bridge()
    {
        IReadOnlyDictionary<string, string>? given = Given(Settled, looking: false, new Dictionary<string, string> { ["BOTH"] = "the profile", ["PROFILED_BY"] = "the profile" });
        Assert.Equal(("the run settings", "the profile", "the profile"), (given!["SETTLED_BY"], given["BOTH"], given["PROFILED_BY"]));
        Assert.True(Read(given).Element("RunConfiguration")!.Element("EnvironmentVariables") is null,
            "A bridge given run settings with variables starts its tests in a process of its own, where a debugger never sees them");
    }

    [Fact]
    public void The_file_a_project_names_is_left_as_it_was_read()
    {
        XElement settings = XElement.Parse(Settled);
        PlatformRunSettings.Given(settings, looking: true, null, _ => null);
        Assert.True(XNode.DeepEquals(XElement.Parse(Settled), settings), "Reading what a host is given changed the settings read, which the next host of the project takes");
    }

    [Fact]
    public void Run_settings_a_person_gave_a_bridge_stand_in_the_place_of_the_projects()
    {
        Assert.Null(Given(Settled, looking: true, inherited: "<RunSettings />"));
        Dictionary<string, string> content = new() { [Variable] = "<RunSettings />" };
        Assert.Same(content, Given(Settled, looking: true, content));
        Dictionary<string, string> file = new() { [FileVariable] = "their.runsettings" };
        Assert.Same(file, Given(Settled, looking: false, file));
    }

    [Fact]
    public void A_bridges_variable_set_empty_gives_it_no_run_settings_so_the_projects_are_given()
    {
        Assert.NotNull(Read(Given(Settled, looking: false, inherited: string.Empty)).Element("TestRunParameters"));
    }
}
