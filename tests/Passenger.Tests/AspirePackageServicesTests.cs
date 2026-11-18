using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Workspace;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Aspire 17's starter, whose AppHost adds its services by path with AddDotnetProject and references none of them,
/// read as a 13.x AppHost's are: Add Aspire Orchestration adds a service by AddDotnetProject with no reference, Go to
/// Declaration goes from a service's path to its project, the AppHost's gutter marks each service it adds, and a service's
/// configuration is checked with what its AppHost gives it.</summary>
public sealed class AspirePackageServicesTests : IClassFixture<AspirePackageApp>
{
    private readonly AspirePackageApp project;

    public AspirePackageServicesTests(AspirePackageApp project)
    {
        this.project = project;
        project.Reset();
    }

    /// <summary>The AppHost's project and its source, from the fixture's folder.</summary>
    private const string HostProject = $"Starter/{AspirePackageApp.Name}.AppHost/{AspirePackageApp.Name}.AppHost.csproj";
    private const string HostFile = $"Starter/{AspirePackageApp.Name}.AppHost/AppHost.cs";

    private string HostSource => project.PathOf(HostFile);

    private string Api => $"{AspirePackageApp.Name}.ApiService";

    /// <summary>A service added to the AppHost by orchestration is added by path with AddDotnetProject, the AppHost referencing
    /// none and given Aspire.Hosting.Dotnet at its own Aspire version where it lacked it; a service it adds already, by
    /// AddDotnetProject with its options, is not added again.</summary>
    [AvaloniaFact]
    public void Orchestration_adds_a_service_to_Aspire_17s_AppHost_by_AddDotnetProject_referencing_none_and_takes_Aspire_Hosting_Dotnet()
    {
        string hostProject = File.ReadAllText(project.Host);
        string dotnet = $"    <PackageReference Include=\"Aspire.Hosting.Dotnet\" Version=\"{AspireApp.PackageTemplatesVersion}\" />\n";
        Assert.Contains(dotnet, hostProject.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        project.Rewrite(HostProject, hostProject.ReplaceLineEndings("\n").Replace(dotnet, string.Empty, StringComparison.Ordinal));
        string solution = project.PathOf($"{AspirePackageApp.Name}.slnx");
        project.Rewrite(Path.GetFileName(solution), File.ReadAllText(solution).Replace("</Solution>", "  <Project Path=\"Starter/Extra/Extra.csproj\" />\n</Solution>", StringComparison.Ordinal));
        Directory.CreateDirectory(project.PathOf("Starter/Extra"));
        project.Write("Starter/Extra/Extra.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
        project.Write("Starter/Extra/Program.cs", "var builder = WebApplication.CreateBuilder(args);\nvar app = builder.Build();\napp.MapGet(\"/\", () => \"Hello\");\napp.Run();\n");
        string hostBefore = File.ReadAllText(HostSource);
        MainWindow window = project.Open(null, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.WorkspaceModel!.Projects.Count == 5 && window.WorkspaceModel.Projects.All(p => p.TargetFrameworks.Count > 0),
                "the starter's projects and Extra evaluated");
            Assert.Equal("Added Aspire orchestration", AspireOrchestrationTests.Add(window, [Api, "Extra"], $"{AspirePackageApp.Name}.AppHost", $"{AspirePackageApp.Name}.ServiceDefaults"));
            string host = File.ReadAllText(HostSource);
            const string Added = "var extra = builder.AddDotnetProject(\"extra\", \"../Extra/Extra.csproj\");";
            Assert.True(host.Contains(Added, StringComparison.Ordinal), $"Extra was not added to the AppHost by its path with AddDotnetProject:\n{host}");
            Assert.True(host.ReplaceLineEndings("\n").Replace(Added + "\n", string.Empty, StringComparison.Ordinal) == hostBefore.ReplaceLineEndings("\n"),
                $"The AppHost was changed beyond Extra's line: the API service it adds already added again, or a service by the obsolete AddProject:\n{host}");
            string written = File.ReadAllText(project.Host);
            Assert.False(written.Contains("ProjectReference", StringComparison.Ordinal), $"The AppHost was given a reference to a service it adds by path:\n{written}");
            Assert.True(written.Contains($"<PackageReference Include=\"Aspire.Hosting.Dotnet\" Version=\"{AspireApp.PackageTemplatesVersion}\" />", StringComparison.Ordinal),
                $"The AppHost was not given Aspire.Hosting.Dotnet, which AddDotnetProject comes in, at its own Aspire version:\n{written}");
            Assert.Contains("AddServiceDefaults()", File.ReadAllText(project.PathOf("Starter/Extra/Program.cs")), StringComparison.Ordinal);
            Assert.Contains($"{AspirePackageApp.Name}.ServiceDefaults", File.ReadAllText(project.PathOf("Starter/Extra/Extra.csproj")), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Go to Declaration from the path AddDotnetProject adds a service by opens the service's project file.</summary>
    [AvaloniaFact]
    public void A_services_path_in_AddDotnetProject_goes_to_its_project()
    {
        MainWindow window = project.Open(HostSource);
        try
        {
            Ui.PlaceAfter(window.EditorArea.CodeEditor, $"\"../{Api}/{AspirePackageApp.Name}.Api");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => window.EditorArea.Active?.Name == $"{Api}.csproj", () => $"Go to Declaration from the API's path to open its project, where {window.EditorArea.Active?.Name} is shown");
            Assert.True(PathNames.Same(PathNames.Canonical(project.PathOf($"Starter/{Api}/{Api}.csproj")), window.EditorArea.Active!.Path));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The AppHost's gutter marks each service AddDotnetProject adds on the line it is added, for the AppHost's Aspire
    /// configuration, and a service's file is one of a service, as the AppHost runs its project.</summary>
    [AvaloniaFact]
    public void The_AppHosts_gutter_marks_each_service_AddDotnetProject_adds_and_its_files_are_a_services()
    {
        MainWindow window = project.Open(HostSource);
        try
        {
            OpenDocument host = window.EditorArea.Active!;
            string[] lines = host.Text.Snapshot.ToString().ReplaceLineEndings("\n").Split('\n');
            (string Service, int Line)[] expected = [.. new[] { "apiservice", "webfrontend" }
                .Select(name => (name, Array.FindIndex(lines, line => line.Contains($"AddDotnetProject(\"{name}\"", StringComparison.Ordinal))))];
            Assert.All(expected, e => Assert.True(e.Line >= 0, $"The starter's AppHost does not add {e.Service} by AddDotnetProject"));
            Ui.WaitFor(() => window.RunTargets(host).Where(t => t.Service is not null).Select(t => (t.Service!, t.Line)).Order().SequenceEqual(expected.Order()),
                () => $"a run mark for each service on its line {string.Join(", ", expected)}, where there are {string.Join(", ", window.RunTargets(host).Select(t => $"{t.Service} {t.Line}"))}");
            Assert.All(window.RunTargets(host).Where(t => t.Service is not null), t => Assert.True(t.Configuration.Aspire && PathNames.Same(t.Configuration.Project, project.Host),
                $"{t.Service}'s mark runs {t.Configuration.Name}, not the AppHost's Aspire configuration"));
            string api = PathNames.Canonical(project.PathOf($"Starter/{Api}/Program.cs"));
            Ui.WaitFor(() => window.Session.AspireFiles.RoleOf(api) == AspireRole.Service, () => $"the API's file to be a service's, where it is {window.Session.AspireFiles.RoleOf(api)}");
            Assert.Equal([project.Host], window.Session.AspireFiles.AppHostsRunning(PathNames.Canonical(project.PathOf($"Starter/{Api}/{Api}.csproj"))));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A key the AppHost gives a service it adds by path (WithEnvironment's Weather__Banner) is given to the service's
    /// reads, where a key nothing gives is still warned of.</summary>
    [AvaloniaFact]
    public void A_services_configuration_is_checked_with_what_its_AppHost_gives_it()
    {
        string program = $"Starter/{Api}/Program.cs";
        string text = File.ReadAllText(project.PathOf(program));
        Assert.Contains("builder.AddServiceDefaults();", text, StringComparison.Ordinal);
        project.Rewrite(program, text.Replace("builder.AddServiceDefaults();",
            "builder.AddServiceDefaults();\nvar banner = builder.Configuration[\"Weather:Banner\"];\nvar motto = builder.Configuration[\"Weather:Motto\"];", StringComparison.Ordinal));
        string host = File.ReadAllText(HostSource);
        Assert.Contains(".WithHttpHealthCheck(\"/health\");", host, StringComparison.Ordinal);
        project.Rewrite(HostFile, ReplaceFirst(host, ".WithHttpHealthCheck(\"/health\");", ".WithHttpHealthCheck(\"/health\")\n    .WithEnvironment(\"Weather__Banner\", \"sunny\");"));
        MainWindow window = project.Open(project.PathOf(program));
        try
        {
            Ui.WaitFor(() => Missing(window).SetEquals(["Weather:Motto"]),
                () => $"only the key nothing gives to be warned of, its AppHost giving Weather:Banner, where {string.Join(" | ", window.EditorArea.Active!.Markers.Select(m => m.Message))}");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A service of an AppHost read whole (each resource a variable, nothing but Aspire's calls) that discovers another
    /// the AppHost adds by AddDotnetProject without referencing it is warned of the reference it lacks, and Go to Declaration
    /// from the name it discovers goes to the AppHost's AddDotnetProject that names it.</summary>
    [AvaloniaFact]
    public void A_service_17s_AppHost_adds_by_path_is_warned_of_a_reference_it_lacks_and_goes_to_the_resource_it_names()
    {
        string web = $"{AspirePackageApp.Name}.Web";
        project.Rewrite(HostFile, $$"""
            var builder = DistributedApplication.CreateBuilder(args);
            var apiService = builder.AddDotnetProject("apiservice", "../{{Api}}/{{Api}}.csproj");
            var webFrontend = builder.AddDotnetProject("webfrontend", "../{{web}}/{{web}}.csproj");
            builder.Build().Run();

            """);
        MainWindow window = project.Open(project.PathOf($"Starter/{web}/Program.cs"));
        try
        {
            Ui.WaitFor(() => window.EditorArea.Active!.Markers.Any(m => m.Kind == Passenger.Editor.MarkerKind.Warning
                    && m.Message.Contains("'webfrontend' does not reference 'apiservice'", StringComparison.Ordinal)),
                () => $"the frontend to be warned it discovers the API with no reference to it, where {string.Join(" | ", window.EditorArea.Active!.Markers.Select(m => m.Message))}");
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "\"https+http://apis");
            Ui.Run(window, AppActions.GoToDeclaration);
            Ui.WaitFor(() => PathNames.Same(window.EditorArea.Active!.Path, PathNames.Canonical(HostSource)), "Go to Declaration from the API's name to open the AppHost");
            Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
            const string Declaring = "var apiService = builder.AddDotnetProject(\"";
            string line = editor.Document.Snapshot.GetLineText(editor.CaretPosition.Line);
            Assert.True(line.StartsWith(Declaring, StringComparison.Ordinal) && editor.CaretPosition.Column == Declaring.Length,
                $"Go to Declaration did not land at apiservice where the AppHost adds it, but at column {editor.CaretPosition.Column + 1} of: {line}");
        }
        finally
        {
            window.Close();
        }
    }

    private HashSet<string> Missing(MainWindow window) => [.. window.EditorArea.Active!.Markers
        .Where(m => m.Message.StartsWith($"Nothing {Api} runs with gives the configuration key '", StringComparison.Ordinal)).Select(m => m.Message.Split('\'')[1])];

    private static string ReplaceFirst(string text, string old, string replacement)
    {
        int at = text.IndexOf(old, StringComparison.Ordinal);
        return text[..at] + replacement + text[(at + old.Length)..];
    }
}
