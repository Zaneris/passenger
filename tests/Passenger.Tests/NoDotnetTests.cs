using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.Explorer;
using Passenger.App.Templates;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Passenger on a machine where no .NET is found (none where the installers put it, none on the PATH, no
/// DOTNET_ROOT): the banner over the window says so on the Welcome screen and over a C# folder, which edits and commits
/// while what needs .NET is refused in the banner's words; Install .NET SDK asks, then installs the SDK a stand-in of
/// Microsoft's release metadata names into the person's own folder, its SHA-512 checked, a wrong one refused with nothing
/// installed, after which the folder's project loads with it and new shells find it; an SDK where no PATH names it is
/// found and named. It changes what Passenger finds and the process's PATH and DOTNET_ROOT, so it runs alone.</summary>
[Collection(Alone.Name)]
public sealed class NoDotnetTests : IAsyncDisposable
{
    private static readonly string DotnetName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    /// <summary>The machine's own .NET, read before any test hides it.</summary>
    private static readonly string Machine = Dotnet.Root();

    /// <summary>The .NET the test projects target ("10.0"), whose SDK the stand-in archive carries.</summary>
    private static readonly string Channel = TestProjects.Framework["net".Length..];

    private readonly string folder = PathNames.Canonical(TestFolders.Create("no-dotnet-"));
    private readonly string? path = Environment.GetEnvironmentVariable("PATH");
    private readonly string? root = Environment.GetEnvironmentVariable(RuntimeVariables.Root);
    private readonly string? zdotdir = Environment.GetEnvironmentVariable("ZDOTDIR");

    /// <summary>The person's own folder Install .NET SDK installs into, in the test's.</summary>
    private string Installs => Path.Combine(folder, "user-dotnet");

    private string Home => Path.Combine(folder, "home");

    public NoDotnetTests()
    {
        Directory.CreateDirectory(Home);
        Dotnet.PlacesStandIn = [Path.Combine(folder, "nowhere", DotnetName)];
        DotnetInstall.FolderStandIn = Installs;
        ShellPath.HomeStandIn = Home;
        ShellPath.UserStandIn = [];
        OwnEnvironment.Set("ZDOTDIR", Home);
        OwnEnvironment.Set("PATH", string.Join(Path.PathSeparator, (path ?? string.Empty).Split(Path.PathSeparator)
            .Where(at => at.Length > 0).Select(WithoutDotnet).OfType<string>()));
        OwnEnvironment.Set(RuntimeVariables.Root, null);
        Dotnet.ForgetSdks();
    }

    /// <summary>A folder of the PATH as the test's PATH has it: as it is where it holds no dotnet; where it does, on macOS and
    /// Linux (whose /usr/bin may link dotnet beside git) a folder of the test's linking each of its other programs, and on
    /// Windows, where .NET's folder holds .NET alone, none.</summary>
    private string? WithoutDotnet(string at, int place)
    {
        if (!File.Exists(Path.Combine(at, DotnetName)))
        {
            return at;
        }
        if (OperatingSystem.IsWindows())
        {
            return null;
        }
        string kept = Directory.CreateDirectory(Path.Combine(folder, "path-without-dotnet", place.ToString(System.Globalization.CultureInfo.InvariantCulture))).FullName;
        foreach (string program in Directory.EnumerateFileSystemEntries(at).Where(p => Path.GetFileName(p) != DotnetName))
        {
            File.CreateSymbolicLink(Path.Combine(kept, Path.GetFileName(program)), program);
        }
        return kept;
    }

    public async ValueTask DisposeAsync()
    {
        Dotnet.PlacesStandIn = null;
        DotnetInstall.FolderStandIn = null;
        ShellPath.HomeStandIn = null;
        ShellPath.UserStandIn = null;
        OwnEnvironment.Set("PATH", path);
        OwnEnvironment.Set(RuntimeVariables.Root, root);
        OwnEnvironment.Set("ZDOTDIR", zdotdir);
        Environment.SetEnvironmentVariable(DotnetReleases.Variable, "http://127.0.0.1:9/");
        Dotnet.ForgetSdks();
        // A host on the test's SDK would be the one a later folder taking that version is given.
        await ProjectHosts.EndUnderAsync(folder);
        TestFolders.Delete(folder);
    }

    [AvaloniaFact]
    public void With_no_dotnet_the_Welcome_screen_says_so_its_New_Solution_refusing_in_those_words_and_links_the_download_page()
    {
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        Ui.Settle();
        Assert.True(window.DotnetSaid == Refusals.NoDotnet, $"With no .NET, the Welcome screen's banner said: {window.DotnetSaid}");
        Assert.Equal(DotnetLack.NoDotnet, window.DotnetWant.Lack);

        Assert.True(!window.Welcome.NewSolutionButton.IsEnabled && ToolTip.GetTip(window.Welcome.NewSolutionButton) as string == Refusals.NoDotnet,
            $"The Welcome screen's New Solution is {(window.Welcome.NewSolutionButton.IsEnabled ? "not greyed" : "greyed")}, its tip saying {ToolTip.GetTip(window.Welcome.NewSolutionButton)}");
        bool ran = window.Perform(AppActions.NewSolution);
        Ui.Settle();
        Assert.True(!ran && window.Actions.WhyNot(AppActions.NewSolution, window.Actions.Context()) == Refusals.NoDotnet && !window.OwnedWindows.OfType<NewProjectDialog>().Any(),
            $"New Solution with no .NET {(ran ? "ran" : "refused")}, saying {window.Actions.WhyNot(AppActions.NewSolution, window.Actions.Context())} ({window.OwnedWindows.Count} windows over it)");

        Ui.ClickButton(window, "Download Page");
        Assert.Equal("https://dotnet.microsoft.com/download", Assert.Single(window.Browsed).AbsoluteUri);
        window.Close();
    }

    [AvaloniaFact]
    public void A_csharp_folder_with_no_dotnet_says_so_refuses_what_needs_it_in_those_words_and_still_edits_and_commits()
    {
        string solution = Shop(folder);
        TestGit.Init(folder);
        MainWindow window = Ui.OpenFolder(solution);
        Ui.WaitFor(() => window.Session.Models.MissingSdk is not null, "the project host to find no .NET", TimeSpan.FromMinutes(1));
        Ui.WaitFor(() => window.CSharpService is { IsReady: true } or { IsStopped: true }, () => $"the C# language server to have tried the projects ({window.CSharpService?.State})", TimeSpan.FromMinutes(2));
        Ui.Settle();
        Assert.True(window.DotnetSaid == Refusals.NoDotnet, $"Over a C# folder with no .NET, the banner said: {window.DotnetSaid}");
        Assert.True(window.Status == Refusals.NoDotnet, $"Over a C# folder with no .NET, the status bar said: {window.Status}");
        ActionContext here = window.Actions.Context();
        foreach (string action in (string[])[AppActions.Build, AppActions.RunProgram, AppActions.Debug, AppActions.RunAllTests, AppActions.NewProject])
        {
            Assert.True(window.Actions.WhyNot(action, here) == Refusals.NoDotnet, $"{action} with no .NET is refused with: {window.Actions.WhyNot(action, here)}");
        }
        Assert.Null(window.Actions.WhyNot(AppActions.InstallDotnetSdk, here));
        Assert.Null(window.Actions.WhyNot(AppActions.InstallDotnetSdk, here.ForAgent()));
        Assert.Equal(Refusals.AgentChangesPath, window.Actions.WhyNot(AppActions.AddDotnetToPath, here.ForAgent()));
        Ui.Run(window, AppActions.Build);
        Assert.Equal(Refusals.NoDotnet, window.Status);

        window.OpenFile(Path.Combine(folder, "App", "Program.cs"));
        Ui.PlaceAfter(window.EditorArea.CodeEditor, "(\"Shop\");");
        window.KeyTextInput(" // open");
        window.EditorArea.SaveAll();
        Ui.Settle();
        Assert.Contains("(\"Shop\"); // open", File.ReadAllText(Path.Combine(folder, "App", "Program.cs")), StringComparison.Ordinal);
        Task read = window.Vcs.RefreshAsync();
        Ui.WaitFor(() => read.IsCompleted && window.Vcs.Changes.Any(c => c.Path.EndsWith("Program.cs", StringComparison.Ordinal)),
            () => $"Program.cs listed as changed, where Git lists [{string.Join(", ", window.Vcs.Changes.Select(c => $"{c.Kind} {c.Path}"))}]");
        Ui.Landed(window.Vcs.CommitAsync([.. window.Vcs.Changes.Where(c => c.Path.EndsWith("Program.cs", StringComparison.Ordinal))], "Open", amend: false, push: false), "the commit");
        Assert.Contains("Open", TestGit.Run(folder, "log", "-1", "--format=%s"), StringComparison.Ordinal);

        // Nothing that cannot start without .NET is started again and again.
        string[] log = Ui.IdeLog().Split('\n');
        int servers = log.Count(l => l.Contains($") started for {folder}", StringComparison.Ordinal) && l.Contains("C# language server", StringComparison.Ordinal));
        int hosts = log.Count(l => l.Contains("Project host (process", StringComparison.Ordinal) && l.Contains($"started with the .NET SDK", StringComparison.Ordinal)
            && l.EndsWith($"for {folder}", StringComparison.Ordinal));
        Assert.True(servers == 1 && hosts == 0, $"With no .NET the C# language server started {servers} times and a project host {hosts} times for the folder");
        window.Close();
    }

    [AvaloniaFact]
    public void Install_dotnet_SDK_asks_then_installs_the_SDK_from_the_feed_into_the_persons_folder_checked_and_the_folder_loads_with_it_and_new_shells_find_it()
    {
        StandIn sdk = Archive.Value;
        using StandInHttp feed = Feed(sdk, sdk.Sha512);
        string solution = Shop(folder);
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Ui.WaitFor(() => window.DotnetWant.Lack == DotnetLack.NoDotnet && window.Session.Models.MissingSdk is not null, "the window to find no .NET", TimeSpan.FromMinutes(1));

        List<string> stages = [];
        window.Actions.Work.Changed += (_, change) =>
        {
            if (change.Work.Id == AppActions.InstallDotnetSdk && !change.Ended)
            {
                stages.Add(change.Work.Stage);
            }
        };
        ConfirmDialog dialog = Asked(window);
        Assert.Equal($"Install the .NET SDK {sdk.Version}?", dialog.Title);
        // Its size, which the metadata does not give, from the archive's HEAD.
        string size = $", {Math.Max(1, new FileInfo(sdk.Path).Length / (1024 * 1024))} MB,";
        Assert.True(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains(size, StringComparison.Ordinal) == true),
            $"The question does not give the download's size{size}: {string.Join(" | ", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))}");
        CheckBox onPath = dialog.GetVisualDescendants().OfType<CheckBox>().Single(c => c.Name == "AddDotnetToPath");
        Assert.True(onPath.IsChecked == true, "Install .NET SDK does not offer to add it to the shell's PATH, checked");
        Ui.ClickButton(dialog, "Install");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallDotnetSdk) is null, () => $"the install to end ({window.Actions.Work.Of(AppActions.InstallDotnetSdk)?.Stage})", TimeSpan.FromMinutes(5));
        // Each step named as it runs, and timed in the log, so where an install's time goes reads from it.
        foreach (string step in (string[])[$"Downloading the .NET SDK {sdk.Version}", $"Checking the .NET SDK {sdk.Version}'s archive", $"Unpacking the .NET SDK {sdk.Version}",
            $"Moving the .NET SDK {sdk.Version} into ", $"Starting the .NET SDK {sdk.Version} for the first time"])
        {
            Assert.True(stages.Any(s => s.StartsWith(step, StringComparison.Ordinal)), $"The install never said '{step}': it said {string.Join(" | ", stages.Distinct())}");
        }
        string log = Ui.IdeLog();
        Assert.True(log.Contains($"Install .NET SDK {sdk.Version}: installed; download ", StringComparison.Ordinal) && log.Contains(", move ", StringComparison.Ordinal)
            && log.Contains($"Install .NET SDK {sdk.Version}: first run ", StringComparison.Ordinal), "The log does not time the install's steps");
        Assert.True(window.Status?.StartsWith($"The .NET SDK {sdk.Version} is installed in ", StringComparison.Ordinal) == true, $"The install said: {window.Status}");
        Assert.True(File.Exists(Path.Combine(Installs, DotnetName)) && File.Exists(Path.Combine(Installs, "sdk", sdk.Version, "dotnet.dll")),
            $"Nothing of the SDK is in {Installs}: {string.Join(", ", Directory.EnumerateFileSystemEntries(Installs).Select(Path.GetFileName))}");
        Assert.DoesNotContain(Directory.EnumerateDirectories(Installs), d => Path.GetFileName(d).StartsWith(".passenger-install-", StringComparison.Ordinal));

        Ui.WaitFor(() => window.Session.Models.MissingSdk is null && window.ExplorerView.Tree.Rows.Any(r => r.Kind == NodeKind.Project && r.Detail == TestProjects.Framework),
            () => $"App to be evaluated with the SDK installed: App's row says {window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Name == "App")?.Detail} ({window.Status})", TimeSpan.FromMinutes(2));
        Assert.True(PathNames.Same(Environment.GetEnvironmentVariable(RuntimeVariables.Root) ?? string.Empty, Installs)
            && PathNames.Same((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)[0], Installs),
            "What Passenger starts is not given the SDK installed first on its PATH and as its DOTNET_ROOT");
        Assert.Null(window.DotnetSaid);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(PathNames.Same(ShellPath.UserStandIn!["Path"].Split(';')[0], Installs) && PathNames.Same(ShellPath.UserStandIn[RuntimeVariables.Root], Installs),
                $"The user's Path is now {ShellPath.UserStandIn.GetValueOrDefault("Path")}");
        }
        else
        {
            string profile = ShellPath.For(Path.Combine(folder, "elsewhere"))!.File!;
            Assert.Contains("export PATH=\"$DOTNET_ROOT:$PATH\"", File.ReadAllText(profile), StringComparison.Ordinal);
        }
        window.Close();
    }

    [AvaloniaFact]
    public void An_archive_whose_SHA_512_is_not_the_one_published_is_refused_with_nothing_installed()
    {
        string archive = TestFolders.Write(folder, OperatingSystem.IsWindows() ? "fake.zip" : "fake.tar.gz", "not the SDK Microsoft publishes\n");
        StandIn sdk = new(archive, "10.0.999", Convert.ToHexStringLower(SHA512.HashData(File.ReadAllBytes(archive))));
        using StandInHttp feed = Feed(sdk, new string('0', 128));
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        Ui.Settle();
        ConfirmDialog dialog = Asked(window);
        Ui.ClickButton(dialog, "Install");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallDotnetSdk) is null, "the install to end", TimeSpan.FromMinutes(1));
        Assert.Equal("The .NET SDK 10.0.999's archive is not the one Microsoft publishes (its SHA-512 differs), so nothing was installed", window.Status);
        Assert.True(!Directory.Exists(Installs), Directory.Exists(Installs)
            ? $"A refused install left the folder it made, {Installs}, holding [{string.Join(", ", Directory.EnumerateFileSystemEntries(Installs).Select(Path.GetFileName))}]"
            : string.Empty);
        Assert.Equal(Refusals.NoDotnet, window.DotnetSaid);
        window.Close();
    }

    [AvaloniaFact]
    public void An_SDK_in_the_home_dotnet_folder_no_PATH_names_is_found_named_and_used_and_Add_to_PATH_puts_it_there()
    {
        string own = Path.Combine(Home, ".dotnet");
        Unpack(Archive.Value.Path, own);
        Dotnet.PlacesStandIn = [Path.Combine(own, DotnetName)];
        string solution = Shop(folder);
        MainWindow window = Ui.OpenFolder(solution, languageServer: false);
        Ui.WaitFor(() => window.ExplorerView.Tree.Rows.Any(r => r.Kind == NodeKind.Project && r.Detail == TestProjects.Framework),
            () => $"App to be evaluated with the SDK in {own}: App's row says {window.ExplorerView.Tree.Rows.FirstOrDefault(r => r.Name == "App")?.Detail} ({window.Status})", TimeSpan.FromMinutes(2));
        Assert.Equal(DotnetLack.OffPath, window.DotnetWant.Lack);
        Assert.True(window.DotnetSaid == $"Passenger uses the .NET SDK {Archive.Value.Version} in {PathNames.Shown(own, null)}, which your shell's PATH does not name, so a terminal outside Passenger finds no dotnet",
            $"The banner over an SDK no PATH names said: {window.DotnetSaid}");

        Ui.ClickButton(window, "Add .NET to the Shell's PATH…");
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any(), "Add to PATH to ask");
        ConfirmDialog dialog = DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single());
        Ui.ClickButton(dialog, "Add to PATH");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.AddDotnetToPath) is null && window.DotnetSaid is null, () => $"the banner to go once the PATH has it ({window.Status})", TimeSpan.FromMinutes(1));
        Assert.Equal(Refusals.OnPathAlready(own), window.Actions.WhyNot(AppActions.AddDotnetToPath, window.Actions.Context()));
        window.Close();
    }

    /// <summary>An agent's Install .NET SDK on a person's window is their question, in a window of its own that waits for
    /// them, the install going on only as they say: declined, nothing is installed and no folder made, in words the agent is
    /// told; an agent's own Passenger, which nobody is at, refuses it as before.</summary>
    [AvaloniaFact]
    public void An_agents_install_on_a_persons_window_is_their_question_and_an_agents_own_passenger_refuses_it()
    {
        StandIn sdk = Archive.Value;
        using StandInHttp feed = Feed(sdk, sdk.Sha512);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        Ui.Settle();
        Assert.Null(window.Actions.RunForAgent(AppActions.InstallDotnetSdk));
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any() || window.Actions.Work.Of(AppActions.InstallDotnetSdk) is null,
            () => $"the agent's install to ask the person ({window.Actions.Work.Of(AppActions.InstallDotnetSdk)?.Stage}; {window.Status})", TimeSpan.FromMinutes(1));
        ConfirmDialog asked = DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().Single());
        Assert.Equal($"Let an agent install the .NET SDK {sdk.Version}?", asked.Title);
        Assert.True(window.Actions.Work.Of(AppActions.InstallDotnetSdk)?.Stage == $"Waiting for the person to allow installing the .NET SDK {sdk.Version}",
            $"The agent's install waits saying {window.Actions.Work.Of(AppActions.InstallDotnetSdk)?.Stage}");
        Ui.ClickButton(asked, "Don't Install");
        Ui.WaitFor(() => window.Actions.Work.Of(AppActions.InstallDotnetSdk) is null, "the agent's install to end", TimeSpan.FromMinutes(1));
        Assert.Equal($"The person did not allow installing the .NET SDK {sdk.Version}, so nothing was installed", window.Status);
        Assert.False(Directory.Exists(Installs), $"The install the person declined made {Installs}");
        window.Close();

        MainWindow own = new() { Width = 1200, Height = 800, StartsLanguageServer = false, AgentsOwn = true };
        own.Show();
        Ui.Settle();
        Assert.Equal(Refusals.AgentInstalls, own.Actions.WhyNot(AppActions.InstallDotnetSdk, own.Actions.Context().ForAgent()));
        own.Close();
    }

    /// <summary>Hide on the word that the shell's PATH does not name the SDK Passenger found is kept for that installation: the
    /// next window keeping its state where the first did says nothing of it either.</summary>
    [AvaloniaFact]
    public void Hide_on_the_off_PATH_word_is_kept_for_that_installation_in_the_next_window()
    {
        string own = Path.Combine(Home, ".dotnet");
        Unpack(Archive.Value.Path, own);
        Dotnet.PlacesStandIn = [Path.Combine(own, DotnetName)];
        string state = TestFolders.Create("no-dotnet-state-");
        try
        {
            MainWindow window = Kept(state);
            Ui.WaitFor(() => window.DotnetWant.Lack == DotnetLack.OffPath && window.DotnetSaid is not null, () => $"the off-PATH word ({window.DotnetWant.Lack}: {window.DotnetSaid})", TimeSpan.FromMinutes(1));
            Ui.ClickButton(window, "Hide");
            Assert.Null(window.DotnetSaid);
            window.Close();

            MainWindow again = Kept(state);
            Ui.WaitFor(() => again.DotnetWant.Lack == DotnetLack.OffPath, () => $"the next window to find the SDK off the PATH ({again.DotnetWant.Lack})", TimeSpan.FromMinutes(1));
            Ui.Settle();
            Assert.True(again.DotnetSaid is null, $"The next window said the word Hide hid: {again.DotnetSaid}");
            again.Close();
        }
        finally
        {
            TestFolders.Delete(state);
        }
    }

    /// <summary>A C# file of no project shown with no .NET at all has the banner say, after what is missing, that it knows no
    /// framework types until an SDK is installed, and why: Roslyn's server reads a loose file's framework only from a project
    /// the SDK's MSBuild builds, so the carried runtime cannot stand in.</summary>
    [AvaloniaFact]
    public void A_loose_csharp_file_with_no_dotnet_has_the_banner_say_it_knows_no_framework_types_and_why()
    {
        string loose = TestFolders.Write(folder, "Basket.cs", "namespace Shop;\n\npublic class Basket\n{\n}\n");
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        Ui.Settle();
        Assert.Null(window.OpenFile(loose));
        Ui.Settle();
        Assert.Equal($"{Refusals.NoDotnet}. {Refusals.LooseWithoutSdk("Basket.cs")}", window.DotnetSaid);
        window.Close();
    }

    /// <summary>A window keeping its state in <paramref name="state"/>, as the window a person uses does.</summary>
    private static MainWindow Kept(string state)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state, StartsLanguageServer = false };
        window.Show();
        window.KeepLayout();
        Ui.Settle();
        return window;
    }

    /// <summary>Install .NET SDK clicked in the banner, waited on until it asks.</summary>
    private static ConfirmDialog Asked(MainWindow window)
    {
        Ui.ClickButton(window, "Install .NET SDK…");
        Ui.WaitFor(() => window.OwnedWindows.OfType<ConfirmDialog>().Any() || window.Actions.Work.Of(AppActions.InstallDotnetSdk) is null,
            () => $"Install .NET SDK to ask ({window.Actions.Work.Of(AppActions.InstallDotnetSdk)?.Stage})", TimeSpan.FromMinutes(1));
        return DialogButtonsTests.Check(window.OwnedWindows.OfType<ConfirmDialog>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Install .NET SDK asked nothing: {window.Status}"));
    }

    /// <summary>A solution of one console project of the test projects' .NET; its path.</summary>
    private static string Shop(string folder)
    {
        TestFolders.Write(folder, "Shop.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "App/App.csproj", TestProjects.Console());
        TestFolders.Write(folder, "App/Program.cs", "System.Console.WriteLine(\"Shop\");\n");
        return Path.Combine(folder, "Shop.slnx");
    }

    /// <summary>A stand-in of an SDK's archive: where it is, the SDK's version, and its SHA-512.</summary>
    private sealed record StandIn(string Path, string Version, string Sha512);

    /// <summary>The release metadata of one channel naming <paramref name="sdk"/> for this system, its hash given, and the archive.</summary>
    private static StandInHttp Feed(StandIn sdk, string hash)
    {
        string system = DotnetReleases.ThisSystem();
        string name = $"dotnet-sdk-{system}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        string file = $"/Sdk/{sdk.Version}/dotnet-sdk-{sdk.Version}-{system}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}";
        string at = string.Empty;
        StandInHttp feed = new(async exchange =>
        {
            if (exchange.Path == "/releases-index.json")
            {
                await exchange.ReplyAsync(200, "application/json",
                    $$"""{ "releases-index": [ { "channel-version": "{{Channel}}", "support-phase": "active", "releases.json": "{{at}}/{{Channel}}/releases.json" } ] }""");
            }
            else if (exchange.Path == $"/{Channel}/releases.json")
            {
                await exchange.ReplyAsync(200, "application/json", $$"""
                    { "channel-version": "{{Channel}}", "releases": [ { "release-version": "{{Channel}}.0", "sdk": { "version": "{{sdk.Version}}", "runtime-version": "",
                      "files": [ { "name": "{{name}}", "rid": "{{system}}", "url": "{{at}}{{file}}", "hash": "{{hash}}" } ] } } ] }
                    """);
            }
            else if (exchange.Path == file && exchange.Method == "HEAD")
            {
                await exchange.HeadAsync(200, "application/octet-stream", new FileInfo(sdk.Path).Length);
            }
            else if (exchange.Path == file)
            {
                await using FileStream body = File.OpenRead(sdk.Path);
                await exchange.HeadAsync(200, "application/octet-stream", body.Length);
                await body.CopyToAsync(exchange.Stream, exchange.Ending);
            }
            else
            {
                await exchange.ReplyAsync(404, "text/plain", "not here");
            }
        });
        at = feed.Root;
        Environment.SetEnvironmentVariable(DotnetReleases.Variable, feed.Root + "/");
        return feed;
    }

    /// <summary>A stand-in of Microsoft's SDK archive for this system, made once for the run from the machine's own .NET: its
    /// dotnet, the newest SDK of the test projects' .NET and the runtime and hostfxr it runs on, without the tools no
    /// evaluation reads (F#, the bundled global tools, .NET Framework's test host).</summary>
    private static readonly Lazy<StandIn> Archive = new(() =>
    {
        string version = DotnetInstallation.Read(Machine).Sdks.First(v => v.StartsWith(Channel + ".", StringComparison.Ordinal) && !VersionOrder.IsPrerelease(v));
        string sdk = Path.Combine(Machine, "sdk", version);
        string runtime = JsonNode.Parse(File.ReadAllText(Path.Combine(sdk, "dotnet.runtimeconfig.json")))!["runtimeOptions"]!["framework"]!["version"]!.GetValue<string>();
        string fxr = Directory.Exists(Path.Combine(Machine, "host", "fxr", runtime)) ? runtime
            : Directory.EnumerateDirectories(Path.Combine(Machine, "host", "fxr")).Select(Path.GetFileName).OfType<string>().Max(VersionOrder.Instance)!;
        string[] left = ["FSharp", "DotnetTools", "TestHostNetFramework"];
        List<(string From, string Entry)> files = [(Path.Combine(Machine, DotnetName), DotnetName)];
        foreach (string part in (string[])[Path.Combine("host", "fxr", fxr), Path.Combine("sdk", version), Path.Combine("shared", "Microsoft.NETCore.App", runtime)])
        {
            string from = Path.Combine(Machine, part);
            files.AddRange(Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)
                .Where(f => !left.Any(l => Path.GetRelativePath(from, f).StartsWith(l + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                .Select(f => (f, Path.Combine(part, Path.GetRelativePath(from, f)).Replace('\\', '/'))));
        }
        string archive = Path.Combine(TestRun.Root, $"dotnet-sdk-{version}-stand-in{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}");
        using (FileStream into = File.Create(archive))
        {
            if (OperatingSystem.IsWindows())
            {
                using ZipArchive zip = new(into, ZipArchiveMode.Create);
                foreach ((string from, string entry) in files)
                {
                    zip.CreateEntryFromFile(from, entry, CompressionLevel.Fastest);
                }
            }
            else
            {
                using GZipStream gzip = new(into, CompressionLevel.Fastest);
                using TarWriter tar = new(gzip);
                foreach ((string from, string entry) in files)
                {
                    tar.WriteEntry(from, entry);
                }
            }
        }
        using FileStream read = File.OpenRead(archive);
        return new StandIn(archive, version, Convert.ToHexStringLower(SHA512.HashData(read)));
    });

    /// <summary>The stand-in archive unpacked by hand, as a person's own install of it would leave it.</summary>
    private static void Unpack(string archive, string into)
    {
        Directory.CreateDirectory(into);
        if (OperatingSystem.IsWindows())
        {
            ZipFile.ExtractToDirectory(archive, into);
            return;
        }
        using FileStream file = File.OpenRead(archive);
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, into, overwriteFiles: false);
    }
}
