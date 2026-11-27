using Passenger.App;
using Passenger.App.Run.Devices;
using Passenger.Core;

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
