using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.App.Web;

/// <summary>What Prettier made of a file: its text formatted with where the caret goes in it, or that its .prettierignore
/// names it, that Prettier has no parser for it, or why it could not (a syntax error, no package).</summary>
public sealed record PrettierAnswer(string? Formatted = null, int Cursor = 0, bool Ignored = false, bool Unsupported = false, string? Error = null, string? Version = null);

/// <summary>Prettier for the folder open: the project's own prettier package (found from each file's folder up, or the
/// one Settings names) with its .prettierrc, .editorconfig and nearest .prettierignore. With no language server,
/// Passenger's runner (<c>web/passenger/prettier.cjs</c>) runs its API on the web servers' Node.js through
/// ChildProcesses, one process kept warm for the folder, since the first format waits for Node.js to start and Prettier
/// to load. Formats are asked one at a time, from any thread.</summary>
public sealed class PrettierService : IAsyncDisposable
{
    /// <summary>Where Settings configures Prettier, for what is said when it cannot run.</summary>
    public static readonly string Place = SettingsDialog.PlaceOfPage(SettingsDialog.PrettierPage);

    /// <summary>How long a format may take before the runner is taken to be stuck and ended.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    private readonly string folder;
    private readonly Func<AppSettings> settings;
    private readonly Func<Task<NodeRuntime>> node;
    private readonly SemaphoreSlim turn = new(1, 1);
    private Runner? runner;
    private int asked;
    private bool disposed;

    public PrettierService(string folder, Func<AppSettings> settings, Func<Task<NodeRuntime>> node)
    {
        this.folder = folder;
        this.settings = settings;
        this.node = node;
    }

    /// <summary>The runner carried beside the app.</summary>
    public static string Script => Path.Combine(WebLanguages.Carried, "passenger", "prettier.cjs");

    /// <summary>The Prettier package that formats a file as Settings configure it, null for none (Disabled, or no package
    /// found). The glob is not asked: Reformat with Prettier formats any file Prettier reads.</summary>
    public string? PackageFor(string path)
    {
        AppSettings now = settings();
        return now.Prettier switch
        {
            ToolConfiguration.Disabled => null,
            ToolConfiguration.Manual => ProjectPackages.Named(now.PrettierPackage, folder),
            _ => ProjectPackages.Installed(path, "prettier"),
        };
    }

    /// <summary>Whether Settings' "Run for files" covers a file, while Prettier is not disabled.</summary>
    public bool Covers(string path)
    {
        AppSettings now = settings();
        return now.Prettier != ToolConfiguration.Disabled && ProjectPackages.Covers(now.PrettierFiles, folder, path);
    }

    /// <summary>The package.json that lists prettier for a file the glob covers but it is not installed for, under
    /// Automatic configuration.</summary>
    public string? Missing(string path) =>
        settings().Prettier == ToolConfiguration.Automatic && Covers(path) ? ProjectPackages.Missing(path, "prettier") : null;

    /// <summary>Whether Reformat Code formats the file with Prettier ("Run on 'Reformat Code' action").</summary>
    public bool OnReformat(string path) => settings().PrettierOnReformat && Covers(path) && PackageFor(path) is not null;

    /// <summary>Whether a save formats the file with Prettier first ("Run on save").</summary>
    public bool OnSave(string path) => settings().PrettierOnSave && Covers(path) && PackageFor(path) is not null;

    /// <summary>The nearest .prettierignore from the file's folder up, for when Settings names none.</summary>
    public static string? IgnoreFile(string path)
    {
        for (string? at = Path.GetDirectoryName(path); at is not null; at = Path.GetDirectoryName(at))
        {
            string ignore = Path.Combine(at, ".prettierignore");
            if (File.Exists(ignore))
            {
                return ignore;
            }
        }
        return null;
    }

    /// <summary>A file's text formatted by its Prettier, with the caret's offset carried into it. Run off the UI thread:
    /// it starts the runner the first time.</summary>
    public async Task<PrettierAnswer> FormatAsync(string path, string text, int cursor, CancellationToken cancel = default)
    {
        if (PackageFor(path) is not { } package)
        {
            return new PrettierAnswer(Error: settings().Prettier == ToolConfiguration.Disabled
                ? $"Prettier is disabled in {Place}"
                : $"no prettier package was found for {Path.GetFileName(path)}: install it (npm install), or name one in {Place}");
        }
        NodeRuntime runtime = await node().ConfigureAwait(false);
        if (runtime.Problem is { } none)
        {
            return new PrettierAnswer(Error: none);
        }
        if (!File.Exists(Script))
        {
            return new PrettierAnswer(Error: $"Passenger's Prettier runner is missing from the app's folder ({Script})");
        }
        await turn.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return new PrettierAnswer(Error: "the folder was closed");
            }
            if (runner is { } stale && (stale.HasExited || stale.Node != runtime.Path))
            {
                End(stale);
            }
            // This format's own runner from here on: the folder's close takes the field away while it waits.
            Runner running;
            try
            {
                running = runner ??= await Runner.StartAsync(runtime.Path!, folder).ConfigureAwait(false);
            }
            catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException)
            {
                return new PrettierAnswer(Error: $"Prettier's runner could not start on {runtime.Path}: {error.Message}");
            }
            if (disposed)
            {
                // Its folder closed as it started, which took no runner away: this one goes with it.
                End(running);
                return new PrettierAnswer(Error: "the folder was closed");
            }
            int id = ++asked;
            JsonObject request = new()
            {
                ["id"] = id,
                ["prettier"] = package,
                ["file"] = path,
                ["text"] = text,
                ["cursor"] = cursor,
                ["ignorePath"] = IgnoreFile(path),
            };
            long began = Stopwatch.GetTimestamp();
            string? line;
            try
            {
                line = await running.AskAsync(request.ToJsonString(), Limit, cancel).ConfigureAwait(false);
            }
            catch (Exception error) when (error is TimeoutException or IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                End(running);
                if (error is OperationCanceledException && cancel.IsCancellationRequested)
                {
                    throw;
                }
                return new PrettierAnswer(Error: disposed ? "the folder was closed"
                    : error is TimeoutException ? $"Prettier did not answer in {Limit.TotalSeconds:0} s" : $"Prettier's runner ended: {error.Message}");
            }
            if (line is null)
            {
                End(running);
                return new PrettierAnswer(Error: disposed ? "the folder was closed" : "Prettier's runner ended before it answered; the IDE's log has what it said");
            }
            if (Read(line, id) is not { } answer)
            {
                // After a line that is not this request's answer, every answer would be the request's before: the runner goes.
                End(running);
                string said = line[..Math.Min(line.Length, 200)];
                IdeErrors.Write($"Prettier's runner (process {running.ProcessId}) wrote what is not its answer for {path}, so it was ended: {said}");
                return new PrettierAnswer(Error: $"Prettier's runner wrote what is not its answer for {Path.GetFileName(path)}, so it was ended: {said}");
            }
            if (!running.Timed)
            {
                running.Timed = true;
                IdeErrors.Write($"Prettier {answer.Version} formatted {Path.GetFileName(path)} for {folder} in {Stopwatch.GetElapsedTime(began).TotalMilliseconds:0} ms, its runner started on Node.js {runtime.Version}");
            }
            return answer;
        }
        finally
        {
            turn.Release();
        }
    }

    /// <summary>Ends a runner, which is then no longer the folder's if it still was.</summary>
    private void End(Runner ending)
    {
        ending.Dispose();
        Interlocked.CompareExchange(ref runner, null, ending);
    }

    /// <summary>The runner's answer to request <paramref name="id"/>; null for a line that is not it: another request's, or
    /// no answer at all.</summary>
    private static PrettierAnswer? Read(string line, int id)
    {
        try
        {
            using JsonDocument read = JsonDocument.Parse(line);
            JsonElement answer = read.RootElement;
            if (answer.ValueKind != JsonValueKind.Object || !answer.TryGetProperty("id", out JsonElement asked) || !asked.TryGetInt32(out int answered) || answered != id)
            {
                return null;
            }
            string? Text(string name) => answer.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            bool Flag(string name) => answer.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
            int cursor = answer.TryGetProperty("cursor", out JsonElement at) && at.TryGetInt32(out int offset) ? offset : 0;
            return new PrettierAnswer(Text("formatted"), cursor, Flag("ignored"), Flag("unsupported"), Text("error"), Text("version"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Ends the runner (another Node.js chosen); the next format starts one.</summary>
    public async Task ResetAsync()
    {
        await turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (runner is { } running)
            {
                End(running);
            }
        }
        finally
        {
            turn.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        disposed = true;
        // Ended at once, whatever it is doing: a format waited on fails with the runner, as one cancelled does.
        Interlocked.Exchange(ref runner, null)?.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>One runner process: a request written as a line, its answer read as the next.</summary>
    private sealed class Runner : IDisposable
    {
        private readonly Process process;
        private int ended;

        private Runner(Process process, string node)
        {
            this.process = process;
            Node = node;
            ProcessId = process.Id;
        }

        public string Node { get; }

        public int ProcessId { get; }

        /// <summary>Whether its first format's time has been logged.</summary>
        public bool Timed { get; set; }

        public bool HasExited
        {
            get
            {
                try
                {
                    return process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>A runner started, its fork off the window's thread, where a format pressed there would wait it
        /// out.</summary>
        public static async Task<Runner> StartAsync(string node, string folder)
        {
            ProcessStartInfo start = ChildProcesses.Command(node, [Script], folder);
            start.RedirectStandardInput = true;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process process = await ChildProcesses.StartAsync(start).ConfigureAwait(false);
            int id = process.Id;
            // What a plugin or configuration prints comes here too: the runner keeps its output for the answers.
            IdeErrors.Observe(ChildProcesses.Hear(process, errors: said =>
            {
                if (said.Length > 0)
                {
                    IdeErrors.Write($"Prettier runner (process {id}): {said}");
                }
            }));
            return new Runner(process, node);
        }

        public async Task<string?> AskAsync(string request, TimeSpan limit, CancellationToken cancel)
        {
            using CancellationTokenSource ending = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            ending.CancelAfter(limit);
            try
            {
                await process.StandardInput.WriteLineAsync(request.AsMemory(), ending.Token).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(ending.Token).ConfigureAwait(false);
                return await process.StandardOutput.ReadLineAsync(ending.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new TimeoutException();
            }
        }

        /// <summary>Ends it, once: the format waiting on it and the folder's close may both.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref ended, 1) == 1)
            {
                return;
            }
            ChildProcesses.Kill(process);
            process.Dispose();
        }
    }
}
