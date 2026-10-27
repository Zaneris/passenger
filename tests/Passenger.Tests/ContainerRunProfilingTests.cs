using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Profiling;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Profiler;

namespace Passenger.Tests;

/// <summary>Profile of a container run configuration as a person uses it, against a real engine: ContainerRunsTests'
/// Compose solution, its web service marked Debug and so the one profiled, held at its start until Passenger's tools record
/// it in its container, the rest running as Run runs them; a memory snapshot taken there; Get Snapshot naming its hot method
/// at its line with the run going on; Attach Profiler recording it again as it runs, and Stop reading that snapshot before it
/// takes the containers down. Skips, saying why, where no Linux engine runs; the tools are published for the engine's
/// processor where a dev build finds them.</summary>
[Collection(Alone.Name)]
public sealed class ContainerRunProfilingTests(ContainerRunDebuggingTests.Tools tools) : IClassFixture<ContainerRunDebuggingTests.Tools>
{
    /// <summary>web's Program.cs: a thread spinning in a hot method and keeping what it makes, from its first moment.</summary>
    public const string Spinning = """
        using System.Runtime.CompilerServices;

        WebApplication app = WebApplication.Create(args);
        new Thread(() =>
        {
            while (true)
            {
                Hot.Spin();
                Kept.Grow();
                Thread.Sleep(10);
            }
        }) { IsBackground = true }.Start();
        app.MapGet("/", () => Message.Text);
        app.Run();

        static class Hot
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static double Spin()
            {
                double total = 0;
                for (int i = 1; i < 10_000_000; i++)
                {
                    total += Math.Sqrt(i);
                }
                return total;
            }
        }

        static class Kept
        {
            private static readonly List<Keepsake> All = [];

            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Grow()
            {
                for (int i = 0; i < 10; i++)
                {
                    All.Add(new Keepsake(All.Count));
                }
            }
        }

        sealed class Keepsake(int number)
        {
            public int Number { get; } = number;
        }

        """;

    /// <summary>The line (from one) of the hot method's first sequence point, its opening brace.</summary>
    public static int SpinLine => Array.FindIndex(Spinning.Split('\n'), l => l.Contains("public static double Spin()", StringComparison.Ordinal)) + 2;

    [AvaloniaFact]
    public void Profile_of_a_compose_configuration_records_the_service_marked_from_its_start_takes_a_memory_snapshot_there_and_Stop_reads_it_first()
    {
        Assert.SkipWhen(tools.WhyNot is not null, tools.WhyNot ?? string.Empty);
        (string folder, string project) = Shop("profile");
        MainWindow window = ContainerRunsTests.Open(folder);
        RunConfiguration marked = window.Configurations.First(c => c.Kind == RunKind.Compose).Copied() with
        {
            Name = "Shop, web profiled",
            ServiceActions = new Dictionary<string, ServiceStart> { ["web"] = ServiceStart.Debug },
            Generated = false,
        };
        window.SetConfigurations([marked], marked.Name);
        try
        {
            Ui.Launched(window, window.ProfileAsync(), "web to come up under the profiler", TimeSpan.FromMinutes(5));
            string web = ContainerRunsTests.Container(project, "web");
            // Compose names the container by its whole id, docker ps by its start.
            Assert.True(window.Profiling?.Place?.Where.StartsWith(web, StringComparison.Ordinal) == true,
                $"The profiler is not recording web's container {web}: {window.Profiling?.Place?.Where} / {window.Status} / {window.ProfilerView.Placeholder}");
            Assert.StartsWith("Profiling web of Shop, web profiled", window.Status, StringComparison.Ordinal);
            Assert.Equal(["api", "cache", "web"], ContainerRunsTests.States(project).Keys.Order(StringComparer.Ordinal));
            Assert.True(window.IsRunning, "The run's log is not followed in the Run window");
            int port = ContainerRunsTests.Published(project, "web");
            Assert.True(Ui.Pumped(() => ContainerRunsTests.Answer(port) == "web 1", TimeSpan.FromMinutes(1)), "web, let go by the profiler, did not answer");

            // Where only the runtime can write it, into the program's output (Linux's ptrace_scope), that is let.
            window.Confirm = (_, _, _) => Task.FromResult(true);
            Ui.ClickButton(window.ProfilerView, "Get Memory Snapshot");
            Ui.WaitFor(() => window.ProfilerView.Memory is not null || window.Status?.StartsWith("No memory snapshot", StringComparison.Ordinal) == true,
                "the memory snapshot of web", TimeSpan.FromMinutes(2));
            Assert.True(window.ProfilerView.Memory?.Types.Any(t => t.Name == "Keepsake" && t.Count > 0) == true, $"web's memory snapshot keeps no Keepsake: {window.Status}");

            SnapshotRead read = Ui.Await(window.SnapshotAsync(), "web's snapshot", TimeSpan.FromMinutes(2));
            Snapshot snapshot = read.Snapshot ?? throw new Xunit.Sdk.XunitException($"No snapshot of web: {read.Message}");
            Assert.True(window.IsRunning && window.Profiling is null, "The run did not go on after web's snapshot");
            HotSpot spin = Assert.Single(snapshot.HotSpots, h => h.Name.StartsWith("Hot.Spin", StringComparison.Ordinal));
            // A web app's idle threads wait in the runtime's own code: of web's own methods, the hot one leads.
            HotSpot[] own = [.. snapshot.HotSpots.Where(h => h.Module == spin.Module)];
            Assert.True(own[0] == spin, $"web's hot method does not lead its own ({spin.Module}): {string.Join(", ", own.Take(5).Select(h => $"{h.Name} {h.Self}"))}");
            MethodCode code = spin.Code ?? throw new Xunit.Sdk.XunitException("web's hot method has no module and token to find its source by");
            IReadOnlyDictionary<MethodCode, (string File, int Line)> sources = Ui.Await(MethodSources.FindAsync(snapshot, [code], CancellationToken.None), "the hot method's source");
            Assert.True(sources.TryGetValue(code, out (string File, int Line) at), $"web's hot method has no source in the PDB in its container: {Asked(snapshot, code)}");
            Assert.Equal((PathNames.Canonical(Path.Combine(folder, "Web", "Program.cs")), SpinLine), (PathNames.Canonical(at.File), at.Line));

            Assert.True(window.Perform(AppActions.AttachProfiler), "Attach Profiler could not run");
            Ui.WaitFor(() => window.Profiling is { Place: not null } || window.ProfilerView.Placeholder.StartsWith("Could not", StringComparison.Ordinal),
                "Attach Profiler to record web as it runs", TimeSpan.FromMinutes(2));
            Assert.True(window.Profiling?.Place?.Where.StartsWith(web, StringComparison.Ordinal) == true, $"Attach Profiler did not record web: {window.Status}");
            Ui.Hold(TimeSpan.FromSeconds(1), "the profiler to sample web as it runs");
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => window.ProfilerView.Snapshot != snapshot || window.ProfilerView.Placeholder.Length > 0, "Stop to read web's second snapshot", TimeSpan.FromMinutes(2));
            Assert.True(window.ProfilerView.Snapshot is { } last && last != snapshot && last.HotSpots.Any(h => h.Name.StartsWith("Hot.Spin", StringComparison.Ordinal)),
                $"Stop read no snapshot of web as it ran: {window.ProfilerView.Placeholder} / {window.Status}");
        }
        finally
        {
            Docker.Stopped(window, project, "Stop to take every container down");
        }
        Ui.WaitFor(() => !window.IsRunning && window.Profiling is null, "the run and its profile to end with Stop");
        window.Close();
    }

    /// <summary>What the container's tools answer for a method's source, asked by hand, for a failure to say.</summary>
    private static string Asked(Snapshot snapshot, MethodCode code)
    {
        if (snapshot.Place is not { } place)
        {
            return "the snapshot names no container";
        }
        ChildResult answered = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(place.Tools(["sources"]), $"{code.Token}\t{code.ModuleFile}\n", TimeSpan.FromSeconds(30))),
            "the tools' sources in the container", TimeSpan.FromSeconds(30) + TestFolders.Spare);
        return $"asked {code.Token} in {code.ModuleFile}; exit {answered.ExitCode}: '{answered.Output}' '{answered.Error}'";
    }

    /// <summary>ContainerRunsTests' Compose solution, web spinning from its start.</summary>
    internal static (string Folder, string Project) Shop(string name)
    {
        (string folder, string project) = ContainerRunsTests.Shop($"prof{name}");
        TestFolders.Write(folder, "Web/Program.cs", Spinning);
        return (folder, project);
    }
}
