using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Passenger.App.Run;
using Passenger.App.ToolWindows;
using Passenger.Core;

namespace Passenger.App.Containers;

/// <summary>A container's log, or a Compose project's or service's with every service's lines merged, or an AppHost's
/// with its containers' merged: <c>docker logs -f</c> (<c>docker compose logs -f</c>) streamed into a console (<see
/// cref="MergedLog"/>) through a <see cref="ProcessRunner"/>, an AppHost's one per container read straight into it.
/// Timestamps are shown on demand, and a project's services and an AppHost's containers each have a toggle; changing
/// either streams the log again.</summary>
public sealed class ContainerLogPane : DockPanel
{
    /// <summary>How many lines of what came before a stream begins with.</summary>
    private const int Tail = 1000;

    private readonly Func<ContainerEngine> engine;
    private readonly ToggleButton timestamps;
    private readonly MergedLog log;
    private ProcessRunner? runner;

    /// <summary>An AppHost's containers' logs as they are merged (<see cref="Merge"/>), a stream each.</summary>
    private readonly List<MergedStream> merged = [];
    private ContainerRow? row;
    private string? streaming;

    /// <summary>What the log shown was read from (<see cref="Signature"/>).</summary>
    private string? streamed;

    public ContainerLogPane(Func<ContainerEngine> engine)
    {
        this.engine = engine;
        timestamps = new ToggleButton { Classes = { StyleClasses.Segment }, Content = "Timestamps", VerticalAlignment = VerticalAlignment.Center, Name = "containerTimestamps" };
        timestamps.IsCheckedChanged += (_, _) => Restart();
        log = new MergedLog("containerLogFind", timestamps);
        log.Console.LinkClicked += (_, link) => LinkClicked?.Invoke(this, link);
        log.Toggled += (_, _) => Restart();
        Children.Add(log);
    }

    /// <summary>Find over the log, as the editor's: shown by its button and by Find's keys while the window has them.</summary>
    public Editors.FindBar Find => log.Find;

    /// <summary>Opens Find over the log.</summary>
    public void OpenFind() => log.OpenFind();

    /// <summary>A link in the log clicked.</summary>
    public event EventHandler<LinkTarget>? LinkClicked;

    /// <summary>The console the log streams into.</summary>
    public OutputView Console => log.Console;

    /// <summary>Whether lines carry the engine's timestamps.</summary>
    public bool Timestamps
    {
        get => timestamps.IsChecked == true;
        set => timestamps.IsChecked = value;
    }

    /// <summary>The services whose lines a project's log shows, each a toggle.</summary>
    public IReadOnlyList<ToggleButton> ServiceToggles => log.Toggles;

    /// <summary>What streams now, as a command's words; null while nothing does.</summary>
    public string? Streaming => streaming;

    /// <summary>Streams a row's log, or nothing; the same row again leaves its stream as it is, and a stopped container's
    /// log as it was read (its scroll and Find kept) until one of its containers runs again or another comes.</summary>
    public void Show(ContainerRow? shown)
    {
        UiThread.Check();
        // An AppHost's log is its containers' as they were when it began: one that comes or goes reads it again.
        bool streams = runner is { IsRunning: true } || merged.Any(each => each.IsRunning);
        if (shown?.Key == row?.Key && (shown is null || streams && (shown.Kind != ContainerRowKind.AppHost || Signature(shown) == streamed)
            || (Signature(shown) == streamed && !shown.Containers.Any(c => c.IsRunning))))
        {
            row = shown;
            return;
        }
        bool otherGroup = shown?.Project?.Name != row?.Project?.Name || shown?.Kind == ContainerRowKind.AppHost && shown.Key != row?.Key;
        row = shown;
        log.Offer(shown switch
        {
            { Project: { } project, Service: null, Kind: ContainerRowKind.Project } => [.. project.Services.Select(s => (s.Service, s.Service))],
            { Kind: ContainerRowKind.AppHost } => [.. shown.Containers.Select(c => (c.Name, c.Name))],
            _ => [],
        }, forget: otherGroup);
        Restart();
    }

    /// <summary>Stops the stream, as the window hides.</summary>
    public void Stop()
    {
        row = null;
        End();
    }

    private void End()
    {
        streaming = null;
        if (runner is not null)
        {
            runner.Stop();
            runner.Dispose();
            runner = null;
        }
        foreach (MergedStream each in merged)
        {
            each.Stop();
        }
        merged.Clear();
    }

    /// <summary>An AppHost's containers' logs merged, as Compose merges a project's: <c>docker logs -f</c> of each
    /// container whose toggle is on, each line under its container's name, read straight into the log (the engine has
    /// no command that follows several containers' logs that Compose did not make).</summary>
    private void Merge(ContainerRow shown, IReadOnlyList<string> options)
    {
        List<string> said = [];
        foreach (ContainerInfo container in shown.Containers.Where(c => log.Shows(c.Name)))
        {
            string[] words = ["logs", .. options, container.Id];
            ProcessStartInfo start;
            try
            {
                start = engine().Command(words);
            }
            catch (FileNotFoundException missing)
            {
                Console.Append($"{missing.Message}\n", error: true);
                return;
            }
            Dictionary<string, string> environment = start.Environment.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value!);
            ProcessStartInfo followed = ChildProcesses.Command(start.FileName, words, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            foreach ((string key, string value) in environment)
            {
                followed.Environment[key] = value;
            }
            try
            {
                merged.Add(MergedStream.Start(followed, $"[{container.Name}] ", Console.Sink));
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
            {
                Console.Append($"{container.Name}: {ProcessRunner.WhyNotStarted(followed, error)}\n", error: true);
                continue;
            }
            said.Add(ContainerEngine.Words(words));
        }
        streaming = said.Count == 0 ? null : string.Join("; ", said);
    }

    /// <summary>One container's log followed into the merged log, its output and errors read straight into the log's
    /// sink (<see cref="ConsoleSink.PumpAsync"/>), each whole line under the container's name; <see cref="Stop"/> ends
    /// it with all it started, and what it says after is dropped.</summary>
    private sealed class MergedStream
    {
        private readonly Process process;
        private readonly CancellationTokenSource letGo = new();

        private MergedStream(Process process) => this.process = process;

        /// <summary>From its start until its program has ended and what it wrote is read; UI thread.</summary>
        public bool IsRunning { get; private set; } = true;

        /// <summary>The log followed; throws what its program's start throws.</summary>
        public static MergedStream Start(ProcessStartInfo start, string prefix, ConsoleSink sink)
        {
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            Process started = ChildProcesses.Start(start);
            MergedStream stream = new(started);
            Task read = Task.WhenAll(sink.PumpAsync(started.StandardOutput, ConsoleStream.Output, stream.letGo.Token, prefix: prefix),
                sink.PumpAsync(started.StandardError, ConsoleStream.Error, stream.letGo.Token, prefix: prefix));
            IdeErrors.Observe(stream.EndedAsync(read));
            return stream;
        }

        /// <summary>Ends it and all it started; from here on what it says is nobody's.</summary>
        public void Stop()
        {
            UiThread.Check();
            letGo.Cancel();
            ChildProcesses.Kill(process);
        }

        /// <summary>Its program's end, once what it wrote is read (a job it left holding its output ended after
        /// ChildProcesses' grace, one out of the kill's reach let go): background work from the end until it no longer
        /// runs, so a test finds the log's last lines and the stream ended. A program still running holds
        /// nothing.</summary>
        private async Task EndedAsync(Task read)
        {
            // Off the UI thread from here on, whose dispatcher may end first.
            await process.WaitForExitAsync().ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            IDisposable held = BackgroundWork.Scope();
            Task ended = read.Ended();
            if (!await ChildProcesses.OutputEndsAsync(process, ended).ConfigureAwait(false))
            {
                letGo.Cancel();
            }
            UiThread.Post(() =>
            {
                IsRunning = false;
                held.Dispose();
            }, held.Dispose);
        }
    }

    /// <summary>The containers a row's log was read from, and which of them ran.</summary>
    private static string Signature(ContainerRow shown) => string.Join(',', shown.Containers.Select(c => $"{c.Id}:{c.IsRunning}"));

    private void Restart()
    {
        End();
        Console.Clear();
        streamed = row is { } read ? Signature(read) : null;
        if (row is { Kind: ContainerRowKind.AppHost } host)
        {
            Merge(host, Options());
            return;
        }
        if (row is not { } shown || Words(shown) is not { } words)
        {
            return;
        }
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            start = engine().Command(words);
        }
        catch (FileNotFoundException missing)
        {
            Console.Append($"{missing.Message}\n", error: true);
            return;
        }
        runner = new ProcessRunner(Console);
        Dictionary<string, string> environment = start.Environment.Where(v => v.Value is not null).ToDictionary(v => v.Key, v => v.Value!);
        if (runner.Start(start.FileName, words, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), environment) is { } problem)
        {
            Console.Append($"{problem}\n", error: true);
            return;
        }
        streaming = ContainerEngine.Words(words);
    }

    /// <summary>What every log's command is given: follow, its tail, and the engine's timestamps when shown.</summary>
    private List<string> Options()
    {
        List<string> options = ["-f", "--tail", Tail.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        if (Timestamps)
        {
            options.Add("-t");
        }
        return options;
    }

    private IReadOnlyList<string>? Words(ContainerRow shown)
    {
        List<string> options = Options();
        if (shown.Kind == ContainerRowKind.Container || (shown.Kind == ContainerRowKind.Service && shown.Container is not null))
        {
            return ["logs", .. options, shown.Container!.Id];
        }
        if (shown.Project is { } project)
        {
            string[] chosen = shown.Service is { } service ? [service] : [.. project.Services.Select(s => s.Service).Where(log.Shows)];
            if (chosen.Length == 0)
            {
                return null;
            }
            // The services' names are labels', never read as flags.
            return [.. project.Arguments, "logs", "--no-color", .. options, "--", .. chosen];
        }
        return null;
    }
}
