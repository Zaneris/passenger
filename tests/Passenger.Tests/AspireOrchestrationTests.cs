using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Passenger.App;
using Passenger.App.Aspire;
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
