using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Passenger.App.State;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Text;
using Xunit.v3;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Passenger.Tests.TestApp))]
[assembly: Passenger.Tests.TestsLeaveNothingBehind]
[assembly: AssemblyFixture(typeof(Passenger.Tests.TestRun))]

namespace Passenger.Tests;

/// <summary>The real app on the headless platform, drawing with Skia so text measures as it does on
/// a desktop.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Passenger.App.App>()
        .UseSkia()
        .UseHarfBuzz()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>What the whole run shares, set before anything reads it and never changed: a folder of the run's own in the
/// system's temporary folder, holding the IDE's state (<c>PASSENGER_SETTINGS</c>: its log, the language servers' logs,
/// the profiler's snapshots; the agents' sockets go in a folder named for it), every test's folders and the shells' history, and an empty Git
/// configuration in place of the person's and the system's, which every git the tests and the IDE run inherits. So a run
/// never writes to the person's own state, log or history, an agent of theirs never attaches to a test's window, and a
/// person's commit signing or hooks never reach a test's commit. The folder goes when the run ends; one a run that was
/// killed left goes when the next begins.</summary>
public sealed class TestRun : IDisposable
{
    internal const string Prefix = "passenger-run-";

    /// <summary>What ends the run's folder's name: Spotlight indexes the system's temporary folder but passes over a folder
    /// so named, so it reads none of a run's thousands of files, nor the stand-in apps whose seals tests break.</summary>
    internal const string Unindexed = ".noindex";

    /// <summary>The run's folder, named for the test process.</summary>
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), Prefix + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + Unindexed);

    /// <summary>The IDE's state folder for the whole run.</summary>
    public static string Settings => Path.Combine(Root, "settings");

    /// <summary>Where the run's IDE finds user secrets in place of the person's own: a project's folder there, by its id, is
    /// what a container run mounts.</summary>
    public static string UserSecrets => Path.Combine(Root, "usersecrets");

    /// <summary>What every wall-clock limit of a test is multiplied by (<see cref="Limits.Scaled"/>): <c>PASSENGER_TEST_TIME_SCALE</c>,
    /// read once, 1 when unset, as on a developer's machine, for whose times each limit is written; CI sets it for each
    /// system from how much slower its runners are. A value that is no number above 0 fails every test of the run, saying
    /// so (<see cref="TestRun()"/>), the run's own clean-up beforehand taking 1.</summary>
    public static double TimeScale => ReadTimeScale.Value.Scale;

    [IdentifierHome]

    public const string TimeScaleVariable = "PASSENGER_TEST_TIME_SCALE";

    private static readonly Lazy<(double Scale, string? Problem)> ReadTimeScale = new(() =>
    {
        string? given = Environment.GetEnvironmentVariable(TimeScaleVariable);
        if (string.IsNullOrEmpty(given))
        {
            return (1, null);
        }
        return double.TryParse(given, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out double scale)
            && scale > 0 && double.IsFinite(scale)
            ? (scale, null)
            : (1, $"{TimeScaleVariable} is \"{given}\": it must be a number above 0, such as 1.5, by which every test's time limits are multiplied");
    });

    /// <summary>The run's fixture, made before its first test: a malformed time scale fails each test with why (a module
    /// initializer's exception reaches dotnet test only as "did not return valid JSON").</summary>
    public TestRun()
    {
        if (ReadTimeScale.Value.Problem is { } problem)
        {
            throw new InvalidOperationException(problem);
        }
    }

    /// <summary>Where the run's windows look for Discord (<c>PASSENGER_DISCORD_IPC</c>): a place nothing listens.</summary>
    public static string DiscordNowhere => OperatingSystem.IsWindows()
        ? $"{Prefix}{Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}-discord-ipc"
        : Path.Combine(Root, "discord-ipc");

    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Begin()
    {
        // Before anything is started, which inherits it.
        Nice.Normal();
        // What the IDE has in flight is recorded from the first window on, for the quiet wait (Ui.Settle) to wait out.
        Passenger.Core.InFlight.Watched = true;
        foreach (string left in Directory.EnumerateDirectories(Path.GetTempPath(), Prefix + "*"))
        {
            // A run still going has its process; one killed has none, and only an id reused since keeps its folder.
            if (int.TryParse(Path.GetFileName(left)[Prefix.Length..].Replace(Unindexed, string.Empty, StringComparison.Ordinal), out int id)
                && (id == Environment.ProcessId || !Processes.IsRunning(id)))
            {
                TestFolders.Delete(left);
            }
        }
        Directory.CreateDirectory(Settings);
        string gitConfig = Path.Combine(Root, "gitconfig");
        File.WriteAllText(gitConfig, string.Empty);
        Environment.SetEnvironmentVariable(StateStore.Variable, Settings);
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", gitConfig);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
        // Configuration given on git's command line through the environment, which would come before the file's.
        Environment.SetEnvironmentVariable("GIT_CONFIG_PARAMETERS", null);
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", null);
        // The terminals' shells keep their history in the run's: sh takes HISTFILE, zsh its startup files from ZDOTDIR.
        Environment.SetEnvironmentVariable("HISTFILE", Path.Combine(Root, "history"));
        Environment.SetEnvironmentVariable("ZDOTDIR", Directory.CreateDirectory(Path.Combine(Root, "zsh")).FullName);
        File.WriteAllText(Path.Combine(Root, "zsh", ".zshrc"), string.Empty);
        NuGetHome();
        // No test reaches GitHub: a Passenger that checks for updates without a stand-in asks where nothing listens.
        Environment.SetEnvironmentVariable(Passenger.App.Updates.ReleaseFeed.Variable, "http://127.0.0.1:9/repos/Zaneris/passenger/releases/latest");
        // Nor NuGet's symbol server: a theme's sources asked for without a stand-in of the test's come from where nothing listens.
        Environment.SetEnvironmentVariable(Passenger.App.Xaml.SourceLink.Variable, "http://127.0.0.1:9/");
        // Nor the web for a JSON schema a file names: those Passenger does not carry are asked of where nothing listens.
        Environment.SetEnvironmentVariable(Passenger.App.Web.JsonSchemas.Variable, "http://127.0.0.1:9/");
        // Nor Docker Hub for an image's tags: a Dockerfile server without a stand-in asks where nothing listens.
        Environment.SetEnvironmentVariable(Passenger.App.Web.DockerHubTags.Variable, "http://127.0.0.1:9/");
        // Nor a registry for a Dockerfile's base images: BuildKit's checks are off but in the one test that turns them on.
        Environment.SetEnvironmentVariable(Passenger.App.Containers.BuildKitChecks.Variable, "off");
        // Nor the person's user secrets: a container run mounts a project's from the run's own folder, which a test writes.
        Environment.SetEnvironmentVariable(Passenger.App.Containers.ContainerPlans.UserSecretsVariable, UserSecrets);
        // Nor the person's Passenger: an agent's call with no window to join starts this build headless, in the run's state.
        Environment.SetEnvironmentVariable(Passenger.App.Mcp.McpProxy.StartVariable, "headless");
        // Nor the person's Discord: a window without a stand-in of its own looks where nothing listens.
        Environment.SetEnvironmentVariable(Passenger.App.Discord.DiscordIpc.Variable, DiscordNowhere);
        // Nor npm's registry: an npm install the IDE runs takes only what the run's own empty cache has.
        string npmConfig = Path.Combine(Root, "npmrc");
        File.WriteAllText(npmConfig, string.Empty);
        Environment.SetEnvironmentVariable("npm_config_userconfig", npmConfig);
        Environment.SetEnvironmentVariable("npm_config_globalconfig", npmConfig);
        Environment.SetEnvironmentVariable("npm_config_cache", Path.Combine(Root, "npm-cache"));
        Environment.SetEnvironmentVariable("npm_config_offline", "true");
        Environment.SetEnvironmentVariable("npm_config_update_notifier", "false");
        Environment.SetEnvironmentVariable("npm_config_audit", "false");
        Environment.SetEnvironmentVariable("npm_config_fund", "false");
        // Nor the person's application data, where a Testing Platform app writes its first-run sentinel unless opted out.
        Environment.SetEnvironmentVariable("TESTINGPLATFORM_TELEMETRY_OPTOUT", "1");
        // Nor Aspire's: an aspire CLI or AppHost the run starts keeps its settings and first-run sentinel in the run's home.
        Environment.SetEnvironmentVariable("ASPIRE_HOME", Path.Combine(Root, "aspire-home"));
        Environment.SetEnvironmentVariable("ASPIRE_CLI_TELEMETRY_OPTOUT", "1");
        Environment.SetEnvironmentVariable("features__updateNotificationsEnabled", "false");
        Environment.SetEnvironmentVariable("ASPIRE_VERSION_CHECK_DISABLED", "true");
        // Nor the person's aspire CLI: the run's own is the only one on its PATH (AspireCli), and makes no certificate.
        Environment.SetEnvironmentVariable("ASPIRE_CLI_GENERATE_HTTPS_CERTIFICATE", "false");
        AspireCli.Begin(Root);
        // Waits a person never notices, which a test holding the IDE to them would wait out in full, made short.
        Environment.SetEnvironmentVariable(Passenger.Dap.AdapterEnvironment.EvaluationTimeoutVariable, "3000");
        Environment.SetEnvironmentVariable(Passenger.App.MainWindow.WatchSaysLaterVariable, "3000");
        Environment.SetEnvironmentVariable(Passenger.App.Mcp.AgentCall.EveryVariable, "1000");
        // The IDE's own waits on what it started (an Aspire service's start, a restart's wait for it) as long as the run's.
        Environment.SetEnvironmentVariable(Passenger.Core.Waits.ScaleVariable, TimeScale.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // A run stopped before its end (Ctrl+C) leaves nothing either.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
    }

    /// <summary>On macOS and Linux, NuGet's home for the run, which NuGet takes from DOTNET_CLI_HOME: its user nuget.config,
    /// its credential providers and its HTTP cache are the run's, so no test reads the person's feeds or credentials or writes
    /// their nuget.config. Its nuget.config names nuget.org and the person's package cache, named as NuGet names its default
    /// (ending in a separator), so a restore downloads nothing new and writes the same NuGetPackageRoot into a project's
    /// nuget.g.props as the person's builds do, leaving their next build nothing to compile again.</summary>
    private static void NuGetHome()
    {
        if (OperatingSystem.IsWindows())
        {
            // NuGet reads %APPDATA% there, so each test's folder clears its feeds; dotnet new's templates get the run's hive.
            Passenger.App.Templates.ProjectTemplates.Hive = Directory.CreateDirectory(Path.Combine(Root, "dotnet-new")).FullName;
            return;
        }
        string personal = Environment.GetEnvironmentVariable("DOTNET_CLI_HOME") is { Length: > 0 } given ? given : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string home = Directory.CreateDirectory(Path.Combine(Root, "home")).FullName;
        string settings = Directory.CreateDirectory(Path.Combine(home, ".nuget", "NuGet")).FullName;
        string cache = System.Security.SecurityElement.Escape(Path.Combine(personal, ".nuget", "packages") + Path.DirectorySeparatorChar);
        File.WriteAllText(Path.Combine(settings, "NuGet.Config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
              <config>
                <add key="globalPackagesFolder" value="{cache}" />
              </config>
            </configuration>
            """);
        Environment.SetEnvironmentVariable("DOTNET_CLI_HOME", home);
        Environment.SetEnvironmentVariable("NUGET_COMMON_APPLICATION_DATA", Directory.CreateDirectory(Path.Combine(Root, "machine")).FullName);
        Environment.SetEnvironmentVariable("NUGET_NETCORE_PLUGIN_PATHS", null);
        Environment.SetEnvironmentVariable("NUGET_PLUGIN_PATHS", null);
        Environment.SetEnvironmentVariable("DOTNET_GENERATE_ASPNET_CERTIFICATE", "false");
        Environment.SetEnvironmentVariable("DOTNET_ADD_GLOBAL_TOOLS_TO_PATH", "false");
        Environment.SetEnvironmentVariable("DOTNET_NOLOGO", "1");
    }

    /// <summary>The run's end, once every test is done (xunit disposes the assembly's fixture then).</summary>
    public void Dispose()
    {
        End();
        // Once more after the log's own last flush, which it hooked after the run began, and so runs before this.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
    }

    private static void End()
    {
        // No child holds a container: what the run's tests made, and what they had the IDE make, goes by its labels.
        Docker.RemoveEverything();
        // The windows' agent sockets are in a folder named for the run's state, outside it.
        foreach (string folder in (string[])[Root, Passenger.App.Mcp.McpHost.SocketFolder, AspireCli.Home])
        {
            try
            {
                TestFolders.Delete(folder);
            }
            catch (Exception held) when (held is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"The tests' folder {folder} could not be removed: {held.Message}");
            }
        }
    }
}

/// <summary>A test's wall-clock limits, each written as the time on a developer's machine and given as that times the run's
/// <see cref="TestRun.TimeScale"/>: Ui's waits scale what they are given, and every other limit a test sets goes through
/// <see cref="Scaled"/>.</summary>
public static class Limits
{
    public static TimeSpan Scaled(this TimeSpan limit) => limit == Timeout.InfiniteTimeSpan ? limit : limit * TestRun.TimeScale;
}

/// <summary>The run's nice value on macOS and Linux. MSBuild keeps no idle node for the next build at any nice value but 0,
/// and GitHub's macOS runner starts everything at -10: the run is brought to 0 as it begins, which lowers its priority and
/// so needs no privilege, and what it starts inherits that. A run started above 0 (by zsh's &amp;, at 5) could come down
/// only with privilege, so a test that needs a node kept skips there, saying why.</summary>
public static partial class Nice
{
    private const int PrioProcess = 0;

    /// <summary>The run's nice value now; 0 on Windows, which has none.</summary>
    public static int Value
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return 0;
            }
            // -1 is a nice value as well as the failure: only errno tells them apart.
            System.Runtime.InteropServices.Marshal.SetLastPInvokeError(0);
            int nice = GetPriority(PrioProcess, 0);
            return nice == -1 && System.Runtime.InteropServices.Marshal.GetLastPInvokeError() != 0 ? 0 : nice;
        }
    }

    /// <summary>Brings the run up to 0 from below. On Linux a nice value is each thread's, so each thread there is now.</summary>
    internal static void Normal()
    {
        if (OperatingSystem.IsWindows() || Value >= 0)
        {
            return;
        }
        IEnumerable<int> threads = OperatingSystem.IsLinux() && Directory.Exists("/proc/self/task")
            ? Directory.EnumerateDirectories("/proc/self/task").Select(task => int.Parse(Path.GetFileName(task), System.Globalization.CultureInfo.InvariantCulture))
            : [0];
        foreach (int thread in threads)
        {
            SetPriority(PrioProcess, thread, 0);
        }
    }

    /// <summary>Skips a test that needs MSBuild to keep a node for the next build, when the run is at a nice value at which
    /// it keeps none.</summary>
    public static void SkipUnlessMSBuildKeepsNodes() => Assert.SkipWhen(Value != 0,
        $"The tests run at nice {Value}, at which MSBuild keeps no idle node for the next build, and only a privileged process could bring them to 0");

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "getpriority", SetLastError = true)]
    private static partial int GetPriority(int which, int who);

    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriority(int which, int who, int priority);
}

/// <summary>A test leaves nothing behind for the next, however it ends. A window test starts with no background work held:
/// each test has a dispatcher of its own, and work a test left running (one that failed before its window settled, or
/// closed it mid-parse) ends on that dispatcher, which never runs again, and held for ever would fail every window test
/// after it on <see cref="Ui.Settle"/>'s limit. And after a window test, every window it left open is closed (one that
/// failed before its own close) and what those windows started (a language server, a build, a debugger) is waited out,
/// then the folders the test made as it ran are removed, the windows that used them gone first.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TestsLeaveNothingBehindAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (IsWindowTest(methodUnderTest))
        {
            BackgroundWork.Forget();
            Passenger.Core.InFlight.Forget();
            OpenWindows.Watch();
        }
        TestFolders.Begin(test.UniqueID);
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        try
        {
            if (IsWindowTest(methodUnderTest) && !OpenWindows.CloseAll())
            {
                TestContext.Current.SendDiagnosticMessage($"{test.TestDisplayName} left {BackgroundWork.Pending} pieces of background work held after its windows closed");
            }
        }
        finally
        {
            TestFolders.End(test.UniqueID);
        }
    }

    public static bool IsWindowTest(MethodInfo method) =>
        method.GetCustomAttribute<AvaloniaFactAttribute>() is not null || method.GetCustomAttribute<AvaloniaTheoryAttribute>() is not null;
}

/// <summary>The windows open now, as a test leaves them, for <see cref="TestsLeaveNothingBehindAttribute"/> to close.</summary>
public static class OpenWindows
{
    private static readonly List<Window> Open = [];
    private static bool watching;

    /// <summary>The longest the work of windows closed after a test may take to land, past the end their own limit gives
    /// what they started.</summary>
    private static readonly TimeSpan Landing = TimeSpan.FromSeconds(30);

    public static void Watch()
    {
        if (!watching)
        {
            watching = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Open.Add(window));
            Window.WindowClosedEvent.AddClassHandler<Window>((window, _) => Open.Remove(window));
        }
        Open.Clear();
    }

    /// <summary>Closes every window still open, the last opened first, waits for what each started to end (its own limit
    /// kills what will not), then for their background work to land, which a dispatcher that never runs again could not
    /// let go. A window its test left open (one that failed) leaves no warm server for the next test: the one it would
    /// keep is ended with the rest. Whether it all landed: work that never does, the next window test lets go of as it
    /// starts.</summary>
    public static bool CloseAll()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            throw new InvalidOperationException("Windows left open can only be closed on their own thread");
        }
        List<Task> ending = [];
        while (Open.Count > 0)
        {
            Window last = Open[^1];
            Open.RemoveAt(Open.Count - 1);
            if (last.IsVisible)
            {
                last.Close();
                if (last is Passenger.App.MainWindow { Ended: { } ended } closed)
                {
                    ending.Add(closed.WarmServer is { } warm ? ended.ContinueWith(_ => warm.Drop(), TaskScheduler.Default).Unwrap() : ended);
                }
            }
        }
        if (ending.Count == 0 && BackgroundWork.Pending == 0)
        {
            return true;
        }
        Ui.Pumped(() => ending.All(e => e.IsCompleted) && BackgroundWork.Pending == 0, Passenger.App.MainWindow.EndLimit + Landing.Scaled(), scaled: false);
        return BackgroundWork.Pending == 0;
    }
}

/// <summary>A code editor alone in a window, driven the way a person drives it: keys by Rider's
/// macOS keymap (so a test reads the same on every platform), typed text, and clicks.</summary>
public sealed class EditorRig
{
    public EditorRig(string text)
    {
        // The one document a test makes: the editor's, which the test then drives as a person does.
#pragma warning disable RS0030
        Document = new TextDocument(text);
#pragma warning restore RS0030
        Editor = new CodeEditor { Keymap = DefaultKeymaps.VisualStudioMac(), Document = Document };
        Window = new Window { Width = 900, Height = 700, Content = Editor };
        Window.Show();
        Editor.Focus();
        Ui.Settle();
    }

    public Window Window { get; }
    public CodeEditor Editor { get; }
    public TextDocument Document { get; }
    public string Text => Document.Snapshot.ToString();
    public int Caret => Editor.Selections.Primary.Caret;

    /// <summary>Where the document's text first differs from <paramref name="expected"/>, or -1 where it is that text,
    /// every character, read piece by piece from the rope as the editor reads it: <see cref="Text"/> would copy the whole
    /// text twice, on the large object heap, for every key a long session checks.</summary>
    public int DifferenceFrom(string expected)
    {
        TextSnapshot text = Document.Snapshot;
        int at = 0;
        int differs = -1;
        text.VisitChunks(0, text.Length, chunk =>
        {
            if (differs < 0)
            {
                ReadOnlySpan<char> mine = chunk.Span;
                int common = mine.CommonPrefixLength(expected.AsSpan(at, Math.Min(mine.Length, expected.Length - at)));
                differs = common < mine.Length ? at + common : -1;
                at += mine.Length;
            }
        });
        return differs >= 0 ? differs : at == expected.Length ? -1 : at;
    }

    /// <summary>"meta shift L", "alt shift PERIOD", "ENTER": Rider's own keymap notation.</summary>
    public void Press(string keys)
    {
        Ui.Press(Window, keys);
    }

    /// <summary>Types one character at a time, as a keyboard does, so the editor's typing help
    /// (closing brackets, stepping over them) sees each one.</summary>
    public void Type(string text)
    {
        foreach (char c in text)
        {
            Window.KeyTextInput(c.ToString());
        }
        Ui.Settle();
    }

    /// <summary>Puts the caret where a click on that offset would.</summary>
    public void PlaceCaret(int offset) => Editor.Selections = SelectionSet.Single(offset);

    /// <summary>Adds a caret where an alt+shift click on that offset would, there being none there, as the primary.</summary>
    public void AddCaret(int offset) => Editor.Selections = Editor.Selections.Add(Selection.At(offset));
}

/// <summary>A test's temporary folders, in the run's own (<see cref="TestRun.Root"/>), deleted as Windows lets them be: git
/// writes its objects read-only, which Windows refuses to delete, and a folder a process still runs in (a git status the
/// closed window started, a debugged program being killed) cannot go until it has ended, so both are waited out for a
/// while. One already gone is no error.</summary>
public static class TestFolders
{
    /// <summary>The folders each test running now made as it ran, by the test's id, from its start (its class's are made
    /// before then, and go with its class's Dispose).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<string>> Made = new(StringComparer.Ordinal);

    /// <summary>A new, empty folder of the test's own, its name starting with <paramref name="name"/>: one made as a test
    /// runs is removed when it ends, however it ends, after its windows (<see cref="TestsLeaveNothingBehindAttribute"/>).</summary>
    public static string Create(string name)
    {
        string path = Unowned(name);
        if (TestContext.Current.Test?.UniqueID is { } test && Made.TryGetValue(test, out List<string>? made))
        {
            lock (made)
            {
                made.Add(path);
            }
        }
        return path;
    }

    /// <summary>A new, empty folder that is no test's own, however late in a test it is made: one a class's fixture makes
    /// for its tests to take (<see cref="RestoredFolders"/>), which whoever takes it removes.</summary>
    public static string Unowned(string name)
    {
        string path;
        do
        {
            path = Path.Combine(TestRun.Root, name + Guid.NewGuid().ToString("N")[..8]);
        }
        while (Directory.Exists(path));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Gets a test class's folders ready (a restore, a repository), removing them if that fails: a class whose
    /// constructor throws is never disposed.</summary>
    public static void Prepare(Action prepare, params string[] paths)
    {
        try
        {
            prepare();
        }
        catch
        {
            foreach (string path in paths)
            {
                Delete(path);
            }
            throw;
        }
    }

    /// <summary>A symlink to a file, as a dotfiles folder keeps a configuration; the test is skipped where the system lets
    /// this user make none (Windows without Developer Mode).</summary>
    public static void Link(string path, string target)
    {
        try
        {
            File.CreateSymbolicLink(path, target);
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"Windows lets this user make no symlink: {refused.Message}");
        }
    }

    /// <summary>A symlink to a folder; where Windows lets this user make none (no Developer Mode), a junction, which any
    /// user may make and which a walk of the folders goes through as it goes through a symlink.</summary>
    public static void LinkFolder(string path, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(path, target);
        }
        catch (Exception refused) when (OperatingSystem.IsWindows() && refused is IOException or UnauthorizedAccessException)
        {
            Passenger.Core.ChildResult made = Task.Run(() => Passenger.Core.ChildProcesses.RunAsync(
                Passenger.Core.ChildProcesses.Command("cmd.exe", ["/c", "mklink", "/J", path, target]), limit: TimeSpan.FromMinutes(1).Scaled())).GetAwaiter().GetResult();
            Assert.True(made.Succeeded, $"Windows made neither a symlink ({refused.Message}) nor a junction at {path}: {made.Output}{made.Error}");
        }
    }

    /// <summary>A file of a test's folder written, the folders it is in made first; its full path, its separators the
    /// system's, as the IDE's own paths are.</summary>
    public static string Write(string folder, string relative, string text)
    {
        string path = Path.GetFullPath(Path.Combine(folder, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public static void Begin(string test) => Made[test] = [];

    public static void End(string test)
    {
        if (!Made.TryRemove(test, out List<string>? made))
        {
            return;
        }
        List<Exception> held = [];
        foreach (string path in made)
        {
            try
            {
                Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                held.Add(error);
            }
        }
        if (held.Count > 0)
        {
            throw new IOException($"The test's folders could not all be removed, so the next run finds them: {string.Join("; ", held.Select(e => e.Message))}", held[0]);
        }
    }

    public static void Delete(string path)
    {
        // A settings folder's log lines still queued would make its logs folder again once it was gone.
        Passenger.App.IdeErrors.Flush();
        bool deleted = Waiting.Until(() =>
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Writable(path);
                return false;
            }
        }, TimeSpan.FromSeconds(30), every: TimeSpan.FromMilliseconds(100));
        if (!deleted && Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException error) when (OperatingSystem.IsWindows())
            {
                throw new IOException($"{error.Message} Still running: {Running(path)}", error);
            }
        }
    }

    /// <summary>The programs that may hold a folder on Windows, which no API names for a folder a process runs in: those
    /// the run started (its children's children, by their parents), and any naming the folder or run by dotnet.</summary>
    private static string Running(string path)
    {
        try
        {
            System.Diagnostics.ProcessStartInfo start = new("powershell", ["-NoProfile", "-Command",
                "Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId)`t$($_.ParentProcessId)`t$($_.Name)`t$($_.CommandLine)\" }"])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using System.Diagnostics.Process listing = System.Diagnostics.Process.Start(start)!;
            string listed = listing.StandardOutput.ReadToEnd();
            listing.WaitForExit(30_000);
            Dictionary<int, (int Parent, string Row)> processes = [];
            foreach (string row in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] fields = row.Split('\t');
                if (fields.Length >= 3 && int.TryParse(fields[0], out int id) && int.TryParse(fields[1], out int parent))
                {
                    processes[id] = (parent, row);
                }
            }
            bool Ours(int id)
            {
                for (int depth = 0; depth < 64 && processes.TryGetValue(id, out (int Parent, string Row) process); depth++)
                {
                    if (process.Parent == Environment.ProcessId)
                    {
                        return true;
                    }
                    id = process.Parent;
                }
                return false;
            }
            string[] holding = [.. processes.Where(p => Ours(p.Key) || p.Value.Row.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                || p.Value.Row.Split('\t')[2].Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)).Select(p => p.Value.Row.Replace('\t', ' '))];
            return holding.Length == 0 ? "nothing of the run's" : string.Join("; ", holding);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return $"(the processes could not be listed: {error.Message})";
        }
    }

    private static void Writable(string path)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                FileAttributes attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public static class Ui
{
    /// <summary>A key, or a chord ("control M , control S"), pressed as a person presses it, then what it ran let land: a
    /// chord's second key comes right after its first, as a person's does within the two seconds the window waits for
    /// it, never after background work that may take longer.</summary>
    public static void Press(TopLevel window, string keys)
    {
        Shortcut shortcut = Passenger.Editor.Shortcut.Parse(keys.Replace('+', ' '));
        Stroke(window, shortcut.First);
        if (shortcut.Second is { } second)
        {
            Stroke(window, second);
        }
        Settle();
    }

    private static void Stroke(TopLevel window, Keystroke stroke)
    {
        RawInputModifiers modifiers = Raw(stroke.Modifiers);
        bool shown = window.IsVisible;
        window.KeyPress(stroke.Key, modifiers, PhysicalKey.None, null);
        // A press that closed its window (a dialog's Enter, done at once) leaves the release nowhere to go.
        if (!shown || window.IsVisible)
        {
            window.KeyRelease(stroke.Key, modifiers, PhysicalKey.None, null);
        }
    }

    /// <summary>A key that types, pressed as a keyboard presses it: the key, carrying the character the layout gives it
    /// ("ł" for a Polish AltGr+L, which Windows sends as Ctrl+Alt+L), then that character as text unless the key was taken
    /// as a shortcut, which keeps the platform from typing it.</summary>
    public static void Type(TopLevel window, string keys, string text)
    {
        Keystroke stroke = Keystroke.Parse(keys.Replace('+', ' '));
        RawInputModifiers modifiers = Raw(stroke.Modifiers);
        bool taken = false;
        EventHandler<KeyEventArgs> watch = (_, e) => taken = e.Handled;
        window.AddHandler(InputElement.KeyDownEvent, watch, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        window.KeyPress(stroke.Key, modifiers, PhysicalKey.None, text);
        window.RemoveHandler(InputElement.KeyDownEvent, watch);
        if (!taken)
        {
            window.KeyTextInput(text);
        }
        window.KeyRelease(stroke.Key, modifiers, PhysicalKey.None, text);
        Settle();
    }

    /// <summary>Long enough for the language server to start and load a small project, on a developer's machine.</summary>
    private static readonly TimeSpan SettleLimit = TimeSpan.FromSeconds(90);

    /// <summary>The dispatcher's own promotion of the timers that have come due to queued ticks, which a running frame
    /// does as its platform timer fires and which jobs run outside a frame never do.</summary>
    private static readonly Action<Dispatcher> PromoteTimers = typeof(Dispatcher).GetMethod("PromoteTimers", BindingFlags.Instance | BindingFlags.NonPublic) is { } promote
        ? promote.CreateDelegate<Action<Dispatcher>>()
        : throw new MissingMethodException("Avalonia's Dispatcher no longer has PromoteTimers, by which a settle fires the timers come due");

    /// <summary>Runs queued UI work and waits until the IDE is quiet: its background work landed (<see cref="BackgroundWork"/>),
    /// nothing in flight that no frame waits for (<see cref="Passenger.Core.InFlight"/>: a language server's request
    /// unanswered), every dispatcher timer that has come due fired, and the frame its last work laid out drawn
    /// (<see cref="Drawn"/>), so a look at bounds or a click finds what that work showed. Fails naming what was still busy,
    /// and since when.</summary>
    public static void Settle()
    {
        TimeSpan limit = SettleLimit.Scaled();
        if (!Settled(DateTime.UtcNow + limit))
        {
            throw new TimeoutException($"The IDE was not quiet within {limit.TotalSeconds} s. Still busy:\n{Busy()}{IdeLogTail()}{ServerLogTail()}");
        }
    }

    /// <summary>What a blink of the caret allocates on the UI thread with the IDE at rest, the least of three: its frame of
    /// the editor, about 25 KB, which a wait draws whatever the IDE did whenever it outlasts the blink's 530 ms, so a measure
    /// of what a key costs takes out the blinks its wait drew (a slower server's longer wait draws more of them).</summary>
    public static long BlinkBytes(CodeEditor editor)
    {
        Settle();
        TimeSpan limit = SettleLimit.Scaled();
        long least = long.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            long blinks = editor.Blinks;
            long before = GC.GetAllocatedBytesForCurrentThread();
            if (!Settled(DateTime.UtcNow + limit, () => editor.Blinks > blinks))
            {
                throw new TimeoutException($"The caret did not blink with the IDE at rest within {limit.TotalSeconds} s (it blinks only in a focused editor). Still busy:\n{Busy()}");
            }
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return least;
    }

    private static bool Settled(DateTime limit, Func<bool>? also = null) => Waiting.Until(() =>
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
        PromoteTimers(Dispatcher.UIThread);
        Dispatcher.UIThread.RunJobs();
        if (BackgroundWork.Pending != 0 || Passenger.Core.InFlight.Count != 0)
        {
            return false;
        }
        Draw();
        return BackgroundWork.Pending == 0 && Passenger.Core.InFlight.Count == 0 && (also is null || also());
    }, limit - DateTime.UtcNow, every: TimeSpan.FromMilliseconds(2), scaled: false);

    /// <summary>Runs what the window has queued (its layout among it) and draws until what that laid out is drawn
    /// (<see cref="Drawn"/>): at most three frames, since an animation always has a next one, and three always do.</summary>
    private static void Draw()
    {
        Dispatcher.UIThread.RunJobs();
        for (int frame = 0; frame < 3 && !Drawn(); frame++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Whether Avalonia has laid out and drawn every window as its last work left it: no render or layout pass
    /// queued, no frame held back until the one before it was drawn, and none handed to the compositor but not yet drawn.
    /// Hit testing reads the last frame drawn, so until then a click lands on what the window showed before.</summary>
    private static bool Drawn()
    {
        object media = Passenger.Inspector.AvaloniaHooks.MediaContext();
        return NextRender.GetValue(media) is null
            && ((IReadOnlyCollection<Avalonia.Rendering.Composition.Compositor>)RequestedCommits.GetValue(media)!).Count == 0
            && ((System.Collections.ICollection)PendingBatches.GetValue(media)!).Count == 0;
    }

    private static readonly FieldInfo NextRender = Passenger.Inspector.AvaloniaHooks.MediaContextField("_nextRenderOp");

    private static readonly FieldInfo RequestedCommits = Passenger.Inspector.AvaloniaHooks.MediaContextField("_requestedCommits");

    private static readonly FieldInfo PendingBatches = Passenger.Inspector.AvaloniaHooks.MediaContextField("_pendingCompositionBatches");

    /// <summary>What the IDE was busy with as a wait gave up: each hold of background work by where it was taken, and each
    /// piece of work in flight, with how long it had been under way.</summary>
    public static string Busy()
    {
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<(string What, DateTime Since)> held = BackgroundWork.Held();
        IReadOnlyList<(string What, DateTime Since)> flying = Passenger.Core.InFlight.Now();
        int unnamed = BackgroundWork.Pending - held.Count;
        string busy = Passenger.Core.InFlight.Describe(held.Concat(flying).OrderBy(b => b.Since), now);
        return (busy.Length == 0 && unnamed <= 0 ? "  nothing named" : busy)
            + (unnamed > 0 ? $"\n  {unnamed} hold{(unnamed == 1 ? "" : "s")} of background work with no name" : string.Empty);
    }

    /// <summary>The end of the IDE's own log, which names an exception that left background work waiting for good.</summary>
    private static string IdeLogTail()
    {
        string[] lines = IdeLog().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? string.Empty : $"\nThe IDE's log ends:\n{string.Join('\n', lines.TakeLast(25))}";
    }

    /// <summary>The end of the newest language server's log, which says where a project load that never ended stopped. The
    /// server writes it line by line, so unlike the IDE's own log there is no queue to flush first. Newest by when it was
    /// begun: a Windows directory listing dates a file still open by its last close, so a server still running looks older
    /// than the one shut down for it.</summary>
    private static string ServerLogTail()
    {
        DirectoryInfo logs = new(Passenger.App.CSharp.CSharpService.LogFolder);
        if (!logs.Exists || logs.EnumerateFiles("server-*.log").OrderByDescending(f => f.CreationTimeUtc).FirstOrDefault() is not { } newest)
        {
            return string.Empty;
        }
        string[] lines = Shared(newest.FullName).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string begun = newest.CreationTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
        return $"\nThe newest language server's log ({newest.Name}, begun {begun}) {(lines.Length == 0 ? "is empty" : "ends:")}\n{string.Join('\n', lines.TakeLast(25))}";
    }

    /// <summary>Waits for something to become true, running the dispatcher's loop (and so its timers)
    /// meanwhile, and ends with the window laid out and drawn as it then stands; fails naming what never happened. What it waits for may hold background work all along (a build, a test
    /// run), which it then waits out for as long as it waits, not for <see cref="Settle"/>'s own limit alone.</summary>
    public static void WaitFor(Func<bool> condition, string what, TimeSpan? limit = null) => WaitFor(condition, () => what, limit);

    /// <summary><see cref="WaitFor(Func{bool}, string, TimeSpan?)"/> whose words are made as it gives up, so what it says
    /// of the window (its status, a model's state) is what stood then, not how the wait began.</summary>
    public static void WaitFor(Func<bool> condition, Func<string> what, TimeSpan? limit = null)
    {
        TimeSpan within = (limit ?? SettleLimit).Scaled();
        DateTime until = DateTime.UtcNow + within;
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException($"Waited {within.TotalSeconds} s for {what()}");
            }
            Frame();
            DateTime settling = DateTime.UtcNow + SettleLimit.Scaled();
            if (!Settled(settling > until ? settling : until))
            {
                throw new TimeoutException($"The IDE was not quiet while waiting {within.TotalSeconds} s for {what()}. Still busy:\n{Busy()}{IdeLogTail()}{ServerLogTail()}");
            }
        }
        // What it waited for may have landed with none of the window's jobs run since (a read that ended at once).
        Draw();
    }

    /// <summary>Waits for a task the window started (a run, a debug session's start) as <see cref="WaitFor"/> waits, and
    /// gives its answer.</summary>
    public static T Await<T>(Task<T> task, string what, TimeSpan? limit = null)
    {
        WaitFor(() => task.IsCompleted, what, limit);
        return task.GetAwaiter().GetResult();
    }

    /// <inheritdoc cref="Await{T}(Task{T}, string, TimeSpan?)"/>
    public static void Await(Task task, string what, TimeSpan? limit = null)
    {
        WaitFor(() => task.IsCompleted, what, limit);
        task.GetAwaiter().GetResult();
    }

    /// <summary>Waits for a launch the window started (Run, Debug, Profile, a test run) as <see cref="Await"/> does, and
    /// fails unless it started, with its refusal and the build's own errors (<see cref="BuildErrors"/>).</summary>
    public static void Launched(Passenger.App.MainWindow window, Task<string?> launch, string what, TimeSpan? limit = null)
    {
        if (Await(launch, what, limit) is { } refused)
        {
            throw new Xunit.Sdk.XunitException($"Waiting for {what}, the window refused: {refused}{BuildErrors(window)}");
        }
    }

    /// <summary>The status bar's words and the last build's own errors after them, for a failure message: of a build that
    /// failed the status bar says only how many errors it had, and CI's runs keep only what the message says.</summary>
    public static string Said(Passenger.App.MainWindow window) => $"{window.Status}{BuildErrors(window)}";

    /// <summary>The last build's errors as MSBuild wrote them (place, code, text, a long one's first lines), each on a line
    /// of its own after a line break; when the build failed with none read, the Build window's last lines; empty when it
    /// had none.</summary>
    public static string BuildErrors(Passenger.App.MainWindow window)
    {
        Passenger.App.Run.Problem[] errors = [.. window.BottomPanel.Problems.Problems.Where(p => p.Severity == Passenger.App.Run.ProblemSeverity.Error)];
        if (errors.Length > 0)
        {
            return "\nThe build's errors:\n" + string.Join('\n', errors.Select(p =>
                $"{p.Path ?? p.Project}({p.Position.Line + 1},{p.Position.Column + 1}): error {p.Code}: {string.Join("\n    ", p.Message.Split('\n').Take(4))}"));
        }
        if (window.Status?.Contains("failed", StringComparison.OrdinalIgnoreCase) != true)
        {
            return string.Empty;
        }
        string[] lines = window.BottomPanel.Build.Document.Snapshot.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length == 0 ? string.Empty : $"\nThe Build window ends:\n{string.Join('\n', lines.TakeLast(30))}";
    }

    /// <summary>Lets the window's background work land, as a screenshot does, then finds what it shows, with no wait of
    /// the test's own: what that work brings (Git's answers, a search's) must be work the window counts.</summary>
    public static void Landed(Func<bool> shown, string what)
    {
        Settle();
        Assert.True(shown(), $"Once its background work had landed the window still lacked {what}");
    }

    /// <summary>Waits for something to become true while background work is still under way (a git made slow, an answer
    /// held back), running the dispatcher's loop but never waiting for that work to land as <see cref="WaitFor"/> does.</summary>
    public static void PumpUntil(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        TimeSpan within = (limit ?? TimeSpan.FromSeconds(90)).Scaled();
        if (!Pumped(condition, within, scaled: false))
        {
            throw new TimeoutException($"Waited {within.TotalSeconds} s for {what}");
        }
    }

    /// <summary>Whether something became true within the limit (scaled unless said), the dispatcher's loop running as
    /// <see cref="PumpUntil"/> runs it, for a caller that goes on either way.</summary>
    public static bool Pumped(Func<bool> condition, TimeSpan limit, bool scaled = true)
    {
        DateTime until = DateTime.UtcNow + (scaled ? limit.Scaled() : limit);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                return false;
            }
            Frame();
        }
        return true;
    }

    /// <summary>One frame of the dispatcher's loop, its timers firing, as a wait runs it between looks.</summary>
    private static void Frame()
    {
#pragma warning disable RS0030 // The one frame between looks at a condition.
        Passenger.App.Screenshot.Pump(TimeSpan.FromMilliseconds(20));
#pragma warning restore RS0030
    }

    /// <summary>Time that must pass for what a test checks, the window running all along as on a desktop: a debounce it
    /// measures, a moment in which nothing is to happen. Never a wait for something to happen, which is
    /// <see cref="WaitFor"/>'s or <see cref="PumpUntil"/>'s; nor scaled, being no limit. <paramref name="why"/> says what
    /// the time is for, as the test's reader needs to know.</summary>
    public static void Hold(TimeSpan time, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
#pragma warning disable RS0030 // Time a test needs the window to run for, named.
        Passenger.App.Screenshot.Pump(time);
#pragma warning restore RS0030
    }

    /// <summary>The window's thread kept busy for a while, running nothing, as a heavy frame keeps it: for a test of what
    /// the IDE does while its window falls behind.</summary>
    public static void Stall(TimeSpan time, string why) => Waiting.For(time, why);

    /// <summary>A click as a person's mouse makes it, pressed and let go at the control's centre (or a point in it) in the
    /// window or popup showing it, once that is drawn; fails unless the control is shown, enabled and what the pointer finds
    /// there, since a click it would miss does nothing for a person. Then lets what it ran land, unless told not to (a
    /// click during work that holds background work, which the test pumps through itself).</summary>
    public static void Click(Control control, Point? at = null, RawInputModifiers modifiers = RawInputModifiers.None, bool settle = true) =>
        Click(control, at, MouseButton.Left, modifiers, settle);

    /// <summary>A right click as <see cref="Click(Control, Point?, RawInputModifiers, bool)"/> makes a click, which opens
    /// the menu of what is there.</summary>
    public static void RightClick(Control control, Point? at = null) => Click(control, at, MouseButton.Right, RawInputModifiers.None, settle: true);

    /// <summary>Two clicks in quick succession at the same place, as a person opens what a row holds.</summary>
    public static void DoubleClick(Control control, Point? at = null)
    {
        (TopLevel top, Point point) = Aim(control, at);
        for (int i = 0; i < 2; i++)
        {
            top.MouseDown(point, MouseButton.Left);
            top.MouseUp(point, MouseButton.Left);
        }
        Settle();
    }

    /// <summary>A drag with the left button from one point of a window to another (its units), through ten moves between,
    /// as a person's hand goes.</summary>
    public static void Drag(TopLevel window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(from + ((to - from) * (step / 10.0)), RawInputModifiers.LeftMouseButton);
        }
        window.MouseUp(to, MouseButton.Left);
        Settle();
    }

    private static void Click(Control control, Point? at, MouseButton button, RawInputModifiers modifiers, bool settle)
    {
        (TopLevel top, Point point) = Aim(control, at);
        top.MouseDown(point, button, modifiers);
        top.MouseUp(point, button, modifiers);
        if (settle)
        {
            Settle();
        }
    }

    /// <summary>Where a click on a control lands, in the window or popup showing it, scrolled into view, the tooltips the pointer
    /// leaves shut, and drawn (hit testing reads the last frame drawn); fails unless the control is shown, enabled and inside
    /// it, and the pointer finds it there.</summary>
    private static (TopLevel Top, Point Point) Aim(Control control, Point? at)
    {
        // A person scrolls to what they click; one already in view stays where it is.
        control.BringIntoView();
        string name = Named(control);
        TopLevel top = TopLevel.GetTopLevel(control) ?? throw new Xunit.Sdk.XunitException($"{name} is in no window, so a click cannot reach it");
        ShutToolTipsLeft(top, control);
        Draw();
        Assert.True(control.IsEffectivelyVisible, $"{name} is hidden, so a click cannot reach it");
        Assert.True(control.IsEffectivelyEnabled, $"{name} is disabled, so a click on it does nothing");
        Point point = control.TranslatePoint(at ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top)
            ?? throw new Xunit.Sdk.XunitException($"{name} is not laid out in its window");
        Assert.True(new Rect(top.ClientSize).Contains(point), $"{name} is outside its window ({point} in {top.ClientSize}), so a click cannot reach it");
        IInputElement? found = top.InputHitTest(point);
        if (found?.GetType().Name == "LightDismissOverlayLayer")
        {
            // An open menu's layer takes the click to close it, then passes it on to what is under it.
            found = top.InputHitTest(point, v => v.GetType().Name != "LightDismissOverlayLayer");
        }
        Assert.True(found is Visual visual && (visual == control || control.IsVisualAncestorOf(visual)),
            $"A click on {name} lands on {(found is Control other ? Covering(other) : found?.ToString() ?? "nothing")} instead, which covers it there ({point} in {top.ClientSize})");
        return (top, point);
    }

    /// <summary>Shuts each tooltip of what the pointer leaves for a click elsewhere, as a person's pointer moving there does:
    /// the rig's pointer jumps to a click without moving, so a tooltip the window opened under it as it redrew could cover
    /// what is clicked next.</summary>
    private static void ShutToolTipsLeft(TopLevel top, Control control)
    {
        foreach (ToolTip tip in top.GetVisualDescendants().OfType<ToolTip>().ToList())
        {
            if (tip.Parent is Avalonia.Controls.Primitives.Popup { PlacementTarget: Control owner } && owner != control && !owner.IsVisualAncestorOf(control))
            {
                ToolTip.SetIsOpen(owner, false);
            }
        }
    }

    /// <summary>What a control is, whose template part it is when it is one and what holds it, so a cover is known by what
    /// drew it.</summary>
    private static string Covering(Control control) => control.TemplatedParent is Control owner ? $"{Named(control)} of {Covering(owner)}"
        : string.Join(" in ", control.GetVisualAncestors().OfType<Control>().Take(6).Prepend(control).Select(Named));

    /// <summary>A person's window shown on a folder, once the folder's opening work has landed; without the C# language
    /// server for a test that asks nothing of C# (<see cref="Passenger.App.MainWindow.StartsLanguageServer"/>), which then
    /// waits for no server's start, load or restore; saving only when asked for a test that reads what it left unsaved.</summary>
    public static Passenger.App.MainWindow OpenFolder(string folder, double width = 1200, double height = 800, bool languageServer = true, bool autosaves = true)
    {
        Passenger.App.MainWindow window = new() { Width = width, Height = height, StartsLanguageServer = languageServer, Autosaves = autosaves };
        window.Show();
        window.OpenFolder(folder);
        Settle();
        return window;
    }

    /// <summary>The caret put just after the first place some text is in an editor, failing when the text is not there.</summary>
    public static void PlaceAfter(CodeEditor editor, string text)
    {
        int at = editor.Document.Snapshot.ToString().IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' is not in the file");
        editor.Selections = SelectionSet.Single(at + text.Length);
    }

    /// <summary>A line typed into a console (Run's, Debug's) as a person types it there, a click in it first taking the keys:
    /// the console sends it to its program's input.</summary>
    public static void TypeLine(Passenger.App.ToolWindows.OutputView console, string line)
    {
        Click(console.Editor, settle: false);
        TopLevel top = TopLevel.GetTopLevel(console)!;
        top.KeyTextInput(line);
        Press(top, "ENTER");
    }

    /// <summary>Clicks the button (or toggle) showing these words among a control's descendants, failing naming the words
    /// the buttons there show when none shows them.</summary>
    public static void ClickButton(Visual within, string words)
    {
        Button[] buttons = [.. within.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible)];
        Click(buttons.FirstOrDefault(b => b.Content as string == words)
            ?? throw new Xunit.Sdk.XunitException($"No button says {words}: [{string.Join(", ", buttons.Select(b => b.Content).OfType<string>())}]"));
    }

    /// <summary>A click on a console line's link to a file, at its first character, as a person clicks it: the pointer
    /// rested there first, failing unless that underlines the link.</summary>
    public static void ClickLink(Passenger.App.ToolWindows.OutputView console, string line)
    {
        CodeEditor editor = console.Editor;
        Passenger.App.ToolWindows.FoundLink link = console.Links.In(line).Single(l => l.Target is Passenger.App.ToolWindows.FileLink);
        int offset = editor.Document.Snapshot.ToString().IndexOf(line, StringComparison.Ordinal) + link.Start;
        editor.GoTo(editor.Document.Snapshot.GetPosition(offset));
        Settle();
        Point within = editor.PointOf(offset) + new Vector(2, editor.LineHeight / 2);
        TopLevel top = TopLevel.GetTopLevel(editor)!;
        top.MouseMove(editor.TranslatePoint(within, top)!.Value);
        Settle();
        Assert.True(editor.HoveredLink is { Target: Passenger.App.ToolWindows.FileLink }, $"The pointer rests on the line's place but nothing is underlined:\n{line}");
        Click(editor, within);
    }

    /// <summary>A line of the program Run started, typed into the Run window's console.</summary>
    public static void TypeToRun(Passenger.App.MainWindow window, string line)
    {
        window.ShowToolWindow(Passenger.App.ToolWindowIds.Run);
        Settle();
        TypeLine(window.BottomPanel.Run, line);
    }

    /// <summary>A line of the debugged program's input, typed into the Debug window's console.</summary>
    public static void TypeToDebugged(Passenger.App.MainWindow window, string line)
    {
        window.ShowToolWindow(Passenger.App.ToolWindowIds.Debug);
        window.BottomPanel.Debug.ShowTab(console: true);
        Settle();
        TypeLine(window.BottomPanel.Debug.Console, line);
    }

    /// <summary>A control as a failure message names it: by its name, words or tip.</summary>
    public static string Named(Control control) => control switch
    {
        { Name.Length: > 0 } => $"{control.GetType().Name} {control.Name}",
        Avalonia.Controls.Primitives.HeaderedSelectingItemsControl { Header: string header } => $"{control.GetType().Name} '{header}'",
        ContentControl { Content: string words } => $"{control.GetType().Name} '{words}'",
        _ when ToolTip.GetTip(control) is string tip => $"{control.GetType().Name} '{tip}'",
        _ => control.GetType().Name,
    };

    /// <summary>Opens a control's right-click menu for what it has chosen, as the menu key does, and chooses an item as
    /// <see cref="Choose"/> does.</summary>
    public static void ChooseFromMenu(Control owner, params string[] path)
    {
        ContextMenu menu = owner.ContextMenu ?? throw new InvalidOperationException($"{owner.GetType().Name} has no menu");
        owner.RaiseEvent(new ContextRequestedEventArgs());
        Settle();
        Choose(menu, path);
    }

    /// <summary>Opens the shown editor's menu at its caret, as the menu key does, and chooses an item as
    /// <see cref="Choose"/> does.</summary>
    public static void ChooseFromEditorMenu(Passenger.App.MainWindow window, params string[] path)
    {
        window.EditorArea.CodeEditor.RequestContextMenu();
        Settle();
        Choose(window.OpenContextMenu ?? throw new Xunit.Sdk.XunitException("The editor's menu did not open"), path);
    }

    /// <summary>Right-clicks a row of the Explorer and chooses an item of the menu it opens as <see cref="Choose"/> does,
    /// failing unless the right click chose that row, so the menu is the row's own.</summary>
    public static void ChooseFromRowMenu(Passenger.App.MainWindow window, Passenger.App.Explorer.ExplorerNode row, params string[] path)
    {
        RightClick(Row(window.ExplorerView, row));
        Assert.True(window.ExplorerView.SelectedNodes.Contains(row),
            $"A right-click on {row.Name} left [{string.Join(", ", window.ExplorerView.SelectedNodes.Select(n => n.Name))}] chosen, so its menu is another row's");
        Choose(window.ExplorerView.OpenMenu ?? throw new Xunit.Sdk.XunitException($"A right-click on {row.Name} opened no menu"), path);
    }

    /// <summary>Clicks a row of the Explorer, as a person chooses one before pressing a key or dragging it, failing unless
    /// that row alone is then chosen.</summary>
    public static void ClickRow(Passenger.App.MainWindow window, Passenger.App.Explorer.ExplorerNode row)
    {
        Click(Row(window.ExplorerView, row));
        Assert.True(window.ExplorerView.SelectedNodes.SequenceEqual([row]),
            $"A click on {row.Name} left [{string.Join(", ", window.ExplorerView.SelectedNodes.Select(n => n.Name))}] chosen");
    }

    /// <summary>The centre of a control, in another's coordinates (its window's, for a drag's points).</summary>
    public static Point Centre(Control control, Visual within) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), within)
        ?? throw new Xunit.Sdk.XunitException($"{Named(control)} is not laid out in {within.GetType().Name}");

    /// <summary>A row of the Explorer as it shows it, scrolled into view.</summary>
    public static Control Row(Passenger.App.Explorer.ExplorerView explorer, Passenger.App.Explorer.ExplorerNode row)
    {
        ListBox rows = explorer.Rows();
        rows.ScrollIntoView(row);
        Settle();
        return rows.ContainerFromItem(row) ?? throw new Xunit.Sdk.XunitException($"The Explorer does not show {row.Name}");
    }

    /// <summary>A row of a list as it shows it, scrolled into view.</summary>
    public static Control Row(ListBox list, object row)
    {
        list.ScrollIntoView(row);
        Settle();
        return list.ContainerFromItem(row) ?? throw new Xunit.Sdk.XunitException($"The list does not show {row}");
    }

    /// <summary>Chooses an item of an open menu, through the submenus named before it ("Add", "Docker Support"), refusing
    /// (InvalidOperationException) naming what the menu has when it lacks one or has it disabled. The item is clicked as a person's is; then the menu
    /// is closed, should the item have left it open, and what it ran is let land.</summary>
    public static void Choose(ContextMenu? menu, params string[] path)
    {
        ChooseIn(menu?.Items, path);
        menu!.Close();
        Settle();
    }

    /// <inheritdoc cref="Choose(ContextMenu?, string[])"/>
    public static void Choose(MenuFlyout? menu, params string[] path)
    {
        ChooseIn(menu?.Items, path);
        menu!.Hide();
        Settle();
    }

    private static void ChooseIn(System.Collections.IEnumerable? shown, string[] path)
    {
        ArgumentOutOfRangeException.ThrowIfZero(path.Length);
        if (shown is null)
        {
            throw new Xunit.Sdk.XunitException($"No menu is open to choose {string.Join(" > ", path)} from");
        }
        IEnumerable<MenuItem> items = shown.OfType<MenuItem>();
        string where = "The menu";
        for (int i = 0; i < path.Length; i++)
        {
            string step = path[i];
            MenuItem item = items.FirstOrDefault(m => (string?)m.Header == step)
                ?? throw new InvalidOperationException($"{where} has no {step}: [{string.Join(", ", items.Select(m => $"{m.Header}{(m.IsEnabled ? "" : " (disabled)")}"))}]");
            if (!item.IsEffectivelyEnabled)
            {
                throw new InvalidOperationException($"{where} has {step} disabled: {ToolTip.GetTip(item)}");
            }
            if (i == path.Length - 1)
            {
                Click(item);
                break;
            }
            if (!item.IsSubMenuOpen)
            {
                Click(item);
            }
            Assert.True(item.IsSubMenuOpen, $"A click on {step} left its submenu closed");
            items = item.Items.OfType<MenuItem>();
            where = step;
        }
    }

    /// <summary>A submenu of the menu bar as the platform opens it: asked to bring its items up to date first.</summary>
    public static NativeMenu Submenu(NativeMenu parent, string title)
    {
        NativeMenu menu = parent.Items.OfType<NativeMenuItem>().First(i => i.Header == title).Menu!;
        Type bridge = menu.GetType().GetInterfaces().First(i => i.GetMethod("RaiseNeedsUpdate") is not null);
        bridge.GetMethod("RaiseNeedsUpdate")!.Invoke(menu, null);
        return menu;
    }

    /// <summary>The clipboard's text, read as a paste would read it.</summary>
    public static string? Clipboard(TopLevel window) =>
        Await(Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(window.Clipboard!), "the clipboard's text");

    /// <summary>Files dragged from the system's file manager onto the middle of a control and dropped there, as the
    /// platform delivers such a drag (an enter, a move, the drop).</summary>
    public static void Drop(TopLevel window, Control onto, params string[] paths)
    {
        DataTransfer data = new();
        foreach (string path in paths)
        {
            // The items a platform's drag carries, made by the window's own storage provider.
            Avalonia.Platform.Storage.IStorageItem? item = Directory.Exists(path)
                ? Await(window.StorageProvider.TryGetFolderFromPathAsync(new Uri(path)), $"the folder {path}")
                : Await(window.StorageProvider.TryGetFileFromPathAsync(new Uri(path)), $"the file {path}");
            data.Add(DataTransferItem.CreateFile(item ?? throw new InvalidOperationException($"The window's storage has no {path}")));
        }
        Point at = onto.TranslatePoint(new Point(onto.Bounds.Width / 2, onto.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The control is not in the window.");
        window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
        window.DragDrop(at, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Copy, RawInputModifiers.None);
        Settle();
    }

    /// <summary>The IDE's log as a person opening it would find it: what was queued for it written first, and read sharing
    /// the file with the log's own thread, which may be writing to it (Windows refuses a reader that does not share).</summary>
    public static string IdeLog()
    {
        Passenger.App.IdeErrors.Flush();
        return Shared(Passenger.App.IdeErrors.LogPath);
    }

    /// <summary>A file's text read alongside a writer that has it open, or nothing when there is no such file (yet, or
    /// any longer: a log is moved aside as it rotates).</summary>
    public static string Shared(string path)
    {
        try
        {
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(file);
            return reader.ReadToEnd();
        }
        catch (Exception missing) when (missing is FileNotFoundException or DirectoryNotFoundException)
        {
            return string.Empty;
        }
    }

    /// <summary>A pixel of the window as the compositor last drew it, at a point in its coordinates: what a person sees,
    /// which a fresh render of every control would not show if one of them was never asked to draw again.</summary>
    public static Avalonia.Media.Color Pixel(TopLevel window, Point at)
    {
        using Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The window has drawn no frame.");
        byte[] pixel = new byte[4];
        System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(pixel, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            PixelPoint point = PixelPoint.FromPoint(at, window.RenderScaling);
            frame.CopyPixels(new PixelRect(point.X, point.Y, 1, 1), pinned.AddrOfPinnedObject(), 4, 4);
        }
        finally
        {
            pinned.Free();
        }
        return frame.Format == Avalonia.Platform.PixelFormat.Rgba8888
            ? Avalonia.Media.Color.FromRgb(pixel[0], pixel[1], pixel[2])
            : Avalonia.Media.Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    /// <summary>Fails unless an editor's gutter and text, as the compositor last drew them, each show more than a ground: an
    /// editor drawing no rows leaves both a colour or two, where line numbers and code bring the shades of their glyphs.</summary>
    public static void DrawsText(TopLevel window, CodeEditor editor, string after)
    {
        Settle();
        using Avalonia.Media.Imaging.WriteableBitmap frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("The window has drawn no frame.");
        double textLeft = editor.PointOf(0).X + editor.ScrollOffset.X;
        int gutter = Colours(new Rect(0, 0, textLeft - 2, editor.Bounds.Height));
        // Short of the scroll bar's error stripe and the problem counts over it.
        int text = Colours(new Rect(textLeft, 0, Math.Max(1, editor.Bounds.Width - textLeft - 80), editor.Bounds.Height));
        Assert.True(gutter > 4 && text > 4,
            $"After {after} the editor drew no rows ({gutter} colours in its gutter, {text} in its text), scrolled {editor.ScrollOffset.Y / editor.LineHeight:0.#} rows down a text of {editor.RowsShown}");

        int Colours(Rect area)
        {
            Point from = editor.TranslatePoint(area.TopLeft, window)!.Value;
            PixelRect pixels = new(PixelPoint.FromPoint(from, window.RenderScaling), PixelSize.FromSize(area.Size, window.RenderScaling));
            byte[] bytes = new byte[pixels.Width * pixels.Height * 4];
            System.Runtime.InteropServices.GCHandle pinned = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                frame.CopyPixels(pixels, pinned.AddrOfPinnedObject(), bytes.Length, pixels.Width * 4);
            }
            finally
            {
                pinned.Free();
            }
            HashSet<int> seen = [];
            for (int i = 0; i < bytes.Length; i += 4)
            {
                seen.Add(bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16));
            }
            return seen.Count;
        }
    }

    /// <summary>Presses the platform's first shortcut for an IDE action, both keystrokes of a chord.</summary>
    public static void Run(TopLevel window, string action)
    {
        Shortcut shortcut = Passenger.App.AppActions.Keymap().ShortcutsFor(action)[0];
        Press(window, shortcut.Notation());
    }

    /// <summary>The first shortcut the platform's keymap gives an IDE action, in keymap notation.</summary>
    public static string Shortcut(string action) => Passenger.App.AppActions.Keymap().ShortcutsFor(action)[0].First.Notation();

    public static RawInputModifiers Raw(KeyModifiers modifiers)
    {
        RawInputModifiers raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift))
        {
            raw |= RawInputModifiers.Shift;
        }
        if (modifiers.HasFlag(KeyModifiers.Control))
        {
            raw |= RawInputModifiers.Control;
        }
        if (modifiers.HasFlag(KeyModifiers.Alt))
        {
            raw |= RawInputModifiers.Alt;
        }
        if (modifiers.HasFlag(KeyModifiers.Meta))
        {
            raw |= RawInputModifiers.Meta;
        }
        return raw;
    }
}

/// <summary>The project files tests write, as short as dotnet new's: each targets <see cref="Framework"/>, so moving the tests
/// to the next .NET changes one line.</summary>
public static class TestProjects
{
    /// <summary>The framework every project a test writes targets, the newest SDK's runtime that the tests' machines have.</summary>
    public const string Framework = "net10.0";

    /// <summary>A class library, with the properties given ("&lt;Nullable&gt;enable&lt;/Nullable&gt;") after its framework.</summary>
    public static string Library(params string[] properties) => Project("Microsoft.NET.Sdk", exe: false, properties);

    /// <summary>A console app, with the properties given after its framework.</summary>
    public static string Console(params string[] properties) => Project("Microsoft.NET.Sdk", exe: true, properties);

    /// <summary>An ASP.NET Core app, with the properties given after its framework.</summary>
    public static string Web(params string[] properties) => Project("Microsoft.NET.Sdk.Web", exe: false, properties);

    /// <summary>A project file with more after its properties (item groups, targets), before its end.</summary>
    public static string With(this string project, string after) => project[..project.LastIndexOf("</Project>", StringComparison.Ordinal)] + after + "</Project>\n";

    private static string Project(string sdk, bool exe, string[] properties) =>
        $"<Project Sdk=\"{sdk}\">\n  <PropertyGroup>\n{(exe ? "    <OutputType>Exe</OutputType>\n" : "")}    <TargetFramework>{Framework}</TargetFramework>\n"
        + string.Concat(properties.Select(p => $"    {p}\n")) + "  </PropertyGroup>\n</Project>\n";
}

public static class ExplorerRows
{
    /// <summary>The Explorer's list of rows.</summary>
    public static ListBox Rows(this Passenger.App.Explorer.ExplorerView explorer) =>
        explorer.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.ItemsSource, explorer.Tree.Rows));
}

/// <summary>The one place a test lets time pass (Thread.Sleep and Task.Delay are banned elsewhere, BannedSymbols.txt): a wait
/// for a condition, looked at again every few milliseconds until it holds or its limit (scaled by the run's
/// <see cref="TestRun.TimeScale"/>) is out, and time that a test needs to pass, named. It never runs the window's loop:
/// <see cref="Ui"/>'s waits do, through it.</summary>
public static class Waiting
{
    private static readonly TimeSpan Often = TimeSpan.FromMilliseconds(10);

    /// <summary>Whether the condition held within the limit, on this thread.</summary>
    public static bool Until(Func<bool> condition, TimeSpan limit, TimeSpan? every = null, bool scaled = true)
    {
        DateTime until = DateTime.UtcNow + (scaled ? limit.Scaled() : limit);
        while (!condition())
        {
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
#pragma warning disable RS0030 // The one wait between looks at a condition.
            Thread.Sleep(every ?? Often);
#pragma warning restore RS0030
        }
        return true;
    }

    /// <inheritdoc cref="Until"/>
    public static Task<bool> UntilAsync(Func<bool> condition, TimeSpan limit, CancellationToken cancel = default, TimeSpan? every = null) =>
        UntilAsync(() => Task.FromResult(condition()), limit, cancel, every);

    /// <summary>Whether a condition that is itself asked for (an agent's call) held within the limit.</summary>
    public static async Task<bool> UntilAsync(Func<Task<bool>> condition, TimeSpan limit, CancellationToken cancel = default, TimeSpan? every = null)
    {
        DateTime until = DateTime.UtcNow + limit.Scaled();
        while (!await condition())
        {
            if (DateTime.UtcNow >= until)
            {
                return false;
            }
#pragma warning disable RS0030 // The one wait between looks at a condition.
            await Task.Delay(every ?? Often, cancel);
#pragma warning restore RS0030
        }
        return true;
    }

    /// <summary>Whether a task ended within the limit (scaled unless said: a time in which it must not end is no limit),
    /// however it ended; the caller's cancellation ends the wait.</summary>
    public static async Task<bool> Within(Task task, TimeSpan limit, CancellationToken cancel = default, bool scaled = true)
    {
        try
        {
            await task.WaitAsync(scaled ? limit.Scaled() : limit, cancel);
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (Exception) when (task.IsCompleted)
        {
            // Ended, by failing or being cancelled: the caller looks at how.
        }
        return true;
    }

    /// <summary>Time that must pass, this thread doing nothing meanwhile: for what a test checks over it, never for
    /// something to happen. Not scaled, being no limit.</summary>
    public static void For(TimeSpan time, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
#pragma warning disable RS0030 // Time a test needs to pass, named.
        Thread.Sleep(time);
#pragma warning restore RS0030
    }

    /// <inheritdoc cref="For"/>
    public static Task ForAsync(TimeSpan time, string why, CancellationToken cancel = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);
#pragma warning disable RS0030 // Time a test needs to pass, named.
        return Task.Delay(time, cancel);
#pragma warning restore RS0030
    }
}

/// <summary>How long a key waits for the window's thread: work posted at input's priority, as the platform's keys wait
/// for the thread between jobs, the longest wait kept until <see cref="Stop"/>. Each is posted as soon as the last is
/// answered, from a thread of its own, so a loaded machine that is slow to wake a timer or the thread pool still
/// takes as many looks as the window answers.</summary>
public sealed class InputWait : IDisposable
{
    private readonly SemaphoreSlim answered = new(0);
    private volatile bool stopped;
    private int posted;

    public InputWait()
    {
        Thread poster = new(() =>
        {
            while (!stopped)
            {
                long at = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref posted);
                Dispatcher.UIThread.Post(() => Answer(Stopwatch.GetElapsedTime(at)), DispatcherPriority.Input);
                while (!stopped && !answered.Wait(100))
                {
                }
            }
        })
        { IsBackground = true, Name = "Input wait" };
        poster.Start();
    }

    public TimeSpan Longest { get; private set; }

    public int Answered { get; private set; }

    public int Posted => Volatile.Read(ref posted);

    /// <summary>Stops measuring, on the window's thread, so a wait while it is not running never counts.</summary>
    public void Stop() => stopped = true;

    public void Dispose() => Stop();

    private void Answer(TimeSpan waited)
    {
        if (!stopped)
        {
            Answered++;
            Longest = waited > Longest ? waited : Longest;
        }
        answered.Release();
    }
}

/// <summary>A test's restore, run as the IDE runs the .NET command line (<see cref="Passenger.Core.ChildProcesses.DotnetCommand"/>):
/// what MSBuild leaves of it is the tests' own and ends with them, and no variable of dotnet test's holds its output open.</summary>
public static class Restore
{
    /// <summary>The longest a restore may take: nuget.org slow, a cache cold.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(5);

    /// <summary>The restore, which fails the test at once naming what it means and quoting what dotnet said: a project
    /// not restored (offline, nuget.org down) would show up later as C# unavailable, far from the cause.</summary>
    public static async Task<Passenger.Core.ChildResult> RunAsync(string target)
    {
        Passenger.Core.ChildResult restored;
        try
        {
            restored = await Passenger.Core.ChildProcesses.RunAsync(Passenger.Core.ChildProcesses.DotnetCommand(["restore", target]), limit: Limit.Scaled());
        }
        catch (TimeoutException hung)
        {
            throw new TimeoutException($"The test's project did not restore, so C# cannot load it: {hung.Message}", hung);
        }
        Assert.True(restored.Succeeded,
            $"The test's project did not restore, so C# cannot load it: dotnet restore {target} exited with code {restored.ExitCode}:\n{restored.Output}{restored.Error}");
        return restored;
    }

    /// <summary>The restore, waited for off the UI thread, whose continuations would otherwise wait on it.</summary>
    public static Passenger.Core.ChildResult Run(string target) => Task.Run(() => RunAsync(target)).GetAwaiter().GetResult();
}

/// <summary>A test class's folders, each made as its constructor would make it and restored at its own path, a few at a
/// time: the first test of a few waits for their restores together rather than each test for its own. Every test that
/// takes one has a fresh folder of its own, which its class removes; those no test took go with the class (a class
/// fixture).</summary>
public sealed class RestoredFolders : IDisposable
{
    /// <summary>The most made and restored at once.</summary>
    private const int Batch = 4;

    private readonly Queue<string> ready = new();

    /// <summary>The tests that have taken a folder, by id.</summary>
    private readonly HashSet<string> takers = new(StringComparer.Ordinal);

    /// <summary>A folder whose name starts with <paramref name="name"/>, written by <paramref name="write"/> (which gives what
    /// to restore in it) and restored; from a test class's constructor or its test, since those made with it for later
    /// tests are none of this test's own (<see cref="TestFolders.Unowned"/>). A batch is as many as the class has tests yet
    /// to take one (<see cref="StillToTake"/>), four at most, so a class of three tests restores three.</summary>
    public string Take(string name, Func<string, string> write)
    {
        int left = StillToTake();
        if (TestContext.Current.Test?.UniqueID is { } test)
        {
            takers.Add(test);
        }
        if (ready.Count == 0)
        {
            List<string> made = [];
            try
            {
                List<string> targets = [];
                for (int i = 0; i < Math.Min(Batch, left); i++)
                {
                    string folder = TestFolders.Unowned(name);
                    made.Add(folder);
                    targets.Add(write(folder));
                }
                Task.Run(() => Task.WhenAll(targets.Select(Restore.RunAsync))).GetAwaiter().GetResult();
            }
            catch
            {
                foreach (string folder in made)
                {
                    TestFolders.Delete(folder);
                }
                throw;
            }
            made.ForEach(ready.Enqueue);
        }
        return ready.Dequeue();
    }

    /// <summary>How many of the class's tests have yet to take a folder, this one among them: its tests (a theory's inline
    /// rows each, its other data a batch's worth) less those that took one. At least one; an overcount restores a folder
    /// no test takes, an undercount (a test taking two) only restores again later.</summary>
    private int StillToTake()
    {
        if (TestContext.Current.TestClass is not Xunit.v3.IXunitTestClass { Class: { } type })
        {
            return Batch;
        }
        int tests = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(method => method.GetCustomAttributes().Any(attribute => attribute is Xunit.v3.IFactAttribute))
            .Sum(method => method.GetCustomAttributes().OfType<Xunit.v3.IDataAttribute>().Sum(data => data is InlineDataAttribute ? 1 : Batch) is var rows and > 0 ? rows : 1);
        string? test = TestContext.Current.Test?.UniqueID;
        return Math.Max(1, tests - takers.Count(taker => taker != test));
    }

    public void Dispose()
    {
        while (ready.TryDequeue(out string? folder))
        {
            TestFolders.Delete(folder);
        }
    }
}

/// <summary>Git as the tests' fixtures run it: as the IDE runs it (<see cref="Passenger.Core.ChildProcesses"/>, so none of a
/// hook's variables points it at another repository, and with the run's empty configuration), its output read, within a
/// limit, failing the test naming the command and what git said when it fails.</summary>
public static class TestGit
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    public static string Run(string folder, params string[] arguments)
    {
        Passenger.Core.ChildResult result;
        try
        {
            result = Task.Run(() => Passenger.Core.ChildProcesses.RunAsync(Passenger.Core.ChildProcesses.Command("git", arguments, folder), limit: Limit.Scaled())).GetAwaiter().GetResult();
        }
        catch (TimeoutException hung)
        {
            throw new TimeoutException($"The test's repository is not as it was to be made: {hung.Message}", hung);
        }
        Assert.True(result.Succeeded,
            $"The test's repository is not as it was to be made: git {string.Join(' ', arguments)} in {folder} exited with code {result.ExitCode}:\n{result.Error}{result.Output}");
        return result.Output;
    }

    /// <summary>A new repository on main, with an author of its own, its files kept as written (not as Git for Windows'
    /// autocrlf would convert them) and its first commit of what the folder holds.</summary>
    public static void Init(string folder)
    {
        Run(folder, "init", "-q", "-b", "main");
        Run(folder, "config", "user.email", "tests@passenger.invalid");
        Run(folder, "config", "user.name", "Passenger Tests");
        Run(folder, "config", "core.autocrlf", "false");
        Run(folder, "add", "-A");
        Run(folder, "commit", "-q", "-m", "First");
    }
}

/// <summary>Programs the IDE started for a test, by id, as a process list would show them.</summary>
public static partial class Processes
{
    /// <summary>The id a program wrote to a file once it was running, the window's loop running meanwhile (not settling,
    /// which a build under way would not let it do).</summary>
    public static int WrittenId(string file, string what) => WrittenId([file], what);

    /// <summary>The id the first of several programs to run wrote to its file.</summary>
    public static int WrittenId(IReadOnlyList<string> files, string what)
    {
        int id = 0;
        Ui.PumpUntil(() => (id = files.Select(ReadId).FirstOrDefault(i => i > 0)) > 0, what, TimeSpan.FromMinutes(2));
        return id;
    }

    /// <summary>The id a program wrote to a file once it was running, for a test with no window of its own.</summary>
    public static async Task<int> WrittenIdAsync(string file, string what)
    {
        int id = 0;
        TimeSpan limit = TimeSpan.FromMinutes(2);
        return await Waiting.UntilAsync(() => (id = ReadId(file)) > 0, limit, every: TimeSpan.FromMilliseconds(100))
            ? id
            : throw new TimeoutException($"Waited {limit.Scaled().TotalSeconds} s for {what}");
    }

    /// <summary>Every process a process started, and every one those started, as the system lists them now.</summary>
    public static IReadOnlyList<int> Descendants(int id)
    {
        ILookup<int, int> children = ParentsOfAll().ToLookup(p => p.Parent, p => p.Child);
        List<int> found = [];
        Queue<int> next = new([id]);
        while (next.TryDequeue(out int parent))
        {
            foreach (int child in children[parent].Where(c => c != parent && c != id && !found.Contains(c) && StartedAfter(c, parent)))
            {
                found.Add(child);
                next.Enqueue(child);
            }
        }
        return found;

        // Windows keeps a gone parent's id in its children and gives the id to new processes: not their children.
        static bool StartedAfter(int child, int parent)
        {
            try
            {
                using System.Diagnostics.Process later = System.Diagnostics.Process.GetProcessById(child);
                using System.Diagnostics.Process earlier = System.Diagnostics.Process.GetProcessById(parent);
                return later.StartTime >= earlier.StartTime;
            }
            catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return false;
            }
        }
    }

    /// <summary>A process as a failure names it: its id and its command line (its name where the system gives none), as
    /// Core reads them.</summary>
    public static string Describe(int id) => Passenger.Core.DotnetProcesses.Describe(id) is { } process
        ? $"{id} {(process.CommandLine.Length > 0 ? process.CommandLine : process.Name)}"
        : $"{id} (gone)";

    /// <summary>Each process now by id, as ps lists it on macOS and Linux: its id, parent, nice value and command line, for a
    /// failure to say what ran and at what priority. Empty on Windows, which has no ps.</summary>
    public static IReadOnlyDictionary<int, string> Listed() =>
        OperatingSystem.IsWindows() ? new Dictionary<int, string>() : ById(Ps("-A", "-o", "pid=,ppid=,nice=,command="));

    /// <summary>Each MSBuild worker node now by id, with the MSBuild variables of its environment (its handshake's salt among
    /// them): ps shows a process's environment to its user on macOS, /proc on Linux. Empty on Windows.</summary>
    public static IReadOnlyDictionary<int, string> MSBuildNodes()
    {
        Dictionary<int, string> nodes = [];
        if (OperatingSystem.IsWindows())
        {
            return nodes;
        }
        foreach ((int id, string line) in ById(Ps(OperatingSystem.IsMacOS() ? ["-A", "-E", "-o", "pid=,command="] : ["-A", "-o", "pid=,command="])))
        {
            if (line.Contains("/nodemode:1", StringComparison.Ordinal))
            {
                nodes[id] = string.Join(' ', MSBuildVariable().Matches(OperatingSystem.IsMacOS() ? line : Environ(id)).Select(m => m.Value));
            }
        }
        return nodes;

        static string Environ(int id)
        {
            try
            {
                return File.ReadAllText($"/proc/{id}/environ").Replace('\0', ' ');
            }
            catch (Exception gone) when (gone is IOException or UnauthorizedAccessException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>The worker nodes MSBuild counts now as a build ends, and the most it keeps for the next build, one and a half
    /// a processor: the MSBuild executable's (those of MSBuild's server, which a terminal's dotnet build uses), not the
    /// .NET host's that a build Passenger starts runs in. While it counts more, a build ending keeps none of its nodes.
    /// None counted on Windows, which has no ps.</summary>
    public static (int Counted, int Most) MSBuildNodesCounted()
    {
        int counted = Listed().Values.Count(line => line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries) is [_, _, _, string program, string rest]
            && program.EndsWith("/MSBuild", StringComparison.Ordinal) && rest.Contains("/nodemode:1", StringComparison.Ordinal));
        return (counted, Math.Max(1, Environment.ProcessorCount * 3 / 2));
    }

    /// <summary>The lines ps lists for these arguments, each trimmed (macOS and Linux).</summary>
    public static string[] Ps(params string[] arguments)
    {
        using System.Diagnostics.Process ps = System.Diagnostics.Process.Start(new ProcessStartInfo("ps", arguments) { RedirectStandardOutput = true })!;
        string text = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static Dictionary<int, string> ById(IEnumerable<string> lines)
    {
        Dictionary<int, string> found = [];
        foreach (string line in lines)
        {
            if (line.Split(' ', 2) is [string first, _] && int.TryParse(first, out int id))
            {
                found[id] = line;
            }
        }
        return found;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\bMSBUILD[A-Z_]*=\S*")]
    private static partial System.Text.RegularExpressions.Regex MSBuildVariable();

    /// <summary>Each process and its parent: ps's columns on macOS and Linux, Win32_Process's on Windows, which has no ps.</summary>
    private static IEnumerable<(int Child, int Parent)> ParentsOfAll()
    {
        string[] lines;
        if (OperatingSystem.IsWindows())
        {
            System.Diagnostics.ProcessStartInfo list = new("powershell", ["-NoProfile", "-Command", "Get-CimInstance Win32_Process | ForEach-Object { \"$($_.ProcessId) $($_.ParentProcessId)\" }"])
            {
                RedirectStandardOutput = true,
            };
            using System.Diagnostics.Process listing = System.Diagnostics.Process.Start(list)!;
            lines = listing.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            listing.WaitForExit();
        }
        else
        {
            lines = Ps("-A", "-o", "pid=,ppid=");
        }
        foreach (string line in lines)
        {
            string[] words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 2 && int.TryParse(words[0], out int child) && int.TryParse(words[1], out int parent))
            {
                yield return (child, parent);
            }
        }
    }

    /// <summary>The id in a file, or 0 before there is one. The writer may still have it open, which Windows refuses a reader.</summary>
    public static int ReadId(string file)
    {
        try
        {
            return File.Exists(file) && int.TryParse(File.ReadAllText(file).Trim(), out int id) ? id : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    public static bool IsRunning(int id)
    {
        if (id <= 0)
        {
            return false;
        }
        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Whether a process has gone within a few seconds, the time a killed one may take to leave the list, or within
    /// the time given.</summary>
    public static bool Gone(int id, TimeSpan? within = null) =>
        Waiting.Until(() => !IsRunning(id), within ?? TimeSpan.FromSeconds(15), every: TimeSpan.FromMilliseconds(100));

    /// <summary>Ends what a failed test left running, its children with it.</summary>
    public static void Kill(int id)
    {
        try
        {
            if (IsRunning(id))
            {
                using System.Diagnostics.Process left = System.Diagnostics.Process.GetProcessById(id);
                left.Kill(entireProcessTree: true);
            }
        }
        catch (Exception gone) when (gone is ArgumentException or InvalidOperationException or AggregateException or System.ComponentModel.Win32Exception)
        {
            // Gone, or ending: Windows refuses to end a process of the tree that is.
        }
    }

    /// <summary>Stops a process where it stands, answering nothing, as a deadlocked one would: SIGSTOP on macOS and
    /// Linux, and on Windows every thread suspended (NtSuspendProcess), which a kill ends as surely. Signalled, not by
    /// running kill: on macOS a child ending while another is stopped sets .NET reaping in a spin that holds Process's lock.</summary>
    public static void Suspend(int id, string what)
    {
        if (OperatingSystem.IsWindows())
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(id);
            int status = NtSuspendProcess(process.Handle);
            Assert.True(status == 0, $"{what} could not be suspended (NTSTATUS 0x{status:X8})");
            return;
        }
        Assert.True(Signal(id, OperatingSystem.IsMacOS() ? 17 : 19) == 0, $"{what} could not be stopped (errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()})");
    }

    [System.Runtime.InteropServices.DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Signal(int process, int signal);
}

/// <summary>Where a process's threads wait, as macOS's sample sees them (it may read any of the user's processes): each
/// thread with a frame naming one of the markers, from the first such frame to its last. On CI, where no one can attach
/// a debugger to what hangs, this names the call that never returned. Empty on other systems.</summary>
public static partial class NativeStacks
{
    public static string Describe(string what, int processId, params string[] markers)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return string.Empty;
        }
        string file = Path.Combine(TestRun.Root, $"sample-{Guid.NewGuid():N}.txt");
        try
        {
            using System.Diagnostics.Process sample = System.Diagnostics.Process.Start(new ProcessStartInfo("/usr/bin/sample", [processId.ToString(System.Globalization.CultureInfo.InvariantCulture), "1", "-file", file])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;
            Task<string> said = sample.StandardOutput.ReadToEndAsync();
            Task<string> complained = sample.StandardError.ReadToEndAsync();
            if (!sample.WaitForExit(TimeSpan.FromMinutes(1)))
            {
                sample.Kill();
                return $"\n{what}: sample did not finish in a minute";
            }
            if (!File.Exists(file))
            {
                return $"\n{what}: sample could not look ({said.Result.Trim()} {complained.Result.Trim()})";
            }
            List<string> threads = [];
            string? thread = null;
            List<string> frames = [];
            foreach (string line in File.ReadLines(file).SkipWhile(l => !l.StartsWith("Call graph:", StringComparison.Ordinal)).Skip(1).TakeWhile(l => l.Trim().Length > 0))
            {
                if (ThreadLine().Match(line) is { Success: true } header)
                {
                    Keep();
                    thread = header.Groups[1].Value.Trim();
                }
                else if (FrameLine().Match(line) is { Success: true } frame)
                {
                    // A C++ frame's parameter list says nothing its name does not.
                    string name = frame.Groups[1].Value;
                    frames.Add($"{(name.IndexOf('(') is > 0 and int open ? name[..open] : name)} ({frame.Groups[2].Value})");
                }
            }
            Keep();
            return threads.Count == 0 ? $"\n{what}: no thread had a frame in {string.Join(" or ", markers)}" : $"\n{what}:\n{string.Join('\n', threads)}";

            void Keep()
            {
                int first = frames.FindIndex(f => markers.Any(m => f.Contains(m, StringComparison.Ordinal)));
                if (thread is not null && first >= 0)
                {
                    threads.Add($"  {thread}\n{string.Join('\n', frames.Skip(first).TakeLast(24).Select(f => $"    {f}"))}");
                }
                frames.Clear();
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"\n{what}: sample failed: {error.Message}";
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    // "    881 Thread_57752918   DispatchQueue_1: com.apple.main-thread  (serial)"
    [System.Text.RegularExpressions.GeneratedRegex(@"^\s+\d+\s+(Thread_\d+.*)$")]
    private static partial System.Text.RegularExpressions.Regex ThreadLine();

    // "    +   881 main  (in ReadsInputConsoleApp) + 184  [0x1024fa124]"
    [System.Text.RegularExpressions.GeneratedRegex(@"^[\s+!:|]*\d+\s+(.+?)\s+\(in ([^)]+)\)")]
    private static partial System.Text.RegularExpressions.Regex FrameLine();
}

/// <summary>A port of the system's choosing, kept from every other program until a program of the test's own is about to
/// listen there: bound but not listening, so a connection to it is refused as to a port nothing holds. The program waits
/// at a gate file before it binds, which <see cref="LetGo"/> opens just after freeing the port; <see cref="Hold"/> takes
/// the port back for a next run once that program has ended.</summary>
public sealed class HeldPort : IDisposable
{
    private System.Net.Sockets.Socket? held;

    public HeldPort()
    {
        held = Bound(0);
        Port = ((System.Net.IPEndPoint)held.LocalEndPoint!).Port;
    }

    public int Port { get; }

    /// <summary>Frees the port and opens the gate the program that listens there waits at.</summary>
    public void LetGo(string gate)
    {
        held?.Dispose();
        held = null;
        File.WriteAllText(gate, string.Empty);
    }

    /// <summary>Closes the gate and takes the port back, the program that listened there having ended.</summary>
    public void Hold(string gate)
    {
        File.Delete(gate);
        held ??= Bound(Port);
    }

    public void Dispose()
    {
        held?.Dispose();
        held = null;
    }

    private static System.Net.Sockets.Socket Bound(int port)
    {
        System.Net.Sockets.Socket socket = new(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        if (port != 0 && !OperatingSystem.IsWindows())
        {
            // Taken back: a connection the last program there closed may linger on it (TIME_WAIT), which holds no one's port.
            socket.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        }
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, port));
        return socket;
    }
}
