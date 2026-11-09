using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Debugging;
using Passenger.Core;

namespace Passenger.Tests;

/// <summary>The SDK's own browser refresh under the debugger with Hot Reload, keeping a page up as dotnet watch does: a Blazor
/// app (<see cref="BellApp"/>) debugged, the page's refresh client the test's own, speaking as the SDK's client in a page does
/// with no browser (<see cref="RefreshClient"/>). A wwwroot stylesheet saved in the editor is applied in place and served
/// as saved, a component's scoped CSS another program wrote or deleted is bundled again and applied, another static file changed
/// reloads the page, and an applied update refreshes it: on the SDK the machine builds with, and on .NET 10's SDK, which
/// speaks the earlier way, where one is installed; and a Blazor Web App with WebAssembly components, whose client's build
/// makes its pages' browser tools, is kept up too, as are a referenced Razor class library's stylesheet and scoped CSS; where a
/// build makes none, Debug says why; a page at a name under localhost is kept up too. Debugged through the window, so run
/// alone.</summary>
[Collection(Alone.Name)]
public sealed class BrowserRefreshTests : IDisposable
{
    private readonly string folder = PathNames.Canonical(TestFolders.Create("browser-refresh-"));

    public void Dispose() => TestFolders.Delete(folder);

    [AvaloniaFact]
    public void Under_debug_a_pages_stylesheets_apply_in_place_its_other_static_files_reload_it_and_an_update_refreshes_it()
    {
        KeptUp(sdk: null);
    }

    [AvaloniaFact]
    public void On_the_NET_10_SDK_a_pages_stylesheets_apply_in_place_its_other_static_files_reload_it_and_an_update_refreshes_it()
    {
        string? sdk = Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult listed = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["--list-sdks"]));
            return listed.Output.Split('\n').Select(line => line.Split(' ')[0].Trim()).Where(version => Regex.IsMatch(version, @"^10\.0\.\d+$"))
                .OrderBy(version => int.Parse(version.Split('.')[2], System.Globalization.CultureInfo.InvariantCulture)).LastOrDefault();
        }), "the SDKs installed");
        Assert.SkipWhen(sdk is null, "No .NET 10 SDK (a release, not a preview) is installed");
        KeptUp(sdk);
    }

    /// <summary>A Blazor Web App with WebAssembly components on the tests' framework (.NET 10), built with the machine's SDK,
    /// whose browser tools (and the key Passenger proves itself with) its WebAssembly client's build makes: debugged, its page
    /// is kept up, a wwwroot stylesheet saved in the editor applied in place, with no word that it is not.</summary>
    [AvaloniaFact]
    public void A_Blazor_Web_App_with_WebAssembly_components_keeps_its_page_up_through_its_clients_browser_tools()
    {
        Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["new", "blazor", "--output", folder, "--name", "Bwa", "--framework", TestProjects.Framework, "--interactivity", "WebAssembly", "--no-https", "--no-restore"]),
                limit: TimeSpan.FromMinutes(3));
            Assert.True(made.Succeeded, $"dotnet new blazor did not make the test's app (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
            return made;
        }), "dotnet new blazor to make the test's app", TimeSpan.FromMinutes(3) + TestFolders.Spare);
        string app = Path.Combine(folder, "Bwa");
        string settings = Path.Combine(app, "Properties", "launchSettings.json");
        JsonNode launch = JsonNode.Parse(File.ReadAllText(settings))!;
        // A port of the system's choosing, which the app says as it listens.
        launch["profiles"]!["http"]!["applicationUrl"] = "http://127.0.0.1:0";
        launch["profiles"]!["http"]!["launchBrowser"] = false;
        File.WriteAllText(settings, launch.ToJsonString());
        // The package cache tests/BlazorGraphs fills holds the app's packages, never fetched.
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"feed\" /></packageSources>\n</configuration>\n");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        Restore.Run(Path.Combine(app, "Bwa.csproj"));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        RefreshClient? client = null;
        try
        {
            // No browser debugs the client, so only the refresh client is a page.
            window.ApplySettings(window.Settings with { DebugBrowser = Path.Combine(folder, "no-browser", "chrome") });
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Bwa: http"), "Bwa's http configuration", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bwa: http"));
            Ui.Launched(window, window.DebugAsync(), "Bwa to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, console), StringComparison.Ordinal);
            Assert.False(console().Contains("the browser is not kept up", StringComparison.Ordinal), $"Passenger keeps no page up:\n{console()}");
            int port = HeardPorts.Of(console, "the app");
            client = Ui.AwaitPumped(Waiting.Apart(() => RefreshClient.ConnectAsync(port)), "the page's refresh client to connect to Passenger through the app");

            window.OpenFile(Path.Combine(app, "wwwroot", "app.css"));
            Ui.Settle();
            BellApp.Edit(window, "html, body {", ".kept-up-in-place { color: rebeccapurple; }\n\nhtml, body {");
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Told(window, client, "UpdateStaticFile", "app.css", console);
            Assert.Contains("kept-up-in-place", Served(port, "/app.css"), StringComparison.Ordinal);
            Ui.WaitFor(() => console().Contains("Hot Reload: app.css applied in 1 page", StringComparison.Ordinal), () => $"the console to say where app.css went:\n{console()}");
        }
        finally
        {
            End(window, client);
        }
    }

    /// <summary>A page the app serves at a name under localhost (as an app made with <c>--localhost-tld</c> is opened) is this
    /// machine's: under debug its refresh client connects, and a stylesheet saved in the editor is applied in it.</summary>
    [AvaloniaFact]
    public void Under_debug_a_page_at_a_name_under_localhost_is_kept_up()
    {
        BellApp.Make(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        RefreshClient? client = null;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Bell: http"), "Bell's http configuration", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bell: http"));
            Ui.Launched(window, window.DebugAsync(), "Bell to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, console), StringComparison.Ordinal);
            int port = HeardPorts.Of(console, "the app");
            client = Ui.AwaitPumped(Waiting.Apart(() => RefreshClient.ConnectAsync(port, "bell.dev.localhost")),
                "the refresh client of a page at bell.dev.localhost to connect to Passenger through the app");

            window.OpenFile(Path.Combine(folder, "wwwroot", "app.css"));
            Ui.Settle();
            BellApp.Edit(window, "html, body {", ".kept-up-in-place { color: rebeccapurple; }\n\nhtml, body {");
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Told(window, client, "UpdateStaticFile", "app.css", console);
        }
        finally
        {
            End(window, client);
        }
    }

    /// <summary>A Razor class library Bell references, as the SDK's template makes one, with a stylesheet and a component's
    /// scoped CSS: under debug, its stylesheet saved in the editor is applied in place by the URL the app serves it at, and
    /// its scoped CSS another program wrote is bundled again by the library and applied, each served as written; deleted, the
    /// library's only sheet leaves none of its rules served.</summary>
    [AvaloniaFact]
    public void Under_debug_a_referenced_Razor_class_librarys_stylesheet_and_scoped_CSS_apply_in_place()
    {
        string bell = Path.Combine(folder, "Bell");
        BellApp.Make(bell);
        string kit = Path.Combine(folder, "Kit");
        Ui.AwaitPumped(Waiting.Apart(async () =>
        {
            ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(
                ["new", "razorclasslib", "--output", kit, "--name", "Kit", "--framework", TestProjects.Framework, "--support-pages-and-views", "--no-restore"]),
                limit: TimeSpan.FromMinutes(3));
            Assert.True(made.Succeeded, $"dotnet new razorclasslib did not make the test's library (exit code {made.ExitCode}):\n{made.Output}{made.Error}");
            return made;
        }), "dotnet new razorclasslib to make the test's library", TimeSpan.FromMinutes(3) + TestFolders.Spare);
        TestFolders.Write(kit, "wwwroot/kit.css", ".kit {\n    color: teal;\n}\n");
        TestFolders.Write(kit, "Banner.razor", "<div class=\"banner\">Kit</div>\n");
        string scoped = TestFolders.Write(kit, "Banner.razor.css", ".banner {\n    color: teal;\n}\n");
        TestFolders.Write(folder, "nuget.config", "<configuration>\n  <packageSources><clear /><add key=\"local\" value=\"Bell/feed\" /></packageSources>\n</configuration>\n");
        string project = Path.Combine(bell, "Bell.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
            "  <ItemGroup>\n    <ProjectReference Include=\"../Kit/Kit.csproj\" />\n  </ItemGroup>\n\n</Project>", StringComparison.Ordinal));
        Restore.Run(project);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        RefreshClient? client = null;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Bell: http"), "Bell's http configuration", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bell: http"));
            Ui.Launched(window, window.DebugAsync(), "Bell to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, console), StringComparison.Ordinal);
            int port = HeardPorts.Of(console, "the app");
            client = Ui.AwaitPumped(Waiting.Apart(() => RefreshClient.ConnectAsync(port)), "the page's refresh client to connect to Passenger through the app");

            window.OpenFile(Path.Combine(kit, "wwwroot", "kit.css"));
            Ui.Settle();
            BellApp.Edit(window, ".kit {", ".kit-in-place { color: rebeccapurple; }\n\n.kit {");
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Told(window, client, "UpdateStaticFile", "_content/Kit/kit.css", console);
            Assert.Contains("kit-in-place", Served(port, "/_content/Kit/kit.css"), StringComparison.Ordinal);
            Ui.WaitFor(() => console().Contains("Hot Reload: _content/Kit/kit.css applied in 1 page", StringComparison.Ordinal),
                () => $"the console to say where kit.css went:\n{console()}");

            File.AppendAllText(scoped, "\n.kit-scoped {\n    color: rebeccapurple;\n}\n");
            Told(window, client, "UpdateStaticFile", "_content/Kit/Kit.bundle.scp.css", console);
            Assert.Contains("kit-scoped", Served(port, "/_content/Kit/Kit.bundle.scp.css"), StringComparison.Ordinal);

            File.Delete(scoped);
            Told(window, client, "UpdateStaticFile", "_content/Kit/Kit.bundle.scp.css", console, times: 2);
            Assert.DoesNotContain("kit-scoped", Served(port, "/_content/Kit/Kit.bundle.scp.css"), StringComparison.Ordinal);
        }
        finally
        {
            End(window, client);
        }
    }

    /// <summary>A project that turns off what makes its pages' browser tools: debugged, it runs, and its console says its
    /// pages are not kept up, why, and what makes them.</summary>
    [AvaloniaFact]
    public void Where_the_build_makes_no_browser_tools_debug_says_why_and_what_makes_them()
    {
        BellApp.Make(folder);
        string project = Path.Combine(folder, "Bell.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</PropertyGroup>",
            "  <EnableHotReloadInRuntimeConfigDevFile>false</EnableHotReloadInRuntimeConfigDevFile>\n  </PropertyGroup>", StringComparison.Ordinal));
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Bell: http"), "Bell's http configuration", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bell: http"));
            Ui.Launched(window, window.DebugAsync(), "Bell to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, console), StringComparison.Ordinal);
            Assert.Contains("Hot Reload: the browser is not kept up: Bell's build made no browser tools for its pages, which the SDK makes in a build whose "
                + "EnableHotReloadInRuntimeConfigDevFile is true, as a Debug build's is unless the project sets it to false", console(), StringComparison.Ordinal);
        }
        finally
        {
            End(window, null);
        }
    }

    /// <summary>The app made on <paramref name="sdk"/> (the machine's own when null) and debugged, each change told to its
    /// page as the SDK's client hears it.</summary>
    private void KeptUp(string? sdk)
    {
        BellApp.Make(folder, sdk: sdk);
        MainWindow window = Ui.OpenFolder(folder);
        RefreshClient? client = null;
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == "Bell: http"), "Bell's http configuration", TimeSpan.FromMinutes(2));
            Assert.True(window.SelectConfiguration("Bell: http"));
            Ui.Launched(window, window.DebugAsync(), "Bell to start under the debugger", TimeSpan.FromMinutes(3));
            Func<string> console = () => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
            Assert.Contains("<h1>Counter</h1>", BellApp.Page(window, console), StringComparison.Ordinal);
            Assert.False(console().Contains("the browser is not kept up", StringComparison.Ordinal), $"Passenger keeps no page up:\n{console()}");
            int port = HeardPorts.Of(console, "the app");
            client = Ui.AwaitPumped(Waiting.Apart(() => RefreshClient.ConnectAsync(port)), "the page's refresh client to connect to Passenger through the app");

            window.OpenFile(Path.Combine(folder, "wwwroot", "app.css"));
            Ui.Settle();
            BellApp.Edit(window, "html, body {", ".kept-up-in-place { color: rebeccapurple; }\n\nhtml, body {");
            Assert.True(window.Perform(AppActions.Save), $"Save could not run: {window.Status}");
            Told(window, client, "UpdateStaticFile", "app.css", console);
            Assert.Contains("kept-up-in-place", Served(port, "/app.css"), StringComparison.Ordinal);
            Ui.WaitFor(() => console().Contains("Hot Reload: app.css applied in 1 page", StringComparison.Ordinal), () => $"the console to say where app.css went:\n{console()}");

            File.AppendAllText(Path.Combine(folder, "Components", "Layout", "MainLayout.razor.css"), "\n.kept-up-scoped {\n    color: rebeccapurple;\n}\n");
            Told(window, client, "UpdateStaticFile", "Bell.styles.css", console);
            Assert.Contains("kept-up-scoped", Served(port, "/Bell.styles.css"), StringComparison.Ordinal);

            File.Delete(Path.Combine(folder, "Components", "Layout", "MainLayout.razor.css"));
            Told(window, client, "UpdateStaticFile", "Bell.styles.css", console, times: 2);
            string bundled = Served(port, "/Bell.styles.css");
            Assert.DoesNotContain("kept-up-scoped", bundled, StringComparison.Ordinal);
            Assert.Contains("NavMenu.razor", bundled, StringComparison.Ordinal);

            string favicon = Path.Combine(folder, "wwwroot", "favicon.png");
            File.WriteAllBytes(favicon, [.. File.ReadAllBytes(favicon), 0]);
            Told(window, client, "UpdateStaticFile", "favicon.png", console);

            window.OpenFile(Path.Combine(folder, "Components", "Pages", "Counter.razor"));
            Ui.Settle();
            BellApp.Edit(window, "<h1>Counter</h1>", "<h1>Tally</h1>");
            Assert.True(window.Perform(AppActions.ApplyHotReloadChanges), $"Apply Hot Reload Changes could not run: {window.Status}");
            Told(window, client, "RefreshBrowser", null, console);
            Assert.True(window.Debugger.LastApplied is { Result: HotReloadResult.Applied }, $"The edit did not apply: {window.Debugger.LastApplied?.Said}");
        }
        finally
        {
            End(window, client);
        }
    }

    /// <summary>The page's refresh client closed, and the app's session ended with its window.</summary>
    private static void End(MainWindow window, RefreshClient? client)
    {
        if (client is not null)
        {
            Ui.AwaitPumped(Waiting.Apart(async () => { await client.DisposeAsync(); return true; }), "the page's refresh client to close");
        }
        window.Perform(AppActions.Stop);
        Ui.WaitFor(() => !window.Debugger.IsDebugging, "the session to end");
        window.Close();
    }

    /// <summary>Waits for the page to be told a message of <paramref name="type"/> (for a path, of it) so many times.</summary>
    private static void Told(MainWindow window, RefreshClient client, string type, string? path, Func<string> console, int times = 1) =>
        Ui.WaitFor(() => client.Heard(type, path, times), () => $"the page to be told {type} {path} {times} times ({window.Status}); it heard:\n{client.Said}\nThe console has:\n{console()}",
            TimeSpan.FromMinutes(2));

    /// <summary>A file the app serves, as it serves it now.</summary>
    private static string Served(int port, string path)
    {
        using HttpClient http = new();
        return Ui.Await(http.GetStringAsync(new Uri($"http://127.0.0.1:{port}{path}")), $"{path} from the app", TimeSpan.FromMinutes(1));
    }
}
