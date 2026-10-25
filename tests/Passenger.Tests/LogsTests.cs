using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Containers;

namespace Passenger.Tests;

/// <summary>The logs folder Help, Show Log opens holds what a person needs when something goes wrong: why the language
/// server could not load a project (said in the status bar too), its load's progress as it goes, each start's phases with
/// their times, and with the diagnostic settings on the protocols' traffic and every program the IDE started. The logs
/// folder is the whole run's, so what a test looks for there names its own server, adapter or folder.</summary>
public sealed class LogsTests : IDisposable
{
    private readonly string folder = TestFolders.Create("logs-");

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void A_project_naming_a_missing_sdk_says_why_in_the_status_bar_and_the_ides_log()
    {
        File.WriteAllText(Path.Combine(folder, "Bad.csproj"),
            $"<Project Sdk=\"Missing.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
        MainWindow window = new() { Width = 1400, Height = 800 };
        window.Show();
        List<string> languageShown = [];
        using IDisposable watching = window.FindControl<TextBlock>("LanguageText")!.GetObservable(TextBlock.TextProperty)
            .Subscribe(new Watcher(text => languageShown.Add(text ?? string.Empty)));
        window.OpenFolder(folder);
        window.OpenFile("Program.cs");
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to finish loading ({window.CSharpService?.State})");
        Ui.Settle();

        const string Reason = "The SDK 'Missing.Sdk' specified could not be found.";
        Assert.True(window.Status?.Contains(Reason, StringComparison.Ordinal) == true,
            $"The status bar says \"{window.Status}\" once the language server has loaded, not why Bad.csproj did not load");
        Assert.StartsWith("Error while loading Bad.csproj: ", window.Status, StringComparison.Ordinal);
        int server = window.CSharpService!.Session!.Server.ProcessId;
        string[] log = Ui.IdeLog().Split('\n');
        Assert.True(log.Any(line => line.Contains($"C# language server (process {server}) error: ", StringComparison.Ordinal) && line.Contains(Reason, StringComparison.Ordinal)),
            $"The IDE's log does not say why Bad.csproj did not load:\n{string.Join('\n', log.Where(line => line.Contains($"(process {server})", StringComparison.Ordinal)))}");
        Assert.Contains(log, line => line.Contains($"C# language server (process {server}) says: There were problems loading project Bad.csproj", StringComparison.Ordinal));
        Assert.True(languageShown.Any(t => t.Contains("Loading 1 project(s)…", StringComparison.Ordinal)),
            $"The status bar never showed the projects loading; it showed: {string.Join(" | ", languageShown.Distinct())}");
        Ui.WaitFor(() => window.FindControl<TextBlock>("LanguageText")!.Text == "C#", "the load's progress to leave the status bar");
        window.Close();
    }

    /// <summary>A file no project holds is in the server's own project (Canonical.csproj, in the temp folder), which it
    /// restores as it makes it: the status bar shows the restore of the person's project, never the server's, and the log
    /// does not take the server's for a project of theirs with unresolved dependencies.</summary>
    [AvaloniaFact]
    public void The_status_bar_shows_a_projects_restore_but_never_the_language_servers_own_project_for_loose_files()
    {
        Directory.CreateDirectory(Path.Combine(folder, "App"));
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        File.WriteAllText(Path.Combine(folder, "App", "App.csproj"),
            TestProjects.Library());
        File.WriteAllText(Path.Combine(folder, "App", "App.cs"), "class App { }\n");
        File.WriteAllText(Path.Combine(folder, "Scripts", "Loose.cs"), "class Loose { }\n");
        MainWindow window = new() { Width = 1400, Height = 800 };
        window.Show();
        List<string> languageShown = [];
        using IDisposable watching = window.FindControl<TextBlock>("LanguageText")!.GetObservable(TextBlock.TextProperty)
            .Subscribe(new Watcher(text => languageShown.Add(text ?? string.Empty)));
        window.OpenFolder(folder);
        window.OpenFile(Path.Combine("Scripts", "Loose.cs"));
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to finish loading ({window.CSharpService?.State})");
        int server = window.CSharpService!.Session!.Server.ProcessId;
        // The loose file's colours are the server's once its own project for it is restored and loaded.
        Ui.WaitFor(() => Ui.IdeLog().Contains($"(process {server}) gave an open file its colours", StringComparison.Ordinal), "the loose file's colours from the server");
        Ui.WaitFor(() => window.FindControl<TextBlock>("LanguageText")!.Text == "C#", "the server's work to leave the status bar");

        string shown = string.Join(" | ", languageShown.Distinct());
        Assert.True(languageShown.Where(t => t.Contains("Restor", StringComparison.Ordinal)).All(t => t.Contains("App.csproj", StringComparison.Ordinal)),
            $"The status bar showed a restore of a project the folder does not have, the language server's own: {shown}");
        Assert.True(languageShown.Any(t => t.Contains("Restoring App.csproj…", StringComparison.Ordinal)),
            $"The status bar never showed App.csproj, which had never been restored, being restored: {shown}");
        string[] named = [.. Ui.IdeLog().Split('\n').Where(line => line.Contains($"(process {server})", StringComparison.Ordinal) && line.Contains("Canonical.csproj", StringComparison.Ordinal))];
        Assert.True(named.Length == 0, $"The IDE's log tells of the language server's own project as of a project of the folder's:\n{string.Join('\n', named)}");
        window.Close();
    }

    /// <summary>The start's timing waits for an open file to be placed in a project without asking the server itself:
    /// the first file open being one no project holds, left in the server's Miscellaneous Files, it is never asked
    /// after again and again.</summary>
    [AvaloniaFact]
    public void Timing_the_start_never_asks_the_language_server_again_and_again_of_a_file_no_project_holds()
    {
        Directory.CreateDirectory(Path.Combine(folder, "App"));
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        File.WriteAllText(Path.Combine(folder, "App", "App.csproj"),
            TestProjects.Library());
        File.WriteAllText(Path.Combine(folder, "App", "App.cs"), "class App { }\n");
        File.WriteAllText(Path.Combine(folder, "Scripts", "Loose.cs"), "class Loose { }\n");
        Restore.Run(Path.Combine(folder, "App"));
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.ApplySettings(window.Settings with { TraceProtocols = true });
        try
        {
            window.OpenFolder(folder);
            window.OpenFile(Path.Combine("Scripts", "Loose.cs"));
            Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to finish loading ({window.CSharpService?.State})");
            int server = window.CSharpService!.Session!.Server.ProcessId;
            Ui.WaitFor(() => Ui.IdeLog().Contains($"(process {server}) gave an open file its colours", StringComparison.Ordinal), "the loose file's colours from the server");
            int Asked() => Logged("lsp.log").Split('\n').Count(line => line.Contains($"[server {server}] --> ", StringComparison.Ordinal)
                && line.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.GetProjectContexts}\"", StringComparison.Ordinal));
            int before = Asked();
            Ui.Hold(TimeSpan.FromSeconds(2), "time in which a file asked after ten times a second is asked after twenty");
            int during = Asked() - before;
            Assert.True(during <= 1, $"The language server was asked {during} times in 2 s which projects hold a file no project holds");

            // A file a project holds, opened next, is the one the start is timed by.
            window.OpenFile(Path.Combine("App", "App.cs"));
            Ui.WaitFor(() => Ui.IdeLog().Contains($"(process {server}) had App.cs in App", StringComparison.Ordinal), "the start timed by the file its project holds");
        }
        finally
        {
            window.ApplySettings(window.Settings with { TraceProtocols = false });
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Tracing_writes_the_language_servers_messages_to_lsp_log_and_every_program_to_the_ides_log_until_turned_off()
    {
        File.WriteAllText(Path.Combine(folder, "Sample.csproj"),
            TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "class Program\n{\n    static void Main()\n    {\n    }\n}\n");
        Restore.Run(folder);
        MainWindow window = new() { Width = 1200, Height = 800 };
        window.Show();
        window.ApplySettings(window.Settings with { TraceProtocols = true, LogEveryProgram = true });
        try
        {
            window.OpenFolder(folder);
            window.OpenFile("Program.cs");
            Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to load the project ({window.CSharpService?.State})");
            int server = window.CSharpService!.Session!.Server.ProcessId;
            Ui.WaitFor(() => Ui.IdeLog().Contains($"(process {server}) gave an open file its colours", StringComparison.Ordinal), "the first colours to be logged");

            string trace = Logged("lsp.log");
            Assert.True(Traced(trace, $"[server {server}] --> ", "\"method\":\"initialize\""),
                $"lsp.log in the logs folder does not have the initialize the server was sent:\n{Start(trace)}");
            Assert.True(Traced(trace, $"[server {server}] <-- ", $"\"method\":\"{Passenger.Lsp.LspMethods.ProjectInitializationComplete}\""),
                $"lsp.log does not have the server's word that the project loaded:\n{Start(trace)}");
            Assert.True(Traced(trace, $"[server {server}] --> ", $"\"method\":\"{Passenger.Lsp.LspMethods.SemanticTokensFull}\""),
                $"lsp.log does not have the open file's colours asked for:\n{Start(trace)}");

            string log = Ui.IdeLog();
            Assert.True(log.Contains($"Process {server} started", StringComparison.Ordinal) && log.Contains("Microsoft.CodeAnalysis.LanguageServer.dll --stdio", StringComparison.Ordinal),
                $"The IDE's log does not have the language server's command line:\n{log}");
            Assert.Contains($"Opened {window.Folder}", log, StringComparison.Ordinal);
            Assert.Matches($@"C# language server \(process {server}\) started for .* in \d+\.\d\d s, loading 1 project", log);
            Assert.Matches($@"C# language server \(process {server}\) loaded 1 project in \d+\.\d\d s", log);

            window.ApplySettings(window.Settings with { TraceProtocols = false });
            int traced = Logged("lsp.log").Length;
            window.KeyTextInput("// untraced");
            Ui.Settle();
            Ui.Hold(TimeSpan.FromMilliseconds(500), "long enough for what the change sent to reach lsp.log, were it traced");
            string after = Logged("lsp.log");
            Assert.True(after.Length == traced, $"lsp.log grew after the trace was turned off:\n{after[traced..]}");
        }
        finally
        {
            window.ApplySettings(window.Settings with { TraceProtocols = false, LogEveryProgram = false });
            window.Close();
        }
    }

    /// <summary>A secret given a program on its command line (an argument naming a password, a URL's credentials) reaches
    /// neither the IDE's log, with every program logged, nor the Run console's echo of the command: both say it as dots, as
    /// every secret Passenger says is, while the program is given it whole.</summary>
    [AvaloniaFact]
    public void A_secret_on_a_programs_command_line_is_masked_in_the_ides_log_and_the_run_consoles_echo()
    {
        const string Password = "lantern-7f3a";
        const string Token = "quill-9e2b";
        File.WriteAllText(Path.Combine(folder, "Given.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"),
            $"System.Console.WriteLine(args is [\"--password={Password}\", \"https://ada:{Token}@db.example/shop\"] ? \"given whole\" : \"given otherwise\");\n");
        Restore.Run(folder);
        List<string> ran = [];
        EventHandler<Passenger.Core.ChildStart> heard = (_, child) =>
        {
            lock (ran)
            {
                ran.Add(child.Command);
            }
        };
        Passenger.Core.ChildProcesses.Started += heard;
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.ApplySettings(window.Settings with { LogEveryProgram = true });
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name.StartsWith("Given", StringComparison.Ordinal)), "the project's run configuration", TimeSpan.FromMinutes(1));
            Ui.Launched(window, window.RunAsync($"--password={Password} https://ada:{Token}@db.example/shop"), "the program to start", TimeSpan.FromMinutes(2));
            Ui.WaitFor(() => window.Runner.Ended.IsCompleted, "the program to end");
            string console = window.BottomPanel.Run.Document.Snapshot.ToString();
            Assert.True(console.Contains("given whole", StringComparison.Ordinal), $"The program was not given its arguments whole:\n{console}");
            string[] given;
            lock (ran)
            {
                given = [.. ran.Where(c => c.Contains(Password, StringComparison.Ordinal))];
            }
            Assert.True(given.Length == 1 && given[0].Contains(Token, StringComparison.Ordinal), $"The program's command line did not carry what it was given: {string.Join(" | ", given)}");

            string masked = $"--password={ContainerSecrets.Masked} https://ada:{ContainerSecrets.Masked}@db.example/shop";
            Assert.True(console.Contains(masked, StringComparison.Ordinal) && !console.Contains(Password, StringComparison.Ordinal) && !console.Contains(Token, StringComparison.Ordinal),
                $"The Run console's echo of the command shows its secrets:\n{console}");
            // Its start and its end, each logged as every program is, the end told on another thread once it has gone.
            Ui.WaitFor(() => Ui.IdeLog().Split('\n').Count(l => l.Contains(masked, StringComparison.Ordinal)) >= 2, "the program's start and end to be logged");
            string log = WholeIdeLog();
            Assert.True(!log.Contains(Password, StringComparison.Ordinal) && !log.Contains(Token, StringComparison.Ordinal),
                $"The IDE's log has a program's secrets in the clear:\n{string.Join('\n', log.Split('\n').Where(l => l.Contains(Password, StringComparison.Ordinal) || l.Contains(Token, StringComparison.Ordinal)))}");
        }
        finally
        {
            Passenger.Core.ChildProcesses.Started -= heard;
            window.ApplySettings(window.Settings with { LogEveryProgram = false });
            window.Close();
        }
    }

    /// <summary>The IDE's log with the logs it rotated aside, so a line rotated away during a test is still read.</summary>
    internal static string WholeIdeLog()
    {
        IdeErrors.Flush();
        return string.Join('\n', Directory.GetFiles(IdeErrors.LogFolder, "passenger*.log").Select(Ui.Shared));
    }

    [AvaloniaFact]
    public void Tracing_writes_the_debuggers_messages_to_dap_log_beside_each_sessions_start_and_end()
    {
        File.WriteAllText(Path.Combine(folder, "Sum.csproj"),
            TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"total 6\");\n");
        Restore.Run(folder);
        // No C# language server: the debugger's trace is what is read, and a run asks nothing of the server.
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        window.ApplySettings(window.Settings with { TraceProtocols = true });
        try
        {
            window.OpenFolder(folder);
            window.OpenFile("Program.cs");
            Ui.Settle();
            Ui.Run(window, AppActions.Debug);
            Ui.WaitFor(() => window.Debugger.Session is { State: Passenger.App.Debugging.DebugState.Ended }, "the program to run to its end under the debugger");
            int adapter = window.Debugger.Session!.AdapterProcessId;

            string log = Logged("dap.log");
            Assert.True(log.Contains($"[adapter {adapter}] launch ", StringComparison.Ordinal) && log.Contains($"[adapter {adapter}] Ended debugging", StringComparison.Ordinal),
                $"dap.log in the logs folder does not have the session's start and end:\n{Start(log)}");
            Assert.True(Traced(log, $"[adapter {adapter}] --> ", "\"command\":\"launch\""), $"dap.log does not have the launch the adapter was sent:\n{Start(log)}");
            Assert.True(Traced(log, $"[adapter {adapter}] <-- ", "\"event\":\"exited\""), $"dap.log does not have the adapter's word that the program ended:\n{Start(log)}");
        }
        finally
        {
            window.ApplySettings(window.Settings with { TraceProtocols = false });
            window.Close();
        }
    }

    /// <summary>The IDE's log names the programs that failed, and no others: not git answering no (a file opened that the
    /// last commit lacks, which git show exits 128 for), nor a language server the IDE ended with its window, however long
    /// it ran and however it went (Node's 141 as the pipes it writes to close); a git command that failed is there.</summary>
    [AvaloniaFact]
    public void The_ides_log_names_programs_that_failed_not_git_answering_no_nor_servers_ended_with_their_window()
    {
        // Named for the test's folder, since the log is the whole run's.
        string untracked = $"Untracked{Path.GetFileName(folder)}";
        File.WriteAllText(Path.Combine(folder, "Logs.csproj"),
            TestProjects.Library());
        File.WriteAllText(Path.Combine(folder, "Tracked.cs"), "class Tracked { }\n");
        Restore.Run(folder);
        TestGit.Init(folder);
        File.WriteAllText(Path.Combine(folder, $"{untracked}.cs"), "class Untracked { }\n");
        File.WriteAllText(Path.Combine(folder, $"{untracked}.json"), "{ \"name\": \"logs\" }\n");
        DateTime opened = DateTime.Now;
        MainWindow window = Ui.OpenFolder(folder);
        window.OpenFile($"{untracked}.cs");
        Ui.WaitFor(() => window.CSharpService is { IsReady: true }, $"the language server to start ({window.CSharpService?.State})");
        Ui.Landed(() => window.Vcs.HeadReads > 0, "the untracked file's last committed text to be asked of git");
        // A file opened once the rest were read asks git show for its own, which has none to give.
        window.OpenFile($"{untracked}.json");
        Ui.Landed(() => window.Vcs.HeadReads > 1, "the second untracked file's last committed text to be asked of git");
        List<int> servers = [window.CSharpService!.Session!.Server.ProcessId];
        if (Passenger.Core.Programs.Node() is not null)
        {
            System.Text.RegularExpressions.Regex started = new($@"JSON language server \(process (\d+)\) started for {System.Text.RegularExpressions.Regex.Escape(window.Folder!)} ");
            Ui.WaitFor(() => started.IsMatch(Ui.IdeLog()), "the JSON language server to start on Node.js");
            servers.Add(int.Parse(started.Match(Ui.IdeLog()).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            Ui.Hold(TimeSpan.FromSeconds(1.5), "long enough to be a program a person waits on, had it been one");
        }
        Passenger.App.Vcs.VcsOutcome checkout = Ui.Await(window.Vcs.CheckoutAsync("no-such-branch"), "git to refuse the checkout");
        Assert.False(checkout.Succeeded, "git checked out a branch the repository does not have");
        window.Close();
        foreach (int server in servers)
        {
            Assert.True(Processes.Gone(server), $"The language server (process {server}) went on after its window closed");
        }
        Ui.Hold(TimeSpan.FromMilliseconds(500), "for each server's end, told on another thread once it has gone, to reach the log were it logged");

        // This window's lines alone: a server's process id was another program's in a test before, Windows reusing ids soon.
        string[] log = Since(Ui.IdeLog(), opened);
        string[] noes = [.. log.Where(line => line.Contains("exited with code", StringComparison.Ordinal) && line.Contains(" show ", StringComparison.Ordinal)
            && line.Contains($":{untracked}.", StringComparison.Ordinal))];
        Assert.True(noes.Length == 0, $"The IDE's log takes git's no, for a file the last commit lacks, for a failure:\n{string.Join('\n', noes)}");
        foreach (int server in servers)
        {
            string[] ended = [.. log.Where(line => line.Contains($"Process {server} ", StringComparison.Ordinal) && line.Contains("exited", StringComparison.Ordinal))];
            Assert.True(ended.Length == 0, $"The IDE's log tells of a language server it ended with its window as of a program that failed or kept it waiting:\n{string.Join('\n', ended)}");
        }
        Assert.True(log.Any(line => line.Contains("exited with code", StringComparison.Ordinal) && line.Contains("no-such-branch", StringComparison.Ordinal)),
            $"The IDE's log does not have the git checkout that failed:\n{string.Join('\n', log.Where(l => l.Contains("exited with code", StringComparison.Ordinal)).TakeLast(20))}");
    }

    /// <summary>The IDE's log held open by another program that does not share its deletion, as PowerShell's Get-Content
    /// -Wait holds it, which Windows then refuses to move: the IDE writes on past the limit, every rotated log kept as it
    /// was, and rotates it as the next lines come once it is let go, each rotated log moving up one.</summary>
    [AvaloniaFact]
    public void A_log_another_program_holds_open_is_written_on_with_its_rotated_logs_kept_and_rotated_once_let_go()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a log another program holds open");
        long limit = IdeErrors.LogLimit;
        IdeErrors.LogLimit = 16 * 1024;
        try
        {
            // Rotated a dozen times over, so every rotated log is there.
            Fill("before", (IdeErrors.Rotations + 1) * IdeErrors.LogLimit);
            string[] rotated = [.. Enumerable.Range(1, IdeErrors.Rotations).Select(n => Path.Combine(IdeErrors.LogFolder, $"passenger.{n}.log"))];
            Assert.All(rotated, path => Assert.True(File.Exists(path), $"{Path.GetFileName(path)} was not made by a dozen rotations"));
            string[] before = [.. rotated.Select(Ui.Shared)];

            using (new FileStream(IdeErrors.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                for (int batch = 0; batch < 20; batch++)
                {
                    Fill($"held {batch}", 4 * 1024);
                }
                for (int n = 0; n < rotated.Length; n++)
                {
                    Assert.True(Ui.Shared(rotated[n]) == before[n], $"{Path.GetFileName(rotated[n])} changed while passenger.log was held open");
                }
                Assert.True(Ui.Shared(IdeErrors.LogPath).Contains("held 19 ", StringComparison.Ordinal), "The lines written while passenger.log was held open are not in it");
            }

            Fill("let go", IdeErrors.LogLimit / 4);
            Assert.True(Ui.Shared(rotated[0]).Contains("held 19 ", StringComparison.Ordinal), "passenger.log, let go, was not rotated to passenger.1.log");
            for (int n = 1; n < rotated.Length; n++)
            {
                Assert.True(Ui.Shared(rotated[n]) == before[n - 1], $"{Path.GetFileName(rotated[n])} is not what {Path.GetFileName(rotated[n - 1])} was before the rotation");
            }
        }
        finally
        {
            // Rotated on until the log held past the limit is gone, as the rest of the run's logs are within it.
            Fill("after", (IdeErrors.Rotations + 1) * IdeErrors.LogLimit);
            IdeErrors.LogLimit = limit;
        }

        // Lines of the IDE's log, a kilobyte each, written now.
        static void Fill(string name, long bytes)
        {
            for (int line = 0; line * 1024 < bytes; line++)
            {
                IdeErrors.Write($"{name} {line} {new string('-', 980)}");
            }
            IdeErrors.Flush();
        }
    }

    /// <summary>The lines of the IDE's log written from a moment on, by the local time each entry begins with (to the
    /// millisecond, as IdeLog writes it), with the lines that go on an entry written then.</summary>
    private static string[] Since(string log, DateTime from)
    {
        DateTime after = from - TimeSpan.FromMilliseconds(1);
        List<string> since = [];
        bool taking = false;
        foreach (string line in log.Split('\n'))
        {
            if (line.Length >= 23 && DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out DateTime written))
            {
                taking = written >= after;
            }
            if (taking)
            {
                since.Add(line);
            }
        }
        return [.. since];
    }

    /// <summary>A log in the logs folder, as a person opening it would find it.</summary>
    private static string Logged(string name)
    {
        IdeErrors.Flush();
        return Ui.Shared(Path.Combine(IdeErrors.LogFolder, name));
    }

    /// <summary>Whether a trace has a line of one server and direction that holds a part of a message.</summary>
    private static bool Traced(string trace, string from, string part) =>
        trace.Split('\n').Any(line => line.Contains(from, StringComparison.Ordinal) && line.Contains(part, StringComparison.Ordinal));

    private static string Start(string text) => text.Length > 2000 ? text[..2000] : text;

    private sealed class Watcher(Action<string?> next) : IObserver<string?>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string? value) => next(value);
    }
}
