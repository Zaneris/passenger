using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Editor;

namespace Passenger.Tests;

/// <summary>Add Aspire Orchestration on a solution of the tests' own target, restored from the run's local feed and the package
/// cache the test project's restore filled (<see cref="AspireApp.UseFeed"/>), so nothing reaches nuget.org.</summary>
public sealed class AspireOrchestrationTests : IClassFixture<AspireOrchestrationTests.Project>
{
    private readonly Project project;

    public AspireOrchestrationTests(Project project)
    {
        this.project = project;
        project.Reset();
    }

    [AvaloniaFact]
    public void Adding_orchestration_creates_the_missing_projects_and_one_global_undo_and_redo_restores_every_file()
    {
        MainWindow window = Open();
        Dictionary<string, byte[]> before = Snapshot();
        string beforeProgram = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        Assert.Equal("Added Aspire orchestration", Add(window, "Api", existing: false));
        string hostProject = project.PathOf("Shop.AppHost/Shop.AppHost.csproj");
        string defaultsProject = project.PathOf("Shop.ServiceDefaults/Shop.ServiceDefaults.csproj");
        Assert.True(File.Exists(hostProject), "Add Aspire Orchestration did not create an AppHost project");
        Assert.True(File.Exists(defaultsProject), "Add Aspire Orchestration did not create ServiceDefaults");
        Assert.Contains("../Api/Api.csproj", File.ReadAllText(hostProject).Replace('\\', '/'), StringComparison.Ordinal);
        Assert.Contains("Shop.ServiceDefaults", File.ReadAllText(project.PathOf("Api/Api.csproj")), StringComparison.Ordinal);
        string hostSource = Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(hostProject)!, "*.cs")
            .Select(File.ReadAllText), text => text.Contains("AddProject", StringComparison.Ordinal));
        Assert.Contains("AddProject(\"api\", \"../Api/Api.csproj\")", hostSource, StringComparison.Ordinal);
        Assert.Contains("AddServiceDefaults()", File.ReadAllText(project.PathOf("Api/Program.cs")), StringComparison.Ordinal);
        Assert.Contains("MapDefaultEndpoints()", File.ReadAllText(project.PathOf("Api/Program.cs")), StringComparison.Ordinal);
        Assert.Contains("Shop.AppHost", File.ReadAllText(project.PathOf("Shop.slnx")), StringComparison.Ordinal);
        Assert.Contains("Shop.ServiceDefaults", File.ReadAllText(project.PathOf("Shop.slnx")), StringComparison.Ordinal);
        Dictionary<string, byte[]> after = Snapshot();
        string afterProgram = window.EditorArea.CodeEditor.Document.Snapshot.ToString();
        List<string> questions = [];
        window.Confirm = (title, _, _) =>
        {
            questions.Add(title);
            return Task.FromResult(true);
        };
        Ui.Run(window, EditorActions.Undo);
        Assert.Equal(beforeProgram, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        Ui.Run(window, AppActions.SaveAll);
        AssertSnapshot(before);
        Assert.Contains("Undo", questions);
        Assert.DoesNotContain("AddServiceDefaults()", window.EditorArea.CodeEditor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        Ui.Run(window, EditorActions.Redo);
        Assert.Equal(afterProgram, window.EditorArea.CodeEditor.Document.Snapshot.ToString());
        Ui.Run(window, AppActions.SaveAll);
        AssertSnapshot(after);
        Assert.Contains("AddServiceDefaults()", window.EditorArea.CodeEditor.Document.Snapshot.ToString(), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>The backing services a service's packages point to join the AppHost with it: a Postgres server and the
    /// database its connection string names, a Redis by its connection string's name, each referenced and waited for by
    /// the service under the name it reads, Aspire's hosting integrations referenced at the AppHost's own version; one
    /// undo takes it all back.</summary>
    [AvaloniaFact]
    public void Orchestration_adds_the_backing_services_a_service_reads_and_references_them_by_its_connection_strings_names()
    {
        project.Write("Plain/Plain.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"Npgsql\" Version=\"9.0.3\" /><PackageReference Include=\"StackExchange.Redis\" Version=\"2.8.16\" /></ItemGroup></Project>");
        project.Write("Plain/appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"shopdb\": \"Host=localhost;Database=shop;Username=app;Password=app\",\n    \"cache\": \"localhost:6379\"\n  }\n}\n");
        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.WaitFor(() => window.WorkspaceModel!.Projects.Any(p => p.Name == "Plain" && p.Packages.Any(q => q.Name == "Npgsql")), "Plain's packages to be evaluated");
        Dictionary<string, byte[]> before = Snapshot();
        Assert.Equal("Added Aspire orchestration", Add(window, "Plain", existing: true));
        string host = File.ReadAllText(project.PathOf("Host/Program.cs"));
        foreach (string line in new[]
        {
            "var postgres = builder.AddPostgres(\"postgres\");",
            "var shopdb = postgres.AddDatabase(\"shopdb\", \"shop\");",
            "var cache = builder.AddRedis(\"cache\");",
            "var plain = builder.AddProject(\"plain\", \"../Plain/Plain.csproj\").WithReference(shopdb).WaitFor(shopdb).WithReference(cache).WaitFor(cache);",
        })
        {
            Assert.True(host.Contains(line, StringComparison.Ordinal), $"The AppHost lacks {line}:\n{host}");
        }
        string hostProject = File.ReadAllText(project.PathOf("Host/Host.csproj"));
        foreach (string package in new[] { "Aspire.Hosting.PostgreSQL", "Aspire.Hosting.Redis" })
        {
            Assert.True(hostProject.Contains($"<PackageReference Include=\"{package}\" Version=\"{AspireApp.TemplatesVersion}\" />", StringComparison.Ordinal),
                $"The AppHost does not reference {package} at its own Aspire version:\n{hostProject}");
        }
        Ui.Run(window, AppActions.FocusExplorer);
        Ui.Run(window, EditorActions.Undo);
        Ui.Run(window, AppActions.SaveAll);
        AssertSnapshot(before);
        window.Close();
    }

    [AvaloniaFact]
    public void A_hosting_package_is_added_beside_one_the_AppHost_has_commented_out_and_the_comment_is_left_as_it_was()
    {
        string commented = $"    <!-- <PackageReference Include=\"Aspire.Hosting.PostgreSQL\" Version=\"{AspireApp.TemplatesVersion}\" /> -->\n";
        project.Write("Host/Host.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <Sdk Name=\"Aspire.AppHost.Sdk\" Version=\"{AspireApp.TemplatesVersion}\" />\n"
            + $"  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings><IsAspireHost>true</IsAspireHost></PropertyGroup>\n"
            + $"  <ItemGroup>\n    <PackageReference Include=\"Aspire.Hosting.AppHost\" Version=\"{AspireApp.TemplatesVersion}\" />\n{commented}"
            + "    <ProjectReference Include=\"../Api/Api.csproj\" />\n  </ItemGroup>\n</Project>\n");
        project.Write("Plain/Plain.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"Npgsql\" Version=\"9.0.3\" /></ItemGroup></Project>");
        project.Write("Plain/appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"shopdb\": \"Host=localhost;Database=shop;Username=app;Password=app\"\n  }\n}\n");
        MainWindow window = Open();
        window.Confirm = (_, _, _) => Task.FromResult(true);
        Ui.WaitFor(() => window.WorkspaceModel!.Projects.Any(p => p.Name == "Plain" && p.Packages.Any(q => q.Name == "Npgsql")), "Plain's packages to be evaluated");
        Assert.Equal("Added Aspire orchestration", Add(window, "Plain", existing: true));
        string hostProject = File.ReadAllText(project.PathOf("Host/Host.csproj"));
        string expected = $"    <PackageReference Include=\"Aspire.Hosting.AppHost\" Version=\"{AspireApp.TemplatesVersion}\" />\n"
            + $"    <PackageReference Include=\"Aspire.Hosting.PostgreSQL\" Version=\"{AspireApp.TemplatesVersion}\" />\n{commented}";
        Assert.True(hostProject.Contains(expected, StringComparison.Ordinal),
            $"The AppHost was not given Aspire.Hosting.PostgreSQL on a line of its own after its last reference, beside the one commented out:\n{hostProject}");
        window.Close();
    }

    [AvaloniaFact]
    public void Reordered_named_AddProject_arguments_do_not_add_the_same_resource_twice()
    {
        MainWindow window = Open();
        Assert.Equal("Added Aspire orchestration", Add(window, "Api", existing: true));
        string host = File.ReadAllText(project.PathOf("Host/Program.cs"));
        Assert.Contains("AddProject(projectPath: \"../Api/Api.csproj\", name: \"api\")", host, StringComparison.Ordinal);
        Assert.Equal(1, host.Split("AddProject", StringSplitOptions.None).Length - 1);
        Assert.Contains("Defaults", File.ReadAllText(project.PathOf("Api/Api.csproj")), StringComparison.Ordinal);
        Assert.Contains("MapDefaultEndpoints()", File.ReadAllText(project.PathOf("Api/Program.cs")), StringComparison.Ordinal);
        Dictionary<string, byte[]> configured = Snapshot();
        Assert.Equal("Aspire orchestration is already configured", Add(window, "Api", existing: true));
        AssertSnapshot(configured);
        window.Close();
    }

    /// <summary>Each project's code is read as it compiles: a ServiceDefaults whose extensions are declared under
    /// #if NET8_0_OR_GREATER is taken for one, and an AppHost's second builder, in a file under #if !NET8_0_OR_GREATER that
    /// its project never compiles, leaves its one builder's file the one the resource is added to.</summary>
    [AvaloniaFact]
    public void Orchestration_reads_each_projects_code_as_it_compiles_taking_extensions_under_its_frameworks_symbols_and_passing_over_a_builder_it_never_compiles()
    {
        string extensions = File.ReadAllText(project.PathOf("Defaults/Extensions.cs"));
        project.Rewrite("Defaults/Extensions.cs", "#if NET8_0_OR_GREATER\n" + extensions + "\n#endif\n");
        project.Write("Host/Legacy.cs", "#if !NET8_0_OR_GREATER\nusing Aspire.Hosting;\nvar legacy = DistributedApplication.CreateBuilder(args);\nlegacy.Build().Run();\n#endif\n");
        MainWindow window = Open();
        Assert.Equal("Added Aspire orchestration", Add(window, "Api", existing: true));
        Assert.Contains("AddServiceDefaults()", File.ReadAllText(project.PathOf("Api/Program.cs")), StringComparison.Ordinal);
        Assert.Contains("Defaults", File.ReadAllText(project.PathOf("Api/Api.csproj")), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflicting_folder_or_resource_is_refused_before_any_solution_file_is_written(bool resource)
    {
        if (resource)
        {
            project.Write("Host/Program.cs", "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\nvar occupied = builder.AddConnectionString(\"api\");\nbuilder.Build().Run();\n");
        }
        else
        {
            Directory.CreateDirectory(project.PathOf("Shop.AppHost"));
            project.Write("Shop.AppHost/keep.txt", "This folder belongs to the user.\n");
        }
        MainWindow window = Open();
        Dictionary<string, byte[]> before = Snapshot();
        string result = Add(window, "Api", existing: resource);
        Assert.StartsWith("Could not add Aspire orchestration:", result, StringComparison.Ordinal);
        Assert.Contains(resource ? "resource name" : "already exists", result, StringComparison.Ordinal);
        AssertSnapshot(before);
        window.Close();
    }

    [AvaloniaFact]
    public void Windows_ServiceDefaults_cannot_be_referenced_by_a_plain_service_and_refusal_writes_nothing()
    {
        MainWindow window = Open();
        Ui.WaitFor(() => window.WorkspaceModel!.Projects.Any(p => p.Name == "Plain" && p.TargetFrameworks.Contains(TestProjects.Framework)),
            "the plain service target framework to be evaluated");
        Dictionary<string, byte[]> before = Snapshot();
        AspireOrchestrationDialog dialog = Show(window, "Plain");
        Select(dialog, dialog.AppHost, "Host");
        Select(dialog, dialog.ServiceDefaults, "WindowsDefaults");
        Ui.Click(dialog.Add);
        string result = Finished(window);
        Assert.StartsWith("Could not add Aspire orchestration:", result, StringComparison.Ordinal);
        Assert.Contains("ServiceDefaults target framework", result, StringComparison.Ordinal);
        Assert.Contains("not compatible", result, StringComparison.Ordinal);
        AssertSnapshot(before);
        window.Close();
    }

    /// <summary>New Project into a solution with an AppHost, with Aspire: the new web project joins that AppHost as a
    /// resource, with the ServiceDefaults of its framework the solution has, nothing else made, and the AppHost's
    /// configuration is chosen.</summary>
    [AvaloniaFact]
    public void New_Project_with_Aspire_joins_the_solutions_AppHost_with_its_ServiceDefaults_and_the_AppHost_is_chosen()
    {
        MainWindow window = Open();
        try
        {
            Ui.Run(window, AppActions.NewProject);
            NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            NewServiceSetupTests.Choose(dialog, "web");
            dialog.ProjectName = "Orders";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Aspire;
            Ui.Settle();
            Assert.StartsWith("It joins Host as a resource", dialog.SetupNote, StringComparison.Ordinal);
            NewServiceSetupTests.Created(dialog, "the web project");

            Assert.Null(NewServiceSetupTests.SetUp(window));
            Assert.Contains("../Orders/Orders.csproj", File.ReadAllText(project.PathOf("Host/Host.csproj")).Replace('\\', '/'), StringComparison.Ordinal);
            Assert.Contains("\"../Orders/Orders.csproj\"", File.ReadAllText(project.PathOf("Host/Program.cs")), StringComparison.Ordinal);
            Assert.Contains("../Defaults/Defaults.csproj", File.ReadAllText(project.PathOf("Orders/Orders.csproj")).Replace('\\', '/'), StringComparison.Ordinal);
            Assert.Contains("AddServiceDefaults()", File.ReadAllText(project.PathOf("Orders/Program.cs")), StringComparison.Ordinal);
            Assert.False(Directory.Exists(project.PathOf("Shop.ServiceDefaults")) || Directory.Exists(project.PathOf("Shop.AppHost")),
                "Aspire made an AppHost or ServiceDefaults beside those the solution has");
            Assert.True(window.Configuration is { Aspire: true } chosen && PathNames.Same(chosen.Project, PathNames.Canonical(project.PathOf("Host/Host.csproj"))),
                $"The configuration chosen is {window.Configuration?.Name}, not the AppHost's: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
        }
        finally
        {
            NewProjectDialog.Remembered = ProjectSetup.None;
            window.Close();
        }
    }

    /// <summary>New Solution of a web project with Aspire: an AppHost and a ServiceDefaults are made with it as the AppHost's
    /// resource, and the AppHost's configuration is chosen.</summary>
    [AvaloniaFact]
    public void New_Solution_with_Aspire_has_an_AppHost_and_ServiceDefaults_and_the_AppHost_is_chosen()
    {
        string place = TestFolders.Create("aspire-new-");
        string settings = TestFolders.Create("settings-");
        TestFolders.Write(place, "Directory.Build.props", "<Project><PropertyGroup><AspireUseCliBundle>false</AspireUseCliBundle></PropertyGroup></Project>");
        AspireApp.UseFeed(place);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        try
        {
            Ui.Click(window.Welcome.NewSolutionButton);
            NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            dialog.Folder = place;
            NewServiceSetupTests.Choose(dialog, "web");
            dialog.SolutionName = "Mall";
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Aspire;
            Ui.Settle();
            Assert.StartsWith("An AppHost and ServiceDefaults made", dialog.SetupNote, StringComparison.Ordinal);
            NewServiceSetupTests.Created(dialog, "the web project");

            Assert.Null(NewServiceSetupTests.SetUp(window));
            string folder = PathNames.Canonical(Path.Combine(place, "Mall"));
            string host = Path.Combine(folder, "Mall.AppHost", "Mall.AppHost.csproj");
            Assert.Contains("../Api/Api.csproj", File.ReadAllText(host).Replace('\\', '/'), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(folder, "Mall.ServiceDefaults", "Mall.ServiceDefaults.csproj")), "No ServiceDefaults was made");
            Assert.Contains("AddServiceDefaults()", File.ReadAllText(Path.Combine(folder, "Api", "Program.cs")), StringComparison.Ordinal);
            Assert.True(window.Configuration is { Aspire: true } chosen && PathNames.Same(chosen.Project, host),
                $"The configuration chosen is {window.Configuration?.Name}, not the AppHost's: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
            Assert.False(window.IsBuilding, "New Solution's set-up was done before the solution's build ended");
            Assert.True(window.Status?.StartsWith($"Api runs with Aspire: Run and Debug launch {window.Configuration!.Name}; Build ", StringComparison.Ordinal) == true
                && window.StatusSeverity is null, $"Once the new solution's build ended the status bar said '{window.Status}' ({window.StatusSeverity}), not how its set-up went");
        }
        finally
        {
            NewProjectDialog.Remembered = ProjectSetup.None;
            window.Close();
            TestFolders.Delete(settings);
            TestFolders.Delete(place);
        }
    }

    /// <summary>New Solution of a web project with Aspire whose project makes no program by the time its set-up reads it (a
    /// library, written as Create is held): once the solution's build ends, the status bar says the set-up failed and why,
    /// with the warning mark, before the build's words.</summary>
    [AvaloniaFact]
    public void New_Solution_whose_set_up_fails_says_why_once_its_build_ends()
    {
        string place = TestFolders.Create("aspire-unset-");
        string settings = TestFolders.Create("settings-");
        TestFolders.Write(place, "Directory.Build.props", "<Project><PropertyGroup><AspireUseCliBundle>false</AspireUseCliBundle></PropertyGroup></Project>");
        AspireApp.UseFeed(place);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Ui.Click(window.Welcome.NewSolutionButton);
            NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            dialog.Folder = place;
            NewServiceSetupTests.Choose(dialog, "web");
            dialog.SolutionName = "Mall";
            dialog.ProjectName = "Api";
            dialog.Framework = TestProjects.Framework;
            dialog.SetupChooser.SelectedIndex = (int)ProjectSetup.Aspire;
            Ui.Settle();
            Task held = dialog.HoldNextMade(release.Task);
            Ui.Click(dialog.CreateButton, settle: false);
            Ui.PumpUntil(() => held.IsCompleted || dialog.Problem is not null, "dotnet new to make Api", TimeSpan.FromMinutes(5));
            Assert.True(dialog.Problem is null, $"Api was not made: {dialog.Problem}");
            string api = Path.Combine(place, "Mall", "Api", "Api.csproj");
            File.WriteAllText(api, File.ReadAllText(api).Replace("<PropertyGroup>", "<PropertyGroup>\n    <OutputType>Library</OutputType>", StringComparison.Ordinal));
            release.SetResult();
            Ui.WaitFor(() => !dialog.IsVisible || dialog.Problem is not null, "Create to end", TimeSpan.FromMinutes(5));
            Assert.True(dialog.Problem is null, $"Api was not made: {dialog.Problem}");

            const string Why = "Api was not set up to run with Aspire: it makes no program to run";
            Assert.Equal(Why, NewServiceSetupTests.SetUp(window));
            Assert.False(window.IsBuilding, "New Solution's set-up was done before the solution's build ended");
            Assert.True(window.Status?.StartsWith($"{Why}; Build ", StringComparison.Ordinal) == true && window.StatusSeverity == Passenger.App.Run.ProblemSeverity.Warning,
                $"Once the new solution's build ended the status bar said '{window.Status}' ({window.StatusSeverity}), not that its set-up failed and why");
            Assert.False(Directory.Exists(Path.Combine(place, "Mall", "Mall.AppHost")), "The set-up that failed made an AppHost");
        }
        finally
        {
            release.TrySetResult();
            NewProjectDialog.Remembered = ProjectSetup.None;
            window.Close();
            TestFolders.Delete(settings);
            TestFolders.Delete(place);
        }
    }

    /// <summary>Add Aspire Orchestration where packages are managed centrally, turned on by Directory.Build.props beside a
    /// Directory.Packages.props that never says so, or by a Directory.Packages.props with no versions yet: the ServiceDefaults
    /// made names no version, Directory.Packages.props gains each of its packages', and the solution restores; one Undo
    /// takes Directory.Packages.props back.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Orchestration_where_packages_are_managed_centrally_puts_the_versions_in_Directory_Packages_props_and_the_solution_restores(bool inBuildProps)
    {
        string folder = CentralFolder(inBuildProps, inBuildProps ? "    <PackageVersion Include=\"Humanizer\" Version=\"2.14.1\" />\n" : string.Empty, string.Empty);
        string props = Path.Combine(folder, "Directory.Packages.props");
        byte[] before = File.ReadAllBytes(props);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.WorkspaceModel!.Projects.Where(p => p.Exists).All(p => p.TargetFrameworks.Count > 0), "Api to be evaluated");
            Assert.Equal("Added Aspire orchestration", Add(window, "Api", existing: false));
            string defaults = File.ReadAllText(Path.Combine(folder, "Shop.ServiceDefaults", "Shop.ServiceDefaults.csproj"));
            string pinned = File.ReadAllText(props);
            List<string> packages = [.. System.Text.RegularExpressions.Regex.Matches(defaults, "<PackageReference Include=\"([^\"]+)\"").Select(m => m.Groups[1].Value)];
            Assert.True(packages.Count > 0 && !defaults.Contains(" Version=", StringComparison.Ordinal),
                $"The ServiceDefaults made is not in central package management's form:\n{defaults}");
            Assert.True(packages.All(p => pinned.Contains($"<PackageVersion Include=\"{p}\" Version=\"", StringComparison.Ordinal)),
                $"Directory.Packages.props lacks a version of the ServiceDefaults' [{string.Join(", ", packages)}]:\n{pinned}");
            Restore.Run(Path.Combine(folder, "Shop.slnx"));

            window.Confirm = (_, _, _) => Task.FromResult(true);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.Run(window, AppActions.SaveAll);
            Assert.True(before.AsSpan().SequenceEqual(File.ReadAllBytes(props)), $"Undo left Directory.Packages.props as\n{File.ReadAllText(props)}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Aspire's hosting integration for the database a service reads, added to the AppHost made where packages are
    /// managed centrally, turned on by Directory.Build.props or by a Directory.Packages.props whose versions are written out
    /// in full: its reference names no version and Directory.Packages.props pins it at the AppHost's Aspire version.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hosting_package_added_where_packages_are_managed_centrally_is_pinned_in_Directory_Packages_props(bool inBuildProps)
    {
        string folder = CentralFolder(inBuildProps, inBuildProps ? "    <PackageVersion Include=\"Npgsql\" Version=\"10.0.3\" />\n"
            : "    <PackageVersion Include=\"Npgsql\" Version=\"10.0.3\"></PackageVersion>\n", "<ItemGroup><PackageReference Include=\"Npgsql\" /></ItemGroup>");
        TestFolders.Write(folder, "Api/appsettings.json", "{\n  \"ConnectionStrings\": {\n    \"shopdb\": \"Host=localhost;Database=shop;Username=app;Password=app\"\n  }\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.WorkspaceModel!.Projects.Any(p => p.Name == "Api" && p.TargetFrameworks.Count > 0 && p.Packages.Any(q => q.Name == "Npgsql")),
                "Api's packages to be evaluated");
            Assert.Equal("Added Aspire orchestration", Add(window, "Api", existing: false));
            string host = File.ReadAllText(Path.Combine(folder, "Shop.AppHost", "Shop.AppHost.csproj"));
            string pinned = File.ReadAllText(Path.Combine(folder, "Directory.Packages.props"));
            Assert.True(host.Contains("<PackageReference Include=\"Aspire.Hosting.PostgreSQL\" />", StringComparison.Ordinal),
                $"The AppHost's Aspire.Hosting.PostgreSQL names a version, or is not there:\n{host}");
            Assert.True(pinned.Contains($"<PackageVersion Include=\"Aspire.Hosting.PostgreSQL\" Version=\"{AspireApp.TemplatesVersion}\" />", StringComparison.Ordinal),
                $"Directory.Packages.props does not pin Aspire.Hosting.PostgreSQL at the AppHost's Aspire version:\n{pinned}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Add Aspire Orchestration with a solution filter open puts the AppHost and ServiceDefaults it makes in the
    /// filter's list as well as the solution's, in its one command of Undo, so the filter opened again loads them, the
    /// project it leaves out still unloaded.</summary>
    [AvaloniaFact]
    public void Orchestration_added_under_an_open_solution_filter_puts_the_projects_it_makes_in_the_filter_so_the_filter_opened_again_loads_them()
    {
        const string Filter = "{\n  \"solution\": {\n    \"path\": \"Shop.slnx\",\n    \"projects\": [\n      \"Api\\\\Api.csproj\"\n    ]\n  }\n}\n";
        string folder = PathNames.Canonical(TestFolders.Create("aspire-filter-"));
        TestFolders.Write(folder, "Directory.Build.props", "<Project><PropertyGroup><AspireUseCliBundle>false</AspireUseCliBundle></PropertyGroup></Project>");
        TestFolders.Write(folder, "Shop.slnx", "<Solution><Project Path=\"Api/Api.csproj\" /><Project Path=\"Tools/Tools.csproj\" /></Solution>");
        TestFolders.Write(folder, "Shop.Api.slnf", Filter);
        TestFolders.Write(folder, "Api/Api.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
        TestFolders.Write(folder, "Api/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Hello\");\napp.Run();\n");
        TestFolders.Write(folder, "Tools/Tools.csproj", TestProjects.Library());
        AspireApp.UseFeed(folder);
        string filter = Path.Combine(folder, "Shop.Api.slnf");
        string listed = Filter.Replace("\"Api\\\\Api.csproj\"\n", "\"Api\\\\Api.csproj\",\n      \"Shop.AppHost\\\\Shop.AppHost.csproj\",\n"
            + "      \"Shop.ServiceDefaults\\\\Shop.ServiceDefaults.csproj\"\n", StringComparison.Ordinal);
        MainWindow window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.WorkspaceModel!.Projects.Any(p => p.Name == "Api" && p.TargetFrameworks.Count > 0), "Api to be evaluated");
            Assert.Equal("Added Aspire orchestration", Add(window, "Api", existing: false));
            Assert.True(File.ReadAllText(filter) == listed, $"The AppHost and ServiceDefaults made went in the solution but not in the filter open, which leaves them out once opened again:\n{File.ReadAllText(filter)}");

            window.Confirm = (_, _, _) => Task.FromResult(true);
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Undo);
            Ui.Run(window, AppActions.SaveAll);
            Assert.True(File.ReadAllText(filter) == Filter, $"Undo took the orchestration back but left the filter listing what it made: {window.Status}\n{File.ReadAllText(filter)}");
            Ui.Run(window, AppActions.FocusExplorer);
            Ui.Run(window, EditorActions.Redo);
            Ui.Run(window, AppActions.SaveAll);
            Assert.True(File.ReadAllText(filter) == listed, $"Redo made the orchestration again but not the filter's list: {window.Status}\n{File.ReadAllText(filter)}");
        }
        finally
        {
            window.Close();
        }
        window = Ui.OpenFolder(filter, languageServer: false);
        try
        {
            string Loaded() => string.Join(", ", window.ExplorerView.Tree.Rows.Where(r => r.Kind == NodeKind.Project)
                .Select(r => $"{r.Name} {(r.IsDimmed ? "unloaded" : "loaded")}").Order(StringComparer.Ordinal));
            Ui.WaitFor(() => Loaded() == "Api loaded, Shop.AppHost loaded, Shop.ServiceDefaults loaded, Tools unloaded",
                () => $"the filter opened again to load what orchestration made and leave Tools out ({Loaded()})");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A solution of one web service, Api (its items given), where packages are managed centrally: turned on by
    /// Directory.Build.props, else by a Directory.Packages.props, which holds the versions given; restored from the run's
    /// local feed and the package cache. Its folder, by its real path.</summary>
    private static string CentralFolder(bool inBuildProps, string versions, string items)
    {
        string folder = PathNames.Canonical(TestFolders.Create("aspire-central-"));
        string turnedOn = "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>";
        TestFolders.Write(folder, "Directory.Build.props", $"<Project><PropertyGroup><AspireUseCliBundle>false</AspireUseCliBundle>{(inBuildProps ? turnedOn : string.Empty)}</PropertyGroup></Project>");
        TestFolders.Write(folder, "Directory.Packages.props", "<Project>\n" + (inBuildProps ? string.Empty : $"  <PropertyGroup>\n    {turnedOn}\n  </PropertyGroup>\n")
            + (versions.Length > 0 ? $"  <ItemGroup>\n{versions}  </ItemGroup>\n" : string.Empty) + "</Project>\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution><Project Path=\"Api/Api.csproj\" /></Solution>");
        TestFolders.Write(folder, "Api/Api.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>{items}</Project>");
        TestFolders.Write(folder, "Api/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Hello\");\napp.Run();\n");
        AspireApp.UseFeed(folder);
        return folder;
    }

    private MainWindow Open()
    {
        MainWindow window = project.Open(project.PathOf("Api/Program.cs"));
        Ui.WaitFor(() => window.WorkspaceModel!.Projects.Where(p => p.Exists).All(p => p.TargetFrameworks.Count > 0),
            "the orchestration candidates to finish evaluation");
        return window;
    }

    private static AspireOrchestrationDialog Show(MainWindow window, string service)
    {
        Ui.Settle();
        string candidates = string.Join("; ", window.WorkspaceModel!.Projects.Select(p =>
            $"{p.Name}: file={p.Path}, exists={p.Exists}, output={p.OutputType ?? "<unknown>"}, frameworks=[{string.Join(',', p.TargetFrameworks)}], evaluated={p.IsEvaluated}, host={p.IsAspireHost}, test={p.IsTestProject}, problem={p.Problem ?? "<none>"}"));
        Ui.Run(window, AppActions.FindAction);
        window.KeyTextInput("Add Aspire Orchestration");
        Ui.Press(window, "ENTER");
        AspireOrchestrationDialog dialog = DialogButtonsTests.Check(Assert.Single(window.OwnedWindows.OfType<AspireOrchestrationDialog>()));
        Ui.Settle();
        List<CheckBox> choices = [.. dialog.ServiceProjects.ItemsSource!.Cast<CheckBox>()];
        Assert.True(choices.Count(c => Equals(c.Content, service)) == 1,
            $"Expected one {service} service checkbox; offered [{string.Join(", ", choices.Select(c => c.Content))}]. Projects when opening the dialog: {candidates}");
        CheckBox check = Assert.Single(choices, c => Equals(c.Content, service));
        Ui.Click(check);
        Assert.True(dialog.Add.IsEnabled, "Selecting a service did not enable Add");
        return dialog;
    }

    private static string Add(MainWindow window, string service, bool existing)
    {
        AspireOrchestrationDialog dialog = Show(window, service);
        if (existing)
        {
            Select(dialog, dialog.AppHost, "Host");
            Select(dialog, dialog.ServiceDefaults, "Defaults");
        }
        Ui.Click(dialog.Add);
        return Finished(window);
    }

    private static string Finished(MainWindow window) => Ui.Await(
        window.AspireOrchestrationResult ?? throw new Xunit.Sdk.XunitException("The orchestration operation was not started"),
        "the orchestration operation to finish", TimeSpan.FromMinutes(10))
        ?? throw new Xunit.Sdk.XunitException("The orchestration dialog was cancelled instead of adding the selected service");

    private static void Select(Window dialog, ComboBox combo, string name)
    {
        int index = combo.ItemsSource!.Cast<string>().ToList().IndexOf(name);
        Assert.True(index >= 0, $"The orchestration dialog does not offer {name}");
        Ui.Click(combo);
        Ui.Press(dialog, "HOME");
        for (int i = 0; i < index; i++)
        {
            Ui.Press(dialog, "DOWN");
        }
        Ui.Press(dialog, "ENTER");
    }

    private Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(project.Folder, "*", SearchOption.AllDirectories)
        .Where(path => !Path.GetRelativePath(project.Folder, path).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
        .ToDictionary(path => Path.GetRelativePath(project.Folder, path), File.ReadAllBytes);

    private void AssertSnapshot(Dictionary<string, byte[]> expected)
    {
        Dictionary<string, byte[]> actual = Snapshot();
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in expected)
        {
            Assert.True(bytes.AsSpan().SequenceEqual(actual[path]), $"Orchestration changed {path} unexpectedly");
        }
    }

    public sealed class Project : SharedProject
    {
        public Project() : base("aspire-orchestration-")
        {
            Put("Directory.Build.props", "<Project><PropertyGroup><AspireUseCliBundle>false</AspireUseCliBundle></PropertyGroup></Project>");
            Put("Shop.slnx", "<Solution><Project Path=\"Api/Api.csproj\" /><Project Path=\"Plain/Plain.csproj\" /><Project Path=\"Host/Host.csproj\" /><Project Path=\"Defaults/Defaults.csproj\" /><Project Path=\"WindowsDefaults/WindowsDefaults.csproj\" /></Solution>");
            foreach (string name in (string[])["Api", "Plain"])
            {
                Put($"{name}/{name}.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>");
                Put($"{name}/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Hello\");\napp.Run();\n");
            }
            Put("Host/Host.csproj", $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <Sdk Name="Aspire.AppHost.Sdk" Version="{AspireApp.TemplatesVersion}" />
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>{TestProjects.Framework}</TargetFramework><ImplicitUsings>enable</ImplicitUsings><IsAspireHost>true</IsAspireHost></PropertyGroup>
                  <ItemGroup><PackageReference Include="Aspire.Hosting.AppHost" Version="{AspireApp.TemplatesVersion}" /><ProjectReference Include="../Api/Api.csproj" /></ItemGroup>
                </Project>
                """);
            Put("Host/Program.cs", "using Aspire.Hosting;\nvar builder = DistributedApplication.CreateBuilder(args);\nvar api = builder.AddProject(projectPath: \"../Api/Api.csproj\", name: \"api\");\nbuilder.Build().Run();\n");
        }

        /// <summary>The pinned Aspire templates installed, the ServiceDefaults projects made by them, and the solution restored.</summary>
        public override async ValueTask InitializeAsync()
        {
            await InstallTemplates();
            foreach ((string name, string framework) in new[] { ("Defaults", TestProjects.Framework), ("WindowsDefaults", TestProjects.Framework + "-windows") })
            {
                ChildResult made = await ChildProcesses.RunAsync(ProjectTemplates.NewCommand(
                    ["aspire-servicedefaults", "--name", name, "--output", PathOf(name), "--framework", TestProjects.Framework, "--no-restore", "--no-update-check"], Folder),
                    limit: TimeSpan.FromMinutes(3));
                Assert.True(made.Succeeded, $"Could not create real ServiceDefaults fixture: {made.Output}{made.Error}");
                string path = PathOf($"{name}/{name}.csproj");
                string text = File.ReadAllText(path);
                File.WriteAllText(path, text.Replace($">{TestProjects.Framework}<", $">{framework}<", StringComparison.Ordinal));
            }
            AspireApp.UseFeed(Folder);
            await PrepareAsync(byRealPath: true);
        }

        private async Task InstallTemplates()
        {
            string version = AvaloniaApp.Metadata("AspireTemplatesVersion").ToLowerInvariant();
            string package = Path.Combine(AvaloniaApp.Metadata("NuGetPackageRoot"), "aspire.projecttemplates", version, $"aspire.projecttemplates.{version}.nupkg");
            Assert.True(File.Exists(package), $"Restore the test project first: pinned Aspire templates are missing at {package}");
            ChildResult installed = await ChildProcesses.RunAsync(ProjectTemplates.NewCommand(
                ["install", package, "--force"], Folder), limit: TimeSpan.FromMinutes(3));
            Assert.True(installed.Succeeded, $"Could not install pinned Aspire templates in the test sandbox: {installed.Output}{installed.Error}");
            ProjectTemplates.Forget();
        }

        private void Put(string name, string text) => TestFolders.Write(Folder, name, text);
    }
}
