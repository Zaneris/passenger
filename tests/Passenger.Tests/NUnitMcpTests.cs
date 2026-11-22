using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ModelContextProtocol.Client;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Editors;
using Passenger.App.Mcp;
using Passenger.App.Testing;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>NUnit as an agent meets it, however it is installed: a run's answer says which case of a method failed,
/// tests_list where a test its runner alone finds is, and a run takes what its project's run settings say.</summary>
public sealed class NUnitMcpTests : McpRig
{
    /// <summary>A method of two named cases, one failing, and a method whose only case is named and fails.</summary>
    private const string Named = """
        using NUnit.Framework;

        namespace Cases.Tests;

        public class Arithmetic
        {
            public static readonly TestCaseData[] Titled = [new TestCaseData(1, 1).SetName("OneIsOne"), new TestCaseData(1, 2).SetName("OneIsTwo")];

            public static readonly TestCaseData[] Alone = [new TestCaseData(2, 3).SetName("TwoIsThree")];

            [TestCaseSource(nameof(Titled))]
            public void Compared(int a, int b) => Assert.That(a, Is.EqualTo(b));

            [TestCaseSource(nameof(Alone))]
            public void Lone(int a, int b) => Assert.That(a, Is.EqualTo(b));
        }

        """;

    /// <summary>An agent's NUnit tests as one flow, on VSTest and on NUnit's own runner, each in an instance of its
    /// own: a failing case named in place of its method named in the run's answer, a generic fixture's place told, and
    /// a project's run settings reaching its tests.</summary>
    [Fact]
    public async Task An_agents_NUnit_runs_name_cases_fixtures_and_run_settings_on_VSTest_and_on_NUnits_own_runner()
    {
        await Flow.StepApartAsync("an agents run names a failing case named in place of its method (NUnitInstall.OnVSTest)", () => new NUnitMcpTests(), t => t.An_agents_run_names_a_failing_case_named_in_place_of_its_method(NUnitInstall.OnVSTest));
        await Flow.StepApartAsync("an agents run names a failing case named in place of its method (NUnitInstall.OnItsRunner)", () => new NUnitMcpTests(), t => t.An_agents_run_names_a_failing_case_named_in_place_of_its_method(NUnitInstall.OnItsRunner));
        await Flow.StepApartAsync("an agent is told where a generic fixture is however nunit is installed (NUnitInstall.OnVSTest)", () => new NUnitMcpTests(), t => t.An_agent_is_told_where_a_generic_fixture_is_however_nunit_is_installed(NUnitInstall.OnVSTest));
        await Flow.StepApartAsync("an agent is told where a generic fixture is however nunit is installed (NUnitInstall.OnItsRunner)", () => new NUnitMcpTests(), t => t.An_agent_is_told_where_a_generic_fixture_is_however_nunit_is_installed(NUnitInstall.OnItsRunner));
        await Flow.StepApartAsync("a projects run settings reach its tests however nunit is installed (NUnitInstall.OnVSTest)", () => new NUnitMcpTests(), t => t.A_projects_run_settings_reach_its_tests_however_nunit_is_installed(NUnitInstall.OnVSTest));
        await Flow.StepApartAsync("a projects run settings reach its tests however nunit is installed (NUnitInstall.OnItsRunner)", () => new NUnitMcpTests(), t => t.A_projects_run_settings_reach_its_tests_however_nunit_is_installed(NUnitInstall.OnItsRunner));
    }

    private async Task An_agents_run_names_a_failing_case_named_in_place_of_its_method(NUnitInstall install)
    {
        await using McpClient client = await Start(["--mcp", await Restored(install, Named)], languageServer: false);
        JsonElement run = await Call(client, McpTools.TestsRun);
        Assert.StartsWith("Tests passed: 1, failed: 2 of 3", run.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("OneIsTwo: Failed", Details(run, "Cases.Tests.Arithmetic.Compared"), StringComparison.Ordinal);
        Assert.StartsWith("TwoIsThree: Failed", Details(run, "Cases.Tests.Arithmetic.Lone"), StringComparison.Ordinal);

        JsonElement alone = await Call(client, McpTools.TestsRun, new() { ["filter"] = "Lone" });
        Assert.StartsWith("Tests passed: 0, failed: 1 of 1", alone.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.StartsWith("TwoIsThree: Failed", Details(alone, "Cases.Tests.Arithmetic.Lone"), StringComparison.Ordinal);
    }

    /// <summary>tests_list's location of a test only a runner finds, on NUnit's own runner as on VSTest.</summary>
    private async Task An_agent_is_told_where_a_generic_fixture_is_however_nunit_is_installed(NUnitInstall install)
    {
        const string Typed = "using NUnit.Framework;\n\nnamespace Cases.Tests;\n\n[TestFixture(typeof(int))]\npublic class Typed<T>\n{\n    [Test]\n    public void Named() { }\n}\n";
        await using McpClient client = await Start(["--mcp", await Restored(install, Typed)], languageServer: false);
        await Call(client, McpTools.TestsRun);
        JsonElement test = Assert.Single((await Call(client, McpTools.TestsList, new() { ["filter"] = "Typed" })).EnumerateArray());
        Assert.True(test.TryGetProperty("location", out JsonElement location) && location.GetString() == "Cases.cs:9", $"tests_list says nowhere a run of it found the test: {test}");
    }

    /// <summary>A project's run settings file reaches its tests however NUnit is installed, a parameter and a variable as a
    /// run of it reads them, and a test only a runner finds still says where it is.</summary>
    private async Task A_projects_run_settings_reach_its_tests_however_nunit_is_installed(NUnitInstall install)
    {
        const string Settled = """
            using NUnit.Framework;

            namespace Cases.Tests;

            [TestFixture(typeof(int))]
            public class Typed<T>
            {
                [Test]
                public void Parameter() => Assert.That(TestContext.Parameters["who"], Is.EqualTo("the parameter"));

                [Test]
                public void Variable() => Assert.That(System.Environment.GetEnvironmentVariable("SETTLED_BY"), Is.EqualTo("the run settings"));
            }

            """;
        string folder = await Restored(install, Settled);
        string project = Path.Combine(folder, "Sums.Tests.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</PropertyGroup>", "  <RunSettingsFilePath>$(MSBuildProjectDirectory)/settled.runsettings</RunSettingsFilePath>\n  </PropertyGroup>", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(folder, "settled.runsettings"), NUnitFolders.RunSettings);
        await using McpClient client = await Start(["--mcp", folder], languageServer: false);

        JsonElement run = await Call(client, McpTools.TestsRun);
        Assert.True(run.GetProperty("summary").GetString()!.StartsWith("Tests passed: 2, failed: 0 of 2", StringComparison.Ordinal),
            $"The tests ran without what their project's run settings say: {run}");
        JsonElement test = Assert.Single((await Call(client, McpTools.TestsList, new() { ["filter"] = "Parameter" })).EnumerateArray());
        Assert.True(test.TryGetProperty("location", out JsonElement location) && location.GetString() == "Cases.cs:9",
            $"Given its project's run settings, a host looking for tests no longer says where they are: {test}");
    }

    /// <summary>What a run's answer says of a test's failure.</summary>
    private static string Details(JsonElement run, string test) =>
        run.GetProperty("failures").EnumerateArray().Where(f => f.GetProperty("test").GetString() == test).Select(f => f.GetProperty("details").GetString()).FirstOrDefault()
            ?? throw new Xunit.Sdk.XunitException($"The run's answer has no failure of {test}: {run}");

    /// <summary>A folder holding a project that takes NUnit as given, with the code given, restored.</summary>
    private async Task<string> Restored(NUnitInstall install, string code)
    {
        string folder = PathNames.Canonical(TestFolders.Create("mcp-nunit-"));
        DeleteAtEnd(folder);
        await TestFolders.PrepareAsync(async () =>
        {
            File.WriteAllText(Path.Combine(folder, "Sums.Tests.csproj"), NUnitFolders.Project(install));
            File.WriteAllText(Path.Combine(folder, "Cases.cs"), code);
            await Restore.RunAsync(folder);
        }, folder);
        return folder;
    }
}

/// <summary>How a test project takes NUnit.</summary>
public enum NUnitInstall
{
    /// <summary>NUnit and its adapter beside VSTest's SDK.</summary>
    OnVSTest,

    /// <summary>NUnit's own runner on the Testing Platform: an executable with no test SDK.</summary>
    OnItsRunner,

    /// <summary>NUnit 4 and the adapter of its day beside VSTest's SDK.</summary>
    FourOnVSTest,
}

/// <summary>The NUnit project each test writes for itself, and the window on it.</summary>
public abstract class NUnitFolders : UnitTestsFolders
{
    protected const string SumTestsFile = "SumTests.cs";

    /// <summary>A test of each ending, the lines the tests name being those of this text.</summary>
    protected const string SumTests = """
        using NUnit.Framework;

        namespace Sums.Tests;

        public class SumTests
        {
            [Test]
            public void Adds()
            {
                TestContext.Out.WriteLine("adding two and two");
                int sum = 2 + 2;
                Assert.That(sum, Is.EqualTo(4));
            }

            [Test]
            public void Fails()
            {
                Assert.That(2 + 2, Is.EqualTo(5));
            }

            [Test]
            [Ignore("later")]
            public void Not_yet() { }

            [TestCase(1, 2)]
            [TestCase(2, 4)]
            public void Doubles(int value, int twice) => Assert.That(value * 2, Is.EqualTo(twice));
        }

        """;

    /// <summary>A run settings file giving a test a variable of 16 letters and a parameter.</summary>
    internal const string RunSettings = """
        <RunSettings>
          <RunConfiguration>
            <EnvironmentVariables>
              <SETTLED_BY>the run settings</SETTLED_BY>
            </EnvironmentVariables>
          </RunConfiguration>
          <TestRunParameters>
            <Parameter name="who" value="the parameter" />
          </TestRunParameters>
        </RunSettings>

        """;

    protected NUnitFolders()
        : base(null)
    {
    }

    /// <summary>A folder of its own holding a project that takes NUnit as given, with the code given, restored.</summary>
    protected string Restored(NUnitInstall install, string file, string code)
    {
        string folder = NewFolder();
        Write(folder, "Sums.Tests.csproj", Project(install));
        Write(folder, file, code);
        Restore.Run(folder);
        return folder;
    }

    /// <summary>A window on a project of <see cref="SumTests"/> with its file open and shown in the Unit Tests window, with
    /// no C# language server, which finding, running and debugging tests ask nothing of.</summary>
    protected static MainWindow Opened(string folder)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.OpenFile(Path.Combine(folder, SumTestsFile));
        Ui.Settle();
        Tree(window);
        Ui.WaitFor(() => Method(window, "Adds") is not null, "the tests read from the code");
        return window;
    }

    /// <summary>The project file that takes NUnit as given.</summary>
    internal static string Project(NUnitInstall install) => install switch
    {
        NUnitInstall.OnItsRunner => Project(
            "    <OutputType>Exe</OutputType>\n    <EnableNUnitRunner>true</EnableNUnitRunner>\n", TestPackages.NUnit, TestPackages.NUnitAdapter, testSdk: false),
        NUnitInstall.FourOnVSTest => Project(string.Empty, TestPackages.NUnitFour, TestPackages.NUnitFourAdapter, testSdk: true),
        _ => Project(string.Empty, TestPackages.NUnit, TestPackages.NUnitAdapter, testSdk: true),
    };

    private static string Project(string properties, string nunit, string adapter, bool testSdk) =>
        $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n{properties}  </PropertyGroup>\n  <ItemGroup>\n" +
        (testSdk ? $"    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n" : string.Empty) +
        $"    <PackageReference Include=\"NUnit\" Version=\"{nunit}\" />\n    <PackageReference Include=\"NUnit3TestAdapter\" Version=\"{adapter}\" />\n  </ItemGroup>\n</Project>\n";
}
