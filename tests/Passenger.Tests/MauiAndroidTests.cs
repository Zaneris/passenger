using Passenger.App;
using Passenger.App.LiveTree;
using Passenger.App.Run.Devices;
using Passenger.Core;
using Passenger.Inspector;
using Passenger.Workspace;
using SkiaSharp;

namespace Passenger.Tests;

/// <summary>The app <c>dotnet new maui</c> makes, its Android head debugged on an emulator from the window: Passenger's
/// MAUI inspector goes in beside the debugger's relay, and the Live Visual Tree shows the app's window down to its main
/// page's labels and its button, named by its x:Name, once its XAML's breakpoints stopped where the page is made; the
/// button's details give its type, its Text set on it and its box
/// in the window; the outline over the button chosen is drawn in the app where the platform lays the button out, a tap
/// given through the inspector clicks it, and its main page's XAML edits reach it with Hot Reload, a property's patched by
/// MAUI, a C# expression's with the page made again. Debug's end ends the app and leaves nothing of the inspector's in it.
/// Skips, saying why, without MAUI's workload, the Android SDK's platform and Java, an emulator, or the relays.
/// Steps of <see cref="AndroidDeviceTests"/>' flow, which runs alone.</summary>
internal static class MauiAndroidTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(8);

    internal static void A_maui_apps_android_head_debugged_on_an_emulator_shows_its_main_pages_label_and_button_in_the_live_tree()
    {
        string folder = PathNames.Canonical(TestFolders.Create("maui-android"));
        (string project, string? unmade) = Ui.AwaitPumped(Waiting.Apart(() => MauiApp.CreateAndroidHeadAsync(folder)), "dotnet new maui and the Android head's restore", Slow);
        if (unmade is not null)
        {
            Assert.Skip(unmade);
        }
        (IReadOnlyList<DeviceTarget> targets, AndroidTools? tools, string? problem) = Ui.Await(Waiting.Apart(() =>
            AndroidDevices.ListAsync(project, MauiApp.Android, new Dictionary<string, string>(), CancellationToken.None)), "the Android workload's devices", Slow);
        if (tools is null)
        {
            Assert.Skip($"There is nothing to run on: {problem}");
        }
        if (tools.Lacks is { } lacks)
        {
            Assert.Skip($"No Android app can be built here: {Refusals.NoAndroidPlatform(lacks)}");
        }
        if (MauiApp.Target(targets) is not { } target)
        {
            Assert.Skip("No Android device is connected and no emulator is made (an AVD of an installed system image), so there is nothing to run on");
            return;
        }
        if (!((string[])["android-arm64", "android-x64"]).Any(runtime => File.Exists(CarriedFiles.Relay(runtime, AndroidApp.RelayFile))))
        {
            Assert.Skip("This build carries no Android debug relay, which `just debug-relay android` makes before a build carries it");
        }
        MainWindow window = Ui.OpenFolder(folder);
        window.ApplySettings(window.Settings with { HotReload = true });
        try
        {
            Ui.WaitFor(() => window.Configurations.Any(c => c.Name == MauiApp.Name), () => $"{MauiApp.Name}'s run configuration; the window has configurations "
                + $"{string.Join(", ", window.Configurations.Select(c => c.Name))}: {Ui.Said(window)}", Slow);
            Assert.True(window.SelectConfiguration(MauiApp.Name));
            ProjectModel model = window.Session.Projects.Single(p => PathNames.Same(p.Path, project));
            window.ChooseRunTarget(model, target);
            Ui.Settle();

            // Breakpoints in the page's XAML, bound in the code MAUI's source generator wrote of it, stop as the page is made.
            (string xaml, int hello, int clickMe) = MauiApp.XamlBreakpoints(window, project);
            // adb ends each connection the app's socket does not take yet a second late, as on a loaded machine.
            using LateRefusals late = new();
            Ui.Launched(window, window.DebugAsync(), "the build, the install and the debugged start on the emulator", Slow);
            MauiApp.StopsInXaml(window, xaml, hello, clickMe, Slow);
            Ui.Run(window, AppActions.Resume);
            LiveTreeView tree = window.LiveTree;
            InspectorWaits.Until(window, () => window.LiveApp, () => tree.Rows.Any(r => r.Element is { Type: "Button", Name: "CounterBtn" }),
                () => $"the main page's button in the Live Visual Tree; it shows {string.Join(", ", tree.Rows.Select(r => r.Element.Type))}", Slow);
            Assert.True(late.Refused > 0, "adb ended no connection before the app's inspector listened, so the step did not hold Passenger's reach to adb's late ends");
            Assert.True(window.LiveApp is { Kind: LiveAppKind.Maui } && window.LiveApp.Framework.StartsWith("MAUI 11.", StringComparison.Ordinal),
                $"The app was taken for {window.LiveApp?.Kind}, {window.LiveApp?.Framework}");
            Assert.True(tree.Rows[0].Element.Type == "Window", $"The tree's first row is {tree.Rows[0].Element.Type}, not the app's window");
            Assert.Contains(tree.Rows, r => r.Element.Type == "MainPage");
            Assert.Equal(2, tree.Rows.Count(r => r.Element.Type == "Label"));

            tree.Tree.SelectedItem = tree.Rows.First(r => r.Element.Name == "CounterBtn");
            Ui.WaitFor(() => tree.Details?.Name == "CounterBtn", () => $"the button's details; the window says: {Ui.Said(window)}", TimeSpan.FromMinutes(1));
            ElementDetails button = tree.Details!;
            Assert.Equal("Button", button.Type);
            Assert.True(button.Properties.SingleOrDefault(p => p.Name == "Text") is { Value: "\"Click me\"", Source: ValueSources.Local },
                $"The button's Text is not its XAML's, set on it: {button.Properties.SingleOrDefault(p => p.Name == "Text")}");
            Box box = button.Layout.InWindow ?? throw new Xunit.Sdk.XunitException("The button's details give it no place in its window");
            Assert.True(box is { X: > 0, Y: > 0, Width: > 100, Height: > 20 }, $"The button's box in its window is {box}");

            // The outline over the button chosen, drawn in the app over where the platform laid the button out.
            LiveApp app = window.LiveApp!;
            long top = tree.Rows[0].Element.Id;
            Deadline drawing = Deadline.In(TimeSpan.FromMinutes(1));
            while (true)
            {
                Picture picture = Ui.AwaitPumped(app.CaptureAsync(top, null, null), "a picture of the app's window", TimeSpan.FromMinutes(1));
                using SKBitmap drawn = SKBitmap.Decode(picture.Png);
                SKColor edge = drawn.GetPixel((int)Math.Round(box.X * picture.Scale), (int)Math.Round((box.Y + (box.Height / 2)) * picture.Scale));
                if (edge.Blue > 0xC0 && edge.Red < 0x60 && edge.Green is > 0x40 and < 0xA0)
                {
                    break;
                }
                Assert.False(drawing.Passed, $"The app drew no outline over the button chosen: its left edge, at {box.X},{box.Y + (box.Height / 2)}, is {edge}");
                Ui.Hold(TimeSpan.FromMilliseconds(500), "the outline to be drawn over the app");
            }

            InputDone tapped = Ui.AwaitPumped(app.InputAsync(new InputAction(InputKinds.Click) { Element = button.Id }), "a tap on the button", TimeSpan.FromMinutes(1));
            Assert.True(tapped.Target == button.Id && tapped.Shows == "Clicked 1 time", $"The tap reached {tapped.TargetName}, which shows \"{tapped.Shows}\", not the button counting it");
            MauiApp.TakesXamlEdits(window, app, project, Slow);

            Ui.Run(window, AppActions.Stop);
            Ui.WaitFor(() => !window.Debugger.IsDebugging, "the debug session to end once stopped", TimeSpan.FromMinutes(1));
            string serial = AndroidSerials.Of(window, target);
            Ui.WaitFor(() => Left(tools, serial).Length == 0, () => $"the app to end and the inspector's files to be let go on the emulator; it still has {Left(tools, serial)}",
                TimeSpan.FromSeconds(30));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>What a start left on the emulator of <paramref name="serial"/>: the app still running, the inspector's files in
    /// it; empty once its end took them away.</summary>
    private static string Left(AndroidTools tools, string serial)
    {
        string running = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", "pidof", MauiApp.Package]))), "pidof on the device")
            .Output.Trim();
        string files = Ui.Await(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.Command(tools.Adb, ["-s", serial, "shell", $"run-as {MauiApp.Package} ls -a files"]))),
            "the app's files").Output;
        return string.Join("; ", [.. running.Length > 0 ? [$"{MauiApp.Package} running as {running}"] : (string[])[],
            .. files.Split('\n').Any(l => l.Trim() == ".passenger") ? ["the inspector's files"] : (string[])[]]);
    }
}
