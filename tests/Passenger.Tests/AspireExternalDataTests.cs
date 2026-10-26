using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>An AppHost Passenger did not start, run by the run's own aspire CLI (<see cref="AspireCli"/>) through ChildProcesses,
/// found and read through the CLI in the Aspire window; everything it started ended with its process group.</summary>
[Collection(Alone.Name)]
public sealed class AspireExternalDataTests(AspireApp project) : IClassFixture<AspireApp>
{
    [AvaloniaFact(Skip = AspireApp.CliSkipped, SkipType = typeof(AspireApp), SkipWhen = nameof(AspireApp.IsCliUnavailable))]
    public void An_external_AppHost_is_found_and_its_environment_values_are_masked_in_the_window()
    {
        project.Reset();
        const string Variable = "PASSENGER_EXTERNAL_SECRET";
        const string Secret = "private-fixture-value-243";
        string host = Path.Combine(Path.GetDirectoryName(project.Host)!, "AppHost.cs");
        string source = File.ReadAllText(host);
        Assert.Contains(".WithHttpHealthCheck(\"/health\")", source, StringComparison.Ordinal);
        project.Write(Path.GetRelativePath(project.Folder, host), source.Replace(".WithHttpHealthCheck(\"/health\")",
            $".WithEnvironment(\"{Variable}\", \"{Secret}\").WithHttpHealthCheck(\"/health\")", StringComparison.Ordinal));
        string cli = Assert.IsType<string>(Programs.OnPath("aspire"));
        Assert.True(PathNames.IsUnder(cli, TestRun.Root), $"The aspire on the run's PATH is {cli}, not the run's own");
        // By real paths, which the fixture restored its references by.
        ProcessStartInfo start = ChildProcesses.Command(cli, ["run", "--apphost", PathNames.Canonical(project.Host), "--non-interactive", "--nologo"],
            PathNames.Canonical(project.Folder));
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        StringBuilder said = new();
        using Process run = ChildProcesses.Start(start);
        run.OutputDataReceived += (_, line) => { lock (said) { said.AppendLine(line.Data); } };
        run.ErrorDataReceived += (_, line) => { lock (said) { said.AppendLine(line.Data); } };
        run.BeginOutputReadLine();
        run.BeginErrorReadLine();
        MainWindow? window = null;
        int[] listed = [];
        try
        {
            // Asked every two seconds, as a person would look, while the AppHost builds and starts.
            Assert.True(Waiting.Until(() => (listed = Listed(cli, project.Host)).Length > 0 || run.HasExited, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2)),
                $"aspire run brought up no AppHost that aspire ps lists:\n{Said(said)}");
            Assert.False(run.HasExited, $"aspire run ended (exit {(run.HasExited ? run.ExitCode : 0)}):\n{Said(said)}");
            window = project.OpenHost();
            Assert.Empty(window.AspireRuntime.Apps);
            FindAction(window, "Discover Aspire AppHosts");
            Ui.WaitFor(() => window.Aspire.Apps.Any(app => PathNames.Same(PathNames.Canonical(project.Host), app.AppHostPath)),
                $"the window to find the CLI's AppHost ({window.Aspire.Status})");
            ChooseApp(window, PathNames.Canonical(project.Host));
            bool Read() => window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == "apiservice"
                && resource.Data.TryGetProperty("environment", out JsonElement environment) && environment.TryGetProperty(Variable, out _));
            // The API may still be starting: each Refresh asks the CLI again.
            for (int refresh = 0; !Read() && refresh < 30; refresh++)
            {
                FindAction(window, "Refresh Aspire Resources");
            }
            Assert.True(Read(), $"The Aspire window never read the external API's environment through the CLI ({window.Aspire.Status})");
            AspireResource api = Assert.Single(window.Aspire.ResourceSnapshots, resource => resource.DisplayName == "apiservice");
            Assert.Equal(Passenger.App.Containers.ContainerSecrets.Masked, api.Data.GetProperty("environment").GetProperty(Variable).GetString());
            Assert.DoesNotContain(Secret, api.Data.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(window.AspireWindow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains(Secret, StringComparison.Ordinal) == true);
            string projectPath = Assert.IsType<string>(api.Data.GetProperty("properties").GetProperty("project.path").GetString());
            Assert.True(PathNames.Same(PathNames.Canonical(Path.Combine(Path.GetDirectoryName(project.ApiProgram)!, $"{AspireApp.Name}.ApiService.csproj")), PathNames.Canonical(projectPath)),
                $"The CLI's project path was {projectPath}");
        }
        finally
        {
            window?.Close();
            ChildProcesses.Kill(run);
            // The orchestrator and the services it started are no children of the CLI's: each follows the AppHost's end.
            int[] ended = [run.Id, .. listed];
            string folder = PathNames.Canonical(project.Folder);
            bool Service(string command) => command.Contains(folder, StringComparison.Ordinal)
                && (command.Contains("dotnet run", StringComparison.Ordinal) || command.Contains("/bin/Debug/", StringComparison.Ordinal));
            string[] Left() => [.. Processes.Listed().Where(process => ended.Contains(process.Key) || Service(process.Value)
                || process.Value.Contains(AspireCli.Home, StringComparison.Ordinal)).Select(process => $"{process.Key}: {process.Value}")];
            Assert.True(Waiting.Until(() => Left().Length == 0, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(1)),
                $"What the CLI's run started outlived the test:\n{string.Join("\n", Left())}");
        }
    }

    /// <summary>The ids of the CLI and the AppHost aspire ps lists for an AppHost, empty while none is up.</summary>
    private static int[] Listed(string cli, string appHost)
    {
        ChildResult listed = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(cli, ["ps", "--format", "json", "--nologo"]),
            limit: TimeSpan.FromSeconds(30).Scaled())), "aspire ps", TimeSpan.FromSeconds(30) + TestFolders.Spare);
        if (!listed.Succeeded || listed.Output.TrimStart() is not ['[', ..] output)
        {
            return [];
        }
        using JsonDocument rows = JsonDocument.Parse(output);
        return [.. rows.RootElement.EnumerateArray()
            .Where(row => row.TryGetProperty("appHostPath", out JsonElement path) && path.GetString() is { } named && PathNames.Same(PathNames.Canonical(named), PathNames.Canonical(appHost)))
            .SelectMany(row => new[] { "appHostPid", "cliPid" }.Select(name => row.TryGetProperty(name, out JsonElement id) && id.TryGetInt32(out int pid) ? pid : 0))
            .Where(pid => pid > 0)];
    }

    /// <summary>Chooses an AppHost in the Aspire window's chooser by clicks and keys, as a person does.</summary>
    private static void ChooseApp(MainWindow window, string appHost)
    {
        ComboBox chooser = window.AspireWindow.AppSelector;
        int index = chooser.Items.OfType<Passenger.App.Aspire.AspireApp>().ToList().FindIndex(app => PathNames.Same(appHost, app.AppHostPath));
        Assert.True(index >= 0, $"The Aspire window does not offer {appHost}");
        Ui.Click(chooser);
        Ui.Press(window, "HOME");
        for (int step = 0; step < index; step++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
        Assert.True(PathNames.Same(appHost, Assert.IsType<Passenger.App.Aspire.AspireApp>(window.Aspire.SelectedApp).AppHostPath));
    }

    private static void FindAction(MainWindow window, string title)
    {
        Ui.Run(window, AppActions.FindAction);
        window.KeyTextInput(title);
        Ui.Settle();
        Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
        Assert.Equal(title, popup.Rows[0].Title);
        Ui.Press(window, "ENTER");
    }

    /// <summary>What the CLI said, and the end of its newest log in the run's Aspire home, which goes with the run.</summary>
    private static string Said(StringBuilder said)
    {
        string logs = Path.Combine(Environment.GetEnvironmentVariable("ASPIRE_HOME") ?? TestRun.Root, "logs");
        string? newest = Directory.Exists(logs) ? new DirectoryInfo(logs).EnumerateFiles().OrderBy(file => file.LastWriteTimeUtc).LastOrDefault()?.FullName : null;
        string log = newest is null ? "no log" : Ui.Shared(newest);
        lock (said)
        {
            return $"{said}\nIts log, {newest}:\n{log[^Math.Min(log.Length, 6000)..]}";
        }
    }
}
