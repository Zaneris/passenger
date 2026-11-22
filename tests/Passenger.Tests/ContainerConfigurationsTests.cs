using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A project's container run configurations come from its properties as MSBuild evaluates them, as a build would
/// take them: from a Directory.Build.props, never under a condition that is false nor from a comment, and never from the
/// SDK's own default alone; and a folder whose projects were evaluated by a build before they carried those properties
/// has them evaluated again rather than showing none. No engine is needed: the configurations are made as the folder opens.</summary>
public sealed class ContainerConfigurationsTests
{
    private const string Props = $$"""
        <Project>
          <PropertyGroup>
            <TargetFramework>{{TestProjects.Framework}}</TargetFramework>
            <OutputType>Exe</OutputType>
          </PropertyGroup>
        {0}</Project>

        """;

    /// <summary>Container run configurations as one flow, each step one test's check in an instance of its own: an SDK
    /// container asked for in Directory.Build.props, a DockerfileFile set there, a Dockerfile configuration changed in
    /// place kept over what its Dockerfile makes, a Docker launch profile's configuration writing its run words back,
    /// and a Docker Compose project's properties and launch profiles as its configurations, never built as a
    /// project.</summary>
    [AvaloniaFact]
    public void A_projects_container_configurations_come_from_its_properties_profiles_and_Dockerfile_and_keep_what_the_person_changed()
    {
        Flow.StepApart<ContainerConfigurationsTests>("a project asking for an SDK container in its Directory Build props has its configuration and one asking under a false condition or in a comment has none", t => t.A_project_asking_for_an_SDK_container_in_its_Directory_Build_props_has_its_configuration_and_one_asking_under_a_false_condition_or_in_a_comment_has_none());
        Flow.StepApart<ContainerConfigurationsTests>("a DockerfileFile set in Directory Build props is the Dockerfile configurations and a folder cached before it was carried is evaluated again", t => t.A_DockerfileFile_set_in_Directory_Build_props_is_the_Dockerfile_configurations_and_a_folder_cached_before_it_was_carried_is_evaluated_again());
        Flow.StepApart<ContainerConfigurationsTests>("a Dockerfile configuration changed in place is kept with the folder over what its Dockerfile makes", t => t.A_Dockerfile_configuration_changed_in_place_is_kept_with_the_folder_over_what_its_Dockerfile_makes());
        Flow.StepApart<ContainerConfigurationsTests>("a Docker launch profiles configuration writes its own run words and variables back to the profile", t => t.A_Docker_launch_profiles_configuration_writes_its_own_run_words_and_variables_back_to_the_profile());
        Flow.StepApart<ContainerConfigurationsTests>("a Docker Compose projects properties and launch profiles are its configurations and it is never built as a project", t => t.A_Docker_Compose_projects_properties_and_launch_profiles_are_its_configurations_and_it_is_never_built_as_a_project());
    }

    private void A_project_asking_for_an_SDK_container_in_its_Directory_Build_props_has_its_configuration_and_one_asking_under_a_false_condition_or_in_a_comment_has_none()
    {
        string folder = Solution("ctr-sdkask-", ["Tool", "Gated", "Quiet", "Plain"], """
              <PropertyGroup Condition="'$(MSBuildProjectName)' == 'Tool'">
                <ContainerRepository>shop-tool</ContainerRepository>
              </PropertyGroup>

            """);
        TestFolders.Write(folder, "Gated/Gated.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup Condition="'$(PublishToRegistry)' == 'true'">
                <EnableSdkContainerSupport>true</EnableSdkContainerSupport>
                <ContainerRepository>shop-gated</ContainerRepository>
              </PropertyGroup>
            </Project>

            """);
        TestFolders.Write(folder, "Quiet/Quiet.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- Not yet:
              <PropertyGroup>
                <EnableSdkContainerSupport>true</EnableSdkContainerSupport>
                <ContainerRepository>shop-quiet</ContainerRepository>
              </PropertyGroup>
              -->
            </Project>

            """);
        MainWindow window = Open(folder, settings: null);
        string[] names = [.. window.Configurations.Select(c => c.Name)];
        // Each project evaluated as runnable, so a missing configuration is the rule's answer, not an evaluation that failed.
        Assert.All((string[])["Tool", "Gated", "Quiet", "Plain"], project => Assert.Contains(project, names));
        RunConfiguration tool = Assert.Single(window.Configurations, c => c.Name == "Tool: SDK container");
        Assert.Equal(RunKind.SdkContainer, tool.Kind);
        Assert.True(!names.Contains("Gated: SDK container"), "A project whose ContainerRepository is set only under a condition that is false has an SDK container configuration");
        Assert.True(!names.Contains("Quiet: SDK container"), "A project whose container properties are in a comment has an SDK container configuration");
        Assert.True(!names.Contains("Plain: SDK container"),
            "A console project that asks for no container has an SDK container configuration, from the SDK turning EnableSdkContainerSupport on for every executable");
        window.Close();
    }

    private void A_DockerfileFile_set_in_Directory_Build_props_is_the_Dockerfile_configurations_and_a_folder_cached_before_it_was_carried_is_evaluated_again()
    {
        string folder = Solution("ctr-dockerfile-", ["App", "Side"], """
              <PropertyGroup Condition="'$(MSBuildProjectName)' == 'App'">
                <DockerfileFile>../docker/App.Dockerfile</DockerfileFile>
              </PropertyGroup>

            """);
        TestFolders.Write(folder, "docker/App.Dockerfile", "FROM mcr.microsoft.com/dotnet/runtime:10.0\n");
        TestFolders.Write(folder, "Side/Dockerfile", "FROM mcr.microsoft.com/dotnet/runtime:10.0\n");
        TestFolders.Write(folder, "Side/Side.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <!-- <PropertyGroup><DockerfileFile>../docker/App.Dockerfile</DockerfileFile></PropertyGroup> -->
            </Project>

            """);
        string settings = TestFolders.Create("settings-");
        string named = PathNames.Canonical(Path.Combine(folder, "docker", "App.Dockerfile"));
        MainWindow first = Open(folder, settings, languageServer: true);
        Assert.Equal(named, Dockerfile(first, "App: Dockerfile"));
        Assert.Equal(PathNames.Canonical(Path.Combine(folder, "Side", "Dockerfile")), Dockerfile(first, "Side: Dockerfile"));
        // The language server's restore of each project, never restored, is among what its evaluation reads: kept once it lands.
        foreach (string project in (string[])["App", "Side"])
        {
            string props = Path.Combine(folder, project, "obj", $"{project}.csproj.nuget.g.props");
            Ui.WaitFor(() => first.Session.Workspace?.Projects.FirstOrDefault(p => p.Name == project)?.Evaluated?.Inputs.Any(i => PathNames.Same(i.Path, props) && i.Length >= 0) == true,
                $"{project} evaluated again with the props its restore wrote", TimeSpan.FromMinutes(2));
        }
        first.Close();

        // The folder's evaluations as the last build before the properties were carried kept them.
        string cache = Directory.EnumerateFiles(settings, "projectModels.json", SearchOption.AllDirectories).Single();
        JsonObject kept = (JsonObject)JsonNode.Parse(File.ReadAllText(cache))!;
        kept["version"] = 3;
        foreach ((string _, JsonNode? project) in (JsonObject)kept["projects"]!)
        {
            ((JsonObject)project!).Remove("properties");
        }
        File.WriteAllText(cache, kept.ToJsonString());

        MainWindow second = Open(folder, settings);
        Assert.True(second.Session.Models.Evaluations > 0,
            "A folder whose cached evaluations carry no properties was shown from the cache, its projects' container configurations missing until they change");
        Assert.Equal(named, Dockerfile(second, "App: Dockerfile"));
        second.Close();
    }

    /// <summary>A Dockerfile configuration's run arguments and mode changed in place are kept with the folder over what its
    /// Dockerfile makes: marked as changed, Reset to Dockerfile offered and no Write to Launch Profile, which it has none of,
    /// and there as changed when the folder opens again, its source as the Dockerfile makes it.</summary>
    private void A_Dockerfile_configuration_changed_in_place_is_kept_with_the_folder_over_what_its_Dockerfile_makes()
    {
        string folder = Solution("ctr-inplace-", ["App"], string.Empty);
        TestFolders.Write(folder, "App/Dockerfile", "FROM mcr.microsoft.com/dotnet/runtime:10.0\n");
        string settings = TestFolders.Create("settings-");
        MainWindow first = Open(folder, settings);
        RunConfigurationsDialog dialog = first.ShowRunConfigurations();
        Assert.True(dialog.Choose("App: Dockerfile"), $"No App: Dockerfile among [{string.Join(", ", dialog.Rows.Select(r => r.Name))}]");
        Assert.True(dialog.RunArgumentsBox.IsEnabled && dialog.ModeBox.IsEnabled, "A generated container configuration's run arguments or mode cannot be changed in place");
        dialog.RunArgumentsBox.Text = "-P -e MODE=dev";
        dialog.ModeBox.SelectedIndex = 1;
        Ui.Settle();
        Assert.Equal(["Run arguments", "Mode"], dialog.Marked);
        Assert.True(dialog.ResetButton.IsEffectivelyVisible && (string?)dialog.ResetButton.Content == "Reset to Dockerfile",
            $"A changed Dockerfile configuration offers {(dialog.ResetButton.IsEffectivelyVisible ? dialog.ResetButton.Content : "no reset")}, not Reset to Dockerfile");
        Assert.False(dialog.WriteButton.IsEffectivelyVisible, "A configuration made from a Dockerfile offers to write to a launch profile it does not have");
        dialog.Ok();
        Ui.Settle();
        first.Close();

        MainWindow second = Open(folder, settings);
        RunConfiguration kept = second.Configurations.Single(c => c.Name == "App: Dockerfile");
        Assert.Equal(("-P -e MODE=dev", ContainerMode.Images, true), (kept.RunArguments, kept.Mode, kept.Generated));
        Assert.Equal(("-P", ContainerMode.Fast), (kept.Source?.RunArguments, kept.Source?.Mode));
        second.Close();
    }

    /// <summary>A Docker launch profile's configuration writes back what the profile holds: the <c>docker run</c> words added
    /// after those its ports make, as its DockerfileRunArguments, a variable added and one taken out with its line; the
    /// configuration is then as the profile makes it, nothing kept here.</summary>
    private void A_Docker_launch_profiles_configuration_writes_its_own_run_words_and_variables_back_to_the_profile()
    {
        string folder = Solution("ctr-writeback-", ["App"], string.Empty);
        const string Settings = """
            {
              "profiles": {
                "Container (Dockerfile)": {
                  "commandName": "Docker",
                  "httpPort": 5080,
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "OLD": "x"
                  }
                }
              }
            }

            """;
        const string Written = """
            {
              "profiles": {
                "Container (Dockerfile)": {
                  "commandName": "Docker",
                  "httpPort": 5080,
                  "environmentVariables": {
                    "ASPNETCORE_ENVIRONMENT": "Development",
                    "LOG": "1"
                  },
                  "DockerfileRunArguments": "--memory 512m"
                }
              }
            }

            """;
        TestFolders.Write(folder, "App/Properties/launchSettings.json", Settings);
        MainWindow window = Open(folder, settings: null);
        try
        {
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Assert.True(dialog.Choose("App: Container (Dockerfile)"), $"No App: Container (Dockerfile) among [{string.Join(", ", dialog.Rows.Select(r => r.Name))}]");
            Assert.Equal("-p 5080:8080", dialog.RunArgumentsBox.Text);
            dialog.RunArgumentsBox.Text = "-p 5080:8080 --memory 512m";
            Ui.Settle();
            Ui.Click(dialog.AddVariableButton);
            dialog.Variables[^1].Name.Text = "LOG";
            dialog.Variables[^1].Value.Text = "1";
            Ui.Settle();
            Avalonia.Controls.Grid old = (Avalonia.Controls.Grid)dialog.Variables.Single(v => v.Name.Text == "OLD").Name.Parent!;
            Ui.Click(old.Children.OfType<Avalonia.Controls.Button>().Single());
            Assert.Equal(["Run arguments", "Environment variables"], dialog.Marked);
            Ui.Click(dialog.WriteButton);
            Assert.Equal(Written, File.ReadAllText(Path.Combine(folder, "App", "Properties", "launchSettings.json")));
            Assert.Equal("Written to its launch profile in launchSettings.json: Run arguments, Environment variables.", dialog.Changes);
            Assert.Empty(dialog.Marked);
            dialog.Ok();
            Ui.Settle();
            RunConfiguration written = window.Configurations.Single(c => c.Name == "App: Container (Dockerfile)");
            Assert.Equal(("-p 5080:8080 --memory 512m", "1", null), (written.RunArguments, written.Environment.GetValueOrDefault("LOG"), written.Source));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A Docker Compose project's (.dcproj) properties and launch profiles are its configurations, read with no engine
    /// needed, and it is never loaded or built as a .NET project.</summary>
    private void A_Docker_Compose_projects_properties_and_launch_profiles_are_its_configurations_and_it_is_never_built_as_a_project()
    {
        (string folder, _) = ContainerRunsTests.Shop("dcproj");
        File.Move(Path.Combine(folder, "compose.yaml"), Path.Combine(folder, "docker-compose.yml"));
        TestFolders.Write(folder, "docker-compose.override.yml", "services:\n  web:\n    environment: { GREETING: hi }\n");
        TestFolders.Write(folder, "docker-compose.dcproj", """
            <?xml version="1.0" encoding="utf-8"?>
            <Project ToolsVersion="15.0" Sdk="Microsoft.Docker.Sdk">
              <PropertyGroup Label="Globals">
                <ProjectVersion>2.1</ProjectVersion>
                <DockerTargetOS>Linux</DockerTargetOS>
                <DockerLaunchAction>LaunchBrowser</DockerLaunchAction>
                <DockerServiceUrl>{Scheme}://localhost:{ServicePort}/</DockerServiceUrl>
                <DockerServiceName>web</DockerServiceName>
              </PropertyGroup>
              <ItemGroup>
                <None Include="docker-compose.override.yml"><DependentUpon>docker-compose.yml</DependentUpon></None>
                <None Include="docker-compose.yml" />
              </ItemGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Properties/launchSettings.json", """
            {
              "profiles": {
                "Web only": {
                  "commandName": "DockerCompose",
                  "composeLaunchServiceName": "web",
                  "serviceActions": { "web": "StartDebugging", "api": "DoNotStart", "cache": "StartWithoutDebugging" }
                }
              }
            }
            """);
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"Web/Web.csproj\" />\n  <Project Path=\"Api/Api.csproj\" />\n  <Project Path=\"docker-compose.dcproj\" />\n</Solution>\n");
        MainWindow window = ContainerRunsTests.Open(folder);
        // Never loaded as a project: Visual Studio's SDK is not .NET's, and building the solution would fail on it.
        Assert.DoesNotContain(window.Projects, p => p.Path.EndsWith(".dcproj", StringComparison.Ordinal));
        RunConfiguration own = window.Configurations.Single(c => c.Name == "docker-compose");
        Assert.Equal(RunKind.Compose, own.Kind);
        Assert.Equal([Path.Combine(folder, "docker-compose.yml"), Path.Combine(folder, "docker-compose.override.yml")], own.ComposeFiles);
        Assert.Equal("web", own.LaunchService);
        Assert.True(own.LaunchBrowser, "The .dcproj's LaunchBrowser action was not taken");
        Assert.Equal("{Scheme}://localhost:{ServicePort}/", own.BrowserUrl);
        RunConfiguration profile = window.Configurations.Single(c => c.Name == "docker-compose: Web only");
        Assert.Equal(ServiceStart.Debug, profile.ServiceActions["web"]);
        Assert.Equal(ServiceStart.DontStart, profile.ServiceActions["api"]);
        Assert.Equal(ServiceStart.Run, profile.ServiceActions["cache"]);
        Assert.DoesNotContain(window.Configurations, c => c.Name.StartsWith("Compose:", StringComparison.Ordinal));
        window.Close();
    }

    /// <summary>A folder with a solution of console projects, each named in it, their framework and output type from a
    /// Directory.Build.props that also holds <paramref name="more"/>.</summary>
    private static string Solution(string prefix, string[] projects, string more)
    {
        string folder = PathNames.Canonical(TestFolders.Create(prefix));
        TestFolders.Write(folder, "Shop.slnx", $"<Solution>\n{string.Concat(projects.Select(p => $"  <Project Path=\"{p}/{p}.csproj\" />\n"))}</Solution>\n");
        TestFolders.Write(folder, "Directory.Build.props", string.Format(System.Globalization.CultureInfo.InvariantCulture, Props, more));
        foreach (string project in projects)
        {
            TestFolders.Write(folder, $"{project}/{project}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
            TestFolders.Write(folder, $"{project}/Program.cs", $"System.Console.WriteLine(\"{project}\");\n");
        }
        return folder;
    }

    /// <summary>A window on the solution once every project is evaluated, with no C# language server, which the run
    /// configurations, the MSBuild host's evaluations, ask nothing of, unless the test reads what its restore writes
    /// (<paramref name="languageServer"/>).</summary>
    private static MainWindow Open(string folder, string? settings, bool languageServer = false)
    {
        MainWindow window = settings is null
            ? new() { Width = 1200, Height = 800, StartsLanguageServer = languageServer }
            : new() { Width = 1200, Height = 800, StateFolder = settings, StartsLanguageServer = languageServer };
        window.Show();
        if (settings is not null)
        {
            window.KeepLayout();
        }
        window.OpenFolder(Path.Combine(folder, "Shop.slnx"));
        Ui.WaitFor(() => window.Session.Models.IsComplete, "every project's evaluation");
        Ui.Settle();
        return window;
    }

    private static string Dockerfile(MainWindow window, string name) =>
        window.Configurations.FirstOrDefault(c => c.Name == name)?.Dockerfile
        ?? throw new InvalidOperationException($"No configuration {name}: [{string.Join(", ", window.Configurations.Select(c => c.Name))}]");
}
