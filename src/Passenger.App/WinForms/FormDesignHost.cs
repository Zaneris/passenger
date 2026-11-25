using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Passenger.App.Run;
using Passenger.App.Xaml;
using Passenger.Core;

namespace Passenger.App.WinForms;

/// <summary>Windows Forms' designer (src/Passenger.WinFormsDesigner, under <c>winformsdesigner/</c> on Windows) in a process
/// of its own, run through ChildProcesses as a XAML previewer is: on the project's runtime configuration and dependencies
/// from a copy of its last build (the state's <c>previews/</c>, so a build never meets its assemblies locked), on the x86
/// .NET for a build for 32-bit x86, or on its own configuration where the project has no build to draw from. Each request
/// goes as a JSON line, the newest waiting while one is drawn; each answer comes back as one, told on the window's thread.</summary>
internal sealed class FormDesignHost : IDisposable
{
    private readonly Channel<string> outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LastLines said = new(5);
    private readonly Action<FormDesignReply> answered;
    private readonly Action<string> ended;
    private Process? process;
    private string? shadow;
    private bool disposed;

    private FormDesignHost(Action<FormDesignReply> answered, Action<string> ended)
    {
        this.answered = answered;
        this.ended = ended;
    }

    /// <summary>Where the app carries the designer.</summary>
    public static string Program => Path.Combine(AppContext.BaseDirectory, "winformsdesigner", "Passenger.WinFormsDesigner.dll");

    /// <summary>The build it draws from, as the build left it; null where it draws without the project's build.</summary>
    public PreviewedBuild? Build { get; private set; }

    /// <summary>The designer started on a copy of the project's build at <paramref name="built"/> (null for none), answering
    /// through <paramref name="answered"/> and saying how it ended through <paramref name="ended"/>; null, having said why
    /// through <paramref name="say"/>, where it could not start.</summary>
    public static async Task<FormDesignHost?> StartAsync(string? built, Action<string> say, Action<FormDesignReply> answered, Action<string> ended)
    {
        if (!File.Exists(Program))
        {
            say("Windows Forms' designer is missing from Passenger's folder (winformsdesigner/)");
            return null;
        }
        FormDesignHost host = new(answered, ended);
        try
        {
            if (await host.RunAsync(built, say) is { } refused)
            {
                host.Dispose();
                say($"The designer could not start: {refused}");
                return null;
            }
            return host;
        }
        catch (Exception error) when (ChildFailures.Of(error))
        {
            host.Dispose();
            say($"The designer could not start: {error.Message}");
            return null;
        }
    }

    /// <summary>The designer started: on the copy with the project's runtime configuration and dependencies, on the x86 .NET
    /// where the build requires it, else on its own configuration; null once started, else why not.</summary>
    private async Task<string?> RunAsync(string? built, Action<string> say)
    {
        List<string> arguments = ["exec"];
        string configuration = Path.ChangeExtension(Program, ".runtimeconfig.json");
        string? x86 = null;
        bool own = true;
        string? assembly = null;
        if (built is not null && await ChildPreviewHost.CopyAsync(say, built) is { } copied)
        {
            Build = copied.Build;
            shadow = copied.Shadow;
            assembly = Path.Combine(copied.Shadow, Path.GetFileName(built));
            string name = Path.GetFileNameWithoutExtension(assembly);
            string copy = assembly;
            string? runs = await BackgroundWork.Run(() => RuntimeConfig.Read(copy)?.TargetFramework);
            own = FrameworkNames.Major(runs) is not >= 8;
            if (!own)
            {
                configuration = Path.Combine(copied.Shadow, name + ".runtimeconfig.json");
            }
            if (OperatingSystem.IsWindows() && await BackgroundWork.Run(() => X86Programs.Requires(copy)))
            {
                x86 = X86Programs.Root(new Dictionary<string, string>());
                string root = x86;
                string configured = own ? Program : copy;
                if (await BackgroundWork.Run(() => X86Programs.Lacking(root, configured, new Dictionary<string, string>())) is { Count: > 0 } lacking)
                {
                    return Refusals.X86RuntimeMissing(root, lacking);
                }
            }
            arguments.AddRange(["--runtimeconfig", configuration]);
            string dependencies = Path.Combine(copied.Shadow, name + ".deps.json");
            if (File.Exists(dependencies))
            {
                arguments.AddRange(["--depsfile", dependencies]);
            }
        }
        else
        {
            arguments.AddRange(["--runtimeconfig", configuration]);
        }
        arguments.Add(Program);
        if (assembly is not null)
        {
            arguments.AddRange(["--assembly", assembly]);
        }
        string folder = shadow ?? Path.GetDirectoryName(Program)!;
        ProcessStartInfo start = x86 is null ? ChildProcesses.DotnetCommand(arguments, folder) : ChildProcesses.DotnetCommand(Path.Combine(x86, Dotnet.HostName), arguments, folder);
        if (own)
        {
            // Its own configuration rolls forward to the newest runtime, which a project on a preview of .NET needs.
            start.Environment[RuntimeVariables.RollForwardToPrerelease] = "1";
        }
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        Process started;
        using (BackgroundWork.Scope())
        {
            started = await ChildProcesses.StartAsync(start);
        }
        process = started;
        Task errors = ChildProcesses.Hear(started, errors: Heard);
        IdeErrors.Observe(errors);
        // Long-lived readers of the designer's streams: each answer holds the window's work itself (UiThread.Post).
        IdeErrors.Observe(BackgroundWork.Unheld(() => WriteAsync(started)));
        Task output = BackgroundWork.Unheld(() => ReadAsync(started));
        IdeErrors.Observe(output);
        AvaloniaPreviewHost.HearEnd(started, [errors, output], () => UiThread.Post(() => Ended(started)));
        say(assembly is null ? "Starting the designer without the project's build…" : $"Starting the designer on {Path.GetFileName(assembly)}…");
        return null;
    }

    /// <summary>A request sent, the newest the designer reads replacing any it has yet to draw.</summary>
    public void Send(FormDesignRequest request)
    {
        if (!disposed)
        {
            outgoing.Writer.TryWrite(JsonSerializer.Serialize(request, PeerJson.Options));
        }
    }

    private void Heard(string? line)
    {
        if (line is null)
        {
            return;
        }
        lock (said)
        {
            said.Heard(line);
        }
    }

    private async Task WriteAsync(Process to)
    {
        try
        {
            await foreach (string line in outgoing.Reader.ReadAllAsync())
            {
                await to.StandardInput.WriteLineAsync(line);
                await to.StandardInput.FlushAsync();
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The designer ended, which Ended says.
        }
    }

    private async Task ReadAsync(Process from)
    {
        try
        {
            // A line that is not the protocol's is the designer's own output.
            await foreach (FormDesignReply answer in JsonLines.ReadAsync(ChildProcesses.OutputStream(from), FormDesignWire.Reply, (line, _) => Heard(line)))
            {
                UiThread.Post(() =>
                {
                    if (!disposed)
                    {
                        answered(answer);
                    }
                });
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The designer ended, which Ended says.
        }
    }

    private void Ended(Process gone)
    {
        if (disposed || gone != process)
        {
            return;
        }
        string last;
        lock (said)
        {
            last = said.Thrown ?? said.Last ?? "it said nothing";
        }
        string how = ExitCodes.Said(gone.ExitCode);
        IdeErrors.Write($"Windows Forms' designer ended with {how}: {last}");
        ended($"The designer ended with {how}: {last}");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        outgoing.Writer.TryComplete();
        Process? running = process;
        process = null;
        if (running is not null)
        {
            ChildProcesses.Kill(running);
        }
        if (shadow is not null)
        {
            ChildPreviewHost.Forget(shadow, running);
        }
        else
        {
            running?.Dispose();
        }
    }
}
