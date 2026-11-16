using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Mobile;
using Passenger.App.Run;
using Passenger.Core;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>Avalonia's cross-platform solution (<c>dotnet new avalonia.xplat</c>) as its template makes it, opened on a Mac
/// whose Xcode's SDK asks every app for a scene manifest: the banner names each edit its iOS head needs to build here (its
/// framework moved from Mono to CoreCLR, its least iOS raised in its project and Info.plist, a scene manifest in Info.plist),
/// Make the iOS Edits makes them as one command Undo takes back and Redo makes again, and the head then builds, restored
/// from nuget.org. Skips, saying why, off a Mac, without the iOS workload, or with an Xcode whose SDK asks no scene
/// manifest.</summary>
public sealed class AvaloniaIosHeadTests
{
    private static readonly TimeSpan Slow = TimeSpan.FromMinutes(8);

    private const string Name = "XApp";

    [AvaloniaFact]
    public void An_avalonia_apps_ios_head_is_told_each_edit_it_needs_and_builds_once_Make_the_iOS_Edits_made_them_as_one_step_of_Undo()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip(Refusals.IosOnMacAlone);
        }
        string folder = PathNames.Canonical(TestFolders.Create("avalonia-ios-edits"));
        Ui.AwaitPumped(Waiting.Apart(() => AvaloniaApp.CreateCrossPlatformAsync(folder, Name)), "dotnet new avalonia.xplat", Slow);
        TestFolders.Write(folder, "nuget.config", TestRun.FromNuGetOrg);
        string head = $"{Name}.iOS";
        string project = Path.Combine(folder, head, $"{head}.csproj");
        string manifest = Path.Combine(folder, head, "Info.plist");
        string projectMade = File.ReadAllText(project);
        string manifestMade = File.ReadAllText(manifest);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.WaitFor(() => window.Projects.Any(p => PathNames.Same(p.Path, project) && p.IsEvaluated), () => $"{head} to evaluate: {Ui.Said(window)}", Slow);
            Ui.Await(window.MobileAsked(), "what the app's heads need to be asked", Slow);
            if (window.MobileWant.Workloads.Count > 0)
            {
                Assert.Skip($"This .NET lacks a workload of the app's heads, so its iOS head is not asked of: {window.MobileSaid}");
            }
            if (window.MobileWant.Xcode is { } xcode)
            {
                Assert.Skip($"The iOS workload cannot build with this Mac's Xcode: {xcode}");
            }
            IosHeadEdits edits = window.MobileWant.IosHeads.SingleOrDefault(h => h.Name == head)
                ?? throw new Xunit.Sdk.XunitException($"The banner names no edit of {head}: {window.MobileSaid}");
            if (edits.SceneSdk is null)
            {
                Assert.Skip($"This Mac's Xcode SDK asks no scene manifest of an app, as Xcode 27's does: {edits.Said}");
            }
            string sentence = $"{head} needs edits to build here: net11.0-ios in place of net10.0-ios, whose iOS apps run on Mono (Passenger builds, runs and debugs "
                + "mobile apps from .NET 11, on CoreCLR); iOS 15.0 at the least, the least .NET 11's iOS workload builds for, in place of 13.0 in its project and "
                + $"Info.plist; and a scene manifest (UIApplicationSceneManifest) in Info.plist, which the iOS {edits.SceneSdk} SDK asks of every app";
            Assert.True(window.MobileSaid?.Contains(sentence, StringComparison.Ordinal) == true, $"The banner did not name each edit of {head}: {window.MobileSaid}");
            Assert.DoesNotContain($"{head} targets net10.0-ios", window.MobileSaid, StringComparison.Ordinal);

            Ui.ClickButton(window, "Make the iOS Edits");
            Ui.WaitFor(() => window.Actions.Work.Of(AppActions.MakeIosEdits) is null, () => $"the edits to be made: {Ui.Said(window)}", Slow);
            Assert.Equal($"The iOS edits of {head} are made", window.Status);
            string projectEdited = File.ReadAllText(project);
            string manifestEdited = File.ReadAllText(manifest);
            Assert.Equal(projectMade.Replace("<TargetFramework>net10.0-ios<", "<TargetFramework>net11.0-ios<", StringComparison.Ordinal)
                .Replace("<SupportedOSPlatformVersion>13.0<", "<SupportedOSPlatformVersion>15.0<", StringComparison.Ordinal), projectEdited);
            Assert.Equal(AppManifests.WithSceneManifest(manifestMade.Replace("<string>13.0</string>", "<string>15.0</string>", StringComparison.Ordinal)), manifestEdited);
            Assert.True(window.MobileSaid?.Contains(head, StringComparison.Ordinal) != true, $"Once made, the banner still names {head}: {window.MobileSaid}");

            Assert.Equal($"Make the iOS Edits of {head}", window.EditorArea.Commands.ToUndo(null)?.Name);
            MigrationsTests.UndoFromExplorer(window, project);
            Ui.WaitFor(() => File.ReadAllText(project) == projectMade && File.ReadAllText(manifest) == manifestMade,
                () => $"one Undo to put back the project and Info.plist as the template made them: {window.Status}\n{File.ReadAllText(project)}\n{File.ReadAllText(manifest)}");
            Ui.ClickRow(window, window.ExplorerView.Tree.Rows.First(r => r.Name.StartsWith(head, StringComparison.Ordinal)));
            Ui.Run(window, Passenger.Editor.EditorActions.Redo);
            Ui.WaitFor(() => File.ReadAllText(project) == projectEdited && File.ReadAllText(manifest) == manifestEdited,
                () => $"Redo to make the edits again: {window.Status}\n{File.ReadAllText(project)}\n{File.ReadAllText(manifest)}");
            Ui.WaitFor(() => window.Projects.Any(p => PathNames.Same(p.Path, project) && p.TargetFrameworks.SequenceEqual(["net11.0-ios"])),
                () => $"{head} to be evaluated again on net11.0-ios: {Ui.Said(window)}", Slow);

            BuildOutcome? built = null;
            window.Builds.Build(BuildKind.Build, [project], outcome => built = outcome);
            Ui.WaitFor(() => built is not null, () => $"{head}'s build to end: {Ui.Said(window)}", Slow);
            Assert.True(built!.Succeeded, $"{head} did not build once its edits were made: {built.Message}\n{string.Join('\n', built.Output)}");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }
}
