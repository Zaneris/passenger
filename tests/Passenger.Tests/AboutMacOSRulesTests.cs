using System.Reflection;

namespace Passenger.Tests;

/// <summary>Which report classes macOS's report lane runs, read from the built tests: a class that reaches macOS's own
/// behaviour carries [AboutMacOS], so the lane that alone runs on macOS keeps it, and a class carrying the mark reaches it
/// or says what of macOS's its subject is. What it reaches (<see cref="TestReach"/>) is held to <see cref="MacOSOwn"/>:
/// the IDE's code that works otherwise on macOS reached anywhere, the system asked by the class's own code, and the
/// tests' helpers that drive macOS's own (<see cref="MacOSHelpers"/>).</summary>
[Collection(Beside.Name)]
public sealed class AboutMacOSRulesTests
{
    /// <summary>What works otherwise on macOS, by the start of the called member's full name (Type::Member), why, and
    /// whether it counts only where the class's own code calls it: a helper asking which system it runs on adapts the
    /// rig, where the class's own question is of its subject.</summary>
    private static readonly (string Called, string Why, bool Own)[] MacOSOwn =
    [
        ("System.OperatingSystem::IsMacOS", "asks whether it runs on macOS", true),
        ("System.OperatingSystem::IsMacCatalyst", "asks whether it runs on macOS", true),
        ("System.OperatingSystem::IsIOS", "asks whether it runs on Apple's systems", true),
        ("System.Runtime.InteropServices.OSPlatform::get_OSX", "asks whether it runs on macOS", true),
        ("Passenger.Core.CommandKey", "takes the system's command key", true),
        ("Passenger.Editor.Keymap::get_Command", "takes the system's command key", true),
        ("Avalonia.Input.Platform.PlatformHotkeyConfiguration", "takes the system's keys", true),
        ("Passenger.Core.PathNames::CaseIgnoredIn", "asks whether the disk tells letter case apart", true),
        ("Passenger.Core.PathNames::NamesIn", "asks whether the disk tells letter case apart", true),
        ("System.IO.FileSystemWatcher", "watches files, through FSEvents on macOS", false),
        ("Passenger.Workspace.WatchedTree", "watches files, through FSEvents on macOS", false),
        ("Passenger.Workspace.FolderEvents", "watches files, through FSEvents on macOS", false),
        ("Passenger.App.Workspace.SharedWatch", "watches files, through FSEvents on macOS", false),
        ("Passenger.App.Workspace.WatchLag", "watches files, through FSEvents on macOS", false),
        ("Passenger.Core.WatchedFile", "watches a file, through FSEvents on macOS", false),
        ("Passenger.Core.UserTemporary", "takes the user's temporary folder, behind /var's link on macOS", false),
        ("Passenger.Core.MacBundles", "reads a macOS app bundle", false),
        ("Passenger.Core.MachOFile", "reads a Mac program", false),
        ("Passenger.Core.ProgramProcessors", "reads a program's processors, Rosetta's on macOS", false),
        ("Passenger.Core.Dotnet::get_Translated", "asks whether it runs under Rosetta", false),
        ("Passenger.App.NativeMenus", "gives macOS's menus", false),
        ("Passenger.App.MainWindow::get_ApplicationMenu", "fills macOS's application menu", false),
        ("Passenger.App.MacTextElements", "speaks to VoiceOver", false),
        ("Passenger.App.WindowTracking", "tracks Apple's fonts", false),
        ("Passenger.App.FaceTracking", "takes a face's tracking off, as Apple's fonts have it", false),
        ("Passenger.App.WindowFonts", "takes the window's typeface, Apple's on macOS", false),
        ("Passenger.Editor.FallbackFonts", "falls back through the system's fonts", false),
        ("Passenger.App.Run.Devices.AppleSimulators", "runs an iOS simulator", false),
        ("Passenger.App.Run.Devices.Simulator", "runs an iOS simulator", false),
        ("Passenger.App.Run.Devices.MacCatalyst", "runs Mac Catalyst", false),
        ("Passenger.Core.AttachedImages", "attaches disk images through hdiutil and diskutil", false),
        ("Passenger.App.Updates.DiskImageUpdater", "updates from a disk image", false),
        ("Passenger.App.Updates.InPlaceUpdater", "puts an update in place", false),
        ("Passenger.Terminal.MacPseudoTerminal", "runs macOS's pseudo-terminal", false),
        ("Passenger.App.Run.DevelopmentCertificate", "reads the dev certificate, from the keychain on macOS", false),
    ];

    /// <summary>The tests' own helpers that drive macOS's own behaviour, by type name or Type.Method, and why.</summary>
    private static readonly Dictionary<string, string> MacOSHelpers = new(StringComparer.Ordinal)
    {
        ["MacBridge"] = "speaks to VoiceOver", ["TestVolumes"] = "makes volumes through hdiutil on macOS",
    };

    /// <summary>The rig's own methods not followed, as Type.Method: what they run never decides a test's outcome.</summary>
    private static readonly HashSet<string> RigsOwn = new(StringComparer.Ordinal)
    {
        // An agent's Passenger's sockets and state taken out once its test has ended.
        "McpRig.Dispose",
    };

    /// <summary>Why a test of another letter case skips on Linux (<see cref="PathsTests"/>), read as the test's words are,
    /// though never written in this class's own.</summary>
    private static readonly string CaseTold = (string)typeof(PathsTests).GetField(nameof(PathsTests.CaseTold), BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    /// <summary>A report class that reaches macOS's own behaviour carries [AboutMacOS]; one that carries it reaches it or
    /// says what of macOS's its subject is, has tests in the report lane, and is not also marked [SameOnEverySystem].</summary>
    [Fact]
    public void A_report_class_that_reaches_macOSs_own_behaviour_is_marked_to_run_there_and_a_marked_one_reaches_it()
    {
        Assembly tests = typeof(AboutMacOSRulesTests).Assembly;
        Type[] classes = [.. tests.GetTypes().Where(type => !type.IsAbstract && TestReach.Tests(type).Any()).OrderBy(type => type.FullName, StringComparer.Ordinal)];
        Assert.True(classes.Length > 100, $"Only {classes.Length} test classes were found, so the built tests were not read");
        using TestReach reach = new(tests);
        List<string> broken = [];
        List<string> marked = [];
        foreach (Type type in classes)
        {
            AboutMacOSAttribute? mark = type.GetCustomAttribute<AboutMacOSAttribute>();
            bool report = TestReach.InReport(type);
            string? reached = report || mark is not null ? Reaches(reach, type).FirstOrDefault() : null;
            if (mark is not null)
            {
                marked.Add($"{type.Name}: {reached ?? mark.Why}");
            }
            if (mark is null && report && reached is not null)
            {
                broken.Add($"{type.Name} reaches macOS's own behaviour, yet macOS's report lane passes it over: {reached}");
            }
            else if (mark is not null && !report)
            {
                broken.Add($"{type.Name} is marked [{AboutMacOSAttribute.Name}], but every test of it is in the gate, which runs on every system");
            }
            else if (mark is not null && type.GetCustomAttribute<SameOnEverySystemAttribute>() is not null)
            {
                broken.Add($"{type.Name} is marked both [{AboutMacOSAttribute.Name}] and [{SameOnEverySystemAttribute.Name}], which runs it on Linux alone");
            }
            else if (mark is not null && reached is null && string.IsNullOrWhiteSpace(mark.Why))
            {
                broken.Add($"{type.Name} is marked [{AboutMacOSAttribute.Name}] but reaches nothing of macOS's own, nor says what of macOS's its subject is");
            }
        }
        TestContext.Current.TestOutputHelper?.WriteLine($"Marked [{AboutMacOSAttribute.Name}]:\n{string.Join('\n', marked)}");
        Assert.True(broken.Count == 0, "macOS's report lane runs only the classes marked [AboutMacOS], so a class reaching macOS's own behaviour unmarked "
            + "leaves a fault of macOS's unseen, and a mark nothing calls for spends macOS's minutes: mark it, or take the mark off:\n" + string.Join('\n', broken));
    }

    /// <summary>What of macOS's own a class reaches, a line each, the way to it said.</summary>
    private static IEnumerable<string> Reaches(TestReach reach, Type type)
    {
        foreach (TestReach.Reached reached in reach.Of(type, TestReach.InReportLane, Helper, RigsOwn))
        {
            if (reached.Helper is { } helper)
            {
                yield return $"it reaches {helper}, which {MacOSHelpers[helper]}: {reached.Path}";
            }
            else if (reached.Words == CaseTold)
            {
                yield return $"it tries a path in other letter case, which macOS's disks ignore: {reached.Path}";
            }
            else if (reached.Called is { } name && MacOSOwn.FirstOrDefault(entry => name.StartsWith(entry.Called, StringComparison.Ordinal) && (reached.Owned || !entry.Own))
                is { Why: { } why })
            {
                yield return $"it {why}: {reached.Path} → {name}";
            }
        }
    }

    /// <summary>The helper of <see cref="MacOSHelpers"/> a method of the tests is, by its outermost type or as
    /// Type.Method; null for any other.</summary>
    private static string? Helper(string outer, string named) => MacOSHelpers.ContainsKey(outer) ? outer : MacOSHelpers.ContainsKey(named) ? named : null;
}
