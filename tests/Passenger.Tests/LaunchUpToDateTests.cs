using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Run of a project whose build would read nothing changed since its last build starts no dotnet build, the Build
/// window saying it is up to date; one whose build reads something changed (a C# file typed into and not saved, its
/// project file, a .props it imports, a project it references, the version of a package it references, the version
/// global.json gives an SDK it imports) is built first, the program then running what changed. EF Core's two versions
/// come from the run's local feed, the SDK's from the folder's own.</summary>
public sealed class LaunchUpToDateTests
{
    /// <summary>The longest a build and a run of the app take on a developer's machine, scaled by the run.</summary>
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(2);

    [Gate]
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
            Ran(window, builds, built: true, $"hello-1 lib-1 a proj-1 {ShopApp.Older} sdk-1", "its first run");
            Ran(window, builds, built: false, $"hello-1 lib-1 a proj-1 {ShopApp.Older} sdk-1", "a run with nothing changed");

            window.OpenFile(Path.Combine(folder, "App", "Program.cs"));
            Ui.Settle();
            string text = window.EditorArea.Active!.Text.Snapshot.ToString();
            window.EditorArea.CodeEditor.ApplyEdits([new TextEdit(text.IndexOf("hello-1", StringComparison.Ordinal), "hello-1".Length, "hello-2")]);
            Ran(window, builds, built: true, $"hello-2 lib-1 a proj-1 {ShopApp.Older} sdk-1", "a run after C# typed and not saved");
            Ran(window, builds, built: false, $"hello-2 lib-1 a proj-1 {ShopApp.Older} sdk-1", "a run after that one");

            Edit(app, "Value=\"proj-1\"", "Value=\"proj-2\"");
            Ran(window, builds, built: true, $"hello-2 lib-1 a proj-2 {ShopApp.Older} sdk-1", "a run after the project file changed");
            Edit(Path.Combine(folder, "Flavour.props"), "FLAVOUR_A", "FLAVOUR_B");
            Ran(window, builds, built: true, $"hello-2 lib-1 b proj-2 {ShopApp.Older} sdk-1", "a run after the .props it imports changed");
            Edit(Path.Combine(folder, "Lib", "Greeting.cs"), "lib-1", "lib-2");
            Ran(window, builds, built: true, $"hello-2 lib-2 b proj-2 {ShopApp.Older} sdk-1", "a run after the library it references changed");
            Edit(app, $"Version=\"{ShopApp.Older}\"", $"Version=\"{ShopApp.Version}\"");
            Ran(window, builds, built: true, $"hello-2 lib-2 b proj-2 {ShopApp.Version} sdk-1", "a run after its package's version changed");
            Edit(Path.Combine(folder, "global.json"), "\"1.0.0\"", "\"2.0.0\"");
            Ran(window, builds, built: true, $"hello-2 lib-2 b proj-2 {ShopApp.Version} sdk-2", "a run after global.json gave an SDK it imports another version");
            Ran(window, builds, built: false, $"hello-2 lib-2 b proj-2 {ShopApp.Version} sdk-2", "a run with nothing changed since");
        }
        finally
        {
            ChildProcesses.Started -= started;
            window.Close();
        }
    }

    /// <summary>Run begun while a build works waits for that build to end, saying so in its work, and then runs; here
    /// the build is held until the test lets it go.</summary>
    [Gate]
    [AvaloniaFact]
    public void A_run_begun_while_a_build_works_waits_for_it_to_end_and_then_runs()
    {
        string folder = TestFolders.Create("uptodate-held-");
        // A build, not a design-time one, held once the test puts "hold" there until it puts "go" (ten minutes at most).
        TestFolders.Write(folder, "Directory.Build.targets", """
            <Project>
              <Target Name="Held" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' != 'true' And Exists('$(MSBuildThisFileDirectory)hold')">
                <Exec Condition="'$(OS)' != 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="rm -f hold; touch held; n=0; while [ ! -f go ] &amp;&amp; [ $n -lt 2400 ]; do sleep 0.25; n=`expr $n + 1`; done" />
                <Exec Condition="'$(OS)' == 'Windows_NT'" WorkingDirectory="$(MSBuildThisFileDirectory)" Command="powershell -NoProfile -Command &quot;Remove-Item hold; New-Item held -ItemType File | Out-Null; $n = 0; while (-not (Test-Path go) -and $n -lt 2400) { Start-Sleep -Milliseconds 250; $n++ }&quot;" />
              </Target>
            </Project>
            """);
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "App/Program.cs", "System.Console.WriteLine(\"said: ran\");\n");
        Restore.Run(PathNames.Canonical(Path.Combine(folder, "App", "App.csproj")));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        const string waiting = "Waiting for the build under way to end…";
        List<string> stages = [];
        EventHandler<WorkChange> worked = (_, change) =>
        {
            if (!change.Ended)
            {
                stages.Add(change.Work.Stage);
            }
        };
        window.Actions.Work.Changed += worked;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "App"), "App's run configuration");
            Assert.True(window.SelectConfiguration("App"));
            File.WriteAllText(Path.Combine(folder, "hold"), string.Empty);
            Assert.True(window.Perform(AppActions.Build));
            Ui.PumpUntil(() => File.Exists(Path.Combine(folder, "held")), "the build to reach its hold", Slow);
            Task<string?> run = window.RunAsync();
            Ui.PumpUntil(() => stages.Contains(waiting) || run.IsCompleted, "the run to wait for the build under way", Slow);
            File.WriteAllText(Path.Combine(folder, "go"), string.Empty);
            Ui.Launched(window, run, "the run begun while the build worked", Slow);
            Ui.WaitFor(() => Output(window).Contains("Process finished", StringComparison.Ordinal), "the run to end", Slow);
            Assert.True(stages.Contains(waiting), $"The run did not say it waited for the build under way; its work said: {string.Join(" | ", stages)}");
            Assert.True(Output(window).Contains("said: ran", StringComparison.Ordinal), $"The run did not run App, saying:\n{Output(window)}");
        }
        finally
        {
            File.WriteAllText(Path.Combine(folder, "go"), string.Empty);
            window.Actions.Work.Changed -= worked;
            window.Close();
        }
    }

    /// <summary>A console app, App, that says what each of its inputs gave it: its C#, the library it references, a
    /// constant its imported .props defines, its project file's assembly metadata, EF Core's version and a constant an SDK
    /// it imports defines, at the version global.json gives it; restored.</summary>
    private static string Write()
    {
        string folder = TestFolders.Create("uptodate-");
        ShopApp.Feed();
        string sdks = Directory.CreateDirectory(Path.Combine(folder, "sdks")).FullName;
        foreach (string version in (string[])["1", "2"])
        {
            File.WriteAllBytes(Path.Combine(sdks, $"Passenger.Flavour.Sdk.{version}.0.0.nupkg"), StandInFeed.Package("Passenger.Flavour.Sdk", $"{version}.0.0", new Dictionary<string, string>
            {
                ["Sdk/Sdk.props"] = "<Project />\n",
                ["Sdk/Sdk.targets"] = $"<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);SDK_{version}</DefineConstants>\n  </PropertyGroup>\n</Project>\n",
            }));
        }
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="feed" value="{System.Security.SecurityElement.Escape(AvaloniaApp.Feed)}" />
                <add key="sdks" value="sdks" />
              </packageSources>
            </configuration>
            """);
        TestFolders.Write(folder, "global.json", "{\n  \"msbuild-sdks\": {\n    \"Passenger.Flavour.Sdk\": \"1.0.0\"\n  }\n}\n");
        TestFolders.Write(folder, "Flavour.props", "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);FLAVOUR_A</DefineConstants>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Lib/Lib.csproj", TestProjects.Library());
        TestFolders.Write(folder, "Lib/Greeting.cs", "namespace Lib;\n\npublic static class Greeting\n{\n    public static string Name => \"lib-1\";\n}\n");
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console("<ImplicitUsings>enable</ImplicitUsings>").With(
            "  <Sdk Name=\"Passenger.Flavour.Sdk\" />\n  <Import Project=\"../Flavour.props\" />\n  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n"
            + $"    <PackageReference Include=\"Microsoft.EntityFrameworkCore\" Version=\"{ShopApp.Older}\" />\n"
            + "    <AssemblyMetadata Include=\"Said\" Value=\"proj-1\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "App/Program.cs", """
            using System.Reflection;

            #if FLAVOUR_A
            const string flavour = "a";
            #else
            const string flavour = "b";
            #endif
            #if SDK_1
            const string sdk = "sdk-1";
            #else
            const string sdk = "sdk-2";
            #endif
            string project = typeof(Words).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "Said").Value!;
            string ef = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
            Console.WriteLine($"said: {Words.Hello} {Lib.Greeting.Name} {flavour} {project} {ef} {sdk}");

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
