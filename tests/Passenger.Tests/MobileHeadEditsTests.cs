using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>A mobile project's heads as their templates made them for .NET 10, opened on a Mac whose Xcode's SDK asks every
/// app for a scene manifest: the banner names each edit each head needs to build and run here, Make the Mobile Edits (or its
/// fix on a line of the project) makes them as one command Undo takes back and Redo makes again, and the head then builds,
/// restored from nuget.org. Avalonia's cross-platform app's iOS head and its Android head (built where the Android workload
/// finds a JDK), a MAUI app made before its template gave its Apple heads scenes, and a Mac Catalyst app whose Info.plist
/// names its least macOS and no scene manifest. Each skips, saying why, off a Mac or without its workloads.</summary>
public sealed partial class MobileHeadEditsTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(8);

    private const string Name = "XApp";

    /// <summary>What the banner says of a .NET 10 head's move to the first .NET on CoreCLR.</summary>
    private static string Moved(string framework, string platform) =>
        $"net11.0-{framework} in place of net10.0-{framework}, whose {platform} apps run on Mono (Passenger builds, runs and debugs mobile apps from .NET 11, on CoreCLR)";

    [AvaloniaFact]
    public void An_avalonia_apps_ios_head_is_told_each_edit_it_needs_and_builds_once_Make_the_Mobile_Edits_made_them_as_one_step_of_Undo()
    {
        SkipOffMac();
        string folder = PathNames.Canonical(TestFolders.Create("avalonia-ios-edits"));
        Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateCrossPlatformAsync(folder, Name)), "dotnet new avalonia.xplat", Slow);
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        string head = $"{Name}.iOS";
        string project = Path.Combine(folder, head, $"{head}.csproj");
        string manifest = Path.Combine(folder, head, "Info.plist");
        string projectMade = File.ReadAllText(project);
        string manifestMade = File.ReadAllText(manifest);
        MainWindow window = Opened(folder, project);
        try
        {
            HeadEdits edits = Edits(window, head);
            string sdk = Assert.Single(edits.Manifests).SceneSdk ?? throw Skipped($"This Mac's Xcode SDK asks no scene manifest of an app, as Xcode 27's does: {edits.Said}");
            string sentence = $"{head} needs edits to build and run here: {Moved("ios", "iOS")}; iOS 15.0 at the least, the least .NET 11's iOS workload builds for, in place "
                + $"of 13.0 in its project and Info.plist; and a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS {sdk} SDK asks of every app";
            Assert.True(window.MobileSaid?.Contains(sentence, StringComparison.Ordinal) == true, $"The banner did not name each edit of {head}: {window.MobileSaid}");
            Assert.DoesNotContain($"{head} targets net10.0-ios", window.MobileSaid, StringComparison.Ordinal);
            string named = Plural.Listed([.. window.MobileWant.Heads.Where(h => h.Makes).Select(h => h.Name)]);

            Ui.ClickButton(window, "Make the Mobile Edits");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeMobileEdits) is null, () => $"the edits to be made: {Ui.Said(window)}", Slow);
            Assert.Equal($"The mobile edits of {named} are made", window.Status);
            string projectEdited = File.ReadAllText(project);
            string manifestEdited = File.ReadAllText(manifest);
            Assert.Equal(projectMade.Replace("<TargetFramework>net10.0-ios<", "<TargetFramework>net11.0-ios<", StringComparison.Ordinal)
                .Replace("<SupportedOSPlatformVersion>13.0<", "<SupportedOSPlatformVersion>15.0<", StringComparison.Ordinal), projectEdited);
            Assert.Equal(AppManifests.WithSceneManifest(manifestMade.Replace("<string>13.0</string>", "<string>15.0</string>", StringComparison.Ordinal)), manifestEdited);
            Assert.True(window.MobileSaid?.Contains($"{head} needs", StringComparison.Ordinal) != true, $"Once made, the banner still names {head}'s edits: {window.MobileSaid}");

            Assert.Equal($"Make the Mobile Edits of {named}", window.EditorArea.Commands.ToUndo(null)?.Name);
            MigrationsTests.UndoFromExplorer(window, project);
            Ui.WaitFor(() => File.ReadAllText(project) == projectMade && File.ReadAllText(manifest) == manifestMade,
                () => $"one Undo to put back the project and Info.plist as the template made them: {window.Status}\n{File.ReadAllText(project)}\n{File.ReadAllText(manifest)}");
            Redo(window, head);
            Ui.WaitFor(() => File.ReadAllText(project) == projectEdited && File.ReadAllText(manifest) == manifestEdited,
                () => $"Redo to make the edits again: {window.Status}\n{File.ReadAllText(project)}\n{File.ReadAllText(manifest)}");
            Builds(window, project, "net11.0-ios");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void An_avalonia_apps_android_head_is_told_its_edits_and_the_fix_on_its_framework_makes_them_so_it_builds_where_a_jdk_is()
    {
        SkipOffMac();
        string folder = PathNames.Canonical(TestFolders.Create("avalonia-android-edits"));
        Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateCrossPlatformAsync(folder, Name)), "dotnet new avalonia.xplat", Slow);
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        string head = $"{Name}.Android";
        string project = Path.Combine(folder, head, $"{head}.csproj");
        string ios = Path.Combine(folder, $"{Name}.iOS", $"{Name}.iOS.csproj");
        string projectMade = File.ReadAllText(project);
        string iosMade = File.ReadAllText(ios);
        MainWindow window = Opened(folder, project);
        try
        {
            string sentence = $"{head} needs edits to build and run here: {Moved("android", "Android")}; and Android API 24 at the least, the least .NET 11's "
                + "Android workload builds for, in place of 23 in its project";
            Assert.Equal(sentence, Edits(window, head).Said);
            Assert.True(window.MobileSaid?.Contains(sentence, StringComparison.Ordinal) == true, $"The banner did not name each edit of {head}: {window.MobileSaid}");

            window.OpenFile(project);
            Ui.Settle();
            Ui.PlaceAfter(window.EditorArea.CodeEditor, "<TargetFramework>");
            Ui.Run(window, AppActions.QuickFix);
            Ui.WaitFor(() => window.Assist.Offered.Contains("Make the Mobile Edits"), () => $"the fix on {head}'s framework; offered: {string.Join(", ", window.Assist.Offered)}");
            int index = window.Assist.Offered.ToList().IndexOf("Make the Mobile Edits");
            for (int i = 0; i < index; i++)
            {
                Ui.Press(window, "DOWN");
            }
            Ui.Press(window, "ENTER");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeMobileEdits) is null && window.Status == $"The mobile edits of {head} are made",
                () => $"the fix to make {head}'s edits: {Ui.Said(window)}", Slow);
            Ui.WaitFor(() => window.EditorArea.Find(project)!.Text.Snapshot.ToString() == projectMade.Replace("net10.0-android", "net11.0-android", StringComparison.Ordinal)
                .Replace("<SupportedOSPlatformVersion>23<", "<SupportedOSPlatformVersion>24<", StringComparison.Ordinal),
                () => $"the open project to take its edits: {window.EditorArea.Find(project)!.Text.Snapshot}");
            Assert.Equal(iosMade, File.ReadAllText(ios));
            Assert.True(window.MobileSaid?.Contains($"{head} needs edits", StringComparison.Ordinal) != true, $"Once made, the banner still names {head}'s edits: {window.MobileSaid}");

            if (window.MobileWant is { Java: not null } or { NoAndroidSdk: true } or { AndroidPackages.Count: > 0 })
            {
                throw Skipped($"The edits were made, but the Android workload has no JDK or SDK here to build {head} with: {window.MobileSaid}");
            }
            Builds(window, project, "net11.0-android");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_maui_app_made_before_its_apple_heads_had_scenes_is_told_every_heads_edits_and_its_ios_and_catalyst_heads_build_once_made()
    {
        SkipOffMac();
        string folder = PathNames.Canonical(TestFolders.Create("maui-heads-edits"));
        string app = Path.Combine(folder, "M10");
        ChildResult made = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["new", "maui", "--framework", "net10.0", "--output", app,
            "--name", "M10", "--no-restore"], folder), limit: TimeSpan.FromMinutes(6))), "dotnet new maui for .NET 10", Slow);
        if (!made.Succeeded)
        {
            Assert.Skip($"This .NET makes no .NET 10 MAUI app, so it lacks MAUI's workload (dotnet workload install maui): {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        // As MAUI's templates made an app before .NET 10: no scene manifest in its Apple heads' Info.plist, and no SceneDelegate.
        foreach (string platform in (string[])["iOS", "MacCatalyst"])
        {
            string plist = Path.Combine(app, "Platforms", platform, "Info.plist");
            string text = File.ReadAllText(plist);
            string taken = SceneManifest().Replace(text, string.Empty, 1);
            Assert.True(taken != text, $"The template's {platform} Info.plist no longer has a scene manifest:\n{text}");
            File.WriteAllText(plist, taken);
            File.Delete(Path.Combine(app, "Platforms", platform, "SceneDelegate.cs"));
        }
        string project = Path.Combine(app, "M10.csproj");
        string projectMade = File.ReadAllText(project);
        MainWindow window = Opened(folder, project);
        try
        {
            HeadEdits edits = Edits(window, "M10");
            Assert.True(edits.Manifests.All(m => m.SceneSdk is not null), $"This Mac's Xcode SDK asks no scene manifest of an app, as Xcode 27's does: {edits.Said}");
            foreach (string said in (string[])["M10 needs edits to build and run here: net11.0-android, net11.0-ios, net11.0-maccatalyst and net11.0-windows10.0.19041.0 in place of",
                "; Android API 24 at the least, the least .NET 11's Android workload builds for, in place of 21 in its project;",
                "in Platforms/iOS/Info.plist, which the iOS 27", "in Platforms/MacCatalyst/Info.plist, which the Mac Catalyst 27",
                "SDK asks of every app, naming a SceneDelegate of MAUI's MauiUISceneDelegate written beside it"])
            {
                Assert.True(window.MobileSaid?.Contains(said, StringComparison.Ordinal) == true, $"The banner did not say \"{said}\": {window.MobileSaid}");
            }

            Ui.ClickButton(window, "Make the Mobile Edits");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeMobileEdits) is null, () => $"the edits to be made: {Ui.Said(window)}", Slow);
            Assert.Equal("The mobile edits of M10 are made", window.Status);
            string edited = File.ReadAllText(project);
            Assert.Contains("<TargetFrameworks>net11.0-android</TargetFrameworks>", edited, StringComparison.Ordinal);
            Assert.Contains("'android'\">24.0</SupportedOSPlatformVersion>", edited, StringComparison.Ordinal);
            Assert.Equal(edits.TrimsDebug, edited.Contains(">None</MtouchLink>", StringComparison.Ordinal));
            foreach (string platform in (string[])["iOS", "MacCatalyst"])
            {
                Assert.Contains("<string>__MAUI_DEFAULT_SCENE_CONFIGURATION__</string>", File.ReadAllText(Path.Combine(app, "Platforms", platform, "Info.plist")), StringComparison.Ordinal);
                Assert.Contains("public class SceneDelegate : MauiUISceneDelegate", File.ReadAllText(Path.Combine(app, "Platforms", platform, "SceneDelegate.cs")), StringComparison.Ordinal);
            }

            MigrationsTests.UndoFromExplorer(window, project);
            Ui.WaitFor(() => File.ReadAllText(project) == projectMade && !File.Exists(Path.Combine(app, "Platforms", "iOS", "SceneDelegate.cs")),
                () => $"one Undo to put back the project and take out the SceneDelegates written: {window.Status}");
            Redo(window, "M10");
            Ui.WaitFor(() => File.ReadAllText(project) == edited && File.Exists(Path.Combine(app, "Platforms", "MacCatalyst", "SceneDelegate.cs")),
                () => $"Redo to make the edits again: {window.Status}");
            Builds(window, project, "net11.0-ios");
            Builds(window, project, "net11.0-maccatalyst");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    [AvaloniaFact]
    public void A_mac_catalyst_app_naming_its_least_macOS_and_no_scene_manifest_is_told_its_edits_and_builds_once_made()
    {
        SkipOffMac();
        string folder = PathNames.Canonical(TestFolders.Create("catalyst-edits"));
        string app = Path.Combine(folder, "Cat");
        ChildResult made = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["new", "maccatalyst", "--Framework", "net10.0-maccatalyst",
            "--output", app, "--name", "Cat"], folder), limit: TimeSpan.FromMinutes(6))), "dotnet new maccatalyst for .NET 10", Slow);
        if (!made.Succeeded)
        {
            Assert.Skip($"This .NET makes no .NET 10 Mac Catalyst app, so it lacks the maccatalyst workload: {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        // As an app made for Mac Catalyst 15 whose Info.plist names its least macOS and gives its SceneDelegate no scene manifest.
        string project = Path.Combine(app, "Cat.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("<SupportedOSPlatformVersion>17.0<", "<SupportedOSPlatformVersion>15.0<", StringComparison.Ordinal));
        string plist = Path.Combine(app, "Info.plist");
        string text = File.ReadAllText(plist);
        int key = text.IndexOf("    <key>UIApplicationSceneManifest</key>", StringComparison.Ordinal);
        Assert.True(key >= 0, $"The template's Info.plist no longer has a scene manifest:\n{text}");
        File.WriteAllText(plist, text[..key].Replace("    <key>UIDeviceFamily</key>", "    <key>LSMinimumSystemVersion</key>\n    <string>12.0</string>\n    <key>UIDeviceFamily</key>",
            StringComparison.Ordinal) + "</dict>\n</plist>\n");
        MainWindow window = Opened(folder, project);
        try
        {
            HeadEdits edits = Edits(window, "Cat");
            string sdk = Assert.Single(edits.Manifests).SceneSdk ?? throw Skipped($"This Mac's Xcode SDK asks no scene manifest of an app, as Xcode 27's does: {edits.Said}");
            foreach (string said in (string[])[$"Cat needs edits to build and run here: {Moved("maccatalyst", "Mac Catalyst")}",
                "; Mac Catalyst 17.0 at the least, the least .NET 11's Mac Catalyst workload builds for, in place of 15.0 in its project;",
                "; macOS 14.0 as Info.plist's LSMinimumSystemVersion, Mac Catalyst 17.0's, in place of 12.0",
                $"a scene manifest (UIApplicationSceneManifest) in Info.plist, which the Mac Catalyst {sdk} SDK asks of every app, naming its SceneDelegate"])
            {
                Assert.True(window.MobileSaid?.Contains(said, StringComparison.Ordinal) == true, $"The banner did not say \"{said}\": {window.MobileSaid}");
            }

            Ui.ClickButton(window, "Make the Mobile Edits");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeMobileEdits) is null, () => $"the edits to be made: {Ui.Said(window)}", Slow);
            Assert.Equal("The mobile edits of Cat are made", window.Status);
            string edited = File.ReadAllText(plist);
            Assert.Contains("<key>LSMinimumSystemVersion</key>\n    <string>14.0</string>", edited, StringComparison.Ordinal);
            Assert.Contains("<string>Default Configuration</string>", edited, StringComparison.Ordinal);
            Assert.Contains("<SupportedOSPlatformVersion>17.0</SupportedOSPlatformVersion>", File.ReadAllText(project), StringComparison.Ordinal);
            Builds(window, project, "net11.0-maccatalyst");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    private static void SkipOffMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone);
        }
    }

    /// <summary>A window on the folder once the head's project has evaluated and what the heads need has been asked;
    /// skipped where this .NET lacks a workload they need.</summary>
    private static MainWindow Opened(string folder, string project)
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        Ui.WaitFor(() => window.Projects.Any(p => PathNames.Same(p.Path, project) && p.IsEvaluated), () => $"{Path.GetFileName(project)} to evaluate: {Ui.Said(window)}", Slow);
        Ui.Await(window.MobileAsked(), "what the app's heads need to be asked", Slow);
        if (window.MobileWant.Workloads.Count > 0 || window.MobileWant.Xcode is not null)
        {
            window.Close();
            Assert.Skip($"This .NET lacks a workload of the app's heads, or Xcode, so its heads are not asked of: {window.MobileSaid}");
        }
        return window;
    }

    /// <summary>The edits the banner names of a project's heads.</summary>
    private static HeadEdits Edits(MainWindow window, string name) =>
        window.MobileWant.Heads.SingleOrDefault(h => h.Name == name) ?? throw new Xunit.Sdk.XunitException($"The banner names no edit of {name}: {window.MobileSaid}");

    /// <summary>Redo from the project's row in the Explorer, which Undo left chosen.</summary>
    private static void Redo(MainWindow window, string name)
    {
        Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith(name, StringComparison.Ordinal)));
        Ui.Run(window, Passenger.Editor.EditorActions.Redo);
    }

    /// <summary>The project built for one of its heads through the window, which succeeds.</summary>
    private static void Builds(MainWindow window, string project, string framework)
    {
        Ui.WaitFor(() => window.Projects.Any(p => PathNames.Same(p.Path, project) && p.TargetFrameworks.Contains(framework, StringComparer.OrdinalIgnoreCase)),
            () => $"{Path.GetFileName(project)} to be evaluated again with {framework}: {Ui.Said(window)}", Slow);
        BuildOutcome? built = null;
        window.Builds.Build(BuildKind.Build, [project], outcome => built = outcome, framework);
        Ui.WaitFor(() => built is not null, () => $"{Path.GetFileName(project)}'s {framework} build to end: {Ui.Said(window)}", Slow);
        Assert.True(built!.Succeeded, $"{Path.GetFileName(project)} did not build for {framework} once its edits were made: {built.Message}\n{string.Join('\n', built.Output)}");
    }

    /// <summary>A scene manifest at the top of an Info.plist, with its line, whichever line breaks the template wrote.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\t<key>UIApplicationSceneManifest</key>\r?\n\t<dict>.*?\r?\n\t</dict>\r?\n", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex SceneManifest();

    /// <summary>A skip thrown where a test cannot go on, saying why.</summary>
    private static Exception Skipped(string why)
    {
        Assert.Skip(why);
        return new InvalidOperationException(why);
    }
}
