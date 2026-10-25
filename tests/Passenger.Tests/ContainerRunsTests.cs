using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Container run configurations as a person runs them against a real engine: a Compose solution's .NET services run
/// from Passenger's own build in their image's runtime stage, the browser opened at the published port, Rerun restarting
/// only the service whose build changed, and Stop leaving nothing; images mode's paths linked back to the workspace; a
/// Docker Compose project's configurations; a Dockerfile project, an SDK container project and a Docker launch profile.
/// Each skips, saying why, where no Linux engine runs; what the IDE makes carries the run's state label, removed at its
/// end, and the images it builds go with it. The Compose solution each test runs (<see cref="Shop(string)"/>) is restored
/// before the test takes it, a few at a time (<see cref="Restored"/>).</summary>
public sealed class ContainerRunsTests(RestoredFolders folders) : IClassFixture<RestoredFolders>, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The restored solutions the test took, which its end removes.</summary>
    private readonly List<string> taken = [];

    public void Dispose() => taken.ForEach(TestFolders.Delete);

    [AvaloniaFact]
    public void A_compose_solution_runs_its_dotnet_services_from_Passengers_build_reruns_only_what_changed_and_Stop_leaves_nothing()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("run");
        MainWindow window = Open(folder);
        const string Name = "Compose: compose.yaml";
        Assert.Contains(Name, window.Configurations.Select(c => c.Name));
        RunConfiguration configuration = window.Configurations.First(c => c.Name == Name);
        Assert.Equal(RunKind.Compose, configuration.Kind);
        Assert.True(window.SelectConfiguration(Name));
        try
        {
            Launched(window, "the Compose application to come up");
            Assert.StartsWith($"Running {Name}: 2 .NET services from Passenger's build, 1 other service", window.Status, StringComparison.Ordinal);
            Dictionary<string, string> states = States(project);
            Assert.Equal(["api", "cache", "web"], states.Keys.Order(StringComparer.Ordinal));
            Assert.All(states.Values, state => Assert.Equal("running", state));
            string web = Container(project, "web");
            Assert.Equal("dotnet /passenger/app/Web.dll", Docker.WithoutInit(Docker.Run("inspect", "-f", "{{.Path}} {{join .Args \" \"}}", web)));
            Assert.Equal(ContainerLabels.StateValue, Docker.Run("inspect", "-f", "{{index .Config.Labels \"net.passenger-ide.state\"}}", web));
            Assert.Equal(ContainerLabels.StateValue, Docker.Run("inspect", "-f", "{{index .Config.Labels \"net.passenger-ide.state\"}}", Container(project, "cache")));
            int webPort = Published(project, "web");
            Assert.Equal("web 1", Page(webPort));

            int apiPort = Published(project, "api");
            Ui.WaitFor(() => window.Browsed.Any(u => u.Port == apiPort), $"the browser to open at api's published port {apiPort}", TimeSpan.FromMinutes(1));
            Ui.WaitFor(() => Run(window).Contains("Now listening on", StringComparison.Ordinal), "the services' log in the Run window", TimeSpan.FromMinutes(1));

            string image = Docker.Run("inspect", "-f", "{{.Image}}", web);
            string apiStarted = Started(project, "api");
            string webStarted = Started(project, "web");
            File.WriteAllText(Path.Combine(folder, "Web", "Message.cs"), "public static class Message\n{\n    public const string Text = \"web 2\";\n}\n");
            Assert.True(window.Perform(AppActions.Rerun), "Rerun did not run");
            Ui.WaitFor(() => window.Status?.Contains("restarted web with the new build", StringComparison.Ordinal) == true,
                "Rerun to restart web", TimeSpan.FromMinutes(3));
            Assert.NotEqual(webStarted, Started(project, "web"));
            Assert.Equal(apiStarted, Started(project, "api"));
            Assert.Equal(image, Docker.Run("inspect", "-f", "{{.Image}}", Container(project, "web")));
            // On the port it had, so a browser open there stays right.
            Assert.Equal(webPort, Published(project, "web"));
            Assert.True(Ui.Pumped(() => Answer(webPort) == "web 2", TimeSpan.FromMinutes(1)), $"web did not answer with its new build: {Answer(webPort)}");
            Assert.True(window.IsRunning, "The run's log stopped being followed through the restart");

            // web alone restarted after api's change built: api still runs its old build, so Rerun restarts it.
            File.WriteAllText(Path.Combine(folder, "Api", "Message.cs"), "public static class Message\n{\n    public const string Text = \"api 2\";\n}\n");
            BuildOutcome built = Ui.Await(window.BuildAsync(), "the solution's build", TimeSpan.FromMinutes(3));
            Assert.True(built.Succeeded, built.Message);
            apiStarted = Started(project, "api");
            ContainerServicesTests.Act(window, ContainerServicesTests.Shown(window), project, "web", AppActions.ContainerRestart);
            Ui.WaitFor(() => window.Status?.Contains(": restarted web", StringComparison.Ordinal) == true, "web's restart from the Containers window", TimeSpan.FromMinutes(2));
            Assert.Equal(apiStarted, Started(project, "api"));
            Assert.True(window.Perform(AppActions.Rerun), "Rerun did not run");
            Ui.WaitFor(() => window.Status?.Contains("with the new build", StringComparison.Ordinal) == true || window.Status?.Contains("nothing restarted", StringComparison.Ordinal) == true,
                "Rerun to restart what changed", TimeSpan.FromMinutes(3));
            Assert.True(window.Status!.Split(": restarted ")[^1].Split(" with ")[0].Split(", ").Contains("api"),
                $"Rerun did not restart api, whose build changed before web's restart: {window.Status}");
            Assert.True(Ui.Pumped(() => Answer(apiPort) == "api 2", TimeSpan.FromMinutes(1)), $"api did not answer with its new build: {Answer(apiPort)}");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Assert.Equal(string.Empty, Docker.Run("network", "ls", "-q", "--filter", $"label=com.docker.compose.project={project}"));
        Ui.WaitFor(() => !window.IsRunning, "the log to stop being followed");
        Assert.Equal($"{Name} stopped: its containers were taken down", window.Status);
        window.Close();
    }

    [AvaloniaFact]
    public void With_a_fast_mode_run_up_builds_and_a_configurations_plan_each_finish()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("builds");
        MainWindow window = Open(folder);
        RunConfiguration compose = window.Configurations.First(c => c.Kind == RunKind.Compose);
        Assert.True(window.SelectConfiguration(compose.Name));
        try
        {
            Launched(window, "the Compose application to come up");
            // Its services run Passenger's build, mounted read-only: the builds and the project host's answers after it never wait on them.
            for (int turn = 1; turn <= 2; turn++)
            {
                BuildOutcome built = Ui.Await(window.BuildAsync(), $"build {turn} with the run up", TimeSpan.FromMinutes(2));
                Assert.True(built.Succeeded, built.Message);
            }
            string webProject = Path.Combine(folder, "Web", "Web.csproj");
            RunConfiguration web = window.Configurations.First(c => c.Kind == RunKind.Project && PathNames.Same(c.Project, webProject));
            (LaunchPlan? plan, string? problem) = Ui.Await(window.Builds.ResolvePlanAsync(web, null), $"{web.Name}'s plan with the run up", TimeSpan.FromMinutes(1));
            Assert.True(plan is not null, problem);
            Assert.All(States(project).Values, state => Assert.Equal("running", state));
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        window.Close();
    }

    /// <summary>A compose run builds its .NET services side by side in one dotnet build: with an error in each, it starts
    /// nothing, says the build failed in both, and Problems lists each one's error under its own project; mended, it comes
    /// up from one more build, and Rerun with nothing changed builds nothing, the Build window saying both are up to date.</summary>
    [AvaloniaFact]
    public void A_compose_run_builds_its_services_in_one_build_says_each_ones_errors_and_Rerun_builds_nothing_unchanged()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("together");
        foreach (string app in (string[])["Web", "Api"])
        {
            TestFolders.Write(folder, $"{app}/Message.cs", "public static class Message\n{\n    public const string Text = \"unbuilt\"\n}\n");
        }
        List<string> builds = [];
        EventHandler<ChildStart> started = (_, child) =>
        {
            if (child.Command.Contains(" build ", StringComparison.Ordinal))
            {
                lock (builds)
                {
                    builds.Add(child.Command);
                }
            }
        };
        ChildProcesses.Started += started;
        MainWindow window = Open(folder);
        try
        {
            Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
            string? refused = Ui.Await(window.RunAsync(), "the run whose services do not build", TimeSpan.FromMinutes(3));
            Assert.True(refused?.StartsWith("Build failed in Api and Web: 2 errors", StringComparison.Ordinal) == true,
                $"A run whose services both fail to build answered: {refused}");
            string[] built = Builds();
            Assert.True(built.Length == 1 && !built[0].Contains("Web.csproj", StringComparison.Ordinal) && !built[0].Contains("Api.csproj", StringComparison.Ordinal),
                $"The services were not built together in one dotnet build:\n{string.Join('\n', built)}");
            Assert.True(Docker.NoneLeft(project), "A run whose build failed started containers");
            ProblemsModel problems = window.BottomPanel.Problems;
            problems.Shown = ProblemsTab.Build;
            Assert.Equal(["Api", "Message.cs", "Web", "Message.cs"], problems.Rows.Where(r => r.Problem is null).Select(r => r.Label));
            foreach (string app in (string[])["Web", "Api"])
            {
                Problem error = Assert.Single(problems.Problems, p => p.Severity == ProblemSeverity.Error && p.Project == app);
                Assert.Equal(PathNames.Canonical(Path.Combine(folder, app, "Message.cs")), error.Path);
                Assert.Equal("CS1002", error.Code);
            }

            foreach (string app in (string[])["Web", "Api"])
            {
                File.WriteAllText(Path.Combine(folder, app, "Message.cs"), $"public static class Message\n{{\n    public const string Text = \"{app.ToLowerInvariant()} 1\";\n}}\n");
            }
            Launched(window, "the mended services to come up");
            Assert.Equal(2, Builds().Length);
            Assert.Equal("web 1", Served(window, project, "web"));
            Assert.Equal("api 1", Served(window, project, "api"));

            Assert.True(window.Perform(AppActions.Rerun), "Rerun did not run");
            Ui.WaitFor(() => window.Status?.Contains("nothing changed, nothing restarted", StringComparison.Ordinal) == true, "Rerun with nothing changed", TimeSpan.FromMinutes(2));
            window.BottomPanel.Build.Flush();
            string console = window.BottomPanel.Build.Document.Snapshot.ToString();
            Assert.True(Builds().Length == 2 && console.Contains(BuildRunController.UpToDateSaid(["Api", "Web"]), StringComparison.Ordinal),
                $"Rerun with nothing changed was to build nothing and say so, but started {Builds().Length - 2} more dotnet builds, the Build window saying:\n{console}");
        }
        finally
        {
            ChildProcesses.Started -= started;
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        window.Close();

        string[] Builds()
        {
            lock (builds)
            {
                return [.. builds];
            }
        }
    }

    [AvaloniaFact]
    public void In_images_mode_a_container_path_in_the_runs_output_links_to_the_workspace_file()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = TestFolders.Create("ctr-images-");
        string project = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}images";
        TestFolders.Write(folder, "src/Shop/Program.cs", "Console.WriteLine(\"shop\");\n");
        // An image built from the context into /src: what it prints names its own paths, as a stack trace from a build in an image does.
        TestFolders.Write(folder, "Dockerfile", $"FROM {Docker.Image}\nWORKDIR /src\nCOPY . .\nCMD [\"sh\", \"-c\", \"echo '   at Shop.Program.Main() in /src/src/Shop/Program.cs:line 1'; sleep 600\"]\n");
        // The image its files name is the person's own, which Passenger's build (its labels on it) never takes the name of.
        string own = $"{project}own:mine";
        Docker.Run("tag", Docker.Image, own);
        string ownImage = Docker.Run("image", "inspect", "-f", "{{.Id}}", own);
        TestFolders.Write(folder, "compose.yaml", $"name: {project}\nservices:\n  shop:\n    build: .\n    image: {own}\n    init: true\n");
        MainWindow window = Open(folder);
        window.SetConfigurations([window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with { Name = "Images", Mode = ContainerMode.Images, Generated = false }], "Images");
        try
        {
            Launched(window, "the image to be built and run");
            // The container's own line, its service's prefix before it: Podman's build (Buildah) writes the CMD it builds in too.
            static bool Logged(string l) => l.Contains("at Shop.Program.Main()", StringComparison.Ordinal) && l.Contains("| ", StringComparison.Ordinal);
            Ui.WaitFor(() => Run(window).Split('\n').Any(Logged), "the container's frame in the Run window", TimeSpan.FromMinutes(1));
            string line = Run(window).Split('\n').First(Logged);
            FileLink link = Assert.IsType<FileLink>(Assert.Single(window.BottomPanel.Run.Links.In(line)).Target);
            Assert.Equal(PathNames.Canonical(Path.Combine(folder, "src", "Shop", "Program.cs")), link.Path);
            Assert.Equal(0, link.Position.Line);
            Assert.Equal($"{project}-shop:passenger", Docker.Short(Docker.Run("inspect", "-f", "{{.Config.Image}}", Container(project, "shop"))));
            Assert.Equal(ownImage, Docker.Run("image", "inspect", "-f", "{{.Id}}", own));
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take the container down");
            window.Close();
            Docker.Ask(["rmi", own]);
        }
    }

    [AvaloniaFact]
    public void A_Dockerfile_project_an_SDK_container_project_and_a_Docker_launch_profile_each_run_their_project_in_a_container()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, _) = Restored("single");
        File.Delete(Path.Combine(folder, "compose.yaml"));
        // No httpPort: a port found free here is bound only after the build, so the engine chooses.
        TestFolders.Write(folder, "Api/Properties/launchSettings.json", """
            {
              "profiles": {
                "Container": { "commandName": "Docker", "launchBrowser": true, "launchUrl": "{Scheme}://{ServiceHost}:{ServicePort}/" }
              }
            }
            """);
        TestFolders.Write(folder, "Tool/Tool.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
            + "    <ImplicitUsings>enable</ImplicitUsings>\n    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Tool/Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/\", () => \"tool 1\");\napp.Run();\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n  <Project Path=\"Api/Api.csproj\" />\n  <Project Path=\"Tool/Tool.csproj\" />\n</Solution>\n");
        MainWindow window = Open(folder);
        Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Tool: SDK container"), "the SDK container project's configuration");
        Assert.Equal(RunKind.Dockerfile, window.Configurations.Single(c => c.Name == "Web: Dockerfile").Kind);
        Assert.Equal(RunKind.Dockerfile, window.Configurations.Single(c => c.Name == "Api: Container").Kind);
        Assert.DoesNotContain("Api: Dockerfile", window.Configurations.Select(c => c.Name));
        foreach ((string name, string answer) in (ReadOnlySpan<(string, string)>)[("Web: Dockerfile", "web 1"), ("Api: Container", "api 1"), ("Tool: SDK container", "tool 1")])
        {
            Assert.True(window.SelectConfiguration(name));
            string? project = null;
            try
            {
                Launched(window, $"{name} to come up");
                project = window.Builds.Containers!.Plan.ProjectName;
                string service = window.Builds.Containers.Plan.Dotnet.Single().Service;
                int published = Published(project, service);
                Assert.Equal(answer, Page(published));
                Ui.WaitFor(() => window.Browsed.Any(u => u.Port == published), $"{name}'s browser at port {published}", TimeSpan.FromMinutes(1));
            }
            finally
            {
                window.Perform(AppActions.Stop);
                if (project is not null)
                {
                    Docker.TakenDown(project, $"Stop to take {name}'s container down");
                }
                Ui.WaitFor(() => !window.IsRunning, $"{name}'s log to stop being followed");
            }
        }
        window.Close();
    }

    [AvaloniaFact]
    public void An_SDK_container_project_as_deployed_runs_the_image_the_SDK_makes()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = TestFolders.Create("ctr-sdkimage-");
        TestFolders.Write(folder, "Tool/Tool.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
            + "    <ImplicitUsings>enable</ImplicitUsings>\n    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Tool/Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/\", () => \"tool as deployed\");\napp.Run();\n");
        MainWindow window = Open(PathNames.Canonical(folder));
        Ui.WaitFor(() => window.Configurations.Any(c => c.Kind == RunKind.SdkContainer), "the SDK container project's configuration");
        RunConfiguration deployed = window.Configurations.First(c => c.Kind == RunKind.SdkContainer).Copied() with { Name = "Tool deployed", Mode = ContainerMode.Images, Generated = false };
        window.SetConfigurations([deployed], deployed.Name);
        string? project = null;
        string? image = null;
        try
        {
            Launched(window, "the SDK's image to be made and run");
            project = window.Builds.Containers!.Plan.ProjectName;
            string container = Container(project, "tool");
            image = Docker.Run("inspect", "-f", "{{.Config.Image}}", container);
            Assert.EndsWith($":{ContainerPlans.SdkImageTag}", image, StringComparison.Ordinal);
            Assert.DoesNotContain("/passenger/app", Docker.Run("inspect", "-f", "{{range .Mounts}}{{.Destination}} {{end}}", container), StringComparison.Ordinal);
            Assert.Equal("tool as deployed", Page(Published(project, "tool")));
        }
        finally
        {
            window.Perform(AppActions.Stop);
            if (project is not null)
            {
                Docker.TakenDown(project, "Stop to take the tool down");
            }
            window.Close();
            if (image is not null)
            {
                // The SDK's image carries no label of the run's to be removed by: this test's own goes now.
                Docker.Run("rmi", "-f", image);
            }
        }
    }

    [AvaloniaFact]
    public void The_Compose_page_lists_the_services_Compose_reads_and_keeps_a_services_project_and_what_Run_does_with_each()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, _) = Restored("table");
        // A service built from a Dockerfile that names no project: its project is the person's to choose.
        TestFolders.Write(folder, "Worker/Dockerfile", $"FROM {Docker.Image}\nCMD [\"sleep\", \"600\"]\n");
        File.AppendAllText(Path.Combine(folder, "compose.yaml"), "  worker:\n    build: { context: ., dockerfile: Worker/Dockerfile }\n");
        MainWindow window = Open(folder);
        RunConfigurationsDialog dialog = DialogButtonsTests.Check(window.ShowRunConfigurations());
        Assert.True(dialog.Choose("Compose: compose.yaml"));
        Press(dialog.CopyButton);
        ComposeServicesTable table = dialog.ServicesTable;
        Ui.WaitFor(() => table.Rows.Count == 4, $"the services read from Compose's model: {table.Said}", TimeSpan.FromMinutes(1));
        Assert.Equal(["api", "cache", "web", "worker"], table.Rows.Select(r => r.Service));
        ComposeServicesTable.Row Row(string service) => table.Rows.Single(r => r.Service == service);
        Assert.Equal("Web", Row("web").Project.SelectedItem);
        Assert.False(Row("cache").Project.IsVisible, "A service that runs an image offers a project");
        Assert.Equal(["Start", "Don't start"], (string[])Row("cache").Action.ItemsSource!);
        Assert.False(Row("worker").IsDotnet, "The worker's project was guessed");
        Row("worker").Project.SelectedItem = "Api";
        Ui.Settle();
        Assert.Equal(["Run", "Debug", "Don't start"], (string[])Row("worker").Action.ItemsSource!);
        Row("web").Action.SelectedItem = "Debug";
        Row("cache").Action.SelectedItem = "Don't start";
        Ui.Settle();
        Press(dialog.OkButton);
        RunConfiguration made = window.Configurations.Single(c => c.Name == "Compose: compose.yaml (1)");
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Api", "Api.csproj")), made.ServiceProjects["worker"]);
        Assert.False(made.ServiceProjects.ContainsKey("web"), "The project web's Dockerfile names was kept as a choice");
        Assert.Equal(ServiceStart.Debug, made.ServiceActions["web"]);
        Assert.Equal(ServiceStart.DontStart, made.ServiceActions["cache"]);
        Assert.False(made.ServiceActions.ContainsKey("api"), "A service left to run was kept as a choice");
        window.Close();
    }

    [AvaloniaFact]
    public void A_compose_files_services_and_a_Dockerfiles_final_FROM_have_run_icons_whose_menu_runs_that_service()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("marks");
        MainWindow window = Open(folder);
        window.OpenFile(Path.Combine(folder, "compose.yaml"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument compose = window.EditorArea.Active!;
        string[] lines = compose.Text.Snapshot.ToString().Split('\n');
        int[] services = [.. new[] { "  web:", "  api:", "  cache:" }.Select(s => Array.IndexOf(lines, s))];
        Ui.WaitFor(() => compose.RunMarks.Select(m => m.Line).Order().SequenceEqual(services.Order()), "the services' run icons");
        window.OpenFile(Path.Combine(folder, "Web", "Dockerfile"));
        Ui.Settle();
        Passenger.App.Workspace.OpenDocument dockerfile = window.EditorArea.Active!;
        int final = Array.FindLastIndex(dockerfile.Text.Snapshot.ToString().Split('\n'), l => l.StartsWith("FROM ", StringComparison.Ordinal));
        Ui.WaitFor(() => dockerfile.RunMarks.Select(m => m.Line).SequenceEqual([final]), "the Dockerfile's run icon on its final FROM");

        window.OpenFile(Path.Combine(folder, "compose.yaml"));
        Ui.Settle();
        int cache = Array.IndexOf(lines, "  cache:");
        Passenger.Editor.CodeEditor shown = window.EditorArea.CodeEditor;
        double x = Enumerable.Range(0, 400).First(x => shown.IsInMarkColumn(new Avalonia.Point(x, 0))) + 3;
        Ui.Click(shown, new Avalonia.Point(x, shown.PointOf(compose.Text.Snapshot.GetLineStart(cache)).Y + (shown.LineHeight / 2)));
        Avalonia.Controls.MenuItem[] items = [.. Assert.IsType<Avalonia.Controls.MenuFlyout>(window.RunMarkMenu).Items.OfType<Avalonia.Controls.MenuItem>()];
        Assert.Equal(["Run 'cache'", "Debug 'cache'", "Profile 'cache'"], items.Select(i => (string?)i.Header));
        Assert.True(items[1].IsEnabled, "Debug of a service was not offered");
        try
        {
            Ui.Click(items[0], settle: false);
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.RunProgram) is null && window.IsRunning, "cache to come up", TimeSpan.FromMinutes(2));
            Assert.True(window.Builds.Containers is not null, $"cache did not come up: {window.Status}");
            Assert.Equal(["cache"], States(project).Keys);
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take cache down");
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_service_that_is_not_dotnet_and_declares_a_watch_has_Composes_own_watch_while_the_run_lasts()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = TestFolders.Create("ctr-watch-");
        string project = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}watch";
        TestFolders.Write(folder, "site/index.html", "first\n");
        TestFolders.Write(folder, "Dockerfile", $"FROM {Docker.Image}\nCOPY site /www\nCMD [\"httpd\", \"-f\", \"-p\", \"8080\", \"-h\", \"/www\"]\n");
        TestFolders.Write(folder, "compose.yaml", $"name: {project}\nservices:\n  site:\n    build: .\n    init: true\n"
            + "    develop:\n      watch:\n        - { action: sync, path: ./site, target: /www }\n");
        MainWindow window = Open(folder);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            Launched(window, "the site to come up");
            string site = Container(project, "site");
            Assert.Equal("first", Docker.Run("exec", site, "cat", "/www/index.html"));
            Ui.WaitFor(() => Run(window).Contains("Watch enabled", StringComparison.Ordinal) || Run(window).Contains("Compose's watch of site ended", StringComparison.Ordinal),
                "Compose's watch to start in the Run window", TimeSpan.FromMinutes(1));
            Assert.True(Run(window).Contains("Watch enabled", StringComparison.Ordinal), $"Compose's watch did not start: {Run(window)}");
            File.WriteAllText(Path.Combine(folder, "site", "index.html"), "second\n");
            Assert.True(Ui.Pumped(() => Docker.Said("exec", site, "cat", "/www/index.html") == "second", TimeSpan.FromMinutes(1)),
                $"Compose's watch did not sync the change: {Run(window)}");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take the site down");
            window.Close();
        }
    }

    /// <summary>A run's log that ends while its services run (Compose's <c>logs --follow</c> crashes on Podman's own docker as a
    /// service is made anew; here its process is killed) is followed again from its end: Stop stays, what the service says
    /// next reaches the Run window, and Stop still takes it down.</summary>
    [AvaloniaFact]
    public void A_runs_log_that_ends_while_its_services_run_is_followed_again_and_Stop_still_takes_them_down()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        string folder = TestFolders.Create("ctr-relog-");
        string project = ProjectNamed("relog");
        TestFolders.Write(folder, "compose.yaml", $"name: {project}\nservices:\n  ticker:\n    image: {Docker.Image}\n    init: true\n"
            + "    command: [\"sh\", \"-c\", \"i=0; while true; do i=$$((i+1)); echo tick $$i; sleep 1; done\"]\n");
        MainWindow window = Open(folder);
        Assert.True(window.SelectConfiguration("Compose: compose.yaml"));
        try
        {
            Launched(window, "the ticker to come up");
            Ui.WaitFor(() => Run(window).Contains("tick 2\n", StringComparison.Ordinal), "the ticker's lines in the Run window", TimeSpan.FromMinutes(1));
            using (System.Diagnostics.Process log = System.Diagnostics.Process.GetProcessById(window.Runner.ProcessId ?? throw new InvalidOperationException("The run follows no log")))
            {
                log.Kill(entireProcessTree: true);
            }
            Ui.WaitFor(() => Run(window).Contains("services still run: following their log again", StringComparison.Ordinal) || !window.IsRunning,
                "the run's log to be followed again", TimeSpan.FromMinutes(1));
            Assert.True(window.IsRunning, $"The run took its log's end for its own: {window.Status}\n{Run(window)}");
            Assert.Null(window.Actions.WhyNot(AppActions.Stop, window.Actions.Context()));
            int followed = Run(window).LastIndexOf("following their log again", StringComparison.Ordinal);
            Ui.WaitFor(() => Run(window)[followed..].Contains("tick ", StringComparison.Ordinal), "the ticker's next lines after the log was followed again",
                TimeSpan.FromMinutes(1));
            Assert.Equal("running", States(project)["ticker"]);
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take the ticker down");
            window.Close();
        }
    }

    /// <summary>A service that serves HTTPS serves the development certificate exported for it, as PEM with its key and no
    /// password (a certificate made for the test, which dev-certs' stand-in writes, the machine's store never read), with the
    /// project's user secrets mounted; no command line the run started names a password.</summary>
    [AvaloniaFact]
    public void A_service_that_serves_HTTPS_has_the_development_certificate_and_the_projects_user_secrets_mounted()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        // A throwaway certificate for localhost stands in for the machine's, whose store is never read and so asks no one anything.
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using X509Certificate2 throwaway = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        (string folder, string project) = Restored("https");
        TestFolders.Write(folder, "Web/Web.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
            + "    <ImplicitUsings>enable</ImplicitUsings>\n    <UserSecretsId>passenger-test-https</UserSecretsId>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(TestRun.UserSecrets, "passenger-test-https/secrets.json", "{ \"Greeting\": \"https\" }\n");
        File.WriteAllText(Path.Combine(folder, "compose.yaml"), File.ReadAllText(Path.Combine(folder, "compose.yaml"))
            .Replace("    ports: [\"8080\"]\n    depends_on", "    ports: [\"8081\"]\n    environment: { ASPNETCORE_HTTPS_PORTS: \"8081\", ASPNETCORE_HTTP_PORTS: \"\" }\n    depends_on", StringComparison.Ordinal));
        // The dev-certs stand-in answers trusted, and its PEM export writes the test's certificate and key.
        List<string> commands = [];
        MainWindow window = Open(folder, (words, at) =>
        {
            lock (commands)
            {
                commands.Add(string.Join(' ', words));
            }
            if (words is ["dev-certs", "https", "-ep", string file, "--format", "Pem", "--no-password"])
            {
                File.WriteAllText(file, throwaway.ExportCertificatePem());
                File.WriteAllText(Path.ChangeExtension(file, ".key"), key.ExportPkcs8PrivateKeyPem());
            }
            return ChildProcesses.DotnetCommand(["--version"], at);
        });
        EventHandler<ChildStart> started = (_, child) =>
        {
            lock (commands)
            {
                commands.Add(child.Command);
            }
        };
        ChildProcesses.Started += started;
        RunConfiguration web = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with { Name = "Web over HTTPS", Services = ["web"], Generated = false };
        window.SetConfigurations([web], web.Name);
        try
        {
            Launched(window, "web to come up over HTTPS");
            string said = Docker.Run("compose", "-p", project, "port", "web", "8081").Split('\n')[0].Trim();
            int port = int.Parse(said[(said.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
            string? served = null;
            using HttpClientHandler handler = new()
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    served = certificate?.Thumbprint;
                    return true;
                },
            };
            using HttpClient https = new(handler) { Timeout = TimeSpan.FromSeconds(5) };
            string? answer = null;
            Assert.True(Ui.Pumped(() => (answer = Get(https, new Uri($"https://localhost:{port}/"))) is not null, TimeSpan.FromMinutes(1)), $"Nothing answered over HTTPS on {port}");
            Assert.Equal("web 1", answer);
            Assert.Equal(throwaway.Thumbprint, served);
            string mounts = Docker.Run("inspect", "-f", "{{range .Mounts}}{{.Destination}} {{end}}", Container(project, "web"));
            Assert.Contains("/https", mounts, StringComparison.Ordinal);
            // The project's own secrets, from the run's folder of them, never the person's.
            Assert.Contains($"{ContainerPlans.AppData}/Microsoft/UserSecrets/passenger-test-https", mounts, StringComparison.Ordinal);
            NoPasswords(commands, window);
        }
        finally
        {
            ChildProcesses.Started -= started;
            Docker.Stopped(window, project, "Stop to take web down");
            window.Close();
        }

        static string? Get(HttpClient client, Uri address)
        {
            try
            {
                return Task.Run(() => client.GetStringAsync(address)).GetAwaiter().GetResult();
            }
            catch (Exception failed) when (failed is HttpRequestException or TaskCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>Holds what a run started (and asked dev-certs for) to naming no password: the certificate exported as PEM
    /// with none, no program given one, nor the override's file of the run.</summary>
    private static void NoPasswords(List<string> commands, MainWindow window)
    {
        string[] all;
        lock (commands)
        {
            all = [.. commands];
        }
        Assert.Contains(all, c => c.Contains("dev-certs https -ep ", StringComparison.Ordinal) && c.EndsWith("--format Pem --no-password", StringComparison.Ordinal));
        string[] holding = [.. all.Where(c => (c.Contains("dev-certs", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(c, @"\s(-p|--password)(\s|=|$)"))
            || c.Contains("Password=", StringComparison.OrdinalIgnoreCase) || c.Contains("__Password", StringComparison.Ordinal))];
        Assert.True(holding.Length == 0, $"A command line the run started holds a password:\n{string.Join('\n', holding)}");
        string written = window.Builds.Containers!.Plan.Compose.Single(w => w.EndsWith(".override.yaml", StringComparison.Ordinal));
        Assert.DoesNotContain("password", File.ReadAllText(written), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A run whose service serves HTTPS with no trusted development certificate asks first, in a window that
    /// lets no agent in (--no-mcp): Run over HTTP serves it over HTTP; a failed export is said in the status bar;
    /// Create and Trust has dev-certs trust it and export it as PEM with no password, the person's alone to read, again
    /// once dev-certs names another certificate (an earlier Passenger's open folder and .pfx put right); Run and
    /// Profile stopped as dev-certs checks say so and leave nothing. dev-certs is a stand-in.</summary>
    [AvaloniaFact]
    public void A_run_that_serves_HTTPS_with_no_trusted_certificate_asks_to_create_and_trust_it_or_run_over_HTTP_and_says_an_export_that_fails()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in dev-certs is a shell script");
        string standIn = TestFolders.Create("devcerts-");
        (string folder, string project) = Restored("certs");
        // Told to serve HTTPS, it serves HTTP on the same port whatever it is given: the stand-in's certificate is none.
        TestFolders.Write(folder, "Web/Program.cs", """
            foreach (string told in (string[])["ASPNETCORE_Kestrel__Certificates__Default__Path", "ASPNETCORE_Kestrel__Certificates__Default__KeyPath", "ASPNETCORE_HTTPS_PORTS"])
            {
                Environment.SetEnvironmentVariable(told, null);
            }
            WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls("http://+:8081");
            WebApplication app = builder.Build();
            app.MapGet("/", () => Message.Text);
            app.Run();

            """);
        File.WriteAllText(Path.Combine(folder, "compose.yaml"), File.ReadAllText(Path.Combine(folder, "compose.yaml"))
            .Replace("    ports: [\"8080\"]\n    depends_on", "    ports: [\"8081\"]\n    environment: { ASPNETCORE_HTTPS_PORTS: \"8081\", ASPNETCORE_HTTP_PORTS: \"\" }\n    depends_on", StringComparison.Ordinal));
        MainWindow window = Open(folder, DevCerts.In(standIn), agentsAllowed: false);
        RunConfiguration web = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with { Name = "Web over HTTPS", Services = ["web"], Generated = false };
        window.SetConfigurations([web], web.Name);
        List<string> commands = [];
        EventHandler<ChildStart> started = (_, child) =>
        {
            lock (commands)
            {
                commands.Add(child.Command);
            }
        };
        ChildProcesses.Started += started;
        string Asked() => DevCerts.Asked(standIn);
        bool Exported() => Asked().Split('\n').Any(l => l.StartsWith("dev-certs https -ep ", StringComparison.Ordinal));
        string Env() => Docker.Run("inspect", "-f", "{{json .Config.Env}}", Container(project, "web"));
        string Mounts() => Docker.Run("inspect", "-f", "{{range .Mounts}}{{.Destination}} {{end}}", Container(project, "web"));
        try
        {
            File.WriteAllText(Path.Combine(standIn, "check"), "0");
            File.WriteAllText(Path.Combine(standIn, "slow"), string.Empty);
            foreach ((string action, Func<Task<string?>> start) in (ReadOnlySpan<(string, Func<Task<string?>>)>)[(AppActions.RunProgram, () => window.RunAsync()),
                (AppActions.Profile, () => window.ProfileAsync())])
            {
                Task<string?> starting = start();
                Ui.PumpUntil(() => window.Actions.Work.Of(action)?.Stage.StartsWith("Checking the HTTPS", StringComparison.Ordinal) == true || starting.IsCompleted,
                    $"{action} to check the development certificate", TimeSpan.FromMinutes(5));
                Assert.False(starting.IsCompleted, $"{action} ended before it checked the certificate: {(starting.IsCompleted ? starting.Result : null)}");
                Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
                Assert.Equal("Web over HTTPS was stopped as it started", Ui.Await(starting, $"{action} to end once stopped", TimeSpan.FromMinutes(1)));
            }
            Assert.False(window.ProfilerView.Status.StartsWith("Starting", StringComparison.Ordinal), $"The profiler still says {window.ProfilerView.Status}");
            Assert.Equal(string.Empty, Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"));
            File.Delete(Path.Combine(standIn, "slow"));
            File.Delete(Path.Combine(standIn, "asked"));

            File.WriteAllText(Path.Combine(standIn, "check"), "7");
            Task<string?> running = window.RunAsync();
            CertificateQuestionDialog asked = Question(window, running);
            Assert.Equal("The HTTPS development certificate is not trusted", asked.Title);
            Press(asked.HttpButton);
            Launched(window, running, "web to come up over HTTP");
            Assert.Contains("web over HTTP for this run: the development certificate is not trusted", window.Status, StringComparison.Ordinal);
            Assert.Equal("dev-certs https --check --trust\n", Asked());
            Assert.Contains("\"ASPNETCORE_HTTP_PORTS=8081\"", Env(), StringComparison.Ordinal);
            Assert.DoesNotContain("/https", Mounts(), StringComparison.Ordinal);
            Assert.Equal("web 1", Page(Published(project, "web", 8081)));
            Stopped(window, project);

            File.WriteAllText(Path.Combine(standIn, "check"), "0");
            File.WriteAllText(Path.Combine(standIn, "fail"), string.Empty);
            File.Delete(Path.Combine(standIn, "asked"));
            Launched(window, window.RunAsync(), "web to come up over HTTP with no certificate exported");
            Assert.Null(window.CertificateQuestion);
            Assert.Contains("web over HTTP for this run: the development certificate could not be exported: dotnet dev-certs https -ep ended with exit code 3: The keychain is locked",
                window.Status, StringComparison.Ordinal);
            Assert.Equal("open the Run window", window.StatusPlace?.Name);
            Assert.Contains("web serves HTTP for this run: the development certificate could not be exported", Run(window), StringComparison.Ordinal);
            Assert.Contains("\"ASPNETCORE_HTTP_PORTS=8081\"", Env(), StringComparison.Ordinal);
            Stopped(window, project);

            File.WriteAllText(Path.Combine(standIn, "check"), "6");
            File.Delete(Path.Combine(standIn, "fail"));
            File.Delete(Path.Combine(standIn, "asked"));
            running = window.RunAsync();
            asked = Question(window, running);
            Assert.Equal("No HTTPS development certificate", asked.Title);
            Press(asked.CreateButton);
            Launched(window, running, "web to come up with the certificate made and trusted");
            Assert.Equal(["dev-certs https --check --trust", "dev-certs https --trust"], Asked().Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(2));
            Assert.StartsWith("dev-certs https -ep ", Asked().Split('\n', StringSplitOptions.RemoveEmptyEntries)[2], StringComparison.Ordinal);
            Assert.Contains("/https", Mounts(), StringComparison.Ordinal);
            Assert.Contains("ASPNETCORE_Kestrel__Certificates__Default__Path=/https/aspnetapp.pem", Env(), StringComparison.Ordinal);
            Assert.Contains("ASPNETCORE_Kestrel__Certificates__Default__KeyPath=/https/aspnetapp.key", Env(), StringComparison.Ordinal);
            Assert.DoesNotContain("over HTTP for this run", window.Status, StringComparison.Ordinal);
            NoPasswords(commands, window);
            // The override holds the configuration's environment: no one but its owner reads it.
            string written = window.Builds.Containers!.Plan.Compose.Single(w => w.EndsWith(".override.yaml", StringComparison.Ordinal));
            string https = Path.Combine(window.Builds.Containers!.Plan.State, "https");
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(written));
                Assert.Equal("drwx------", File.ReadAllText(Path.Combine(standIn, "folder")).Trim());
                OwnersAlone(https, "aspnetapp.pem", "aspnetapp.key");
            }
            Stopped(window, project);

            // The copy kept is not known to be the certificate dev-certs names now: exported again, then kept.
            File.WriteAllText(Path.Combine(standIn, "check"), "0");
            foreach ((string thumbprint, bool again) in (ReadOnlySpan<(string, bool)>)[(new string('A', 40), true), (new string('A', 40), false), (new string('B', 40), true)])
            {
                File.WriteAllText(Path.Combine(standIn, "thumbprint"), thumbprint);
                File.Delete(Path.Combine(standIn, "asked"));
                File.Delete(Path.Combine(standIn, "folder"));
                if (again && !OperatingSystem.IsWindows())
                {
                    // As an earlier Passenger left them: the folder open to others, and the key theirs to read.
                    File.SetUnixFileMode(https, File.GetUnixFileMode(https) | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    File.SetUnixFileMode(Path.Combine(https, "aspnetapp.key"), File.GetUnixFileMode(Path.Combine(https, "aspnetapp.key")) | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                }
                // An earlier Passenger's .pfx and its password, which the mounted folder would give the container.
                File.WriteAllText(Path.Combine(https, "aspnetapp.pfx"), "earlier");
                File.WriteAllText(Path.Combine(https, "password"), "earlier");
                Launched(window, window.RunAsync(), "web to come up with the certificate dev-certs names");
                Assert.True(Exported() == again, $"With dev-certs naming {thumbprint[..4]}…, the certificate was {(again ? "not exported again" : "exported again")}: {Asked()}");
                Assert.Contains("ASPNETCORE_Kestrel__Certificates__Default__Path=/https/aspnetapp.pem", Env(), StringComparison.Ordinal);
                Assert.False(File.Exists(Path.Combine(https, "aspnetapp.pfx")) || File.Exists(Path.Combine(https, "password")),
                    "An earlier Passenger's .pfx and password are still in the folder the container mounts");
                if (again && !OperatingSystem.IsWindows())
                {
                    Assert.Equal("drwx------", File.ReadAllText(Path.Combine(standIn, "folder")).Trim());
                    OwnersAlone(https, "aspnetapp.pem", "aspnetapp.key", "thumbprint");
                }
                NoPasswords(commands, window);
                Stopped(window, project);
            }
        }
        finally
        {
            ChildProcesses.Started -= started;
            Docker.Stopped(window, project, "Stop to take web down");
            window.Close();
        }

        static void OwnersAlone(string folder, params string[] files)
        {
            foreach (string file in files)
            {
                Assert.True(File.GetUnixFileMode(Path.Combine(folder, file)) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
                    $"The certificate's {file} is {File.GetUnixFileMode(Path.Combine(folder, file))}, which others may read");
            }
        }

        static CertificateQuestionDialog Question(MainWindow window, Task<string?> running)
        {
            Ui.PumpUntil(() => window.CertificateQuestion is not null || running.IsCompleted, "the run to ask about the certificate", TimeSpan.FromMinutes(5));
            Assert.True(window.CertificateQuestion is not null, $"The run asked nothing of its certificate: {(running.IsCompleted ? running.Result : null)}");
            return DialogButtonsTests.Check(window.CertificateQuestion!);
        }

        static void Launched(MainWindow window, Task<string?> running, string what)
        {
            string? refused = Ui.Await(running, what, TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{Run(window)}");
        }

        static void Stopped(MainWindow window, string project)
        {
            Docker.Stopped(window, project, "Stop to take web down", "com.docker.compose.service=web");
            Ui.WaitFor(() => !window.IsRunning, "the log to stop being followed");
        }
    }

    /// <summary>A service's user secrets reach it in its container, from Passenger's build and as deployed by an image whose
    /// user is its own (on Linux, run as the person, who alone may read them): its project's folder of them alone is
    /// mounted, where .NET finds it whoever the app runs as, and never another project's. The backing service the runs keep
    /// after Stop runs on for the next, until the folder closes.</summary>
    [AvaloniaFact]
    public void A_services_user_secrets_reach_it_in_its_container_from_Passengers_build_and_as_deployed_and_only_its_own_are_mounted_its_backing_service_kept()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("secrets");
        string id = $"passenger-test-{project}";
        TestFolders.Write(folder, "Web/Web.csproj", $"<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
            + $"    <ImplicitUsings>enable</ImplicitUsings>\n    <UserSecretsId>{id}</UserSecretsId>\n  </PropertyGroup>\n</Project>\n");
        TestFolders.Write(folder, "Web/Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/\", () => app.Configuration[\"Greeting\"] ?? \"no secret\");\napp.Run();\n");
        // Kept as dotnet user-secrets keeps them: readable by their owner alone.
        string secrets = Path.Combine(TestRun.UserSecrets, id, "secrets.json");
        TestFolders.Write(TestRun.UserSecrets, $"{id}/secrets.json", "{ \"Greeting\": \"from secrets\" }\n");
        TestFolders.Write(TestRun.UserSecrets, "another-project/secrets.json", "{ \"Greeting\": \"another's\" }\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.GetDirectoryName(secrets)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // As deployed: web published (no static web assets of this machine's to look for) into the runtime's image, run as its own user.
        ChildResult built = Task.Run(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["publish", Path.Combine(folder, "Web", "Web.csproj"), "-c", "Debug",
            "-o", Path.Combine(folder, "deployed"), "-nologo", "-v:q"], folder), null, TimeSpan.FromMinutes(5).Scaled(), CancellationToken.None)).GetAwaiter().GetResult();
        Assert.True(built.Succeeded, $"web did not build: {built.Output}{built.Error}");
        // Its files readable by any user whatever the mode the run made them with: in the image they are root's.
        TestFolders.Write(folder, "Web/Deployed.Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:10.0\nUSER $APP_UID\nWORKDIR /app\nCOPY --chmod=755 deployed .\nEXPOSE 8080\nENTRYPOINT [\"dotnet\", \"Web.dll\"]\n");
        TestFolders.Write(folder, "deployed.yaml", "services:\n  web:\n    build: { context: ., dockerfile: Web/Deployed.Dockerfile }\n    environment: { ASPNETCORE_ENVIRONMENT: Development }\n");
        MainWindow window = Open(folder);
        RunConfiguration fast = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "Web from Passenger's build",
            Services = ["web"],
            KeepBackingServices = true,
            Generated = false,
        };
        RunConfiguration deployed = fast with { Name = "Web as deployed", Mode = ContainerMode.Images, ComposeFiles = [.. fast.ComposeFiles, Path.Combine(folder, "deployed.yaml")] };
        window.SetConfigurations([fast, deployed], fast.Name);
        bool closed = false;
        try
        {
            foreach (RunConfiguration each in (RunConfiguration[])[fast, deployed])
            {
                Assert.True(window.SelectConfiguration(each.Name));
                Launched(window, $"{each.Name} to come up");
                Assert.Equal(["cache", "web"], States(project).Keys.Order(StringComparer.Ordinal));
                Assert.Equal("from secrets", Served(window, project, "web"));
                string[] mounts = Docker.Run("inspect", "-f", "{{range .Mounts}}{{.Destination}}\n{{end}}", Container(project, "web")).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal([$"{ContainerPlans.AppData}/Microsoft/UserSecrets/{id}"], mounts.Where(m => m.Contains("secrets", StringComparison.OrdinalIgnoreCase)));
                if (OperatingSystem.IsLinux() && each.Mode == ContainerMode.Images)
                {
                    Assert.Contains("web runs as you", window.Status, StringComparison.Ordinal);
                }
                Docker.Stopped(window, project, $"Stop to take {each.Name} down", "com.docker.compose.service=web");
                Assert.Equal("running", States(project)["cache"]);
                if (each == fast)
                {
                    Assert.Equal($"{fast.Name} stopped: its .NET services were taken down, cache kept running", window.Status);
                }
                Ui.WaitFor(() => !window.IsRunning, "the log to stop being followed");
            }
            closed = true;
            window.Close();
            Assert.True(Ui.Pumped(() => Docker.NoneLeft(project), TimeSpan.FromMinutes(2)), "The kept cache outlived its folder");
        }
        finally
        {
            if (!closed)
            {
                window.Perform(AppActions.Stop);
                window.Close();
                Docker.TakenDown(project, "Stop and the folder's end to take every container down");
            }
        }
    }

    /// <summary>Containers of the run's Compose project that Passenger did not make (the person's own docker compose up's), of
    /// a service the run would start, are replaced only once the person says so: Cancel runs nothing and leaves them as they
    /// are; Replace Them runs it in their place, and Stop then takes the run's down. One of a service the run never starts,
    /// an orphan of the project, is no one's to replace: it outlives the run's start and its Stop, only what carries this
    /// process's labels taken down.</summary>
    [AvaloniaFact]
    public void Containers_of_the_project_Passenger_did_not_make_are_replaced_only_once_the_person_says_so_and_an_orphan_outlives_the_run()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("theirs");
        // The person's own cache, from their own compose up (with the run's label, so the run's end removes it should this test not).
        TestFolders.Write(folder, "theirs.yaml", $"services:\n  cache:\n    labels: [\"{Docker.RunLabel}\"]\n");
        Docker.Run("compose", "-f", Path.Combine(folder, "compose.yaml"), "-f", Path.Combine(folder, "theirs.yaml"), "up", "-d", "cache");
        string theirs = Container(project, "cache");
        // Their own container of a service the compose file does not have.
        string own = $"{project}-own";
        Docker.Run(["run", "-d", .. Docker.AnyPids, "--name", own, "--label", Docker.RunLabel, "--label", $"com.docker.compose.project={project}",
            "--label", "com.docker.compose.service=notes", "--label", "com.docker.compose.oneoff=False", Docker.Image, "sleep", "600"]);
        MainWindow window = Open(folder);
        RunConfiguration cache = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with { Name = "Cache", Services = ["cache"], Generated = false };
        window.SetConfigurations([cache], cache.Name);
        try
        {
            Task<string?> running = window.RunAsync();
            ReplaceContainersDialog asked = Question(window, running);
            string listed = string.Join('\n', asked.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text));
            Assert.Contains($"{project}-cache-1", listed, StringComparison.Ordinal);
            Assert.DoesNotContain(own, listed, StringComparison.Ordinal);
            Press(asked.CancelButton);
            Assert.Equal($"Cache was not run: {project}-cache-1 of {project}, which Passenger did not make, were left as they are", Ui.Await(running, "Run to end once Cancel was answered", TimeSpan.FromMinutes(1)));
            Assert.Equal(theirs, Container(project, "cache"));
            Assert.Equal("running", Docker.State(theirs));

            running = window.RunAsync();
            Press(Question(window, running).ReplaceButton);
            string? refused = Ui.Await(running, "cache to come up in place of the person's", TimeSpan.FromMinutes(5));
            Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{Run(window)}");
            string ours = Container(project, "cache");
            Assert.NotEqual(theirs, ours);
            Assert.Equal(ContainerLabels.ProcessValue, Docker.Run("inspect", "-f", $"{{{{index .Config.Labels \"{ContainerLabels.Process}\"}}}}", ours));
            Assert.Equal("running", Docker.State(own));
            Docker.Stopped(window, project, "Stop to take the run's cache down", "com.docker.compose.service=cache");
            Assert.Equal("running", Docker.State(own));
            Ui.WaitFor(() => !window.IsRunning, "the log to stop being followed");
        }
        finally
        {
            window.Perform(AppActions.Stop);
            foreach (string left in Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}").Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                Docker.Run("rm", "-f", left);
            }
            window.Close();
        }

        static ReplaceContainersDialog Question(MainWindow window, Task<string?> running)
        {
            Ui.PumpUntil(() => window.ReplaceQuestion is not null || running.IsCompleted, "the run to ask about the containers it would replace", TimeSpan.FromMinutes(2));
            Assert.True(window.ReplaceQuestion is not null, $"The run asked nothing of the person's own container: {(running.IsCompleted ? running.Result : null)}");
            return DialogButtonsTests.Check(window.ReplaceQuestion!);
        }
    }

    /// <summary>A configuration whose every service is marked Don't start runs nothing, saying why, where up given no service
    /// would have started them all.</summary>
    [AvaloniaFact]
    public void A_configuration_that_starts_no_service_says_so_and_starts_nothing()
    {
        Assert.SkipWhen(Docker.WhyNot is not null, Docker.WhyNot ?? string.Empty);
        (string folder, string project) = Restored("nothing");
        MainWindow window = Open(folder);
        RunConfiguration none = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "None",
            ServiceActions = new Dictionary<string, ServiceStart> { ["web"] = ServiceStart.DontStart, ["api"] = ServiceStart.DontStart, ["cache"] = ServiceStart.DontStart },
            Generated = false,
        };
        window.SetConfigurations([none], none.Name);
        try
        {
            Assert.Equal("None starts nothing: every service it would start is marked Don't start (Run > Edit Configurations)",
                Ui.Await(window.RunAsync(), "Run to refuse", TimeSpan.FromMinutes(2)));
            Assert.Equal(string.Empty, Docker.Run("ps", "-aq", "--filter", $"label=com.docker.compose.project={project}"));
        }
        finally
        {
            window.Perform(AppActions.Stop);
            window.Close();
        }
    }

    private static void Press(Avalonia.Controls.Button button)
    {
        Ui.Click(button);
    }

    /// <summary>A folder with a solution of two ASP.NET Core projects, each with a Dockerfile of .NET's own stages, and a
    /// compose.yaml running them beside a busybox "cache" they do not need; its Compose project named for the test.</summary>
    internal static (string Folder, string Project) Shop(string name)
    {
        string folder = TestFolders.Create($"ctr-{name}-");
        string project = ProjectNamed(name);
        WriteShop(folder, project);
        return (PathNames.Canonical(folder), project);
    }

    /// <summary><see cref="Shop(string)"/>'s solution, restored with the next few tests' before this one took it, its Compose
    /// project named for the test; the test's end removes it.</summary>
    private (string Folder, string Project) Restored(string name)
    {
        string folder = folders.Take("ctr-shop-", made =>
        {
            WriteShop(made, ProjectNamed("shop"));
            return Path.Combine(made, "Shop.slnx");
        });
        taken.Add(folder);
        string project = ProjectNamed(name);
        TestFolders.Write(folder, "compose.yaml", Compose(project));
        return (PathNames.Canonical(folder), project);
    }

    /// <summary>A test's Compose project's name: the test run's and the test's own, so whatever it leaves is told apart.</summary>
    private static string ProjectNamed(string name) => $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}{name}";

    private static void WriteShop(string folder, string project)
    {
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n  <Project Path=\"Api/Api.csproj\" />\n</Solution>\n");
        foreach (string app in (string[])["Web", "Api"])
        {
            TestFolders.Write(folder, $"{app}/{app}.csproj", TestProjects.Web("<ImplicitUsings>enable</ImplicitUsings>"));
            TestFolders.Write(folder, $"{app}/Program.cs", "WebApplication app = WebApplication.Create(args);\napp.MapGet(\"/\", () => Message.Text);\napp.Run();\n");
            TestFolders.Write(folder, $"{app}/Message.cs", $"public static class Message\n{{\n    public const string Text = \"{app.ToLowerInvariant()} 1\";\n}}\n");
            TestFolders.Write(folder, $"{app}/Dockerfile", $"""
                FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
                USER $APP_UID
                WORKDIR /app
                EXPOSE 8080

                FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
                WORKDIR /src
                COPY . .
                RUN dotnet publish "{app}/{app}.csproj" -c Release -o /app/publish

                FROM base AS final
                WORKDIR /app
                COPY --from=build /app/publish .
                ENTRYPOINT ["dotnet", "{app}.dll"]

                """);
        }
        TestFolders.Write(folder, "compose.yaml", Compose(project));
    }

    /// <summary>The solution's compose.yaml, its Compose project of this name.</summary>
    private static string Compose(string project) => $$"""
        name: {{project}}
        services:
          web:
            build: { context: ., dockerfile: Web/Dockerfile }
            ports: ["8080"]
            depends_on: { cache: { condition: service_healthy } }
          api:
            build: { context: ., dockerfile: Api/Dockerfile }
            ports: ["8080"]
          cache:
            image: {{Docker.Image}}
            command: ["sh", "-c", "mkdir -p /www && echo cache > /www/index.html && exec httpd -f -p 6379 -h /www"]
            healthcheck: { test: ["CMD", "wget", "-q", "-O", "-", "http://localhost:6379/"], interval: 1s, retries: 30 }

        """;

    /// <summary>A window on a Compose solution; with no C# language server unless a test asks it something (Hot Reload),
    /// since a container run, its debugging and its profiling ask it nothing.</summary>
    internal static MainWindow Open(string folder, DotnetWords? devCertificates = null, bool agentsAllowed = true, bool languageServer = false)
    {
        MainWindow window = new()
        {
            Width = 1200, Height = 800, DevCertificates = devCertificates, AgentsAllowed = agentsAllowed, StartsLanguageServer = languageServer,
        };
        window.Show();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>Each service's container's state, by service, failing the test when docker cannot say.</summary>
    internal static Dictionary<string, string> States(string project) =>
        StatesOf(Docker.Run("compose", "-p", project, "ps", "--all", "--format", "{{.Service}} {{.State}}"));

    /// <summary>Each service's container's state as a wait looks at them: null while docker cannot say.</summary>
    internal static Dictionary<string, string>? StatesNow(string project) =>
        Docker.Said("compose", "-p", project, "ps", "--all", "--format", "{{.Service}} {{.State}}") is { } said ? StatesOf(said) : null;

    /// <summary>Compose's lines of services and states; a service with two containers for a moment (one made anew beside the
    /// one it replaces) has both states, joined, which no single state equals.</summary>
    private static Dictionary<string, string> StatesOf(string said) =>
        said.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(' ')).Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join('+', g.Select(p => p[1])), StringComparer.Ordinal);

    internal static string Container(string project, string service) =>
        Docker.Run("ps", "-q", "--filter", $"label=com.docker.compose.project={project}", "--filter", $"label=com.docker.compose.service={service}");

    /// <summary>When a service's container last started; empty while it has none, stopped or not (a restart between its
    /// stop and its start, a container made anew), which a wait reads as not yet.</summary>
    internal static string Started(string project, string service) =>
        Docker.Ask(["ps", "-aq", "--filter", $"label=com.docker.compose.project={project}", "--filter", $"label=com.docker.compose.service={service}"]).Trim() is { Length: > 0 } container
            && !container.Contains('\n', StringComparison.Ordinal)
            ? Docker.Ask(["inspect", "-f", "{{.State.StartedAt}}", container]).Trim()
            : string.Empty;

    /// <summary>The host's port a service's port inside (8080 unless given) is published on.</summary>
    internal static int Published(string project, string service, int inside = 8080)
    {
        string said = Docker.Run("compose", "-p", project, "port", service, inside.ToString(CultureInfo.InvariantCulture)).Split('\n')[0].Trim();
        return int.Parse(said[(said.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);
    }

    /// <summary>The page a port serves, waited for until it answers.</summary>
    private static string Page(int port)
    {
        string? answer = null;
        Assert.True(Ui.Pumped(() => (answer = Answer(port)) is not null, TimeSpan.FromMinutes(1)), $"Nothing answered on port {port}");
        return answer!;
    }

    /// <summary>The page a service serves on the port its 8080 is published on, or the test failed with what its container
    /// and the Run window say of why it serves none.</summary>
    private static string Served(MainWindow window, string project, string service)
    {
        string said = Docker.Ask(["compose", "-p", project, "port", service, "8080"]).Split('\n')[0].Trim();
        string? answer = null;
        // Docker's CLI says 0.0.0.0:port; on Podman's socket ":port", the address left out.
        if (said.LastIndexOf(':') is int colon and >= 0 && int.TryParse(said[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && Ui.Pumped(() => (answer = Answer(port)) is not null, TimeSpan.FromMinutes(1)))
        {
            return answer!;
        }
        string container = Docker.Ask(["ps", "-aq", "--filter", $"label=com.docker.compose.project={project}", "--filter", $"label=com.docker.compose.service={service}"]).Trim();
        Assert.Fail($"{service} gave no answer ({said}); its container: {Docker.Ask(["inspect", "-f", "{{.State.Status}} {{.State.ExitCode}} {{.Config.User}}", container])}"
            + $"\nits log's end:\n{Docker.Ask(["logs", "--tail", "40", container])}\nthe Run window:\n{Run(window)}");
        return said;
    }

    internal static string? Answer(int port)
    {
        try
        {
            return Task.Run(() => Http.GetStringAsync(new Uri($"http://localhost:{port}/"))).GetAwaiter().GetResult();
        }
        catch (Exception failed) when (failed is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Runs the chosen configuration, failing with what its services said when it does not come up.</summary>
    private static void Launched(MainWindow window, string what)
    {
        string? refused = Ui.Await(window.RunAsync(), what, TimeSpan.FromMinutes(5));
        Assert.True(refused is null, $"{refused}{Ui.BuildErrors(window)}\n{Run(window)}");
    }

    internal static string Run(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }
}
