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
/// restored from nuget.org: Avalonia's cross-platform app's iOS head, and a MAUI app made before its template gave its Apple
/// heads scenes (a SceneDelegate made since the banner asked kept as it is). Each skips, saying why, off a Mac or without its
/// workloads.</summary>
[AboutMacOS]
public sealed partial class MobileHeadEditsTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(8);

    private const string Name = "XApp";

    /// <summary>What the banner says of a .NET 10 head's move to the first .NET on CoreCLR.</summary>
    private static string Moved(string framework, string platform) =>
        $"net11.0-{framework} in place of net10.0-{framework}, whose {platform} apps run on Mono (Passenger builds, runs and debugs mobile apps from .NET 11, on CoreCLR)";

    /// <summary>The banner names each edit a head needs, Make the Mobile Edits makes them as one step of Undo keeping a file
    /// the person made since, and the heads then build.</summary>
    [AvaloniaFact]
    public void A_heads_edits_are_named_and_made_as_one_step_of_Undo_keeping_what_the_person_made_and_the_head_then_builds()
    {
        Flow.StepApart<MobileHeadEditsTests>("an Avalonia app's iOS head is told each edit it needs and builds once Make the Mobile Edits made them as one step of Undo",
            t => t.An_avalonia_apps_ios_head_is_told_each_edit_it_needs_and_builds_once_Make_the_Mobile_Edits_made_them_as_one_step_of_Undo());
        Flow.StepApart<MobileHeadEditsTests>("a MAUI app made before its Apple heads had scenes is told every head's edits, and its iOS and Catalyst heads build once made",
            t => t.A_maui_app_made_before_its_apple_heads_had_scenes_is_told_every_heads_edits_and_its_ios_and_catalyst_heads_build_once_made());
        Flow.StepApart<MobileHeadEditsTests>("a SceneDelegate made since the banner asked is kept as it is by Make the Mobile Edits, which says so",
            t => t.A_scene_delegate_made_since_the_banner_asked_is_kept_as_it_is_by_Make_the_Mobile_Edits_which_says_so());
    }

    private void An_avalonia_apps_ios_head_is_told_each_edit_it_needs_and_builds_once_Make_the_Mobile_Edits_made_them_as_one_step_of_Undo()
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

    private void A_maui_app_made_before_its_apple_heads_had_scenes_is_told_every_heads_edits_and_its_ios_and_catalyst_heads_build_once_made()
    {
        (string folder, string app, string project) = MauiBeforeScenes("maui-heads-edits");
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

            Assert.DoesNotContain("MtouchLink", window.MobileSaid, StringComparison.Ordinal);

            Ui.ClickButton(window, "Make the Mobile Edits");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeMobileEdits) is null, () => $"the edits to be made: {Ui.Said(window)}", Slow);
            Assert.Equal("The mobile edits of M10 are made", window.Status);
            string edited = File.ReadAllText(project);
            Assert.Contains("<TargetFrameworks>net11.0-android</TargetFrameworks>", edited, StringComparison.Ordinal);
            Assert.Contains("'android'\">24.0</SupportedOSPlatformVersion>", edited, StringComparison.Ordinal);
            Assert.DoesNotContain("MtouchLink", edited, StringComparison.Ordinal);
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
            // Its Mac Catalyst head's Debug build trims .NET's own assemblies, as MAUI's does: the fix on its framework's line keeps them whole.
            Ui.WaitFor(() => window.MobileWant.Trimmed.Any(h => h.Name == "M10"), () => $"M10's Mac Catalyst Debug build to be found trimmed: {Ui.Said(window)}", Slow);
            {
                Choose(window, project, "net11.0-maccatalyst", "Keep .NET's Assemblies Whole in Debug");
                Ui.WaitFor(() => File.ReadAllText(project).Contains("== 'maccatalyst'\">None</MtouchLink>", StringComparison.Ordinal),
                    () => $"the fix to keep the Mac Catalyst head's Debug build whole: {Ui.Said(window)}\n{File.ReadAllText(project)}", Slow);
                Assert.Equal(MobileHeads.Whole("M10"), window.Status);
            }
            Builds(window, project, "net11.0-ios");
            Builds(window, project, "net11.0-maccatalyst");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A SceneDelegate the person made after the banner asked for one to be written, before the edits are made:
    /// Make the Mobile Edits keeps it as it is, writing the other head's, and says so.</summary>
    private void A_scene_delegate_made_since_the_banner_asked_is_kept_as_it_is_by_Make_the_Mobile_Edits_which_says_so()
    {
        (string folder, string app, string project) = MauiBeforeScenes("maui-scene-made-since");
        MainWindow window = Opened(folder, project);
        try
        {
            HeadEdits edits = Edits(window, "M10");
            Assert.True(edits.Manifests.Count(m => m.DelegateFile is not null) == 2, $"The banner did not say a SceneDelegate is to be written for each Apple head: {edits.Said}");
            string own = Path.Combine(app, "Platforms", "MacCatalyst", "SceneDelegate.cs");
            const string Theirs = "using Foundation;\n\nnamespace M10;\n\n[Register(\"SceneDelegate\")]\npublic class SceneDelegate : MauiUISceneDelegate\n{\n    // Its own.\n}\n";
            File.WriteAllText(own, Theirs);

            Ui.ClickButton(window, "Make the Mobile Edits");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeMobileEdits) is null, () => $"the edits to be made: {Ui.Said(window)}", Slow);
            Assert.True(File.ReadAllText(own) == Theirs, $"The SceneDelegate made since the banner asked was written over:\n{File.ReadAllText(own)}");
            Assert.Contains("public class SceneDelegate : MauiUISceneDelegate", File.ReadAllText(Path.Combine(app, "Platforms", "iOS", "SceneDelegate.cs")), StringComparison.Ordinal);
            Assert.Contains("<string>__MAUI_DEFAULT_SCENE_CONFIGURATION__</string>", File.ReadAllText(Path.Combine(app, "Platforms", "MacCatalyst", "Info.plist")), StringComparison.Ordinal);
            Assert.True(window.Status?.StartsWith("The mobile edits of M10 are made; ", StringComparison.Ordinal) == true
                && window.Status.EndsWith("/MacCatalyst/SceneDelegate.cs, made since the edits were read, is kept as it was", StringComparison.Ordinal),
                $"The status bar did not say the SceneDelegate made since was kept: {window.Status}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>MAUI's .NET 10 app as its templates made it before .NET 10, in a folder of its own: no scene manifest in its
    /// Apple heads' Info.plist, and no SceneDelegate; skipped off a Mac or without MAUI's workload.</summary>
    private static (string Folder, string App, string Project) MauiBeforeScenes(string name)
    {
        SkipOffMac();
        string folder = PathNames.Canonical(TestFolders.Create(name));
        string app = Path.Combine(folder, "M10");
        ChildResult made = Ui.AwaitPumped(Waiting.Apart(() => ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["new", "maui", "--framework", "net10.0", "--output", app,
            "--name", "M10", "--no-restore"], folder), limit: TimeSpan.FromMinutes(6))), "dotnet new maui for .NET 10", Slow);
        if (!made.Succeeded)
        {
            TestFolders.Delete(folder);
            Assert.Skip($"This .NET makes no .NET 10 MAUI app, so it lacks MAUI's workload (dotnet workload install maui): {made.FirstLine}");
        }
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        foreach (string platform in (string[])["iOS", "MacCatalyst"])
        {
            string plist = Path.Combine(app, "Platforms", platform, "Info.plist");
            string text = File.ReadAllText(plist);
            string taken = SceneManifest().Replace(text, string.Empty, 1);
            Assert.True(taken != text, $"The template's {platform} Info.plist no longer has a scene manifest:\n{text}");
            File.WriteAllText(plist, taken);
            File.Delete(Path.Combine(app, "Platforms", platform, "SceneDelegate.cs"));
        }
        return (folder, app, Path.Combine(app, "M10.csproj"));
    }

    private static void SkipOffMac()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone.Shown);
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

    /// <summary>A fix chosen from Show Context Actions with the caret after some text of a file it opens.</summary>
    private static void Choose(MainWindow window, string file, string after, string fix)
    {
        window.OpenFile(file);
        Ui.Settle();
        Ui.PlaceAfter(window.EditorArea.CodeEditor, after);
        Ui.Run(window, AppActions.QuickFix);
        Ui.WaitFor(() => window.Assist.Offered.Contains(fix), () => $"{fix} after {after}; offered: {string.Join(", ", window.Assist.Offered)}");
        int index = window.Assist.Offered.ToList().IndexOf(fix);
        for (int i = 0; i < index; i++)
        {
            Ui.Press(window, "DOWN");
        }
        Ui.Press(window, "ENTER");
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
