using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.Core;
using Passenger.Host;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The Solution view shows each project as MSBuild evaluates it, in the project host: its conditions, imports
/// (Directory.Build.props, central package management) and wildcards as a build sees them; a project file edited on disk
/// shows anew; and a folder opened again shows its projects from the cache of their last evaluation, evaluating none whose
/// inputs did not change.</summary>
public sealed class ProjectEvaluationTests : IDisposable
{
    /// <summary>A package referenced under a condition that is false, one under a condition the repository's props make true,
    /// one whose version is central; .cs files of a folder removed, and those of a folder outside the project taken in by a
    /// wildcard but one folder of them excluded, each at a link of the project's.</summary>
    private const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Humanizer.Core" />
            <PackageReference Include="Newtonsoft.Json" Condition="'$(TargetFramework)' == 'net8.0'" />
            <PackageReference Include="Serilog" Condition="'$(ShopLogs)' == 'true'" />
            <Compile Remove="Legacy/**" />
            <Compile Include="../Shared/**/*.cs" Exclude="../Shared/Obsolete/**" Link="Shared/%(RecursiveDir)%(Filename)%(Extension)" />
          </ItemGroup>
        </Project>

        """;

    private readonly string folder = PathNames.Canonical(TestFolders.Create("evaluation-"));

    public ProjectEvaluationTests()
    {
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        Write("Directory.Build.props", $"<Project>\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n    <ShopLogs>true</ShopLogs>\n  </PropertyGroup>\n</Project>\n");
        Write("Directory.Packages.props", """
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Humanizer.Core" Version="2.14.1" />
                <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
                <PackageVersion Include="Serilog" Version="4.0.0" />
              </ItemGroup>
            </Project>

            """);
        Write("App/App.csproj", Project);
        Write("App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        Write("App/Legacy/Old.cs", "class Old\n{\n}\n");
        Write("Shared/Helper.cs", "class Helper\n{\n}\n");
        Write("Shared/Text/Words.cs", "class Words\n{\n}\n");
        Write("Shared/Obsolete/Gone.cs", "class Gone\n{\n}\n");
    }

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_project_shows_its_files_and_packages_as_MSBuild_evaluates_its_conditions_imports_wildcards_and_central_versions()
    {
        MainWindow window = Open();
        Assert.Equal(TestProjects.Framework, Row(window, NodeKind.Project, "App").Detail);
        Assert.Equal(["Dependencies", "Shared", "Program.cs"], Children(window, "App"));
        Assert.Equal(["Text", "Helper.cs"], Children(window, "Shared"));
        Assert.Equal(["Words.cs"], Children(window, "Text"));
        Assert.Equal([(TestProjects.Framework, null), ("Humanizer.Core", "2.14.1"), ("Serilog", "4.0.0")],
            Children(window, "Dependencies").Select(name => (name, Row(window, null, name).Detail)));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Shared", "Text", "Words.cs")), Row(window, NodeKind.File, "Words.cs").Path);
        window.Close();
    }

    [AvaloniaFact]
    public void A_project_file_edited_on_disk_shows_what_it_says_now()
    {
        MainWindow window = Open();
        Assert.Contains("Serilog", Children(window, "Dependencies"));
        Write("App/App.csproj", Project.Replace("""<Compile Remove="Legacy/**" />""", """<Compile Remove="Program.cs" />""")
            .Replace("""'$(ShopLogs)' == 'true'""", """'$(ShopLogs)' == 'false'"""));
        Ui.WaitFor(() => !window.ExplorerView.Tree.Rows.Any(r => r.Name == "Serilog"), "the Dependencies to drop the package the project's condition now leaves out");
        Assert.Equal(["Dependencies", "Legacy", "Shared"], Children(window, "App"));
        Assert.Equal(["Old.cs"], Children(window, "Legacy"));
        window.Close();
    }

    /// <summary>A host whose answer does not read, as one of another build's could write, is ended and the evaluation asked
    /// of it fails; the next change is evaluated by a host started anew.</summary>
    [AvaloniaFact]
    public void A_project_host_whose_answer_does_not_read_is_ended_and_the_next_change_is_evaluated_by_a_new_one()
    {
        MainWindow window = Open();
        Assert.Contains("Serilog", Children(window, "Dependencies"));
        ProjectHost spoiled = Task.Run(() => ProjectHosts.ForAsync(folder)).GetAwaiter().GetResult();
        spoiled.SpoilNext(HostMethods.Evaluate);
        Write("App/App.csproj", Project.Replace("""<Compile Remove="Legacy/**" />""", """<Compile Remove="Legacy/Old.cs" />"""));
        Ui.WaitFor(() => spoiled.HasExited, "the host whose answer did not read to be ended");
        Ui.Settle();
        Ui.WaitFor(() => Ui.IdeLog().Contains("could not evaluate the projects", StringComparison.Ordinal)
            && Ui.IdeLog().Contains("the project host's answer could not be read", StringComparison.Ordinal), "the evaluation asked of it to fail, saying why");

        Write("App/App.csproj", Project.Replace("""'$(ShopLogs)' == 'true'""", """'$(ShopLogs)' == 'false'"""));
        Ui.WaitFor(() => !window.ExplorerView.Tree.Rows.Any(r => r.Name == "Serilog"), "the next change's evaluation to drop the package its condition now leaves out");
        Assert.Null(window.Session.Models.Problem);
        int now = Task.Run(() => ProjectHosts.ForAsync(folder)).GetAwaiter().GetResult().ProcessId;
        Assert.True(now != spoiled.ProcessId, "The next evaluation was asked of the host whose answer did not read");
        window.Close();
    }

    [AvaloniaFact]
    public void A_folder_opened_again_shows_its_projects_from_their_last_evaluation_before_any_is_evaluated_again()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = new() { Width = 1200, Height = 800, StateFolder = settings };
        first.Show();
        first.KeepLayout();
        first.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        Assert.True(first.Session.Models.Evaluations > 0, "The folder's first opening evaluated nothing");
        WaitForRestore(first);
        first.Close();

        MainWindow second = new() { Width = 1200, Height = 800, StateFolder = settings };
        second.Show();
        second.KeepLayout();
        second.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        // The cache is read off the window's thread, its tree shown as it lands.
        Ui.Settle();
        Assert.True(second.Session.Models.Evaluations == 0,
            $"Opened again with nothing changed, the folder had {second.Session.Models.Evaluations} project(s) evaluated again rather than shown from the cache");
        Assert.Equal(["Dependencies", "Shared", "Program.cs"], Children(second, "App"));
        Assert.Contains("Serilog", Children(second, "Dependencies"));

        // A file a project's wildcard takes in, made while it was closed, has that project evaluated again as it opens.
        second.Close();
        Write("Shared/Text/Letters.cs", "class Letters\n{\n}\n");
        MainWindow third = new() { Width = 1200, Height = 800, StateFolder = settings };
        third.Show();
        third.KeepLayout();
        third.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        Assert.Equal(1, third.Session.Models.Evaluations);
        Assert.Equal(["Text", "Helper.cs"], Children(third, "Shared"));
        Assert.Equal(["Letters.cs", "Words.cs"], Children(third, "Text"));
        third.Close();
    }

    [AvaloniaFact]
    public void A_directory_build_file_made_above_an_open_project_shows_what_it_adds()
    {
        MainWindow window = Open();
        Assert.DoesNotContain("Newtonsoft.Json", Children(window, "Dependencies"));
        // Above the project's folder, where only MSBuild's look upward for the file reaches.
        Write("Directory.Build.targets", "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" />\n  </ItemGroup>\n</Project>\n");
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Name == "Newtonsoft.Json"), "the Dependencies to show the package the new Directory.Build.targets adds");
        Assert.Equal("13.0.3", Row(window, null, "Newtonsoft.Json").Detail);
        window.Close();
    }

    [AvaloniaFact]
    public void A_folder_opened_again_evaluates_a_project_whose_directory_build_file_came_while_it_was_closed()
    {
        string settings = TestFolders.Create("settings-");
        MainWindow first = Keeping(settings, languageServer: false);
        Assert.DoesNotContain("Newtonsoft.Json", Children(first, "Dependencies"));
        first.Close();

        Write("Directory.Build.targets", "<Project>\n  <ItemGroup>\n    <PackageReference Include=\"Newtonsoft.Json\" />\n  </ItemGroup>\n</Project>\n");
        MainWindow second = Keeping(settings, languageServer: false);
        Assert.True(second.Session.Models.Evaluations > 0,
            "A Directory.Build.targets made above the project while the folder was closed had nothing evaluated as it opened again, the project shown from the cache as it was");
        Assert.Contains("Newtonsoft.Json", Children(second, "Dependencies"));
        second.Close();
    }

    [AvaloniaFact]
    public void A_project_is_evaluated_again_once_its_first_restore_writes_what_it_imports_and_kept_so()
    {
        // Never restored: the language server restores it as it loads it, after its first evaluation.
        string settings = TestFolders.Create("settings-");
        MainWindow first = Keeping(settings);
        WaitForRestore(first);
        first.Close();

        MainWindow second = Keeping(settings);
        Assert.True(second.Session.Models.Evaluations == 0,
            $"Opened again with nothing changed since its restore, the folder had {second.Session.Models.Evaluations} project(s) evaluated again rather than shown from the cache");
        second.Close();
    }

    [AvaloniaFact]
    public void A_project_holding_folder_links_back_up_its_own_tree_keeps_its_real_folders_alone_as_what_its_evaluation_and_build_read()
    {
        // Links to the folder each is in and to those above it: a walk that followed them would go round the tree.
        string app = Path.Combine(folder, "App");
        Directory.CreateDirectory(Path.Combine(app, "Legacy", "Deep"));
        TestFolders.LinkFolder(Path.Combine(app, "self"), app);
        TestFolders.LinkFolder(Path.Combine(app, "up"), folder);
        TestFolders.LinkFolder(Path.Combine(app, "Legacy", "here"), Path.Combine(app, "Legacy"));
        TestFolders.LinkFolder(Path.Combine(app, "Legacy", "Deep", "up"), Path.Combine(app, "Legacy"));
        string[] real = [.. new[] { "App", "App/Legacy", "App/Legacy/Deep", "Shared", "Shared/Text" }
            .Select(f => Path.Combine(folder, f.Replace('/', Path.DirectorySeparatorChar))).Order(StringComparer.Ordinal)];

        string settings = TestFolders.Create("settings-");
        MainWindow window = Keeping(settings);
        window.OpenFile(Path.Combine(app, "Program.cs"));
        string project = Path.Combine(app, "App.csproj");
        Ui.WaitFor(() => window.Session.State?.ProjectModels.Value.Projects.ContainsKey(project) == true, "App's evaluation kept for the folder's next opening");
        // MSBuild's wildcards pass the links over too: the project holds what it held without them.
        Assert.Equal(["Dependencies", "Shared", "Program.cs"], Children(window, "App"));
        Ui.WaitFor(() => window.Session.State?.ProjectBuilds.Value.Projects.ContainsKey(project) == true, "App's design-time build kept for the folder's next opening", TimeSpan.FromMinutes(2));

        IReadOnlyList<Passenger.Host.Stamp> evaluated = window.Session.State!.ProjectModels.Value.Projects[project].Folders;
        IReadOnlyList<Passenger.Host.Stamp> built = [.. window.Session.State.ProjectBuilds.Value.Projects[project].Inputs.Where(s => s.Folder)];
        Assert.True(evaluated.Count == real.Length, $"App's evaluation kept {evaluated.Count} folders' stamps for the {real.Length} its tree has{Walk(evaluated)}");
        Assert.True(built.Count == real.Length, $"App's design-time build kept {built.Count} folders' stamps for the {real.Length} its tree has{Walk(built)}");
        Assert.Equal(real, evaluated.Select(s => PathNames.Canonical(s.Path)).Order(StringComparer.Ordinal));
        Assert.Equal(real, built.Select(s => PathNames.Canonical(s.Path)).Order(StringComparer.Ordinal));
        window.Close();

        static string Walk(IReadOnlyList<Passenger.Host.Stamp> stamps) => stamps.Count > 20
            ? ", its walk going round the links back up"
            : ": " + string.Join(", ", stamps.Select(s => s.Path));
    }

    [AvaloniaFact]
    public void The_project_of_the_file_shown_and_those_it_references_load_in_the_language_server_before_the_rest()
    {
        Write("Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Core/Core.csproj\" />\n  <Project Path=\"Reports/Reports.csproj\" />\n"
            + "  <Project Path=\"Admin/Admin.csproj\" />\n</Solution>\n");
        Write("App/App.csproj", Project.Replace("</ItemGroup>", "  <ProjectReference Include=\"../Core/Core.csproj\" />\n  </ItemGroup>"));
        foreach (string name in new[] { "Core", "Reports", "Admin" })
        {
            Write($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
            Write($"{name}/{name}Code.cs", $"class {name}Code\n{{\n}}\n");
        }
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        window.OpenFile(Path.Combine(folder, "App", "Program.cs"));
        // The order the server took them in, as its log says it finished (or failed) each: unrestored, they load with errors.
        string[] projects = ["App", "Core", "Reports", "Admin"];
        string log = string.Empty;
        Ui.PumpUntil(() => window.CSharpService?.Session is { } session && (log = ServerLog(session.Server.ProcessId)) is var read
            && projects.All(p => read.Contains(Path.Combine(p, p + ".csproj"), StringComparison.Ordinal)), "the language server's log to tell of the solution's four projects");
        int At(string project) => log.IndexOf(Path.Combine(project, project + ".csproj"), StringComparison.Ordinal) is >= 0 and var at ? at
            : throw new InvalidOperationException($"The language server's log says nothing of {project}:\n{log}");
        Assert.True(Math.Max(At("App"), At("Core")) < Math.Min(At("Reports"), At("Admin")),
            $"The language server took up another project before the file shown's, and the one it references:\n{log}");
        window.Close();

        static string ServerLog(int process) => Directory.Exists(Passenger.App.CSharp.CSharpService.LogFolder)
            && Directory.EnumerateFiles(Passenger.App.CSharp.CSharpService.LogFolder, $"server-*-{process}.log").SingleOrDefault() is { } path ? Ui.Shared(path) : string.Empty;
    }

    /// <summary>A window on the solution with no C# language server: the Solution view's projects are the project host's
    /// evaluation, and the server would restore the solution's packages, from nuget.org where the package cache lacks them.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(Path.Combine(folder, "Shop.slnx"), languageServer: false);
        return window;
    }

    /// <summary>Waits for the restore the language server makes of App as it loads it, never restored, to reach App's
    /// evaluation: what the restore writes for the project to import is among what the evaluation read.</summary>
    private void WaitForRestore(MainWindow window)
    {
        string props = Path.Combine(folder, "App", "obj", "App.csproj.nuget.g.props");
        Ui.WaitFor(() => window.Session.Workspace?.Projects.FirstOrDefault(p => p.Name == "App")?.Evaluated?.Inputs.Any(i => PathNames.Same(i.Path, props) && i.Length >= 0) == true,
            "App evaluated again with the props its restore wrote for it to import", TimeSpan.FromMinutes(2));
    }

    /// <summary>A window keeping the folder's state in <paramref name="settings"/>, its evaluations' cache among it; with no C#
    /// language server, and none of its restore, for a test that reads only the evaluations (<paramref name="languageServer"/>).</summary>
    private MainWindow Keeping(string settings, bool languageServer = true)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = languageServer };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.Settle();
        return window;
    }

    /// <summary>A row of the Explorer, opened, and the names of the rows under it.</summary>
    private static List<string> Children(MainWindow window, string name)
    {
        ExplorerTree tree = window.ExplorerView.Tree;
        ExplorerNode row = Row(window, null, name);
        tree.Expand(row);
        return [.. (row.Children ?? []).Select(c => c.Name)];
    }

    /// <summary>A row by its kind and name, the solution's top opened down to the project's first.</summary>
    private static ExplorerNode Row(MainWindow window, NodeKind? kind, string name)
    {
        ExplorerTree tree = window.ExplorerView.Tree;
        if (tree.Rows.FirstOrDefault(r => r.Kind == NodeKind.Project) is { IsExpanded: false } project)
        {
            tree.Expand(project);
        }
        return tree.Rows.FirstOrDefault(r => (kind is null || r.Kind == kind) && r.Name == name)
            ?? throw new InvalidOperationException($"The Explorer shows no {kind} {name}: [{string.Join(", ", tree.Rows.Select(r => r.Name))}]");
    }

    private void Write(string relative, string text) => TestFolders.Write(folder, relative, text);
}
