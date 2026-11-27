using Passenger.App;
using Passenger.App.Debugging;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Inspector;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, as one head alone (its Android, iOS or Mac Catalyst head): the template's other heads
/// taken out of its frameworks, so its restore and build need that platform's workload and no other, restored from
/// nuget.org, as no restore of the solution's brings MAUI's mobile packages (so a test of it is never one of the gate's). The
/// template's main page has a label "Hello, World!" and a button named CounterBtn, "Click me", which counts its clicks.</summary>
internal static class MauiApp
{
    /// <summary>The app's project, run configuration and Android application id (its iOS bundle identifier too).</summary>
    public const string Name = "MauiApp1";

    public const string Package = "com.companyname.mauiapp1";

    /// <summary>The framework the Android head is built for: the first .NET whose Android apps run on CoreCLR.</summary>
    public static readonly string Android = $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-android";

    /// <summary>The framework the iOS head is built for: the first .NET whose iOS apps run on CoreCLR.</summary>
    public static readonly string Ios = $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-ios";

    /// <summary>The framework the Mac Catalyst head is built for, of the same .NET as the others.</summary>
    public static readonly string MacCatalyst = $"net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-maccatalyst";

    /// <summary>The longest dotnet new and the restore may each take.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(6);

    /// <summary>Makes the app in a folder of its own under <paramref name="folder"/>, its Android head alone, and restores it:
    /// its project, and why it could not be made or restored here (no MAUI workload, no reach of nuget.org), or null.</summary>
    public static Task<(string Project, string? Unmade)> CreateAndroidHeadAsync(string folder) => CreateHeadAsync(folder, Android);

    /// <summary>The app's iOS head alone, made and restored as <see cref="CreateAndroidHeadAsync"/> makes its Android head.</summary>
    public static Task<(string Project, string? Unmade)> CreateIosHeadAsync(string folder) => CreateHeadAsync(folder, Ios);

    /// <summary>The app's Mac Catalyst head alone, made and restored as <see cref="CreateAndroidHeadAsync"/> makes its Android head.</summary>
    public static Task<(string Project, string? Unmade)> CreateMacCatalystHeadAsync(string folder) => CreateHeadAsync(folder, MacCatalyst);

    /// <summary>The app with <paramref name="framework"/> alone in place of the template's first framework and those its
    /// conditions add, restored.</summary>
    private static async Task<(string Project, string? Unmade)> CreateHeadAsync(string folder, string framework)
    {
        string app = Path.Combine(folder, Name);
        string project = Path.Combine(app, $"{Name}.csproj");
        ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["new", "maui", "--output", app, "--name", Name, "--no-restore"], folder), limit: Limit);
        if (!made.Succeeded)
        {
            return (project, $"This .NET makes no .NET MAUI app, so it lacks MAUI's workload (dotnet workload install maui): {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        string[] lines = File.ReadAllLines(project);
        Assert.True(lines.Count(l => l.Trim() == $"<TargetFrameworks>{Android}</TargetFrameworks>") == 1,
            $"The template's project no longer names {Android} alone first among its frameworks:\n{string.Join('\n', lines)}");
        File.WriteAllLines(project, lines.Where(l => !l.TrimStart().StartsWith("<TargetFrameworks Condition=", StringComparison.Ordinal))
            .Select(l => l.Trim() == $"<TargetFrameworks>{Android}</TargetFrameworks>" ? l.Replace(Android, framework, StringComparison.Ordinal) : l));
        ChildResult restored = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], app), limit: Limit);
        return (project, restored.Succeeded ? null
            : $"{Name}'s {framework} head does not restore here from nuget.org: {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
    }

    /// <summary>The main page's XAML edited while the app runs with Hot Reload: its label's text, which MAUI patches into the
    /// open page, then a label of a C# expression of the button's text, for which the page is made again; each read back
    /// from the app.</summary>
    public static void TakesXamlEdits(MainWindow window, LiveApp app, string project, TimeSpan limit)
    {
        window.OpenFile(Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml"));
        Typing.Replace(window, "Text=\"Hello, World!\"", "Text=\"Hello, Passenger!\"");
        XamlApplied(window, "a property's edit", limit);
        Assert.False(window.Debugger.LastApplied!.Said.Contains("made again", StringComparison.Ordinal),
            $"The page was made again for a property's edit, which MAUI patches: {window.Debugger.LastApplied.Said}");
        Shows(window, app, "Hello, Passenger!", "the edited label's text");
        Typing.Replace(window, "HorizontalOptions=\"Fill\" />", "HorizontalOptions=\"Fill\" />\n\n<Label Text=\"{CounterBtn.Text + '!'}\" />");
        XamlApplied(window, "an element with a C# expression of the button's text", limit);
        Assert.True(window.Debugger.LastApplied!.Said.Contains("MainPage made again", StringComparison.Ordinal),
            $"The page was not made again for a C# expression, which MAUI's patch passes over: {window.Debugger.LastApplied.Said}");
        ShowsButtonsText(window, app, "!", "the added label whose C# expression reads the button's text", "Click me");
    }

    /// <summary>Breakpoints set in the main page's XAML: on the Hello label's line, and on the button's Text attribute; the
    /// file, and those lines (from zero).</summary>
    public static (string Xaml, int Hello, int ClickMe) XamlBreakpoints(MainWindow window, string project)
    {
        string xaml = Path.Combine(PathNames.Canonical(Path.GetDirectoryName(project)!), "MainPage.xaml");
        string[] written = File.ReadAllLines(xaml);
        int hello = Array.FindIndex(written, l => l.Contains("Text=\"Hello, World!\"", StringComparison.Ordinal)) - 1;
        int clickMe = Array.FindIndex(written, l => l.Contains("Text=\"Click me\"", StringComparison.Ordinal));
        window.OpenFile(xaml);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(hello, written[hello].IndexOf('<', StringComparison.Ordinal)));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(clickMe, written[clickMe].IndexOf("Text=", StringComparison.Ordinal) + 1));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        return (xaml, hello, clickMe);
    }

    /// <summary>The program, going on, stops at the label's line of the page's XAML where the label is made, the label in its
    /// locals; Step Over goes on to the next element the page makes; Continue stops at the button's Text attribute.</summary>
    public static void StopsInXaml(MainWindow window, string xaml, int hello, int clickMe, TimeSpan limit)
    {
        XamlStop(window, xaml, hello, "the Hello label's line, where the label is made", limit);
        Ui.WaitFor(() => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == "label")?.Value is { } made && made.Contains("Label", StringComparison.Ordinal),
            () => $"the stop's locals to show the label made; they show {string.Join(", ", window.BottomPanel.Debug.Tree.Rows.Where(r => r.Depth == 0).Select(r => $"{r.Name} = {r.Value}"))}", limit);
        Ui.Run(window, AppActions.StepOver);
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames is [{ Source.Path: { } path, Line: int line }, ..]
                && PathNames.Same(path, xaml) && line != hello + 1,
            () => $"Step Over to the next element MainPage.xaml makes, at {Top(window)}: {Ui.Said(window)}", limit);
        Ui.Run(window, AppActions.Resume);
        XamlStop(window, xaml, clickMe, "the button's Text attribute, where its text is set", limit);
    }

    /// <summary>Waits for the program to stop at a line (from zero) of a XAML file, the stop shown there.</summary>
    private static void XamlStop(MainWindow window, string xaml, int line, string what, TimeSpan limit) =>
        Ui.WaitFor(() => window.Debugger.Session?.State == DebugState.Paused && window.BottomPanel.Debug.Frames is [{ Source.Path: { } path, Line: int at }, ..]
                && PathNames.Same(path, xaml) && at == line + 1,
            () => $"the stop at {what}, line {line + 1} of {Path.GetFileName(xaml)}, shown at {Top(window)}: {Ui.Said(window)}", limit);

    /// <summary>Where the stop shown is, for a failure's words.</summary>
    public static string Top(MainWindow window) =>
        window.BottomPanel.Debug.Frames is [var top, ..] ? $"{top.Name} {top.Source?.Path}:{top.Line}" : "no frame";

    /// <summary>Apply Hot Reload Changes for a XAML edit, which the app takes: MAUI patched its open page, or it was made again.</summary>
    public static void XamlApplied(MainWindow window, string what, TimeSpan limit)
    {
        HotReloadOutcome? before = window.Debugger.LastApplied;
        Ui.Run(window, AppActions.ApplyHotReloadChanges);
        Ui.WaitFor(() => window.Debugger.LastApplied != before, () => $"Hot Reload to say what it made of {what}: {Ui.Said(window)}", limit);
        HotReloadOutcome applied = window.Debugger.LastApplied!;
        Assert.True(applied.Result == HotReloadResult.Applied && applied.Said.Contains("XAML changes applied: MainPage.xaml (MainPage", StringComparison.Ordinal),
            $"The app did not take {what}: {applied.Result}, {applied.Said}");
    }

    /// <summary>Waits until an element of the app shows <paramref name="text"/>, its type or a property's value as <paramref name="with"/> says.</summary>
    public static void Shows(MainWindow window, LiveApp app, string text, string what, (string Property, string Value)? with = null)
    {
        AppTree tree = new([]);
        string[] asked = with is { Property: not "Type" } property ? [property.Property] : [];
        Ui.WaitFor(() => AppTree.Walk((tree = Ui.AwaitPumped(app.AgentTreeAsync(asked), "the app's tree with what each element shows", TimeSpan.FromMinutes(1))).Roots)
            .Any(e => e.Element.Text == text && (with is not { } also || (also.Property == "Type" ? e.Element.Type : e.Element.Values?.GetValueOrDefault(also.Property)) == also.Value)),
            () => $"{what} to show \"{text}\" in the app, which shows {Shown(tree)} after Hot Reload said {window.Debugger.LastApplied?.Said} of\n"
                + window.EditorArea.CodeEditor.Document.Snapshot, TimeSpan.FromSeconds(30));
    }

    /// <summary>The page's button, as the app's tree has it now.</summary>
    public static Element Button(LiveApp app) =>
        AppTree.Walk(Ui.AwaitPumped(app.TreeAsync(), "the app's tree", TimeSpan.FromMinutes(1)).Roots).Select(e => e.Element).First(e => e is { Type: "Button", Name: "CounterBtn" });

    /// <summary>Waits until a label of the app shows the button's text (<paramref name="shown"/> where named), then <paramref name="after"/>.</summary>
    public static void ShowsButtonsText(MainWindow window, LiveApp app, string after, string what, string? shown = null)
    {
        AppTree tree = new([]);
        Ui.WaitFor(() =>
        {
            tree = Ui.AwaitPumped(app.AgentTreeAsync([]), "the app's tree with what each element shows", TimeSpan.FromMinutes(1));
            string? button = AppTree.Walk(tree.Roots).Select(e => e.Element).FirstOrDefault(e => e is { Type: "Button", Name: "CounterBtn" })?.Text;
            return button is not null && (shown is null || button == shown) && AppTree.Walk(tree.Roots).Any(e => e.Element is { Type: "Label" } label && label.Text == button + after);
        }, () => $"{what}: the app shows {Shown(tree)} after Hot Reload said {window.Debugger.LastApplied?.Said}", TimeSpan.FromSeconds(30));
    }

    /// <summary>What each element of a tree is and shows, for a failure's words.</summary>
    private static string Shown(AppTree tree) =>
        string.Join(", ", AppTree.Walk(tree.Roots).Select(e => $"{e.Element.Type} {e.Element.Text} {string.Join(' ', e.Element.Values ?? new Dictionary<string, string>())}".TrimEnd()).Take(40));

    /// <summary>A device test project a .NET template makes (<paramref name="template"/>: androidtest, iostest, maccatalysttest) in a folder
    /// of its own under <paramref name="folder"/>, restored from nuget.org: its project, and why it could not be made or
    /// restored here (no workload of its platform, no reach of nuget.org), or null. Its three tests pass, fail and skip.</summary>
    public static async Task<(string Project, string? Unmade)> CreateDeviceTestsAsync(string folder, string template)
    {
        string tests = Path.Combine(folder, DeviceTests);
        string project = Path.Combine(tests, $"{DeviceTests}.csproj");
        ChildResult made = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["new", template, "--output", tests, "--name", DeviceTests], folder), limit: Limit);
        if (!made.Succeeded)
        {
            return (project, $"This .NET has no {template} template, so it lacks its platform's workload: {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        ChildResult restored = await ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["restore", project], tests), limit: Limit);
        return (project, restored.Succeeded ? null
            : $"{DeviceTests} does not restore here from nuget.org: {restored.Output.Split('\n').FirstOrDefault(l => l.Contains(": error ", StringComparison.Ordinal))?.Trim() ?? restored.FirstLine}");
    }

    /// <summary>A device test project's name, its run configuration's too.</summary>
    public const string DeviceTests = "DeviceTests";

    /// <summary>What the Tests window says once a device test project's three tests have run, its time left out.</summary>
    public const string DeviceTestsRan = "Tests passed: 1, failed: 1, skipped: 1 of 3";

    /// <summary>A device test project's tests debugged from the Tests window where its project runs: its first test, empty as
    /// the template makes it, given a local and a breakpoint after it, the run stopping there with the local shown, then going
    /// on (past the stops the failing and skipped tests' exceptions may make) to its end, each test told. The window has the
    /// project open, its target chosen.</summary>
    public static void DebugDeviceTests(MainWindow window, string project, string where, TimeSpan limit)
    {
        string tests = PathNames.Canonical(Path.Combine(Path.GetDirectoryName(project)!, "Test1.cs"));
        string text = File.ReadAllText(tests);
        System.Text.RegularExpressions.Match empty = System.Text.RegularExpressions.Regex.Match(text, @"TestMethod1\s*\(\s*\)\s*\{\s*\}");
        Assert.True(empty.Success, $"The template's first test is no longer empty:\n{text}");
        text = string.Concat(text.AsSpan(0, empty.Index), "TestMethod1()\n    {\n        int answer = 6 * 7;\n        Console.WriteLine(answer);\n    }",
            text.AsSpan(empty.Index + empty.Length));
        File.WriteAllText(tests, text);
        int after = text.AsSpan(0, text.IndexOf("Console.WriteLine(answer);", StringComparison.Ordinal)).Count('\n');
        Ui.WaitFor(() => window.Tests.Tree.Methods.Any(m => m.Name == "TestMethod1"), () => $"the template's tests read from its code: {Ui.Said(window)}", limit);
        window.OpenFile(tests);
        Ui.Settle();
        window.EditorArea.CodeEditor.GoTo(new Passenger.Text.TextPosition(after, 8));
        Ui.Run(window, AppActions.ToggleBreakpoint);
        Task<string?> debugged = window.Tests.RunAsync(window.Folder!, debug: true);
        Ui.WaitFor(() => window.Debugger.Session?.State == Passenger.App.Debugging.DebugState.Paused && Shown(window, "answer") is not null,
            () => $"the stop at the breakpoint in TestMethod1 {where}; the debugger is {window.Debugger.Session?.State.ToString() ?? "not debugging"}: "
                + $"{window.BottomPanel.Tests.Status}\n{Ui.Said(window)}\n{DebugConsole(window)}", limit);
        Assert.Equal("42", Shown(window, "answer"));
        for (int stops = 0; stops < 4 && !debugged.IsCompleted; stops++)
        {
            Ui.Run(window, AppActions.Resume);
            Ui.WaitFor(() => debugged.IsCompleted || window.Debugger.Session?.State == Passenger.App.Debugging.DebugState.Paused,
                () => $"the debugged run {where} to go on to its end: {window.BottomPanel.Tests.Status}\n{DebugConsole(window)}", limit);
        }
        Ui.Launched(window, debugged, $"the debugged run's end {where}", limit);
        Assert.True(System.Text.RegularExpressions.Regex.Replace(window.BottomPanel.Tests.Status, $" in {SaidWhile.Pattern}$", string.Empty) == DeviceTestsRan,
            $"The debugged tests {where} were not each told: {window.BottomPanel.Tests.Status}\n{DebugConsole(window)}");
        Ui.WaitFor(() => !window.Debugger.IsDebugging, $"the debug session {where} to end with the run", TimeSpan.FromMinutes(1));

        static string? Shown(MainWindow window, string name) => window.BottomPanel.Debug.Tree.Rows.FirstOrDefault(r => r.Depth == 0 && r.Name == name)?.Value;

        static string DebugConsole(MainWindow window) => window.BottomPanel.Debug.Console.Document.Snapshot.ToString();
    }

    /// <summary>The device or emulator to run on, of those listed: the one adb's ANDROID_SERIAL names, as adb itself takes
    /// it where several are connected, else the first running, else the first emulator to start; null for none.</summary>
    public static DeviceTarget? Target(IReadOnlyList<DeviceTarget> targets) =>
        (Environment.GetEnvironmentVariable("ANDROID_SERIAL") is { Length: > 0 } serial ? targets.FirstOrDefault(t => t.Running && t.Id == serial) : null)
        ?? targets.FirstOrDefault(t => t.Running) ?? targets.FirstOrDefault(t => t.Kind == TargetKind.Emulator);
}
