using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Editors;
using Passenger.App.Explorer;
using Passenger.App.Run;
using Passenger.App.Settings;
using Passenger.App.Web;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>A folder's Node packages through the window: a pnpm monorepo's root and packages named with their package
/// manager in the Explorer, each package's scripts run configurations that Run starts with pnpm (a stand-in beside the
/// Node.js Settings names, never npm) in the Run window and Stop ends; an ASP.NET Core app's front end beside its project's
/// own configuration; and a script that cannot run saying why and how to get what it lacks: no Node.js, a Node.js older
/// than its engines ask (said as it runs), no Yarn for a Yarn package. A test that cannot find Node.js skips, saying so.</summary>
public sealed class NodeScriptsTests
{
    private const string NoNode = "Node.js is not on the PATH, and a package's scripts run on it";

    /// <summary>A program that says it started and runs until it is ended, as a development server does.</summary>
    private const string Serving = "node -e \"console.log('serving'); setInterval(() => {}, 1000)\"";

    [AvaloniaFact]
    public void A_pnpm_monorepos_packages_show_their_manager_and_each_script_runs_with_pnpm_in_the_Run_window_until_stopped()
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
            Assert.Equal("mono · pnpm workspace of 2 packages", tree.Root!.Detail);
            tree.Expand(Assert.Single(tree.Rows, r => r.Kind == NodeKind.Folder && r.Name == "packages"));
            Assert.Equal("@mono/web · pnpm", Assert.Single(tree.Rows, r => r.Name == "web").Detail);
            ExplorerNode api = Assert.Single(tree.Rows, r => r.Name == "api");
            Assert.Equal("@mono/api · pnpm", api.Detail);
            Assert.Equal("Node package @mono/api, run with pnpm (pnpm-lock.yaml): its scripts start and test run from the run configurations", api.Tip);

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
            Assert.Equal(Refusals.ScriptOnlyRuns("@mono/web: dev"), window.Actions.WhyNot(AppActions.Debug, window.Actions.Context()));

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

    [AvaloniaFact]
    public void An_aspnet_apps_front_end_is_a_package_of_its_project_whose_scripts_follow_the_projects_own_configuration()
    {
        string folder = PathNames.Canonical(TestFolders.Create("node-spa-"));
        TestFolders.Write(folder, "Shop/Shop.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>{TestProjects.Framework}</TargetFramework>
                <SpaRoot>ClientApp\</SpaRoot>
              </PropertyGroup>
            </Project>
            """);
        TestFolders.Write(folder, "Shop/Program.cs", "WebApplication.CreateBuilder(args).Build().Run();\n");
        TestFolders.Write(folder, "Shop/ClientApp/package.json", """{ "name": "shop-client", "scripts": { "start": "ng serve", "build": "ng build" } }""");
        TestFolders.Write(folder, "Shop/ClientApp/package-lock.json", "{}");
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Select(c => c.Name).SequenceEqual(["Shop", "shop-client: start", "shop-client: build"]),
                () => $"the project's configuration then its front end's scripts, where there are {string.Join(", ", window.Configurations.Select(c => c.Name))}",
                TimeSpan.FromMinutes(2));
            Assert.Equal("Shop", window.Configuration?.Name);
            RunConfiguration start = window.Configurations.Single(c => c.Name == "shop-client: start");
            Assert.Equal((RunKind.Script, "start", Path.Combine(folder, "Shop", "ClientApp", "package.json")), (start.Kind, start.Script, start.Project));

            ExplorerTree tree = window.ExplorerView.Tree;
            tree.Expand(Assert.Single(tree.Rows, r => r.Name == "Shop"));
            Assert.Equal("shop-client · npm", Assert.Single(tree.Rows, r => r.Name == "ClientApp").Detail);

            // Edit Configurations shows the script with what a person may change of its run.
            Assert.True(window.SelectConfiguration("shop-client: start"));
            RunConfigurationsDialog dialog = window.ShowRunConfigurations();
            Ui.Settle();
            Assert.Equal(("package.json Script", "start, in Shop/ClientApp"), (dialog.KindText.Text, dialog.ScriptText.Text));
            Assert.True(dialog.ArgumentsBox.IsEffectivelyVisible, "A script's arguments are not offered in Edit Configurations");
            Assert.False(dialog.ProgramBox.IsEffectivelyVisible, "A script's configuration offers a program of its own");
            dialog.Close();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_script_with_no_Node_js_says_where_to_name_one_and_on_one_older_than_its_engines_ask_runs_saying_so_first()
    {
        Assert.SkipWhen(Programs.Node() is not { } node || Npm.Cli(node) is null, "npm is not beside the PATH's Node.js, and an npm package's scripts run with it");
        string folder = PathNames.Canonical(TestFolders.Create("node-engines-"));
        string tools = NodeBeside(folder, standIn: null);
        TestFolders.Write(folder, "package.json", """{ "name": "site", "engines": { "node": ">=99" }, "scripts": { "start": "node -e \"console.log('started')\"" } }""");
        TestFolders.Write(folder, "package-lock.json", "{}");
        string missing = Path.Combine(folder, "no-node-here", NodeName);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, Autosaves = false };
        window.Show();
        try
        {
            window.ApplySettings(window.Settings with { NodeInterpreter = missing });
            window.OpenFolder(folder);
            Ui.WaitFor(() => window.Configuration?.Name == "site: start", "the package's script chosen, the folder's one configuration");
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => window.Status?.StartsWith("site's scripts", StringComparison.Ordinal) == true, () => $"the run to say why it cannot start: {window.Status}");
            Assert.Equal($"site's scripts run on Node.js, and there is no Node.js at {missing}, which {NodeRuntime.SettingsPlace} names", window.Status);

            string named = Path.Combine(tools, NodeName);
            window.ApplySettings(window.Settings with { NodeInterpreter = named });
            Ui.Run(window, AppActions.RunProgram);
            Ui.WaitFor(() => Output(window).Contains("started", StringComparison.Ordinal) && window.Shown(window.Configuration!) == "site: start",
                () => $"the script to run on the Node.js named, and end:\n{Output(window)}\n{window.Status}");
            string older = Output(window).Split('\n')[0];
            Assert.StartsWith("site needs Node.js 99.0.0 or later, and ", older, StringComparison.Ordinal);
            Assert.EndsWith($": update it, or name a later one in {NodeRuntime.SettingsPlace}", older, StringComparison.Ordinal);
            Assert.Equal("site: start ended with exit code 0", window.Status);
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
        Assert.SkipWhen(Programs.OnPath(OperatingSystem.IsWindows() ? "yarn.cmd" : "yarn") is not null, "Yarn is on the PATH, which a package locked by Yarn runs with");
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
