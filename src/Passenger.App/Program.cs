using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using Velopack;
using Passenger.Core;

namespace Passenger.App;

internal static class Program
{
    /// <summary>When Main began, so a screenshot can say how long its first frame took. Set as Main's
    /// first act, since a static initializer would run only when something first read it.</summary>
    public static long StartedAt { get; private set; }

    /// <summary>What the command line asked to open, for the first window the desktop lifetime makes. The process's, as
    /// its command line is: the windows opened after it open what the person chooses.</summary>
    public static LaunchOptions Options { get; private set; } = LaunchOptions.Empty;

    [STAThread]
    public static int Main(string[] args)
    {
        StartedAt = Stopwatch.GetTimestamp();
        // Run by git to ask for a login: the window answers, and nothing of the IDE starts.
        if (Passenger.Vcs.GitAskPass.Answer(args) is int asked)
        {
            return asked;
        }
        // Run by git as its gpg to sign a commit, nothing of the IDE started; synchronously, as Main returns gpg's exit code.
        if (Synchronously.Run(() => Passenger.Vcs.GpgLoopback.RunAsync(args)) is int signed)
        {
            return signed;
        }
        // First, as Velopack asks; an update never installs as a second Passenger starts, which would end the first.
        if (Installation.Installed)
        {
            Installation.Hooked(VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false)).Run();
        }
        LaunchOptions options;
        try
        {
            options = LaunchOptions.Parse(args);
            _ = Updates.ReleaseFeed.Default;
            _ = Discord.DiscordIpc.Default;
            _ = ThemeSource.Repository.Reached;
            Web.JsonSchemas.CheckVariable();
            Web.DockerHubTags.CheckVariable();
            OpenVsx.CheckVariable();
            _ = Containers.BuildKitChecks.ByDefault;
            _ = Debugging.BrowserRun.Proxy;
            Mcp.McpProxy.CheckVariable();
            _ = Mcp.McpHost.StartsLanguageServer;
            _ = MainWindow.WatchSaysLater;
            _ = CSharp.CSharpService.SlowLoad;
            _ = Mcp.AgentCall.Every;
            _ = Dap.AdapterEnvironment.EvaluationTimeout;
            _ = Text.TextSearch.Apart;
            _ = Languages.TextMate.TextMateHighlighter.LineTime;
            _ = Waits.Scale;
        }
        catch (FormatException error)
        {
            Console.Error.WriteLine($"passenger: {error.Message}");
            return 2;
        }

        Options = options;
        // Before anything starts, so nothing started reads it; a relay keeps its own, which the updated app it becomes reads.
        Stream? input = options.Mcp == McpMode.Attach ? null : StandardInput.Take();
        // Before the shell's environment or a look for leftovers: Main's thread waits here for a running Passenger's answer.
        if (LaunchHandOff.HandsOff(options) && Synchronously.Run(() => LaunchHandOff.HandOffAsync(LaunchHandOff.Of(options), CancellationToken.None)))
        {
            input?.Dispose();
            return 0;
        }
        Core.ShellEnvironment.Load();
        if (options.Mcp != McpMode.Attach && options.Screenshot is null)
        {
            // What a crash left goes now, not held: an engine that answers slowly or not at all delays nothing.
            IdeErrors.Observe(Containers.ContainerLabels.LookForLeftovers(Containers.ContainerEngine.Shared, State.StateStore.DefaultRoot));
        }
        switch (options.Mcp)
        {
            case McpMode.Attach:
                return Mcp.McpHost.Attach(options.AttachTo, options.Folder);
            case McpMode.Stdio:
                return Mcp.McpHost.RunStdio(options, input!);
        }
        if (options.Headless)
        {
            return Mcp.McpHost.RunHeadless(options, input!);
        }
        // A test run's launch that no running Passenger took starts this build headless, never a window on the person's screen.
        if (Mcp.McpProxy.StartsHeadless && options.Screenshot is null)
        {
            return Mcp.McpHost.RunHeadless(options, input!);
        }
        input?.Dispose();
        if (options.Screenshot is { } screenshot)
        {
            return Apart(() => Screenshot.Run(options, screenshot));
        }
        int code = BuildAvaloniaApp()
            .Checked()
            .AfterSetup(builder => ApplyTheme(builder.Instance!, options))
            .StartWithClassicDesktopLifetime(args);
        // What the windows ended (debuggers, Roslyn's servers) has a moment; then an update made ready installs, last of all.
        PassengerApp.End();
        return code;
    }

    /// <summary>A screenshot keeps nothing of the person's: unless PASSENGER_SETTINGS names a folder, its state and logs
    /// (Roslyn's server's among them) go to a temporary one, removed when it ends, and its terminal's shell never reads or
    /// writes the person's history or startup files.</summary>
    private static int Apart(Func<int> run)
    {
        string? scratch = null;
        if (Environment.GetEnvironmentVariable(State.StateStore.Variable) is not { Length: > 0 } root)
        {
            scratch = Directory.CreateTempSubdirectory("passenger-screenshot-").FullName;
            root = scratch;
            Environment.SetEnvironmentVariable(State.StateStore.Variable, scratch);
        }
        // sh takes HISTFILE as given, and zsh takes its startup files, and from them its history, from ZDOTDIR.
        Environment.SetEnvironmentVariable("HISTFILE", Path.Combine(root, "history"));
        Environment.SetEnvironmentVariable("ZDOTDIR", Directory.CreateDirectory(Path.Combine(root, "zsh")).FullName);
        try
        {
            return run();
        }
        finally
        {
            if (scratch is not null)
            {
                // The log is written off a queue: what it still holds would make the folder again after it went.
                IdeLog.Flush();
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A log the language server still holds open on Windows: the system's temporary folder is cleared in time.
                }
            }
        }
    }

    /// <summary>Ends Passenger as a malformed flag does, exit code 2 and why on standard error, where the command line's
    /// <c>--run</c> or <c>--debug</c> could not start in the window it opened (<see cref="MainWindow.StartedRun"/>).</summary>
    public static async Task EndUnlessStartedAsync(MainWindow window, Action<int> end)
    {
        if (await window.StartedRun is { } refused)
        {
            SayNotStarted(refused);
            end(2);
        }
    }

    /// <summary>Says in the log and on standard error why what the command line asked to start did not.</summary>
    public static void SayNotStarted(string why)
    {
        IdeErrors.Write($"Launch: {why}");
        Console.Error.WriteLine($"passenger: {why}");
    }

    /// <summary>The theme, its dark or light and the code's colours the command line names, over those in force.</summary>
    public static void ApplyTheme(Application app, LaunchOptions options)
    {
        ApplyTheme(app, options.Theme);
        if (options.Look is not null || options.Scheme is not null)
        {
            Themes.Apply(app, options.Look is { } look ? look.Theme : Themes.Look, options.Scheme is { } scheme ? scheme.Theme : Themes.Scheme);
        }
    }

    public static void ApplyTheme(Application app, ThemeChoice? theme)
    {
        if (theme is { } choice)
        {
            Themes.UseVariant(app, choice switch
            {
                ThemeChoice.Dark => ThemeVariant.Dark,
                ThemeChoice.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            });
        }
    }

    /// <summary>The XAML previewer calls this by name, so it stays public and parameterless. Avalonia's log goes to the
    /// IDE's own (<see cref="IdeErrors"/>), which the app installs.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
#pragma warning disable AVALONIA_X11_CSD // X11 extends a window into its frame only with this on: the merged title bar (TitleBars).
            .With(new X11PlatformOptions { EnableDrawnDecorations = true });
#pragma warning restore AVALONIA_X11_CSD
}
