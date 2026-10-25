using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.Workspace;

/// <summary>The project host could not answer: it would not start (no SDK for the folder, no runtime that runs it), it
/// ended while a request waited, or the request failed in it. The one failure a caller of <see cref="ProjectHost"/>
/// catches.</summary>
public sealed class ProjectHostFailed(string reason, Exception? inner = null) : Exception(reason, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>One project host (Passenger.Host, carried under <c>host/</c>): MSBuild's evaluation in a process of its
/// own, run by the machine's dotnet on its newest runtime (prereleases included), with the SDK a folder picks loaded.
/// Requests go out as lines of JSON and their answers, partial ones first, come back on the reader's thread: whatever a
/// caller does with them on the UI thread it crosses there itself.</summary>
public sealed class ProjectHost : IAsyncDisposable
{
    private readonly Process process;

    /// <summary>The requests waiting for their answers, each with what takes its partial answers.</summary>
    private readonly PendingRequests<Action<JsonElement>?, JsonElement> pending = new();
    private readonly object writing = new();

    /// <summary>The methods whose next answers are spoiled (<see cref="SpoilNext"/>), and the requests now asked of them.</summary>
    private readonly Dictionary<string, int> spoiling = new(StringComparer.Ordinal);
    private readonly HashSet<int> spoiled = [];

    private volatile bool gone;

    /// <summary>The folder the host was started for, with the global.json over it as it stood (<see cref="Dotnet.GlobalJsonOver"/>).</summary>
    private readonly (string Folder, string GlobalJson) startedFor;

    private ProjectHost(Process process, HostReady ready, string folder, string globalJson)
    {
        this.process = process;
        ProcessId = process.Id;
        Sdk = ready.Sdk;
        Version = ready.Version;
        startedFor = (folder, globalJson);
    }

    /// <summary>Whether its SDK is the one a folder picks, known without asking dotnet: the host was started for that folder,
    /// whose global.json stands as it did, and found the SDK as dotnet does there.</summary>
    public bool StartedFor(string folder) => PathNames.Same(startedFor.Folder, folder) && Dotnet.GlobalJsonOver(folder) == startedFor.GlobalJson;

    /// <summary>Where the app carries the host.</summary>
    public static string Assembly => Path.Combine(AppContext.BaseDirectory, "host", "Passenger.Host.dll");

    /// <summary>The SDK's folder whose MSBuild the host loaded, which every folder it evaluates for picks too.</summary>
    public string Sdk { get; }

    public string Version { get; }

    public int ProcessId { get; }

    public bool HasExited => gone || process.HasExited;

    /// <summary>Starts a host with the SDK a folder picks and waits for it to say which it loaded; a
    /// <see cref="ProjectHostFailed"/> when it loads none.</summary>
    public static async Task<ProjectHost> StartAsync(string folder, CancellationToken cancel = default)
    {
        if (!File.Exists(Assembly))
        {
            throw new ProjectHostFailed("the project host is missing from the app's folder");
        }
        // As it stands before the host reads it, so a change while it starts is a change since.
        string globalJson = Dotnet.GlobalJsonOver(folder);
        ProcessStartInfo start;
        try
        {
            // Run in the IDE's folder, not the one it evaluates for, which may be deleted under it.
            start = ChildProcesses.DotnetCommand([Assembly, "--folder", folder]);
        }
        catch (FileNotFoundException missing)
        {
            throw new ProjectHostFailed(missing.Message, missing);
        }
        // The newest runtime there is, a prerelease too, so the SDK a folder picks can always load in it.
        start.Environment[RuntimeVariables.RollForwardToPrerelease] = "1";
        // A build's own, from a Passenger started under one (dotnet run, dotnet test): the host finds its SDK itself.
        foreach (string variable in new[] { "MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath", "MSBuildLoadMicrosoftTargetsReadOnly" })
        {
            start.Environment.Remove(variable);
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process process;
        try
        {
            process = await ChildProcesses.StartAsync(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            throw new ProjectHostFailed($"the project host could not start: {error.Message}", error);
        }
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                PassengerLog.Write($"Project host (process {process.Id}): {line}");
            }
        };
        process.BeginErrorReadLine();
        try
        {
            string? first = await process.StandardOutput.ReadLineAsync(cancel).AsTask().WaitAsync(TimeSpan.FromMinutes(1), cancel);
            HostReply reply = first is null
                ? throw new ProjectHostFailed($"the project host ended as it started ({Ended(process)})")
                : JsonSerializer.Deserialize<HostReply>(first, HostJson.Options) ?? throw new ProjectHostFailed("the project host said nothing as it started");
            if (reply.Error is { } error)
            {
                throw new ProjectHostFailed(error);
            }
            HostReady ready = reply.Result?.Deserialize<HostReady>(HostJson.Options) ?? throw new ProjectHostFailed("the project host did not say which SDK it loaded");
            ProjectHost host = new(process, ready, folder, globalJson);
            UnawaitedWork.Observe(Task.Run(host.ReadAsync));
            return host;
        }
        catch (Exception error)
        {
            ChildProcesses.Kill(process);
            process.Dispose();
            throw error switch
            {
                ProjectHostFailed => error,
                TimeoutException => new ProjectHostFailed("the project host did not start within a minute", error),
                JsonException => new ProjectHostFailed("the project host's first line does not read", error),
                _ => error,
            };
        }
    }

    private static string Ended(Process process)
    {
        try
        {
            return process.WaitForExit(2000) ? $"exit code {process.ExitCode}" : "still running";
        }
        catch (InvalidOperationException)
        {
            return "gone";
        }
    }

    /// <summary>Evaluates projects, handing each over as it is evaluated (a multi-targeted one twice: its outer evaluation,
    /// then complete), on the reader's thread; done once every one has been.</summary>
    public async Task<EvaluateDone> EvaluateAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, Action<EvaluatedProject> evaluated, CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Evaluate, new EvaluateParams(projects, properties),
            partial => evaluated(partial.Deserialize<EvaluatedProject>(HostJson.Options)!), cancel)).Deserialize<EvaluateDone>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for its evaluation");

    /// <summary>Builds projects at design time, handing each over as its builds end, on the reader's thread; done once
    /// every one has been.</summary>
    public async Task<BuildDone> BuildAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, Action<BuiltProject> built, CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Build, new BuildParams(projects, properties),
            partial => built(partial.Deserialize<BuiltProject>(HostJson.Options)!), cancel)).Deserialize<BuildDone>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for its builds");

    /// <summary>Evaluates one project for the code help in its MSBuild files, the open files' unsaved texts read in
    /// place of the disk's.</summary>
    public async Task<ProjectInsight> InspectAsync(string project, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> texts,
        CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Inspect, new InspectParams(project, properties, texts), null, cancel)).Deserialize<ProjectInsight>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for the project's evaluation");

    /// <summary>Some properties of one project as a build with these global properties sees them (a publish profile's,
    /// imported by PublishProfile), each that has a value; or why the project did not evaluate.</summary>
    public async Task<PropertiesAnswer> PropertiesAsync(string project, IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> names,
        CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Properties, new PropertiesParams(project, properties, names), null, cancel)).Deserialize<PropertiesAnswer>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for the project's properties");

    /// <summary>What a build of one project with these global properties reads and makes, as its evaluations say, or why a
    /// build of it reads more than that.</summary>
    public async Task<BuildInputs> InputsAsync(string project, IReadOnlyDictionary<string, string> properties, CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Inputs, new InputsParams(project, properties), null, cancel)).Deserialize<BuildInputs>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for what the project's build reads");

    /// <summary>Holds the host's design-time builds of WPF projects off for a build of the IDE's own: done once none runs,
    /// those still running named to <paramref name="waiting"/> first (on the reader's thread), and none starts again until
    /// <paramref name="release"/> is cancelled.</summary>
    public async Task HoldAsync(Action<IReadOnlyList<string>> waiting, CancellationToken release)
    {
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<JsonElement> asked = AskAsync(HostMethods.Hold, new HoldParams(), partial =>
        {
            HoldState state = partial.Deserialize<HoldState>(HostJson.Options)!;
            if (state.Held)
            {
                held.TrySetResult();
            }
            else
            {
                waiting(state.Running);
            }
        }, release);
        // Answered only as it ends: released, or with the host.
        UnawaitedWork.Observe(asked.ContinueWith(ended =>
        {
            if (ended.Exception?.InnerException is { } failed)
            {
                held.TrySetException(failed);
            }
            else
            {
                held.TrySetCanceled(release);
            }
        }, TaskScheduler.Default));
        await held.Task;
    }

    private async Task<JsonElement> AskAsync(string method, object parameters, Action<JsonElement>? partial, CancellationToken cancel)
    {
        (int id, Task<JsonElement> answered) = pending.Add(partial);
        lock (spoiling)
        {
            if (spoiling.GetValueOrDefault(method) > 0)
            {
                spoiling[method]--;
                spoiled.Add(id);
            }
        }
        Send(id, method, parameters);
        // Given up by the caller, the host is told to end the request, and what it still sends of it is dropped.
        using (cancel.Register(() =>
        {
            pending.Forget(id, answered, cancel);
            Send(pending.NextId(), HostMethods.Cancel, new CancelParams(id));
        }))
        {
            return await answered;
        }
    }

    /// <summary>A request on its way to the host, whose answer, if any, is matched by its id.</summary>
    private void Send(int id, string method, object parameters)
    {
        string line = JsonSerializer.Serialize(new HostRequest(id, method, JsonSerializer.SerializeToElement(parameters, parameters.GetType(), HostJson.Options)), HostJson.Options);
        try
        {
            lock (writing)
            {
                process.StandardInput.WriteLine(line);
                process.StandardInput.Flush();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Fail($"the project host could not be asked: {error.Message}");
        }
    }

    /// <summary>Has the host's answer to the next request of a method read as a line that does not, as a host of
    /// another build could write one.</summary>
    public void SpoilNext(string method)
    {
        lock (spoiling)
        {
            spoiling[method] = spoiling.GetValueOrDefault(method) + 1;
        }
    }

    /// <summary>Reads the host's answers until it ends. Anything else that ends the reading (a line that does not read, an
    /// answer its caller could not take) leaves what the host says next unmatched to what was asked, which would wait for
    /// good: the host is ended, every request fails, and the next is asked of a host started anew.</summary>
    private async Task ReadAsync()
    {
        string why = "the project host ended";
        try
        {
            // Taken once: a disposed process gives no reader, where the reader taken says it was closed.
            StreamReader output = process.StandardOutput;
            await foreach (HostReply reply in JsonLines.ReadAsync(output, HostJson.Reply, static (_, unread) => throw unread))
            {
                lock (spoiling)
                {
                    if (spoiled.Contains(reply.Id))
                    {
                        throw new JsonException("the line was spoiled, as a test asked");
                    }
                }
                if (reply.Partial is { } partial)
                {
                    if (pending.TryGet(reply.Id, out Action<JsonElement>? taken))
                    {
                        taken?.Invoke(partial);
                    }
                }
                else if (reply.Error is { } error)
                {
                    pending.Fail(reply.Id, _ => new ProjectHostFailed(error));
                }
                else
                {
                    pending.Answer(reply.Id, reply.Result ?? default);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException || gone)
        {
            // The pipe went with the process, or with the host let go of.
        }
        catch (Exception error)
        {
            why = $"the project host's answer could not be read ({error.GetType().Name}: {error.Message})";
            PassengerLog.Write($"Project host (process {ProcessId}) was ended: {why}");
            ChildProcesses.Kill(process);
        }
        Fail(why);
    }

    /// <summary>Fails every request waiting, and each asked from now on.</summary>
    private void Fail(string why)
    {
        gone = true;
        pending.End(_ => new ProjectHostFailed(why));
    }

    /// <summary>Closes the host's input, which ends it, and ends it outright if it has not gone a moment later.</summary>
    public async ValueTask DisposeAsync()
    {
        Fail("the project host has ended");
        ChildProcesses.Ending(process);
        try
        {
            process.StandardInput.Close();
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
        }
        ChildProcesses.Kill(process);
        process.Dispose();
    }
}

/// <summary>The process's project hosts, one for each SDK its folders pick: every window whose folder picks the same SDK
/// shares one (a host loads one SDK's MSBuild, for good), a host that ended is started again when next asked for, and
/// each ends with the IDE.</summary>
public static class ProjectHosts
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly List<ProjectHost> Running = [];

    /// <summary>The host for a folder's SDK: one running already, else one started for it.</summary>
    public static async Task<ProjectHost> ForAsync(string folder, CancellationToken cancel = default)
    {
        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Running.RemoveAll(h => h.HasExited);
            if (Running.FirstOrDefault(h => h.StartedFor(folder)) is { } own)
            {
                return own;
            }
            if (Running.Count > 0)
            {
                string? wanted;
                try
                {
                    wanted = await Dotnet.SdkAsync(folder, cancel).ConfigureAwait(false);
                }
                catch (Exception unnamed) when (unnamed is InvalidOperationException or IOException or TimeoutException)
                {
                    // The host started for the folder says why it loads no SDK.
                    wanted = null;
                }
                // A host's SDK is MSBuild's folder, named for its version under the dotnet's sdk folder.
                if (wanted is not null && Running.FirstOrDefault(h => Path.GetFileName(Path.TrimEndingDirectorySeparator(h.Sdk)) == wanted) is { } same)
                {
                    return same;
                }
            }
            ProjectHost started = await ProjectHost.StartAsync(folder, cancel).ConfigureAwait(false);
            PassengerLog.Write($"Project host (process {started.ProcessId}) started with the .NET SDK {started.Version} for {folder}");
            Running.Add(started);
            return started;
        }
        finally
        {
            Gate.Release();
        }
    }
}
