using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Run;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, opened in the window: its one run configuration is named for it, and the
/// menu of where it runs offers this machine's head (Windows on Windows, Mac Catalyst on a Mac) beside Android, its
/// devices and emulators or what Android lacks here. With all it needs here, it says nothing lacking and builds every
/// head. Run builds this machine's head: on Windows it runs as the WinUI app it is; on a Mac the system opens it hidden,
/// never in front. What it writes is in the Run window, and Stop ends it; on Windows with Hot Reload, under dotnet watch,
/// a saved edit reaches it as it runs. Skips, saying why, without MAUI's workload, whose template makes the app, or, for a
/// build, without the workload of any head the app's restore reads. Its packages come from nuget.org, as no restore of
/// the solution's brings them, so it is never one of the gate's.</summary>
public sealed class MauiTemplateRunTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(15);

    /// <summary>What the app writes once its main page is made, which the Run window shows.</summary>
    private const string Up = "MauiApp1's main page is up";

    [AvaloniaFact]
    public void The_maui_templates_app_is_listed_with_this_machines_head_and_android_to_choose_from_and_its_windows_head_runs_here()
    {
        string folder = Made();
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                Ui.WaitFor(() => window.Session.Projects.Any(p => p.IsEvaluated) && window.Configurations.Count > 0,
                    () => $"the app's project evaluated and its configurations made; it has {string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
                Assert.Equal(["MauiApp1"], window.Configurations.Select(c => c.Name));
                Assert.Equal("MauiApp1", window.Session.Configurations.Chosen?.Name);

                Ui.Click(window.FindControl<Button>("RunTargetButton")!);
                Ui.WaitFor(() => window.OpenContextMenu is not null, () => $"the menu of where the app runs: {Ui.Said(window)}", Slow);
                ContextMenu menu = window.OpenContextMenu!;
                List<string> offered = [.. menu.Items.OfType<MenuItem>().Select(m => m.Header switch
                {
                    string words => words,
                    TextBlock block => block.Text ?? string.Empty,
                    _ => string.Empty,
                })];
                ProjectModel app = window.Session.Projects.Single();
                IReadOnlyList<DeviceTarget> listed = window.Session.Targets.Listed(app);
                Assert.True(listed.Any(t => t.Platform == TargetPlatform.Android) || offered.Any(o => o.Contains("Android", StringComparison.Ordinal)),
                    $"The menu offers nothing of Android, neither a device or emulator nor what Android lacks here: {string.Join(" | ", offered)}");
                if (OperatingSystem.IsMacOS())
                {
                    Assert.Contains(DeviceTargets.Here(TargetPlatform.MacCatalyst).Shown, offered);
                }
                if (!OperatingSystem.IsWindows())
                {
                    menu.Close();
                    return;
                }
                Assert.Contains(DeviceTargets.Here(TargetPlatform.Windows).Shown, offered);
                menu.Close();

                Built(window);
                Ui.Launched(window, window.RunAsync(), "the Windows head's build and start", Slow);
                Assert.Equal("net11.0-windows10.0.19041.0", window.Session.RunPlan?.Framework);
                Ui.WaitFor(() => Output(window).Contains(Up, StringComparison.Ordinal),
                    () => $"the WinUI app to make its main page, said in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
                Assert.True(window.Runner.IsRunning, $"The app ended once its page was up:\n{Output(window)}");
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the app to end once stopped", TimeSpan.FromMinutes(1));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Opened where this machine has all the app needs, the banner says nothing lacking and Build builds each head
    /// the app targets here (on a Mac Android's, iOS's for the simulator and Mac Catalyst's) to its app. Where the banner
    /// names something lacking, the build checks it: a JDK, the Android SDK or Xcode said lacking fails it, a simulator
    /// runtime, which only a run needs, does not, and the test skips saying what this machine lacks.</summary>
    [AvaloniaFact]
    public void The_maui_templates_app_says_nothing_lacking_where_this_machine_has_all_it_needs_and_builds_every_head_here()
    {
        string folder = Made();
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                Ui.WaitFor(() => window.Session.Projects.Any(p => p.IsEvaluated), () => $"the app's project to evaluate: {Ui.Said(window)}", Slow);
                Built(window);
                MobileWant want = window.MobileWant;
                string? said = window.MobileSaid;
                IReadOnlyList<string> heads = window.Session.Projects.Single().TargetFrameworks;
                Assert.True(window.Perform(AppActions.Build), $"The app's build could not begin: {window.Status}");
                Ui.WaitFor(() => !window.IsBuilding, () => $"the app's build to end: {window.Status}", Slow);
                bool built = window.Status?.StartsWith("Build succeeded", StringComparison.Ordinal) == true;
                if (said is not null)
                {
                    Assert.False(built && (want.Java is not null || want.NoAndroidSdk || want.Xcode is not null),
                        $"The banner says the app lacks what its heads build with, yet every head built: {said}");
                    Assert.Skip(built ? $"Every head built, but this machine lacks what the app runs with, as the banner says: {said}"
                        : $"This machine lacks what the app builds with, as the banner says: {said}\n{Ui.Said(window)}");
                }
                Assert.True(built, $"The banner said nothing lacking, yet the app's build failed: {Ui.Said(window)}");
                string output = Path.Combine(folder, "MauiApp1", "bin", "Debug");
                foreach (string head in heads)
                {
                    string place = Path.Combine(output, head);
                    string[] made = Directory.Exists(place) ? [.. Directory.EnumerateFileSystemEntries(place, "*", SearchOption.AllDirectories).Select(Path.GetFileName).OfType<string>()] : [];
                    string? platform = FrameworkNames.PlatformOf(head);
                    string app = platform == FrameworkNames.Android ? $"{MauiApp.Package}-Signed.apk"
                        : platform is not null && MobilePlatforms.IsApple(platform) ? "MauiApp1.app"
                        : "MauiApp1.dll";
                    Assert.True(made.Contains(app), $"The build said it succeeded, but {head}'s head made no {app} in {place}");
                }
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>On a Mac, Run with nothing chosen builds the app's Mac Catalyst head and the system opens it as an app, hidden
    /// and never in front as nobody sees this window: what it writes is in the Run window while it runs, and Stop ends it,
    /// the app gone with its opener. Its Debug build trims .NET's own assemblies, so the Run window offers to keep them
    /// whole, which writes MtouchLink None for its Debug build.</summary>
    [AvaloniaFact]
    public void On_a_mac_run_opens_the_maui_apps_mac_catalyst_head_hidden_its_output_in_the_run_window_and_stop_ends_it()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("A .NET MAUI app's Mac Catalyst head runs only on a Mac");
        }
        string folder = Made();
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            try
            {
                Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "MauiApp1"), () => $"the app's configuration: {Ui.Said(window)}", Slow);
                Assert.Equal(DeviceTargets.Here(TargetPlatform.MacCatalyst).Shown, window.FindControl<TextBlock>("RunTargetName")!.Text);
                Assert.True(window.Unseen, "The test's window is one a person sees, so the app would open on their screen");
                Built(window);
                Ui.Launched(window, window.RunAsync(), "the Mac Catalyst head's build and its opening", Slow);
                LaunchPlan plan = window.Session.RunPlan ?? throw new Xunit.Sdk.XunitException("Run made no plan");
                Assert.Equal("net11.0-maccatalyst", plan.Framework);
                MacCatalystApp app = plan.MacCatalyst?.App ?? throw new Xunit.Sdk.XunitException($"The plan opens no Mac Catalyst app: it runs {plan.Program}");
                IReadOnlyList<string> opening = [.. plan.Arguments.Take(plan.Leading)];
                Assert.True(opening.Contains("-g") && opening.Contains("-j"),
                    $"The app opens where the person sees it, though nobody sees the window that ran it: {string.Join(' ', opening.Skip(3))}");
                Ui.WaitFor(() => Output(window).Contains(Up, StringComparison.Ordinal),
                    () => $"the app to make its main page, said in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
                Assert.True(window.Runner.IsRunning, $"The app's run ended once its page was up:\n{Output(window)}");
                Assert.True(Running(app) is not null, $"No process runs {app.Executable} once its page is up");
                // The template's Debug build trims .NET's own assemblies: the Run window offers to keep them whole.
                Assert.False(app.Whole, "The template's Mac Catalyst Debug build no longer trims .NET's own assemblies");
                Assert.Equal($"MauiApp1's Debug build trims .NET's own assemblies, so {MacCatalystApp.KeptOut}", window.BottomPanel.Run.Offered);
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the app's run to end once stopped", TimeSpan.FromMinutes(1));
                Ui.WaitFor(() => Running(app) is null, () => $"the app to end once stopped; process {Running(app)} still runs it", TimeSpan.FromSeconds(30));
                string project = Path.Combine(folder, "MauiApp1", "MauiApp1.csproj");
                Ui.ClickButton(window.BottomPanel.Run, "Keep .NET's Assemblies Whole in Debug");
                Ui.WaitFor(() => window.Actions.Work.Of(AppActions.KeepAssembliesWhole) is null && File.ReadAllText(project).Contains("'Debug'\">None</MtouchLink>", StringComparison.Ordinal),
                    () => $"the Run window's offer to keep the Debug build whole: {Ui.Said(window)}\n{File.ReadAllText(project)}", Slow);
                Assert.Equal(Passenger.App.Mobile.MobileHeads.Whole("MauiApp1"), window.Status);
                Assert.Null(window.BottomPanel.Run.Offered);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>The process running the app's program; null where none does.</summary>
    private static int? Running(MacCatalystApp app)
    {
        foreach (System.Diagnostics.Process process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                if (Libc.ExecutableOf(process.Id) is { } program && PathNames.Same(program, app.Executable))
                {
                    return process.Id;
                }
            }
        }
        return null;
    }

    /// <summary>Run with Hot Reload on Windows runs the app's Windows head under dotnet watch, as a program of this machine:
    /// an edit of the method it calls each second, saved, reaches the running WinUI app, what it writes after being the
    /// edit's, in the Run window; Stop ends it.</summary>
    [AvaloniaFact]
    public void Run_with_hot_reload_on_windows_applies_a_saved_edit_to_the_maui_apps_windows_head()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("A .NET MAUI app's Windows head runs only on Windows");
        }
        string folder = Made();
        try
        {
            MainWindow window = Ui.OpenFolder(folder, languageServer: false);
            window.ApplySettings(window.Settings with { HotReload = true });
            try
            {
                Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "MauiApp1"), () => $"the app's configuration: {Ui.Said(window)}", Slow);
                Built(window);
                Ui.Launched(window, window.RunAsync(), "the Windows head's start under dotnet watch", Slow);
                Ui.WaitFor(() => Output(window).Contains(Ticks, StringComparison.Ordinal),
                    () => $"the WinUI app to tick, said in the Run window; it shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
                window.OpenFile(Path.Combine(folder, "MauiApp1", "MainPage.xaml.cs"));
                Typing.Replace(window, $"\"{Ticks}\"", "\"MauiApp1 ticks after Hot Reload\"");
                Ui.Run(window, AppActions.SaveAll);
                Ui.WaitFor(() => Output(window).Contains("MauiApp1 ticks after Hot Reload", StringComparison.Ordinal),
                    () => $"the running app to tick as the saved edit does; the Run window shows:\n{Output(window)}\n{Ui.Said(window)}", Slow);
                Ui.Run(window, AppActions.Stop);
                Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the app to end once stopped", TimeSpan.FromMinutes(1));
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            TestFolders.Delete(folder);
        }
    }

    /// <summary>What the app writes each second, from a method a Hot Reload edit changes.</summary>
    private const string Ticks = "MauiApp1 ticks as built";

    /// <summary>A folder holding the app <c>dotnet new maui</c> makes, restoring from nuget.org, its main page saying when it
    /// is up and then each second what <see cref="Ticks"/> says; skipped, saying why, where this .NET makes no MAUI app.</summary>
    private static string Made()
    {
        string folder = PathNames.Canonical(TestFolders.Create("maui-template-"));
        ChildResult made = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
            ["new", "maui", "--output", Path.Combine(folder, "MauiApp1"), "--name", "MauiApp1", "--no-restore"], folder), limit: TimeSpan.FromMinutes(3))),
            "dotnet new maui", TimeSpan.FromMinutes(4));
        if (!made.Succeeded)
        {
            TestFolders.Delete(folder);
            Assert.Skip($"This .NET makes no .NET MAUI app, so it lacks MAUI's workload (dotnet workload install maui): {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        string page = Path.Combine(folder, "MauiApp1", "MainPage.xaml.cs");
        string code = File.ReadAllText(page).ReplaceLineEndings("\n");
        Assert.True(code.Contains("\t\tInitializeComponent();\n\t}", StringComparison.Ordinal), $"The template's main page builds its component otherwise: {code}");
        File.WriteAllText(page, code.Replace("\t\tInitializeComponent();\n\t}", $"\t\tInitializeComponent();\n\t\tConsole.WriteLine(\"{Up}\");\n"
            + $"\t\tticking = new Timer(_ => Console.WriteLine(Ticked()), null, 1000, 1000);\n\t}}\n\n\tTimer? ticking;\n\n\tstatic string Ticked() => \"{Ticks}\";", StringComparison.Ordinal));
        return folder;
    }

    /// <summary>Skips, saying which, where this .NET lacks a workload of the app's heads: its restore reads every head, so a
    /// build of one needs the workload of each.</summary>
    private static void Built(MainWindow window)
    {
        Ui.Await(window.MobileAsked(), "what the app's heads need to be asked", Slow);
        if (window.MobileWant.Workloads.Count > 0)
        {
            Assert.Skip($"This .NET lacks the workloads {string.Join(", ", window.MobileWant.Workloads)}, without which the app's restore reads none of its heads");
        }
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
