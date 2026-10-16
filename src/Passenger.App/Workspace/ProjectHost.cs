using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.App.Workspace;

/// <summary>The project host could not answer: it would not start (no SDK for the folder, no runtime that runs it), it
/// ended while a request waited, or the request failed in it. The one failure a caller of <see cref="ProjectHost"/>
/// catches.</summary>
public sealed class ProjectHostFailed(string reason, Exception? inner = null) : Exception(reason, inner)
{
    public string Reason { get; } = reason;
}

/// <summary>One project host (Passenger.Host, carried under <c>host/</c>): MSBuild's evaluation in a process of its own,
/// run by the machine's dotnet on its newest runtime (prereleases included, as Roslyn runs its build host), with the SDK
/// a folder picks loaded. Requests go out as lines of JSON and their answers, partial ones first, come back on the
/// reader's thread: whatever a caller does with them on the UI thread it crosses there itself.</summary>
public sealed class ProjectHost : IAsyncDisposable
{
    private readonly Process process;
    private readonly Dictionary<int, Pending> pending = [];
    private readonly object writing = new();
    private int next;
    private volatile bool gone;

    private ProjectHost(Process process, HostReady ready)
    {
        this.process = process;
        Sdk = ready.Sdk;
        Version = ready.Version;
    }

    /// <summary>Where the app carries the host.</summary>
    public static string Assembly => Path.Combine(AppContext.BaseDirectory, "host", "Passenger.Host.dll");

    /// <summary>The SDK's folder whose MSBuild the host loaded, which every folder it evaluates for picks too.</summary>
    public string Sdk { get; }

    public string Version { get; }

    public int ProcessId => process.Id;

    public bool HasExited => gone || process.HasExited;

    /// <summary>Starts a host with the SDK a folder picks and waits for it to say which it loaded; a
    /// <see cref="ProjectHostFailed"/> when it loads none.</summary>
    public static async Task<ProjectHost> StartAsync(string folder, CancellationToken cancel = default)
    {
        if (!File.Exists(Assembly))
        {
            throw new ProjectHostFailed("the project host is missing from the app's folder");
        }
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
        start.Environment["DOTNET_ROLL_FORWARD_TO_PRERELEASE"] = "1";
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
            process = ChildProcesses.Start(start);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException)
        {
            throw new ProjectHostFailed($"the project host could not start: {error.Message}", error);
        }
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                IdeErrors.Write($"Project host (process {process.Id}): {line}");
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
            ProjectHost host = new(process, ready);
            _ = Task.Run(host.ReadAsync);
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

    /// <summary>The SDK a folder's projects would load with, which a host of another SDK cannot evaluate them for.</summary>
    public async Task<SdkAnswer> SdkForAsync(string folder, CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Sdk, new SdkParams(folder), null, cancel)).Deserialize<SdkAnswer>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for the SDK");

    /// <summary>Evaluates projects, handing each over as it is evaluated (a multi-targeted one twice: its outer evaluation,
    /// then complete), on the reader's thread; done once every one has been.</summary>
    public async Task<EvaluateDone> EvaluateAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, Action<EvaluatedProject> evaluated, CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Evaluate, new EvaluateParams(projects, properties),
            partial => evaluated(partial.Deserialize<EvaluatedProject>(HostJson.Options)!), cancel)).Deserialize<EvaluateDone>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for its evaluation");

    /// <summary>Builds projects at design time, as Roslyn's server would, handing each over as its builds end, on the
    /// reader's thread; done once every one has been.</summary>
    public async Task<BuildDone> BuildAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, Action<BuiltProject> built, CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Build, new BuildParams(projects, properties),
            partial => built(partial.Deserialize<BuiltProject>(HostJson.Options)!), cancel)).Deserialize<BuildDone>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for its builds");

    /// <summary>Evaluates one project for the code help in its MSBuild files, the open files' unsaved texts read in place of
    /// the disk's; the host answers again from its cache while what the last answer read holds.</summary>
    public async Task<ProjectInsight> InspectAsync(string project, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> texts,
        CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.Inspect, new InspectParams(project, properties, texts), null, cancel)).Deserialize<ProjectInsight>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for the project's evaluation");

    /// <summary>Reads original imports without a global hook overriding them, through every referenced framework.</summary>
    public async Task<WpfBuildPrepared> PrepareWpfBuildAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> designTimeProperties,
        CancellationToken cancel = default) =>
        (await AskAsync(HostMethods.WpfBuild, new WpfBuildParams(projects, properties, designTimeProperties), null, cancel)).Deserialize<WpfBuildPrepared>(HostJson.Options)
            ?? throw new ProjectHostFailed("the project host gave no answer for the WPF population build hook");

    private async Task<JsonElement> AskAsync(string method, object parameters, Action<JsonElement>? partial, CancellationToken cancel)
    {
        TaskCompletionSource<JsonElement> answered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int id;
        lock (pending)
        {
            if (gone)
            {
                throw new ProjectHostFailed("the project host has ended");
            }
            id = ++next;
            pending[id] = new Pending(answered, partial);
        }
        Send(id, method, parameters);
        // Given up by the caller, the host is told to end the request, and what it still sends of it is dropped.
        using (cancel.Register(() =>
        {
            int cancelling;
            lock (pending)
            {
                pending.Remove(id);
                cancelling = ++next;
            }
            answered.TrySetCanceled(cancel);
            Send(cancelling, HostMethods.Cancel, new CancelParams(id));
        }))
        {
            return await answered.Task;
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

    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                HostReply? reply;
                try
                {
                    reply = JsonSerializer.Deserialize<HostReply>(line, HostJson.Options);
                }
                catch (JsonException error)
                {
                    IdeErrors.Write($"Project host (process {process.Id}) wrote a line that does not read: {error.Message}");
                    continue;
                }
                if (reply is null)
                {
                    continue;
                }
                Pending? asked;
                lock (pending)
                {
                    pending.TryGetValue(reply.Id, out asked);
                    if (asked is not null && reply.Partial is null)
                    {
                        pending.Remove(reply.Id);
                    }
                }
                if (asked is null)
                {
                    continue;
                }
                if (reply.Partial is { } partial)
                {
                    asked.Partial?.Invoke(partial);
                }
                else if (reply.Error is { } error)
                {
                    asked.Answered.TrySetException(new ProjectHostFailed(error));
                }
                else
                {
                    asked.Answered.TrySetResult(reply.Result ?? default);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe went with the process.
        }
        Fail("the project host ended");
    }

    private void Fail(string why)
    {
        List<Pending> failed;
        lock (pending)
        {
            gone = true;
            failed = [.. pending.Values];
            pending.Clear();
        }
        foreach (Pending asked in failed)
        {
            asked.Answered.TrySetException(new ProjectHostFailed(why));
        }
    }

    /// <summary>Closes the host's input, which ends it, and ends it outright if it has not gone a moment later.</summary>
    public async ValueTask DisposeAsync()
    {
        gone = true;
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

    private sealed record Pending(TaskCompletionSource<JsonElement> Answered, Action<JsonElement>? Partial);
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
            if (Running.FirstOrDefault() is { } asked)
            {
                SdkAnswer sdk;
                try
                {
                    sdk = await asked.SdkForAsync(folder, cancel).ConfigureAwait(false);
                }
                catch (ProjectHostFailed)
                {
                    sdk = new SdkAnswer(null, null);
                }
                if (sdk.Sdk is { } wanted && Running.FirstOrDefault(h => PathNames.Same(h.Sdk, wanted)) is { } same)
                {
                    return same;
                }
            }
            ProjectHost started = await ProjectHost.StartAsync(folder, cancel).ConfigureAwait(false);
            IdeErrors.Write($"Project host (process {started.ProcessId}) started with the .NET SDK {started.Version} for {folder}");
            Running.Add(started);
            return started;
        }
        finally
        {
            Gate.Release();
        }
    }
}
