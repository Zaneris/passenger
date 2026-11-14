using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.Workspace;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A run configuration names a build configuration of its own in Edit Configurations, kept with it, and every start
/// of it builds and runs as that one while the rest stay on the folder's: Profile of a copy set to Release profiles the Release
/// build while Run of the project's own stays on Debug, and Run with Hot Reload of one set to x64 has dotnet watch build
/// the x64 build. Hot Reload while debugging one applies no edit while the editor's code is read as another build, saying
/// why, and applies them once the folder builds as it does. Profiles, debugs and runs under dotnet watch, held to the
/// profiler's and dotnet watch's times, so run alone.</summary>
[Collection(Alone.Name)]
public sealed class RunConfigurationBuildTests : IDisposable
{
    /// <summary>The program: the folder it was built into, then a line of its own for Debug's build and another for every
    /// other's.</summary>
    private const string Program = """
        System.Console.WriteLine($"built into {System.AppContext.BaseDirectory}");
        #if DEBUG
        System.Console.WriteLine("debug");
        #else
        System.Console.WriteLine("optimized");
        #endif

        """;

    private readonly string folder = PathNames.Canonical(TestFolders.Create("run-configuration-build-"));

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_configuration_set_to_release_profiles_the_release_build_while_run_stays_on_the_folders_debug_and_dotnet_watch_takes_its_platform()
    {
        // x64 only puts the build in a folder of its own: the program still runs on any processor.
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console("<Platforms>AnyCPU;x64</Platforms>", "<PlatformTarget>AnyCPU</PlatformTarget>"));
        TestFolders.Write(folder, "Hello/Program.cs", Program);
        Restore.Run(Path.Combine(folder, "Hello", "Hello.csproj"));
        string bin = Path.Combine(folder, "Hello", "bin");
        string debug = Path.Combine(bin, "Debug", TestProjects.Framework) + Path.DirectorySeparatorChar;
        string release = Path.Combine(bin, "Release", TestProjects.Framework) + Path.DirectorySeparatorChar;
        string x64 = Path.Combine(bin, "x64", "Debug", TestProjects.Framework) + Path.DirectorySeparatorChar;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => BuildConfigurationTests.Offered(window) == "Debug | AnyCPU, Debug | x64, Release | AnyCPU, Release | x64",
                () => $"each configuration and platform the project declares offered, where the widget offers {BuildConfigurationTests.Offered(window)}");
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Hello"), () => $"Hello's configuration, where there are {string.Join(", ", window.Configurations.Select(c => c.Name))}");

            Assert.True(window.SelectConfiguration("Hello"));
            RunConfigurationsDialog dialog = DialogButtonsTests.Check(window.ShowRunConfigurations());
            Assert.Equal("Hello", dialog.Chosen?.Name);
            Assert.True(dialog.BuildConfigurationBox.IsEffectivelyVisible, "Edit Configurations shows no build configuration for a project's configuration");
            Assert.Equal(["The folder's (Debug | AnyCPU)", "Debug | AnyCPU", "Debug | x64", "Release | AnyCPU", "Release | x64"], Items(dialog));
            Assert.Equal(0, dialog.BuildConfigurationBox.SelectedIndex);
            Ui.Click(dialog.CopyButton);
            Assert.Equal("Hello (1)", dialog.Chosen?.Name);
            Choose(dialog, "Release | AnyCPU");
            // A copy of the copy, named for what it builds as.
            Ui.Click(dialog.CopyButton);
            Assert.Equal("Release | AnyCPU", Shown(dialog));
            dialog.NameBox.Text = "Hello x64";
            Ui.Settle();
            Choose(dialog, "Debug | x64");
            Ui.Click(dialog.OkButton);
            Ui.Settle();
            Assert.Equal("Release|AnyCPU", window.Configurations.Single(c => c.Name == "Hello (1)").BuildConfiguration);
            Assert.Equal("Debug|x64", window.Configurations.Single(c => c.Name == "Hello x64").BuildConfiguration);
            Assert.Equal(string.Empty, window.Configurations.Single(c => c.Name == "Hello").BuildConfiguration);

            // Edit Configurations shows each as it was kept.
            dialog = window.ShowRunConfigurations();
            Assert.Equal("Hello x64", dialog.Chosen?.Name);
            Assert.Equal("Debug | x64", Shown(dialog));
            dialog.Close();
            Ui.Settle();

            Assert.True(window.SelectConfiguration("Hello (1)"));
            Ui.Launched(window, window.ProfileAsync(), "the profiled start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null, () => $"the profiled program's end to show its snapshot; the Run window shows:\n{Output(window)}",
                TimeSpan.FromMinutes(2));
            Assert.True(Output(window).Contains($"built into {release}", StringComparison.Ordinal) && Output(window).Contains("optimized", StringComparison.Ordinal),
                $"Profile of the configuration set to Release did not start the program its Release build made, in {release}:\n{Output(window)}");
            Assert.False(Directory.Exists(Path.Combine(bin, "Debug")), "Profile of the configuration set to Release built the program as Debug, the folder's");

            Assert.True(window.SelectConfiguration("Hello"));
            Ui.Launched(window, window.RunAsync(), "the run of the project's own configuration", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, () => $"the run to end; the Run window shows:\n{Output(window)}", TimeSpan.FromMinutes(2));
            Assert.True(Output(window).Contains($"built into {debug}", StringComparison.Ordinal) && Output(window).Split('\n').Any(line => line.Trim() == "debug"),
                $"Run of the configuration naming none did not run the folder's Debug build, in {debug}:\n{Output(window)}");

            Assert.True(window.SelectConfiguration("Hello x64"));
            window.ApplySettings(window.Settings with { HotReload = true });
            Assert.True(window.Perform(AppActions.RunProgram), $"Run with Hot Reload did not start: {window.Status}");
            Ui.WaitFor(() => Output(window).Contains("Waiting for a file to change", StringComparison.Ordinal),
                () => $"dotnet watch to run the program and wait for a change; the status bar says \"{window.Status}\" and the Run window:\n{Output(window)}", TimeSpan.FromMinutes(3));
            Assert.True(Output(window).Contains($"built into {x64}", StringComparison.Ordinal),
                $"Run with Hot Reload of the configuration set to x64 did not have dotnet watch run the program's x64 build, in {x64}:\n{Output(window)}");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.IsRunning, "dotnet watch to end");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Greets once every 100 ms, numbered, with a line of its own where built as Debug; an x64 build is told by a
    /// warning the language server says only where it reads the code as x64.</summary>
    private const string Greeter = """
        static class Greeter
        {
        #if X64
        #warning read as x64
        #endif
            public static void Greet(int i)
            {
        #if DEBUG
                System.Console.WriteLine($"debug {i}");
        #endif
                System.Console.WriteLine($"greeting {i}");
            }
        }

        """;

    [AvaloniaFact]
    public void Hot_reload_of_a_debugged_configuration_building_as_another_than_the_folder_applies_nothing_saying_why_until_the_folder_builds_as_it()
    {
        TestFolders.Write(folder, "Hello/Hello.csproj", TestProjects.Console("<Platforms>AnyCPU;x64</Platforms>", "<PlatformTarget>AnyCPU</PlatformTarget>",
            "<DefineConstants Condition=\"'$(Platform)' == 'x64'\">$(DefineConstants);X64</DefineConstants>"));
        TestFolders.Write(folder, "Hello/Program.cs", "for (int i = 1; ; i++)\n{\n    Greeter.Greet(i);\n    System.Threading.Thread.Sleep(100);\n}\n");
        TestFolders.Write(folder, "Hello/Greeter.cs", Greeter);
        Restore.Run(Path.Combine(folder, "Hello", "Hello.csproj"));
        string greeter = Path.Combine(folder, "Hello", "Greeter.cs");
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.WaitFor(() => BuildConfigurationTests.Offered(window) == "Debug | AnyCPU, Debug | x64, Release | AnyCPU, Release | x64",
                () => $"each configuration and platform the project declares offered, where the widget offers {BuildConfigurationTests.Offered(window)}");
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Hello"), () => $"Hello's configuration, where there are {string.Join(", ", window.Configurations.Select(c => c.Name))}");
            Assert.True(window.SelectConfiguration("Hello"));
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Ui.Click(dialog.CopyButton);
            Choose(dialog, "Release | AnyCPU");
            Ui.Click(dialog.CopyButton);
            dialog.NameBox.Text = "Hello x64";
            Ui.Settle();
            Choose(dialog, "Debug | x64");
            Ui.Click(dialog.OkButton);
            Ui.Settle();

            // Built as Release, an edit to code only Debug compiles is code the program does not run.
            Assert.True(window.SelectConfiguration("Hello (1)"));
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Debugged(window).Contains("greeting 2", StringComparison.Ordinal), () => $"the Release build to greet:\n{Debugged(window)}", TimeSpan.FromMinutes(2));
            Assert.DoesNotContain("debug 1", Debugged(window), StringComparison.Ordinal);
            window.OpenFile(greeter);
            Typing.Replace(window, "$\"debug {i}\"", "$\"debugged {i}\"");
            string release = Refusals.BuiltUnlike("Hello (1)", "Hello", "Release|AnyCPU", "Debug|AnyCPU");
            Assert.Equal(release, window.Debugger.WhyNotApply());
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Assert.True(window.Status?.Contains(release, StringComparison.Ordinal) == true, $"Apply Hot Reload Changes did not say why the Release build takes no edit: {window.Status}");
            Assert.Null(window.Debugger.LastApplied);
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");

            // Built as x64, its edits wait for the editor's code to be read as x64, and are then applied.
            Assert.True(window.SelectConfiguration("Hello x64"));
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => Debugged(window).Contains("greeting 2", StringComparison.Ordinal), () => $"the x64 build to greet:\n{Debugged(window)}", TimeSpan.FromMinutes(2));
            // The edit made under Release is saved as the build begins: Debug's line is said either way.
            Assert.True(DebugLines(Debugged(window)) > 0, $"the x64 build, a Debug one, did not say Debug's line:\n{Debugged(window)}");
            window.OpenFile(greeter);
            OpenDocument document = window.EditorArea.Active!;
            Typing.Replace(window, "$\"greeting {i}\"", "$\"howdy {i}\"");
            Assert.Equal(Refusals.BuiltUnlike("Hello x64", "Hello", "Debug|x64", "Debug|AnyCPU"), window.Debugger.WhyNotApply());
            BuildConfigurationTests.Choose(window, "Debug | x64");
            Ui.WaitFor(() => window.CSharpService?.For(document)?.Diagnostics.Any(d => d.Code?.ToString() == "CS1030") == true,
                () => $"the language server to read the code as x64, warning as only x64 does{Ui.ServerLogTail()}", TimeSpan.FromMinutes(2));
            Assert.Null(window.Debugger.WhyNotApply());
            Ui.Run(window, AppActions.ApplyHotReloadChanges);
            Ui.WaitFor(() => window.Debugger.LastApplied is not null, "Hot Reload to say what it applied");
            Assert.True(window.Debugger.LastApplied!.Result == HotReloadResult.Applied, $"the x64 build did not take the edit once the folder built as x64: {window.Debugger.LastApplied.Said}");
            int from = Debugged(window).Length;
            Ui.WaitFor(() => Debugged(window)[from..].Contains("howdy", StringComparison.Ordinal), () => $"the program to greet as the edited code does:\n{Debugged(window)}");
            Ui.WaitFor(() => DebugLines(Debugged(window)[from..]) >= 2, () => $"the edited code to keep what only Debug compiles:\n{Debugged(window)}");
            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        }
        finally
        {
            window.Close();
        }
    }

    private static string Debugged(MainWindow window) => window.Debugger.Output().ToString();

    private static int DebugLines(string output) => output.Split('\n').Count(line => line.StartsWith("debug", StringComparison.Ordinal));

    private static IReadOnlyList<string> Items(RunConfigurationsDialog dialog) =>
        [.. dialog.BuildConfigurationBox.Items.OfType<ComboBoxItem>().Select(item => item.Content as string ?? string.Empty)];

    private static string Shown(RunConfigurationsDialog dialog) => (dialog.BuildConfigurationBox.SelectedItem as ComboBoxItem)?.Content as string ?? string.Empty;

    private static void Choose(RunConfigurationsDialog dialog, string shown)
    {
        dialog.BuildConfigurationBox.SelectedIndex = Items(dialog).ToList().IndexOf(shown);
        Ui.Settle();
        Assert.Equal(shown, Shown(dialog));
    }

    private static string Output(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }
}
