using System.IO.Compression;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Passenger.App;
using Passenger.App.Workspace;
using Passenger.Core;
using Passenger.Editor;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The language server's projects. It loads them from what Passenger's project host built of them at design
/// time, as its own build host would have built them, so it answers across a project reference; a folder opened again
/// has them loaded from those builds as kept, none built again while nothing they read changed. A file of a
/// multi-targeted project is answered for in the target framework chosen in the editor's switcher.</summary>
public sealed partial class LanguageServerProjectsTests : IClassFixture<RestoredFolders>, IDisposable
{
    private readonly string folder;
    private readonly string settings = TestFolders.Create("settings-");

    public LanguageServerProjectsTests(RestoredFolders folders)
    {
        string made = string.Empty;
        TestFolders.Prepare(() => made = folders.Take("builds-", root =>
        {
            Write(root, "Shapes.slnx", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
            Write(root, "Lib/Lib.csproj",
                $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFrameworks>{TestProjects.Framework};net11.0</TargetFrameworks>\n  </PropertyGroup>\n</Project>\n");
            Write(root, "Lib/Greeter.cs", """
                namespace Lib;

                public static class Greeter
                {
                    public static string Hello() => "Hello";
                #if NET11_0_OR_GREATER
                    public static int Newer() => Missing;
                #endif
                }

                """);
            Write(root, "App/App.csproj",
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n  <ItemGroup>\n    <ProjectReference Include=\"../Lib/Lib.csproj\" />\n  </ItemGroup>\n</Project>\n");
            Write(root, "App/Program.cs", "System.Console.WriteLine(Lib.Greeter.Hello() + Lib.Greeter.Absent());\n");
            // Restored at its real path: MSBuild resolves a reference from there, which would restore Lib twice at once.
            return PathNames.Canonical(Path.Combine(root, "Shapes.slnx"));
        }), settings);
        folder = PathNames.Canonical(made);
    }

    public void Dispose()
    {
        TestFolders.Delete(folder);
        TestFolders.Delete(settings);
    }

    /// <summary>A restore landing during a project's first design-time build, its assets dated before the build began (copied
    /// in with their times kept): the language server, given that build, hears of them and is given one made after them,
    /// which the folder opened again loads as kept.</summary>
    [AvaloniaFact]
    public void A_project_whose_packages_arrive_during_its_first_build_is_built_again_as_the_server_hears_of_them()
    {
        string root = PathNames.Canonical(TestFolders.Create("late-assets-"));
        try
        {
            string feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
            string package = Path.Combine(feed, "Passenger.RestoreTest.1.0.0.nupkg");
            File.WriteAllBytes(package, StandInFeed.Package("Passenger.RestoreTest", "1.0.0"));
            using (ZipArchive zip = ZipFile.Open(package, ZipArchiveMode.Update))
            {
                zip.CreateEntryFromFile(typeof(PathNames).Assembly.Location, "lib/net11.0/Passenger.Core.dll");
            }
            Write(root, "nuget.config", """
                <configuration>
                  <packageSources><clear /><add key="local" value="feed" /></packageSources>
                  <config><add key="globalPackagesFolder" value="packages" /></config>
                </configuration>
                """);
            Write(root, "App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <RestorePackagesPath>$(MSBuildProjectDirectory)/packages</RestorePackagesPath>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="Passenger.RestoreTest" Version="1.0.0" /></ItemGroup>
                </Project>
                """);
            Write(root, "Page.cs", "using Passenger.Core;\nclass Page { string Path() => PathNames.Canonical(Missing); }\n");
            Restore.Run(Path.Combine(root, "App.csproj"));
            string assets = Path.Combine(root, "obj", "project.assets.json");
            string prebuilt = Path.Combine(root, "assets.prebuilt.json");
            File.Move(assets, prebuilt);
            // The build's Copy keeps this time, so no file's time can say the assets came after the build read the project.
            File.SetLastWriteTimeUtc(prebuilt, DateTime.UtcNow - TimeSpan.FromMinutes(1));
            // The build goes on a while after its assets arrive, so the language server asks for it while it runs and is given it.
            int held = (int)Math.Ceiling(TimeSpan.FromSeconds(3).Scaled().TotalSeconds);
            Write(root, "Directory.Build.targets", $"""
                <Project>
                  <Target Name="AssetsArriveLate" BeforeTargets="CoreCompile"
                          Condition="'$(DesignTimeBuild)' == 'true' and !Exists('$(ProjectAssetsFile)')">
                    <Copy SourceFiles="$(MSBuildProjectDirectory)/assets.prebuilt.json" DestinationFiles="$(ProjectAssetsFile)" />
                    <Exec Condition="'$(OS)' != 'Windows_NT'" Command="sleep {held}" />
                    <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command Start-Sleep -Seconds {held}" />
                  </Target>
                </Project>
                """);

            string page = Path.Combine(root, "Page.cs");
            MainWindow first = Keeping(root);
            first.OpenFile(page);
            Ui.WaitFor(() => Resolved(first, page),
                () => $"the first window's language server to resolve the package whose assets arrived during its design-time build, told of them and given a build made after them, which the one made without them must not stand for: Page.cs has {string.Join(", ", Codes(first, page))}, after {first.Session.DesignTimeBuilds.Builds} design-time build(s)",
                TimeSpan.FromMinutes(2));
            Assert.True(File.Exists(assets), "The assets file did not arrive during the first design-time build");
            Assert.True(first.Session.DesignTimeBuilds.Builds == 2,
                $"The first window built the project {first.Session.DesignTimeBuilds.Builds} time(s) at design time, not once as its package's assets arrived and once more after them");
            first.Close();

            MainWindow second = Keeping(root);
            second.OpenFile(page);
            WaitForCode(second, page, "CS0103", "the error on Missing as the folder opened again loads the project");
            Assert.True(second.Session.DesignTimeBuilds.Builds == 0,
                $"Opened again with nothing changed, the project was built {second.Session.DesignTimeBuilds.Builds} time(s) rather than loaded from the build made after its assets arrived");
            Assert.True(Resolved(second, page), $"Opened again, the project was loaded from a build without its package: Page.cs has {string.Join(", ", Codes(second, page))}");
            second.Close();
        }
        finally
        {
            TestFolders.Delete(root);
        }
    }

    [Gate]
    [AvaloniaFact]
    public void A_folder_opened_with_builds_kept_before_restore_inputs_were_stamped_is_built_again_once()
    {
        string program = Path.Combine(folder, "App", "Program.cs");
        MainWindow first = Keeping();
        first.OpenFile(program);
        WaitForAbsent(first, program);
        first.Close();

        string cache = Assert.Single(Directory.GetFiles(settings, "projectBuilds.json", SearchOption.AllDirectories));
        JsonObject document = JsonNode.Parse(File.ReadAllText(cache))!.AsObject();
        document["version"] = 3;
        File.WriteAllText(cache, document.ToJsonString());

        MainWindow second = Keeping();
        second.OpenFile(program);
        WaitForAbsent(second, program);
        Assert.Equal(2, second.Session.DesignTimeBuilds.Builds);
        second.Close();

        MainWindow third = Keeping();
        third.OpenFile(program);
        WaitForAbsent(third, program);
        Assert.Equal(0, third.Session.DesignTimeBuilds.Builds);
        third.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_folder_opened_again_has_the_language_server_load_its_projects_from_the_design_time_builds_kept()
    {
        string program = Path.Combine(folder, "App", "Program.cs");
        MainWindow first = Keeping();
        first.OpenFile(program);
        // Greeter is Lib's: the server took App's reference to it from the host's build.
        WaitForAbsent(first, program);
        Assert.True(first.CSharpService!.Session!.Fed, "The language server built the projects itself rather than load the project host's builds");
        Assert.Equal(2, first.Session.DesignTimeBuilds.Builds);
        first.Close();

        MainWindow second = Keeping();
        second.OpenFile(program);
        WaitForAbsent(second, program);
        Assert.True(second.CSharpService!.Session!.Fed, "Opened again, the language server built the projects itself rather than load the builds kept");
        Assert.True(second.Session.DesignTimeBuilds.Builds == 0,
            $"Opened again with nothing changed, the folder had {second.Session.DesignTimeBuilds.Builds} project(s) built again rather than loaded from the builds kept");
        second.Close();
    }

    [Gate]
    [AvaloniaFact]
    public void A_directory_build_file_made_above_the_projects_reaches_the_language_server_made_while_the_folder_was_closed_or_open()
    {
        // Each error but the first compiled only under a constant a Directory.Build file above the projects defines.
        string counts = Path.Combine(folder, "App", "Counts.cs");
        File.WriteAllText(counts, "static class Counts\n{\n    static int None() => Lacking;\n#if SHAPES\n    static int Many() => \"many\";\n#endif\n#if MORE\n    static int More() => 1.5;\n#endif\n}\n");
        MainWindow first = Keeping();
        first.OpenFile(counts);
        WaitForCode(first, counts, "CS0103", "the server's error on Lacking, which it gives with nothing defined");
        Assert.DoesNotContain("CS0029", Codes(first, counts));
        first.Close();

        Write(folder, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);SHAPES</DefineConstants>\n  </PropertyGroup>\n</Project>\n");
        MainWindow second = Keeping();
        second.OpenFile(counts);
        WaitForCode(second, counts, "CS0029", "the error only SHAPES compiles, the Directory.Build.props made while the folder was closed reaching the server's builds rather than the builds kept");

        Write(folder, "Directory.Build.targets", "<Project>\n  <PropertyGroup>\n    <DefineConstants>$(DefineConstants);MORE</DefineConstants>\n  </PropertyGroup>\n</Project>\n");
        WaitForCode(second, counts, "CS0266", "the error only MORE compiles, the Directory.Build.targets made while the folder is open reaching the server");
        second.Close();
    }

    /// <summary>A file open as the language server starts again, or as the solution gains a project, reaches the new server
    /// only once it has said that file's project loaded, never one of its Miscellaneous Files meanwhile, though a program
    /// found beside the solution as it started is loaded beside the projects shown first; a file no project of the folder
    /// holds reaches it at once.</summary>
    [Gate]
    [AvaloniaFact]
    public void Files_open_as_the_server_starts_again_or_the_solution_gains_a_project_reach_it_once_their_projects_have_loaded()
    {
        string program = Path.Combine(folder, "App", "Program.cs");
        string greeter = Path.Combine(folder, "Lib", "Greeter.cs");
        string extra = Write(folder, "Extra/Extra.cs", "namespace Extra;\n\npublic static class Sum\n{\n}\n");
        Write(folder, "Extra/Extra.csproj", TestProjects.Library());
        Restore.Run(Path.Combine(folder, "Extra", "Extra.csproj"));
        MainWindow window = Keeping();
        window.ApplySettings(window.Settings with { TraceProtocols = true });
        try
        {
            window.OpenFile(program);
            window.OpenFile(extra);
            // Shown, Lib's file has Lib loaded first, App after it.
            window.OpenFile(greeter);
            int first = AllServed(window, 0, program, extra, greeter);

            // Ended: the server started again finds a program written beside the solution as it starts.
            Passenger.App.CSharp.CSharpService csharp = window.CSharpService!;
            HeldFeed held = new(csharp.Feed!);
            csharp.Feed = held;
            System.Diagnostics.Process.GetProcessById(first).Kill();
            ProgramFoundAsItStarts(window, held, "tool.cs");
            int again = AllServed(window, first, program, extra, greeter);
            string[] trace = Trace(again);
            List<string> early = Early(trace, (program, "App.csproj"), (greeter, "Lib.csproj"));
            Assert.True(early.Count == 0,
                $"The language server started again was given files before it loaded their projects, which it made Miscellaneous Files meanwhile: {string.Join("; ", early)}\n{Shown(trace)}");
            int extraOpened = Array.FindIndex(trace, line => Opens(line, extra));
            int firstLoad = Array.FindIndex(trace, line => Reports(line, ".csproj"));
            Assert.True(extraOpened >= 0 && extraOpened < firstLoad,
                $"Extra.cs, which no project of the solution held, was not given the server started again at once, before it loaded a project: {Shown(trace)}");

            // The solution gains Extra, and the server started for it finds another program written as it starts.
            HeldFeed? next = null;
            EventHandler hold = (_, _) =>
            {
                if (next is null && window.Session.CSharp is { Feed: { } feed } made)
                {
                    made.Feed = next = new HeldFeed(feed);
                }
            };
            window.Session.CSharpChanged += hold;
            try
            {
                // Replaced whole, so no read finds it half written.
                string written = Write(folder, "Shapes.slnx.new", "<Solution>\n  <Project Path=\"App/App.csproj\" />\n  <Project Path=\"Extra/Extra.csproj\" />\n  <Project Path=\"Lib/Lib.csproj\" />\n</Solution>\n");
                File.Move(written, Path.Combine(folder, "Shapes.slnx"), overwrite: true);
                Ui.PumpUntil(() => next is { Asked: true }, "a language server started for the solution with Extra to ask for its projects", TimeSpan.FromMinutes(1));
                ProgramFoundAsItStarts(window, next!, "more.cs");
            }
            finally
            {
                window.Session.CSharpChanged -= hold;
                next?.Release();
            }
            int gained = AllServed(window, again, program, extra, greeter);
            string[] grown = Trace(gained);
            early = Early(grown, (program, "App.csproj"), (extra, "Extra.csproj"), (greeter, "Lib.csproj"));
            Assert.True(early.Count == 0,
                $"The language server started for the solution with Extra was given files before it loaded their projects, which it made Miscellaneous Files meanwhile: {string.Join("; ", early)}\n{Shown(grown)}");
        }
        finally
        {
            window.ApplySettings(window.Settings with { TraceProtocols = false });
            window.Close();
        }
    }

    /// <summary>Writes a program beside the solution while a starting server's projects are held from it, and lets them go
    /// once the folder has found the program.</summary>
    private void ProgramFoundAsItStarts(MainWindow window, HeldFeed held, string name)
    {
        Ui.PumpUntil(() => held.Asked, "the language server starting to ask for the projects to load", TimeSpan.FromMinutes(1));
        string written = Write(folder, name, "#!/usr/bin/env dotnet\nSystem.Console.WriteLine(\"tool\");\n");
        Ui.PumpUntil(() => window.Session.Workspace?.Programs.Any(p => PathNames.Same(p.Path, written)) == true,
            $"{name}, written beside the solution, to be found as the language server starts", TimeSpan.FromMinutes(1));
        held.Release();
    }

    /// <summary>Waits for a language server other than <paramref name="before"/> to have loaded every project and been given
    /// each file; its process.</summary>
    private static int AllServed(MainWindow window, int before, params string[] files)
    {
        Ui.WaitFor(() => window.CSharpService is { Session: { IsLoaded: true } session } csharp && session.Server.ProcessId != before
            && files.All(f => window.EditorArea.Documents.FirstOrDefault(d => PathNames.Same(d.Path, f)) is { } open && csharp.For(open)?.Lsp is not null),
            $"a language server to load the projects and be given the open files ({window.CSharpService?.State})", TimeSpan.FromMinutes(3));
        return window.CSharpService!.Session!.Server.ProcessId;
    }

    /// <summary>What lsp.log holds of one server's messages, a line each.</summary>
    private static string[] Trace(int server)
    {
        Passenger.App.IdeErrors.Flush();
        return [.. Ui.Shared(Path.Combine(Passenger.App.IdeErrors.LogFolder, "lsp.log")).Split('\n').Where(line => line.Contains($"[server {server}] ", StringComparison.Ordinal))];
    }

    /// <summary>The files a server was told open before it said their projects loaded, each with its project's name.</summary>
    private static List<string> Early(string[] trace, params (string File, string Project)[] files)
    {
        List<string> early = [];
        foreach ((string file, string project) in files)
        {
            int opened = Array.FindIndex(trace, line => Opens(line, file));
            int loaded = Array.FindIndex(trace, line => Reports(line, project));
            if (opened < 0 || loaded < 0 || opened < loaded)
            {
                early.Add($"{Path.GetFileName(file)} told open at message {opened}, {project} said loaded at {loaded}");
            }
        }
        return early;
    }

    /// <summary>Whether a traced line tells the server a file is open.</summary>
    private static bool Opens(string line, string file) =>
        line.Contains("] --> ", StringComparison.Ordinal) && line.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.DidOpen}\"", StringComparison.Ordinal)
        && System.Text.RegularExpressions.Regex.Match(line, "\"uri\":\"([^\"]+)\"") is { Success: true } uri && PathNames.Same(new Uri(uri.Groups[1].Value).LocalPath, file);

    /// <summary>Whether a traced line is the server's word that a project's load ended, with problems or none.</summary>
    private static bool Reports(string line, string project) =>
        line.Contains("] <-- ", StringComparison.Ordinal) && line.Contains($"\"method\":\"{Passenger.Lsp.LspMethods.LogMessage}\"", StringComparison.Ordinal)
        && (line.Contains("Successfully completed load of", StringComparison.Ordinal) || line.Contains(" while loading ", StringComparison.Ordinal))
        && line.Contains(project, StringComparison.Ordinal);

    /// <summary>The didOpen and load lines of a trace, for a failure's message.</summary>
    private static string Shown(string[] trace) =>
        string.Join('\n', trace.Where(line => line.Contains(Passenger.Lsp.LspMethods.DidOpen, StringComparison.Ordinal) || Reports(line, ".csproj")).Select(line => line.Length > 300 ? line[..300] : line));

    /// <summary>The window's project feed with its first list of the projects to load held until the test lets it go, as a
    /// start whose design-time builds come slowly.</summary>
    private sealed class HeldFeed(Passenger.Lsp.IProjectFeed feed) : Passenger.Lsp.IProjectFeed
    {
        private readonly TaskCompletionSource asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Asked => asked.Task.IsCompleted;

        public void Release() => released.TrySetResult();

        public async Task<Passenger.Lsp.FedProjects?> ProjectsAsync(CancellationToken cancel)
        {
            asked.TrySetResult();
            await released.Task.WaitAsync(cancel);
            return await feed.ProjectsAsync(cancel);
        }

        public Task<Passenger.Lsp.FedProject> InfoAsync(string path, CancellationToken cancel) => feed.InfoAsync(path, cancel);
    }

    private static IEnumerable<string?> Codes(MainWindow window, string path) =>
        window.EditorArea.Documents.FirstOrDefault(d => d.Path == path) is { } open && window.CSharpService?.For(open) is { } served
            ? served.Diagnostics.Select(d => d.Code?.ToString())
            : [];

    /// <summary>Whether Page.cs has the server's error on Missing and none from the package's PathNames, which resolved.</summary>
    private static bool Resolved(MainWindow window, string page)
    {
        List<string?> codes = [.. Codes(window, page)];
        return codes.Contains("CS0103") && !codes.Contains("CS0246") && !codes.Contains("CS0234");
    }

    private static void WaitForCode(MainWindow window, string path, string code, string what) =>
        Ui.WaitFor(() => Codes(window, path).Contains(code), what, TimeSpan.FromMinutes(2));

    [Gate]
    [AvaloniaFact]
    public void A_file_of_a_multi_targeted_project_is_answered_for_in_the_target_framework_chosen_in_the_editor()
    {
        string greeter = Path.Combine(folder, "Lib", "Greeter.cs");
        MainWindow window = Keeping();
        window.OpenFile(greeter);
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => window.CSharpService?.IsReady == true && document.ProjectContexts.Count == 2,
            "the server to say Greeter.cs is compiled for both of Lib's target frameworks", TimeSpan.FromMinutes(2));
        Assert.Equal([$"Lib ({TestProjects.Framework})", "Lib (net11.0)"], document.ProjectContexts.Order(StringComparer.Ordinal));
        Assert.Equal(document.ProjectContext![5..^1], editor.ContextLabel);

        // Only net11.0 compiles the member that names what does not exist.
        Choose(window, editor, "Lib (net11.0)");
        Assert.Equal("net11.0", editor.ContextLabel);
        Ui.WaitFor(() => Missing(window, document), "the error on Missing, which only net11.0 compiles");
        Choose(window, editor, $"Lib ({TestProjects.Framework})");
        Assert.Equal(TestProjects.Framework, editor.ContextLabel);
        Ui.WaitFor(() => !Missing(window, document) && window.CSharpService!.For(document)!.Diagnostics.Count == 0,
            $"the error on Missing to go, {TestProjects.Framework} compiling none of it");
        window.Close();
    }

    /// <summary>The file's own parse (its brackets' pairs, its folds, what Safe Delete reads) takes the symbols of the
    /// framework chosen in the editor: the member only net11.0 compiles is disabled text to it in the other framework and
    /// code in net11.0.</summary>
    [AvaloniaFact]
    public void A_multi_targeted_files_own_parse_takes_the_symbols_of_the_target_framework_chosen_in_the_editor()
    {
        string greeter = Path.Combine(folder, "Lib", "Greeter.cs");
        MainWindow window = Keeping();
        window.OpenFile(greeter);
        OpenDocument document = window.EditorArea.Active!;
        CodeEditor editor = window.EditorArea.CodeEditor;
        Ui.WaitFor(() => window.CSharpService?.IsReady == true && document.ProjectContexts.Count == 2,
            "the server to say Greeter.cs is compiled for both of Lib's target frameworks", TimeSpan.FromMinutes(2));

        Choose(window, editor, "Lib (net11.0)");
        Ui.WaitFor(() => !ParsedDisabled(document, "Missing;"), "Newer, which only net11.0 compiles, parsed as code with net11.0 chosen");
        Choose(window, editor, $"Lib ({TestProjects.Framework})");
        Ui.WaitFor(() => ParsedDisabled(document, "Missing;"), $"Newer, which {TestProjects.Framework} never compiles, parsed as disabled text with {TestProjects.Framework} chosen");
        window.Close();
    }

    /// <summary>Whether the file's own parse colours the code at <paramref name="code"/> as disabled, whatever the server's colours.</summary>
    private static bool ParsedDisabled(OpenDocument document, string code)
    {
        Passenger.Text.TextSnapshot text = document.Text.Snapshot;
        int at = text.ToString().IndexOf(code, StringComparison.Ordinal);
        Passenger.Text.LineSpan line = text.GetLine(text.GetLineFromOffset(at));
        return document.Syntax!.GetSpans(text, line.Start, line.EndIncludingBreak - line.Start).Any(s => s.Kind == TokenKind.Excluded && s.Start <= at && s.End > at);
    }

    /// <summary>Clicks the editor's switcher and the framework in the menu it opens.</summary>
    private static void Choose(MainWindow window, CodeEditor editor, string context)
    {
        Rect bounds = editor.ContextSwitcherBounds;
        Assert.True(bounds.Width > 0, "The editor shows no target framework switcher for a file compiled for two");
        Ui.Click(editor, bounds.Center);
        Ui.Choose(Assert.IsType<ContextMenu>(window.OpenContextMenu), context);
    }

    [Gate]
    [AvaloniaFact]
    public void Leaving_a_folder_whose_projects_are_still_being_built_lets_the_next_folders_builds_start_at_once()
    {
        // A project whose design-time build waits three minutes, as a large solution's round goes on for minutes.
        string slow = PathNames.Canonical(TestFolders.Create("slow-"));
        string quick = PathNames.Canonical(TestFolders.Create("quick-"));
        try
        {
            Write(slow, "Slow/Slow.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            Write(slow, "Slow/Slow.cs", "class Slow\n{\n}\n");
            Write(slow, "Directory.Build.targets", """
                <Project>
                  <Target Name="Wait" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                    <Exec Condition="'$(OS)' != 'Windows_NT'" Command="echo begun &gt; built.started; sleep 180" />
                    <Exec Condition="'$(OS)' == 'Windows_NT'" Command="powershell -NoProfile -Command &quot;Set-Content -Path built.started -Value begun; Start-Sleep -Seconds 180&quot;" />
                  </Target>
                </Project>
                """);
            Write(quick, "Quick/Quick.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            Write(quick, "Quick/Quick.cs", "class Quick\n{\n}\n");
            Restore.Run(Path.Combine(slow, "Slow", "Slow.csproj"));
            Restore.Run(Path.Combine(quick, "Quick", "Quick.csproj"));

            MainWindow window = new() { Width = 1200, Height = 800 };
            window.Show();
            window.OpenFolder(slow);
            // Pumped, not settled: the language server's requests about the open file wait for its load, as long as the build.
            Ui.PumpUntil(() => File.Exists(Path.Combine(slow, "Slow", "built.started")), "the slow project's design-time build to begin", TimeSpan.FromMinutes(2));

            // The host builds one round at a time: the slow folder's must end as it is left, or the next waits minutes.
            window.OpenFolder(quick);
            Ui.PumpUntil(() => window.Session.DesignTimeBuilds.Builds == 1,
                "the next folder's project built at design time, which waits behind the round of the folder left unless that round ended",
                TimeSpan.FromSeconds(60));
            window.Close();
        }
        finally
        {
            TestFolders.Delete(slow);
            TestFolders.Delete(quick);
        }
    }

    /// <summary>A folder deleted while the project host builds its project at design time ends the host, which every
    /// window shares, as its window closes; another window's round, waiting in that host behind the first, is built by a
    /// host started anew and its project loads. Windows will not delete a folder a process works in.</summary>
    [AvaloniaFact]
    public void A_folder_deleted_under_its_design_time_build_leaves_another_windows_project_to_be_built_and_loaded()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows will not delete a folder a process works in");
        BehindAHeldBuild((gone, first, other) =>
        {
            TestFolders.Delete(gone);
            first.Close();
            Assert.True(Ui.Pumped(() => other.Session.DesignTimeBuilds.Builds == 1, TimeSpan.FromMinutes(1)),
                $"The other window's project was not built at design time once the host its round waited in had ended:\n{HostLines()}");
            Ui.WaitFor(() => other.CSharpService?.Session is { IsLoaded: true }, "the other window's language server to load its project");
            Assert.DoesNotContain("problems loading", Ui.ServerLogTail(), StringComparison.Ordinal);
        });
    }

    /// <summary>A folder deleted while the project host builds its project at design time, its window left open: the
    /// window sees the folder go and ends the host, which every window shares, so another window's round, waiting in that
    /// host behind the first, is built by a host started anew and its project loads. Windows will not delete a folder a
    /// process works in.</summary>
    [AvaloniaFact]
    public void A_folder_deleted_under_its_design_time_build_never_holds_another_windows_project_while_its_window_stays_open()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows will not delete a folder a process works in");
        BehindAHeldBuild((gone, first, other) =>
        {
            TestFolders.Delete(gone);
            Assert.True(Ui.Pumped(() => other.Session.DesignTimeBuilds.Builds == 1, TimeSpan.FromMinutes(1)),
                $"The other window's project was not built at design time while the window whose folder was deleted under its build stayed open:\n{HostLines()}");
            Ui.WaitFor(() => other.CSharpService?.Session is { IsLoaded: true }, "the other window's language server to load its project");
            Assert.True(first.Session.Folder == gone, $"The window whose folder was deleted no longer has it open, but {first.Session.Folder ?? "none"}");
            Assert.DoesNotContain("problems loading", Ui.ServerLogTail(), StringComparison.Ordinal);
        });
    }

    /// <summary>Two windows on folders of one project each, sharing a project host: the first's project held in its
    /// design-time build until a file the build wrote in its folder goes, the second's round waiting in the host behind
    /// it; then what <paramref name="then"/> does (given the first's folder), both windows closed and the folders deleted
    /// after.</summary>
    private static void BehindAHeldBuild(Action<string, MainWindow, MainWindow> then)
    {
        string gone = PathNames.Canonical(TestFolders.Create("gone-"));
        string kept = PathNames.Canonical(TestFolders.Create("kept-"));
        MainWindow? first = null;
        MainWindow? second = null;
        try
        {
            const string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net11.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";
            Write(gone, "Gone/Gone.csproj", project);
            Write(gone, "Gone/Gone.cs", "class Gone\n{\n}\n");
            Write(gone, "Directory.Build.targets", """
                <Project>
                  <Target Name="Hold" BeforeTargets="CoreCompile" Condition="'$(DesignTimeBuild)' == 'true'">
                    <Exec Command="echo held &gt; held; while [ -f held ]; do sleep 0.1; done" />
                  </Target>
                </Project>
                """);
            Write(kept, "Kept/Kept.csproj", project);
            Write(kept, "Kept/Kept.cs", "class Kept\n{\n}\n");
            Restore.Run(Path.Combine(gone, "Gone", "Gone.csproj"));
            Restore.Run(Path.Combine(kept, "Kept", "Kept.csproj"));

            first = new() { Width = 1200, Height = 800 };
            first.Show();
            first.OpenFolder(gone);
            // Pumped, not settled: the language server's load waits for the design-time build.
            Ui.PumpUntil(() => File.Exists(Path.Combine(gone, "Gone", "held")), "the design-time build of the folder to be deleted to begin", TimeSpan.FromMinutes(2));
            ProjectHost host = Ui.AwaitPumped(Waiting.Apart(() => ProjectHosts.ForAsync(gone)), "the folder's project host");
            second = new() { Width = 1200, Height = 800 };
            second.Show();
            second.OpenFolder(kept);
            MainWindow other = second;
            Ui.PumpUntil(() => host.Building == 2 && other.Session.DesignTimeBuilds.Awaited == 1,
                "the other window's language server to wait for its project's builds, their round waiting in the host behind the first", TimeSpan.FromMinutes(2));
            then(gone, first, second);
        }
        finally
        {
            second?.Close();
            first?.Close();
            TestFolders.Delete(gone);
            TestFolders.Delete(kept);
        }
    }

    /// <summary>The IDE's last words on the project hosts, but for what a host wrote itself.</summary>
    private static string HostLines() =>
        string.Join('\n', Ui.IdeLog().Split('\n').Where(l => l.Contains("project host", StringComparison.OrdinalIgnoreCase) && !HostSaid().IsMatch(l)).TakeLast(6));

    /// <summary>A line the project host wrote to its error stream, as the IDE's log copies it.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"Project host \(process \d+\): ")]
    private static partial System.Text.RegularExpressions.Regex HostSaid();

    private static bool Missing(MainWindow window, OpenDocument document) =>
        window.CSharpService?.For(document)?.Diagnostics.Any(d => d.Code?.ToString() == "CS0103" && d.Message.Contains("Missing", StringComparison.Ordinal)) == true;

    private MainWindow Keeping(string? root = null)
    {
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = settings };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(root ?? Path.Combine(folder, "Shapes.slnx"));
        Ui.Settle();
        return window;
    }

    /// <summary>Waits for the server's error on the member Greeter lacks, which it can give only knowing Lib's Greeter.</summary>
    private static void WaitForAbsent(MainWindow window, string program) =>
        Ui.WaitFor(() => window.EditorArea.Documents.FirstOrDefault(d => d.Path == program) is { } open && window.CSharpService?.For(open)?.Diagnostics.Any(d => d.Code?.ToString() == "CS0117" && d.Message.Contains("Absent", StringComparison.Ordinal)) == true,
            "the server's error on Greeter.Absent, which it gives only knowing Lib's Greeter", TimeSpan.FromMinutes(2));

    private static string Write(string root, string relative, string text) => TestFolders.Write(root, relative, text);
}
