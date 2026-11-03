using System.Globalization;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>Publish beside a folder's: a project's container image made by the SDK into the test's engine through a container
/// profile, on a base image a stand-in registry serves (each image labelled for the run and removed after); a push to a
/// registry docker holds no login for refused, saying how to log in; and an AppHost's Docker Compose files, written by its
/// publishers, landed in the folder chosen as one step of Undo, which <c>docker compose config</c> reads. Alone: the
/// refusal points the process's DOCKER_CONFIG at a folder of the test's while it asks.</summary>
[Collection(Alone.Name)]
public sealed class ContainerPublishTests : IClassFixture<StandInAppHost>, IDisposable
{
    private readonly StandInAppHost appHost;
    private readonly string folder = TestFolders.Create("container-publish-");

    public ContainerPublishTests(StandInAppHost appHost) => this.appHost = appHost;

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_projects_container_image_is_published_into_the_engine_through_a_container_profile()
    {
        string? noEngine = Docker.WhyNot;
        Assert.SkipWhen(noEngine is not null, noEngine ?? string.Empty);
        using StandInRegistry registry = new();
        string project = WriteApp(registry.BaseImage);
        string image = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}publish";
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            PublishDialog dialog = DialogButtonsTests.Check(Show(window, project));
            dialog.Container = true;
            Assert.Equal("ContainerProfile", dialog.ProfileName);
            dialog.ImageName = image;
            dialog.ImageTag = "t1";
            Ui.Settle();
            Assert.Equal($"dotnet publish {Path.Combine("Shipped", "Shipped.csproj")} -c Release -p:PublishProfile=ContainerProfile -t:PublishContainer", dialog.Command);
            Assert.Equal($"Makes {image}:t1 in the engine on this machine", dialog.ImageSaid);

            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            Assert.True(ended.Succeeded, $"The image was not published: {ended.Message}\n{string.Join('\n', ended.Output)}");
            Assert.Equal($"{image}:t1", ended.Image);
            Assert.Equal($"Published Shipped (ContainerProfile) as the image {image}:t1, into the engine", window.Status);
            string? labelled = Docker.Said("image", "inspect", "--format", $"{{{{index .Config.Labels \"{Docker.RunLabel.Split('=', 2)[0]}\"}}}}", $"{image}:t1");
            Assert.True(labelled == Docker.RunLabel.Split('=', 2)[1], $"The engine holds no {image}:t1 labelled for the run ({labelled}); the publish said:\n{string.Join('\n', ended.Output)}\nThe registry had {registry.Connections} connections and was asked: {string.Join(", ", registry.Requests)}");
            string profile = File.ReadAllText(Path.Combine(folder, "Shipped", "Properties", "PublishProfiles", "ContainerProfile.pubxml"));
            Assert.True(profile.Contains("<WebPublishMethod>Container</WebPublishMethod>", StringComparison.Ordinal)
                && profile.Contains($"<ContainerRepository>{image}</ContainerRepository>", StringComparison.Ordinal)
                && profile.Contains("<ContainerImageTag>t1</ContainerImageTag>", StringComparison.Ordinal) && !profile.Contains("ContainerRegistry", StringComparison.Ordinal),
                $"The container profile says:\n{profile}");
            Assert.Contains(registry.Requests, request => request.EndsWith("/manifests/1", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
            RemoveImages();
        }
    }

    /// <summary>A container profile of two platforms and two tags, as the SDK reads them (ContainerRuntimeIdentifiers,
    /// ContainerImageTags), makes one image of both platforms under each tag in the engine, on a base the stand-in
    /// registry serves for both; an engine without containerd's image store holds no such image, and the test skips.</summary>
    [AvaloniaFact]
    public void A_two_platform_image_with_two_tags_is_published_into_the_engine_through_its_profile()
    {
        string? noEngine = Docker.WhyNot;
        Assert.SkipWhen(noEngine is not null, noEngine ?? string.Empty);
        Assert.SkipWhen(Docker.Said("info", "--format", "{{json .DriverStatus}}")?.Contains("io.containerd.snapshotter", StringComparison.Ordinal) != true,
            "The engine keeps its images without containerd's image store, which alone holds an image of more than one platform");
        using StandInRegistry registry = new("amd64", "arm64");
        // No app host, which a runtime's publish would take from a package of each platform's: framework-dependent, it needs none.
        string project = WriteApp(registry.BaseImage, "<UseAppHost>false</UseAppHost>");
        string image = $"ptest{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}platforms";
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            PublishDialog dialog = Show(window, project);
            dialog.Container = true;
            dialog.ImageName = image;
            dialog.ImageTag = "t1; t2";
            dialog.Runtimes = "linux-x64;linux-arm64";
            Ui.Settle();
            Assert.Equal($"Makes {image}:t1 and {image}:t2 for linux-x64 and linux-arm64 in the engine on this machine", dialog.ImageSaid);

            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            Assert.True(ended.Succeeded, $"The image was not published: {ended.Message}\n{string.Join('\n', ended.Output)}");
            Assert.Equal($"{image}:t1, {image}:t2", ended.Image);
            string profile = File.ReadAllText(Path.Combine(folder, "Shipped", "Properties", "PublishProfiles", "ContainerProfile.pubxml"));
            Assert.True(profile.Contains("<ContainerImageTags>t1;t2</ContainerImageTags>", StringComparison.Ordinal)
                && profile.Contains("<ContainerRuntimeIdentifiers>linux-x64;linux-arm64</ContainerRuntimeIdentifiers>", StringComparison.Ordinal)
                && !profile.Contains("<ContainerImageTag>", StringComparison.Ordinal), $"The container profile says:\n{profile}");
            (string key, string value) = (Docker.RunLabel.Split('=', 2)[0], Docker.RunLabel.Split('=', 2)[1]);
            foreach (string tag in (string[])["t1", "t2"])
            {
                foreach (string platform in (string[])["linux/amd64", "linux/arm64"])
                {
                    string? labelled = Docker.Said("image", "inspect", "--platform", platform, "--format", $"{{{{index .Config.Labels \"{key}\"}}}}", $"{image}:{tag}");
                    Assert.True(labelled == value, $"The engine holds no {image}:{tag} for {platform} labelled for the run ({labelled}); the publish said:\n{string.Join('\n', ended.Output)}");
                }
            }
        }
        finally
        {
            window.Close();
            RemoveImages();
        }
    }

    [AvaloniaFact]
    public void A_push_to_a_registry_with_no_docker_login_is_refused_saying_how_to_log_in()
    {
        string project = WriteApp("localhost:1/standin/base:1");
        TestFolders.Write(folder, "Shipped/Properties/PublishProfiles/Pushed.pubxml", "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project>\n  <PropertyGroup>\n"
            + "    <WebPublishMethod>Container</WebPublishMethod>\n    <ContainerRegistry>registry.passenger-test.invalid</ContainerRegistry>\n  </PropertyGroup>\n</Project>\n");
        string config = TestFolders.Write(folder, "docker-config/config.json", "{\n  \"auths\": {\n    \"https://index.docker.io/v1/\": {}\n  }\n}\n");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        string? had = Environment.GetEnvironmentVariable(EngineVariables.DockerConfig);
        try
        {
            PublishDialog dialog = Show(window, project);
            dialog.ChooseProfile("Pushed");
            Ui.WaitFor(() => !dialog.IsReading, "the Publish dialog to read the profile that pushes", TimeSpan.FromMinutes(1));
            Assert.Equal((true, "registry.passenger-test.invalid"), (dialog.Container, dialog.Registry));
            Assert.Contains("with docker's login for registry.passenger-test.invalid", dialog.ImageSaid, StringComparison.Ordinal);
            // Docker's configuration the test's own, holding a login for another registry, while the publish asks it.
            OwnEnvironment.Set(EngineVariables.DockerConfig, Path.GetDirectoryName(config));
            BuildOutcome ended = Publish(window, dialog.PublishAsync);
            OwnEnvironment.Set(EngineVariables.DockerConfig, had);
            Assert.Equal(BuildStatus.Refused, ended.Status);
            Assert.Equal($"Shipped (Pushed) was not pushed: No docker login for registry.passenger-test.invalid was found in {PathNames.Shown(config, null)}: "
                + "run docker login registry.passenger-test.invalid in a terminal, then publish again", ended.Message);
            Assert.False(window.IsBuilding, "The refused push started a build");
        }
        finally
        {
            OwnEnvironment.Set(EngineVariables.DockerConfig, had);
            window.Close();
        }
    }

    [AvaloniaFact]
    public void An_AppHost_publishes_Docker_Compose_files_that_docker_compose_config_reads_as_one_step_of_Undo()
    {
        string? noEngine = Docker.WhyNot;
        Assert.SkipWhen(noEngine is not null, noEngine ?? string.Empty);
        appHost.Reset();
        MainWindow window = appHost.Open(appHost.Program, languageServer: false);
        try
        {
            string host = PathNames.Canonical(appHost.ProjectFile);
            AppHostPublishDialog dialog = DialogButtonsTests.Check(window.ShowAppHostPublish(host) ?? throw new InvalidOperationException($"Publish did not open: {window.Status}"));
            Assert.Equal("aspire-output", dialog.OutputFolder);
            dialog.OutputFolder = "deploy";
            Ui.Settle();
            string deploy = Path.Combine(Path.GetDirectoryName(host)!, "deploy");
            Assert.Equal($"dotnet run --project {Path.Combine(StandInAppHost.Name, $"{StandInAppHost.Name}.csproj")} --no-launch-profile -- --operation publish --step publish "
                + $"--output-path {Path.Combine(StandInAppHost.Name, "deploy")}", dialog.Command);

            BuildOutcome ended = Publish(window, () => Task.FromResult(dialog.Publish()));
            Assert.True(ended.Succeeded, $"The AppHost was not published: {ended.Message}\n{string.Join('\n', ended.Output)}");
            string compose = Path.Combine(deploy, "docker-compose.yaml");
            Assert.True(ended.Published is { } at && PathNames.Same(at, deploy), $"The publish says it landed in {ended.Published}, not in {deploy}");
            Assert.Equal($"Published {StandInAppHost.Name} to {Path.Combine(StandInAppHost.Name, "deploy")}: docker-compose.yaml", window.Status);
            Assert.Contains("standin-api:latest", Docker.Run("compose", "-f", compose, "config"), StringComparison.Ordinal);
            Assert.Equal($"Publish {StandInAppHost.Name}", window.EditorArea.Commands.ToUndo(null)?.Name);

            // Published again, the folder is the one it published to.
            AppHostPublishDialog again = window.ShowAppHostPublish(host)!;
            Assert.Equal("deploy", again.OutputFolder);
            again.Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A console app in the test's folder whose image builds on the base given and carries the run's label.</summary>
    private string WriteApp(string baseImage, params string[] properties)
    {
        (string key, string value) = (Docker.RunLabel.Split('=', 2)[0], Docker.RunLabel.Split('=', 2)[1]);
        TestFolders.Write(folder, "Shipped/Shipped.csproj", TestProjects.Console([$"<ContainerBaseImage>{baseImage}</ContainerBaseImage>", .. properties])
            .With($"  <ItemGroup>\n    <ContainerLabel Include=\"{key}\" Value=\"{value}\" />\n  </ItemGroup>\n"));
        TestFolders.Write(folder, "Shipped/Program.cs", "System.Console.WriteLine(\"shipped\");\n");
        return PathNames.Canonical(Path.Combine(folder, "Shipped", "Shipped.csproj"));
    }

    private static PublishDialog Show(MainWindow window, string project)
    {
        PublishDialog dialog = window.ShowPublish(project) ?? throw new InvalidOperationException($"Publish did not open: {window.Status}");
        Ui.WaitFor(() => !dialog.IsReading, "the Publish dialog to read how the project publishes", TimeSpan.FromMinutes(1));
        Assert.True(dialog.Problem.Length == 0, $"The Publish dialog says: {dialog.Problem}");
        return dialog;
    }

    /// <summary>A publish the window starts waited for to its end.</summary>
    private static BuildOutcome Publish(MainWindow window, Func<Task<string?>> start)
    {
        Task<string?> asked = start();
        Ui.WaitFor(() => window.LastPublish is { IsCompleted: true } || (asked.IsCompleted && asked.Result is not null), "the publish to end", TimeSpan.FromMinutes(4));
        Assert.True(window.LastPublish is not null, $"The publish did not start: {(asked.IsCompleted ? asked.Result : "still asking")}; the status bar says {window.Status}");
        return Ui.Await(window.LastPublish, "the publish's outcome", TimeSpan.FromMinutes(4));
    }

    /// <summary>The images this run's publishes made, by its label, removed.</summary>
    internal static void RemoveImages()
    {
        if (Docker.Said("images", "-q", "--filter", $"label={Docker.RunLabel}") is { Length: > 0 } ids)
        {
            Docker.Said(["rmi", "-f", .. ids.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()]);
        }
    }
}
