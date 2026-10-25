using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Aspire;
using Passenger.App.Aspire.Protocol;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Text;

namespace Passenger.Tests;

/// <summary>The Aspire window over an AppHost's resource service, a stand-in the test drives (<see cref="AspireInteractionHost"/>),
/// which a stand-in AppHost names in its launch profile (<see cref="StandInAppHost"/>): the window runs that AppHost as a person
/// runs theirs and speaks to the stand-in with the key it gave it. No Aspire package, dashboard, orchestrator or CLI is needed,
/// so these run on every system.</summary>
public sealed class AspireStandInTests(StandInAppHost project) : IClassFixture<StandInAppHost>
{
    /// <summary>A single-file AppHost has its run configuration as the folder opens, found among the files of the folder's one
    /// walk, and as the folder watcher sees one come, a C# file gain the AppHost SDK's line, or one go; never one under a hidden
    /// folder, which a walk for what projects build passes over.</summary>
    [AvaloniaFact]
    public void Single_file_AppHosts_are_found_as_the_folder_opens_and_followed_as_files_come_change_and_go()
    {
        project.Reset();
        string sdk = $"#:sdk Aspire.AppHost.Sdk@{AspireApp.TemplatesVersion}\nvar builder = DistributedApplication.CreateBuilder(args);\nbuilder.Build().Run();\n";
        TestFolders.Write(project.Folder, "Loose/apphost.cs", sdk);
        TestFolders.Write(project.Folder, ".hidden/apphost.cs", sdk);
        TestFolders.Write(project.Folder, "Later/Notes.cs", "System.Console.WriteLine(\"Not an AppHost yet\");\n");
        string loose = PathNames.Canonical(project.PathOf("Loose/apphost.cs"));
        string notes = PathNames.Canonical(project.PathOf("Later/Notes.cs"));
        MainWindow window = project.Open(null, languageServer: false);
        try
        {
            Ui.WaitFor(() => Found(window, loose), "the single-file AppHost there as the folder opened");
            Assert.DoesNotContain(window.Configurations, c => c.Project.Contains($"{Path.DirectorySeparatorChar}.hidden{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            Assert.False(Found(window, notes), "A C# file without the AppHost SDK's line was taken for a single-file AppHost");

            TestFolders.Write(project.Folder, "Came/apphost.cs", sdk);
            string came = PathNames.Canonical(project.PathOf("Came/apphost.cs"));
            Ui.WaitFor(() => Found(window, came), "a single-file AppHost made after the folder opened");
            File.WriteAllText(notes, sdk);
            Ui.WaitFor(() => Found(window, notes), "a C# file that gained the AppHost SDK's line");
            File.Delete(loose);
            Ui.WaitFor(() => !Found(window, loose), "a deleted single-file AppHost's configuration to go");
            Assert.True(Found(window, came) && Found(window, notes), "The single-file AppHosts still there lost their configurations as another went");
            Assert.DoesNotContain(window.Configurations, c => c.Project.Contains($"{Path.DirectorySeparatorChar}.hidden{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }

        static bool Found(MainWindow window, string file) => window.Configurations.Any(c => c.Aspire && PathNames.Same(c.Project, file));
    }

    /// <summary>An AppHost whose launch profile serves HTTPS, with no trusted development certificate, has the person
    /// asked once before it starts (dev-certs is <see cref="DevCerts"/>' stand-in, which trusts nothing on this
    /// machine): Stop closes the question and starts nothing; Run over HTTP has it serve HTTP where it would have
    /// served HTTPS, unsecured transport allowed, the status bar saying why; and Create and Trust has dev-certs trust
    /// it, the AppHost serving HTTPS as its profile says.</summary>
    [AvaloniaFact]
    public void An_AppHost_that_serves_HTTPS_with_no_trusted_certificate_asks_once_before_it_starts_to_run_over_HTTP_or_trust_it()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in dev-certs is a shell script");
        project.Reset();
        string standIn = TestFolders.Create("devcerts-");
        File.WriteAllText(Path.Combine(standIn, "check"), "7");
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        const string Https = "https://localhost:7311";
        project.Serve(backend, Https);
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, DevCertificates = DevCerts.In(standIn) };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));

            Task<string?> running = window.RunAsync();
            CertificateQuestionDialog asked = Question(window, running);
            Assert.Equal("The HTTPS development certificate is not trusted", asked.Title);
            Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
            Assert.Equal($"{StandInAppHost.Configuration} was stopped as it started", Ui.Await(running, "the start to end once stopped"));
            Ui.WaitFor(() => window.CertificateQuestion is null && !asked.IsVisible, "Stop to close the question");
            Assert.Empty(window.AspireRuntime.Apps);
            Assert.False(File.Exists(project.KeyFile), "The AppHost started though its start was stopped as it asked");

            File.Delete(Path.Combine(standIn, "asked"));
            running = window.RunAsync();
            asked = Question(window, running);
            Ui.Click(asked.HttpButton);
            Ui.Launched(window, running, "the AppHost to start over HTTP");
            Assert.Equal(["http://localhost:7311", "true"], Told());
            Assert.Contains("StandIn.AppHost over HTTP for this run: the development certificate is not trusted", window.Status, StringComparison.Ordinal);
            Assert.Equal("dev-certs https --check --trust\n", DevCerts.Asked(standIn));
            Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0 && !window.IsRunning, "Stop to end the AppHost");
            File.Delete(project.KeyFile);

            File.WriteAllText(Path.Combine(standIn, "check"), "6");
            File.Delete(Path.Combine(standIn, "asked"));
            running = window.RunAsync();
            asked = Question(window, running);
            Assert.Equal("No HTTPS development certificate", asked.Title);
            Ui.Click(asked.CreateButton);
            Ui.Launched(window, running, "the AppHost to start over HTTPS");
            string[] told = Told();
            Assert.True(told[0].StartsWith(Https + ";", StringComparison.Ordinal) && told[1].Length == 0, $"The AppHost was told {string.Join(" and ", told)}");
            Assert.Equal("dev-certs https --check --trust\ndev-certs https --trust\n", DevCerts.Asked(standIn));
            Assert.DoesNotContain("over HTTP", window.Status, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        string[] Told() => File.ReadAllText(project.UrlsFile).Split('\n');
    }

    /// <summary>Debug pressed while Run brings the AppHost up takes Run's place: Run's start ends, saying so, and the
    /// AppHost comes up once, debugged. Run's start is held at its certificate's check, a stand-in that takes its time
    /// (<see cref="DevCerts"/>), which trusts nothing on this machine.</summary>
    [AvaloniaFact]
    public void Debug_while_Run_brings_the_AppHost_up_takes_its_place()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in dev-certs is a shell script");
        project.Reset();
        string standIn = TestFolders.Create("devcerts-");
        File.WriteAllText(Path.Combine(standIn, "check"), "0");
        File.WriteAllText(Path.Combine(standIn, "slow"), string.Empty);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend, "https://localhost:7313");
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, StartsLanguageServer = false, DevCertificates = DevCerts.In(standIn) };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Task<string?> running = window.RunAsync();
            Ui.PumpUntil(() => window.Actions.Work.Of(AppActions.RunProgram)?.Stage.StartsWith("Checking the HTTPS", StringComparison.Ordinal) == true || running.IsCompleted,
                "Run to check the development certificate", TimeSpan.FromMinutes(2));
            Assert.False(running.IsCompleted, $"Run ended before it checked the certificate: {(running.IsCompleted ? running.Result : null)}");
            File.Delete(Path.Combine(standIn, "slow"));
            Task<string?> debugging = window.DebugAsync();
            Assert.Equal($"{StandInAppHost.Configuration} was stopped as it started", Ui.Await(running, "Run's start to end as Debug takes its place"));
            Ui.Launched(window, debugging, "the AppHost to come up debugged");
            Assert.Equal(RunStart.Debug, Assert.Single(window.AspireRuntime.Apps).Mode);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>Rerun with nothing changed builds and restarts nothing; with the AppHost's own code changed it stops
    /// the AppHost first, then builds it and runs it again, never building over its running files, which Windows locks
    /// while it runs. Neither the builds nor the AppHost start on the window's thread, where a fork of a large process
    /// costs tens of milliseconds.</summary>
    [AvaloniaFact]
    public void Rerun_runs_an_AppHost_whose_own_code_changed_again_once_stopped_and_leaves_one_unchanged_running()
    {
        project.Reset();
        // Each AppHost writes its own key, which its resource connection is read by: none an earlier test's.
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        string folder = PathNames.Canonical(project.Folder);
        List<string> onWindow = [];
        int started = 0;
        void Heard(object? sender, ChildStart start)
        {
            if (start.Folder.Length > 0 && PathNames.Within(PathNames.Canonical(start.Folder), folder))
            {
                Interlocked.Increment(ref started);
                if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                {
                    lock (onWindow)
                    {
                        onWindow.Add(start.Command);
                    }
                }
            }
        }
        ChildProcesses.Started += Heard;
        MainWindow window = Ui.OpenFolder(project.Folder, languageServer: false, autosaves: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Launched(window, window.RunAsync(), "the stand-in AppHost to come up");
            int first = Assert.Single(window.AspireRuntime.Apps).ProcessId!.Value;
            Ui.Run(window, AppActions.Rerun);
            Assert.Contains("nothing restarted", window.Status, StringComparison.Ordinal);
            Assert.Equal(first, Assert.Single(window.AspireRuntime.Apps).ProcessId);

            File.Delete(project.KeyFile);
            File.WriteAllText(project.Program, File.ReadAllText(project.Program).Replace("The stand-in AppHost is up\"", "The stand-in AppHost is up again\"", StringComparison.Ordinal));
            Ui.Run(window, AppActions.Rerun);
            Ui.WaitFor(() => window.AspireRuntime.Apps is [{ ProcessId: int again } app] && again != first && Said(app).Contains("The stand-in AppHost is up again", StringComparison.Ordinal),
                "Rerun to run the changed AppHost again", TimeSpan.FromMinutes(3));
            Assert.False(Processes.IsRunning(first), "The AppHost that ran before Rerun still runs");
            Assert.True(started >= 3, $"Only {started} programs were heard starting in the AppHost's folder: its two builds and runs were not");
            lock (onWindow)
            {
                Assert.True(onWindow.Count == 0, $"{onWindow.Count} of the {started} programs started in the AppHost's folder started on the window's thread: {string.Join("; ", onWindow.Distinct())}");
            }
        }
        finally
        {
            ChildProcesses.Started -= Heard;
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        static string Said(Passenger.App.Aspire.AspireAppRun app)
        {
            app.Console.Flush();
            return app.Console.Document.Snapshot.ToString();
        }
    }

    /// <summary>The question a start asked of its certificate, held to the dialogs' rules.</summary>
    internal static CertificateQuestionDialog Question(MainWindow window, Task<string?> running)
    {
        Ui.PumpUntil(() => window.CertificateQuestion is not null || running.IsCompleted, "the start to ask about the certificate", TimeSpan.FromMinutes(2));
        Assert.True(window.CertificateQuestion is not null, $"The start asked nothing of its certificate: {(running.IsCompleted ? running.Result : null)}");
        return DialogButtonsTests.Check(window.CertificateQuestion!);
    }

    [AvaloniaFact]
    public void The_console_drops_overlapping_lines_follows_a_new_generation_links_its_lines_and_ends_its_streams_when_hidden()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        backend.PushResource(new Resource { Name = "apiservice", DisplayName = "apiservice", Uid = "first", ResourceType = "Project", State = "Running" });
        MainWindow window = OpenRunning(backend);
        List<string> started = [];
        EventHandler<ChildStart> observe = (_, child) =>
        {
            lock (started)
            {
                started.Add(child.Command);
            }
        };
        try
        {
            FindAction(window, "Show Aspire Log");
            Ui.WaitFor(() => backend.Consoles.Length == 1, "the console's subscription");
            AspireInteractionHost.ConsoleSubscription old = Assert.Single(backend.Consoles);
            Assert.Equal("apiservice", old.ResourceName);
            old.Push(Line(1), Line(2));
            old.Push(Line(2), Line(3));
            Ui.WaitFor(() => Console(window).Contains("line 3", StringComparison.Ordinal), "both overlapping batches");
            Assert.Equal(1, Console(window).Split('\n').Count(line => line.Trim() == "[apiservice] line 2"));
            Assert.False(old.Closed.Task.IsCompleted);
            backend.PushResource(new Resource { Name = "apiservice", DisplayName = "apiservice", Uid = "second", ResourceType = "Project", State = "Running" });
            Ui.WaitFor(() => old.Closed.Task.IsCompleted && backend.Consoles.Length == 2, "the old console subscription to be ended and replaced");
            AspireInteractionHost.ConsoleSubscription next = backend.Consoles[1];
            next.Push(new ConsoleLogLine { Text = "new generation", LineNumber = 1 });
            old.Push(new ConsoleLogLine { Text = "stale generation", LineNumber = 4 });
            Ui.WaitFor(() => Console(window).Contains("new generation", StringComparison.Ordinal), "the new generation's output");
            Assert.DoesNotContain("stale generation", Console(window), StringComparison.Ordinal);
            Assert.True(Console(window).IndexOf("[apiservice] line 3", StringComparison.Ordinal) is >= 0 and int before
                && before < Console(window).IndexOf("[apiservice] new generation", StringComparison.Ordinal),
                $"The new generation's lines did not follow those shown before its restart: {Console(window)}");
            next.Push(
                new ConsoleLogLine { Text = $"{project.Program}(3,4): error ASPIRETEST: request source", LineNumber = 2 },
                new ConsoleLogLine { Text = "See https://example.com/aspire/help.", LineNumber = 3 });
            Ui.WaitFor(() => Console(window).Contains("https://example.com/aspire/help", StringComparison.Ordinal), "the console's source and web links");
            ClickConsoleLink(window, "example.com");
            Assert.Equal("https://example.com/aspire/help", Assert.Single(window.Browsed).AbsoluteUri);
            ClickConsoleLink(window, project.Program);
            Ui.WaitFor(() => window.EditorArea.Active is { } active && PathNames.Same(PathNames.Canonical(project.Program), active.Path)
                && window.EditorArea.CodeEditor.CaretPosition == new TextPosition(2, 3), "the console's source link to open its file at its line and column");
            Assert.Single(window.Browsed);

            FindAction(window, "Show Aspire Log");
            ChildProcesses.Started += observe;
            Ui.Click(window.AspireWindow.Console.Editor);
            Ui.Run(window, AppActions.HideToolWindow);
            Ui.WaitFor(() => next.Closed.Task.IsCompleted, "hiding the Aspire window to end its console's subscription");
            Ui.Settle();
            lock (started)
            {
                Assert.DoesNotContain(started, command => command.Contains("aspire", StringComparison.OrdinalIgnoreCase)
                    && (command.Contains("resource", StringComparison.OrdinalIgnoreCase) || command.Contains("telemetry", StringComparison.OrdinalIgnoreCase)));
            }
            Assert.True(window.IsRunning, "Hiding the Aspire window stopped the AppHost");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            ChildProcesses.Started -= observe;
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        static ConsoleLogLine Line(int number) => new() { Text = $"line {number}", LineNumber = number };
    }

    /// <summary>Following an app's logs costs each log what it adds: a log landing while a thousand are kept allocates
    /// on the UI thread about what one does while ten are, and the oldest leaves the list as the dashboard's bound is
    /// passed.</summary>
    [AvaloniaFact]
    public void A_log_landing_costs_the_UI_thread_what_it_adds_however_many_are_kept()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        backend.PushResource(new Resource { Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "Running" });
        AspireDashboardHost dashboard = Ui.Await(AspireDashboardHost.StartAsync(), "the stand-in dashboard");
        dashboard.Streams = true;
        MainWindow window = OpenRunning(backend, dashboard.Endpoint);
        int sent = 0;
        try
        {
            FindAction(window, "Show Aspire Resources");
            Ui.Click(Tab(window, "Telemetry"));
            Ui.WaitFor(() => dashboard.Following("logs") == 1, "the Telemetry tab to follow the dashboard's logs");
            long few = Costs(10);
            long many = Costs(AspireClient.LogLimit);
            // With a thousand kept, a row comes into view as the oldest leaves the top (about 45 KB); with ten, none.
            Assert.True(many < few + 128 * 1024,
                $"A log landing with {AspireClient.LogLimit} kept allocated {many / 1024} KB on the UI thread, with 10 kept {few / 1024} KB: the list is read again whole at each landing");
            IReadOnlyList<AspireStructuredLog> listed = window.AspireWindow.Telemetry.Logs;
            Assert.Equal(AspireClient.LogLimit, listed.Count);
            Assert.Equal($"log {sent - AspireClient.LogLimit + 1}", listed[0].Message);
            Assert.Equal($"log {sent}", listed[^1].Message);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(dashboard.DisposeAsync().AsTask()), "the stand-in dashboard to stop");
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        // The least one log landing allocated on the UI thread, with as many kept as given (the first fill one envelope).
        long Costs(int kept)
        {
            Send(kept - sent);
            Ui.WaitFor(() => Landed(sent), $"the first {sent} logs to land");
            Ui.Settle();
            long least = long.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Send(1);
                Ui.WaitFor(() => Landed(sent), $"log {sent} to land");
                Ui.Settle();
                least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return least;
        }

        void Send(int count)
        {
            dashboard.Send("logs", "{\"resourceLogs\":[{\"resource\":{\"attributes\":[{\"key\":\"service.name\",\"value\":{\"stringValue\":\"apiservice\"}}]},"
                + "\"scopeLogs\":[{\"logRecords\":["
                + string.Join(",", Enumerable.Range(sent + 1, count).Select(number =>
                    $"{{\"timeUnixNano\":\"1760000000000000000\",\"severityText\":\"Information\",\"body\":{{\"stringValue\":\"log {number}\"}}}}"))
                + "]}]}]}");
            sent += count;
        }

        bool Landed(int last) => window.AspireWindow.StructuredLogs.ItemCount > 0
            && window.AspireWindow.StructuredLogs.Items[window.AspireWindow.StructuredLogs.ItemCount - 1] is Passenger.App.ToolWindows.RunItem<AspireStructuredLog> item
            && item.Data.Message == $"log {last}";
    }

    /// <summary>One resource's restart costs what it changed: with three resources' consoles merged and shown, a new
    /// generation of one ends its own stream alone and reads its lines again after those shown; the others' streams go
    /// on as they were, never asked for again, and every line already shown stays.</summary>
    [AvaloniaFact]
    public void A_resources_restart_reads_only_its_own_console_again()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        foreach (string name in (string[])["apiservice", "worker", "cache"])
        {
            backend.PushResource(new Resource { Name = name, DisplayName = name, Uid = name, ResourceType = "Project", State = "Running" });
        }
        MainWindow window = OpenRunning(backend);
        try
        {
            FindAction(window, "Show Aspire Log");
            Ui.WaitFor(() => backend.Consoles.Length == 3, "the three resources' console subscriptions");
            AspireInteractionHost.ConsoleSubscription api = Of("apiservice"), worker = Of("worker"), cache = Of("cache");
            api.Push(new ConsoleLogLine { Text = "api began", LineNumber = 1 });
            worker.Push(new ConsoleLogLine { Text = "worker began", LineNumber = 1 });
            cache.Push(new ConsoleLogLine { Text = "cache began", LineNumber = 1 });
            string[] began = ["[apiservice] api began", "[worker] worker began", "[cache] cache began"];
            Ui.WaitFor(() => began.All(line => Console(window).Contains(line, StringComparison.Ordinal)), "the three resources' lines merged");

            backend.PushResource(new Resource { Name = "apiservice", DisplayName = "apiservice", Uid = "apiservice again", ResourceType = "Project", State = "Running" });
            Ui.WaitFor(() => api.Closed.Task.IsCompleted && backend.Consoles.Count(console => console.ResourceName == "apiservice") == 2,
                "the restarted resource's console to be read again");
            Of("apiservice").Push(new ConsoleLogLine { Text = "api began again", LineNumber = 1 });
            worker.Push(new ConsoleLogLine { Text = "worker goes on", LineNumber = 2 });
            Ui.WaitFor(() => Console(window).Contains("[apiservice] api began again", StringComparison.Ordinal)
                && Console(window).Contains("[worker] worker goes on", StringComparison.Ordinal), "the restarted resource's lines and the others' going on");
            Assert.False(worker.Closed.Task.IsCompleted || cache.Closed.Task.IsCompleted, "One resource's restart ended the others' console streams");
            Assert.Equal(1, backend.Consoles.Count(console => console.ResourceName == "worker"));
            Assert.Equal(1, backend.Consoles.Count(console => console.ResourceName == "cache"));
            string shown = Console(window);
            Assert.All(began, line => Assert.Contains(line, shown, StringComparison.Ordinal));
            Assert.True(shown.IndexOf("[apiservice] api began", StringComparison.Ordinal) < shown.IndexOf("[apiservice] api began again", StringComparison.Ordinal),
                $"The restarted resource's lines did not follow those shown before: {shown}");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        AspireInteractionHost.ConsoleSubscription Of(string name) => backend.Consoles.Last(console => console.ResourceName == name);
    }

    /// <summary>An AppHost's resources reach the Aspire window while it is hidden, as its run follows them, and show
    /// once it is; an update in the background keeps the chosen parent and its children open.</summary>
    [AvaloniaFact]
    public void A_resource_update_in_the_background_keeps_the_chosen_parent_and_its_children_open()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        backend.PushResource(new Resource
        {
            Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "Running",
            HealthReports = { new HealthReport { Key = "ready", Status = HealthStatus.Healthy } },
        });
        Resource worker = new()
        {
            Name = "worker", DisplayName = "worker", Uid = "worker", ResourceType = "Executable", State = "Running",
            Relationships = { new ResourceRelationship { Type = "Parent", ResourceName = "apiservice" } },
        };
        MainWindow window = OpenRunning(backend);
        try
        {
            // Run showed the window: hidden now, it is still told what its run's resources become.
            window.DockHost.Hide(ToolWindowIds.Aspire);
            Ui.Settle();
            backend.PushResource(worker);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Count == 2, "the child that came with the window hidden");
            Assert.False(window.Aspire.IsDisplayed, "The hidden Aspire window still streams its resources");
            FindAction(window, "Show Aspire Resources");
            Assert.Equal([("apiservice", 0), ("worker", 1)], window.Aspire.Rows.Select(row => (row.Resource.Name, row.Depth)));
            Assert.Contains(Row(window, "worker").GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "worker");
            Ui.Click(Words(Row(window, "apiservice"), "apiservice"));
            Assert.Equal("apiservice", window.Aspire.SelectedResource?.Name);
            Ui.Click(Chevron(window, "apiservice"));
            Ui.WaitFor(() => window.Aspire.Rows.Count == 1, "the chevron to close the parent");
            Ui.Click(Chevron(window, "apiservice"));
            Ui.WaitFor(() => window.Aspire.Rows.Count == 2, "the chevron to open the parent");
            Assert.Equal("apiservice", window.Aspire.SelectedResource?.Name);
            ListBox resources = window.AspireWindow.ResourceList;
            TreeKeysCheck.Press(window, resources.ItemsSource!, resources.Items.OfType<Passenger.App.ToolWindows.RunItem<AspireRow>>().First(i => i.Data.Resource.Name == "apiservice"), i => i.Data, "the Aspire window");
            worker.State = "Stopped";
            backend.PushResource(worker);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Any(resource => resource.Name == "worker" && resource.State == "Stopped"),
                "the child's update");
            Assert.Equal("apiservice", window.Aspire.SelectedResource?.Name);
            Assert.True(window.Aspire.Rows.Any(row => row.Resource.Name == "worker"), "The child's update closed its parent");
            Assert.Contains(Row(window, "apiservice").GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "healthy");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>The Aspire window over the AppHost's resources, driven by clicks: a resource's row has its state's dot,
    /// its health's tag and its address as a link to the browser; its Details are framed tables, a secret's value
    /// masked (by its name, a connection string's password); its toolbar's icon buttons each run an action, showing its
    /// work; its commands are a menu; its console merges every resource's lines, a toggle each ending that resource's
    /// stream, with Find; and at the window's least width nothing overflows its room.</summary>
    [AvaloniaFact]
    public void The_window_shows_a_resources_row_details_commands_and_merged_console_as_Containers_shows_a_services()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        backend.PushResource(new Resource
        {
            Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "Running",
            HealthReports = { new HealthReport { Key = "ready", Status = HealthStatus.Healthy, Description = "It answers" } },
            Urls = { new Url { EndpointName = "http", FullUrl = "http://localhost:5310/" } },
            Environment =
            {
                new Passenger.App.Aspire.Protocol.EnvironmentVariable { Name = "GREETING", Value = "hello-shown" },
                new Passenger.App.Aspire.Protocol.EnvironmentVariable { Name = "API_KEY", Value = "key-never-shown" },
                new Passenger.App.Aspire.Protocol.EnvironmentVariable { Name = "DATABASE_URL", Value = "postgres://app:pass-never-shown@db/shop" },
            },
            Properties = { new ResourceProperty { Name = "project.path", Value = Google.Protobuf.WellKnownTypes.Value.ForString(project.ProjectFile) } },
            Commands = { new ResourceCommand { Name = "resource-restart", DisplayName = "Restart", State = ResourceCommandState.Enabled } },
        });
        backend.PushResource(new Resource { Name = "cache", DisplayName = "cache", Uid = "cache", ResourceType = "Container", State = "Starting" });
        MainWindow window = OpenRunning(backend);
        try
        {
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Rows.Count == 2, "both resources' rows");
            Control api = Row(window, "apiservice");
            Assert.Contains(api.GetVisualDescendants().OfType<TextBlock>(), text => text.IsEffectivelyVisible && text.Text == "healthy");
            Assert.True(api.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single().IsEffectivelyVisible, "The resource's row has no state dot");
            Button address = api.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "aspireUrl");
            Assert.Equal("localhost:5310", address.Content);
            Ui.Click(address);
            Assert.Equal("http://localhost:5310/", Assert.Single(window.Browsed).AbsoluteUri);

            Ui.Click(Words(api, "apiservice"));
            Ui.Click(Tab(window, "Details"));
            AspireDetailsPane details = window.AspireWindow.Details;
            Ui.WaitFor(() => details.Resource?.Name == "apiservice", "the API's details");
            string[] shown = [.. details.GetVisualDescendants().OfType<SelectableTextBlock>().Select(text => text.Text ?? string.Empty)];
            Assert.Contains("hello-shown", shown);
            Assert.Contains(Passenger.App.Containers.ContainerSecrets.Masked, shown);
            Assert.DoesNotContain(shown, text => text.Contains("never-shown", StringComparison.Ordinal));
            Assert.Contains(shown, text => text.StartsWith("postgres://app:", StringComparison.Ordinal) && text.EndsWith("@db/shop", StringComparison.Ordinal));
            Assert.Contains(shown, text => text.Contains("It answers", StringComparison.Ordinal));
            Assert.True(details.GetVisualDescendants().OfType<Border>().Count(border => border.Classes.Contains("framed")) >= 4,
                "The details are not framed tables as Containers' are");

            foreach ((string action, Button button) in window.AspireWindow.Tools)
            {
                Assert.True(button.Classes.Contains("tool") && button.GetVisualDescendants().OfType<PathIcon>().Any(), $"{action} is not a toolbar's icon button");
                Assert.Equal(action, ActionButtons.GetAction(button));
            }
            Ui.Click(window.AspireWindow.Tools[AppActions.AspireCommand]);
            ContextMenu commands = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(menu => menu.IsOpen)
                ?? throw new Xunit.Sdk.XunitException("The commands button opened no menu");
            Assert.Equal(["Restart"], commands.Items.OfType<MenuItem>().Select(item => (string)item.Header!));
            commands.Close();
            Ui.Settle();

            Ui.Click(Tab(window, "Log"));
            AspireInteractionHost.ConsoleSubscription[] Open() => [.. backend.Consoles.Where(console => !console.Closed.Task.IsCompleted)];
            Ui.WaitFor(() => Open().Length == 2, "both resources' console subscriptions");
            AspireInteractionHost.ConsoleSubscription apiLines = Open().Single(console => console.ResourceName == "apiservice");
            AspireInteractionHost.ConsoleSubscription cacheLines = Open().Single(console => console.ResourceName == "cache");
            int apiRead = backend.Consoles.Count(console => console.ResourceName == "apiservice");
            apiLines.Push(new ConsoleLogLine { Text = "api says hello", LineNumber = 1 });
            cacheLines.Push(new ConsoleLogLine { Text = "cache warms", LineNumber = 1 });
            Ui.WaitFor(() => Console(window).Contains("[apiservice] api says hello", StringComparison.Ordinal)
                && Console(window).Contains("[cache] cache warms", StringComparison.Ordinal), "both resources' lines merged");
            Assert.Equal(["apiservice", "cache"], window.AspireWindow.Log.Toggles.Select(toggle => (string)toggle.Content!));
            Ui.Click(window.AspireWindow.Log.Toggles.Single(toggle => Equals(toggle.Content, "cache")));
            Ui.WaitFor(() => cacheLines.Closed.Task.IsCompleted, "the cache's toggle turned off to end its stream");
            // The API's console is read again from its start; the stand-in sends only what it is pushed.
            Ui.WaitFor(() => backend.Consoles.Count(console => console.ResourceName == "apiservice") == apiRead + 1, "the API's console read again");
            Assert.DoesNotContain(backend.Consoles, console => console.ResourceName == "cache" && !console.Closed.Task.IsCompleted);
            backend.Consoles.Last(console => console.ResourceName == "apiservice").Push(new ConsoleLogLine { Text = "api says hello", LineNumber = 1 });
            Ui.WaitFor(() => Console(window).Contains("[apiservice] api says hello", StringComparison.Ordinal), "the API's lines, read again");
            Assert.DoesNotContain("[cache]", Console(window), StringComparison.Ordinal);
            Ui.Click(window.AspireWindow.Log.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "aspireLogFind"));
            Assert.True(window.AspireWindow.Find.IsEffectivelyVisible, "The console's Find did not open");

            window.Width = MainWindow.SmallestWidth;
            Ui.Settle();
            UiRulesTests.FitsItsRoom(window, window.AspireWindow, "The Aspire window's Log");
            Ui.Click(Tab(window, "Details"));
            Ui.WaitFor(() => details.Resource?.Name == "apiservice" && details.IsEffectivelyVisible, "the API's details again");
            UiRulesTests.FitsItsRoom(window, window.AspireWindow, "The Aspire window's Details");
            TextBlock type = details.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Type" && text.IsEffectivelyVisible);
            Assert.True(type.Bounds.Width + type.Margin.Right <= details.Bounds.Width * 0.45,
                $"The Details' names take {type.Bounds.Width + type.Margin.Right:0} of its {details.Bounds.Width:0} px, leaving their values too little");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A click on a resource's address, in its row or its Details, or on its commands lands while the AppHost
    /// streams updates: an update between the press and the release (its health turned degraded) changes the row in
    /// place, its control and its link kept, and the click opens the browser; the commands button and its menu's item,
    /// each pressed as an update comes, still run the command at the AppHost.</summary>
    [AvaloniaFact]
    public void A_click_on_a_resources_address_or_command_lands_while_the_AppHost_streams_updates()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        Resource api = new()
        {
            Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "Running",
            HealthReports = { new HealthReport { Key = "ready", Status = HealthStatus.Healthy } },
            Urls = { new Url { EndpointName = "http", FullUrl = "http://localhost:5310/" } },
        };
        backend.PushResource(api);
        MainWindow window = OpenRunning(backend);
        try
        {
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Rows.Count == 1, "the API's row");
            Control row = Row(window, "apiservice");
            Button address = row.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "aspireUrl");
            Ui.Settle();
            Point at = Ui.Centre(address, window);
            Assert.Same(address, Avalonia.Input.InputExtensions.InputHitTest(window, at) is Visual found ? found.GetVisualAncestors().Prepend(found).OfType<Button>().FirstOrDefault() : null);
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            api.HealthReports[0].Status = HealthStatus.Degraded;
            backend.PushResource(api);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Single().HealthStatus == "Degraded", "the AppHost's update as the address is pressed");
            Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "degraded");
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Ui.Settle();
            Assert.Equal("http://localhost:5310/", Assert.Single(window.Browsed).AbsoluteUri);
            Assert.Same(row, Row(window, "apiservice"));

            Ui.Click(Words(row, "apiservice"));
            Ui.Click(Tab(window, "Details"));
            Button detailed = window.AspireWindow.Details.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "aspireDetailsUrl");
            detailed.BringIntoView();
            Ui.Settle();
            at = Ui.Centre(detailed, window);
            Assert.Same(detailed, Avalonia.Input.InputExtensions.InputHitTest(window, at) is Visual under ? under.GetVisualAncestors().Prepend(under).OfType<Button>().FirstOrDefault() : null);
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            api.Commands.Add(new ResourceCommand { Name = "resource-restart", DisplayName = "Restart", State = ResourceCommandState.Enabled });
            backend.PushResource(api);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Single().Commands.Count == 1, "the AppHost's update as the Details' address is pressed");
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Ui.Settle();
            Assert.Equal(2, window.Browsed.Count);

            api.Commands.Add(new ResourceCommand { Name = "passenger-ping", DisplayName = "Ping", State = ResourceCommandState.Enabled });
            backend.PushResource(api);
            Ui.WaitFor(() => window.Aspire.SelectedResource?.Commands.Any(command => command.Name == "passenger-ping") == true, "the API's Ping command");
            Button commands = window.AspireWindow.Tools[AppActions.AspireCommand];
            at = Ui.Centre(commands, window);
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            api.HealthReports[0].Status = HealthStatus.Healthy;
            backend.PushResource(api);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Single().HealthStatus == "Healthy", "the AppHost's update as the commands button is pressed");
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Ui.Settle();
            ContextMenu menu = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(open => open.IsOpen)
                ?? throw new Xunit.Sdk.XunitException("The commands button pressed as an update came opened no menu");
            MenuItem ping = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Ping"));
            TopLevel shown = TopLevel.GetTopLevel(ping)!;
            at = Ui.Centre(ping, shown);
            shown.MouseDown(at, Avalonia.Input.MouseButton.Left);
            api.HealthReports[0].Status = HealthStatus.Degraded;
            backend.PushResource(api);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Single().HealthStatus == "Degraded", "the AppHost's update as the command's item is pressed");
            shown.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Ui.WaitFor(() => backend.Commands.Any(command => command is { ResourceName: "apiservice", CommandName: "passenger-ping" }),
                "the command pressed as an update came to reach the AppHost");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>Profile of an AppHost the window runs asks which of its running .NET services to profile, in a dialog held to
    /// the dialogs' rules: the stand-in AppHost's service started by the IDE as DCP asks it to (the execution protocol, with
    /// the endpoint and token the IDE gave the AppHost), listed by its resource; Cancel starts no profile.</summary>
    [AvaloniaFact]
    public void Profile_of_a_running_AppHost_asks_which_service_in_a_dialog_held_to_the_dialogs_rules()
    {
        project.Reset();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the run session: {answer}");
            Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.Any(service => service.ProcessId is > 0), "the API started as DCP asked");
            backend.PushResource(new Resource
            {
                Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "Running",
                Properties = { new ResourceProperty { Name = "project.path", Value = Google.Protobuf.WellKnownTypes.Value.ForString(PathNames.Canonical(project.ApiProject)) } },
            });
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Any(resource => resource.Name == "apiservice"), "the API's resource");

            Assert.True(window.Perform(AppActions.Profile), "Profile did not run");
            Ui.PumpUntil(() => window.OwnedWindows.OfType<AspireServiceDialog>().Any(dialog => dialog.IsVisible), "the question which service to profile");
            AspireServiceDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<AspireServiceDialog>().Single());
            Assert.Equal(["apiservice"], asked.Services.Items.OfType<AspireSelection>().Select(selection => selection.Resource.Name));
            Ui.ClickButton(asked, "Cancel");
            Ui.WaitFor(() => !asked.IsVisible, "Cancel to close the question");
            Assert.Contains("Profiling was canceled", window.Status, StringComparison.Ordinal);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>Profile of an Aspire service as it runs (the stand-in's API, started by the IDE as DCP asks) records it
    /// through its AppHost's run: Stop reads its snapshot before the API's program goes, so the trace keeps its rundown
    /// and names the API's own code, which ran before the recording began.</summary>
    [AvaloniaFact]
    public void Stop_reads_the_profile_of_an_Aspire_service_as_it_runs_before_its_program_goes()
    {
        project.Reset();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the run session: {answer}");
            int api = Api(window);
            backend.PushResource(ApiResource());
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Rows.Any(row => row.Resource.Name == "apiservice"), "the API's row");
            Ui.Click(Words(Row(window, "apiservice"), "apiservice"));
            FindAction(window, "Profile Service");
            Ui.WaitFor(() => window.Profiling?.ProcessId == api, $"Profile to record the API as it runs ({window.Status})");
            Ui.Hold(TimeSpan.FromSeconds(1), "the profiler to sample the API as it runs");

            Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
            Ui.WaitFor(() => window.ProfilerView.Snapshot is not null || (window.Status ?? string.Empty).StartsWith("The snapshot could not be read", StringComparison.Ordinal),
                "Stop to read the API's snapshot", TimeSpan.FromMinutes(2));
            Passenger.Profiler.Snapshot snapshot = window.ProfilerView.Snapshot ?? throw new Xunit.Sdk.XunitException($"Stop read no snapshot of the API: {window.Status}");
            Assert.True(Named(snapshot.All), "Stop took the API's program before reading its recording: the trace lost its rundown, and the API's own code is unnamed");
            Ui.WaitFor(() => !Processes.IsRunning(api) && window.AspireRuntime.Apps.Count == 0, "Stop to end the API and its AppHost once its snapshot was read");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        static bool Named(Passenger.Profiler.CallNode node) =>
            node.Module.Contains(StandInAppHost.Api, StringComparison.Ordinal) || node.Children.Any(Named);
    }

    /// <summary>An Avalonia app the AppHost runs as a service (DCP's start of it, Passenger's inspector put in it as in Run's
    /// app) has its failed bindings told to an agent as Problems lists them, named by its service.</summary>
    [AvaloniaFact]
    public void An_agent_is_told_the_failed_bindings_of_an_Avalonia_app_the_AppHost_runs_as_a_service()
    {
        project.Reset();
        string app = project.WriteBindingsApp();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("bindings", projectPath: app);
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the run session: {answer}");
            Ui.WaitFor(() => window.Session.FailedBindings.Any(f => f.Failure.Path == "Nmae"), () => $"the service's caption binding to fail, as Problems lists it ({window.Status}): "
                + string.Join(", ", window.AspireRuntime.Apps.SelectMany(a => a.Services).Select(s => $"{((IRunService)s).Name} {s.Inspection?.State.ToString() ?? "uninspected"}")),
                TimeSpan.FromMinutes(2));
            IReadOnlyList<Passenger.App.Mcp.AppBindingError> told = Ui.Await(new Passenger.App.Mcp.PassengerTools(window).AppBindingErrors(), "the failed bindings an agent asks for");
            Assert.True(told.Any(f => f.Path == "Nmae" && f.Control == "TextBlock#Caption" && f.App.StartsWith("bindings of ", StringComparison.Ordinal)),
                $"An agent was not told the service's failed binding Problems lists: [{string.Join("; ", told.Select(f => $"{f.App}: {f.Control} {f.Path}"))}]");
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>"(running)" says only what runs, and Rerun follows the configuration chosen: a plain run that ended is not
    /// "(running)" once an AppHost runs; with the API's configuration chosen while the AppHost runs, Rerun runs the API beside
    /// it, then stops it and runs it again, the AppHost running on as another configuration's run.</summary>
    [AvaloniaFact]
    public void Running_and_Rerun_follow_the_configuration_chosen_while_an_AppHost_runs()
    {
        project.Reset();
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration)
                && window.Configurations.Any(configuration => !configuration.Aspire && PathNames.Same(configuration.Project, PathNames.Canonical(project.ApiProject))),
                "the AppHost's and the API's configurations");
            RunConfiguration api = window.Configurations.First(configuration => !configuration.Aspire && PathNames.Same(configuration.Project, PathNames.Canonical(project.ApiProject)));
            Assert.True(window.SelectConfiguration(api.Name));
            Ui.Launched(window, window.RunAsync(), "the API to run", TimeSpan.FromMinutes(3));
            Assert.Equal($"{api.Name} (running)", window.Shown(api));
            Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
            Ui.WaitFor(() => !window.IsRunning, "Stop to end the API");

            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, $"the stand-in AppHost's start ({window.Status})");
            int host = Assert.Single(window.AspireRuntime.Apps).ProcessId!.Value;
            Assert.Equal(api.Name, window.Shown(api));

            Assert.True(window.SelectConfiguration(api.Name));
            Ui.Run(window, AppActions.Rerun);
            Ui.WaitFor(() => window.Shown(api) == $"{api.Name} (running)" && window.Session.Runner.ProcessId is not null,
                $"Rerun to run the API chosen while the AppHost runs ({window.Status})", TimeSpan.FromMinutes(3));
            int first = window.Session.Runner.ProcessId!.Value;
            Ui.Run(window, AppActions.Rerun);
            Ui.WaitFor(() => window.Session.Runner.ProcessId is int again && again != first && window.Session.Runner.IsRunning,
                $"Rerun to run the API again ({window.Status})", TimeSpan.FromMinutes(3));
            Assert.False(Processes.IsRunning(first), "The API that ran before Rerun still runs");
            Assert.True(Processes.IsRunning(host) && window.AspireRuntime.Apps.Count == 1, "Rerun of the API stopped the AppHost, another configuration's run");
            Assert.Equal($"{api.Name} (running)", window.Shown(api));
            Assert.Equal(StandInAppHost.Configuration + " (running)", window.Shown(window.Configurations.First(c => c.Name == StandInAppHost.Configuration)));
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A restart that fails is said as the restart's refusal, in words, whoever asked for it: Rerun after the API's
    /// code changed has its AppHost stop it (the stand-in's stop command ends its program), builds it, and finds the resource
    /// to start again made anew under another identity, which it says rather than a sequence's raw complaint.</summary>
    [AvaloniaFact]
    public void A_restart_that_fails_is_said_as_its_refusal_in_words()
    {
        project.Reset();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the run session: {answer}");
            int api = Api(window);
            Resource resource = ApiResource();
            resource.Commands.Add(new ResourceCommand { Name = "resource-stop", DisplayName = "Stop", State = ResourceCommandState.Enabled });
            resource.Commands.Add(new ResourceCommand { Name = "resource-start", DisplayName = "Start", State = ResourceCommandState.Enabled });
            backend.PushResource(resource);
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Any(shown => shown.Name == "apiservice" && shown.Commands.Count == 2), "the API's resource and its commands");
            backend.OnCommand = command =>
            {
                if (command.CommandName == "resource-stop")
                {
                    Processes.Kill(api);
                    resource.Uid = "made-anew";
                    backend.PushResource(resource);
                }
            };

            File.AppendAllText(project.PathOf($"{StandInAppHost.Api}/Program.cs"), "// changed since it started\n");
            Ui.Run(window, AppActions.Rerun);
            string config = StandInAppHost.Configuration;
            Ui.WaitFor(() => window.Status?.Contains("could not be restarted", StringComparison.Ordinal) == true, $"Rerun to say the API's restart failed ({window.Status})", TimeSpan.FromMinutes(3));
            Assert.Equal($"apiservice of {config} could not be restarted: apiservice is no longer one resource of {config} to start again after its build", window.Status);
            Assert.Equal(["resource-stop"], backend.Commands.Select(command => command.CommandName));
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>Profile of an AppHost waits as long as the AppHost takes to ask for the service it records, then gives that
    /// service its time to reach the profiler, counted from the ask: past it, the start is given up as the time limit it is,
    /// never as a stop nobody pressed, and the AppHost is stopped. Here the window's limit is a moment, which no start meets.</summary>
    [AvaloniaFact]
    public void Profile_of_an_AppHost_gives_up_on_a_service_past_its_time_saying_so()
    {
        project.Reset();
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            window.AspireRuntime.ProfileLimit = TimeSpan.FromMilliseconds(10);
            Task<string?> profiling = window.ProfileAsync();
            Ui.PumpUntil(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1 || profiling.IsCompleted,
                "the AppHost to come up for Profile", TimeSpan.FromMinutes(2));
            Assert.False(profiling.IsCompleted, $"Profile ended before its AppHost asked for a service: {(profiling.IsCompleted ? profiling.Result : null)}");
            Assert.True(window.Actions.Work.Of(AppActions.Profile) is not null && window.Actions.Work.Of(AppActions.RunProgram) is null,
                "Profile of the AppHost did not start under Profile's own work");
            Ui.Hold(TimeSpan.FromSeconds(1), "Profile to wait on with no service asked for, however long its AppHost takes");
            Assert.False(profiling.IsCompleted, $"Profile gave up before its AppHost asked for a service: {(profiling.IsCompleted ? profiling.Result : null)}");

            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode _) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            string? why = Ui.Await(profiling, "Profile to give up on the API");
            Assert.True(why?.StartsWith("apiservice did not start under the profiler within ", StringComparison.Ordinal) == true
                && why.EndsWith(" s of its AppHost asking for it", StringComparison.Ordinal), $"Profile's time limit was said as {why}");
            Assert.Equal(why, window.Status);
            Ui.WaitFor(() => window.AspireRuntime.Apps.Count == 0, "the AppHost to stop as Profile gave up");
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>Run or Debug of a configuration that runs ends it and starts it again, a plain program's in the Run console and an
    /// AppHost's alike, each start in its own words; and "Already running" is said only of what shares the Run console, naming
    /// it: another configuration's Run while the API has it.</summary>
    [AvaloniaFact]
    public void Run_or_Debug_of_a_configuration_that_runs_starts_it_again_and_only_the_Run_consoles_own_is_said_running()
    {
        project.Reset();
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration)
                && window.Configurations.Any(configuration => !configuration.Aspire && PathNames.Same(configuration.Project, PathNames.Canonical(project.ApiProject))),
                "the AppHost's and the API's configurations");
            RunConfiguration api = window.Configurations.First(configuration => !configuration.Aspire && PathNames.Same(configuration.Project, PathNames.Canonical(project.ApiProject)));
            Assert.True(window.SelectConfiguration(api.Name));
            Ui.Launched(window, window.RunAsync(), "the API to run", TimeSpan.FromMinutes(3));
            int first = window.Session.Runner.ProcessId!.Value;
            Ui.Launched(window, window.RunAsync(), "Run of the API that runs to run it again", TimeSpan.FromMinutes(3));
            Assert.True(window.Session.Runner.ProcessId is int again && again != first && !Processes.IsRunning(first), "Run of the API that ran did not end it and run it again");

            RunConfiguration other = api.Copied() with { Name = "The API again", Generated = false };
            window.SetConfigurations([other], other.Name);
            Assert.Equal($"Already running: {api.Name} has the Run console; stop it first", Ui.Await(window.RunAsync(), "Run of another configuration"));
            Assert.True(window.Perform(AppActions.Stop), "Stop could not run");
            Ui.WaitFor(() => !window.IsRunning, "Stop to end the API");

            string config = StandInAppHost.Configuration;
            Assert.True(window.SelectConfiguration(config));
            Ui.Launched(window, window.RunAsync(), "the stand-in AppHost to come up");
            int host = Assert.Single(window.AspireRuntime.Apps).ProcessId!.Value;
            // Each AppHost writes its own key, which its resource connection is read by: none an earlier one's.
            File.Delete(project.KeyFile);
            Ui.Launched(window, window.RunAsync(), "Run of the AppHost that runs to run it again");
            Assert.True(window.AspireRuntime.Apps is [{ ProcessId: int rerun }] && rerun != host && !Processes.IsRunning(host),
                $"Run of the AppHost that ran did not end it and run it again ({window.Status})");
            Assert.Equal($"Running {config}", window.Status);
            host = window.AspireRuntime.Apps.Single().ProcessId!.Value;
            File.Delete(project.KeyFile);
            Ui.Launched(window, window.DebugAsync(), "Debug of the AppHost that runs to debug it");
            Assert.True(window.AspireRuntime.Apps is [{ ProcessId: int debugged, Mode: RunStart.Debug }] && debugged != host && !Processes.IsRunning(host),
                $"Debug of the AppHost that ran did not end it and debug it ({window.Status})");
            Assert.Equal($"Debugging {config}: each .NET service it starts under the debugger", window.Status);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>An Aspire service's Restart from the Aspire window goes through its run: one the debugger attached to
    /// as it ran is attached to again once its AppHost has started it again (the stand-in's restart command ends it and
    /// asks for it again, as DCP does). Run without Debugger of one that nothing holds or debugs restarts nothing,
    /// saying so.</summary>
    [AvaloniaFact]
    public void An_Aspire_services_Restart_goes_through_its_run_and_one_attached_as_it_ran_is_attached_again()
    {
        project.Reset();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        Task<(System.Net.WebSockets.ClientWebSocket Notified, System.Net.HttpStatusCode Answer)>? again = null;
        try
        {
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the run session: {answer}");
            int first = Api(window);
            Resource resource = ApiResource();
            resource.Commands.Add(new ResourceCommand { Name = "resource-restart", DisplayName = "Restart", State = ResourceCommandState.Enabled });
            backend.PushResource(resource);
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Rows.Any(row => row.Resource.Name == "apiservice"), "the API's row");
            Ui.Click(Words(Row(window, "apiservice"), "apiservice"));

            FindAction(window, "Run Service without Debugger");
            string config = StandInAppHost.Configuration;
            Ui.WaitFor(() => window.Status == $"apiservice of {config} already runs without the debugger: nothing restarted", $"Run without Debugger to say so ({window.Status})");
            Assert.True(Processes.IsRunning(first) && Api(window) == first && backend.Commands.Length == 0, "Run without Debugger restarted the API, which nothing held or debugged");

            FindAction(window, "Attach to Service");
            Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.Single().DebugTab?.IsLive == true, $"Attach to the API as it runs ({window.Status})");
            backend.OnCommand = command =>
            {
                if (command.CommandName == "resource-restart")
                {
                    Processes.Kill(first);
                    again = Task.Run(() => project.StartApiAsync("apiservice", subscribe: false));
                }
            };
            // Attach shows the Debug window: the Aspire window's resources are asked for again.
            FindAction(window, "Show Aspire Resources");
            Ui.Click(Words(Row(window, "apiservice"), "apiservice"));
            Ui.Click(window.AspireWindow.Tools[AppActions.AspireCommand]);
            ContextMenu commands = window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault(menu => menu.IsOpen)
                ?? throw new Xunit.Sdk.XunitException("The commands button opened no menu");
            Ui.Choose(commands, "Restart");
            Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.SingleOrDefault() is { ProcessId: int restarted, DebugTab.IsLive: true } && restarted != first,
                $"the API restarted through its run and attached to again ({window.Status})", TimeSpan.FromMinutes(2));
            Assert.Equal(["resource-restart"], backend.Commands.Select(command => command.CommandName));
            Assert.Equal($"{config}: restarted apiservice", window.Status);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            if (again is not null)
            {
                Ui.Await(again, "the restart's start request").Notified.Dispose();
            }
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A service held at its start for a debugger that cannot reach it runs on without it, saying so, never left
    /// FailedToStart or held for good: debugged, the stand-in's API started by DCP with its runtime's diagnostics off.</summary>
    [AvaloniaFact]
    public void A_held_Aspire_service_the_debugger_cannot_reach_runs_on_without_it_saying_so()
    {
        project.Reset();
        project.BuildApi();
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Launched(window, window.DebugAsync(), "the stand-in AppHost to come up debugged");
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice", "Debug",
                new Dictionary<string, string> { ["DOTNET_EnableDiagnostics"] = "0" });
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the held API's start: {answer} ({window.Status})");
            int api = Api(window);
            Assert.Equal("apiservice runs without the debugger: Its runtime was started with DOTNET_EnableDiagnostics=0, which turns its debugger off: nothing can attach to it",
                window.Status);
            Ui.Hold(TimeSpan.FromSeconds(1), "the API to run on");
            Assert.True(Processes.IsRunning(api) && window.AspireRuntime.Apps.Single().Services.Single() is { DebugTab: null }, "The API the debugger could not reach did not run on");
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A service held at its start for a debugger its runtime keeps out (the debugger off, its diagnostic port on) runs
    /// on undebugged, and a .NET program it starts runs too, held for no one: debugged, the stand-in's API runs a child of its
    /// own as it starts, and says what the child wrote.</summary>
    [AvaloniaFact]
    public void An_Aspire_service_the_debugger_cannot_reach_runs_on_and_so_does_a_program_it_starts()
    {
        project.Reset();
        project.BuildApi();
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Launched(window, window.DebugAsync(), "the stand-in AppHost to come up debugged");
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice", "Debug",
                new Dictionary<string, string> { ["DOTNET_EnableDiagnostics_Debugger"] = "0", ["STAND_IN_CHILD"] = "1" });
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the held API's start: {answer} ({window.Status})");
            int api = Api(window);
            AspireResourceRun service = window.AspireRuntime.Apps.Single().Services.Single();
            Ui.WaitFor(() => service.Console.Document.Snapshot.ToString().Contains("child said: child ran", StringComparison.Ordinal),
                () => $"the API, which the debugger could not reach, and the program it starts to run, not held at their start ({service.Console.Document.Snapshot})");
            Assert.StartsWith("apiservice runs without the debugger: ", window.Status, StringComparison.Ordinal);
            Assert.True(Processes.IsRunning(api) && service.DebugTab is null, "The API the debugger could not reach did not run on undebugged");
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A save made while Apply Hot Reload Changes applies an edit to a service run with Hot Reload sends the service no
    /// second update: the save's apply waits for the one under way, which took the edit, and the service's console says the
    /// one apply. Run with Hot Reload, the stand-in's API is attached to quietly as DCP starts it.</summary>
    [AvaloniaFact]
    public void A_save_during_an_apply_to_a_service_run_with_Hot_Reload_sends_it_one_update()
    {
        project.Reset();
        project.BuildApi();
        File.Delete(project.KeyFile);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            window.ApplySettings(window.Settings with { HotReload = true });
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, $"the stand-in AppHost to come up ({window.Status})");
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the API's start: {answer} ({window.Status})");
            Ui.WaitFor(() => window.Debugger.Running.Count == 1, $"the API to be attached to for Hot Reload ({window.Status})");
            AspireResourceRun service = window.AspireRuntime.Apps.Single().Services.Single();

            Assert.Null(window.OpenFile(project.PathOf($"{StandInAppHost.Api}/Program.cs")));
            Typing.Replace(window, "\"The stand-in API is up\"", "\"The stand-in API is up, edited\"");
            List<Passenger.App.Debugging.HotReloadOutcome> told = [];
            window.Debugger.Applied += (_, outcome) => told.Add(outcome);
            // The action's apply under way, then the save, before anything has settled.
            Assert.Null(window.Actions.Run(AppActions.ApplyHotReloadChanges));
            Assert.True(window.EditorArea.SaveAll(), "The edited file was not saved");
            Ui.WaitFor(() => told.Count > 0 && window.Actions.Work.Of(AppActions.ApplyHotReloadChanges) is null, $"Apply Hot Reload Changes to end ({window.Status})",
                TimeSpan.FromMinutes(2));
            Ui.Settle();

            string console = service.Console.Document.Snapshot.ToString();
            Assert.True(told.Count == 1 && told[0].Result == Passenger.App.Debugging.HotReloadResult.Applied,
                $"The API took {told.Count} applies for one edit, saved as it applied: {string.Join("; ", told.Select(outcome => outcome.Said))}");
            Assert.True(console.Split('\n').Count(line => line.StartsWith("Hot Reload: ", StringComparison.Ordinal)) == 1,
                $"The API's console does not say the one apply its edit took: {console}");
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>The stand-in API's program, once the IDE has started it as DCP asked.</summary>
    private static int Api(MainWindow window)
    {
        Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.Any(service => service.ProcessId is > 0), "the API started as DCP asked");
        return window.AspireRuntime.Apps.Single().Services.Single().ProcessId!.Value;
    }

    /// <summary>The stand-in API as its AppHost's resource service says it runs.</summary>
    private Resource ApiResource() => new()
    {
        Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "Running",
        Properties = { new ResourceProperty { Name = "project.path", Value = Google.Protobuf.WellKnownTypes.Value.ForString(PathNames.Canonical(project.ApiProject)) } },
    };

    /// <summary>A service its run configuration says not to start is Not started, never a failure: the IDE refuses DCP's first
    /// start of it, and the resource DCP then calls FailedToStart has a muted dot and a tip saying why and how it runs, in its
    /// row and in its Details.</summary>
    [AvaloniaFact]
    public void A_service_marked_Dont_start_reads_Not_started_in_its_row_and_Details()
    {
        project.Reset();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            RunConfiguration marked = project.DontStartApi(window.Configurations.First(configuration => configuration.Name == StandInAppHost.Configuration));
            window.SetConfigurations([marked], marked.Name);
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, $"the stand-in AppHost's start ({window.Status})");
            (System.Net.WebSockets.ClientWebSocket notified, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            using System.Net.WebSockets.ClientWebSocket subscribed = notified;
            Assert.False((int)answer is >= 200 and < 300, "The IDE started a service its configuration says not to start");
            Assert.DoesNotContain(window.AspireRuntime.Apps.Single().Services, service => service.ProcessId is > 0);
            // As DCP then says it: its start failed.
            backend.PushResource(project.Refused());
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Rows.Any(row => row.Resource.Name == "apiservice"), "the refused service's row");
            Control row = Row(window, "apiservice");
            Avalonia.Controls.Shapes.Ellipse dot = row.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Single();
            Assert.Equal($"{AspireAppRun.NotStarted}. {AspireAppRun.NotStartedWhy}", ToolTip.GetTip(dot));
            Assert.Equal(Colour(window, TokenKeys.TextMuted), (dot.Fill as Avalonia.Media.ISolidColorBrush)?.Color);
            Ui.Click(Words(row, "apiservice"));
            Ui.Click(Tab(window, "Details"));
            string[] shown = [.. window.AspireWindow.Details.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty)];
            Assert.Contains(AspireAppRun.NotStarted, shown);
            Assert.Contains(AspireAppRun.NotStartedWhy, shown);
            Assert.DoesNotContain("FailedToStart", shown);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        static Avalonia.Media.Color Colour(MainWindow window, string token) =>
            window.TryFindResource(token, window.ActualThemeVariant, out object? found) && found is Avalonia.Media.ISolidColorBrush brush ? brush.Color : default;
    }

    /// <summary>The Aspire window's service actions are the Containers window's, under the same titles and in the same
    /// place: its row's menu begins with them, as its toolbar's group after Refresh does, and Find Action lists each
    /// once. On a container they are greyed in the menu with why; and Debug Service of a service its run left unstarted
    /// (its configuration's Don't start), chosen from its row's menu, has its AppHost start it, the debugger attaching
    /// as it starts.</summary>
    [AvaloniaFact]
    public void Service_actions_grey_with_why_on_a_container_and_Debug_starts_a_service_left_unstarted_under_the_debugger()
    {
        project.Reset();
        project.BuildApi();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = project.Open(project.Program, languageServer: false);
        System.Net.WebSockets.ClientWebSocket? started = null;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            RunConfiguration marked = project.DontStartApi(window.Configurations.First(configuration => configuration.Name == StandInAppHost.Configuration));
            window.SetConfigurations([marked], marked.Name);
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, $"the stand-in AppHost's start ({window.Status})");
            (System.Net.WebSockets.ClientWebSocket refusedStart, System.Net.HttpStatusCode refused) = project.AskToStartApi("apiservice");
            refusedStart.Dispose();
            Assert.False((int)refused is >= 200 and < 300, "The IDE started a service its configuration says not to start");
            Resource unstarted = project.Refused();
            unstarted.Commands.Add(new ResourceCommand { Name = "resource-start", DisplayName = "Start", State = ResourceCommandState.Enabled });
            backend.PushResource(unstarted);
            backend.PushResource(new Resource { Name = "cache", DisplayName = "cache", Uid = "cache", ResourceType = "Container", State = "Running" });
            FindAction(window, "Show Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Rows.Count == 2, "the API's and the cache's rows");
            string[] services = ["Debug Service", "Attach to Service", "Run Service without Debugger", "Profile Service"];
            Assert.Equal(services, window.AspireWindow.Tools.Keys.Skip(2).Take(4).Select(id => window.Actions[id]!.Title));

            ContextMenu menu = RowMenu(window, "cache");
            MenuItem[] first = [.. menu.Items.OfType<Control>().TakeWhile(item => item is MenuItem).Cast<MenuItem>()];
            Assert.Equal(services, first.Select(item => (string?)item.Header));
            foreach (MenuItem item in first)
            {
                Assert.False(item.IsEnabled, $"{item.Header} is not greyed on a container");
                Assert.Equal($"cache is not a .NET service of {marked.Name}", ToolTip.GetTip(item));
            }
            menu.Close();
            Ui.Settle();
            Ui.Run(window, AppActions.FindAction);
            window.KeyTextInput("Service");
            Ui.Settle();
            Passenger.App.Navigation.ActionPopup popup = Assert.IsType<Passenger.App.Navigation.ActionPopup>(window.Popup);
            Assert.All(services, title => Assert.Single(popup.Rows, row => row.Title == title));
            Ui.Press(window, "ESCAPE");

            Ui.Choose(RowMenu(window, "apiservice"), "Debug Service");
            Ui.WaitFor(() => backend.Commands.Any(command => command.CommandName == "resource-start"), $"the AppHost asked to start the API ({window.Status})");
            Assert.Equal($"Debugging apiservice of {marked.Name} from its start: its AppHost was asked to start it", window.Status);
            (started, System.Net.HttpStatusCode answer) = project.AskToStartApi("apiservice");
            Assert.True((int)answer is >= 200 and < 300, $"The IDE refused the API's start once Debug asked it: {answer} ({window.Status})");
            Ui.WaitFor(() => window.AspireRuntime.Apps.Single().Services.SingleOrDefault() is { ProcessId: > 0, DebugTab.IsLive: true },
                $"the API debugged from its start ({window.Status})", TimeSpan.FromMinutes(1));
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            started?.Dispose();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A resource's row's menu, opened by a right click on its name once a click has chosen it.</summary>
    private static ContextMenu RowMenu(MainWindow window, string name)
    {
        Ui.Click(Words(Row(window, name), name));
        Ui.RightClick(Words(Row(window, name), name));
        ContextMenu menu = window.AspireWindow.ResourceList.ContextMenu ?? throw new Xunit.Sdk.XunitException($"{name}'s row has no menu");
        Ui.WaitFor(() => menu.IsOpen, $"{name}'s row menu to open");
        return menu;
    }

    /// <summary>A resource's row as the Aspire window shows it, scrolled into view.</summary>
    internal static Control Row(MainWindow window, string name) => Ui.Row(window.AspireWindow.ResourceList, window.Aspire.ItemOf(name)
        ?? throw new Xunit.Sdk.XunitException($"The Aspire window lists no {name}, only {string.Join(", ", window.Aspire.Rows.Select(row => row.Resource.Name))}"));

    /// <summary>Words shown in a row.</summary>
    internal static TextBlock Words(Control row, string words) => row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == words);

    /// <summary>A row's chevron, which opens and closes it.</summary>
    internal static PathIcon Chevron(MainWindow window, string name) =>
        Row(window, name).GetVisualDescendants().OfType<PathIcon>().Single(icon => icon.Name == "rowChevron");

    /// <summary>A tab of the Aspire window's pages.</summary>
    internal static Avalonia.Controls.Primitives.ToggleButton Tab(MainWindow window, string page) => window.AspireWindow.GetVisualDescendants()
        .OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(tab => tab.Classes.Contains("panelTab") && tab.Content is TextBlock { Text: var words } && words == page);

    [AvaloniaFact]
    public void A_confirmation_cancelled_and_a_form_the_AppHost_updates_send_what_the_dialog_shows()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            backend.Push(new WatchInteractionsResponseUpdate
            {
                InteractionId = 1,
                Title = "Restart API?",
                Message = "Confirm restart",
                ShowSecondaryButton = true,
                Notification = new InteractionNotification { Intent = MessageIntent.Confirmation },
            });
            AspireInteractionDialog confirmation = Interaction(window, "Restart API?");
            Ui.Click(confirmation.Secondary);
            Ui.WaitFor(() => backend.Answers.Length == 1, "the confirmation's answer");
            WatchInteractionsRequestUpdate canceled = Assert.Single(backend.Answers);
            Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.Notification, canceled.KindCase);
            Assert.False(canceled.Notification.Result);
            CompleteInteraction(backend, window, 1);

            backend.Push(Form("before"));
            AspireInteractionDialog form = Interaction(window, "API settings");
            TextBox input = Assert.IsType<TextBox>(form.InputControls["value"]);
            Ui.Click(input);
            Ui.WaitFor(() => backend.Answers.Any(answer => answer.InteractionId == 2
                && answer.KindCase == WatchInteractionsRequestUpdate.KindOneofCase.InputsDialog
                && answer.ResponseUpdate && answer.InputsDialog.InputItems[0].Value == "before"),
                "the form's first value");
            int baseline = backend.Answers.Length;
            AspireInteractionHost.WireBarrier sending = backend.HoldNextAnswer();
            string firstValue = new string('x', 256 * 1024) + "typed while connected";
            ReplaceDuringWork(form, firstValue);
            Ui.PumpUntil(() => sending.Entered.Task.IsCompleted, "the typed value's update to reach the held frame");
            Assert.Equal(baseline, backend.Answers.Length);
            WatchInteractionsResponseUpdate update = Form("server update");
            update.InputsDialog.InputItems[0].ValidationErrors.Add("Choose another value");
            backend.Push(update);
            Ui.PumpUntil(() => form.Problem.Text?.Contains("Choose another value", StringComparison.Ordinal) == true,
                "the AppHost's update while the first answer is still on its way");
            input = Assert.IsType<TextBox>(form.InputControls["value"]);
            Assert.Equal(firstValue, input.Text);
            Ui.Click(input, settle: false);
            ReplaceDuringWork(form, "superseded value");
            ReplaceDuringWork(form, "latest value");
            sending.Release.TrySetResult();
            Ui.WaitFor(() => backend.Answers.Length == baseline + 2, "the first update and the latest value after it");
            WatchInteractionsRequestUpdate[] updates = backend.Answers.Skip(baseline).ToArray();
            Assert.Equal(firstValue, updates[0].InputsDialog.InputItems[0].Value);
            Assert.Equal("latest value", updates[1].InputsDialog.InputItems[0].Value);
            Assert.All(updates, answer =>
            {
                Assert.Equal(2, answer.InteractionId);
                Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.InputsDialog, answer.KindCase);
                Assert.True(answer.ResponseUpdate);
            });
            Ui.Click(form.Primary);
            Ui.WaitFor(() => backend.Answers.Length == baseline + 3, "the form's answer");
            Assert.False(backend.Answers[^1].ResponseUpdate);
            Assert.Equal("latest value", backend.Answers[^1].InputsDialog.InputItems[0].Value);
            CompleteInteraction(backend, window, 2);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A prompt the AppHost shows (a real AppHost's own, as the development certificate's notification is)
    /// never keeps the main window open: the window's close closes it with it, and ends the AppHost as Stop
    /// would.</summary>
    [AvaloniaFact]
    public void A_prompt_the_AppHost_shows_closes_with_the_window_whose_close_ends_the_AppHost()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            int host = Assert.Single(window.AspireRuntime.Apps).ProcessId!.Value;
            backend.Push(new WatchInteractionsResponseUpdate
            {
                InteractionId = 7,
                Title = "No trusted development certificate",
                MessageBox = new InteractionMessageBox { Intent = MessageIntent.Warning },
            });
            Interaction(window, "No trusted development certificate");
            window.Close();
            Assert.False(window.IsVisible, "The AppHost's prompt kept the window open as it was closed");
            Assert.True(window.Ended is not null, "The window's close did not end what its folder runs");
            Ui.WaitFor(() => !Processes.IsRunning(host), "the window's close to end the AppHost", TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A terminal prompt, which Passenger cannot give, closed by its button, by Escape and by the window's close, each
    /// answered as cancelled for the request it shows last, the connection and the next prompt going on.</summary>
    [AvaloniaFact]
    public void A_terminal_prompt_closed_any_way_is_answered_cancelled_and_ordinary_prompts_go_on()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            int id = 900;
            foreach (string close in (string[])["button", "escape", "window"])
            {
                WatchInteractionsResponseUpdate update = new()
                {
                    InteractionId = id,
                    Title = $"Open a terminal ({close})",
                    PrimaryButtonText = "Open terminal",
                    PromptTerminal = new InteractionPromptTerminal { TerminalId = "terminal-before" },
                };
                backend.Push(update);
                AspireInteractionDialog terminal = Interaction(window, update.Title);
                Assert.Equal("Close", terminal.Primary.Content);
                Assert.Contains(terminal.GetVisualDescendants().OfType<TextBlock>(), block =>
                    block.Text?.Contains("Terminal interactions are not supported", StringComparison.Ordinal) == true);
                update.PromptTerminal.TerminalId = "terminal-current";
                update.Title = $"Current terminal request ({close})";
                backend.Push(update);
                Ui.WaitFor(() => terminal.Title?.EndsWith(" · " + update.Title, StringComparison.Ordinal) == true,
                    "the prompt's newer request");
                int answered = backend.Answers.Length;
                switch (close)
                {
                    case "button":
                        Ui.Click(terminal.Primary);
                        break;
                    case "escape":
                        Ui.Press(terminal, "ESCAPE");
                        break;
                    default:
                        // The system's close, which the headless platform draws no button for.
                        terminal.Close();
                        break;
                }
                Ui.WaitFor(() => backend.Answers.Length == answered + 1, $"the terminal prompt closed by its {close} to be answered");
                WatchInteractionsRequestUpdate rejected = backend.Answers[^1];
                Assert.Equal(id, rejected.InteractionId);
                Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.PromptTerminal, rejected.KindCase);
                Assert.Equal("terminal-current", rejected.PromptTerminal.TerminalId);
                Assert.True(rejected.PromptTerminal.HasResult);
                Assert.False(rejected.PromptTerminal.Result);
                Assert.False(backend.Disconnected.Task.IsCompleted, $"Closing a terminal prompt by its {close} ended the connection");
                CompleteInteraction(backend, window, id);
                id++;
            }

            backend.Push(new WatchInteractionsResponseUpdate
            {
                InteractionId = id,
                Title = "Continue normally",
                MessageBox = new InteractionMessageBox { Intent = MessageIntent.Confirmation },
            });
            AspireInteractionDialog ordinary = Interaction(window, "Continue normally");
            int before = backend.Answers.Length;
            Ui.Click(ordinary.Primary);
            Ui.WaitFor(() => backend.Answers.Length == before + 1, "the ordinary prompt's answer");
            Assert.Equal(id, backend.Answers[^1].InteractionId);
            Assert.Equal(WatchInteractionsRequestUpdate.KindOneofCase.MessageBox, backend.Answers[^1].KindCase);
            Assert.True(backend.Answers[^1].MessageBox.Result);
            Assert.False(backend.Disconnected.Task.IsCompleted);
            CompleteInteraction(backend, window, id);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    [AvaloniaFact]
    public void A_file_input_the_AppHost_replaces_refuses_the_upload_under_way_and_takes_the_file_chosen_again()
    {
        project.Reset();
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        MainWindow window = OpenRunning(backend);
        try
        {
            backend.Push(FileForm());
            AspireInteractionDialog dialog = Interaction(window, "Upload API settings");
            PickUploadFile(window, dialog, project.Program);
            Ui.PumpUntil(() => backend.FirstUpload.Entered.Task.IsCompleted, "the chosen file's upload to reach the held reply");
            UploadFileChunk[] oldUpload = Assert.Single(backend.Uploads);
            Assert.Equal(3, oldUpload[0].InteractionId);
            Assert.Equal("attachment", oldUpload[0].InputName);
            Assert.Equal(Path.GetFileName(project.Program), oldUpload[0].FileName);
            Assert.Equal(File.ReadAllBytes(project.Program), oldUpload.SelectMany(chunk => chunk.Data.ToByteArray()).ToArray());
            WatchInteractionsResponseUpdate update = FileForm();
            update.Message = "The server replaced this file input";
            backend.Push(update);
            Ui.PumpUntil(() => dialog.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == update.Message),
                "the replaced file input while the first upload is held");
            Assert.Same(window, dialog.Owner);
            backend.FirstUpload.Release.TrySetResult();
            Ui.WaitFor(() => dialog.Problem.IsEffectivelyVisible && dialog.Problem.Text!.Contains("Choose the file again", StringComparison.Ordinal),
                "the dialog to refuse the upload made for the input replaced");
            Ui.Click(dialog.Primary);
            Ui.WaitFor(() => backend.Answers.Length == 1, "the answer with no file");
            WatchInteractionsRequestUpdate rejected = Assert.Single(backend.Answers);
            Assert.Equal(string.Empty, Assert.Single(rejected.InputsDialog.InputItems).Value);
            Assert.DoesNotContain("stale-file-id", rejected.ToString(), StringComparison.Ordinal);

            PickUploadFile(window, dialog, project.ProjectFile);
            Ui.WaitFor(() => Equals(((Button)dialog.InputControls["attachment"]).Content, "1 file(s) uploaded"), "the second upload to finish");
            Assert.Equal(2, backend.Uploads.Length);
            Assert.Equal(File.ReadAllBytes(project.ProjectFile), backend.Uploads[1].SelectMany(chunk => chunk.Data.ToByteArray()).ToArray());
            Ui.Click(dialog.Primary);
            Ui.WaitFor(() => backend.Answers.Length == 2, "the answer with the file chosen again");
            Assert.False(backend.Answers[^1].ResponseUpdate);
            string value = Assert.Single(backend.Answers[^1].InputsDialog.InputItems).Value;
            using JsonDocument references = JsonDocument.Parse(value);
            JsonElement reference = Assert.Single(references.RootElement.EnumerateArray());
            Assert.Equal("current-file-id", reference.GetProperty("Id").GetString());
            Assert.Equal(Path.GetFileName(project.ProjectFile), reference.GetProperty("Name").GetString());
            Assert.DoesNotContain("stale-file-id", backend.Answers[^1].ToString(), StringComparison.Ordinal);
            CompleteInteraction(backend, window, 3);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }

        static WatchInteractionsResponseUpdate FileForm() => new()
        {
            InteractionId = 3,
            Title = "Upload API settings",
            InputsDialog = new InteractionInputsDialog
            {
                InputItems = { new InteractionInput { Name = "attachment", Label = "Settings file", InputType = InputType.File } },
            },
        };
    }

    /// <summary>Discover asks the window's aspire CLI (a stand-in's, <see cref="MainWindow.AspireCliFrom"/>) its version first:
    /// within 13.x from 13.6.0, any patch, it lists the AppHosts the CLI finds; below it, or another major, it says why and
    /// lists none.</summary>
    [AvaloniaFact]
    public void Discover_takes_a_CLI_of_13_6_or_later_within_13_and_says_why_it_will_not_take_another()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in aspire CLI is a shell script, and Windows runs no shell script as a program");
        project.Reset();
        string cli = StandInCli();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(cli)!, "ps.json"), JsonSerializer.Serialize(new[]
        {
            new { appHostPath = PathNames.Canonical(project.ProjectFile), appHostPid = Environment.ProcessId, status = "Running" },
        }));
        MainWindow window = OpenWithCli(cli);
        try
        {
            foreach ((string version, string? refusal) in ((string, string?)[])[("13.6.0", null), ("13.6.3", null), ("13.7.0", null),
                ("13.5.4", "below the minimum supported version 13.6.0"), ("14.0.0", "uses an unverified protocol major")])
            {
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(cli)!, "version"), version + "\n");
                FindAction(window, "Discover Aspire AppHosts");
                Ui.WaitFor(() => refusal is null
                    ? window.Aspire.Apps.Any(app => PathNames.Same(app.AppHostPath, PathNames.Canonical(project.ProjectFile)))
                    : window.Aspire.Status.Contains(refusal, StringComparison.Ordinal) && window.AspireWindow.AppSelector.Items.Count == 0,
                    $"Discover with aspire {version} ({window.Aspire.Status})");
            }
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>An AppHost the window did not start is read through the CLI: its addresses shown without the credentials or
    /// query keys they carried, one that is no web address not offered at all, and the CLI's failure said with its secrets
    /// masked.</summary>
    [AvaloniaFact]
    public void An_AppHost_read_through_the_CLI_shows_its_addresses_without_credentials_and_its_failure_masked()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in aspire CLI is a shell script, and Windows runs no shell script as a program");
        project.Reset();
        string cli = StandInCli();
        string folder = Path.GetDirectoryName(cli)!;
        File.WriteAllText(Path.Combine(folder, "version"), "13.6.3\n");
        File.WriteAllText(Path.Combine(folder, "ps.json"), JsonSerializer.Serialize(new[]
        {
            new { appHostPath = PathNames.Canonical(project.ProjectFile), appHostPid = Environment.ProcessId, status = "Running" },
        }));
        File.WriteAllText(Path.Combine(folder, "resources.json"), JsonSerializer.Serialize(new
        {
            resources = new[]
            {
                new
                {
                    name = "api",
                    displayName = "api",
                    state = "Running",
                    healthStatus = "Healthy",
                    properties = new { },
                    environment = new { },
                    commands = new { },
                    urls = new[] { new { url = "http://user:password@localhost:8080/?api_key=private&view=1" }, new { url = "javascript:alert(1)" } },
                },
            },
        }));
        MainWindow window = OpenWithCli(cli);
        try
        {
            FindAction(window, "Discover Aspire AppHosts");
            Ui.WaitFor(() => window.AspireWindow.AppSelector.Items.Count == 1, $"the CLI's AppHost ({window.Aspire.Status})");
            ComboBox chooser = window.AspireWindow.AppSelector;
            Ui.Click(chooser);
            Ui.Press(window, "HOME");
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Aspire.ResourceSnapshots.Any(resource => resource.DisplayName == "api"), $"the CLI's resource ({window.Aspire.Status})");
            FindAction(window, "Show Aspire Resources");
            string[] Tips() => [.. window.AspireWindow.ResourceList.GetVisualDescendants().OfType<Button>().Select(button => ToolTip.GetTip(button) as string).OfType<string>()];
            Ui.WaitFor(() => Tips().Any(tip => tip.TrimEnd('/') == "http://localhost:8080/?view=1"), $"the resource's address as a link ({string.Join(", ", Tips())})");
            string[] tips = Tips();
            Assert.DoesNotContain(tips, tip => tip.Contains("password", StringComparison.Ordinal) || tip.Contains("private", StringComparison.Ordinal)
                || tip.StartsWith("javascript", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("password", window.Aspire.ResourceSnapshots.Single().Data.GetRawText(), StringComparison.Ordinal);

            File.WriteAllText(Path.Combine(folder, "failure"), "Configuration failed: Password=synthetic-password; Authorization=Bearer synthetic-token");
            FindAction(window, "Refresh Aspire Resources");
            Ui.WaitFor(() => window.Aspire.Status.Contains("Configuration failed", StringComparison.Ordinal), $"the CLI's failure said ({window.Aspire.Status})");
            Assert.DoesNotContain("synthetic-password", window.Aspire.Status, StringComparison.Ordinal);
            Assert.DoesNotContain("synthetic-token", window.Aspire.Status, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A stand-in aspire CLI in a folder of the test's: its version, ps and describe answers files beside it, and a
    /// failure file, when there is one, its error and exit code 9.</summary>
    private static string StandInCli()
    {
        string folder = TestFolders.Create("aspire-cli-");
        string cli = Path.Combine(folder, "aspire");
        File.WriteAllText(cli, "#!/bin/sh\nhere=$(dirname \"$0\")\nif [ \"$1\" = --version ]; then cat \"$here/version\"; exit 0; fi\n"
            + "if [ \"$1\" = ps ]; then cat \"$here/ps.json\"; exit 0; fi\n"
            + "if [ -f \"$here/failure\" ]; then cat \"$here/failure\" >&2; exit 9; fi\ncat \"$here/resources.json\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(cli, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return cli;
    }

    /// <summary>A window on the stand-in AppHost's folder that runs the given aspire CLI for AppHosts it did not start; no C#
    /// language server.</summary>
    private MainWindow OpenWithCli(string cli)
    {
        MainWindow window = new() { Width = 1200, Height = 800, Autosaves = false, WarmServer = project.Server, StartsLanguageServer = false, AspireCliFrom = cli };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        return window;
    }

    /// <summary>A folder's projects kept by a Passenger from before evaluations said which are AppHosts (the cache's version 5,
    /// whose entries say nothing of it) are evaluated again as the folder opens, so its AppHost has its Aspire configuration,
    /// rather than shown as a plain program from the cache until its project changes.</summary>
    [AvaloniaFact]
    public void An_AppHost_an_older_Passenger_kept_as_a_plain_project_opens_with_its_Aspire_configuration()
    {
        project.Reset();
        string state = TestFolders.Create("aspire-state-");
        try
        {
            MainWindow first = Keeping(state);
            try
            {
                Ui.WaitFor(() => first.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration),
                    "the stand-in AppHost's Aspire configuration");
            }
            finally
            {
                first.Close();
            }
            string cache = Directory.EnumerateFiles(state, "projectModels.json", SearchOption.AllDirectories).Single();
            JsonObject kept = (JsonObject)JsonNode.Parse(File.ReadAllText(cache))!;
            kept["version"] = 5;
            int told = 0;
            foreach ((string _, JsonNode? entry) in (JsonObject)kept["projects"]!)
            {
                told += ((JsonObject)entry!).Remove("isAspireHost") ? 1 : 0;
            }
            Assert.True(told > 0, $"The kept evaluations never said which project is an AppHost: {File.ReadAllText(cache)[..Math.Min(2000, new FileInfo(cache).Length is var length ? (int)length : 0)]}");
            File.WriteAllText(cache, kept.ToJsonString());

            MainWindow second = Keeping(state);
            try
            {
                Assert.True(second.Session.Models.Evaluations > 0,
                    "A folder whose kept evaluations say nothing of AppHosts was shown from them, its AppHost a plain program until it changes");
                Ui.WaitFor(() => second.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration),
                    "the AppHost's Aspire configuration after the older Passenger's evaluations");
            }
            finally
            {
                second.Close();
            }
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>A window on the stand-in AppHost's folder that keeps its state, the folder's projects' evaluations among it, in a
    /// state folder of the test's, once every project is evaluated; no C# language server.</summary>
    private MainWindow Keeping(string state)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(project.Folder);
        Ui.WaitFor(() => window.Session.Models.IsComplete, "every project's evaluation");
        Ui.Settle();
        return window;
    }

    /// <summary>The container engine stopping while an AppHost runs (Docker Desktop quit) is said where a container run
    /// says it, in the same words: the status bar, whose click starts Docker Desktop where it is installed (never
    /// clicked here), and Problems' Containers tab, which empties as the engine comes back; an engine with no Compose
    /// serves the run, which needs none. A stand-in engine stops and starts, never Docker Desktop.</summary>
    [AvaloniaFact]
    public void A_container_engine_that_stops_while_an_AppHost_runs_is_said_in_the_status_bar_and_Problems_as_for_a_container_run()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        project.Reset();
        (string cli, string state) = ContainersTests.StandInEngine(TestFolders.Create("aspire-engine-"), up: true);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        project.Serve(backend);
        MainWindow window = new()
        {
            Width = 1200, Height = 800, Autosaves = false, WarmServer = project.Server, StartsLanguageServer = false,
            Engine = new Passenger.App.Containers.ContainerEngine(cli: cli),
        };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration),
                "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1,
                $"the window to connect to the stand-in AppHost's resource service ({window.Status})");
            Assert.Empty(window.BottomPanel.Problems.ContainerProblems);

            File.WriteAllText(state, "down");
            const string Down = "Docker isn't running (context stand-in)";
            Ui.WaitFor(() => window.BottomPanel.Problems.ContainerProblems.Any(problem => problem.Message.StartsWith(Down, StringComparison.Ordinal)),
                "the stopped engine listed in Problems' Containers tab", TimeSpan.FromMinutes(1));
            Assert.StartsWith(Down, window.Status, StringComparison.Ordinal);
            Assert.Equal(Passenger.App.Containers.EngineState.DesktopApp is null ? "see it in Problems, Containers" : "start Docker Desktop", window.StatusPlace?.Name);
            Assert.Single(window.BottomPanel.Problems.ContainerProblems);

            File.WriteAllText(state, "up");
            Ui.WaitFor(() => window.BottomPanel.Problems.ContainerProblems.Count == 0, "Problems' Containers tab emptied as the engine came back",
                TimeSpan.FromMinutes(1));
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>The Containers window's AppHost row has a merged Log and Stats over its containers: their lines each
    /// under its container's name, a toggle each, one turned off leaving its lines out, and each container's numbers, a
    /// reading changing its line in place. A stand-in engine lists the two containers the stand-in AppHost's resources
    /// name (kept ones, which the run never takes down), their logs and stats what it echoes.</summary>
    [AvaloniaFact]
    public void The_Containers_windows_AppHost_row_merges_its_containers_logs_and_stats()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The stand-in engine is a shell script");
        project.Reset();
        const string Cache = "5a5d0c0ffee6";
        const string Queue = "5a5d0c0ffee7";
        static string Listed(string id, string name) =>
            $$"""{"ID":"{{id}}","Names":"{{name}}","Image":"redis:7","State":"running","Status":"Up 1 minute","Ports":"","Labels":"com.microsoft.developer.usvc-dev.uid=x"}""";
        static string Numbers(string id, string name, string cpu = "1.50%") =>
            $$"""{"Container":"{{id}}","Name":"{{name}}","CPUPerc":"{{cpu}}","MemPerc":"2.00%","MemUsage":"10MiB / 1GiB","NetIO":"1kB / 2kB","BlockIO":"0B / 0B","PIDs":"3"}""";
        (string cli, _) = ContainersTests.StandInEngine(TestFolders.Create("aspire-host-log-"), up: true,
            logs: "for word in \"$@\"; do last=$word; done; echo \"hello from $last\"; sleep 600",
            stats: $"echo '{Numbers(Cache, "cache-abc")}'; echo '{Numbers(Queue, "queue-def")}'; sleep 1; echo '{Numbers(Cache, "cache-abc", "4.25%")}'; sleep 600",
            more: [Listed(Cache, "cache-abc"), Listed(Queue, "queue-def")]);
        AspireInteractionHost backend = Ui.Await(AspireInteractionHost.StartAsync(project.KeyFile), "the stand-in resource service");
        foreach ((string name, string id) in (ReadOnlySpan<(string, string)>)[("cache-abc", Cache), ("queue-def", Queue)])
        {
            backend.PushResource(new Resource
            {
                Name = name, DisplayName = name[..name.IndexOf('-')], Uid = name, ResourceType = "Container", State = "Running",
                Properties =
                {
                    new ResourceProperty { Name = "container.id", Value = Google.Protobuf.WellKnownTypes.Value.ForString(id + new string('0', 52)) },
                    new ResourceProperty { Name = "container.lifetime", Value = Google.Protobuf.WellKnownTypes.Value.ForString("Persistent") },
                },
            });
        }
        project.Serve(backend);
        MainWindow window = new()
        {
            Width = 1200, Height = 800, Autosaves = false, WarmServer = project.Server, StartsLanguageServer = false,
            Engine = new Passenger.App.Containers.ContainerEngine(cli: cli),
        };
        window.Show();
        window.OpenFolder(project.Folder);
        Ui.Settle();
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration), "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1, $"the stand-in AppHost's start ({window.Status})");
            window.ShowToolWindow(ToolWindowIds.Containers);
            Ui.Settle();
            Passenger.App.Containers.ContainersView view = window.ContainersWindow;
            Ui.WaitFor(() => view.Rows.Any(row => row.Kind == Passenger.App.Containers.ContainerRowKind.AppHost && row.Containers.Count == 2),
                $"the AppHost's row over its two containers, not [{string.Join(", ", view.Rows.Select(row => $"{row.Kind} {row.Label}"))}]");
            Ui.Click(view.GetVisualDescendants().OfType<Passenger.App.ToolWindows.RunRowView>().Single(row => row.Shown?.Label == StandInAppHost.Configuration)
                .GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == StandInAppHost.Configuration));
            Assert.Equal(Passenger.App.Containers.ContainerRowKind.AppHost, view.Chosen?.Kind);

            Assert.Equal("Log", view.ShownTab);
            string Shown()
            {
                view.Log.Console.Flush();
                return view.Log.Console.Document.Snapshot.ToString();
            }
            Ui.WaitFor(() => Shown().Contains($"[cache-abc] hello from {Cache}", StringComparison.Ordinal)
                && Shown().Contains($"[queue-def] hello from {Queue}", StringComparison.Ordinal), $"both containers' lines merged, not [{Shown()}]");
            Assert.Equal(["cache-abc", "queue-def"], view.Log.ServiceToggles.Select(toggle => (string?)toggle.Content));
            Ui.Click(view.Log.ServiceToggles.Single(toggle => Equals(toggle.Content, "queue-def")));
            Ui.WaitFor(() => Shown().Contains($"[cache-abc] hello from {Cache}", StringComparison.Ordinal) && !Shown().Contains("[queue-def]", StringComparison.Ordinal),
                $"the queue's lines left out once its toggle is off, not [{Shown()}]");

            Ui.Click(view.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(tab => tab.Name == "containersStatsTab"));
            Ui.WaitFor(() => view.Stats.Latest.Count == 2, $"both containers' numbers, not [{string.Join(", ", view.Stats.Latest.Select(sample => sample.Name))}] ({view.Stats.Said})");
            Assert.Equal(["cache-abc", "queue-def"], view.Stats.Latest.Select(sample => sample.Name).Order(StringComparer.Ordinal));
            Grid cache = (Grid)view.Stats.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "cache-abc").Parent!;
            Ui.WaitFor(() => view.Stats.Latest.Any(sample => sample.Name == "cache-abc" && sample.Cpu == 4.25), "the cache's second reading");
            Assert.Contains(cache.Children.OfType<TextBlock>(), cell => cell.Text == "4.25%");
            Assert.Same(cache, view.Stats.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "cache-abc").Parent);
            Assert.Empty(backend.Errors);
        }
        finally
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
        }
    }

    /// <summary>A window on the stand-in AppHost, run from its Aspire configuration as a person runs it, once the
    /// window has connected to the stand-in resource service its launch profile names; no C# language server, which
    /// nothing here asks.</summary>
    private MainWindow OpenRunning(AspireInteractionHost backend, Uri? dashboard = null)
    {
        if (dashboard is null)
        {
            project.Serve(backend);
        }
        else
        {
            project.Serve(backend, dashboard);
        }
        MainWindow window = project.Open(project.Program, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(configuration => configuration.Name == StandInAppHost.Configuration),
                "the stand-in AppHost's Aspire configuration");
            Assert.True(window.SelectConfiguration(StandInAppHost.Configuration));
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => backend.Connected.Task.IsCompleted && window.AspireRuntime.Apps.Count == 1,
                $"the window to connect to the stand-in AppHost's resource service ({window.Status})");
            Assert.True(PathNames.Same(PathNames.Canonical(project.ProjectFile), Assert.Single(window.AspireRuntime.Apps).Configuration.Project));
            return window;
        }
        catch
        {
            window.Close();
            Ui.Await(Complete(backend.DisposeAsync().AsTask()), "the stand-in resource service to stop");
            throw;
        }
    }

    private static string Console(MainWindow window) => window.AspireWindow.Console.Document.Snapshot.ToString();

    /// <summary>Chooses a file for the prompt's file input as a person does in the window's file dialog, which the test
    /// answers for (<see cref="MainWindow.PickFiles"/>): one file, asked for by the input's label.</summary>
    private static void PickUploadFile(MainWindow window, AspireInteractionDialog dialog, string path)
    {
        List<(string Title, bool Several)> asked = [];
        window.PickFiles = (title, _, several) =>
        {
            asked.Add((title, several));
            return Task.FromResult<IReadOnlyList<string>>([path]);
        };
        Ui.Click(dialog.InputControls["attachment"], settle: false);
        Ui.PumpUntil(() => asked.Count == 1, "the file input to ask for a file");
        Assert.Equal(("Settings file", false), asked[0]);
    }

    internal static AspireInteractionDialog Interaction(MainWindow window, string title)
    {
        Ui.WaitFor(() => window.OwnedWindows.OfType<AspireInteractionDialog>().Any(dialog => dialog.IsVisible && dialog.Title?.EndsWith(" · " + title, StringComparison.Ordinal) == true),
            $"the AppHost's {title} prompt");
        AspireInteractionDialog dialog = Assert.Single(window.OwnedWindows.OfType<AspireInteractionDialog>(), dialog => dialog.Title?.EndsWith(" · " + title, StringComparison.Ordinal) == true);
        Assert.Same(window, dialog.Owner);
        return DialogButtonsTests.Check(dialog);
    }

    private static void CompleteInteraction(AspireInteractionHost backend, MainWindow window, int id)
    {
        backend.Push(new WatchInteractionsResponseUpdate { InteractionId = id, Complete = new InteractionComplete() });
        Ui.WaitFor(() => !window.OwnedWindows.OfType<AspireInteractionDialog>().Any(dialog => dialog.InteractionId == id),
            "the prompt the AppHost completed to close");
    }

    /// <summary>Select All (the headless platform's, Ctrl+A, on every system) and typing, pressed without waiting for the window
    /// to settle, since a held answer holds it.</summary>
    private static void ReplaceDuringWork(TopLevel window, string text)
    {
        window.KeyPress(Avalonia.Input.Key.A, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
        window.KeyRelease(Avalonia.Input.Key.A, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.None, null);
        window.KeyTextInput(text);
    }

    private static void ClickConsoleLink(MainWindow window, string text)
    {
        Passenger.Editor.CodeEditor editor = window.AspireWindow.Console.Editor;
        int offset = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"The Aspire console has no {text} link");
        int line = editor.Document.Snapshot.GetLineFromOffset(offset);
        editor.ScrollTo(new Vector(0, Math.Max(0, line * editor.LineHeight - editor.Bounds.Height / 3)));
        Ui.Settle();
        Point at = editor.PointOf(offset) + new Vector(2, editor.LineHeight / 2);
        window.MouseMove(editor.TranslatePoint(at, window)!.Value);
        Ui.WaitFor(() => editor.HoveredLink is { } link && link.Range.Start <= offset && offset < link.Range.End,
            $"the console's link for {text} under the pointer");
        Ui.Click(editor, at);
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

    private static WatchInteractionsResponseUpdate Form(string value) => new()
    {
        InteractionId = 2,
        Title = "API settings",
        InputsDialog = new InteractionInputsDialog
        {
            InputItems = { new InteractionInput { Name = "value", Label = "Value", InputType = InputType.Text, Value = value, UpdateStateOnChange = true } },
        },
    };

    private static async Task<bool> Complete(Task task)
    {
        await task;
        return true;
    }
}

/// <summary>A stand-in AppHost: a console project the IDE runs as one (it sets IsAspireHost), which writes the resource service
/// key the IDE gave it where its stand-in resource service reads it (<see cref="AspireInteractionHost.KeyFile"/>) and waits to
/// be stopped; its launch profile, written for each test, names that stand-in's address.</summary>
public sealed class StandInAppHost : SharedProject
{
    public const string Name = "StandIn.AppHost";
    public const string Configuration = $"Aspire: {Name}: http";

    public StandInAppHost() : base("aspire-stand-in-")
    {
        // The version the AppHost SDK would set, without which the .NET SDK takes the project for a workload-era AppHost.
        TestFolders.Write(Folder, $"{Name}/{Name}.csproj", TestProjects.Console("<IsAspireHost>true</IsAspireHost>",
            $"<AspireHostingSDKVersion>{AspireApp.TemplatesVersion}</AspireHostingSDKVersion>"));
        TestFolders.Write(Folder, $"{Name}/Program.cs", """
            using System;
            using System.IO;
            using System.Threading;

            string keyFile = Environment.GetEnvironmentVariable("PASSENGER_STAND_IN_KEY")!;
            File.WriteAllText(keyFile + ".urls", $"{Environment.GetEnvironmentVariable("ASPNETCORE_URLS")}\n{Environment.GetEnvironmentVariable("ASPIRE_ALLOW_UNSECURED_TRANSPORT")}");
            File.WriteAllText(keyFile + ".dashboard", Environment.GetEnvironmentVariable("AppHost__DashboardApiKey"));
            File.WriteAllText(keyFile + ".session", $"{Environment.GetEnvironmentVariable("DEBUG_SESSION_PORT")}\n{Environment.GetEnvironmentVariable("DEBUG_SESSION_TOKEN")}\n{Environment.GetEnvironmentVariable("DEBUG_SESSION_INFO")}");
            File.WriteAllText(keyFile + ".part", Environment.GetEnvironmentVariable("ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY"));
            File.Move(keyFile + ".part", keyFile, overwrite: true);
            Console.WriteLine("The stand-in AppHost is up");
            Thread.Sleep(Timeout.Infinite);

            """);
        TestFolders.Write(Folder, $"{Name}/Properties/launchSettings.json", Profile(null));
        TestFolders.Write(Folder, $"{Api}/{Api}.csproj", TestProjects.Console());
        TestFolders.Write(Folder, $"{Api}/Program.cs", """
            using System;
            using System.Diagnostics;
            using System.IO;
            using System.Threading;

            if (args is ["child"])
            {
                Console.WriteLine("child ran");
                return;
            }
            if (Environment.GetEnvironmentVariable("STAND_IN_CHILD") == "1")
            {
                ProcessStartInfo start = new(Environment.ProcessPath!) { RedirectStandardOutput = true };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                {
                    start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
                }
                start.ArgumentList.Add("child");
                using Process child = Process.Start(start)!;
                Console.WriteLine($"child said: {child.StandardOutput.ReadToEnd().Trim()}");
            }
            Console.WriteLine("The stand-in API is up");
            Thread.Sleep(Timeout.Infinite);

            """);
        TestFolders.Write(Folder, "StandIn.slnx", $"<Solution>\n  <Project Path=\"{Name}/{Name}.csproj\" />\n  <Project Path=\"{Api}/{Api}.csproj\" />\n</Solution>\n");
        Prepare();
    }

    public string ProjectFile => PathOf($"{Name}/{Name}.csproj");

    /// <summary>A service the stand-in AppHost's orchestrator would start, which a test asks the IDE to start by the execution
    /// protocol, as DCP does: a console app that says it is up and waits, with STAND_IN_CHILD=1 having first run itself as a
    /// child and said what the child wrote.</summary>
    public const string Api = "StandIn.Api";

    public string ApiProject => PathOf($"{Api}/{Api}.csproj");

    /// <summary>Builds the stand-in's service, which the IDE starts as it is built, by its real path, as the IDE's
    /// builds of it (a restart's) name it: a build through the run folder's link (macOS's TMPDIR, under /var) after one
    /// of the IDE's would have MSBuild's IncrementalClean delete the runtimeconfig.json and deps.json the IDE's
    /// wrote.</summary>
    internal void BuildApi()
    {
        ChildResult result = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", PathNames.Canonical(ApiProject), "-nologo", "-v:q"]),
            limit: TimeSpan.FromMinutes(3).Scaled()), "the stand-in API's build");
        Assert.True(result.Succeeded, $"The stand-in API did not build: {result.Output}{result.Error}");
    }

    /// <summary>Asks the IDE to start the stand-in's service as a resource of that name, as DCP starts a project resource: the
    /// sessions' notifications subscribed to first (held while the socket given back is open), then a run session asked of
    /// the execution endpoint the IDE gave the AppHost; with the IDE's answer.</summary>
    internal (System.Net.WebSockets.ClientWebSocket Notified, System.Net.HttpStatusCode Answer) AskToStartApi(string resource, string mode = "NoDebug",
        IReadOnlyDictionary<string, string>? environment = null, string? projectPath = null) =>
        Ui.Await(Task.Run(() => StartApiAsync(resource, mode, environment, projectPath: projectPath)), "the IDE's answer to the run session");

    /// <summary>Writes tests/Debuggees/BindingErrorsApp into the stand-in's folder, restored from the run's feed and built by
    /// its real path, for the AppHost to start as a service of its own: an Avalonia app whose caption's binding fails. Its
    /// project's path; the next test's <see cref="SharedProject.Reset"/> takes it away.</summary>
    internal string WriteBindingsApp()
    {
        string folder = PathOf("Bindings");
        Directory.CreateDirectory(folder);
        string app = PathNames.Canonical(AvaloniaApp.Debuggee(folder, "BindingErrorsApp"));
        AvaloniaApp.UseFeed(folder);
        Restore.Run(app);
        ChildResult result = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", app, "-nologo", "-v:q"]),
            limit: TimeSpan.FromMinutes(4).Scaled()), "the Avalonia service's build");
        Assert.True(result.Succeeded, $"The Avalonia service did not build: {result.Output}{result.Error}");
        return app;
    }

    /// <summary><see cref="AskToStartApi"/> off the window's thread, as DCP asks again for a resource its AppHost restarts:
    /// the session's mode as DCP gives it, and variables beside the resource's name; subscribed to the sessions' notifications
    /// unless <paramref name="subscribe"/> is false (DCP subscribes once, which a second refuses). The IDE's answer is no
    /// status at all when it closed its endpoint as it stopped the AppHost, the request unanswered.</summary>
    internal async Task<(System.Net.WebSockets.ClientWebSocket Notified, System.Net.HttpStatusCode Answer)> StartApiAsync(string resource, string mode = "NoDebug",
        IReadOnlyDictionary<string, string>? environment = null, bool subscribe = true, string? projectPath = null)
    {
        string[] session = (await File.ReadAllTextAsync(SessionFile)).Split('\n', 3);
        using JsonDocument info = JsonDocument.Parse(session[2]);
        string version = info.RootElement.GetProperty("protocols_supported")[0].GetString()!;
        const string Instance = "standin-tests-abcdefghijkl";
        System.Net.WebSockets.ClientWebSocket notified = new();
        notified.Options.SetRequestHeader("Authorization", "Bearer " + session[1]);
        notified.Options.SetRequestHeader("Microsoft-Developer-DCP-Instance-ID", Instance);
        if (subscribe)
        {
            await notified.ConnectAsync(new Uri($"ws://{session[0]}/run_session/notify?api-version={version}"), CancellationToken.None);
        }
        using HttpClient http = new();
        using HttpRequestMessage start = new(HttpMethod.Put, $"http://{session[0]}/run_session?api-version={version}")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                launch_configurations = new[] { new { type = "project", project_path = projectPath ?? ApiProject, mode } },
                env = new[] { new { name = "OTEL_SERVICE_NAME", value = resource } }
                    .Concat((environment ?? new Dictionary<string, string>()).Select(variable => new { name = variable.Key, value = variable.Value })).ToArray(),
                args = Array.Empty<string>(),
            }), System.Text.Encoding.UTF8, "application/json"),
        };
        start.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session[1]);
        start.Headers.Add("Microsoft-Developer-DCP-Instance-ID", Instance);
        try
        {
            using HttpResponseMessage answered = await http.SendAsync(start);
            return (notified, answered.StatusCode);
        }
        catch (HttpRequestException)
        {
            return (notified, 0);
        }
    }

    /// <summary>The stand-in AppHost's configuration with its service, apiservice, marked Don't start.</summary>
    internal RunConfiguration DontStartApi(RunConfiguration generated) => generated with
    {
        Name = "Stand-in, API not started",
        Generated = false,
        ServiceActions = new Dictionary<string, ServiceStart> { ["apiservice"] = ServiceStart.DontStart },
        ServiceProjects = new Dictionary<string, string> { ["apiservice"] = PathNames.Canonical(ApiProject) },
    };

    /// <summary>The service as DCP says it once the IDE refused its start.</summary>
    internal Resource Refused() => new()
    {
        Name = "apiservice", DisplayName = "apiservice", Uid = "api", ResourceType = "Project", State = "FailedToStart",
        Properties = { new ResourceProperty { Name = "project.path", Value = Google.Protobuf.WellKnownTypes.Value.ForString(PathNames.Canonical(ApiProject)) } },
    };

    /// <summary>Where the stand-in AppHost writes, before its key, the IDE's execution endpoint (DEBUG_SESSION_PORT), its token
    /// and its session's info, a line each, as DCP reads them.</summary>
    public string SessionFile => KeyFile + ".session";

    public string Program => PathOf($"{Name}/Program.cs");

    public string KeyFile => PathOf("resource-service.key");

    /// <summary>Where the stand-in AppHost writes, as it starts, the addresses it was told to serve on (ASPNETCORE_URLS) and
    /// whether it may serve them unsecured (ASPIRE_ALLOW_UNSECURED_TRANSPORT), a line each, before its key.</summary>
    public string UrlsFile => KeyFile + ".urls";

    /// <summary>Where the stand-in AppHost writes, before its key, the dashboard key the IDE gave it, which the IDE's telemetry
    /// requests carry.</summary>
    public string DashboardKeyFile => KeyFile + ".dashboard";

    /// <summary>The AppHost's launch profile, naming the stand-in resource service's address, as an AppHost's names its own;
    /// with <paramref name="https"/>, its dashboard served there as an https profile serves it, with no unsecured transport.</summary>
    internal void Serve(AspireInteractionHost backend, string? https = null) => Write($"{Name}/Properties/launchSettings.json", Profile(backend.Endpoint, https));

    /// <summary>The launch profile of <see cref="Serve(AspireInteractionHost, string?)"/>, the AppHost's own address
    /// (applicationUrl, where the IDE finds its dashboard) the stand-in dashboard's.</summary>
    internal void Serve(AspireInteractionHost backend, Uri dashboard) =>
        Write($"{Name}/Properties/launchSettings.json", Profile(backend.Endpoint, applicationUrl: dashboard.AbsoluteUri.TrimEnd('/')));

    private string Profile(Uri? resourceService, string? https = null, string? applicationUrl = null)
    {
        Dictionary<string, string> environment = new()
        {
            ["PASSENGER_STAND_IN_KEY"] = KeyFile,
        };
        if (https is null)
        {
            environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
        }
        else
        {
            environment["ASPNETCORE_URLS"] = https;
        }
        if (resourceService is not null)
        {
            environment["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = resourceService.AbsoluteUri;
        }
        return applicationUrl is null
            ? JsonSerializer.Serialize(new { profiles = new { http = new { commandName = "Project", environmentVariables = environment } } })
            : JsonSerializer.Serialize(new { profiles = new { http = new { commandName = "Project", applicationUrl, environmentVariables = environment } } });
    }
}
