using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.LiveTree;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Console programs that use Avalonia's libraries and open none of its windows, run from the window as a person
/// runs them: Shapes takes Avalonia.Base alone, as a library's tool or a test harness does; Sketch and Easel take the Avalonia
/// package, Avalonia.Controls with it, and make no Application; and Gallery, an Avalonia project too, has its RunCommand
/// run Shapes, so the inspector Gallery's build is given goes into a program it cannot serve. Each lives a second, Easel
/// eight, long enough for anything of the inspector's to end it first, and ends with its own exit code.</summary>
public sealed partial class AvaloniaLibraryProgramsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public AvaloniaLibraryProgramsTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("avalonia-libraries-", Write));
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void In_a_persons_window_programs_using_avalonias_libraries_and_none_of_its_windows_run_to_their_own_end() => RunEach(unseen: false);

    /// <summary>A window nobody sees (an agent's own Passenger's) keeps an Avalonia app off the screen, which none of these
    /// programs can reach: each runs as it would without Passenger.</summary>
    [AvaloniaFact]
    public void In_a_window_nobody_sees_programs_using_avalonias_libraries_and_none_of_its_windows_run_to_their_own_end() => RunEach(unseen: true);

    /// <summary>Easel counts the inspector's looks by the logger it takes: Easel sets a sink of its own again each time it
    /// finds the inspector's in its place, as an app setting itself up does.</summary>
    [AvaloniaFact]
    public void In_a_persons_window_a_program_with_avalonias_controls_and_no_application_wakes_the_inspector_seldom_after_its_first_seconds()
    {
        MainWindow window = Opened(unseen: false);
        try
        {
            string easel = RunToItsEnd(window, "Easel");
            Match looks = Looks().Match(easel);
            Assert.True(Ran(easel, "Easel", 11) && looks.Success, $"Easel did not run to its own end with its own output:\n{easel}");
            Assert.True(int.Parse(looks.Groups["first"].Value) > 0, $"The inspector never looked at Easel's logger, so Easel counted nothing of it:\n{easel}");
            int later = int.Parse(looks.Groups["later"].Value);
            int seconds = int.Parse(looks.Groups["ms"].Value) / 1000;
            Assert.True(later <= seconds + 2,
                $"The inspector looked {later} times in {seconds} s once Easel's first seconds had passed, waking a program that never makes an Application all its life:\n{easel}");
        }
        finally
        {
            window.Close();
        }
    }

    private void RunEach(bool unseen)
    {
        MainWindow window = Opened(unseen);
        try
        {
            string shapes = RunToItsEnd(window, "Shapes");
            Assert.True(Ran(shapes, "Shapes", 7), $"Shapes, which takes Avalonia.Base alone, did not run to its own end with its own output:\n{shapes}");
            Assert.True(window.Session.RunApp is null,
                "Shapes, which takes Avalonia.Base alone, was given the inspector, which has no Avalonia.Controls there to find an app's windows with");

            string sketch = RunToItsEnd(window, "Sketch");
            Assert.True(Ran(sketch, "Sketch", 9), $"Sketch, which takes Avalonia.Controls and makes no Application, did not run to its own end with its own output:\n{sketch}");

            string gallery = RunToItsEnd(window, "Gallery");
            Assert.True(Ran(gallery, "Shapes", 7), $"Shapes run by Gallery's RunCommand, given the inspector by Gallery's build, did not run to its own end with its own output:\n{gallery}");
            Assert.True(window.Session.RunApp?.Problem?.Contains("Avalonia.Controls", StringComparison.Ordinal) == true,
                $"The inspector in Shapes did not say why it could not serve it, where the app tools and the Live Visual Tree would show it: {window.Session.RunApp?.Problem}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A window on the folder, once every project's configuration is listed.</summary>
    private MainWindow Opened(bool unseen)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Unseen = unseen };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        Ui.WaitFor(() => window.Configurations.Count(c => c.Name is "Shapes" or "Sketch" or "Easel" or "Gallery") == 4,
            () => $"each project's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})");
        return window;
    }

    /// <summary>The Run window's text once a configuration's program, run from the window, has ended.</summary>
    private static string RunToItsEnd(MainWindow window, string configuration)
    {
        Assert.True(window.SelectConfiguration(configuration), $"There is no configuration {configuration}: {string.Join(", ", window.Configurations.Select(c => c.Name))}");
        // Emptied first, so the end waited for is this run's, not the one before's.
        window.BottomPanel.Run.Clear();
        Assert.True(window.Perform(AppActions.RunProgram), $"Run was refused for {configuration}: {Ui.Said(window)}");
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), () => $"{configuration} to end ({Ui.Said(window)}):\n{Output(window)}", TimeSpan.FromMinutes(2));
        return Output(window);
    }

    /// <summary>Whether a program wrote both its lines and ended with its own exit code.</summary>
    private static bool Ran(string output, string program, int exitCode) =>
        output.Contains($"{program} began", StringComparison.Ordinal) && output.Contains($"{program} ended", StringComparison.Ordinal)
        && output.Contains($"Process finished with {ExitCodes.Said(exitCode)}.", StringComparison.Ordinal);

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();

    /// <summary>What Easel counted of the inspector's looks, in its first seconds and in the time after.</summary>
    [GeneratedRegex(@"its logger taken (?<first>\d+) times in its first seconds, (?<later>\d+) times in the (?<ms>\d+) ms after")]
    private static partial Regex Looks();

    /// <summary>The solution of the four projects, to restore.</summary>
    private static string Write(string made)
    {
        TestFolders.Write(made, "Drawing.slnx", "<Solution>\n" + string.Concat(((string[])["Easel", "Gallery", "Shapes", "Sketch"])
            .Select(project => $"  <Project Path=\"{project}/{project}.csproj\" />\n")) + "</Solution>\n");
        // Avalonia.Base from the package's own folder, as Passenger's inspector takes Avalonia's assemblies, nothing else of it.
        TestFolders.Write(made, "Shapes/Shapes.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>").With($"""
              <ItemGroup>
                <PackageReference Include="Avalonia" Version="{AvaloniaApp.Version}" ExcludeAssets="all" GeneratePathProperty="true" />
                <Reference Include="$(PkgAvalonia)/lib/net8.0/Avalonia.Base.dll" />
              </ItemGroup>

            """));
        TestFolders.Write(made, "Shapes/Program.cs", """
            using Avalonia;
            using Avalonia.Media;

            Console.WriteLine("Shapes began");
            Rect union = new Rect(0, 0, 10, 10).Union(new Rect(5, 5, 20, 20));
            Thread.Sleep(1000);
            Console.WriteLine($"Shapes ended, {union.Width} wide in {Color.Parse("#FF8800")}");
            return 7;

            """);
        TestFolders.Write(made, "Sketch/Sketch.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>").With(AvaloniaApp.Packages("Avalonia")));
        TestFolders.Write(made, "Sketch/Program.cs", """
            using Avalonia.Controls;

            Console.WriteLine("Sketch began");
            GridLength star = new(2, GridUnitType.Star);
            Thread.Sleep(1000);
            Console.WriteLine($"Sketch ended, {star} wide");
            return 9;

            """);
        TestFolders.Write(made, "Easel/Easel.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>").With(AvaloniaApp.Packages("Avalonia")));
        TestFolders.Write(made, "Easel/Program.cs", """
            using System.Diagnostics;
            using Avalonia.Controls;
            using Avalonia.Logging;

            Console.WriteLine("Easel began");
            Own own = new();
            GridLength star = new(2, GridUnitType.Star);
            Stopwatch since = Stopwatch.StartNew();
            int first = Taken(TimeSpan.FromSeconds(5));
            long from = since.ElapsedMilliseconds;
            int later = Taken(TimeSpan.FromSeconds(3));
            Console.WriteLine($"Easel ended, {star} wide: its logger taken {first} times in its first seconds, {later} times in the {since.ElapsedMilliseconds - from} ms after");
            return 11;

            // Each time the logger is found taken from Easel's own sink is one of the inspector's looks.
            int Taken(TimeSpan stretch)
            {
                int taken = 0;
                Stopwatch counting = Stopwatch.StartNew();
                Logger.Sink = own;
                while (counting.Elapsed < stretch)
                {
                    if (Logger.Sink != own)
                    {
                        taken++;
                        Logger.Sink = own;
                    }
                    Thread.Sleep(1);
                }
                return taken;
            }

            sealed class Own : ILogSink
            {
                public bool IsEnabled(LogEventLevel level, string area) => false;

                public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
                {
                }

                public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
                {
                }
            }

            """);
        // Its RunCommand runs Shapes, which its reference builds first.
        TestFolders.Write(made, "Gallery/Gallery.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>",
            "<RunCommand>dotnet</RunCommand>", "<RunArguments>\"$(MSBuildProjectDirectory)/../Shapes/bin/$(Configuration)/$(TargetFramework)/Shapes.dll\"</RunArguments>")
            .With(AvaloniaApp.Packages("Avalonia")).With("  <ItemGroup>\n    <ProjectReference Include=\"../Shapes/Shapes.csproj\" ReferenceOutputAssembly=\"false\" />\n  </ItemGroup>\n"));
        TestFolders.Write(made, "Gallery/Program.cs", "Console.WriteLine(\"Gallery ran itself\");\n");
        // Restored by its real path, so a project the others reference is never restored under two spellings at once.
        return Path.Combine(PathNames.Canonical(made), "Drawing.slnx");
    }
}
