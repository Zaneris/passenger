using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A folder's Node packages through the window: a pnpm monorepo's root and packages named with their package
/// manager in the Explorer, each package's scripts run configurations that Run starts with pnpm (a stand-in beside the
/// Node.js Settings names, never npm) in the Run window and Stop ends; a script's arguments reaching its package
/// manager word for word, a Windows .cmd's too; and a Yarn package with no Yarn saying how to get it, starting nothing.
/// A test that cannot find Node.js skips, saying so.</summary>
public sealed class NodeScriptsTests
{
    private const string NoNode = "Node.js is not on the PATH, and a package's scripts run on it";

    /// <summary>A program that says it started and runs until it is ended, as a development server does.</summary>
    private const string Serving = "node -e \"console.log('serving'); setInterval(() => {}, 1000)\"";

    /// <summary>A package's scripts run in the Run window with its own package manager, their arguments word for
    /// word.</summary>
    [AvaloniaFact]
    public void A_packages_scripts_run_with_its_own_manager()
    {
        Flow.Step(nameof(A_pnpm_monorepos_packages_show_their_manager_and_each_script_runs_with_pnpm_in_the_Run_window_until_stopped),
            A_pnpm_monorepos_packages_show_their_manager_and_each_script_runs_with_pnpm_in_the_Run_window_until_stopped);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_scripts_arguments_reach_its_package_manager_word_for_word_through_cmds_operators_and_variables_in_the_Run_window_and_the_Terminal),
            A_scripts_arguments_reach_its_package_manager_word_for_word_through_cmds_operators_and_variables_in_the_Run_window_and_the_Terminal);
        OpenWindows.CloseAll();
        Flow.Step(nameof(A_run_of_several_parts_installs_with_the_packages_manager_then_starts_each_script_once_the_one_before_is_ready),
            A_run_of_several_parts_installs_with_the_packages_manager_then_starts_each_script_once_the_one_before_is_ready);
    }

    private void A_pnpm_monorepos_packages_show_their_manager_and_each_script_runs_with_pnpm_in_the_Run_window_until_stopped()
    {
        string folder = PathNames.Canonical(TestFolders.Create("node-mono-"));
        string tools = NodeBeside(folder, standIn: "pnpm");
        TestFolders.Write(folder, "package.json", """{ "name": "mono", "private": true, "scripts": { "build": "node -e 1" } }""");
        TestFolders.Write(folder, "pnpm-workspace.yaml", "packages:\n  - 'packages/*'\n");
        TestFolders.Write(folder, "pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        TestFolders.Write(folder, "packages/web/package.json", $$"""{ "name": "@mono/web", "scripts": { "predev": "node -e 1", "dev": "{{Serving.Replace("\"", "\\\"", StringComparison.Ordinal)}}" } }""");
        TestFolders.Write(folder, "packages/api/package.json", """{ "name": "@mono/api", "scripts": { "start": "node index.js", "test": "node test.js" } }""");
        List<ChildStart> started = [];
        void Heard(object? sender, ChildStart start)
        {
            lock (started)
            {
                started.Add(start);
            }
        }
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Autosaves = false };
        window.Show();
        ChildProcesses.Started += Heard;
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = Path.Combine(tools, NodeName) });
            window.OpenFolder(folder);
            string[] scripts = ["mono: build", "@mono/api: start", "@mono/api: test", "@mono/web: dev"];
            Ui.WaitFor(() => window.Configurations.Select(c => c.Name).SequenceEqual(scripts),
                () => $"each package's scripts but the hook run before dev, where the configurations are {string.Join(", ", window.Configurations.Select(c => c.Name))}");

            ExplorerTree tree = window.ExplorerView.Tree;
            Assert.Equal("mono · pnpm workspace of 2 packages", tree.Root!.Detail?.English);
            tree.Expand(Assert.Single(tree.Rows, r => r.Kind == NodeKind.Folder && r.Name == "packages"));
            Assert.Equal("@mono/web · pnpm", Assert.Single(tree.Rows, r => r.Name == "web").Detail?.English);
            ExplorerNode api = Assert.Single(tree.Rows, r => r.Name == "api");
            Assert.Equal("@mono/api · pnpm", api.Detail?.English);
            Assert.Equal("Node package @mono/api, run with pnpm (pnpm-lock.yaml): its scripts start and test run from the run configurations", api.Tip?.English);

            // The package.json's scripts have run icons, whose menu runs a script and greys what only .NET's programs take.
            window.OpenFile(Path.Combine(folder, "packages", "web", "package.json"));
            Ui.Settle();
            Passenger.App.Workspace.OpenDocument manifest = window.EditorArea.Active!;
            int dev = Array.FindIndex(manifest.Text.Snapshot.ToString().Split('\n'), l => l.Contains("\"dev\":", StringComparison.Ordinal));
            Ui.WaitFor(() => manifest.RunMarks.Select(m => m.Line).SequenceEqual([dev]), "the run icon of dev, and none of the hook run before it");
            Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
            double gutter = Enumerable.Range(0, 400).First(x => editor.IsOnGutterColumn(editor.Gutter!.Marks, new Avalonia.Point(x, 0))) + 3;
            Ui.Click(editor, new Avalonia.Point(gutter, editor.PointOf(manifest.Text.Snapshot.GetLineStart(dev)).Y + (editor.LineHeight / 2)));
            Avalonia.Controls.MenuItem[] items = [.. Assert.IsType<Avalonia.Controls.MenuFlyout>(window.RunMarkMenu).Items.OfType<Avalonia.Controls.MenuItem>()];
            Assert.Equal(["Run '@mono/web: dev'", "Debug '@mono/web: dev'", "Profile '@mono/web: dev'"], items.Select(i => i.Header as string));
            Assert.Equal([true, false, false], items.Select(i => i.IsEnabled));
            Ui.Choose(window.RunMarkMenu!, "Run '@mono/web: dev'");
            Ui.WaitFor(() => Output(window).Contains("serving", StringComparison.Ordinal), () => $"the script to start serving:\n{Output(window)}\n{window.Status}");
            string web = Path.Combine(folder, "packages", "web");
            Assert.Contains($"stand-in pnpm run dev", Output(window), StringComparison.Ordinal);
            Assert.Equal($"Running @mono/web: dev: pnpm run dev in {PathNames.Shown(web, folder)}", window.Status);
            Assert.Equal("@mono/web: dev (running)", window.Shown(window.Configuration!));
            lock (started)
            {
                ChildStart pnpm = Assert.Single(started, s => s.Command.Contains(Path.Combine(tools, "pnpm"), StringComparison.Ordinal));
                Assert.Equal(web, PathNames.Canonical(pnpm.Folder));
                Assert.DoesNotContain(started, s => s.Command.Contains("npm-cli.js", StringComparison.Ordinal));
            }
            Assert.Equal(Refusals.ScriptOnlyRuns("@mono/web: dev").Shown, window.Actions.WhyNot(AppActions.Debug, window.Actions.Context())?.Shown);

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => window.Shown(window.Configuration!) == "@mono/web: dev", () => $"the script's run to end once stopped: {window.Status}");
            Assert.StartsWith("@mono/web: dev ended with ", window.Status, StringComparison.Ordinal);
        }
        finally
        {
            ChildProcesses.Started -= Heard;
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    /// <summary>Words cmd.exe would read as its own (an operator, a pipe, a variable, an escape) reach a pnpm script's
    /// package manager as they were given, run in the Run window and in the Terminal, where on Windows pnpm is a .cmd that
    /// cmd.exe runs; the stand-ins for Node.js and pnpm say what they were given, so no Node.js is needed.</summary>
    private void A_scripts_arguments_reach_its_package_manager_word_for_word_through_cmds_operators_and_variables_in_the_Run_window_and_the_Terminal()
    {
        const string Words = "R&D a|b %PATH% x^y";
        string folder = PathNames.Canonical(TestFolders.Create("node-words-"));
        string tools = Path.Combine(folder, "tools");
        string node = TestFolders.StandIn(tools, "node", "echo v22.12.0", "echo v22.12.0");
        TestFolders.StandIn(tools, "pnpm", "echo stand-in pnpm %*", "echo \"stand-in pnpm $*\"");
        TestFolders.Write(folder, "package.json", """{ "name": "site", "scripts": { "dev": "vite" } }""");
        TestFolders.Write(folder, "pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Autosaves = false };
        window.Show();
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = node });
            window.OpenFolder(folder);
            Ui.WaitFor(() => window.Configuration?.Name == "site: dev", "the package's script chosen, the folder's one configuration");
            Ui.Launched(window, window.RunAsync(Words), "the script's run in the Run window");
            Ui.WaitFor(() => window.Shown(window.Configuration!) == "site: dev" && Output(window).Contains("stand-in pnpm", StringComparison.Ordinal),
                () => $"the stand-in pnpm to say what it was given, and end:\n{Output(window)}\n{window.Status}");
            Assert.True(Given(Output(window)) == $"run dev {Words}",
                $"pnpm was given other words than the run's {Words}, as cmd.exe read them as its own:\n{Output(window)}");

            RunConfiguration inTerminal = window.Configuration! with { Name = "site: dev in the terminal", Generated = false, Console = ConsoleKind.Terminal };
            window.SetConfigurations([inTerminal], inTerminal.Name);
            Ui.Launched(window, window.RunAsync(Words), "the script's run in the Terminal");
            TerminalRun run = window.RunInTerminal ?? throw new Xunit.Sdk.XunitException("The script did not run in the Terminal");
            Ui.WaitFor(() => !run.IsRunning && Shown(run).Contains("stand-in pnpm", StringComparison.Ordinal),
                () => $"the stand-in pnpm to say in the Terminal what it was given, and end:\n{Shown(run)}");
            Assert.True(Given(Shown(run)) == $"run dev {Words}",
                $"pnpm in the Terminal was given other words than the run's {Words}, as cmd.exe read them as its own:\n{Shown(run)}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }

        // What the stand-in pnpm said it was given, the quotes a .cmd keeps around its arguments taken off.
        static string Given(string said) =>
            said.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("stand-in pnpm ", StringComparison.Ordinal))["stand-in pnpm ".Length..].Replace("\"", string.Empty, StringComparison.Ordinal);

        static string Shown(TerminalRun run) =>
            string.Join('\n', run.Session.Screen.Scrollback.Select(l => l.Text()).Concat(run.Session.Screen.Lines.Select(l => l.Text())));
    }

    /// <summary>A run of several parts in the folder's passenger-runs.json: the package's install, then its dev script, which
    /// the part after it waits for until it says what it runs, then its build script, each by pnpm (a stand-in saying what it
    /// was given) in a tab of the Run window of its own, named by the package's name.</summary>
    private void A_run_of_several_parts_installs_with_the_packages_manager_then_starts_each_script_once_the_one_before_is_ready()
    {
        string folder = PathNames.Canonical(TestFolders.Create("node-parts-"));
        string tools = Path.Combine(folder, "tools");
        string node = TestFolders.StandIn(tools, "node", "echo v22.12.0", "echo v22.12.0");
        TestFolders.StandIn(tools, "pnpm", "echo stand-in pnpm %*", "echo \"stand-in pnpm $*\"");
        TestFolders.Write(folder, "package.json", """{ "name": "site", "scripts": { "dev": "vite", "build": "vite build" } }""");
        TestFolders.Write(folder, "pnpm-lock.yaml", "lockfileVersion: '9.0'\n");
        TestFolders.Write(folder, Passenger.Workspace.RunsFile.FileName, """
            { "passengerRuns": 1, "runs": [ { "name": "Site", "parts": [
              { "install": "package.json" },
              { "script": "dev", "package": "package.json", "readyWhen": { "line": "pnpm.*dev" } },
              { "script": "build", "package": "package.json" }
            ] } ] }
            """);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Autosaves = false };
        window.Show();
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = node });
            window.OpenFolder(folder);
            Ui.WaitFor(() => window.SelectConfiguration("Site"), () => $"the folder's run to be listed ({string.Join(", ", window.Configurations.Select(c => c.Name))})");
            Ui.Launched(window, window.RunAsync(), "the run's parts in the Run window");
            Ui.WaitFor(() => window.RunParts.Select(p => p.Name).SequenceEqual(["site: install", "site: dev", "site: build"]) && window.RunParts.All(p => !p.IsRunning)
                    && window.RunParts[2].Runner.Shown().ToString().Contains("stand-in pnpm run build", StringComparison.Ordinal),
                () => $"each part to run by pnpm in its own tab, in turn ({string.Join("; ", window.RunParts.Select(p => $"{p.Name} {p.Said}: {p.Runner.Shown()}"))}; {window.Status})");
            Assert.True(window.RunParts[0].Runner.Shown().ToString().Contains("stand-in pnpm install", StringComparison.Ordinal)
                && window.RunParts[1].Runner.Shown().ToString().Contains("stand-in pnpm run dev", StringComparison.Ordinal),
                $"The install or the dev script was not run by the package's own manager: {string.Join("; ", window.RunParts.Select(p => p.Runner.Shown().ToString()))}");
            Assert.True(window.Status == "Running Site: site: install, site: dev and site: build, each in a tab of the Run window", $"The status bar says {window.Status}");
        }
        finally
        {
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_Yarn_package_with_no_Yarn_says_how_to_get_it_and_starts_nothing_rather_than_npm()
    {
        Assert.SkipWhen(Programs.Command("yarn") is not null, "Yarn is on the PATH, which a package locked by Yarn runs with");
        string folder = PathNames.Canonical(TestFolders.Create("node-yarn-"));
        string tools = NodeBeside(folder, standIn: null);
        TestFolders.Write(folder, "package.json", """{ "name": "shop", "scripts": { "start": "node index.js" } }""");
        TestFolders.Write(folder, "yarn.lock", "# yarn lockfile v1\n");
        List<ChildStart> started = [];
        void Heard(object? sender, ChildStart start)
        {
            lock (started)
            {
                started.Add(start);
            }
        }
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Autosaves = false };
        window.Show();
        ChildProcesses.Started += Heard;
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = Path.Combine(tools, NodeName) });
            window.OpenFolder(folder);
            Ui.WaitFor(() => window.Configuration?.Name == "shop: start", "the package's script chosen, the folder's one configuration");
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => window.Status?.StartsWith("shop runs its scripts", StringComparison.Ordinal) == true, () => $"the run to say why it cannot start: {window.Status}");
            Assert.Equal("shop runs its scripts with yarn (yarn.lock), and yarn was not found beside Node.js or on the PATH: "
                + "corepack enable yarn puts it there with Node.js, or see https://yarnpkg.com/getting-started/install", window.Status);
            Assert.Equal("shop: start", window.Shown(window.Configuration!));
            lock (started)
            {
                Assert.DoesNotContain(started, s => s.Command.Contains("run start", StringComparison.Ordinal));
            }
        }
        finally
        {
            ChildProcesses.Started -= Heard;
            window.ApplySettings(new AppSettings());
            window.Close();
        }
    }

    private static readonly string NodeName = OperatingSystem.IsWindows() ? "node.exe" : "node";

    /// <summary>A Node.js of the test's own in a folder beside the packages, a link to the PATH's, with a stand-in package
    /// manager beside it that says how it was asked and then serves, where one is named; the folder.</summary>
    private static string NodeBeside(string folder, string? standIn)
    {
        Assert.SkipWhen(Programs.Node() is null, NoNode);
        Assert.SkipWhen(OperatingSystem.IsWindows(), "A Node.js of the test's own is a link to the PATH's, which Windows makes only with a privilege");
        string tools = Path.Combine(folder, "tools");
        Directory.CreateDirectory(tools);
        File.CreateSymbolicLink(Path.Combine(tools, NodeName), Programs.Node()!);
        if (standIn is not null && !OperatingSystem.IsWindows())
        {
            string program = TestFolders.Write(tools, standIn, $"#!/bin/sh\necho \"stand-in {standIn} $*\"\nexec {Serving}\n");
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return tools;
    }

    private static string Output(MainWindow window)
    {
        window.BottomPanel.Run.Flush();
        return window.BottomPanel.Run.Document.Snapshot.ToString();
    }
}
