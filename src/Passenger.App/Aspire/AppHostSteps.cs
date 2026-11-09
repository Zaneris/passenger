using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Lsp;
using Passenger.Workspace;

namespace Passenger.App.Aspire;

/// <summary>What an AppHost's pipeline read came to: its steps, or why none were read.</summary>
public sealed record AppHostStepsRead(IReadOnlyList<AppHostStep> Steps, string? Problem);

/// <summary>A deploy step Publish offers: its name and words as the AppHost declares them, what it needs, and what is missing
/// here for it to run, a login ("needs the Azure CLI's login, …"); null when nothing is.</summary>
public sealed record DeployStep(string Name, string? Description, DeployNeeds Needs, string? Missing)
{
    /// <summary>Why it cannot run here, its name first; null when it can.</summary>
    public string? WhyNot => Missing is null ? null : $"{Name} {Missing}";
}

/// <summary>The deploy steps Publish offers for an AppHost, or why it offers none.</summary>
public sealed record DeployOffer(IReadOnlyList<DeployStep> Steps, string? Problem);

/// <summary>An AppHost's steps read from the AppHost itself, as Aspire's CLI reads them for <c>aspire do --list-steps</c>:
/// run with <see cref="AppHostPipeline.ListArguments"/> and a backchannel socket of Passenger's (<see
/// cref="AspireVariables.BackchannelPath"/>) it listens on, asked whether it lists its steps, then for them, then to stop,
/// and ended with all it started however the read went. Only an AppHost of an Aspire that lists its steps is asked (<see
/// cref="AppHostPipeline.WhyNotListed"/>): one before it would take the operation for another.</summary>
public static class AppHostSteps
{
    /// <summary>The longest the AppHost is given to build, start and answer.</summary>
    private static readonly TimeSpan ReadLimit = TimeSpan.FromMinutes(3);

    /// <summary>How often the socket the AppHost makes is looked for while it builds and starts.</summary>
    private static readonly TimeSpan LookEvery = TimeSpan.FromMilliseconds(200);

    /// <summary>The longest the AppHost is given to stop by itself once asked, before it is ended.</summary>
    private static readonly TimeSpan StopLimit = TimeSpan.FromSeconds(5);

    /// <summary>The most of the AppHost's last words a failed read quotes.</summary>
    private const int WordsKept = 12;

    private static int made;

    /// <summary>The deploy steps an AppHost declares, each with why it cannot run here where a login it needs is missing
    /// (the engine asked once, where a step needs it); none, saying why, for one that cannot be asked or deploys nothing.</summary>
    public static async Task<DeployOffer> OfferAsync(ProjectModel host, Containers.ContainerEngine engine, CancellationToken cancel)
    {
        AppHostStepsRead read = await ReadAsync(host.Path, HostingVersionOf(host), host.WindowsTargeting, cancel);
        if (read.Problem is { } problem)
        {
            return new DeployOffer([], problem);
        }
        IReadOnlyList<AppHostStep> deploy = AppHostPipeline.DeploySteps(read.Steps);
        if (deploy.Count == 0)
        {
            return new DeployOffer([], "its code adds nothing to deploy to (AddDockerComposeEnvironment, an Azure environment or AddKubernetesEnvironment adds one)");
        }
        DeployNeeds[] needs = [.. deploy.Select(step => AppHostPipeline.Needs(step.Name, read.Steps))];
        string? engineProblem = needs.Any(n => n.HasFlag(DeployNeeds.ContainerEngine)) && await engine.StateAsync(cancel: cancel) is { IsReady: false } state
            ? state.Problem ?? $"{state.EngineName} does not answer"
            : null;
        return new DeployOffer([.. deploy.Select((step, i) => new DeployStep(step.Name, step.Description, needs[i], DeployLogins.Missing(needs[i], engineProblem)))], null);
    }

    /// <summary>The Aspire an AppHost hosts with: its Aspire.Hosting reference's version, else its AppHost SDK's.</summary>
    public static string? HostingVersionOf(ProjectModel host) =>
        host.Packages.Where(p => (p.Name.Equals(HostingPackage, StringComparison.OrdinalIgnoreCase) || p.Name.Equals(AppHostPackage, StringComparison.OrdinalIgnoreCase))
            && p.Version is { Length: > 0 }).Select(p => p.Version).FirstOrDefault()
        ?? host.Property(Host.CarriedProperties.AspireHostingSDKVersion);

    private const string HostingPackage = "Aspire.Hosting";

    private const string AppHostPackage = "Aspire.Hosting.AppHost";

    /// <summary>The steps of an AppHost of a hosting version, or why none: its version, a build that failed (its last words
    /// said), an AppHost that ended or answered nothing within the limit.</summary>
    public static async Task<AppHostStepsRead> ReadAsync(string project, string? hostingVersion, bool windowsTargeting, CancellationToken cancel)
    {
        if (AppHostPipeline.WhyNotListed(hostingVersion) is { } why)
        {
            return new AppHostStepsRead([], why);
        }
        string socket;
        try
        {
            socket = Path.Combine(SocketFolders.Own(StateStore.DefaultRoot), $"{Environment.ProcessId}-{Interlocked.Increment(ref made)}.backchannel");
            // Only a dead Passenger that had this process's number leaves one by this name.
            File.Delete(socket);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new AppHostStepsRead([], $"its steps cannot be asked for: {error.Message}");
        }
        if (!SocketFolders.Fits(socket))
        {
            return new AppHostStepsRead([], $"its steps cannot be asked for: the backchannel's socket is too long, as {SocketFolders.TooLong(socket)}");
        }
        Queue<string> said = new();
        void Heard(string line)
        {
            lock (said)
            {
                said.Enqueue(line);
                if (said.Count > WordsKept)
                {
                    said.Dequeue();
                }
            }
        }
        string Said()
        {
            lock (said)
            {
                return said.Count == 0 ? string.Empty
                    : $": {Containers.ContainerSecrets.InSaid(string.Join(' ', said.Select(line => line.Trim()).Where(line => line.Length > 0).TakeLast(3)), Path.GetDirectoryName(project))}";
            }
        }
        Process host;
        try
        {
            ProcessStartInfo start = ChildProcesses.DotnetCommand(["run", "--project", project, "--no-launch-profile",
                .. windowsTargeting ? (string[])[$"-p:{MSBuildNames.EnableWindowsTargeting}=true"] : [], "--", .. AppHostPipeline.ListArguments],
                Path.GetDirectoryName(project));
            start.Environment[AspireVariables.BackchannelPath] = socket;
            start.Environment[AspireVariables.InteractivityEnabled] = "false";
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            host = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            return new AppHostStepsRead([], $"it did not start: {error.Message}");
        }
        ChildProcesses.HoldFamily(host);
        Task heard = ChildProcesses.Hear(host, Heard, Heard, longest: 2000);
        IdeErrors.Observe(heard);
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(Waits.Of(ReadLimit));
        try
        {
            using Socket connected = await ConnectAsync(socket, host, limit.Token);
            using JsonRpcPeer peer = new(new NetworkStream(connected, ownsSocket: false));
            peer.Start();
            string[]? can = await peer.AskAsync<string[]>(AppHostPipeline.CapabilitiesRequest, new Dictionary<string, object?>(), tellCanceled: false, limit.Token);
            if (can is null || !can.Contains(AppHostPipeline.ListsSteps))
            {
                return new AppHostStepsRead([], "it does not list its steps over Aspire's backchannel");
            }
            JsonElement answer = await peer.AskAsync<JsonElement>(AppHostPipeline.StepsRequest, new Dictionary<string, object?> { ["request"] = null },
                tellCanceled: false, limit.Token);
            IReadOnlyList<AppHostStep> steps = AppHostPipeline.Read(answer);
            await StopAsync(peer, host);
            return new AppHostStepsRead(steps, null);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new AppHostStepsRead([], $"it gave no steps within {Waits.Of(ReadLimit).TotalMinutes:0} minutes{Said()}");
        }
        catch (JsonRpcFailed failed)
        {
            return new AppHostStepsRead([], $"its backchannel said: {failed.Message}");
        }
        catch (AppHostEnded)
        {
            await heard.Ended();
            return new AppHostStepsRead([], $"it ended before it listed its steps{Said()}");
        }
        catch (Exception error) when (error is SocketException or IOException)
        {
            return new AppHostStepsRead([], $"its backchannel failed: {error.Message}");
        }
        finally
        {
            ChildProcesses.Kill(host);
            host.Dispose();
            try
            {
                File.Delete(socket);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Left for the next of this process's number to take away.
            }
        }
    }

    /// <summary>The AppHost's socket connected to once it listens, looked for while the AppHost builds and starts.</summary>
    private static async Task<Socket> ConnectAsync(string path, Process host, CancellationToken cancel)
    {
        while (true)
        {
            if (File.Exists(path))
            {
                Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancel);
                    return socket;
                }
                catch (SocketException)
                {
                    // Made but not yet listened on: looked for again.
                    socket.Dispose();
                }
            }
            if (!DotnetProcesses.Runs(host.Id))
            {
                throw new AppHostEnded();
            }
            await Task.Delay(LookEvery, cancel);
        }
    }

    /// <summary>The AppHost asked to stop, and given a moment to, before it is ended all the same.</summary>
    private static async Task StopAsync(JsonRpcPeer peer, Process host)
    {
        using CancellationTokenSource stopping = new(StopLimit);
        try
        {
            await peer.AskAsync<JsonElement>(AppHostPipeline.StopRequest, new Dictionary<string, object?>(), tellCanceled: false, stopping.Token);
            await host.WaitForExitAsync(stopping.Token);
        }
        catch (Exception error) when (error is JsonRpcFailed or OperationCanceledException)
        {
            // Ended below all the same.
        }
    }

    /// <summary>The AppHost ended before it listened.</summary>
    private sealed class AppHostEnded : Exception;
}
