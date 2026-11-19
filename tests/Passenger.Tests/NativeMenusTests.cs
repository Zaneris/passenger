using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Welcome;
using Passenger.Core;
using Passenger.Tests.Debugging.Helpers;

namespace Passenger.Tests;

/// <summary>The menus macOS shows from Passenger's (the application's, each window's menu bar, the Dock's), each given
/// its owner once and changed after only in its items (<see cref="NativeMenus"/>), as Avalonia's macOS exporter of each
/// refuses a second NativeMenu: held on the Dock by that exporter itself, made in the headless test process over
/// Avalonia's native library as its macOS platform makes it as setup ends, its menu read and chosen from as the Dock asks
/// the app for it; and held in the code, which gives no menu outside NativeMenus.</summary>
public sealed partial class NativeMenusTests
{
    [AvaloniaFact]
    public void The_Docks_menu_follows_the_recent_list_through_a_closed_solution_a_second_window_and_its_own_items()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "The Dock is macOS's");
        MacDock.Bring();
        string state = TestFolders.Create("dock-menu-state-");
        string under = PathNames.Canonical(TestFolders.Create("dock-menu-"));
        string shop = Path.GetDirectoryName(TestFolders.Write(under, Path.Combine("Shop", "Shop.slnx"), "<Solution />\n"))!;
        string game = Directory.CreateDirectory(Path.Combine(under, "Game")).FullName;
        RecentShortcuts.DockFollowsUnseen = true;
        try
        {
            MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = state };
            window.Show();
            window.KeepLayout();
            window.OpenFolder(shop);
            Ui.Settle();
            Follows(["Shop"], "the solution opened");

            Ui.Submenu(window.MenuBar, "File").Items.OfType<NativeMenuItem>().First(i => i.Header == "Close Solution").Command!.Execute(null);
            Ui.Settle();
            Assert.True(window.Folder is null, $"File, Close Solution left {window.Folder} open");
            Follows(["Shop"], "File, Close Solution");

            MacDock.Choose("New Window");
            Ui.Settle();
            Assert.True(PassengerApp.Windows.Count == 2 && PassengerApp.Front is { } front && front != window,
                $"The Dock's New Window left {PassengerApp.Windows.Count} windows, so its item did nothing once its menu followed a change");
            MainWindow second = PassengerApp.Front!;
            second.OpenFolder(game);
            Ui.Settle();
            Follows(["Game", "Shop"], "a second window opened a folder");

            window.Activate();
            Ui.Settle();
            MacDock.Choose("Shop");
            Ui.Settle();
            Assert.True(window.Folder is { } reopened && PathNames.Same(reopened, shop),
                $"The Dock's Shop, chosen with the first window in front, left it on {window.Folder ?? "no folder"}");
            Follows(["Shop", "Game"], "the Dock's Shop reopened the solution");

            second.ForgetRecent(game);
            Ui.Settle();
            Follows(["Shop"], "the second window forgot the folder");
        }
        finally
        {
            RecentShortcuts.DockFollowsUnseen = false;
        }

        // The Dock's menu, once what the exporter queued has run, lists the recent ones, then New Window.
        static void Follows(string[] recent, string after)
        {
            string[] expected = [.. recent, "-", "New Window"];
            Ui.WaitFor(() => IdeErrors.Errors.Count > 0 || MacDock.Titles().SequenceEqual(expected),
                () => $"the Dock's menu to list {string.Join(", ", expected)} after {after}; it lists {string.Join(", ", MacDock.Titles())}");
            Assert.True(IdeErrors.Errors.Count == 0,
                $"After {after} updating the Dock's menu failed, so it goes on listing what it listed before: {string.Join("\n", IdeErrors.Errors.Select(e => e.Details))}");
        }
    }

    /// <summary>A menu macOS shows is given its owner only in NativeMenus' file: one given a second NativeMenu elsewhere is
    /// refused by Avalonia's exporter with an internal error, and the menu goes on showing what it showed. Read from the
    /// code as written, comments passed over.</summary>
    [Fact]
    public void No_menu_macOS_shows_is_given_outside_NativeMenus()
    {
        const string Home = "src/Passenger.App/NativeMenus.cs";
        string root = Debuggees.Root;
        List<string> found = [];
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            string relative = PathNames.Relative(file, root)!;
            if (relative == Home || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }
            string[] lines = File.ReadAllLines(file);
            for (int line = 0; line < lines.Length; line++)
            {
                if (!lines[line].TrimStart().StartsWith("//", StringComparison.Ordinal) && GivesMenu().IsMatch(lines[line]))
                {
                    found.Add($"{relative}:{line + 1}: {lines[line].Trim()}");
                }
            }
        }
        Assert.True(found.Count == 0, "A menu macOS shows given outside NativeMenus may be given a second NativeMenu, which Avalonia's macOS exporter "
            + $"refuses with an internal error, the menu going on showing what it showed: fill it through NativeMenus.Fill:\n{string.Join('\n', found)}");
    }

    /// <summary>A menu given its owner: through the attached property's setter, or its property set directly.</summary>
    [GeneratedRegex(@"\b(NativeMenu|NativeDock|TrayIcon)\.SetMenu\b|SetValue\(\s*(NativeMenu|NativeDock|TrayIcon)\.MenuProperty\b|\[(NativeMenu|NativeDock|TrayIcon)\.MenuProperty\]\s*=")]
    private static partial Regex GivesMenu();

    /// <summary>Avalonia's exporter of the Dock's menu on macOS, made once in the test process over Avalonia's native
    /// library as Avalonia's macOS platform makes it as setup ends, so NativeDock's menu on the application reaches AppKit
    /// as it reaches the person's Dock; and that menu as AppKit asks the app's delegate for it when the Dock's menu opens.</summary>
    private static partial class MacDock
    {
        private const string Runtime = "/usr/lib/libobjc.A.dylib";

        private static object? exporter;

        /// <summary>The app's delegate as Avalonia's native library declares it, made only to be asked for the Dock's menu.</summary>
        private static nint answering;

        public static unsafe void Bring()
        {
            if (exporter is not null)
            {
                return;
            }
            nint library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "runtimes", "osx", "native", "libAvaloniaNative.dylib"));
            nint made = ((delegate* unmanaged<nint>)NativeLibrary.GetExport(library, "CreateAvaloniaNative"))();
            Assembly avalonia = Assembly.Load("Avalonia.Native");
            Type face = avalonia.GetType("Avalonia.Native.Interop.IAvaloniaNativeFactory", throwOnError: true)!;
            Type runtime = Type.GetType("MicroCom.Runtime.MicroComRuntime, MicroCom.Runtime", throwOnError: true)!;
            MethodInfo proxy = runtime.GetMethods().Single(m => m.Name == "CreateProxyFor" && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ ParameterType: var pointer }, { ParameterType: var owns }] && pointer == typeof(nint) && owns == typeof(bool)).MakeGenericMethod(face);
            object factory = proxy.Invoke(null, [made, true])!;
            Type exporting = avalonia.GetType("Avalonia.Native.AvaloniaNativeMenuExporter", throwOnError: true)!;
            object dock = Enum.Parse(exporting.GetNestedType("MenuTarget", BindingFlags.NonPublic)!, "Dock");
            exporter = Activator.CreateInstance(exporting, BindingFlags.Instance | BindingFlags.NonPublic, null, [factory, dock], null)!;
            answering = Send(Send(Class("AvnAppDelegate"), Selector("alloc")), Selector("init"));
        }

        /// <summary>The Dock's menu's items as AppKit holds them, a separator as "-".</summary>
        public static string[] Titles()
        {
            nint pool = PoolPush();
            try
            {
                nint menu = Menu();
                return [.. Enumerable.Range(0, menu == 0 ? 0 : (int)Send(menu, Selector("numberOfItems"))).Select(at => TitleOf(Send(menu, Selector("itemAtIndex:"), at)))];
            }
            finally
            {
                PoolPop(pool);
            }
        }

        /// <summary>The Dock's item titled so chosen as AppKit chooses it: asked whether it is enabled, then sent its action.</summary>
        public static void Choose(string title)
        {
            nint pool = PoolPush();
            try
            {
                nint menu = Menu();
                nint item = Enumerable.Range(0, (int)Send(menu, Selector("numberOfItems"))).Select(at => Send(menu, Selector("itemAtIndex:"), at))
                    .FirstOrDefault(i => TitleOf(i) == title);
                Assert.True(item != 0, $"The Dock's menu has no {title}: it lists {string.Join(", ", Titles())}");
                nint target = Send(item, Selector("target"));
                Assert.True((byte)Send(target, Selector("validateMenuItem:"), item) != 0, $"The Dock's {title} is greyed");
                Send(target, Send(item, Selector("action")), item);
            }
            finally
            {
                PoolPop(pool);
            }
        }

        private static nint Menu() => Send(answering, Selector("applicationDockMenu:"), 0);

        private static string TitleOf(nint item) => (byte)Send(item, Selector("isSeparatorItem")) != 0 ? "-"
            : Marshal.PtrToStringUTF8(Send(Send(item, Selector("title")), Selector("UTF8String"))) ?? string.Empty;

        [LibraryImport(Runtime, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Class(string name);

        [LibraryImport(Runtime, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
        private static partial nint Selector(string name);

        [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPush")]
        private static partial nint PoolPush();

        [LibraryImport(Runtime, EntryPoint = "objc_autoreleasePoolPop")]
        private static partial void PoolPop(nint pool);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector);

        [LibraryImport(Runtime, EntryPoint = "objc_msgSend")]
        private static partial nint Send(nint receiver, nint selector, nint argument);
    }
}
