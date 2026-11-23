using System.Reflection;
using System.Text.RegularExpressions;

namespace Passenger.Tests;

/// <summary>What a class marked [SameOnEverySystem] may run, read from the built tests: the mark keeps it to one system's
/// report lane, so nothing it runs may behave otherwise on another, the C# language server, the project host's reads, the
/// language servers whose answers it reads and what its own thread allocates under the headless platform counted alike
/// everywhere. What it reaches (<see cref="TestReach"/>) is held: a call out of the tests to <see cref="Differing"/>, a
/// helper of theirs to <see cref="DifferingHelpers"/>, the actions they name to <see cref="Starting"/>, its own words to
/// naming no system and no Godot project's file. What the IL cannot show, what its windows ran, the mark itself holds
/// (<see cref="SameOnEverySystemAttribute"/>).</summary>
[Collection(Beside.Name)]
public sealed partial class SameOnEverySystemRulesTests
{
    /// <summary>Calls whose work differs by system, by the start of the called member's full name (Type::Member), and why.</summary>
    private static readonly (string Called, string Why)[] Differing =
    [
        ("System.Diagnostics.Process", "starts or reads a process"),
        ("Passenger.Core.ChildProcesses", "starts a process"),
        ("Passenger.Core.Programs", "finds a program on the system"),
        ("Passenger.Core.Dotnet", "finds .NET as the system has it"),
        ("Passenger.Core.DotnetProcesses", "reads the system's processes"),
        ("Passenger.Terminal.", "runs the terminal"),
        ("Passenger.App.Terminal", "runs the terminal"),
        ("Passenger.Dap.", "runs the debugger"),
        ("Passenger.Debugger.", "runs the debugger"),
        ("Passenger.DebugAdapter.", "runs the debugger"),
        ("Passenger.App.Debugging.Debug", "runs the debugger"),
        ("Passenger.App.Debugging.InProcessAdapter", "runs the debugger"),
        ("Passenger.App.Debugging.WebAssemblyAdapter", "runs the debugger"),
        ("Passenger.App.Debugging.Browser", "runs a browser"),
        ("Passenger.App.Debugging.HotReload", "runs Hot Reload"),
        ("Passenger.App.Debugging.UpdateHandlerHook", "runs Hot Reload"),
        ("Passenger.Profiler.", "profiles a process"),
        ("Passenger.App.BuildRunController", "builds and runs"),
        ("Passenger.Packages.", "restores or reads NuGet's packages"),
        ("Passenger.Vcs.", "runs Git"),
        ("Passenger.App.Vcs.", "runs Git"),
        ("Passenger.App.Containers.ContainerRun", "runs containers"),
        ("Passenger.App.Aspire.", "runs Aspire"),
        ("Passenger.App.Godot.", "runs Godot"),
        ("Passenger.App.Web.WebLanguages::InstallAsync", "installs a language's tools"),
        ("Passenger.App.Web.WebLanguages::UseTools", "runs a language's tools"),
        ("Passenger.App.Web.FolderTools", "runs the folder's linters and formatters"),
        ("Passenger.App.Web.PrettierService", "runs Prettier"),
        ("Passenger.App.Browsers", "runs a browser"),
        ("Passenger.App.MainWindow::Browse", "opens a browser"),
        ("Passenger.App.MainWindow::ShowInFileManager", "opens the file manager"),
        ("System.IO.FileSystemWatcher", "watches files"),
        ("Passenger.Workspace.WatchedTree", "watches files"),
        ("Passenger.Core.WatchedFile", "watches files"),
        ("System.IO.Path::get_DirectorySeparatorChar", "reads the system's path separator"),
        ("System.IO.Path::DirectorySeparatorChar", "reads the system's path separator"),
        ("System.IO.Path::PathSeparator", "reads the system's path separator"),
        ("System.Environment::get_NewLine", "reads the system's line ending"),
        ("System.OperatingSystem::", "asks which system it runs on"),
        ("System.Runtime.InteropServices.RuntimeInformation", "asks which system it runs on"),
        ("Passenger.Core.CommandKey", "takes the system's command key"),
        ("Passenger.Editor.Keymap::get_Command", "takes the system's command key"),
        ("Avalonia.Input.Platform.PlatformHotkeyConfiguration", "takes the system's keys"),
        ("Avalonia.Input.TextInput.", "runs an input method"),
        ("Passenger.Editor.ViewInputMethod", "runs an input method"),
        ("Avalonia.Media.FontManager", "reads the system's fonts"),
        ("Avalonia.Media.FormattedText", "shapes text"),
        ("Avalonia.Media.TextFormatting.", "shapes text"),
        ("Avalonia.Media.GlyphRun", "shapes text"),
        ("Avalonia.Media.Imaging.RenderTargetBitmap", "reads drawn pixels"),
        ("Avalonia.Automation.Peers.", "speaks to a screen reader"),
        ("Avalonia.Automation.Provider.", "speaks to a screen reader"),
        ("Passenger.Editor.UiAutomation.", "speaks to a screen reader"),
        ("Passenger.App.MacTextElements", "speaks to a screen reader"),
        ("System.Net.Sockets.", "speaks through the system's sockets"),
        ("System.Net.Http.", "speaks through the system's sockets"),
        ("System.Net.HttpListener", "speaks through the system's sockets"),
        ("System.GC::GetTotalAllocatedBytes", "counts every thread's allocations, the system's runtime's own among them"),
        ("Xunit.Assert::Skip", "skips"),
    ];

    /// <summary>The tests' own helpers whose work differs by system, by type name or Type.Method, and why.</summary>
    private static readonly Dictionary<string, string> DifferingHelpers = new(StringComparer.Ordinal)
    {
        ["McpRig"] = "starts a Passenger of its own", ["Debuggees.Program"] = "runs a debuggee", ["Docker"] = "runs containers",
        ["ContainerPrograms"] = "runs containers", ["AspireApp"] = "runs Aspire", ["AspireFunctionsApp"] = "runs Aspire",
        ["AspireDashboardHost"] = "runs Aspire", ["AspireInteractionHost"] = "runs Aspire", ["AvaloniaApp"] = "builds and runs an app",
        ["WpfApp"] = "builds and runs an app", ["MauiApp"] = "builds and runs an app", ["BellApp"] = "builds and runs an app",
        ["TestGodotEditors"] = "runs Godot", ["StandInHttp"] = "serves HTTP on the system's sockets",
        ["StandInFeed"] = "serves HTTP on the system's sockets", ["StandInRegistry"] = "serves HTTP on the system's sockets",
        ["StandInGitRemote"] = "runs Git", ["SilentPort"] = "listens on the system's sockets",
        ["StopRefusingPort"] = "listens on the system's sockets", ["DevCerts"] = "reads the system's certificates",
        ["TestVolumes"] = "makes the system's volumes", ["MacBridge"] = "speaks to a screen reader",
        ["AndroidSerials"] = "runs a device", ["SshKeygenQuestions"] = "runs ssh-keygen", ["RefreshClient"] = "runs a browser's page",
        ["TestPictures"] = "reads drawn pixels", ["Screenshot"] = "reads drawn pixels",
        ["HeldBytes"] = "measures what the system's runtime keeps",
    };

    /// <summary>The rig's own methods not followed, as Type.Method: what they run never decides a test's outcome.</summary>
    private static readonly HashSet<string> RigsOwn = new(StringComparer.Ordinal)
    {
        // Naming the programs still running where a wait gives up.
        "Ui.ProgramsRunning",
        // Deleting a test's folders once it has ended, waiting out what holds them on Windows.
        "TestFolders.Delete",
        // A table's skip where each of its rows skipped, which the rows' own code, followed, shows.
        "Cases.SkipWhereEverySkipped",
    };

    /// <summary>A system named in a test's own words.</summary>
    [GeneratedRegex(@"\b(macOS|Mac OS|OS X|Linux|Ubuntu|win-(x64|arm64|x86)|osx-(x64|arm64)|linux-(x64|arm64|musl))\b|(?<!Tool )\bWindows\b")]
    private static partial Regex SystemNamed();

    /// <summary>A file of a Godot project the test makes or opens, which a Godot of the system's own reads.</summary>
    [GeneratedRegex(@"\.(gd|tscn|tres|gdshader)\b|\bproject\.godot\b", RegexOptions.IgnoreCase)]
    private static partial Regex ProgramsFile();

    /// <summary>The IDE's actions that build, run, debug, profile, publish or test a program, open the terminal or a
    /// browser, work Git, containers, Aspire, migrations or Godot, or install or update, by their ids, as a test names them
    /// to run: any its code or a helper it reaches names.</summary>
    private static readonly Dictionary<string, string> Starting = typeof(Passenger.App.AppActions)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.FieldType == typeof(string) && StartingAction().IsMatch(field.Name))
        .ToDictionary(field => (string)field.GetRawConstantValue()!, field => field.Name, StringComparer.Ordinal);

    /// <summary>The names of the actions <see cref="Starting"/> takes.</summary>
    [GeneratedRegex(@"^(Build|Rebuild|Publish|Run|Debug|Profile|AttachProfiler|AttachToProcess|Reattach|Rerun|ApplyHotReload|ShowTerminal|NewTerminal|OpenInTerminal|OpenInBrowser|Commit$|Push$|Fetch$|Stash$|Unstash$|CloneRepository|UpdateProject|ContinueRebase|Abort|Service(Run|Debug|Attach|Profile)|Container(Start|Stop|Restart|Pause|Remove|Scale|Pull|Prune|Shell)|AddMigration|RemoveLastMigration|UpdateDatabase|ScriptMigrations|BundleMigrations|Install|CheckForUpdates|RestartToUpdate|TestEndpoint|NuGetUpdateAll|Godot|AspireCommand)")]
    private static partial Regex StartingAction();

    /// <summary>A class marked [SameOnEverySystem] starts no process, build, run, terminal, debugger, Git or container of its
    /// own, no browser, file watcher, socket, font, shaping, screen reader, input method, system key or path separator,
    /// asks no system's name, counts no allocation but its own thread's and skips nothing; it is in neither the gate,
    /// which runs on every system, nor the Alone collection, whose classes hold to a time, debug or count every thread's
    /// allocations.</summary>
    [Fact]
    public void A_class_marked_same_on_every_system_runs_nothing_that_behaves_otherwise_on_another()
    {
        Assembly tests = typeof(SameOnEverySystemRulesTests).Assembly;
        Type[] marked = [.. tests.GetTypes().Where(type => type.GetCustomAttribute<SameOnEverySystemAttribute>() is not null).OrderBy(type => type.FullName, StringComparer.Ordinal)];
        Assert.True(marked.Length > 0, "No class marked [SameOnEverySystem] was found, so the built tests were not read");
        using TestReach reach = new(tests);
        List<string> broken = [.. marked.SelectMany(type => Breaks(reach, type))];
        TestContext.Current.TestOutputHelper?.WriteLine($"Marked [SameOnEverySystem]:\n{string.Join('\n', marked.Select(type => type.Name))}");
        Assert.True(broken.Count == 0, "A class marked [SameOnEverySystem] runs on one system alone, yet runs what another system may do otherwise, "
            + "so a fault there would go unseen: take the mark off, or keep the class to what behaves the same everywhere:\n" + string.Join('\n', broken));
    }

    /// <summary>Why a marked class breaks its rule, a line each, the way to each call said; none where it keeps it.</summary>
    private static IEnumerable<string> Breaks(TestReach reach, Type type)
    {
        if (type.GetCustomAttribute<GateAttribute>() is not null || TestReach.Tests(type).Any(method => method.GetCustomAttribute<GateAttribute>() is not null))
        {
            yield return $"{type.Name} has tests in the gate, which runs on every system";
        }
        if (type.GetCustomAttributes(inherit: true).OfType<Xunit.v3.ICollectionAttribute>().Any(collection => collection.Name == Alone.Name))
        {
            yield return $"{type.Name} runs alone: it holds what it tests to a time, debugs, or counts every thread's allocations, which each system's runtime and libraries shape";
        }
        foreach (MethodInfo test in TestReach.Tests(type).Where(Skips))
        {
            yield return $"{type.Name}.{test.Name} skips where something is missing, which differs by system";
        }
        HashSet<string> said = [];
        foreach (TestReach.Reached reached in reach.Of(type, _ => true, Helper, RigsOwn))
        {
            if (reached.Helper is { } helper)
            {
                if (said.Add($"{helper}:{reached.Path}"))
                {
                    yield return $"{type.Name} reaches {helper}, which {DifferingHelpers[helper]}: {reached.Path}";
                }
            }
            else if (reached.Words is { } words)
            {
                if (reached.Owned && (SystemNamed().Match(words) is { Success: true } system) && said.Add($"system:{system.Value}"))
                {
                    yield return $"{type.Name}'s words name a system ({system.Value}): {reached.Path}";
                }
                if (reached.Owned && (ProgramsFile().Match(words) is { Success: true } file) && said.Add($"file:{file.Value}"))
                {
                    yield return $"{type.Name} makes or opens a Godot project's file ({file.Value}), which a Godot of its own reads: {reached.Path}";
                }
                if ((Starting.TryGetValue(words, out string? action) || words == Passenger.App.ToolWindowIds.Terminal) && said.Add($"action:{words}"))
                {
                    yield return $"{type.Name} names {action ?? "the Terminal window"}, which starts what another system may run otherwise: {reached.Path}";
                }
            }
            else if (reached.Called is { } name && Differing.FirstOrDefault(entry => name.StartsWith(entry.Called, StringComparison.Ordinal)) is { Why: { } why }
                && said.Add($"{name}:{why}"))
            {
                yield return $"{type.Name} {why}: {reached.Path} → {name}";
            }
        }
    }

    /// <summary>The helper of <see cref="DifferingHelpers"/> a method of the tests is, by its outermost type or as
    /// Type.Method; null for any other.</summary>
    private static string? Helper(string outer, string named) => DifferingHelpers.ContainsKey(outer) ? outer : DifferingHelpers.ContainsKey(named) ? named : null;

    private static bool Skips(MethodInfo test) => test.GetCustomAttributes().OfType<Xunit.v3.IFactAttribute>()
        .Any(fact => fact.Skip is not null || fact.SkipWhen is not null || fact.SkipUnless is not null);
}
