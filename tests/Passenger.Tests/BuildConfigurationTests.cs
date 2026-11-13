using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
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

    /// <summary>A console project with no solution offers Debug and Release, Debug chosen; Release chosen in the widget's
    /// menu greys the code under #if DEBUG and draws the #else as code once the project is evaluated again, and Build then
    /// builds into bin/Release, never bin/Debug.</summary>
    [AvaloniaFact]
    public void Release_chosen_in_the_widget_greys_debug_code_and_the_next_build_builds_into_bin_release()
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
    [AvaloniaFact]
    public void Run_under_release_starts_the_program_its_release_build_made()
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
            string? refused = Ui.Await(window.RunAsync(), "the run", TimeSpan.FromMinutes(2));
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
    [AvaloniaTheory]
    [InlineData("All.slnx")]
    [InlineData("All.sln")]
    public void A_solutions_configuration_is_offered_and_builds_each_project_as_it_maps_it(string solution)
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

    /// <summary>The configuration chosen is the folder's: a window keeping state opens it again building as Staging, its
    /// widget naming it and its projects evaluated under it from the start.</summary>
    [AvaloniaFact]
    public void The_configuration_chosen_is_kept_with_the_folder_and_a_window_opening_it_again_builds_as_it()
    {
        string folder = PathNames.Canonical(TestFolders.Create("configuration-kept-"));
        string settings = TestFolders.Create("configuration-settings-");
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console("<Configurations>Debug;Release;Staging</Configurations>"));
        try
        {
            MainWindow first = Keeping(settings, folder);
            Ui.WaitFor(() => Offered(first) == "Debug, Release, Staging", () => $"Debug, Release and Staging offered, where the widget offers {Offered(first)}");
            Choose(first, "Staging");
            first.Close();

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

    /// <summary>A window that keeps its state in a folder of its own, on the folder given.</summary>
    private static MainWindow Keeping(string settings, string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>A project declaring Godot.NET.Sdk's configurations, with no solution, offers Debug, ExportDebug and
    /// ExportRelease, never Release.</summary>
    [AvaloniaFact]
    public void A_project_declaring_godots_configurations_offers_them_and_never_release()
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

    /// <summary>A configuration chosen as a person chooses it: the widget clicked, then its item in the menu.</summary>
    private static void Choose(MainWindow window, string shown)
    {
        Ui.Click(window.FindControl<Button>("BuildConfigButton")!);
        Ui.WaitFor(() => window.OpenContextMenu is { IsOpen: true }, "the menu of the build configurations");
        Ui.Click(window.OpenContextMenu!.Items.OfType<MenuItem>().Single(i => i.Header as string == shown));
        Ui.WaitFor(() => window.FindControl<TextBlock>("BuildConfigName")!.Text == shown, () => $"the widget to name {shown}: {Ui.Said(window)}");
    }

    /// <summary>The configurations the widget's menu would offer, as shown, in order.</summary>
    private static string Offered(MainWindow window) =>
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
