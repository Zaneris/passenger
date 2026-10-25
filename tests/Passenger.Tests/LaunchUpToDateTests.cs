using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Run of a project whose build would read nothing changed since its last build starts no dotnet build, the Build
/// window saying it is up to date; one whose build reads something changed (a C# file typed into and not saved, its
/// project file, a .props it imports, a project it references, the version of a package it references) is built first,
/// the program then running what changed. EF Core's two versions come from the run's local feed.</summary>
public sealed class LaunchUpToDateTests
{
    /// <summary>The longest a build and a run of the app take on a developer's machine, scaled by the run.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(2);

    [AvaloniaFact]
    public void A_run_with_nothing_its_build_reads_changed_starts_no_build_and_each_change_builds_first()
    {
        string folder = Write();
        string app = Path.Combine(folder, "App", "App.csproj");
        List<string> builds = [];
        EventHandler<ChildStart> started = (_, child) =>
        {
            if (child.Command.Contains(" build ", StringComparison.Ordinal) && child.Command.Contains("App.csproj", StringComparison.Ordinal))
            {
                lock (builds)
                {
                    builds.Add(child.Command);
                }
            }
        };
        ChildProcesses.Started += started;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "App"), "App's run configuration");
            Assert.True(window.SelectConfiguration("App"));
            Ran(window, builds, built: true, $"hello-1 lib-1 a proj-1 {ShopApp.Older}", "its first run");
            Ran(window, builds, built: false, $"hello-1 lib-1 a proj-1 {ShopApp.Older}", "a run with nothing changed");

            window.OpenFile(Path.Combine(folder, "App", "Program.cs"));
            Ui.Settle();
            string text = window.EditorArea.Active!.Text.Snapshot.ToString();
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(text.IndexOf("hello-1", StringComparison.Ordinal), "hello-1".Length, "hello-2")]);
            Ran(window, builds, built: true, $"hello-2 lib-1 a proj-1 {ShopApp.Older}", "a run after C# typed and not saved");
            Ran(window, builds, built: false, $"hello-2 lib-1 a proj-1 {ShopApp.Older}", "a run after that one");

            Edit(app, "Value=\"proj-1\"", "Value=\"proj-2\"");
            Ran(window, builds, built: true, $"hello-2 lib-1 a proj-2 {ShopApp.Older}", "a run after the project file changed");
            Edit(Path.Combine(folder, "Flavour.props"), "FLAVOUR_A", "FLAVOUR_B");
            Ran(window, builds, built: true, $"hello-2 lib-1 b proj-2 {ShopApp.Older}", "a run after the .props it imports changed");
            Edit(Path.Combine(folder, "Lib", "Greeting.cs"), "lib-1", "lib-2");
            Ran(window, builds, built: true, $"hello-2 lib-2 b proj-2 {ShopApp.Older}", "a run after the library it references changed");
            Edit(app, $"Version=\"{ShopApp.Older}\"", $"Version=\"{ShopApp.Version}\"");
            Ran(window, builds, built: true, $"hello-2 lib-2 b proj-2 {ShopApp.Version}", "a run after its package's version changed");
            Ran(window, builds, built: false, $"hello-2 lib-2 b proj-2 {ShopApp.Version}", "a run with nothing changed since");
        }
        finally
        {
            ChildProcesses.Started -= started;
            window.Close();
        }
    }

    /// <summary>A console app, App, that says what each of its inputs gave it: its C#, the library it references, a
    /// constant its imported .props defines, its project file's assembly metadata, and EF Core's version; restored.</summary>
    private static string Write()
    {
        string folder = TestFolders.Create("uptodate-");
        ShopApp.Feed();
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{System.Security.SecurityElement.Escape(AvaloniaApp.Feed)}" />
              </packageSources>
            </configuration>
            """);
        TestFolders.Write(folder, "Flavour.props", "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);FLAVOUR_A</DefineConstants>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Greeting.cs", "namespace Lib;\n\npublic static class Greeting\n{\n    public static string Name => \"lib-1\";\n}\n");
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>").With(
            "  <Import Project=\"../Flavour.props\" />\n  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n"
            + $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore\" Version=\"{ShopApp.Older}\" />\n"
            + "    <AssemblyMetadata Include=\"Said\" Value=\"proj-1\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "App/Program.cs", """
            using System.Reflection;

            #if FLAVOUR_A
            const string flavour = "a";
            #else
            const string flavour = "b";
            #endif
            string project = typeof(Words).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Said").Value!;
            string ef = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Console.WriteLine($"said: {Words.Hello} {Lib.Greeting.Name} {flavour} {project} {ef}");

            static class Words
            {
                public const string Hello = "hello-1";
            }

            """);
        TestFolders.Write(folder, "UpToDate.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
        // By its real path, as the IDE's builds name it, so the first build's restore finds nothing to do.
        Restore.Run(PathNames.Canonical(Path.Combine(folder, "App", "App.csproj")));
        return folder;
    }

    /// <summary>A file changed on disk as another program changes it.</summary>
    private static void Edit(string file, string from, string to)
    {
        string text = File.ReadAllText(file);
        Assert.Contains(from, text, StringComparison.Ordinal);
        File.WriteAllText(file, text.Replace(from, to, StringComparison.Ordinal));
    }

    /// <summary>Run, to its end: built first or not as <paramref name="built"/> says, the Build window saying which, and
    /// the program saying <paramref name="said"/>.</summary>
    private static void Ran(MainWindow window, List<string> builds, bool built, string said, string what)
    {
        int before;
        lock (builds)
        {
            before = builds.Count;
        }
        Ui.Launched(window, window.RunAsync(), what, Slow);
        Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), $"{what} to end", Slow);
        int ran;
        lock (builds)
        {
            ran = builds.Count - before;
        }
        window.BottomPanel.Build.Flush();
        string console = window.BottomPanel.Build.Document.Snapshot.ToString();
        bool skipped = console.Contains(BuildRunController.UpToDateSaid("App"), StringComparison.Ordinal);
        Assert.True(built ? ran == 1 && !skipped : ran == 0 && skipped,
            $"{what} {(built ? "was not built first" : "was built though nothing it reads changed")}: {ran} dotnet builds started, and the Build window says:\n{console}");
        Assert.True(Output(window).Contains($"said: {said}", StringComparison.Ordinal), $"{what} did not run what was built, saying:\n{Output(window)}");
    }

    private static string Output(MainWindow window) => window.BottomPanel.Run.Document.Snapshot.ToString();
}
