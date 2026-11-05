using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Aspire;
using Passenger.App.Containers;
using Passenger.App.Run;
using Passenger.Core;

namespace Passenger.App.Telemetry;

/// <summary>A project run's dashboard (its configuration's Collect logs and traces): <see cref="ContainerTelemetry"/>'s, the one
/// service of a Compose project of Passenger's own on the person's engine, labelled for the folder (<see cref="FolderContainers"/>,
/// <see cref="ContainerLabels"/>), so the folder's end, the watchdog and the next start after a crash take it down as they take
/// a container run's; its telemetry API and OTLP endpoints published on this machine's loopback alone, each asked with a key
/// of the run's. The start that makes it tells its program where to send its OpenTelemetry (<see cref="CollectAsync"/>); a
/// program the IDE ends takes it down with it, one that ends by itself leaves it kept (<see cref="ProgramEnded"/>), its records
/// read until Stop, the run's next start or the folder's end (<see cref="Dispose"/>). A UI-thread object of the session's.</summary>
public sealed class RunDashboard : ITelemetryRun, IDisposable
{
    private readonly ContainerEngine engine;
    private readonly FolderContainers containers;
    private readonly string? labelledFor;
    private TelemetrySource? source;
    private Task? stopping;

    private RunDashboard(string name, string project, IReadOnlyList<string> compose, ContainerEngine engine, FolderContainers containers, string? labelledFor)
    {
        Name = name;
        ProjectName = project;
        Compose = compose;
        this.engine = engine;
        this.containers = containers;
        this.labelledFor = labelledFor;
    }

    /// <summary>Its run's configuration's name.</summary>
    public string Name { get; }

    /// <summary>Its Compose project's name, by which the Containers window lists it.</summary>
    public string ProjectName { get; }

    /// <summary>The <c>compose -p NAME -f …</c> words every command of it takes.</summary>
    public IReadOnlyList<string> Compose { get; }

    /// <summary>The name its program's records go by: the program's own, where it sets one.</summary>
    public string Records { get; private set; } = string.Empty;

    public bool IsStopped => stopping is not null;

    /// <summary>Its telemetry API, while it runs.</summary>
    public TelemetrySource? Telemetry => IsStopped ? null : source;

    /// <summary>Taken down (or being), heard once.</summary>
    public event EventHandler? Stopped;

    /// <summary>Whether its program has ended by itself, the dashboard kept so its records can still be read.</summary>
    public bool Kept { get; private set; }

    /// <summary>Kept once its program ended by itself, heard once.</summary>
    public event EventHandler? KeptChanged;

    /// <summary>Its program has ended: by the IDE (<paramref name="stopped"/>: Stop, a Rerun), which takes it down too; else by
    /// itself, which keeps it, its records read on. True when kept.</summary>
    public bool ProgramEnded(bool stopped)
    {
        UiThread.Check();
        if (IsStopped || Kept)
        {
            return Kept;
        }
        if (stopped)
        {
            Dispose();
            return false;
        }
        Kept = true;
        KeptChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>What a run says of its kept dashboard as its program ends by itself.</summary>
    public string KeptSaid => $"{Name}'s logs and traces stay in its Telemetry tab until Stop or its next run";

    /// <summary>How long a dashboard come up may take to answer before its run goes on without it.</summary>
    private static readonly TimeSpan AnswerLimit = TimeSpan.FromSeconds(30);

    /// <summary>The plan of a project run that collects its logs and traces, with what it says of them (<see cref="LaunchPlan.Notes"/>):
    /// its program told to send them to a dashboard of its own, brought up on <paramref name="engine"/>, which the plan holds
    /// (<see cref="LaunchPlan.Dashboard"/>), unless the environment it runs in, what it inherits with its own over it, sends
    /// them all elsewhere, a signal sent elsewhere said so and each variable it sets kept; or, where no dashboard can come up,
    /// why, the plan run as it is. Canceled, what came up is taken down.</summary>
    public static async Task<LaunchPlan> CollectAsync(LaunchPlan plan, ContainerEngine engine, FolderContainers containers, string? folder, string stateFolder,
        CancellationToken cancel)
    {
        UiThread.Check();
        IReadOnlyDictionary<string, string> own = OtlpEnvironment.Effective(
            Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().Select(set => KeyValuePair.Create((string)set.Key, (string?)set.Value ?? string.Empty)),
            plan.Environment);
        if (OtlpEnvironment.NoneComes(own) is { Count: > 0 } elsewhere)
        {
            return plan with
            {
                Notes = [.. plan.Notes, $"{plan.Name} sends its logs and traces where its own {Plural.Listed(elsewhere)} {(elsewhere.Count == 1 ? "says" : "say")}, so Passenger's dashboard is not started for it"],
            };
        }
        (RunDashboard? dashboard, string? why) = await StartAsync(plan.Name, engine, containers, folder, stateFolder, cancel);
        if (dashboard is null)
        {
            return plan with { Notes = [.. plan.Notes, $"{plan.Name} runs without its logs and traces collected: {why}"] };
        }
        string named = Path.GetFileNameWithoutExtension(plan.Project ?? plan.ProgramName);
        OtlpEnvironment told = OtlpEnvironment.Of(dashboard.Grpc!, dashboard.Http!, dashboard.OtlpKey!, named, own)!;
        dashboard.Records = OtlpEnvironment.NameOf(named, own);
        Dictionary<string, string> environment = new(plan.Environment);
        foreach ((string variable, string value) in told.Told)
        {
            environment[variable] = value;
        }
        string sent = told.Elsewhere.Count == 0 ? "logs and traces" : Plural.Listed(told.Sent);
        List<string> notes = [.. plan.Notes, $"{plan.Name}'s {sent} go to Aspire's dashboard ({dashboard.ProjectName}), which the Containers window's "
            + "Telemetry tab and an agent's telemetry read"];
        if (told.Elsewhere.Count > 0)
        {
            notes.Add($"The rest of what {plan.Name} sends goes where its own {Plural.Listed(told.Elsewhere)} {(told.Elsewhere.Count == 1 ? "says" : "say")}, without the dashboard's key");
        }
        if (told.Kept.Count > 0)
        {
            notes.Add($"{plan.Name} keeps its own {Plural.Listed(told.Kept)}, as its environment sets");
        }
        return plan with { Environment = environment, Dashboard = dashboard, Notes = notes };
    }

    /// <summary>Its OTLP endpoints on this machine, over gRPC and over HTTP, and the key they take.</summary>
    private string? Grpc { get; set; }

    private string? Http { get; set; }

    private string? OtlpKey { get; set; }

    /// <summary>Brings a dashboard up for a run (<paramref name="name"/>) and waits until it answers: the dashboard, or null and
    /// why not (no Linux engine, one on another machine, an image that could not be pulled, ports unread, no answer).</summary>
    private static async Task<(RunDashboard? Dashboard, string? Why)> StartAsync(string name, ContainerEngine engine, FolderContainers containers, string? folder,
        string stateFolder, CancellationToken cancel)
    {
        EngineState state = await engine.StateAsync(cancel: cancel);
        if (!state.IsReady)
        {
            return (null, state.Problem ?? "the container engine is not ready");
        }
        if (ContainerTelemetry.Elsewhere(state) is { } elsewhere)
        {
            return (null, elsewhere);
        }
        ContainerTelemetry keys = ContainerTelemetry.New();
        string runsIn = folder is null ? Path.Combine(stateFolder, "containers") : ContainerPlans.StateOf(stateFolder, folder);
        // A name of its own each run: the take-down of the run before it, by its project's labels, never reaches this one.
        string project = $"passenger-dashboard-{ContainerPlans.Slug(name)}-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";
        string file = Path.Combine(runsIn, $"{ContainerPlans.Slug(name)}.dashboard.yaml");
        IReadOnlyList<string> labels = containers.Labels;
        // Its network carries the labels too, which its take-down finds it by.
        JsonObject written = new()
        {
            ["services"] = new JsonObject { [ContainerTelemetry.Service] = keys.Entry(ContainerPlans.Labels(labels), [], otlp: true) },
            ["networks"] = new JsonObject { ["default"] = new JsonObject { ["labels"] = ContainerPlans.Labels(labels) } },
        };
        try
        {
            Directory.CreateDirectory(runsIn);
            // It holds the dashboard's keys: the person's alone to read.
            OwnFiles.Write(file, written.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception unwritten) when (unwritten is IOException or UnauthorizedAccessException)
        {
            return (null, $"its dashboard's compose file could not be written: {unwritten.Message}");
        }
        RunDashboard dashboard = new(name, project, ["compose", "-p", project, "-f", file, "--project-directory", runsIn], engine, containers, folder)
        {
            OtlpKey = keys.OtlpKey,
        };
        containers.StartedProject(dashboard.Compose);
        try
        {
            ChildResult up = await engine.RunAsync([.. dashboard.Compose, "up", "--detach", "--wait", "--", ContainerTelemetry.Service], TimeSpan.FromMinutes(10), cancel, runsIn);
            if (!up.Succeeded)
            {
                return await dashboard.GoneAsync($"its dashboard did not come up: {ContainerEngine.Said(up, runsIn)}");
            }
            int[] targets = [ContainerTelemetry.ApiPort, ContainerTelemetry.OtlpPort, ContainerTelemetry.OtlpHttpPort];
            int?[] ports = new int?[targets.Length];
            string said = string.Empty;
            for (int i = 0; i < targets.Length; i++)
            {
                (ports[i], string answer) = await engine.PublishedPortAsync(dashboard.Compose, ContainerTelemetry.Service, targets[i], runsIn, cancel);
                said = ports[i] is null ? answer : said;
            }
            if (ports is not [{ } api, { } grpc, { } http])
            {
                return await dashboard.GoneAsync($"its dashboard's ports could not be read ({said})");
            }
            dashboard.source = new TelemetrySource(ContainerTelemetry.Loopback(api), keys.ApiKey);
            dashboard.Grpc = ContainerTelemetry.Loopback(grpc).ToString().TrimEnd('/');
            dashboard.Http = ContainerTelemetry.Loopback(http).ToString().TrimEnd('/');
            // The program starts once its endpoint listens: what it sends as it starts would be lost before.
            long began = Stopwatch.GetTimestamp();
            while (true)
            {
                try
                {
                    await AspireTelemetry.ReadLogsAsync(dashboard.source.Endpoint, dashboard.source.ApiKey, limit: 1, cancel: cancel);
                    return (dashboard, null);
                }
                catch (Exception unanswered) when (TelemetrySource.Said(unanswered) && !cancel.IsCancellationRequested)
                {
                    if (Stopwatch.GetElapsedTime(began) > AnswerLimit)
                    {
                        return await dashboard.GoneAsync(dashboard.source.Unread(unanswered));
                    }
                }
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancel);
            }
        }
        catch (OperationCanceledException)
        {
            await dashboard.StopAsync();
            throw;
        }
        catch (Exception failed) when (failed is TimeoutException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            return await dashboard.GoneAsync($"its dashboard did not come up: {failed.Message}");
        }
    }

    /// <summary>A dashboard that did not come up as it should, taken down: null, and why.</summary>
    private async Task<(RunDashboard? Dashboard, string? Why)> GoneAsync(string why)
    {
        await StopAsync();
        return (null, why);
    }

    /// <summary>Takes it down: the containers and network its labels are on, never another project's; once, whoever asks.</summary>
    public Task StopAsync()
    {
        UiThread.Check();
        if (stopping is not null)
        {
            return stopping;
        }
        containers.EndedProject(Compose);
        stopping = BackgroundWork.Run(() => FolderContainers.TakeDownAsync(engine, Compose, labelledFor, $"as {Name} ended"));
        Stopped?.Invoke(this, EventArgs.Empty);
        return stopping;
    }

    /// <summary>Its run ended: taken down, heard by the IDE's errors should the engine fail.</summary>
    public void Dispose() => IdeErrors.Observe(StopAsync());
}
