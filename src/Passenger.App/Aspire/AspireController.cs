using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;
using Passenger.Profiler;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>Owns AppHosts and their DCP-requested service processes for one workspace.</summary>
public sealed partial class AspireController(ProjectSession workspace, string stateFolder) : IAsyncDisposable
{
    private readonly List<AspireAppRun> apps = [];
    private readonly HashSet<string> startingPaths = new(PathNames.Comparer);
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    private Task? closing;
    private AspireContainerOwnership? containers;
    internal CancellationToken ClosingToken => lifetime.Token;

    /// <summary>The session containers the folder's AppHosts' orchestrators made, recorded as each run's resources tell of
    /// them and taken down with the run (<see cref="AspireContainerOwnership"/>).</summary>
    internal AspireContainerOwnership Containers
    {
        get
        {
            if (containers is null)
            {
                containers = new(stateFolder, workspace.Containers.Engine, lifetime.Token);
                containers.Elsewhere += (_, made) =>
                {
                    if (FindApp(made.AppHost) is { } app)
                    {
                        string said = $"{app.Configuration.Name}'s orchestrator made {Plural.Of(made.Containers, "container")} on an engine Passenger reaches "
                            + "neither through docker nor through the runtime it was told or would find (docker, podman), so Passenger cannot list, "
                            + "follow or remove them: set ASPIRE_CONTAINER_RUNTIME to the engine docker or podman reaches, or docker's context to DCP's";
                        app.Console.Append(said + "\n", error: true);
                        ContainersElsewhere?.Invoke(this, said);
                    }
                };
                containers.OtherEnginesChanged += (_, _) => ContainerEnginesChanged?.Invoke(this, EventArgs.Empty);
            }
            return containers;
        }
    }

    /// <summary>What a run's orchestrator made on an engine Passenger reaches no way, in words for the status bar.</summary>
    public event EventHandler<string>? ContainersElsewhere;

    /// <summary>The engines besides the window's that hold the folder's AppHosts' containers: the runtime an orchestrator
    /// chose where it is not the engine Passenger's docker reaches (Podman's beside Docker's).</summary>
    public IReadOnlyList<Containers.ContainerEngine> ContainerEngines => containers?.OtherEngines ?? [];

    /// <summary><see cref="ContainerEngines"/> changed. On the UI thread.</summary>
    public event EventHandler? ContainerEnginesChanged;

    public IReadOnlyList<AspireAppRun> Apps => apps.AsReadOnly();
    public OutputView? LastAppConsole { get; private set; }
    public AspireResourceRun? SelectedService { get; private set; }
    public LiveApp? SelectedInspection => SelectedService?.Inspection;
    public bool IsRunning => apps.Any(app => !app.Stopping && (app.Runner.IsRunning || app.IsTest));
    public event EventHandler? Changed;

    /// <summary>A service's app told of failed bindings: raised once for all told at once (thousands, as a window opens), on
    /// the UI thread, for Problems' Bindings alone, never the AppHost's whole state.</summary>
    public event EventHandler? BindingsChanged;

    private bool bindingsDue;

    private void BindingsFailed()
    {
        if (bindingsDue)
        {
            return;
        }
        bindingsDue = true;
        UiThread.Post(() =>
        {
            bindingsDue = false;
            if (!disposed)
            {
                BindingsChanged?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    /// <summary>Restarts a service through its AppHost (the window's resource commands): stopped, its project built again
    /// when asked, and started, or restarted; done once asked.</summary>
    public Func<AspireAppRun, AspireResourceRun, bool, CancellationToken, Task>? RestartCommand { get; set; }

    /// <summary>Attaches to a service held at its start as the start comes, once: the run of services' one attach
    /// (BuildRunController's), null once attached, else why not.</summary>
    public Func<AspireAppRun, AspireResourceRun, RunStart, CancellationToken, Task<string?>>? AttachStart { get; set; }

    /// <summary>What a person chose for a service of a configuration's run as it ran (debugged, or not), which its first start
    /// in a run that brings the configuration up again keeps; null when nothing was chosen.</summary>
    public Func<string, string, bool?>? Chosen { get; set; }
    public Func<AspireAppRun, CancellationToken, Task<AspireInteractions?>>? ConnectInteractions { get; set; }

    /// <summary>How long Profile of a configuration waits for the service it records to reach the profiler, counted from DCP's
    /// request to start that service (however long its AppHost takes to ask, which Stop ends): past it the start is given up,
    /// said as the time limit it is.</summary>
    public TimeSpan ProfileLimit { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Whether the Debug window's chosen tab is another program's than any service's (a plain launch beside the
    /// AppHost), which the Aspire window's resources read again do not take back: only a person's choice of a resource does.</summary>
    public bool OtherProgramChosen { get; private set; }

    public void SelectService(AspireResourceRun? service)
    {
        UiThread.Check();
        OtherProgramChosen = false;
        if (SelectedService != service)
        {
            SelectedService = service;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SelectDebugger(DebugTab? tab)
    {
        AspireResourceRun? service = tab is null ? null : apps.SelectMany(app => app.Services).FirstOrDefault(service => service.DebugTab == tab);
        SelectService(service);
        OtherProgramChosen = tab is not null && service is null;
    }

    public AspireAppRun? FindApp(string appHost) => apps.FirstOrDefault(app => PathNames.Same(app.Configuration.Project, appHost));

    public AspireResourceRun? FindService(AspireSelection selection)
    {
        if (FindApp(selection.AppHostPath) is not { } app || selection.Resource.Source is not { } source || !Path.IsPathFullyQualified(source))
        {
            return null;
        }
        if (selection.Resource.State is not ("Running" or "Starting"))
        {
            return null;
        }
        AspireResourceRun[] matching = app.Services.Where(service => PathNames.Same(service.Project, source)).ToArray();
        int? processId = null;
        foreach (System.Text.Json.JsonProperty property in AspireJson.Properties(selection.Resource.Data, "properties"))
        {
            if (property.Name != "executable.pid")
            {
                continue;
            }
            if (property.Value.ValueKind == System.Text.Json.JsonValueKind.Number && property.Value.TryGetDouble(out double number)
                && number is > 0 and <= int.MaxValue && number == Math.Truncate(number))
            {
                processId = (int)number;
            }
            else if (property.Value.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(property.Value.GetString(), out int value) && value > 0)
            {
                processId = value;
            }
        }
        AspireResourceRun[] named = matching.Where(service => processId is int pid
            ? service.ProcessId == pid && (service.ResourceName is null || service.ResourceName == selection.Resource.Name
                || AspireResource.IsNameWithSuffix(selection.Resource.Name, service.ResourceName) || service.ResourceName == selection.Resource.DisplayName)
            : service.ResourceName == selection.Resource.Name).Take(2).ToArray();
        if (named.Length != 1)
        {
            return null;
        }
        AspireResourceRun matched = named[0];
        if (!disposed && !workspace.IsEnded && !lifetime.IsCancellationRequested && !app.Stopping
            && !app.Lifetime.IsCancellationRequested && processId is > 0 && selection.Resource.Uid is { Length: > 0 } uid)
        {
            (string Name, string Project, string IdentityKey) identity = (Name: selection.Resource.Name, Project: matched.Project, IdentityKey: matched.IdentityKey);
            if (app.OwnedResourceIdentities.TryGetValue(uid, out (string Name, string Project, string IdentityKey)? previous)
                && (previous is not { } known || known.Name != identity.Name
                    || !PathNames.Same(known.Project, identity.Project) || known.IdentityKey != identity.IdentityKey))
            {
                app.OwnedResourceIdentities[uid] = null;
            }
            else
            {
                app.OwnedResourceIdentities[uid] = identity;
            }
        }
        return matched;
    }

    public void SetDebugMode(AspireSelection selection, bool debugging)
    {
        UiThread.Check();
        AspireAppRun app = FindApp(selection.AppHostPath) ?? throw new InvalidOperationException("The selected AppHost is not owned by this workspace");
        if (disposed || workspace.IsEnded || lifetime.IsCancellationRequested || app.Stopping || app.Lifetime.IsCancellationRequested)
        {
            throw new InvalidOperationException("The workspace or owned AppHost changed");
        }
        if (FindService(selection) is { } service)
        {
            app.Choose(((IRunService)service).Name, debugging);
            return;
        }
        // One its run left unstarted, as its configuration says Don't start: its next start is held as now chosen.
        if (app.UnstartedName(selection.Resource) is { } unstarted)
        {
            app.Choose(unstarted, debugging);
            return;
        }
        if (selection.Resource.State is not ("NotStarted" or "Exited" or "Finished" or "FailedToStart")
            || selection.Resource.Uid is not { Length: > 0 } uid
            || !app.OwnedResourceIdentities.TryGetValue(uid, out (string Name, string Project, string IdentityKey)? remembered) || remembered is not { } identity
            || identity.Name != selection.Resource.Name || selection.Resource.Source is not { } source
            || !Path.IsPathFullyQualified(source) || !PathNames.Same(identity.Project, source))
        {
            throw new InvalidOperationException("The selected resource does not identify one previously owned .NET service");
        }
        app.Choose(selection.Resource.DisplayName, debugging);
    }

    public bool IsAspire(RunConfiguration configuration) => configuration.Aspire
        || workspace.Projects.Any(project => project.IsAspireHost && PathNames.Same(project.Path, configuration.Project));

    /// <summary>The AppHost's own addresses, as its launch profile gives them, that its dashboard, telemetry and resource
    /// service serve on, which over HTTP serve HTTP.</summary>
    private static readonly string[] AppHostAddresses = ["ASPNETCORE_URLS", "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL",
        "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", "DOTNET_DASHBOARD_OTLP_ENDPOINT_URL", "DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL", "DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"];

    /// <summary>The AppHosts of an Aspire configuration (it and those it links) that serve HTTPS on this machine, by name, as
    /// their launch profiles say: what the development certificate is checked for before any of them starts. Throws a
    /// <see cref="LaunchException"/> for a single-file AppHost's profile that is not there or does not run it. The
    /// configurations are the window's, the profiles read off its thread.</summary>
    public async Task<IReadOnlyList<string>> ServingHttpsAsync(RunConfiguration configuration)
    {
        RunConfiguration[] candidates = [.. new[] { configuration }.Concat(configuration.LinkedAppHosts
            .Select(path => workspace.Configurations.All.FirstOrDefault(candidate => candidate.Aspire && PathNames.Same(candidate.Project, path)))
            .OfType<RunConfiguration>())];
        return await BackgroundWork.Run(() => (IReadOnlyList<string>)[.. candidates
            .Where(candidate => (Path.GetExtension(candidate.Project).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? ReadFileProfile(candidate) : candidate).Environment
                .Where(variable => AppHostAddresses.Contains(variable.Key))
                .SelectMany(variable => variable.Value.Split(';'))
                .Any(url => Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) && uri.Scheme == "https"
                    && (uri.IsLoopback || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))))
            .Select(candidate => Path.GetFileNameWithoutExtension(candidate.Project))]);
    }

    /// <summary>Starts an Aspire configuration's AppHosts, each built first as it says, and once each is connected to answers
    /// null, else why not. <paramref name="https"/> is how its HTTPS is served, as the start worked out before: over HTTP, the
    /// AppHosts' own addresses and the services they start serve HTTP for this run.</summary>
    public async Task<string?> StartAsync(RunConfiguration configuration, RunStart mode,
        Func<BuildStep, Task<BuildOutcome>> build, CancellationToken cancellation = default, HttpsChoice? https = null)
    {
        UiThread.Check();
        ObjectDisposedException.ThrowIf(disposed, this);
        using CancellationTokenSource starting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellation);
        List<AspireAppRun> started = [];
        List<string> reserved = [];
        try
        {
            string[] paths = [configuration.Project, .. configuration.LinkedAppHosts];
            if (paths.Distinct(PathNames.Comparer).Count() != paths.Length)
            {
                return "An Aspire configuration cannot link an AppHost more than once";
            }
            foreach (string path in paths)
            {
                if (startingPaths.Contains(path) || apps.Any(app => PathNames.Same(app.Configuration.Project, path)))
                {
                    return $"{Path.GetFileNameWithoutExtension(path)} is already running in this workspace";
                }
            }
            foreach (string path in paths)
            {
                startingPaths.Add(path);
                reserved.Add(path);
            }
            string group = Guid.NewGuid().ToString("N");
            RunConfiguration[] configurations = paths.Select(path => PathNames.Same(path, configuration.Project) ? configuration
                : workspace.Configurations.All.FirstOrDefault(candidate => candidate.Aspire && PathNames.Same(candidate.Project, path))
                    ?? throw new LaunchException($"The linked AppHost {Path.GetFileName(path)} is not available")).ToArray();
            (string AppHost, string Name)[] candidates = configurations.SelectMany(chosen => chosen.ServiceActions
                .Where(pair => pair.Value == ServiceStart.Debug && chosen.ServiceProjects.ContainsKey(pair.Key))
                .OrderBy(pair => pair.Key == chosen.LaunchService ? 0 : 1)
                .Select(pair => (chosen.Project, pair.Key))).ToArray();
            if (candidates.Length == 0)
            {
                candidates = configurations.Where(chosen => chosen.ServiceProjects.ContainsKey(chosen.LaunchService))
                    .Select(chosen => (chosen.Project, chosen.LaunchService)).ToArray();
            }
            (string AppHost, string Name)? profileTarget = candidates.Length > 0 ? candidates[0] : null;
            https ??= HttpsChoice.None;
            foreach (RunConfiguration chosen in configurations)
            {
                AspireAppRun app = await StartAppAsync(chosen, mode, group, build, starting.Token, profileTarget, https, said: started.Count == 0 ? https.Said : []);
                started.Add(app);
            }
            if (mode == RunStart.Profile)
            {
                // The AppHost asks in its own time (Stop ends the wait), which no drawing waits on: the hold is let go.
                Task asked;
                BackgroundWork.End();
                try
                {
                    asked = await Task.WhenAny(started.Select(app => (Task)app.ProfileAsked.Task)
                        .Concat(started.Select(app => (Task)app.Runner.Ended))).WaitAsync(starting.Token);
                }
                finally
                {
                    BackgroundWork.Begin();
                }
                if (started.FirstOrDefault(app => ReferenceEquals(asked, app.ProfileAsked.Task)) is not { } profiled)
                {
                    throw new LaunchException("The AppHost ended before a service could be profiled");
                }
                string service = await profiled.ProfileAsked.Task;
                // The start's landing or its limit falling due, whichever came first where it came (ClaimsProfile), however late this looks.
                Task winner = await Task.WhenAny(profiled.ProfileStarted.Task, profiled.Runner.Ended).WaitAsync(starting.Token);
                if (!ReferenceEquals(winner, profiled.ProfileStarted.Task))
                {
                    throw new LaunchException($"The AppHost ended before {service} could be profiled");
                }
                if (await profiled.ProfileStarted.Task is { } failed)
                {
                    throw new LaunchException(failed);
                }
            }
            return null;
        }
        catch (Exception error) when (error is LaunchException or IOException or InvalidOperationException
            or OperationCanceledException or System.ComponentModel.Win32Exception or TimeoutException or JsonException or ProfilingException)
        {
            foreach (AspireAppRun app in started)
            {
                await StopAppAsync(app);
            }
            return error is OperationCanceledException ? BuildRunController.StoppedStarting(configuration.Name) : error.Message;
        }
        finally
        {
            foreach (string path in reserved)
            {
                startingPaths.Remove(path);
            }
        }
    }

    private async Task<AspireAppRun> StartAppAsync(RunConfiguration configuration, RunStart mode, string group,
        Func<BuildStep, Task<BuildOutcome>> build, CancellationToken cancellation, (string AppHost, string Name)? profileTarget, HttpsChoice https,
        IReadOnlyList<string> said)
    {
        DateTime began = DateTime.UtcNow;
        LaunchPlan plan = await ResolveAppHostAsync(configuration, build, cancellation);
        if (plan.Build is { } step && await build(step) is { Succeeded: false } failed)
        {
            throw new LaunchException(failed.Message);
        }
        cancellation.ThrowIfCancellationRequested();
        if (workspace.IsEnded || disposed)
        {
            throw new LaunchException(Refusals.SolutionClosed);
        }
        if (plan.WhyNot("run") is { } why)
        {
            throw new LaunchException(why);
        }
        AspireAppRun app = new(configuration, mode, group)
        {
            ProfileTarget = profileTarget,
            BuildBegan = began,
            OverHttp = https.OverHttp is not null,
            Restarting = (run, resource, build, cancel) => (RestartCommand ?? throw new InvalidOperationException("Aspire resource restart is unavailable"))(run, resource, build, cancel),
        };
        if (https.OverHttp is { } note)
        {
            app.Notes.Add(note);
        }
        foreach (string line in said)
        {
            app.Console.Append($"{line}\n", error: true);
        }
        LastAppConsole = app.Console;
        apps.Add(app);
        try
        {
            AspireExecutionServer execution = await StartEndpointAsync(app, cancellation);
            app.Lifetime.Token.ThrowIfCancellationRequested();
            cancellation.ThrowIfCancellationRequested();
            Dictionary<string, string> environment = new(plan.Environment, StringComparer.Ordinal);
            if (app.OverHttp)
            {
                // Its dashboard, telemetry and resource service serve HTTP for this run, which Aspire takes once told it may.
                foreach (string address in AppHostAddresses)
                {
                    if (environment.GetValueOrDefault(address) is { } urls && urls.Contains("https:", StringComparison.OrdinalIgnoreCase))
                    {
                        environment[address] = urls.Replace("https:", "http:", StringComparison.OrdinalIgnoreCase);
                    }
                }
                foreach ((string name, string value) in DevelopmentCertificate.OverHttp(environment))
                {
                    environment[name] = value;
                }
                environment["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";
            }
            foreach ((string name, string value) in execution.Environment)
            {
                environment[name] = value;
            }
            environment.Remove("DEBUG_SESSION_SERVER_CERTIFICATE");
            environment["DEBUG_SESSION_RUN_MODE"] = mode == RunStart.Debug ? "Debug" : "NoDebug";
            environment["AppHost__FilePath"] = configuration.Project;
            environment["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "1";
            ConfigureResourceService(app, environment);
            ConfigureDashboard(app, environment);
            app.Runner.Exited += (_, _) => BackgroundWork.Track(StopAppAsync(app));
            ProcessStartInfo hostStart = ChildProcesses.Command(plan.Program, plan.Arguments, plan.WorkingDirectory);
            hostStart.Environment.Remove("DEBUG_SESSION_SERVER_CERTIFICATE");
            // The runtime its orchestrator is told, where its containers are looked for first when Passenger's docker lacks them.
            app.ContainerRuntime = ((string[])["ASPIRE_CONTAINER_RUNTIME", "DOTNET_ASPIRE_CONTAINER_RUNTIME"])
                .Select(name => environment.GetValueOrDefault(name) ?? (hostStart.Environment.TryGetValue(name, out string? inherited) ? inherited : null))
                .FirstOrDefault(told => told is { Length: > 0 });
            if (await app.Runner.StartAsync(hostStart, environment) is { } refused)
            {
                throw new LaunchException(refused);
            }
            app.ProcessId = app.Runner.ProcessId;
            app.Console.SetRunning(true);
            app.Console.StopClicked += (_, _) => BackgroundWork.Track(StopGroupAsync(app.Group));
            Changed?.Invoke(this, EventArgs.Empty);
            if (ConnectInteractions is { } connect)
            {
                using CancellationTokenSource connecting = CancellationTokenSource.CreateLinkedTokenSource(cancellation, app.Lifetime.Token);
                Task<AspireInteractions?> pending = connect(app, connecting.Token);
                try
                {
                    if (await Task.WhenAny(pending, app.Runner.Ended).WaitAsync(connecting.Token) != pending)
                    {
                        throw new LaunchException("The AppHost exited before it became ready. Read its AppHost console in the Aspire window.");
                    }
                    app.Interactions = await pending;
                    connecting.Token.ThrowIfCancellationRequested();
                    Follow(app);
                }
                catch
                {
                    connecting.Cancel();
                    try
                    {
                        if (await pending is { } late)
                        {
                            await late.DisposeAsync();
                        }
                    }
                    catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
                    {
                    }
                    throw;
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return app;
        }
        catch
        {
            await StopAppAsync(app);
            throw;
        }
    }

    /// <summary>The run's resources followed from its connection until it stops, whatever the window shows: each container
    /// the orchestrator makes is known as the stream tells of it, its session containers recorded at once for every end
    /// that takes them down (<see cref="Containers"/>).</summary>
    private void Follow(AspireAppRun app)
    {
        if (app.Interactions is not { } connection)
        {
            return;
        }
        connection.ResourcesChanged += (_, snapshot) =>
        {
            if (disposed || app.Stopping || FindApp(app.Configuration.Project) != app)
            {
                return;
            }
            string[] ids = [.. snapshot.SelectMany(resource => AspireJson.Properties(resource.Data, "properties"))
                .Where(property => property.Name == "container.id" && property.Value.ValueKind == JsonValueKind.String)
                .Select(property => property.Value.GetString()).OfType<string>().Where(value => value.Length > 0).Distinct(StringComparer.Ordinal)];
            // Each health check's run is an update: the Containers window is regrouped only when the containers named change.
            bool moved = !ids.ToHashSet(StringComparer.Ordinal).SetEquals(app.KnownContainerIds);
            app.KnownContainerIds = ids;
            BackgroundWork.Track(Reported(Containers.Observe(app.Configuration.Project, snapshot, app.ContainerRuntime)));
            if (moved)
            {
                ContainersChanged?.Invoke(this, app);
            }
        };
        app.Following = connection.Follow();
    }

    /// <summary>The containers a run's resources name changed (<see cref="AspireAppRun.KnownContainerIds"/>).</summary>
    public event EventHandler<AspireAppRun>? ContainersChanged;

    private static async Task Reported(Task work)
    {
        try
        {
            await work;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            IdeErrors.Write($"Aspire containers: {error.Message}");
        }
    }

    private static void ConfigureResourceService(AspireAppRun app, IDictionary<string, string> environment)
    {
        Uri? endpoint = null;
        if (environment.TryGetValue("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", out string? configured))
        {
            Uri.TryCreate(configured.Split(';')[0], UriKind.Absolute, out endpoint);
        }
        if (endpoint is null || endpoint.Port == 0)
        {
            using TcpListener port = new(IPAddress.Loopback, 0);
            port.Start();
            int number = ((IPEndPoint)port.LocalEndpoint).Port;
            bool allowHttp = environment.TryGetValue("ASPIRE_ALLOW_UNSECURED_TRANSPORT", out string? allowed)
                && bool.TryParse(allowed, out bool unsecured) && unsecured;
            endpoint = new Uri($"{(allowHttp ? "http" : "https")}://localhost:{number}");
            environment["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = endpoint.AbsoluteUri;
        }
        if (!endpoint.IsLoopback || endpoint.Scheme is not ("http" or "https"))
        {
            throw new LaunchException("Aspire's resource service must use a loopback HTTP or HTTPS endpoint");
        }
        app.ResourceServiceEndpoint = endpoint;
        app.ResourceServiceKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        environment["ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY"] = app.ResourceServiceKey;
    }

    private static void ConfigureDashboard(AspireAppRun app, IDictionary<string, string> environment)
    {
        string urls = environment.TryGetValue("ASPNETCORE_URLS", out string? configured) ? configured
            : Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? string.Empty;
        Uri? existing = urls.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(url => Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? endpoint) ? endpoint : null)
            .FirstOrDefault(endpoint => endpoint?.Scheme == Uri.UriSchemeHttp);
        using TcpListener port = new(IPAddress.Loopback, 0);
        if (existing is null)
        {
            port.Start();
        }
        app.DashboardEndpoint = existing ?? new Uri($"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}");
        app.DashboardApiKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        environment["AppHost__DashboardApiKey"] = app.DashboardApiKey;
        if (existing is null)
        {
            environment["ASPNETCORE_URLS"] = string.IsNullOrWhiteSpace(urls) ? app.DashboardEndpoint.AbsoluteUri
                : $"{urls.TrimEnd(';')};{app.DashboardEndpoint.AbsoluteUri}";
        }
    }

    private Task<AspireExecutionServer> StartEndpointAsync(AspireAppRun app, CancellationToken cancellation)
    {
        return app.EndpointStarting = StartAndPublishAsync();

        async Task<AspireExecutionServer> StartAndPublishAsync()
        {
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation, app.Lifetime.Token);
            AspireExecutionServer execution = await AspireExecutionServer.StartAsync(
                (launch, cancel) => CreateOnUiAsync(app, launch, cancel),
                (handle, cancel) => app.Lifetime.IsCancellationRequested ? Task.CompletedTask : StopForServerAsync(app, handle, cancel), startup.Token).ConfigureAwait(false);
            app.Execution = execution;
            if (startup.IsCancellationRequested)
            {
                await execution.DisposeAsync().ConfigureAwait(false);
                startup.Token.ThrowIfCancellationRequested();
            }
            return execution;
        }
    }

    /// <summary>A session's stop the orchestrator asks, or its connection's end leaves to the server: done once its program has
    /// gone, or once the run itself is ending, whose own end stops every session it has.</summary>
    private async Task StopForServerAsync(AspireAppRun app, AspireProjectSession handle, CancellationToken cancel)
    {
        try
        {
            await UiThread.InvokeAsync(async () =>
            {
                cancel.ThrowIfCancellationRequested();
                if (!app.Lifetime.IsCancellationRequested)
                {
                    await StopResourceAsync(app, handle.Id);
                }
                return true;
            }).WaitAsync(cancel).WaitAsync(app.Lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (app.Lifetime.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            // The run ended as the stop waited: its own end (Stop, the folder's) stops the session.
        }
    }

    private async Task<AspireProjectSession> CreateOnUiAsync(AspireAppRun app, AspireProjectLaunch request, CancellationToken cancel)
    {
        Task<AspireProjectSession> creating = UiThread.InvokeAsync(() => StartResourceAsync(app, request, cancel));
        try
        {
            return await creating.WaitAsync(cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // A request can give up after its UI launch succeeds; that late process still belongs to the app.
            IdeErrors.Observe(CleanLateLaunchAsync());
            throw;
        }

        async Task CleanLateLaunchAsync()
        {
            AspireProjectSession handle;
            try
            {
                handle = await creating.ConfigureAwait(false);
            }
            catch (Exception) when (creating.IsFaulted || creating.IsCanceled)
            {
                // The start failed, or was stopped, after its request gave up: it stopped its own service, so none came late.
                return;
            }
            try
            {
                if (!app.Lifetime.IsCancellationRequested)
                {
                    await UiThread.InvokeAsync(async () =>
                    {
                        if (!app.Lifetime.IsCancellationRequested)
                        {
                            await StopResourceAsync(app, handle.Id);
                        }
                        return true;
                    }).WaitAsync(app.Lifetime.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception error)
            {
                IdeErrors.Report(error);
            }
        }
    }

    private static async Task DisposeEndpointAsync(Task<AspireExecutionServer> starting)
    {
        try
        {
            AspireExecutionServer execution = await starting.ConfigureAwait(false);
            await execution.DisposeAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<LaunchPlan> ResolveAppHostAsync(RunConfiguration configuration, Func<BuildStep, Task<BuildOutcome>> build, CancellationToken cancellation)
    {
        if (!Path.GetExtension(configuration.Project).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            if (configuration.Program.Length > 0)
            {
                throw new LaunchException("Owned Aspire AppHosts must launch the project's built target; clear Exe path");
            }
            return await LaunchPlan.ResolveAsync(configuration, workspace.Projects, workspace.Workspace, stateFolder, cancellation);
        }
        if (configuration.Program.Length > 0 || configuration.Framework.Length > 0)
        {
            throw new LaunchException("Single-file AppHosts use the framework declared in the file; clear Exe path and Target framework");
        }
        string folder = Path.GetDirectoryName(configuration.Project)!;
        // What it reads of the disk (the file, its folder, its launch profile) is read off the UI thread.
        configuration = await BackgroundWork.Run(() =>
        {
            if (!AspireAppHosts.IsSingleFile(configuration.Project))
            {
                throw new LaunchException("The selected C# file does not declare the Aspire AppHost SDK");
            }
            if (AspireAppHosts.SingleFileWhyNot(configuration.Project, Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)) is { } unsupported)
            {
                throw new LaunchException(unsupported);
            }
            return ReadFileProfile(configuration);
        }, cancellation);
        string output = Path.Combine(stateFolder, "aspire", Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(PathNames.Canonical(configuration.Project)))), "output");
        // A build of the Build window's: Stop ends it, Problems lists what it found.
        if (configuration.BuildBeforeLaunch && await build(new BuildStep(configuration.Project, null, output)) is { Succeeded: false } failed)
        {
            throw new LaunchException(failed.Message);
        }
        cancellation.ThrowIfCancellationRequested();
        ChildResult evaluated = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
            ["build", configuration.Project, "--output", output, "--no-restore", "--nologo", "-getProperty:TargetPath"], folder), cancel: cancellation);
        string target = evaluated.Output.Trim();
        if (!evaluated.Succeeded || !Path.IsPathFullyQualified(target) || !await BackgroundWork.Run(() => File.Exists(target), cancellation))
        {
            throw new LaunchException("MSBuild could not resolve the built single-file AppHost TargetPath");
        }
        string Expand(string value)
        {
            string expanded = Environment.ExpandEnvironmentVariables(value.Replace("$(ProjectDir)", folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (expanded.Contains("$(", StringComparison.Ordinal))
            {
                throw new LaunchException("Single-file AppHosts support the ProjectDir macro, not MSBuild output properties");
            }
            return expanded;
        }
        return new LaunchPlan
        {
            Name = configuration.Name,
            Program = target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? Dotnet.Host() : target,
            Arguments = [.. target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? new[] { target } : [],
                .. CommandLine.Split(Expand(configuration.Arguments))],
            WorkingDirectory = configuration.WorkingDirectory.Length == 0 ? folder : PathNames.Canonical(Path.Combine(folder, Expand(configuration.WorkingDirectory))),
            Environment = configuration.Environment.ToDictionary(variable => variable.Key, variable => Expand(variable.Value)),
            Project = configuration.Project,
            Built = target,
        };
    }

    /// <summary>A single-file AppHost's configuration with its launch profile's values (the one it names, else the first),
    /// what the configuration sets itself kept; as it is with no profile to read and none named.</summary>
    internal static RunConfiguration ReadFileProfile(RunConfiguration configuration)
    {
        IReadOnlyList<LaunchProfile> profiles = LaunchProfile.Read(LaunchProfile.FileSettingsPath(configuration.Project));
        if (profiles.FirstOrDefault(profile => configuration.LaunchProfile.Length == 0 || profile.Name == configuration.LaunchProfile) is not { } chosen)
        {
            return configuration.LaunchProfile.Length == 0 ? configuration
                : throw new LaunchException($"Launch profile {configuration.LaunchProfile} was not found");
        }
        // A profile naming no command runs the AppHost itself.
        LaunchProfile profile = chosen.Command is null ? chosen with { Command = LaunchProfile.ProjectCommand } : chosen;
        if (!profile.RunsProject)
        {
            throw new LaunchException("Single-file AppHosts require a Project launch profile");
        }
        Dictionary<string, string> environment = profile.Environment();
        foreach ((string name, string text) in configuration.Environment)
        {
            environment[name] = text;
        }
        return configuration with
        {
            Environment = environment,
            Arguments = configuration.Arguments.Length > 0 ? configuration.Arguments : profile.CommandLineArgs ?? string.Empty,
            WorkingDirectory = configuration.WorkingDirectory.Length > 0 ? configuration.WorkingDirectory : profile.WorkingDirectory ?? string.Empty,
        };
    }

    private async Task<AspireProjectSession> StartResourceAsync(AspireAppRun app, AspireProjectLaunch request, CancellationToken cancellation)
    {
        UiThread.Check();
        // Profile's limit counts from DCP's request for the service it records, which this start claims as it is asked.
        bool profiling = ClaimsProfile(app, request);
        try
        {
            return await StartRequestedAsync(app, request, profiling, cancellation);
        }
        catch (Exception error) when (profiling)
        {
            app.ProfileStarted.TrySetResult(error is OperationCanceledException ? BuildRunController.StoppedStarting(app.Name) : error.Message);
            throw;
        }
    }

    /// <summary>Whether this start is the one Profile of its configuration records: the first asked for of its run's AppHosts,
    /// or of the service Profile chose; claimed, the profile's time limit counts from now and falls due on its own timer, so
    /// a start that lands after it is past it however late Profile's start looks again.</summary>
    private bool ClaimsProfile(AspireAppRun app, AspireProjectLaunch request)
    {
        string path = PathNames.Canonical(request.ProjectPath);
        string? name = request.Environment.FirstOrDefault(variable => variable.Name == "OTEL_SERVICE_NAME")?.Value;
        if (app.Mode != RunStart.Profile || apps.Any(candidate => candidate.Group == app.Group && candidate.ProfileClaimed)
            || app.ProfileTarget is { } selected && !(PathNames.Same(selected.AppHost, app.Configuration.Project) && name == selected.Name
                && app.Configuration.ServiceProjects.TryGetValue(selected.Name, out string? selectedProject) && PathNames.Same(selectedProject, path)))
        {
            return false;
        }
        app.ProfileClaimed = true;
        string service = name ?? Path.GetFileNameWithoutExtension(path);
        TimeSpan limit = Waits.Of(ProfileLimit);
        app.ProfileAsked.TrySetResult(service);
        IdeErrors.Observe(Task.Delay(limit, app.Lifetime.Token).ContinueWith(_ => app.ProfileStarted.TrySetResult(
            $"{service} did not start under the profiler within {limit.TotalSeconds:0.##} s of its AppHost asking for it"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
        return true;
    }

    private async Task<AspireProjectSession> StartRequestedAsync(AspireAppRun app, AspireProjectLaunch request, bool profiling, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (app.Stopping || workspace.IsEnded || disposed || app.Resources.Count >= 200)
        {
            throw new LaunchException("The Aspire app is stopping or its service limit was reached");
        }
        string path = PathNames.Canonical(request.ProjectPath);
        if (Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            throw new LaunchException(AspireExecutionServer.FileServiceUnsupported);
        }
        ProjectModel? known = workspace.Projects.FirstOrDefault(project => PathNames.Same(project.Path, path));
        bool referenced = workspace.Projects.Any(project => project.ProjectReferences.Any(reference => PathNames.Same(reference, path)));
        if (!WorkspaceModel.IsProject(path) || (known is null && !referenced && (workspace.Folder is null || !PathNames.IsUnder(path, workspace.Folder))))
        {
            throw new LaunchException("DCP requested a project outside this workspace and its project references");
        }
        ProjectModel project = known ?? ProjectModel.Unevaluated(path);
        string? profileName = request.DisableLaunchProfile ? null : request.LaunchProfile ?? app.Configuration.LaunchProfile;
        // The project and its launch profiles are read off the UI thread.
        (bool exists, LaunchProfile? profile) = await BackgroundWork.Run(() => (File.Exists(path),
            profileName is null ? null : LaunchProfile.Read(project).FirstOrDefault(candidate => candidate.Name == profileName)), cancellation);
        if (!exists)
        {
            throw new LaunchException("DCP requested a project outside this workspace and its project references");
        }
        if (app.Stopping || workspace.IsEnded || disposed)
        {
            throw new LaunchException("The Aspire app is stopping or its service limit was reached");
        }
        RunConfiguration configuration = profile is null
            ? new RunConfiguration { Name = project.Name, Project = path, WorkingDirectory = "$(ProjectDir)" }
            : RunConfiguration.From(project, profile) with { Name = project.Name };
        configuration = configuration with { BuildBeforeLaunch = false, LaunchBrowser = false, Console = ConsoleKind.Internal };
        LaunchPlan plan = await LaunchPlan.ResolveAsync(configuration,
            known is null ? [.. workspace.Projects, project] : workspace.Projects, workspace.Workspace, stateFolder, cancellation);
        Dictionary<string, string> environment = new(plan.Environment, StringComparer.Ordinal);
        foreach (AspireEnvironmentVariable variable in request.Environment)
        {
            environment[variable.Name] = variable.Value;
        }
        if (app.OverHttp)
        {
            // No certificate for this run: a service told to serve HTTPS serves HTTP where it would have.
            foreach ((string name, string value) in DevelopmentCertificate.OverHttp(environment))
            {
                environment[name] = value;
            }
        }
        IReadOnlyList<string> arguments = request.Arguments is { } given
            ? [.. plan.Built is { } built && plan.Arguments.FirstOrDefault() == built ? new[] { built } : [], .. given]
            : plan.Arguments;
        plan = plan with { Name = $"{app.Configuration.Name} / {project.Name}", Environment = environment, Arguments = arguments };
        LaunchPlan stamped = plan;
        string stamp = await BackgroundWork.Run(() => AspireResourceRun.ReadBuiltStamp(stamped), cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (app.Stopping || workspace.IsEnded || app.Resources.Count >= 200)
        {
            throw new LaunchException("The Aspire application ended or reached its service limit during launch");
        }
        AspireResourceRun resource = new(request.SessionId, request.DcpInstanceId, path, plan) { BuiltStamp = stamp };
        resource.ResourceName = request.Environment.FirstOrDefault(variable => variable.Name == "OTEL_SERVICE_NAME")?.Value;
        string serviceName = ((IRunService)resource).Name;
        if (app.LeftUnstarted(serviceName, ResourceAction(app.Configuration, resource) == ServiceStart.DontStart))
        {
            throw new LaunchException(app.WhyNotStarted(serviceName));
        }
        app.Resources.Add(resource.Id, resource);
        using CancellationTokenSource starting = CancellationTokenSource.CreateLinkedTokenSource(cancellation, app.Lifetime.Token, resource.Stopping.Token);
        cancellation = starting.Token;
        string? failure = null;
        try
        {
            // Held as the run decides from its first start (FirstHold); the one Profile records, for the profiler.
            RunStart? chosen = app.Starting(resource, () => FirstHold(app, resource, request));
            RunStart? held = profiling ? RunStart.Profile : chosen;
            resource.Inspection = held != RunStart.HotReload ? LiveApp.For(plan, workspace.Overlays) : null;
            if (resource.Inspection is { } inspection)
            {
                inspection.BindingFailed += (_, _) => BindingsFailed();
            }
            LaunchPlan inspected = resource.Inspection?.Plan ?? plan;
            // Held until what attaches lets it go, never where nothing can; the profiler's gets no changeable code to measure.
            if (held is not null && AspireAppRun.DiagnosticsOff(inspected.Environment) is null)
            {
                Dictionary<string, string> holding = new(inspected.Environment, StringComparer.Ordinal);
                foreach ((string name, string value) in HeldStart.Variables(changeable: held != RunStart.Profile))
                {
                    holding[name] = value;
                }
                inspected = inspected with { Environment = holding };
            }
            ProcessRunner runner = resource.Runner = new ProcessRunner(resource.Console, takesInput: true);
            runner.OutputReceived += (_, output) => app.Execution?.NotifyServiceLog(request.DcpInstanceId, request.SessionId, output.Text, output.IsError);
            runner.Exited += (_, code) => Finished(app, resource, code);
            string? refused = await runner.StartAsync(inspected.Program, inspected.Arguments, inspected.WorkingDirectory, inspected.Environment);
            if (refused is not null)
            {
                throw new LaunchException(refused);
            }
            resource.ProcessId = runner.ProcessId;
            resource.Held = held == RunStart.Profile ? null : held;
            // The run of services' one attach to this start, whose session (or recording) lets it go.
            if (held is { } purpose && await (AttachStart ?? throw new LaunchException("The debugger is unavailable"))(app, resource, purpose, cancellation) is { } unattached)
            {
                if (purpose == RunStart.Profile)
                {
                    throw new LaunchException(unattached);
                }
                // Held for a debugger that could not reach it, it would wait for good: it runs on unheld, said.
                await app.HoldAsync(serviceName, null, CancellationToken.None);
                resource.Held = null;
                await LetGoAsync(resource.ProcessId);
                string said = $"{serviceName} of {app.Name} runs without the debugger, which could not attach: {unattached}";
                app.Console.Append(said + "\n", error: true);
                RunsUndebugged?.Invoke(this, said);
            }
            cancellation.ThrowIfCancellationRequested();
            if (profiling)
            {
                app.ProfileStarted.TrySetResult(null);
            }
            Changed?.Invoke(this, EventArgs.Empty);
            return new AspireProjectSession(resource.Id, resource.ProcessId);
        }
        catch (Exception error)
        {
            failure = error.Message;
            await StopResourceAsync(app, resource.Id);
            throw;
        }
        finally
        {
            app.Landed(resource, failure);
        }
    }

    /// <summary>A service held at its start for a debugger that could not reach it, which then runs on unheld: said.</summary>
    public event EventHandler<string>? RunsUndebugged;

    /// <summary>Lets a program held at its runtime's start go (<see cref="HeldStart"/>), its hold cleared for what it starts;
    /// one already going, or gone, is left as it is.</summary>
    private static Task LetGoAsync(int? process) => process is not int id ? Task.CompletedTask : BackgroundWork.Run(async () =>
    {
        if (await HeldStart.LetGoAsync(id) is { } why)
        {
            IdeLog.Write(IdeLog.Ide, $"Aspire: process {id} was not let go: {why}");
        }
    });

    /// <summary>What a service is held at its start for, its first start in a run: as a person chose it as the configuration
    /// last ran, else debugged where the configuration marks it Debug, or, with none marked, where DCP's request says;
    /// quietly for Hot Reload under Run with Hot Reload; else unheld (every one but the one profiled under Profile).</summary>
    private RunStart? FirstHold(AspireAppRun app, AspireResourceRun resource, AspireProjectLaunch request)
    {
        if (app.Mode == RunStart.Profile)
        {
            return null;
        }
        bool marked = app.Configuration.ServiceActions.Values.Contains(ServiceStart.Debug);
        bool debugging = Chosen?.Invoke(app.Configuration.Name, ((IRunService)resource).Name)
            ?? (marked ? ResourceAction(app.Configuration, resource) == ServiceStart.Debug : request.Mode == "Debug");
        return debugging ? RunStart.Debug : app.Mode == RunStart.HotReload ? RunStart.HotReload : null;
    }

    private static ServiceStart? ResourceAction(RunConfiguration configuration, AspireResourceRun resource)
    {
        return resource.ResourceName is { } name && configuration.ServiceActions.TryGetValue(name, out ServiceStart action)
            && configuration.ServiceProjects.TryGetValue(name, out string? project) && PathNames.Same(project, resource.Project)
                ? action : null;
    }

    private void Finished(AspireAppRun app, AspireResourceRun resource, int? exitCode)
    {
        UiThread.Check();
        if (disposed)
        {
            return;
        }
        app.Execution?.NotifyProcessTerminated(resource.InstanceId, resource.Id, exitCode is int code ? unchecked((uint)code) : null);
        resource.Stopping.Cancel();
        // Its own session and recording, as Stop's order ends them: the program has gone by itself.
        BackgroundWork.Track(app.EndSessionsAsync(workspace.Debug, [resource]));
        resource.Inspection?.Dispose();
        resource.Runner?.Dispose();
        app.Resources.Remove(resource.Id);
        if (SelectedService == resource)
        {
            SelectedService = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private Task StopResourceAsync(AspireAppRun app, string id)
    {
        UiThread.Check();
        if (!app.Resources.TryGetValue(id, out AspireResourceRun? resource))
        {
            return Task.CompletedTask;
        }
        return resource.Ending ??= StopOwnedAsync();

        async Task StopOwnedAsync()
        {
            resource.Stopping.Cancel();
            // Stop's order: its session (its own alone) and its recording, its snapshot read, before its program.
            await app.EndSessionsAsync(workspace.Debug, [resource]);
            if (resource.Runner is { } runner)
            {
                if (runner.IsRunning)
                {
                    runner.Stop();
                    await runner.Ended;
                }
                runner.Dispose();
            }
            resource.Inspection?.Dispose();
            app.Resources.Remove(id);
            if (SelectedService == resource)
            {
                SelectedService = null;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task StopAppAsync(AspireAppRun app)
    {
        UiThread.Check();
        if (app.Stopping)
        {
            await app.Stopped.Task;
            return;
        }
        app.Stopping = true;
        app.Lifetime.Cancel();
        try
        {
            // Stop's order, every run of services': the sessions and the recording reaching its services end first.
            await app.EndSessionsAsync(workspace.Debug);
            foreach (string id in app.Resources.Keys.ToArray())
            {
                await StopResourceAsync(app, id);
            }
            app.Runner.Stop();
            if (app.EndpointStarting is { } starting)
            {
                await DisposeEndpointAsync(starting);
            }
            app.Following?.Dispose();
            if (app.Interactions is { } interactions)
            {
                await interactions.DisposeAsync();
            }
            foreach (string id in app.Resources.Keys.ToArray())
            {
                await StopResourceAsync(app, id);
            }
            if (app.Runner.IsRunning)
            {
                await app.Runner.Ended;
            }
            // Its session containers last, once its AppHost has gone.
            await Containers.TakeDownAsync(app.Configuration.Project, "as its AppHost stopped");
        }
        finally
        {
            app.Runner.Stop();
            foreach (AspireResourceRun resource in app.Resources.Values)
            {
                resource.Runner?.Stop();
            }
            app.Console.SetRunning(false);
            app.Runner.Dispose();
            apps.Remove(app);
            app.Stopped.TrySetResult();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task StopGroupAsync(string group) => Task.WhenAll(apps.Where(app => app.Group == group).ToArray().Select(StopAppAsync));
    public Task StopAllAsync() => Task.WhenAll(apps.ToArray().Select(StopAppAsync));

    public Task CloseAsync()
    {
        UiThread.Check();
        if (disposed)
        {
            return closing ?? Task.CompletedTask;
        }
        disposed = true;
        AspireAppRun[] owned = apps.ToArray();
        foreach (AspireAppRun app in owned)
        {
            app.Stopping = true;
        }
        lifetime.Cancel();
        List<(AspireAppRun App, AspireResourceRun[] Resources, Task Debuggers)> captured = [];
        foreach (AspireAppRun app in owned)
        {
            app.Lifetime.Cancel();
            AspireResourceRun[] resources = app.Resources.Values.ToArray();
            foreach (AspireResourceRun resource in resources)
            {
                resource.Stopping.Cancel();
            }
            // Stop's order: each start's own session (in its tab, or quietly for Hot Reload) ends before its program.
            Task debuggers = app.EndSessionsAsync(workspace.Debug, resources, closing: true);
            foreach (AspireResourceRun resource in resources)
            {
                resource.Inspection?.Dispose();
            }
            captured.Add((app, resources, debuggers));
            app.Resources.Clear();
            app.Interactions?.Dispose();
            app.Console.SetRunning(false);
        }
        apps.Clear();
        startingPaths.Clear();
        SelectedService = null;
        // A recording of a service, the profiler's attach, is let go with the folder's profiler (ProjectSession's end).
        Task rundown = Task.CompletedTask;
        List<Task> endings = [];
        foreach ((AspireAppRun app, AspireResourceRun[] resources, Task debuggers) in captured)
        {
            Task prerequisite = Task.WhenAll(rundown, debuggers);
            List<Task> pending = [prerequisite, app.Runner.DeferStopAfter(prerequisite)];
            foreach (AspireResourceRun resource in resources)
            {
                if (resource.Runner is { } runner)
                {
                    pending.Add(runner.DeferStopAfter(prerequisite));
                    runner.Dispose();
                }
            }
            if (app.EndpointStarting is { } endpoint)
            {
                pending.Add(DisposeEndpointAfterAsync(prerequisite, endpoint));
            }
            if (app.Interactions is { } interactions)
            {
                pending.Add(interactions.DisposeAsync().AsTask());
            }
            app.Following?.Dispose();
            app.Runner.Dispose();
            // Its session containers once its AppHost and its services have gone.
            pending.Add(Containers.TakeDownAsync(app.Configuration.Project, "as the folder closed", Task.WhenAll(pending)));
            endings.Add(CompleteAppAsync(app, pending));
        }
        closing = CompleteAll(endings);
        Changed?.Invoke(this, EventArgs.Empty);
        return closing;

        static async Task CompleteAppAsync(AspireAppRun app, IReadOnlyList<Task> pending)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            finally
            {
                app.Stopped.TrySetResult();
            }
        }

        async Task CompleteAll(IReadOnlyList<Task> pending)
        {
            try
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        static async Task DisposeEndpointAfterAsync(Task prerequisite, Task<AspireExecutionServer> endpoint)
        {
            try
            {
                await prerequisite.ConfigureAwait(false);
            }
            finally
            {
                await DisposeEndpointAsync(endpoint).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());
}

/// <summary>An Aspire AppHost the window started, a run of services (<see cref="ServiceRun"/>) whose services DCP asks the
/// IDE to start on this machine: each start is the controller's (<c>StartResourceAsync</c>), held at its runtime's start as
/// the model says (<see cref="ServiceRun.HeldFor"/>) and attached to as it starts, once (<see cref="AttachesItsStarts"/>).
/// What is its own: a service restarts through its AppHost's resource commands (<see cref="RestartAsync"/>), its program
/// is the newest started for its name, and its session is named by its AppHost too.</summary>
public sealed class AspireAppRun : ServiceRun
{
    /// <summary>The services started at least once in this run, whose hold is the model's from then on.</summary>
    private readonly HashSet<string> decided = new(StringComparer.Ordinal);

    /// <summary>The services it left unstarted, as their configuration says Don't start, until a start of theirs is asked.</summary>
    private readonly HashSet<string> unstarted = new(StringComparer.Ordinal);

    /// <summary>How a resource the run left unstarted is said, where DCP says the start it refused failed, and why.</summary>
    public const string NotStarted = "Not started";

    public const string NotStartedWhy = $"Its run configuration says Don't start; {StartsService}";

    /// <summary>A service an AppHost's run left unstarted is started by its resource's Start, Debug Service or Run Service
    /// without Debugger, each of which has its AppHost start it.</summary>
    public const string StartsService = "its Start, Debug Service or Run Service without Debugger starts it";

    public override string StartsIt => StartsService;

    public override bool LeftUnstarted(string name) => unstarted.Contains(name);

    /// <summary>Each service's next start, as a restart waits for it: its failure, or null once started and attached to.</summary>
    private readonly Dictionary<string, TaskCompletionSource<string?>> nextStarts = new(StringComparer.Ordinal);

    private long started;

    internal AspireAppRun(RunConfiguration configuration, RunStart mode, string group)
    {
        Configuration = configuration;
        How = mode;
        Group = group;
        Runner = new ProcessRunner(Console, takesInput: true);
    }

    public RunConfiguration Configuration { get; }
    public RunStart Mode => How;
    public override string Group { get; }
    public override string Name => Configuration.Name;
    public bool IsTest { get; internal init; }

    /// <summary>Whether it and the services it starts serve HTTP for this run where they would serve HTTPS, there being no
    /// trusted development certificate to serve it with (<see cref="HttpsChoice"/>).</summary>
    public bool OverHttp { get; internal init; }
    public OutputView Console { get; } = new();
    public ProcessRunner Runner { get; }
    public int? ProcessId { get; internal set; }
    internal Uri? ResourceServiceEndpoint { get; set; }
    internal string ResourceServiceKey { get; set; } = string.Empty;
    internal Uri? DashboardEndpoint { get; set; }
    internal string DashboardApiKey { get; set; } = string.Empty;
    public override Telemetry.TelemetrySource? Telemetry => DashboardEndpoint is { } endpoint && DashboardApiKey.Length > 0 ? new(endpoint, DashboardApiKey) : null;
    internal bool ProfileClaimed { get; set; }
    internal (string AppHost, string Name)? ProfileTarget { get; init; }

    /// <summary>When the AppHost's build began (the build its first services run too): its own code changed once a file its
    /// folder holds was written after it.</summary>
    internal DateTime BuildBegan { get; init; }

    /// <summary>When the build each service project was built by again for a restart began, by project.</summary>
    internal Dictionary<string, DateTime> Rebuilt { get; } = new(PathNames.Comparer);
    internal TaskCompletionSource<string?> ProfileStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>DCP's request to start the service Profile records, by its name; the profile's time limit counts from it.</summary>
    internal TaskCompletionSource<string> ProfileAsked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IReadOnlyList<string> KnownContainerIds { get; internal set; } = [];
    internal AspireInteractions? Interactions { get; set; }

    /// <summary>The container runtime its orchestrator was told (ASPIRE_CONTAINER_RUNTIME in its environment), else null.</summary>
    internal string? ContainerRuntime { get; set; }

    /// <summary>The run's hold on its resource stream, which stays open while the run is up (<c>AspireController.Follow</c>).</summary>
    internal IDisposable? Following { get; set; }
    internal CancellationTokenSource Lifetime { get; } = new();

    /// <summary>The services it runs now, in the order they started.</summary>
    public override IReadOnlyList<AspireResourceRun> Services => [.. Resources.Values.OrderBy(resource => resource.Sequence)];
    private AspireExecutionServer? execution;
    internal AspireExecutionServer? Execution
    {
        get => Volatile.Read(ref execution);
        set => Volatile.Write(ref execution, value);
    }
    internal Task<AspireExecutionServer>? EndpointStarting { get; set; }
    internal Dictionary<string, AspireResourceRun> Resources { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, (string Name, string Project, string IdentityKey)?> OwnedResourceIdentities { get; } = new(StringComparer.Ordinal);
    internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool Stopping { get; set; }

    /// <summary>Restarts a service through its AppHost (the controller's, the window's resource commands): its stop, its
    /// project's build when asked, and its start, or its restart; done once asked.</summary>
    internal Func<AspireAppRun, AspireResourceRun, bool, CancellationToken, Task>? Restarting { get; set; }

    public override bool IsStopped => Stopping;

    public override bool AttachesItsStarts => true;

    public override bool RestartsToRunWithoutDebugger => true;

    /// <summary>A service's build is written over its files, which Windows keeps while it runs: built between its stop and
    /// its start (<see cref="RestartAsync"/>'s build).</summary>
    public override bool BuildsBeforeRestart => false;

    /// <summary>In the service's own console, which its resource shows in the Aspire window.</summary>
    public override void Say(IRunService service, string line, bool error) =>
        ((service as AspireResourceRun ?? ServiceNamed(service.Name) as AspireResourceRun)?.Console ?? Console).Append($"{line}\n", error);

    /// <summary>By its AppHost too, as two AppHosts may each run a resource of one name.</summary>
    public override string SessionName(IRunService service) => $"{Path.GetFileNameWithoutExtension(Configuration.Project)}: {service.Name}";

    /// <summary>The newest start of a service of that name still running.</summary>
    public override IRunService? ServiceNamed(string name) =>
        Resources.Values.Where(resource => ((IRunService)resource).Name == name && !resource.Stopping.IsCancellationRequested).MaxBy(resource => resource.Sequence);

    public override bool Refused(Exception failure) => failure is IOException or InvalidOperationException or TimeoutException or LaunchException or InvalidDataException
        or System.ComponentModel.Win32Exception;

    /// <summary>A start's own session, never another start's of the same name: in its tab, stopped; quiet (for Hot Reload),
    /// let go; closed as the folder ends.</summary>
    protected override Task EndSessionAsync(DebugController debug, IRunService service, bool closing)
    {
        if (service is not AspireResourceRun start)
        {
            return base.EndSessionAsync(debug, service, closing);
        }
        DebugSession? own = start.DebugTab?.Session ?? start.Debugged;
        return own is not { State: not DebugState.Ended } ? Task.CompletedTask
            : closing ? own.CloseAsync()
            : start.DebugTab is { } tab ? debug.StopAsync(tab)
            : debug.DetachAsync(own);
    }

    /// <summary>Nothing to write: a service's next start reads its hold.</summary>
    protected override Task HoldsChangedAsync(CancellationToken cancel) => Task.CompletedTask;

    /// <summary>A service's program on this machine: the newest start of its name, with its inspected app, open to Hot Reload
    /// when it started held (with DOTNET_MODIFIABLE_ASSEMBLIES=debug).</summary>
    public override Task<ServiceProgram?> ProgramAsync(IRunService service, Task up, CancellationToken stop) =>
        ServiceNamed(service.Name) is AspireResourceRun { ProcessId: int id } running
            ? Task.FromResult<ServiceProgram?>(new ServiceProgram(id, null, null)
            {
                HotReload = running.Held is not null,
                App = running.Inspection,
                WhyNot = DiagnosticsOff(running.Plan.Environment),
            })
            : throw new ServiceUnreachedException($"{service.Name} of {Name} has no program running");

    /// <summary>Why no debugger or profiler can reach a program its environment starts with its runtime's diagnostics off
    /// (never held at its start, then, since nothing could let it go); null when they are on.</summary>
    internal static string? DiagnosticsOff(IReadOnlyDictionary<string, string> environment) => HeldStart.DebuggerOff(environment.GetValueOrDefault);

    /// <summary>Where a service answers: its project resource's URL as the AppHost's resource service gives it, HTTP first;
    /// null before the AppHost's resource service is reached, and for a resource with none.</summary>
    public override async Task<string?> AddressAsync(IRunService service, CancellationToken cancel)
    {
        if (Interactions is not { } interactions)
        {
            return null;
        }
        IReadOnlyList<AspireResource> resources = await interactions.GetResourcesAsync();
        return resources.FirstOrDefault(r => r.IsProject && r.Source is { } source && PathNames.Same(source, service.Project) && r.IsNamed(service.Name)) is { } resource
            && AspireModel.Links(resource) is { Count: > 0 } links
            ? (links.FirstOrDefault(l => l.Scheme == "http") ?? links[0]).GetLeftPart(UriPartial.Authority) : null;
    }

    /// <summary>Restarts services through the AppHost, one after another, each waited for until its next start has started
    /// and been attached to (a held one) as it started; built between its stop and its start when asked.</summary>
    public override async Task<string?> RestartAsync(IReadOnlyList<IRunService> services, CancellationToken cancel, string? why = null, bool build = false)
    {
        foreach (IRunService service in services)
        {
            // The start given, whose program its ended session may have ended with it, else the newest of its name.
            if ((service as AspireResourceRun ?? ServiceNamed(service.Name)) is not AspireResourceRun running)
            {
                return $"{service.Name} of {Name} is not running";
            }
            Console.Append($"Restarting {service.Name}{(why is null ? string.Empty : $": {why}")}\n");
            Task<string?> next = NextStart(service.Name);
            await (Restarting ?? throw new InvalidOperationException("Aspire resource restart is unavailable"))(this, running, build, cancel);
            TimeSpan limit = Waits.Of(StartLimit);
            try
            {
                if (await next.WaitAsync(limit, cancel) is { } failed)
                {
                    return $"{service.Name} did not start again: {failed}";
                }
            }
            catch (TimeoutException)
            {
                return $"{service.Name} did not start again within {limit.TotalMinutes:0.#} minutes of its restart";
            }
        }
        return null;
    }

    /// <summary>How long a restart waits for its service's next start, which DCP may take a while to ask for.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(3);

    /// <summary>A service's next start, its failure or null.</summary>
    private Task<string?> NextStart(string name)
    {
        if (!nextStarts.TryGetValue(name, out TaskCompletionSource<string?>? next))
        {
            nextStarts[name] = next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return next.Task;
    }

    /// <summary>A service's start is under way: numbered, and, its first in this run, what it is held at its start for decided
    /// (<paramref name="first"/>: a person's choice in an earlier run, else the configuration's and DCP's), the model's hold
    /// from then on. What it is held for now.</summary>
    internal RunStart? Starting(AspireResourceRun resource, Func<RunStart?> first)
    {
        resource.Sequence = ++started;
        string name = ((IRunService)resource).Name;
        unstarted.Remove(name);
        if (decided.Add(name) && first() is { } held)
        {
            Brought([(resource, held)]);
        }
        return HeldFor(name);
    }

    /// <summary>Whether a service's start is its first in this run and its configuration says Don't start it: refused then,
    /// and decided from then on, so a start asked later (the Aspire window's) runs it, unheld.</summary>
    internal bool LeftUnstarted(string name, bool dontStart)
    {
        if (!dontStart || !decided.Add(name))
        {
            return false;
        }
        unstarted.Add(name);
        return true;
    }

    /// <summary>Whether a resource is a service the run left unstarted, as its configuration says Don't start: DCP's
    /// FailedToStart for the start the run refused, by the service's name (its display name, or the resource's name with
    /// DCP's suffix).</summary>
    public bool IsLeftUnstarted(AspireResource resource) => UnstartedName(resource) is not null;

    /// <summary>The name of the service a resource is that the run left unstarted (<see cref="IsLeftUnstarted"/>), or null.</summary>
    public string? UnstartedName(AspireResource resource) => resource.State == "FailedToStart" ? unstarted.FirstOrDefault(name =>
        resource.IsNamed(name)) : null;

    /// <summary>A resource's state as Passenger says it everywhere: <see cref="NotStarted"/> for one the run left unstarted,
    /// else DCP's own.</summary>
    public string? StateOf(AspireResource resource) => IsLeftUnstarted(resource) ? NotStarted : resource.State;

    /// <summary>A service's start has landed: started, attached to where held, or failed (<paramref name="failure"/>).</summary>
    internal void Landed(AspireResourceRun resource, string? failure)
    {
        if (nextStarts.Remove(((IRunService)resource).Name, out TaskCompletionSource<string?>? next))
        {
            next.TrySetResult(failure);
        }
    }

    /// <summary>A person's choice of a service: debugged from its next start, or run without the debugger.</summary>
    internal void Choose(string name, bool debugging)
    {
        decided.Add(name);
        IdeErrors.Observe(HoldAsync(name, debugging ? RunStart.Debug : null, CancellationToken.None));
    }

    /// <summary>The service's start the session attached to keeps its tab (which the Aspire window chooses) and session.</summary>
    public override void Attached(IRunService service, ServiceProgram program, DebugTab? tab, DebugSession? debugged)
    {
        if (Resources.Values.FirstOrDefault(resource => resource.ProcessId == program.Id) is not { } run)
        {
            return;
        }
        run.DebugTab = tab;
        run.Debugged = debugged;
        SessionsChanged?.Invoke(this, EventArgs.Empty);
        if (tab is not null)
        {
            tab.StateChanged += (_, _) =>
            {
                if (run.DebugTab == tab && tab.Session is { State: DebugState.Ended })
                {
                    run.DebugTab = null;
                    SessionsChanged?.Invoke(this, EventArgs.Empty);
                }
            };
        }
    }

    /// <summary>A service's start was attached to, or its session ended: what an agent's wait for a resource's session wakes on.</summary>
    public event EventHandler? SessionsChanged;
}

/// <summary>One start of a service DCP asked the IDE for, a service of its AppHost's run (<see cref="IRunService"/>, by its
/// resource's name): its program, console, session and inspected app.</summary>
public sealed class AspireResourceRun(string id, string instanceId, string project, LaunchPlan plan) : IRunService
{
    public string Id { get; } = id;
    public string InstanceId { get; } = instanceId;
    public string Project { get; } = project;
    public LaunchPlan Plan { get; } = plan;
    /// <summary>Its build's output as it started (each assembly's, symbols' and settings' name, length and time), read off
    /// the UI thread before it starts.</summary>
    internal string BuiltStamp { get; init; } = string.Empty;

    /// <summary>Whether Rerun builds it again and restarts it: its output changed since it started (a build of another's),
    /// or a file its build reads was written after the build it runs began (<paramref name="since"/>). Reads the disk: off
    /// the UI thread.</summary>
    internal bool ChangedSinceStart(IReadOnlyList<ProjectModel> projects, DateTime since) =>
        BuiltStamp != ReadBuiltStamp(Plan) || BuiltInputs.ChangedSince(Project, since, projects);

    public string? ResourceName { get; internal set; }
    internal string IdentityKey => Plan.Environment.GetValueOrDefault("OTEL_RESOURCE_ATTRIBUTES") is { Length: > 0 } attributes
        ? $"{Project}\n{attributes}" : ResourceName ?? Project;
    public int? ProcessId { get; internal set; }
    public DebugTab? DebugTab { get; internal set; }

    /// <summary>The session attached to this start (in its tab, or quietly for Hot Reload), which its end lets go.</summary>
    internal DebugSession? Debugged { get; set; }

    /// <summary>What it started held for (the debugger, quietly for Hot Reload), or null when it started unheld.</summary>
    internal RunStart? Held { get; set; }

    /// <summary>Its start's number in its AppHost's run, the newest of a name its service's program.</summary>
    internal long Sequence { get; set; }
    public LiveApp? Inspection { get; internal set; }
    public OutputView Console { get; } = new();
    internal ProcessRunner? Runner { get; set; }
    internal Task? Ending { get; set; }
    internal CancellationTokenSource Stopping { get; } = new();

    string IRunService.Name => ResourceName ?? Path.GetFileNameWithoutExtension(Project);

    bool IRunService.Starts => true;

    /// <summary>Its build's output as it stands, the run of services' one stamp (<see cref="BuiltInputs.Stamp"/>).</summary>
    internal static string ReadBuiltStamp(LaunchPlan launch) => BuiltInputs.Stamp(Path.GetDirectoryName(launch.Built ?? launch.Program));
}
