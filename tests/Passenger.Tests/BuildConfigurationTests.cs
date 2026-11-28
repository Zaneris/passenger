using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.State;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The build configuration chosen beside the run configuration: a folder offers what its solution declares, or with
/// none what its projects declare; choosing one builds nothing, but the editor follows it as the project is evaluated again
/// (#if DEBUG greyed under Release), and the next build builds into its output, a solution's build each project as its
/// configuration maps it.</summary>
public sealed class BuildConfigurationTests
{
    private const string Program = "class Program\n{\n    static void Main()\n    {\n#if DEBUG\n        System.Console.WriteLine(\"debug\");\n#else\n"
        + "        System.Console.WriteLine(\"optimized\");\n#endif\n    }\n}\n";

    /// <summary>The configuration chosen greys code it leaves out, and Build, Run, a launch profile and a test run
    /// build and start each project as the solution maps it, as one flow, each step the check one test made in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void The_configuration_chosen_builds_and_runs_each_project_as_its_solution_maps_it()
    {
        Flow.StepApart<BuildConfigurationTests>("release chosen in the widget greys debug code and the next build builds into bin release", t => t.Release_chosen_in_the_widget_greys_debug_code_and_the_next_build_builds_into_bin_release());
        Flow.StepApart<BuildConfigurationTests>("Run under release starts the program its release build made", t => t.Run_under_release_starts_the_program_its_release_build_made());
        Flow.StepApart<BuildConfigurationTests>("a solutions configuration is offered and builds each project as it maps it (\"All.slnx\")", t => t.A_solutions_configuration_is_offered_and_builds_each_project_as_it_maps_it("All.slnx"));
        Flow.StepApart<BuildConfigurationTests>("Run under the solutions default builds and starts the app as the default maps it", t => t.Run_under_the_solutions_default_builds_and_starts_the_app_as_the_default_maps_it());
        Flow.StepApart<BuildConfigurationTests>("Run under staging builds the library the app references as the solution maps it", t => t.Run_under_staging_builds_the_library_the_app_references_as_the_solution_maps_it());
        Flow.StepApart<BuildConfigurationTests>("Run of a launch profile under staging builds its projects together each as the solution maps it", t => t.Run_of_a_launch_profile_under_staging_builds_its_projects_together_each_as_the_solution_maps_it());
        Flow.StepApart<BuildConfigurationTests>("a test run under release runs the tests release build", t => t.A_test_run_under_release_runs_the_tests_release_build());
    }

    /// <summary>The configuration chosen is kept with the folder through its projects' evaluation afresh, Godot's own
    /// offered and never Release, and the language server answers as it compiles the file, as one flow, each step in an
    /// instance of its own.</summary>
    [AvaloniaFact]
    public void The_configuration_chosen_is_kept_with_the_folder_and_answers_the_language_server()
    {
        Flow.StepApart<BuildConfigurationTests>("the configuration chosen is kept with the folder and a window opening it again builds as it", t => t.The_configuration_chosen_is_kept_with_the_folder_and_a_window_opening_it_again_builds_as_it());
        Flow.StepApart<BuildConfigurationTests>("the configuration kept stands while the folder opened again finds and evaluates its projects afresh", t => t.The_configuration_kept_stands_while_the_folder_opened_again_finds_and_evaluates_its_projects_afresh());
        Flow.StepApart<BuildConfigurationTests>("a project declaring godots configurations offers them and never release", t => t.A_project_declaring_godots_configurations_offers_them_and_never_release());
        Flow.StepApart<BuildConfigurationTests>("the language server answers as the configuration chosen compiles the file", t => t.The_language_server_answers_as_the_configuration_chosen_compiles_the_file());
    }

    /// <summary>A console project with no solution offers Debug and Release, Debug chosen; Release chosen in the widget's
    /// menu greys the code under #if DEBUG and draws the #else as code once the project is evaluated again, and Build then
    /// builds into bin/Release, never bin/Debug.</summary>
    private void Release_chosen_in_the_widget_greys_debug_code_and_the_next_build_builds_into_bin_release()
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-release-"));
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        string program = TestFolders.Write(folder, "Hello/Program.cs", Program);
        Restore.Run(Path.Combine(folder, "Hello", "Hello.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(program);
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => Offered(window) == "Debug, Release", () => $"Debug and Release offered, where the widget offers {Offered(window)}");
            Button widget = window.FindControl<Button>("BuildConfigButton")!;
            Assert.True(widget.IsEffectivelyVisible, "The build configuration's widget is hidden while the project offers Debug and Release");
            Assert.Equal("Debug", window.FindControl<TextBlock>("BuildConfigName")!.Text);
            Ui.WaitFor(() => !Disabled(document, "\"debug\"") && Disabled(document, "\"optimized\""), "the code under #if DEBUG drawn as code, as Debug compiles it");

            Choose(window, "Release");
            Assert.StartsWith("Builds, runs and tests build as Release", window.Status, StringComparison.Ordinal);
            Assert.False(window.IsBuilding, "Choosing Release began a build");
            Ui.WaitFor(() => Disabled(document, "\"debug\"") && !Disabled(document, "\"optimized\""),
                "the code under #if DEBUG greyed and the #else drawn as code once Release is chosen");

            window.Perform(AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
            Assert.True(window.Status == "Build succeeded", $"The build did not succeed: {Ui.Said(window)}");
            Assert.True(File.Exists(Path.Combine(folder, "Hello", "bin", "Release", TestProjects.Framework, "Hello.dll")), "Build under Release did not build into bin/Release");
            Assert.False(File.Exists(Path.Combine(folder, "Hello", "bin", "Debug", TestProjects.Framework, "Hello.dll")), "Build under Release built into bin/Debug");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Release chosen, Run builds the program and starts the one its Release build made, which runs its #else.</summary>
    private void Run_under_release_starts_the_program_its_release_build_made()
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-run-"));
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        TestFolders.Write(folder, "Hello/Program.cs", Program);
        Restore.Run(Path.Combine(folder, "Hello", "Hello.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, Release", () => $"Debug and Release offered, where the widget offers {Offered(window)}");
            Choose(window, "Release");
            string? refused = Ui.Await(window.RunAsync(), "the run", TimeSpan.FromMinutes(2))?.English;
            Assert.True(refused is null, $"Run under Release did not start: {refused}");
            Ui.WaitFor(() => !window.Runner.IsRunning, "the program to end");
            string output = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(output.Contains("optimized", StringComparison.Ordinal) && output.Contains(Path.Combine("bin", "Release"), StringComparison.Ordinal),
                $"Run under Release did not start the program its Release build made:\n{output}");
            Assert.False(File.Exists(Path.Combine(folder, "Hello", "bin", "Debug", TestProjects.Framework, "Hello.dll")), "Run under Release built into bin/Debug");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A .sln and a .slnx declaring Staging beside Debug and Release offer the three; Staging chosen, Build builds
    /// the solution as its Staging maps each project: the app in Staging, the library it references in Release, and the tool
    /// it leaves out of the build not at all.</summary>
    private void A_solutions_configuration_is_offered_and_builds_each_project_as_it_maps_it(string solution)
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-solution-"));
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console("<Configurations>Debug;Release;Staging</Configurations>")
            .With("  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "App/Program.cs", "class Program\n{\n    static void Main() => System.Console.WriteLine(Lib.Name);\n}\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Lib.cs", "public static class Lib\n{\n    public const string Name = \"lib\";\n}\n");
        TestFolders.Write(folder, "Tool/Tool.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Tool/Tool.cs", "public static class Tool\n{\n}\n");
        TestFolders.Write(folder, solution, solution.EndsWith(".slnx", StringComparison.Ordinal) ? Slnx : Sln);
        Restore.Run(Path.Combine(folder, solution));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, Release, Staging", () => $"Debug, Release and Staging offered, where the widget offers {Offered(window)}");
            Choose(window, "Staging");

            window.Perform(AppActions.Build);
            Ui.WaitFor(() => !window.IsBuilding, "the build", TimeSpan.FromMinutes(2));
            Assert.True(window.Status == "Build succeeded", $"The build did not succeed: {Ui.Said(window)}");
            Assert.True(File.Exists(Output(folder, "App", "Staging")), "The solution's Staging did not build the app in Staging");
            Assert.True(File.Exists(Output(folder, "Lib", "Release")), "The solution's Staging did not build the library in Release, as it maps it");
            Assert.False(File.Exists(Output(folder, "Lib", "Staging")), "The solution's Staging built the library in Staging, which it maps to Release");
            Assert.False(Directory.Exists(Path.Combine(folder, "Tool", "bin")), "The solution's Staging built the tool it leaves out of the build");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A solution whose default, Debug, maps its app to Release: Run builds and starts the app's Release build, as
    /// the solution's Build builds it, the library it references built as Debug.</summary>
    private void Run_under_the_solutions_default_builds_and_starts_the_app_as_the_default_maps_it()
    {
        string folder = Mapping("configuration-default-mapped-");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, Release, Staging", () => $"Debug, Release and Staging offered, where the widget offers {Offered(window)}");
            Assert.Equal("Debug", window.FindControl<TextBlock>("BuildConfigName")!.Text);
            string output = RunToItsEnd(window);
            Assert.True(output.Contains("app Release, lib Debug", StringComparison.Ordinal),
                $"Run under the solution's Debug did not start the app as Release and its library as Debug, as Debug maps them:\n{output}");
            Assert.True(File.Exists(Output(folder, "App", "Release")), "Run under the solution's Debug did not build the app into bin/Release, as Debug maps it");
            Assert.False(File.Exists(Output(folder, "App", "Debug")), "Run under the solution's Debug built the app as Debug, which Debug maps to Release");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Staging chosen, which maps the app to Staging and the library it references to Release: Run builds the app as
    /// Staging and the library as Release, and the program runs both so, never a Staging build of the library.</summary>
    private void Run_under_staging_builds_the_library_the_app_references_as_the_solution_maps_it()
    {
        string folder = Mapping("configuration-references-mapped-");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, Release, Staging", () => $"Debug, Release and Staging offered, where the widget offers {Offered(window)}");
            Choose(window, "Staging");
            string output = RunToItsEnd(window);
            Assert.True(output.Contains("app Staging, lib Release", StringComparison.Ordinal),
                $"Run under Staging did not start the app as Staging with its library as Release, as Staging maps them:\n{output}");
            Assert.True(File.Exists(Output(folder, "Lib", "Release")), "Run under Staging did not build the library into bin/Release, as Staging maps it");
            Assert.False(File.Exists(Output(folder, "Lib", "Staging")), "Run under Staging built the library as Staging, which Staging maps to Release");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Staging chosen and a launch profile starting the app and a second one, which Staging maps to Release: Run
    /// builds the two in one build, each as Staging maps it, and the library both reference as Release, never as Staging.</summary>
    private void Run_of_a_launch_profile_under_staging_builds_its_projects_together_each_as_the_solution_maps_it()
    {
        string folder = Mapping("configuration-together-mapped-", second: true);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, Release, Staging", () => $"Debug, Release and Staging offered, where the widget offers {Offered(window)}");
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Both"), () => $"the launch profile listed: {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Assert.True(window.SelectConfiguration("Both"), "The launch profile could not be chosen");
            Choose(window, "Staging");
            Ui.Launched(window, window.RunAsync(), "the profile's projects to start", TimeSpan.FromMinutes(3));
            Ui.WaitFor(() => !window.IsRunning, () => $"the profile's programs to end: {Ui.Said(window)}", TimeSpan.FromMinutes(1));
            Assert.True(File.Exists(Output(folder, "App", "Staging")), "Run of the profile under Staging did not build the app as Staging, as Staging maps it");
            Assert.True(File.Exists(Output(folder, "Second", "Release")), "Run of the profile under Staging did not build the second app as Release, as Staging maps it");
            Assert.False(File.Exists(Output(folder, "Second", "Staging")), "Run of the profile under Staging built the second app as Staging, which Staging maps to Release");
            Assert.True(File.Exists(Output(folder, "Lib", "Release")), "Run of the profile under Staging did not build the library as Release, as Staging maps it");
            Assert.False(File.Exists(Output(folder, "Lib", "Staging")), "Run of the profile under Staging built the library as Staging, which Staging maps to Release");
        }
        finally
        {
            if (window.IsRunning)
            {
                Assert.Null(window.Actions.Run(AppActions.StopAll)?.Shown);
                Ui.WaitFor(() => !window.IsRunning, "the profile's programs to end");
            }
            window.Close();
        }
    }

    /// <summary>A restored solution of an app and the library it references, which prints the configuration each was built
    /// as: its Debug maps the app to Release, its Staging maps the library to Release. With <paramref name="second"/>, a second
    /// app referencing the library, which Staging maps to Release, and a launch profile, Both, starting the two apps.</summary>
    private static string Mapping(string prefix, bool second = false)
    {
        string folder = PathNames.Canonical(TestFolders.Create(prefix));
        foreach (string app in second ? (string[])["App", "Second"] : ["App"])
        {
            TestFolders.Write(folder, $"{app}/{app}.csproj", TestProjects.Console("<Configurations>Debug;Release;Staging</Configurations>")
                .With("  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n"));
            TestFolders.Write(folder, $"{app}/Program.cs", "using System.Reflection;\n\nclass Program\n{\n"
                + "    static void Main() => System.Console.WriteLine($\"app {Of(typeof(Program))}, lib {Of(typeof(Lib))}\");\n\n"
                + "    static string Of(System.Type type) => type.Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;\n}\n");
        }
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Lib.cs", "public static class Lib\n{\n}\n");
        string secondApp = second ? "\n  <Project Path=\"Second/Second.csproj\">\n    <BuildType Solution=\"Staging|*\" Project=\"Release\" />\n  </Project>" : string.Empty;
        TestFolders.Write(folder, "Mapped.slnx", $"""
            <Solution>
              <Configurations>
                <BuildType Name="Debug" />
                <BuildType Name="Release" />
                <BuildType Name="Staging" />
              </Configurations>
              <Project Path="App/App.csproj">
                <BuildType Solution="Debug|*" Project="Release" />
              </Project>
              <Project Path="Lib/Lib.csproj">
                <BuildType Solution="Staging|*" Project="Release" />
              </Project>{secondApp}
            </Solution>

            """);
        if (second)
        {
            TestFolders.Write(folder, "Mapped.slnLaunch", "[\n  {\n    \"Name\": \"Both\",\n    \"Projects\": [\n      { \"Path\": \"App\\\\App.csproj\", \"Action\": \"Start\" },\n"
                + "      { \"Path\": \"Second\\\\Second.csproj\", \"Action\": \"Start\" }\n    ]\n  }\n]\n");
        }
        Restore.Run(Path.Combine(folder, "Mapped.slnx"));
        return folder;
    }

    /// <summary>Run of the configuration chosen, waited for until its program ends: what the Run window shows of it.</summary>
    private static string RunToItsEnd(MainWindow window)
    {
        Ui.Launched(window, window.RunAsync(), "the run", TimeSpan.FromMinutes(2));
        Ui.WaitFor(() => !window.Runner.IsRunning, "the program to end");
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }

    /// <summary>The configuration chosen is the folder's: a window keeping state opens it again building as Staging, its
    /// widget naming it and its projects evaluated under it from the start.</summary>
    private void The_configuration_chosen_is_kept_with_the_folder_and_a_window_opening_it_again_builds_as_it() =>
        OpenedAgainUnderStaging("configuration-kept-", (_, _) => { });

    /// <summary>The configuration kept stands while the folder opened again finds its projects and evaluates them afresh, its
    /// evaluations not kept: until a project's evaluation is in, nothing says it no longer declares Staging.</summary>
    private void The_configuration_kept_stands_while_the_folder_opened_again_finds_and_evaluates_its_projects_afresh() =>
        OpenedAgainUnderStaging("configuration-afresh-", (settings, folder) => File.Delete(Path.Combine(ProjectState.FolderOf(settings, folder), "projectModels.json")));

    /// <summary>Staging chosen in a window keeping state, then <paramref name="between"/> (given the state folder and the
    /// folder) before another window opens the folder again: it builds as Staging from the start, its widget naming it and
    /// its project evaluated under it.</summary>
    private static void OpenedAgainUnderStaging(string prefix, Action<string, string> between)
    {
        string folder = PathNames.Canonical(TestFolders.Create(prefix));
        string settings = TestFolders.Create("configuration-settings-");
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console("<Configurations>Debug;Release;Staging</Configurations>"));
        try
        {
            MainWindow first = Keeping(settings, folder);
            Ui.WaitFor(() => Offered(first) == "Debug, Release, Staging", () => $"Debug, Release and Staging offered, where the widget offers {Offered(first)}");
            Choose(first, "Staging");
            Ui.WaitFor(() => first.Session.Projects.Single().Property(Passenger.Host.CarriedProperties.TargetPath)?.Contains("Staging", StringComparison.Ordinal) == true,
                "the project evaluated under Staging in the first window");
            first.Close();
            Assert.True(KeptEvaluation(settings, folder) is { } kept && kept.Contains("Staging", StringComparison.Ordinal),
                $"The folder kept no evaluation of its project under Staging, so its next opening evaluates it again: {KeptEvaluation(settings, folder) ?? "none kept"}");
            between(settings, folder);

            MainWindow second = Keeping(settings, folder);
            try
            {
                Assert.True(second.Session.Workspace?.Build?.Chosen.Configuration == "Staging",
                    $"The folder opened again building as {second.Session.Workspace?.Build?.Chosen.Key ?? "what MSBuild takes given none"}, not the Staging chosen before");
                Ui.WaitFor(() => second.FindControl<TextBlock>("BuildConfigName")!.Text == "Staging", () => $"the widget to name Staging: {Ui.Said(second)}");
                Ui.WaitFor(() => second.Session.Projects.Single().Property(Passenger.Host.CarriedProperties.TargetPath)?.Contains("Staging", StringComparison.Ordinal) == true,
                    () => $"the project evaluated under Staging, where its build makes {second.Session.Projects.Single().Property(Passenger.Host.CarriedProperties.TargetPath)}");
            }
            finally
            {
                second.Close();
            }
        }
        finally
        {
            TestFolders.Delete(settings);
        }
    }

    /// <summary>Where the build of the folder's one project kept in the store puts it, as its evaluation said; null for none kept.</summary>
    private static string? KeptEvaluation(string settings, string folder)
    {
        StateStore store = StateStore.Open(settings);
        try
        {
            return store.Hold(folder).ProjectModels.Value.Projects.Values.SingleOrDefault()?.Properties?.GetValueOrDefault(Passenger.Host.CarriedProperties.TargetPath)?.Value;
        }
        finally
        {
            store.LetGo(folder);
            store.Release();
        }
    }

    /// <summary>A window that keeps its state in a folder of its own, on the folder given, without the C# language server,
    /// which a configuration kept asks nothing of.</summary>
    private static MainWindow Keeping(string settings, string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>A project declaring Godot.NET.Sdk's configurations, with no solution, offers Debug, ExportDebug and
    /// ExportRelease, never Release.</summary>
    private void A_project_declaring_godots_configurations_offers_them_and_never_release()
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-godot-"));
        TestFolders.Write(folder, "Game/Game.csproj", TestProjects.Library("<Configurations>Debug;ExportDebug;ExportRelease</Configurations>"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, ExportDebug, ExportRelease", () => $"Godot's three configurations offered, where the widget offers {Offered(window)}");
        }
        finally
        {
            window.Close();
        }
    }

    private const string Slnx = """
        <Solution>
          <Configurations>
            <BuildType Name="Debug" />
            <BuildType Name="Release" />
            <BuildType Name="Staging" />
          </Configurations>
          <Project Path="App/App.csproj" />
          <Project Path="Lib/Lib.csproj">
            <BuildType Solution="Staging|*" Project="Release" />
          </Project>
          <Project Path="Tool/Tool.csproj">
            <Build Solution="Staging|*" Project="false" />
          </Project>
        </Solution>

        """;

    private const string Sln = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        # Visual Studio Version 17
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lib", "Lib\Lib.csproj", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Tool", "Tool\Tool.csproj", "{33333333-3333-3333-3333-333333333333}"
        EndProject
        Global
        	GlobalSection(SolutionConfigurationPlatforms) = preSolution
        		Debug|Any CPU = Debug|Any CPU
        		Release|Any CPU = Release|Any CPU
        		Staging|Any CPU = Staging|Any CPU
        	EndGlobalSection
        	GlobalSection(ProjectConfigurationPlatforms) = postSolution
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Release|Any CPU.Build.0 = Release|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Staging|Any CPU.ActiveCfg = Staging|Any CPU
        		{11111111-1111-1111-1111-111111111111}.Staging|Any CPU.Build.0 = Staging|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Release|Any CPU.Build.0 = Release|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Staging|Any CPU.ActiveCfg = Release|Any CPU
        		{22222222-2222-2222-2222-222222222222}.Staging|Any CPU.Build.0 = Release|Any CPU
        		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
        		{33333333-3333-3333-3333-333333333333}.Debug|Any CPU.Build.0 = Debug|Any CPU
        		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.ActiveCfg = Release|Any CPU
        		{33333333-3333-3333-3333-333333333333}.Release|Any CPU.Build.0 = Release|Any CPU
        		{33333333-3333-3333-3333-333333333333}.Staging|Any CPU.ActiveCfg = Release|Any CPU
        	EndGlobalSection
        EndGlobal

        """;

    /// <summary>The C# language server follows the choice: under Debug it says the error in the code under #if DEBUG and
    /// nothing of the #else's, and once Release is chosen the other way round, as Release compiles the file.</summary>
    private void The_language_server_answers_as_the_configuration_chosen_compiles_the_file()
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-server-"));
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console());
        string program = TestFolders.Write(folder, "Hello/Program.cs",
            "class Program\n{\n    static void Main()\n    {\n#if DEBUG\n        Lacking();\n#else\n        Missing();\n#endif\n    }\n}\n");
        Restore.Run(Path.Combine(folder, "Hello", "Hello.csproj"));
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            window.OpenFile(program);
            OpenDocument document = window.EditorArea.Active!;
            Ui.WaitFor(() => Said(window, document, "Lacking") && !Said(window, document, "Missing"),
                () => $"the server's error on Lacking alone, as Debug compiles the file; it says {Errors(window, document)}", TimeSpan.FromMinutes(2));

            Choose(window, "Release");
            Ui.WaitFor(() => Said(window, document, "Missing") && !Said(window, document, "Lacking"),
                () => $"the server's error on Missing alone once Release is chosen, as Release compiles the file; it says {Errors(window, document)}", TimeSpan.FromMinutes(2));
        }
        finally
        {
            window.Close();
        }

        static bool Said(MainWindow window, OpenDocument document, string name) =>
            window.CSharpService?.For(document)?.Diagnostics.Any(d => d.Code?.ToString() == "CS0103" && d.Message.Contains($"'{name}'", StringComparison.Ordinal)) == true;

        static string Errors(MainWindow window, OpenDocument document) =>
            string.Join("; ", window.CSharpService?.For(document)?.Diagnostics.Select(d => $"{d.Code} {d.Message}") ?? ["nothing"]);
    }

    /// <summary>Release chosen, Run All builds the test project as Release and runs its Release build, whose test passes
    /// only there, building nothing as Debug.</summary>
    private void A_test_run_under_release_runs_the_tests_release_build()
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-tests-"));
        TestFolders.Write(folder, "Built.Tests/Built.Tests.csproj", TestProjects.Library("<ImplicitUsings>enable</ImplicitUsings>").With(
            $"  <ItemGroup>\n    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"{TestPackages.TestSdk}\" />\n"
            + $"    <PackageReference Include=\"xunit\" Version=\"{TestPackages.XunitTwo}\" />\n"
            + $"    <PackageReference Include=\"xunit.runner.visualstudio\" Version=\"{TestPackages.XunitTwoRunner}\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Built.Tests/BuiltTests.cs", "using Xunit;\n\nnamespace Built.Tests;\n\npublic class BuiltTests\n{\n    [Fact]\n"
            + "    public void Built_as_release()\n    {\n#if DEBUG\n        Assert.Fail(\"built as Debug\");\n#endif\n"
            + "        Assert.Contains(Path.Combine(\"bin\", \"Release\"), AppContext.BaseDirectory);\n    }\n}\n");
        Restore.Run(Path.Combine(folder, "Built.Tests"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => Offered(window) == "Debug, Release", () => $"Debug and Release offered, where the widget offers {Offered(window)}");
            Choose(window, "Release");
            Assert.True(window.Perform(AppActions.RunAllTests), $"Run All did not run: {window.Status}");
            Ui.WaitFor(() => !window.Tests.IsBusy && Built(window) is { State: not Passenger.App.Testing.TestState.NotRun },
                () => $"the run of the test: {Ui.Said(window)}", TimeSpan.FromMinutes(3));
            Assert.True(Built(window) is { State: Passenger.App.Testing.TestState.Passed },
                $"The test passing only in a Release build did not pass under Release: {Built(window)?.Details}{Ui.BuildErrors(window)}");
            Assert.True(File.Exists(Path.Combine(folder, "Built.Tests", "bin", "Release", TestProjects.Framework, "Built.Tests.dll")), "The test run under Release did not build into bin/Release");
            Assert.False(Directory.Exists(Path.Combine(folder, "Built.Tests", "bin", "Debug")), "The test run under Release built into bin/Debug");
        }
        finally
        {
            window.Close();
        }

        static Passenger.App.Testing.TestNode? Built(MainWindow window) =>
            window.Tests.Tree.Roots.SelectMany(r => r.Methods).FirstOrDefault(m => m.Name == "Built_as_release");
    }

    /// <summary>A configuration chosen as a person chooses it: the widget clicked, then its item in the menu.</summary>
    internal static void Choose(MainWindow window, string shown)
    {
        Ui.Click(window.FindControl<Button>("BuildConfigButton")!);
        Ui.WaitFor(() => window.OpenContextMenu is { IsOpen: true }, "the menu of the build configurations");
        Ui.Click(window.OpenContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == shown));
        Ui.WaitFor(() => window.FindControl<TextBlock>("BuildConfigName")!.Text == shown, () => $"the widget to name {shown}: {Ui.Said(window)}");
    }

    /// <summary>The configurations the widget's menu would offer, as shown, in order.</summary>
    internal static string Offered(MainWindow window) =>
        string.Join(", ", window.Session.BuildChoices.Offered.Select(o => o.Shown(window.Session.BuildChoices.Offered)));

    private static string Output(string folder, string project, string configuration) =>
        Path.Combine(folder, project, "bin", configuration, TestProjects.Framework, $"{project}.dll");

    /// <summary>Whether the editor draws the code that starts with <paramref name="code"/> as disabled, as its parse colours
    /// the line.</summary>
    private static bool Disabled(OpenDocument document, string code)
    {
        TextSnapshot text = document.Text.Snapshot;
        int at = text.ToString().IndexOf(code, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{code}' is not in {document.Name}");
        LineSpan line = text.GetLine(text.GetLineFromOffset(at));
        return document.Highlighter!.GetSpans(text, line.Start, line.EndIncludingBreak - line.Start).Any(s => s.Kind == TokenKind.Excluded && s.Start <= at && s.End > at);
    }
}
