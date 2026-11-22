using System.Reflection;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Aspire's starter and single-file AppHost as its templates at <see cref="AspireApp.PackageTemplatesVersion"/> make
/// them, each an AppHost of Aspire's AppHost package alone, no AppHost SDK, adding its services by path: restored together
/// from the run's feed (tests/AspireGraphs/Package*), shared by the window tests.</summary>
public sealed class AspirePackageApp : SharedProject
{
    public const string Name = "PackageAspire";

    /// <summary>Why a test that runs the starter skips (<see cref="CannotRun"/>): as a real AppHost of the SDK's does, or no CLI
    /// of this Aspire's own, whose bundle holds the dashboard and orchestrator such an AppHost's build names.</summary>
    public const string RunSkipped = "Runs a real AppHost of Aspire's package alone, which needs the aspire CLI of its version (PassengerAspireRuntime=true) on macOS or Linux, its bundle unpacked in the run's own folder";

    /// <summary>The aspire CLI of <see cref="AspireApp.PackageTemplatesVersion"/> the build downloaded, null where it did not.</summary>
    public static string? Cli { get; } = typeof(AspirePackageApp).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "AspirePackageCli")?.Value is { } path && File.Exists(path) ? path : null;

    public static bool CannotRun => AspireApp.Unavailable is not null || Cli is null;

    public AspirePackageApp() : base("aspire-package-")
    {
    }

    public override async ValueTask InitializeAsync()
    {
        AvaloniaApp.UseFeed(Folder);
        await AspireApp.MakeAsync(AspireApp.PackageTemplatesVersion, "aspire-starter", PathOf("Starter"), Name, "--use-redis-cache", "false", "--skipRestore");
        await AspireApp.MakeAsync(AspireApp.PackageTemplatesVersion, "aspire-apphost-singlefile", PathOf("Single"), "Single");
        string project = File.ReadAllText(Host);
        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk\">", project, StringComparison.Ordinal);
        Assert.Contains($"<PackageReference Include=\"{Passenger.Host.AspireHosts.Package}\" Version=\"{AspireApp.PackageTemplatesVersion}\" />", project, StringComparison.Ordinal);
        Assert.DoesNotContain(Passenger.Host.AspireHosts.Sdk, project, StringComparison.Ordinal);
        Assert.DoesNotContain(Passenger.Host.AspireHosts.Property, project, StringComparison.Ordinal);
        Assert.Contains($"#:package {Passenger.Host.AspireHosts.Package}@{AspireApp.PackageTemplatesVersion}", File.ReadAllText(SingleHost), StringComparison.Ordinal);
        File.WriteAllText(PathOf("PackageAspire.slnx"), "<Solution>\n" + string.Concat(new[] { "AppHost", "ApiService", "Web", "ServiceDefaults" }
            .Select(part => $"  <Project Path=\"Starter/{Name}.{part}/{Name}.{part}.csproj\" />\n")) + "</Solution>\n");
        string hostFolder = Path.GetDirectoryName(Host)!;
        string hostSource = Path.Combine(hostFolder, "AppHost.cs");
        string hostText = File.ReadAllText(hostSource);
        foreach (string service in new[] { "ApiService", "Web" })
        {
            string added = $"\"../{Name}.{service}/{Name}.{service}.csproj\")";
            Assert.Contains(added, hostText, StringComparison.Ordinal);
            hostText = hostText.Replace(added, added[..^1] + ", options => options.LaunchProfileName = \"http\")", StringComparison.Ordinal);
        }
        File.WriteAllText(hostSource, hostText);
        AspireApp.KeepToTheRun(hostFolder);
        if (!CannotRun)
        {
            // In the run's Aspire home, as a person's CLI unpacks it in theirs, where the AppHost's build finds it.
            string home = Environment.GetEnvironmentVariable("ASPIRE_HOME") ?? throw new InvalidOperationException("The run has no Aspire home of its own");
            System.Diagnostics.ProcessStartInfo setup = ChildProcesses.Command(Cli!, ["setup", "--install-path", home], Folder);
            setup.Environment["HOME"] = AspireCli.Home;
            setup.Environment[AspireVariables.TelemetryOptOut] = "1";
            ChildResult unpacked = await ChildProcesses.RunAsync(setup, limit: TimeSpan.FromMinutes(3).Scaled());
            Assert.True(unpacked.Succeeded, $"The aspire CLI {AspireApp.PackageTemplatesVersion} did not unpack its bundle:\n{unpacked.Output}{unpacked.Error}");
        }
        await PrepareAsync(byRealPath: true);
    }

    // By their real paths, as the IDE names them (macOS's temporary folder is reached through a link).
    public string Host => PathNames.Canonical(PathOf($"Starter/{Name}.AppHost/{Name}.AppHost.csproj"));
    public string SingleHost => PathNames.Canonical(PathOf("Single/apphost.cs"));
}

/// <summary>An AppHost of Aspire's package alone, as Aspire's templates make one from 17 (no AppHost SDK, its IsAspireHost
/// from the package's props), is found as an AppHost of the SDK's is: the project and the single-file program each an
/// AppHost with its Aspire run configuration, and the folder's Aspire window shown; a project not yet restored is one by
/// the package it references. The starter runs through the window as one of the SDK's does, its dashboard and orchestrator
/// from the aspire CLI's bundle, alone as its real AppHost's ports and programs ask.</summary>
[Collection(Alone.Name)]
public sealed class AspirePackageAppHostTests(AspirePackageApp project) : IClassFixture<AspirePackageApp>
{
    /// <summary>Aspire 17's AppHosts found as one flow, each step one test's check in an instance of its own: its
    /// starter and a single-file AppHost found with their run configurations and the Aspire window, and a project
    /// referencing Aspire's AppHost package an AppHost before its first restore while one saying otherwise is
    /// not.</summary>
    [AvaloniaFact]
    public void Aspire_17s_AppHosts_are_found_with_their_configurations_by_the_SDK_or_package_they_name()
    {
        Flow.StepApart("aspire 17s starter and single file AppHost are found with their run configurations and the Aspire window", () => new AspirePackageAppHostTests(project), t => t.Aspire_17s_starter_and_single_file_AppHost_are_found_with_their_run_configurations_and_the_Aspire_window());
        Flow.StepApart("a project referencing Aspires AppHost package is an AppHost before its first restore and one saying otherwise is not", () => new AspirePackageAppHostTests(project), t => t.A_project_referencing_Aspires_AppHost_package_is_an_AppHost_before_its_first_restore_and_one_saying_otherwise_is_not());
    }

    private void Aspire_17s_starter_and_single_file_AppHost_are_found_with_their_run_configurations_and_the_Aspire_window()
    {
        project.Reset();
        MainWindow window = project.Open(null, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Count == 4 && window.Projects.All(p => p.IsEvaluated),
                () => $"the starter's four projects evaluated, where {string.Join(", ", window.Projects.Select(p => $"{p.Name} {p.IsEvaluated} {p.Problem}"))}");
            ProjectModel host = window.Projects.Single(p => PathNames.Same(p.Path, project.Host));
            Assert.True(host.IsAspireHost, $"{host.Name}, an AppHost by Aspire's package alone, is not evaluated as an AppHost");
            Assert.All(window.Projects.Where(p => p != host), p => Assert.False(p.IsAspireHost, $"{p.Name}, a service of the AppHost, is taken for an AppHost"));
            Ui.WaitFor(() => window.Configurations.Any(c => c.Aspire && PathNames.Same(c.Project, project.Host))
                && window.Configurations.Any(c => PathNames.Same(c.Project, project.SingleHost)),
                () => $"the AppHosts' run configurations, where there are {string.Join(", ", window.Configurations.Select(c => $"{c.Name} ({c.Project}, Aspire {c.Aspire})"))}");
            RunConfiguration starter = window.Configurations.First(c => PathNames.Same(c.Project, project.Host));
            Assert.StartsWith(RunConfiguration.NameOf(host), starter.Name, StringComparison.Ordinal);
            Assert.True(window.AspireRuntime.IsAspire(starter), "The starter's AppHost configuration does not run as Aspire's");
            Assert.True(window.AspireRuntime.IsAspire(window.Configurations.First(c => PathNames.Same(c.Project, project.SingleHost))),
                "The single-file AppHost's configuration does not run as Aspire's");
            Ui.WaitFor(() => window.DockHost[ToolWindowIds.Aspire].Unserved is null, () => $"the Aspire window served, where it is not: {window.DockHost[ToolWindowIds.Aspire].Unserved}");
            Assert.Null(window.Actions.WhyNot(AppActions.ShowAspire, window.Actions.Context()));
        }
        finally
        {
            window.Close();
        }
    }

    private void A_project_referencing_Aspires_AppHost_package_is_an_AppHost_before_its_first_restore_and_one_saying_otherwise_is_not()
    {
        string folder = TestFolders.Create("aspire-unrestored-");
        string package = $"  <ItemGroup><PackageReference Include=\"{Passenger.Host.AspireHosts.Package}\" Version=\"{AspireApp.PackageTemplatesVersion}\" /></ItemGroup>\n";
        TestFolders.Write(folder, "Host/Host.csproj", TestProjects.Console().With(package));
        TestFolders.Write(folder, "Tool/Tool.csproj", TestProjects.Console($"<{Passenger.Host.AspireHosts.Property}>false</{Passenger.Host.AspireHosts.Property}>").With(package));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Count == 2 && window.Projects.All(p => p.IsEvaluated),
                () => $"both projects evaluated, where {string.Join(", ", window.Projects.Select(p => $"{p.Name} {p.IsEvaluated} {p.Problem}"))}");
            Assert.False(File.Exists(Path.Combine(folder, "Host", "obj", "project.assets.json")), "The AppHost was restored, so its package's props said what this test holds of its reference");
            Assert.True(window.Projects.Single(p => p.Name == "Host").IsAspireHost, "An AppHost of Aspire's package, not yet restored, is not taken for one");
            Assert.False(window.Projects.Single(p => p.Name == "Tool").IsAspireHost, "A project saying it is no AppHost is taken for one for the package it references");
            Ui.WaitFor(() => window.DockHost[ToolWindowIds.Aspire].Unserved is null, () => $"the Aspire window served, where it is not: {window.DockHost[ToolWindowIds.Aspire].Unserved}");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Skip = AspirePackageApp.RunSkipped, SkipType = typeof(AspirePackageApp), SkipWhen = nameof(AspirePackageApp.CannotRun))]
    public void Aspire_17s_starter_runs_through_the_window_its_services_started_by_the_IDE_as_their_AppHost_asks()
    {
        project.Reset();
        MainWindow window = project.Open(null, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Aspire && PathNames.Same(c.Project, project.Host) && c.LaunchProfile == "http"),
                "the starter's generated HTTP Aspire configuration");
            Assert.True(window.SelectConfiguration($"Aspire: {AspirePackageApp.Name}.AppHost: http"));
            Ui.Run(window, AppActions.RunProgram);
            window.ShowToolWindow(ToolWindowIds.Aspire);
            Ui.WaitFor(() => new[] { "apiservice", "webfrontend" }.All(name => window.Aspire.ResourceSnapshots.Any(resource =>
                resource.DisplayName == name && resource.State == AspireStates.Running && resource.IsProject
                    && window.AspireRuntime.FindService(new(project.Host, resource))?.ProcessId is > 0)),
                () => $"the API and frontend running, started by the IDE as the AppHost asked, where its resources are {string.Join(", ", window.Aspire.ResourceSnapshots.Select(r => r.ToString()))} "
                    + $"and its console says:\n{window.AspireRuntime.Apps.FirstOrDefault()?.Console.Document.Snapshot}", TimeSpan.FromMinutes(4));
        }
        finally
        {
            Ui.Await(window.AspireRuntime.StopAllAsync(), "the AppHost to stop");
            window.Close();
        }
    }
}
