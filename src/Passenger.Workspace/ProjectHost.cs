using System.Diagnostics;
using System.Text.Json;
using Passenger.Core;
using Passenger.Host;

namespace Passenger.Workspace;

/// <summary>The project host could not answer: it would not start (no SDK for the folder, no runtime that runs it), it
/// ended while a request waited, or the request failed in it. The one failure a caller of <see cref="ProjectHost"/>
/// catches.</summary>
public sealed class ProjectHostFailed(string reason, Exception? inner = null, bool hostEnded = false) : Exception(reason, inner)
{
    public string Reason { get; } = reason;

    /// <summary>The host's process ended under the request, by itself (it crashed, or was killed) or for a build in it
    /// that could never end (<see cref="ProjectHost.End"/>): a host started anew (<see cref="ProjectHosts.ForAsync"/>)
    /// may answer it.</summary>
    public bool HostEnded { get; } = hostEnded;
}

/// <summary>One project host (Passenger.Host, carried under <c>host/</c>): MSBuild's evaluation in a process of its
/// own, run by the machine's dotnet on its newest runtime (prereleases included), with the SDK a folder picks loaded.
/// Requests go out as lines of JSON and their answers, partial ones first, come back on the reader's thread: whatever a
/// caller does with them on the UI thread it crosses there itself.</summary>
public sealed class ProjectHost : IAsyncDisposable
{
    private readonly Process process;

    /// <summary>The requests waiting for their answers, each with what reads its partial answers and its result.</summary>
    private readonly PendingRequests<Asked, object?> pending = new();

    /// <summary>The host's output, a line at a time.</summary>
    private readonly Utf8Lines lines;
    private readonly object writing = new();

    /// <summary>The methods whose next answers are spoiled (<see cref="SpoilNext"/>), and the requests now asked of them.</summary>
    private readonly Dictionary<string, int> spoiling = new(StringComparer.Ordinal);
    private readonly HashSet<int> spoiled = [];

    private volatile bool gone;

    private int building;

    /// <summary>The first line of what the runtime wrote as the host crashed, for the words of its end.</summary>
    private volatile string? crashed;

    /// <summary>The folder the host was started for, with the global.json over it as it stood (<see cref="Dotnet.GlobalJsonOver"/>).</summary>
    private readonly (string Folder, string GlobalJson) startedFor;

    private ProjectHost(Process process, Utf8Lines lines, HostReady ready, string folder, string globalJson)
    {
        this.process = process;
        this.lines = lines;
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
        ProjectHost? started = null;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line)
            {
                PassengerLog.Write($"Project host (process {process.Id}): {line}");
                if (started is { crashed: null } host && line.StartsWith("Unhandled exception.", StringComparison.Ordinal))
                {
                    host.crashed = line.Length > 300 ? line[..300] + "…" : line;
                }
            }
        };
        process.BeginErrorReadLine();
        Utf8Lines lines = new(process.StandardOutput.BaseStream);
        try
        {
            ReadOnlyMemory<byte> first = await lines.NextAsync(cancel).AsTask().WaitAsync(TimeSpan.FromMinutes(1), cancel)
                ?? throw new ProjectHostFailed($"the project host ended as it started ({Ended(process)})");
            HostReady ready = Ready(first.Span);
            ProjectHost host = started = new(process, lines, ready, folder, globalJson);
            // Not held: it reads the host's answers for as long as the host lives.
            UnawaitedWork.Observe(BackgroundWork.Unheld(host.ReadAsync));
            return host;
        }
        catch (Exception error)
        {
            ChildProcesses.Kill(process);
            process.Dispose();
            lines.Dispose();
            throw error switch
            {
                ProjectHostFailed => error,
                TimeoutException => new ProjectHostFailed("the project host did not start within a minute", error),
                JsonException => new ProjectHostFailed("the project host's first line does not read", error),
                _ => error,
            };
        }
    }

    /// <summary>The SDK the host's first line says it loaded; a <see cref="ProjectHostFailed"/> when it says why it loaded none.</summary>
    private static HostReady Ready(ReadOnlySpan<byte> first)
    {
        Reply reply = Reply.Read(first);
        return reply.Error is { } error ? throw new ProjectHostFailed(error)
            : reply.Value.IsEmpty || reply.Partial || JsonSerializer.Deserialize<HostReady>(reply.Value, HostJson.Options) is not { } ready
                ? throw new ProjectHostFailed("the project host did not say which SDK it loaded")
            : ready;
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
    public async Task<EvaluateDone> EvaluateAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, Action<EvaluatedProject> evaluated, CancellationToken cancel = default)
    {
        await GiveProgramsAsync(projects, cancel).ConfigureAwait(false);
        return await AskAsync<EvaluateDone>(HostMethods.Evaluate, new EvaluateParams(projects, properties),
            partial => evaluated(JsonSerializer.Deserialize<EvaluatedProject>(partial, HostJson.Options)!), cancel).ConfigureAwait(false)
            ?? throw new ProjectHostFailed("the project host gave no answer for its evaluation");
    }

    /// <summary>The heads each program's project was last given to the host from, and the programs it references, by its
    /// C# file.</summary>
    private readonly Dictionary<string, (IReadOnlyList<Stamp> Heads, IReadOnlyList<string> Referenced)> programs = new(PathNames.Comparer);

    /// <summary>Gives the host the project the SDK makes of each file-based program among <paramref name="projects"/>
    /// (<see cref="ProgramProjects"/>), and of each program they reference, before it is asked of them, a few read at once:
    /// again only once the directives it was made from changed.</summary>
    private async Task GiveProgramsAsync(IEnumerable<string> projects, CancellationToken cancel)
    {
        List<string> next = [.. projects.Where(ProgramFiles.IsProgram).Distinct(PathNames.Comparer)];
        HashSet<string> seen = new(next, PathNames.Comparer);
        while (next.Count > 0)
        {
            List<string> referenced = [];
            foreach (string[] few in next.Chunk(4))
            {
                foreach (IReadOnlyList<string> named in await Task.WhenAll(few.Select(path => GiveProgramAsync(path, cancel))).ConfigureAwait(false))
                {
                    referenced.AddRange(named.Where(seen.Add));
                }
            }
            next = referenced;
        }
    }

    /// <summary>One program's project given to the host unless the one given still stands; the programs it references.</summary>
    private async Task<IReadOnlyList<string>> GiveProgramAsync(string path, CancellationToken cancel)
    {
        lock (programs)
        {
            if (programs.TryGetValue(path, out (IReadOnlyList<Stamp> Heads, IReadOnlyList<string> Referenced) given) && given.Heads.All(Stamps.Holds))
            {
                return given.Referenced;
            }
        }
        (ProgramParams program, IReadOnlyList<Stamp> heads, IReadOnlyList<string> referenced) = await ProgramProjects.ReadAsync(path, cancel).ConfigureAwait(false);
        await AskAsync<bool?>(HostMethods.Program, program, null, cancel).ConfigureAwait(false);
        lock (programs)
        {
            programs[path] = (heads, referenced);
        }
        return referenced;
    }

    /// <summary>Builds projects at design time, handing each over as its builds end, on the reader's thread; done once
    /// every one has been.</summary>
    public async Task<BuildDone> BuildAsync(IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> properties, Action<BuiltProject> built, CancellationToken cancel = default)
    {
        Interlocked.Increment(ref building);
        try
        {
            await GiveProgramsAsync(projects, cancel).ConfigureAwait(false);
            return await AskAsync<BuildDone>(HostMethods.Build, new BuildParams(projects, properties),
                partial => built(BuiltProject.Read(partial)), cancel).ConfigureAwait(false)
                ?? throw new ProjectHostFailed("the project host gave no answer for its builds");
        }
        finally
        {
            Interlocked.Decrement(ref building);
        }
    }

    /// <summary>The rounds of design-time builds asked and not yet done: the host builds one round at a time.</summary>
    public int Building => Volatile.Read(ref building);

    /// <summary>Evaluates one project for the code help in its MSBuild files, the open files' unsaved texts read in
    /// place of the disk's.</summary>
    public async Task<ProjectInsight> InspectAsync(string project, IReadOnlyDictionary<string, string> properties, IReadOnlyDictionary<string, string> texts,
        CancellationToken cancel = default) =>
        await AskAsync<ProjectInsight>(HostMethods.Inspect, new InspectParams(project, properties, texts), null, cancel).ConfigureAwait(false)
            ?? throw new ProjectHostFailed("the project host gave no answer for the project's evaluation");

    /// <summary>Some properties of one project as a build with these global properties sees them (a publish profile's,
    /// imported by PublishProfile), each that has a value; or why the project did not evaluate.</summary>
    public async Task<PropertiesAnswer> PropertiesAsync(string project, IReadOnlyDictionary<string, string> properties, IReadOnlyList<string> names,
        CancellationToken cancel = default)
    {
        await GiveProgramsAsync([project], cancel).ConfigureAwait(false);
        return await AskAsync<PropertiesAnswer>(HostMethods.Properties, new PropertiesParams(project, properties, names), null, cancel).ConfigureAwait(false)
            ?? throw new ProjectHostFailed("the project host gave no answer for the project's properties");
    }

    /// <summary>What a build of one project with these global properties reads and makes, as its evaluations say, or why a
    /// build of it reads more than that.</summary>
    public async Task<BuildInputs> InputsAsync(string project, IReadOnlyDictionary<string, string> properties, CancellationToken cancel = default)
    {
        await GiveProgramsAsync([project], cancel).ConfigureAwait(false);
        return await AskAsync<BuildInputs>(HostMethods.Inputs, new InputsParams(project, properties), null, cancel).ConfigureAwait(false)
            ?? throw new ProjectHostFailed("the project host gave no answer for what the project's build reads");
    }

    /// <summary>Holds the host's design-time builds of WPF projects off for a build of the IDE's own: done once none runs,
    /// those still running named to <paramref name="waiting"/> first (on the reader's thread), and none starts again until
    /// <paramref name="release"/> is cancelled.</summary>
    public async Task HoldAsync(Action<IReadOnlyList<string>> waiting, CancellationToken release)
    {
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool?> asked = AskAsync<bool?>(HostMethods.Hold, new HoldParams(), partial =>
        {
            HoldState state = JsonSerializer.Deserialize<HoldState>(partial, HostJson.Options)!;
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

    /// <summary>A request's answer, read in place from its line as it comes: null when it has none. Its partial answers are
    /// handed to <paramref name="partial"/> as they come, as their bytes, on the reader's thread.</summary>
    private async Task<T?> AskAsync<T>(string method, object parameters, Action<ReadOnlySpan<byte>>? partial, CancellationToken cancel)
    {
        (int id, Task<object?> answered) = pending.Add(new Asked(partial, static result => result.IsEmpty ? null : JsonSerializer.Deserialize<T>(result, HostJson.Options)));
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
            return (T?)await answered;
        }
    }

    /// <summary>A request waiting for its answer: what takes its partial answers, and what reads its result (empty for none)
    /// while its line is still in the buffer.</summary>
    private readonly record struct Asked(Action<ReadOnlySpan<byte>>? Partial, Func<ReadOnlySpan<byte>, object?> Result);

    /// <summary>A reply's line read in place (<see cref="HostReply"/>): its request's id, and its value's bytes, partial or
    /// the result, or its error.</summary>
    private readonly ref struct Reply(int id, bool partial, ReadOnlySpan<byte> value, string? error)
    {
        public int Id { get; } = id;

        public bool Partial { get; } = partial;

        public ReadOnlySpan<byte> Value { get; } = value;

        public string? Error { get; } = error;

        /// <summary>A line's reply; a <see cref="JsonException"/> when it is not one object.</summary>
        public static Reply Read(ReadOnlySpan<byte> line)
        {
            Utf8JsonReader reader = new(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("a reply that is not an object");
            }
            int id = 0;
            bool partial = false;
            ReadOnlySpan<byte> value = default;
            string? error = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isId = reader.ValueTextEquals(HostReply.Id);
                bool isPartial = reader.ValueTextEquals(HostReply.Partial);
                bool isValue = isPartial || reader.ValueTextEquals(HostReply.Result);
                bool isError = reader.ValueTextEquals(HostReply.Error);
                reader.Read();
                if (isId)
                {
                    id = reader.GetInt32();
                }
                else if (isValue && reader.TokenType != JsonTokenType.Null)
                {
                    int start = (int)reader.TokenStartIndex;
                    reader.Skip();
                    value = line[start..(int)reader.BytesConsumed];
                    partial = isPartial;
                }
                else if (isError)
                {
                    error = reader.GetString();
                }
                else
                {
                    reader.Skip();
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                throw new JsonException("a reply with more than its one object");
            }
            return new Reply(id, partial, value, error);
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
        string? why = null;
        try
        {
            while (await lines.NextAsync().ConfigureAwait(false) is { } line)
            {
                if (!line.IsEmpty)
                {
                    Receive(line.Span);
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
            PassengerLog.Write($"Project host (process {ProcessId}) was ended: {why}: {error}");
            ChildProcesses.Kill(process);
        }
        finally
        {
            lines.Dispose();
        }
        if (why is not null)
        {
            Fail(why, ended: false);
        }
        else
        {
            Fail(gone ? "the project host has ended" : await EndedAsync().ConfigureAwait(false));
        }
    }

    /// <summary>One reply, read in place: a partial answer handed to its request's reader, or its result read into what the
    /// request waits for, a result that does not read failing that request alone.</summary>
    private void Receive(ReadOnlySpan<byte> line)
    {
        Reply reply = Reply.Read(line);
        lock (spoiling)
        {
            if (spoiled.Contains(reply.Id))
            {
                throw new JsonException("the line was spoiled, as a test asked");
            }
        }
        if (reply.Partial)
        {
            if (pending.TryGet(reply.Id, out Asked taken))
            {
                taken.Partial?.Invoke(reply.Value);
            }
        }
        else if (reply.Error is { } error)
        {
            pending.Fail(reply.Id, _ => new ProjectHostFailed(error));
        }
        else if (pending.TryGet(reply.Id, out Asked asked))
        {
            Exception? unread = null;
            object? answer = null;
            try
            {
                answer = asked.Result(reply.Value);
            }
            catch (Exception failed)
            {
                unread = failed;
            }
            if (unread is null)
            {
                pending.Answer(reply.Id, answer);
            }
            else
            {
                pending.Fail(reply.Id, _ => unread);
            }
        }
    }

    /// <summary>How the host's process ended by itself: its exit code, and the first line of what it wrote as it crashed.</summary>
    private async Task<string> EndedAsync()
    {
        string how;
        try
        {
            // Its error stream read to its end too, which holds what it wrote as it crashed.
            using CancellationTokenSource limit = new(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            how = $"exit code {process.ExitCode}";
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
        {
            how = "its output closed while it ran";
        }
        return crashed is { } said ? $"the project host ended ({how}): {said}" : $"the project host ended ({how})";
    }

    /// <summary>Ends the host now, for a build in it that can never end: every request waiting fails as at the host's own
    /// end (<see cref="ProjectHostFailed.HostEnded"/>), so a host started anew may answer it.</summary>
    public void End(string why)
    {
        PassengerLog.Write($"Project host (process {ProcessId}) was ended: {why}");
        Fail($"the project host was ended: {why}");
        ChildProcesses.Kill(process);
    }

    /// <summary>Fails every request waiting, and each asked from now on.</summary>
    private void Fail(string why, bool ended = true)
    {
        gone = true;
        pending.End(_ => new ProjectHostFailed(why, hostEnded: ended));
    }

    /// <summary>Closes the host's input, which ends it, and ends it outright if it has not gone a moment later.</summary>
    public async ValueTask DisposeAsync()
    {
        Fail("the project host has ended", ended: false);
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

    /// <summary>The host for a folder's SDK: one running already, else one started for it, which a folder deleted since
    /// is refused (<see cref="ProjectHostFailed"/>). Found on the thread pool, whoever asks: what the gate is held over
    /// (the SDK asked, a host started) is every window's.</summary>
    public static Task<ProjectHost> ForAsync(string folder, CancellationToken cancel = default) =>
        // Not held, each caller holding its own wait: on a caller's thread, one busy or never running again would keep the gate.
        BackgroundWork.Unheld(() => FindAsync(folder, cancel), cancel);

    private static async Task<ProjectHost> FindAsync(string folder, CancellationToken cancel)
    {
        await Gate.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            Running.RemoveAll(h => h.HasExited);
            if (Running.FirstOrDefault(h => h.StartedFor(folder)) is { } own)
            {
                return own;
            }
            if (!Directory.Exists(folder))
            {
                throw new ProjectHostFailed($"{folder} has been deleted");
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
