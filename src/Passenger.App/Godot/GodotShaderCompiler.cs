using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.App.Godot;

/// <summary>A uniform as Godot's compiler read it: its name, its Variant type, its property hint and the hint's words.</summary>
internal sealed record GodotShaderUniform(string Name, GodotType Type, long Hint, string HintString)
{
    /// <summary>What Godot makes of it, in words: <c>float, in 0.0 to 10.0, step 0.001</c>.</summary>
    public string Described
    {
        get
        {
            string type = GodotVariant.TypeName(Type);
            string[] parts = HintString.Split(',');
            return Hint switch
            {
                GodotHints.Range when parts.Length >= 2 => $"{type}, in {parts[0]} to {parts[1]}{(parts.Length > 2 ? $", step {Step(parts[2])}" : string.Empty)}",
                GodotHints.Enum => $"{type}, one of {string.Join(", ", parts)}",
                GodotHints.ResourceType => $"{HintString} ({type})",
                GodotHints.ColorNoAlpha => $"{type}, without alpha",
                _ => type,
            };
        }
    }

    /// <summary>A step as Godot writes it from a 32-bit float (<c>0.0010000000475</c>), to the digits that tell it.</summary>
    private static string Step(string written) =>
        double.TryParse(written, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double step)
            ? ((float)step).ToString("G7", System.Globalization.CultureInfo.InvariantCulture) : written;
}

/// <summary>What Godot's compiler said of a shader: the messages its logger heard, and the uniforms it read (none where it
/// stopped at an error).</summary>
internal sealed record GodotShaderCompiled(IReadOnlyList<GodotShaderMessage> Messages, IReadOnlyList<GodotShaderUniform> Uniforms);

/// <summary>Godot's own shader compiler, for a window's session: Passenger's helper (<c>PassengerShaders.gd</c>) run by a
/// headless Godot, the one Settings names or found, which compiles each shader handed to it through its dummy renderer and
/// answers over loopback with what Godot's logger heard (Godot 4.5's <c>OS.add_logger</c>) and the uniforms read. It runs
/// on a project of Passenger's own in the state's cache, given the shader's includes as text, so no project of the
/// person's is touched, with the window's user folders for Godot, else its own beside it; one helper for each rendering
/// method a project uses (<see cref="GodotDefinitions.RenderingMethod"/>), its project set to it, as Godot's compiler
/// refuses what the method lacks; started as first needed through ChildProcesses, started again after it ends (a few
/// times at most), let go once no shader has been open or asked of for <see cref="Idle"/>, and ended with the session.
/// The built-ins a Godot takes are asked of it once per version and method (<see cref="TableAsync"/>).</summary>
public sealed class GodotShaderCompiler(Func<string?> setting, Func<IReadOnlyDictionary<string, string>?> folders) : IAsyncDisposable
{
    /// <summary>The variable the helper reads the token it says back from, so no other program on the machine is taken for it.</summary>
    [IdentifierHome]
    public const string TokenVariable = "PASSENGER_SHADER_TOKEN";

    /// <summary>The variable that shortens how long the helpers wait, no shader open nor asked of, before they are let go.</summary>
    [IdentifierHome]
    public const string IdleVariable = "PASSENGER_SHADER_IDLE_MS";

    /// <summary>The rendering method a project names none of compiles under.</summary>
    public const string DefaultMethod = GodotDefinitions.ForwardPlus;

    /// <summary>How long the helpers wait, no shader open nor asked of, before they are let go: about 160 MB each.</summary>
    private static TimeSpan Idle => EnvironmentValues.Milliseconds(IdleVariable, TimeSpan.FromMinutes(3));

    /// <summary>The first Godot whose scripts can hear its logger.</summary>
    private static readonly Version Logged = new(4, 5);

    /// <summary>How long the helper may take to start and say its token.</summary>
    private static readonly TimeSpan Starting = TimeSpan.FromSeconds(30);

    /// <summary>How long a compile, or the probe of a version's built-ins, may take to be answered.</summary>
    private static readonly TimeSpan Answering = TimeSpan.FromSeconds(20);

    /// <summary>How many times the helper is started in a session before its failure is said.</summary>
    private const int MostStarts = 3;

    private static readonly Lazy<byte[]> Script = new(() =>
    {
        using Stream stream = typeof(GodotShaderCompiler).Assembly.GetManifestResourceStream("Godot/PassengerShaders.gd")
            ?? throw new InvalidOperationException("Passenger carries no Godot/PassengerShaders.gd");
        using MemoryStream read = new();
        stream.CopyTo(read);
        return read.ToArray();
    });

    /// <summary>The project a helper runs on: its name, its rendering method, and no log file written for it.</summary>
    private static byte[] ProjectOf(string method) => Encoding.UTF8.GetBytes("config_version=5\n\n[application]\n\nconfig/name=\"Passenger Shaders\"\n\n"
        + "[debug]\n\nfile_logging/enable_file_logging=false\nfile_logging/enable_file_logging.pc=false\n\n"
        + $"[rendering]\n\nrenderer/rendering_method=\"{method}\"\n");

    private readonly SemaphoreSlim turn = new(1, 1);
    private readonly CancellationTokenSource ending = new();
    private readonly Dictionary<string, Task<GodotShaderTable>> tables = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Helper> helpers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> starts = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<GodotShaderUniform>> read = new(StringComparer.Ordinal);
    private volatile bool disposed;
    private int open;
    private CancellationTokenSource? idle;

    /// <summary>Why Godot compiles no shader now, in words for the person; null while it does, or before it was first asked.</summary>
    public string? Problem { get; private set; }

    /// <summary>Where the helpers' projects live: the state's cache, a folder for each rendering method.</summary>
    private static string Folder => Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "shaders");

    /// <summary>A shader file opened, which keeps the helpers on while it is.</summary>
    public void Opened() => Interlocked.Increment(ref open);

    /// <summary>A shader file closed: once none is open, the helpers go after <see cref="Idle"/> unasked.</summary>
    public void Closed()
    {
        if (Interlocked.Decrement(ref open) <= 0)
        {
            LetGoLater();
        }
    }

    /// <summary>The helpers let go once <see cref="Idle"/> passes with no shader open and nothing asked of them.</summary>
    private void LetGoLater()
    {
        CancellationTokenSource waiting = new();
        Interlocked.Exchange(ref idle, waiting)?.Cancel();
        // Unheld: no screenshot or settling test waits on helpers kept for a shader yet to come.
        IdeErrors.Observe(BackgroundWork.Unheld(async () =>
        {
            try
            {
                await Task.Delay(Idle, waiting.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (Volatile.Read(ref open) > 0 || disposed)
            {
                return;
            }
            await turn.WaitAsync();
            try
            {
                if (Volatile.Read(ref idle) != waiting || helpers.IsEmpty)
                {
                    return;
                }
                EndHelpers();
                starts.Clear();
                IdeErrors.Write($"Godot's shader compiler let go: no shader open nor asked of for {Idle.TotalSeconds:0} s");
            }
            finally
            {
                turn.Release();
            }
        }));
    }

    /// <summary>Ends every helper, a request under way failing at once.</summary>
    private void EndHelpers()
    {
        foreach (string method in helpers.Keys)
        {
            if (helpers.TryRemove(method, out Helper? gone))
            {
                gone.Dispose();
            }
        }
    }

    /// <summary>What Godot's compiler says of a shader; null where it cannot be asked (<see cref="Problem"/> says why).</summary>
    internal async Task<GodotShaderCompiled?> CompileAsync(GodotShaderUnit unit, string method = DefaultMethod, CancellationToken cancel = default)
    {
        using JsonDocument? answer = await AskAsync(method, writer =>
        {
            writer.WriteStartObject("compile");
            writer.WriteString("path", unit.Res);
            writer.WriteString("code", unit.Code);
            writer.WriteStartObject("includes");
            foreach ((string res, string text) in unit.Includes)
            {
                writer.WriteString(res, text);
            }
            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancel);
        if (answer is null)
        {
            return null;
        }
        JsonElement root = answer.RootElement;
        GodotShaderCompiled compiled = new(GodotShaderUnit.MessagesOf(JsonMembers.Items(root, "messages")),
            [.. JsonMembers.Items(root, "uniforms").Select(u => new GodotShaderUniform(JsonMembers.Text(u, "name") ?? string.Empty,
                (GodotType)(JsonMembers.Number(u, "type") ?? 0), JsonMembers.Number(u, "hint") ?? 0, JsonMembers.Text(u, "hint_string") ?? string.Empty))]);
        // A compile stopped at an error reads no uniform: what the last one read stands until one reads them again.
        if (compiled.Uniforms.Count > 0 || !compiled.Messages.Any(m => m.Error))
        {
            read[unit.Res] = compiled.Uniforms;
        }
        return compiled;
    }

    /// <summary>What the last compile of a shader, by its res:// path (a scene's own by its file and id), read of a uniform;
    /// null where it read none of that name.</summary>
    internal GodotShaderUniform? UniformOf(string res, string name) =>
        read.TryGetValue(res, out IReadOnlyList<GodotShaderUniform>? uniforms) ? uniforms.FirstOrDefault(u => u.Name == name) : null;

    /// <summary>The carried table of the shading language as the Godot found takes it: each candidate probed by its compiler
    /// once per Godot version and kept in the state's cache; the table as carried where no Godot can be asked.</summary>
    public async Task<GodotShaderTable> TableAsync(string method = DefaultMethod, CancellationToken cancel = default)
    {
        GodotShaderTable carried = GodotShaderTable.Carried;
        if (await AskAsync(method, null, cancel) is null || helpers.GetValueOrDefault(method) is not { } running)
        {
            return carried;
        }
        string key = $"{running.Version}-{running.Build}-{method}-{carried.Fingerprint}";
        if (!tables.TryGetValue(key, out Task<GodotShaderTable>? table))
        {
            tables[key] = table = ProbedAsync(carried, key, method);
        }
        return await table.WaitAsync(cancel);
    }

    /// <summary>The table without what the Godot refused: read from the cache, else probed and written there.</summary>
    private async Task<GodotShaderTable> ProbedAsync(GodotShaderTable carried, string key, string method)
    {
        string kept = Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "shader-built-ins", $"{string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '_'))}.json");
        if (await BackgroundWork.Run(() => ReadRefused(kept)) is { } known)
        {
            return carried.Without(known);
        }
        IReadOnlyList<GodotShaderProbe> probes = carried.Probes();
        using JsonDocument? answer = await AskAsync(method, writer =>
        {
            writer.WriteStartArray("probe");
            foreach (GodotShaderProbe probe in probes)
            {
                writer.WriteStringValue(probe.Code);
            }
            writer.WriteEndArray();
        }, CancellationToken.None);
        if (answer is null)
        {
            return carried;
        }
        HashSet<string> refused = [.. JsonMembers.Items(answer.RootElement, "refused").Select(i => i.TryGetInt32(out int index) && index >= 0 && index < probes.Count ? probes[index].Key : null)
            .OfType<string>()];
        await BackgroundWork.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(kept)!);
            AtomicFile.Write(kept, JsonSerializer.SerializeToUtf8Bytes(refused.Order(StringComparer.Ordinal).ToArray()));
        });
        return carried.Without(refused);
    }

    /// <summary>What a Godot refused, as kept; null where nothing is kept, or it cannot be read.</summary>
    private static HashSet<string>? ReadRefused(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }
            using JsonDocument read = JsonDocument.Parse(File.ReadAllBytes(path));
            return read.RootElement.ValueKind == JsonValueKind.Array ? [.. read.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null).OfType<string>()] : null;
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>A request answered by the rendering method's helper, started first where none runs; with no body, only
    /// that it runs (an empty answer). Null where none can run or it did not answer in time, which ends it.</summary>
    private async Task<JsonDocument?> AskAsync(string method, Action<Utf8JsonWriter>? body, CancellationToken cancel)
    {
        Interlocked.Exchange(ref idle, null)?.Cancel();
        await turn.WaitAsync(cancel);
        try
        {
            if (disposed)
            {
                return null;
            }
            if (helpers.GetValueOrDefault(method) is not { Running: true })
            {
                if (helpers.TryRemove(method, out Helper? ended))
                {
                    ended.Dispose();
                }
                if (starts.GetValueOrDefault(method) >= MostStarts)
                {
                    return null;
                }
                starts[method] = starts.GetValueOrDefault(method) + 1;
                if (await StartAsync(method) is { } started)
                {
                    helpers[method] = started;
                }
                if (disposed)
                {
                    EndHelpers();
                }
            }
            if (helpers.GetValueOrDefault(method) is not { } running)
            {
                return null;
            }
            if (body is null)
            {
                return JsonDocument.Parse("{}");
            }
            JsonDocument? answer = await running.AskAsync(body, Waits.Of(Answering), cancel);
            if (answer is null && !cancel.IsCancellationRequested && !disposed)
            {
                IdeErrors.Write($"Godot's shader compiler did not answer within {Waits.Of(Answering).TotalSeconds:0} s, so it is ended");
                running.Dispose();
                helpers.TryRemove(method, out _);
            }
            return answer;
        }
        finally
        {
            if (Volatile.Read(ref open) <= 0)
            {
                LetGoLater();
            }
            turn.Release();
        }
    }

    /// <summary>The helper started on the Godot found and connected, its token said back; null where it cannot be, with
    /// <see cref="Problem"/> saying why.</summary>
    private async Task<Helper?> StartAsync(string method)
    {
        string? chosen = setting();
        GodotFound? found = await BackgroundWork.Run(() => GodotLaunch.Found(chosen));
        if (found is not { Exists: true })
        {
            return Unavailable("no Godot was found to compile it: install Godot 4.5 or newer, then choose its executable in "
                + $"{SettingsDialog.PlaceOfPage(SettingsDialog.GodotPage)}, set {GodotExecutables.Variable} to it, or put it on the PATH");
        }
        // Its files' version where they say it: asking Godot itself starts it once more, seconds on a machine's first run.
        string? version = found.Version ?? await GodotExecutables.VersionAsync(found);
        if (version is null || !System.Version.TryParse(string.Join('.', version.Split('.').Take(2)), out Version? number) || number < Logged)
        {
            return Unavailable($"{PathNames.Shown(found.Executable, null)} is Godot {version ?? "of no version it says"}, whose scripts hear no logger: "
                + $"Godot {Logged} added the logger Passenger hears its shader compiler through, so a Godot {Logged} or newer marks shaders");
        }
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Process? process = null;
        try
        {
            string folder = Path.Combine(Folder, method);
            await BackgroundWork.Run(() =>
            {
                Directory.CreateDirectory(folder);
                Kept(Path.Combine(folder, "project.godot"), ProjectOf(method));
                Kept(Path.Combine(folder, "PassengerShaders.gd"), Script.Value);
            });
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ProcessStartInfo start = ChildProcesses.Command(found.Executable, [.. GodotLaunch.Chosen(chosen)?.Arguments ?? [], GodotExecutables.Headless,
                "--path", folder, "--script", "res://PassengerShaders.gd", "--", port.ToString(System.Globalization.CultureInfo.InvariantCulture)], folder);
            foreach ((string name, string value) in folders() ?? GodotUserFolders.Under(Path.Combine(State.StateStore.DefaultRoot, "cache", "godot", "shaders-home")))
            {
                start.Environment[name] = value;
            }
            start.Environment[TokenVariable] = token;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            process = await ChildProcesses.StartAsync(start);
            // What it prints is Godot's own report of each shader's error, which the logger hears too.
            IdeErrors.Observe(ChildProcesses.Hear(process, _ => { }, _ => { }));
            Task<TcpClient> accepted = listener.AcceptTcpClientAsync(ending.Token).AsTask();
            Task ended = process.WaitForExitAsync(ending.Token);
            Task first = await Task.WhenAny(accepted, ended, Task.Delay(Waits.Of(Starting), ending.Token));
            if (first != accepted)
            {
                // The accept ends faulted as the listener stops, which nothing is to hear.
                UnawaitedWork.Dismiss(accepted);
                string why = ending.IsCancellationRequested ? "its session ended"
                    : first == ended ? $"it ended as it started ({ExitCodes.Said(process.ExitCode)})" : $"it did not answer within {Waits.Of(Starting).TotalSeconds:0} s";
                ChildProcesses.Kill(process);
                process.Dispose();
                return ending.IsCancellationRequested ? null : Unavailable($"Godot's shader compiler did not start: {why}");
            }
            Helper started = new(process, await accepted);
            if (!await started.GreetAsync(token, Waits.Of(Starting)) || ending.IsCancellationRequested)
            {
                started.Dispose();
                return ending.IsCancellationRequested ? null : Unavailable("Godot's shader compiler did not start: it never said the token it was given");
            }
            IdeErrors.Write($"Godot's shader compiler runs on Godot {started.Version} ({found.Executable}), rendering method {method}");
            Problem = null;
            return started;
        }
        catch (Exception unstarted) when (unstarted is System.ComponentModel.Win32Exception or IOException or SocketException or UnauthorizedAccessException)
        {
            if (process is not null)
            {
                ChildProcesses.Kill(process);
            }
            return Unavailable($"Godot's shader compiler did not start: {unstarted.Message}");
        }
        finally
        {
            listener.Stop();
        }
    }

    private Helper? Unavailable(string problem)
    {
        if (Problem != problem)
        {
            IdeErrors.Write($"Godot's shaders are not compiled: {problem}");
        }
        Problem = problem;
        return null;
    }

    /// <summary>A file of the helper's project written where it is not there as carried.</summary>
    private static void Kept(string path, byte[] bytes)
    {
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            AtomicFile.Write(path, bytes);
        }
    }

    /// <summary>Ends the helpers now, a start under way too, and refuses every request from then on; needing no UI thread,
    /// so the process can wait for it once the window's loop has stopped.</summary>
    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await ending.CancelAsync().ConfigureAwait(false);
        Volatile.Read(ref idle)?.Cancel();
        EndHelpers();
    }

    /// <summary>A helper running: its process and its connection, each answer read off it by the request's id.</summary>
    private sealed class Helper : IDisposable
    {
        private readonly Process process;
        private readonly TcpClient client;
        private readonly NetworkStream stream;
        private readonly StreamReader reader;
        private readonly Dictionary<long, TaskCompletionSource<JsonDocument?>> asked = [];
        private readonly object gate = new();
        private long next;
        private bool ended;
        private Task? reading;

        public Helper(Process process, TcpClient client)
        {
            this.process = process;
            this.client = client;
            stream = client.GetStream();
            reader = new StreamReader(stream, new UTF8Encoding(false));
        }

        public string Version { get; private set; } = string.Empty;

        public string Build { get; private set; } = string.Empty;

        public bool Running
        {
            get
            {
                lock (gate)
                {
                    return !ended && DotnetProcesses.Runs(process.Id);
                }
            }
        }

        /// <summary>Its first line read: whether it says the token, its version and build kept; then its answers read.</summary>
        public async Task<bool> GreetAsync(string token, TimeSpan limit)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync().WaitAsync(limit);
            }
            catch (Exception unread) when (unread is IOException or TimeoutException or ObjectDisposedException)
            {
                return false;
            }
            if (line is null)
            {
                return false;
            }
            try
            {
                using JsonDocument hello = JsonDocument.Parse(line);
                if (JsonMembers.Text(hello.RootElement, "token") != token)
                {
                    return false;
                }
                Version = JsonMembers.Text(hello.RootElement, "version") ?? string.Empty;
                Build = JsonMembers.Text(hello.RootElement, "build") ?? string.Empty;
            }
            catch (JsonException)
            {
                return false;
            }
            // Unheld: it reads for as long as the helper runs, which no wait for the IDE to be quiet may wait out.
            reading = BackgroundWork.Unheld(ReadAsync);
            return true;
        }

        /// <summary>A request's answer, its id written in; null where the helper ended or did not answer within the limit.</summary>
        public async Task<JsonDocument?> AskAsync(Action<Utf8JsonWriter> body, TimeSpan limit, CancellationToken cancel)
        {
            TaskCompletionSource<JsonDocument?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            long id;
            lock (gate)
            {
                if (ended)
                {
                    return null;
                }
                id = ++next;
                asked[id] = answer;
            }
            using MemoryStream line = new();
            using (Utf8JsonWriter writer = new(line))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", id);
                body(writer);
                writer.WriteEndObject();
            }
            line.WriteByte((byte)'\n');
            try
            {
                await stream.WriteAsync(line.GetBuffer().AsMemory(0, (int)line.Length), cancel);
                return await answer.Task.WaitAsync(limit, cancel);
            }
            catch (Exception unanswered) when (unanswered is IOException or TimeoutException or ObjectDisposedException or OperationCanceledException)
            {
                return null;
            }
            finally
            {
                lock (gate)
                {
                    asked.Remove(id);
                }
            }
        }

        /// <summary>Each answer, a JSON line, given to its request until the connection ends, which ends every request left.</summary>
        private async Task ReadAsync()
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                {
                    JsonDocument? answer;
                    try
                    {
                        answer = JsonDocument.Parse(line);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                    long id = JsonMembers.Number(answer.RootElement, "id") ?? 0;
                    TaskCompletionSource<JsonDocument?>? waiting;
                    lock (gate)
                    {
                        asked.Remove(id, out waiting);
                    }
                    if (waiting is null || !waiting.TrySetResult(answer))
                    {
                        answer.Dispose();
                    }
                }
            }
            catch (Exception unread) when (unread is IOException or ObjectDisposedException or SocketException)
            {
                // The connection ended under the read: every request left ends unanswered.
            }
            finally
            {
                End();
            }
        }

        /// <summary>Every request left ends unanswered, and none is taken from now on.</summary>
        private void End()
        {
            lock (gate)
            {
                ended = true;
                foreach (TaskCompletionSource<JsonDocument?> waiting in asked.Values)
                {
                    waiting.TrySetResult(null);
                }
                asked.Clear();
            }
        }

        public void Dispose()
        {
            End();
            ChildProcesses.Kill(process);
            client.Dispose();
            if (reading is { } read)
            {
                UnawaitedWork.Dismiss(read);
            }
            process.Dispose();
        }
    }
}
