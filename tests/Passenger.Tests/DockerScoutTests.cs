using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Containers;
using Passenger.App.Editors;
using Passenger.App.ToolWindows;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>Container images' known vulnerabilities through Docker Scout, a stand-in for it the docker CLI's plugin on a
/// DOCKER_CONFIG of the test's own, so nothing is pulled or scanned: a Dockerfile's base image asked of it only once the
/// person asks, then marked with the tag it recommends as the fix, one edit of Undo; an image's Details in the Containers
/// window saying what Scout sends and, once asked, its CVEs; and without a scout, why and how to get one. Alone: the
/// process's DOCKER_CONFIG is the test's while it runs.</summary>
[Collection(Alone.Name)]
public sealed class DockerScoutTests
{
    private const string Base = "mcr.microsoft.com/dotnet/aspnet:8.0";

    [AvaloniaFact]
    public void A_dockerfiles_base_image_is_asked_of_scout_only_once_asked_then_marked_with_the_tag_it_recommends_as_one_edit()
    {
        SkipWithoutDockerCli();
        string folder = TestFolders.Create("docker-scout-");
        string dockerfile = TestFolders.Write(folder, "Dockerfile", $"FROM {Base} AS base\nWORKDIR /app\n\nFROM base AS final\nCOPY . .\n");
        using StandInScout scout = new(folder, plugin: true);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(PathNames.Canonical(dockerfile));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(scout.Asked.Count == 0, $"Docker Scout was asked unasked: {string.Join("; ", scout.Asked)}");

            Assert.True(window.Perform(AppActions.ScoutBaseImages), "Check Base Images with Docker Scout did not run");
            string expected = $"Docker Scout: {Base} has 1 critical, 1 high, 1 medium; Scout recommends mcr.microsoft.com/dotnet/aspnet:8.0.11";
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == expected),
                () => $"the FROM marked as Scout found it, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]; Scout was asked {string.Join("; ", scout.Asked)}");
            Assert.Contains(scout.Asked, asked => asked == $"cves --format sarif {Base}");
            Assert.Equal(1, scout.Asked.Count(asked => asked.StartsWith("cves ", StringComparison.Ordinal)));
            Marker marked = document.Markers.Single(m => m.Message == expected);
            Assert.Equal(0, document.Text.Snapshot.GetPosition(marked.Range.Start).Line);
            Assert.Contains(window.BottomPanel.Problems.ContainerProblems, p => p.Message == expected);

            window.EditorArea.CodeEditor.Selections = SelectionSet.Single(document.Text.Snapshot.GetText(0, 30).IndexOf("aspnet", StringComparison.Ordinal));
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the FROM's fixes");
            Assert.Equal("Use mcr.microsoft.com/dotnet/aspnet:8.0.11", window.Assist.Offered[0]);
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.StartsWith("FROM mcr.microsoft.com/dotnet/aspnet:8.0.11 AS base\n", document.Text.Snapshot.ToString(), StringComparison.Ordinal);
            Assert.Contains("FROM base AS final", document.Text.Snapshot.ToString(), StringComparison.Ordinal);
            Ui.Run(window, EditorActions.Undo);
            Ui.Settle();
            Assert.StartsWith($"FROM {Base} AS base\n", document.Text.Snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void An_images_details_say_what_scout_sends_and_once_asked_its_vulnerabilities_by_severity()
    {
        SkipWithoutDockerCli();
        string? noEngine = Docker.WhyNot;
        Assert.SkipWhen(noEngine is not null, noEngine ?? string.Empty);
        string? image = Docker.Said("images", "--format", "{{.Repository}}:{{.Tag}}")?.Split('\n').FirstOrDefault(i => !i.Contains("<none>", StringComparison.Ordinal) && i.Length > 1);
        Assert.SkipWhen(image is null, "The engine holds no image with a name to show the Details of");
        string folder = TestFolders.Create("docker-scout-");
        using StandInScout scout = new(folder, plugin: true);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.ShowToolWindow(ToolWindowIds.Containers);
            ContainersView view = window.ContainersWindow;
            // The Images group opened, as a person opens it.
            Ui.WaitFor(() => view.Rows.Any(r => r.Key == "group:Images"), "the engine's images listed");
            view.ExpandAll();
            Ui.WaitFor(() => view.Choose(r => r.Kind == ContainerRowKind.Image && r.Label == image), () => $"{image}'s row among the engine's images: "
                + string.Join(", ", view.Rows.Where(r => r.Kind == ContainerRowKind.Image).Select(r => r.Label)));
            Ui.Settle();
            ImageDetailsPane details = view.ImageDetails;
            Assert.Equal(RunWindows.DetailsTab, view.ShownTab);
            Assert.Contains(DockerScout.Sends, Words(details));
            Assert.True(scout.Asked.Count == 0, $"Docker Scout was asked unasked: {string.Join("; ", scout.Asked)}");

            Ui.Click(details.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "imageScout"));
            Ui.WaitFor(() => details.Report is not null, "Docker Scout's report on the image");
            Ui.Settle();
            Assert.Null(details.Report!.Problem);
            Assert.Equal("1 critical, 1 high, 1 medium", details.Report.Counts);
            string[] said = Words(details);
            Assert.Contains("critical: openssl 3.0.11, fixed in 3.0.14", said);
            Assert.Contains("medium: libc6 2.36, no fix yet", said);
            Assert.Contains(scout.Asked, asked => asked == $"cves --format sarif {image}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>Two stages on one image are marked each on its own FROM, and the fix offered on the second changes the second,
    /// the first left as it was.</summary>
    [AvaloniaFact]
    public void The_fix_on_the_second_of_two_FROMs_naming_one_image_changes_that_FROM_alone()
    {
        SkipWithoutDockerCli();
        string folder = TestFolders.Create("docker-scout-");
        string dockerfile = TestFolders.Write(folder, "Dockerfile", $"FROM {Base} AS build\nRUN true\n\nFROM {Base} AS final\nCOPY . .\n");
        using StandInScout scout = new(folder, plugin: true);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(PathNames.Canonical(dockerfile));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(window.Perform(AppActions.ScoutBaseImages), "Check Base Images with Docker Scout did not run");
            string expected = $"Docker Scout: {Base} has 1 critical, 1 high, 1 medium; Scout recommends mcr.microsoft.com/dotnet/aspnet:8.0.11";
            Ui.WaitFor(() => document.Markers.Count(m => m.Message == expected) == 2,
                () => $"both FROMs marked, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]");

            window.EditorArea.CodeEditor.Selections = SelectionSet.Single(document.Text.Snapshot.ToString().LastIndexOf("aspnet", StringComparison.Ordinal));
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Count > 0, "the second FROM's fixes");
            Ui.Press(window, "ENTER");
            Ui.Settle();
            Assert.Equal($"FROM {Base} AS build\nRUN true\n\nFROM mcr.microsoft.com/dotnet/aspnet:8.0.11 AS final\nCOPY . .\n", document.Text.Snapshot.ToString());
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void Without_docker_scout_a_check_says_why_and_how_to_get_it()
    {
        SkipWithoutDockerCli();
        string folder = TestFolders.Create("docker-scout-");
        string dockerfile = TestFolders.Write(folder, "Dockerfile", $"FROM {Base}\n");
        using StandInScout scout = new(folder, plugin: false);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            window.OpenFile(PathNames.Canonical(dockerfile));
            Ui.Settle();
            OpenDocument document = window.EditorArea.Active!;
            Assert.True(window.Perform(AppActions.ScoutBaseImages), "Check Base Images with Docker Scout did not run");
            Ui.WaitFor(() => document.Markers.Any(m => m.Message == $"Docker Scout: {DockerScout.Missing}"),
                () => $"the FROM marked saying there is no scout, not [{string.Join(", ", document.Markers.Select(m => m.Message))}]");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    private static string[] Words(Avalonia.Visual pane) =>
        [.. pane.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>().Select(t => t.Text ?? string.Empty)];

    private static void SkipWithoutDockerCli()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in Docker Scout is a shell script, which Windows' docker does not run as a CLI plugin");
        Assert.SkipWhen(Programs.OnPath("docker") is null, "No docker CLI to run Docker Scout through");
    }

    /// <summary>A DOCKER_CONFIG of the test's own for the process while it stands: the person's context and other plugins kept
    /// (its config's current context, its contexts and plugins read through links), and with <paramref name="plugin"/> a stand-in docker-scout
    /// plugin that answers with a report of three CVEs and a recommended tag, writing each command it is asked.</summary>
    private sealed class StandInScout : IDisposable
    {
        private readonly string? had = Environment.GetEnvironmentVariable(EngineVariables.DockerConfig);
        private readonly string config;

        public StandInScout(string folder, bool plugin)
        {
            config = Directory.CreateDirectory(Path.Combine(folder, ".docker-config")).FullName;
            string person = had is { Length: > 0 } named ? named : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
            string? context = null;
            try
            {
                using JsonDocument read = JsonDocument.Parse(File.ReadAllText(Path.Combine(person, "config.json")));
                context = read.RootElement.TryGetProperty("currentContext", out JsonElement current) ? current.GetString() : null;
            }
            catch (Exception missing) when (missing is IOException or JsonException or UnauthorizedAccessException)
            {
                // No config of the person's: the default context.
            }
            File.WriteAllText(Path.Combine(config, "config.json"), context is null ? "{}\n" : $"{{\"currentContext\": \"{context}\"}}\n");
            if (Directory.Exists(Path.Combine(person, "contexts")))
            {
                Directory.CreateSymbolicLink(Path.Combine(config, "contexts"), Path.Combine(person, "contexts"));
            }
            // The person's other plugins kept (Compose's among them), through links; their scout never.
            string plugins = Directory.CreateDirectory(Path.Combine(config, "cli-plugins")).FullName;
            if (Directory.Exists(Path.Combine(person, "cli-plugins")))
            {
                foreach (string other in Directory.EnumerateFileSystemEntries(Path.Combine(person, "cli-plugins")).Where(o => !Path.GetFileName(o).StartsWith("docker-scout", StringComparison.Ordinal)))
                {
                    File.CreateSymbolicLink(Path.Combine(plugins, Path.GetFileName(other)), other);
                }
            }
            if (plugin)
            {
                File.WriteAllText(Path.Combine(config, "scout-cves.sarif"), Sarif);
                File.WriteAllText(Path.Combine(config, "scout-recommendations.txt"), Recommendations);
                string script = Path.Combine(plugins, "docker-scout");
                File.WriteAllText(script, $$"""
                    #!/bin/sh
                    here="{{config}}"
                    if [ "$1" = "docker-cli-plugin-metadata" ]; then
                      echo '{"SchemaVersion":"0.1.0","Vendor":"Passenger tests","Version":"1.0.0","ShortDescription":"A stand-in Docker Scout"}'
                      exit 0
                    fi
                    shift
                    echo "$*" >> "$here/scout-asked.log"
                    case "$1" in
                      cves) cat "$here/scout-cves.sarif" ;;
                      recommendations) cat "$here/scout-recommendations.txt" ;;
                      version) echo "version: v1.0.0 (stand-in)" ;;
                      *) echo "The stand-in Docker Scout knows no $1" >&2; exit 1 ;;
                    esac

                    """);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }
            OwnEnvironment.Set(EngineVariables.DockerConfig, config);
        }

        /// <summary>Each command the stand-in was asked, its words after scout.</summary>
        public IReadOnlyList<string> Asked => File.Exists(Path.Combine(config, "scout-asked.log"))
            ? [.. File.ReadAllLines(Path.Combine(config, "scout-asked.log")).Where(l => l.Length > 0)] : [];

        public void Dispose() => OwnEnvironment.Set(EngineVariables.DockerConfig, had);

        private const string Sarif = """
            {"version": "2.1.0", "runs": [{"tool": {"driver": {"name": "docker scout", "rules": [
              {"id": "CVE-2024-0001", "shortDescription": {"text": "openssl overflow"}, "properties": {"cvssV3_severity": "CRITICAL", "fixed_version": "3.0.14",
                "affected_version": "<3.0.14", "purls": ["pkg:deb/debian/openssl@3.0.11?os_distro=bookworm"]}},
              {"id": "CVE-2024-0002", "shortDescription": {"text": "zlib read"}, "properties": {"cvssV3_severity": "HIGH", "fixed_version": "1:1.2.13",
                "purls": ["pkg:deb/debian/zlib@1:1.2.12"]}},
              {"id": "CVE-2024-0003", "shortDescription": {"text": "libc leak"}, "properties": {"cvssV3_severity": "MEDIUM", "fixed_version": "not fixed",
                "purls": ["pkg:deb/debian/libc6@2.36"]}}]}}, "results": []}]}
            """;

        private const string Recommendations = """
              Target   │  mcr.microsoft.com/dotnet/aspnet:8.0
                digest │  0123456789ab

            ## Recommended fixes

              Base image is  debian:12-slim

            Change base image
            The list displays new recommended tags in descending order, where the top results are rated as most suitable.

                          Tag              │                  Details                   │   Pushed    │       Vulnerabilities
            ───────────────────────────────┼────────────────────────────────────────────┼─────────────┼──────────────────────────
               8.0.11                      │ Benefits:                                  │ 1 week ago  │    0C     0H     1M     3L
               Newer image for same tag    │ • Same OS detected                         │             │     -1     -1
               8.0.11-bookworm-slim        │ Benefits:                                  │ 1 week ago  │    0C     0H     1M     3L

            """;
    }
}
