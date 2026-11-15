using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.LiveTree;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Console programs that use Avalonia's libraries and open none of its windows, run from the window as a person
/// runs them: Shapes takes Avalonia.Base alone, as a library's tool or a test harness does; Sketch takes the Avalonia
/// package, Avalonia.Controls with it, and makes no Application; and Gallery, an Avalonia project too, has its RunCommand
/// run Shapes, so the inspector Gallery's build is given goes into a program it cannot serve. Each lives a second, long
/// enough for anything of the inspector's to end it first, and ends with its own exit code.</summary>
public sealed class AvaloniaLibraryProgramsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;

    public AvaloniaLibraryProgramsTests(RestoredFolders folders)
    {
        folder = PathNames.Canonical(folders.Take("avalonia-libraries-", Write));
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void In_a_persons_window_programs_using_avalonias_libraries_and_none_of_its_windows_run_to_their_own_end() => RunEach(unseen: false);

    /// <summary>A window nobody sees (an agent's own Passenger's) keeps an Avalonia app off the screen, which neither program
    /// can reach: each runs as it would without Passenger.</summary>
    [AvaloniaFact]
    public void In_a_window_nobody_sees_programs_using_avalonias_libraries_and_none_of_its_windows_run_to_their_own_end() => RunEach(unseen: true);

    private void RunEach(bool unseen)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Unseen = unseen };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.Configurations.Count(c => c.Name is "Shapes" or "Sketch" or "Gallery") == 3,
                () => $"each project's configuration ({string.Join(", ", window.Configurations.Select(c => c.Name))})");

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

    /// <summary>The solution of the three projects.</summary>
    private static string Write(string made)
    {
        TestFolders.Write(made, "Drawing.slnx", "<Solution>\n  <Project Path=\"Gallery/Gallery.csproj\" />\n  <Project Path=\"Shapes/Shapes.csproj\" />\n  <Project Path=\"Sketch/Sketch.csproj\" />\n</Solution>\n");
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
        // Its RunCommand runs Shapes, which its reference builds first.
        TestFolders.Write(made, "Gallery/Gallery.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>",
            "<RunCommand>dotnet</RunCommand>", "<RunArguments>\"$(MSBuildProjectDirectory)/../Shapes/bin/$(Configuration)/$(TargetFramework)/Shapes.dll\"</RunArguments>")
            .With(AvaloniaApp.Packages("Avalonia")).With("  <ItemGroup>\n    <ProjectReference Include=\"../Shapes/Shapes.csproj\" ReferenceOutputAssembly=\"false\" />\n  </ItemGroup>\n"));
        TestFolders.Write(made, "Gallery/Program.cs", "Console.WriteLine(\"Gallery ran itself\");\n");
        return Path.Combine(made, "Drawing.slnx");
    }
}
