using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>A folder the person has not trusted, as their own window opens it: the question asked once and kept, a parent
/// trusted covering the folders in it, none of the folder's code run (its targets, its Git configuration's programs, the C#
/// server, its own TypeScript) and no remote reached until Trust Folder, and then each; its trust taken back in Settings,
/// and its answer forgotten.</summary>
public sealed class WorkspaceTrustTests
{
    [AvaloniaFact]
    public void An_untrusted_folder_runs_none_of_its_code_until_trusted_then_its_targets_git_monitor_and_language_server_run()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-"));
        // Outside the folder, whose watcher would hear each mark and have Git's status read again, marking it again.
        string marks = PathNames.Canonical(TestFolders.Create("untrusted-marks-"));
        string built = Path.Combine(marks, "built.marker");
        string monitored = Path.Combine(marks, "monitored.marker");
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"Hello\");\n");
        File.WriteAllText(Path.Combine(folder, "Directory.Build.targets"),
            $"<Project>\n  <Target Name=\"Mark\" BeforeTargets=\"CoreCompile\">\n    <Exec Command=\"echo ran &gt; &quot;{built}&quot;\" />\n  </Target>\n</Project>\n");
        TestGit.Run(folder, "init", "-q");
        TestGit.Run(folder, "add", "Program.cs");
        string monitor = Path.Combine(folder, ".git", "monitor.sh");
        File.WriteAllText(monitor, $"#!/bin/sh\necho ran > '{monitored.Replace('\\', '/')}'\nexit 1\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(monitor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        TestGit.Run(folder, "config", GitConfigKeys.CoreFsmonitor, monitor.Replace('\\', '/'));
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Ui.Settle();

            Assert.Equal(Refusals.UntrustedSaid, window.TrustSaid);
            Assert.True(window.Vcs.Branch is { Length: > 0 }, "Git's status was not read in the folder not trusted, where it runs without the repository's programs");
            Assert.False(File.Exists(monitored), "Git ran the repository's fsmonitor in a folder not trusted");
            Assert.Null(window.Session.CSharp);
            Assert.Equal(0, window.Session.Models.Evaluations);
            Assert.Equal(0, window.Session.DesignTimeBuilds.Builds);
            Assert.False(File.Exists(built), "A design-time build ran Directory.Build.targets' Exec in a folder not trusted");
            foreach (string action in (string[])[AppActions.Build, AppActions.RunProgram, AppActions.Debug, AppActions.RunAllTests, AppActions.Profile,
                AppActions.Push, AppActions.Fetch, AppActions.UpdateProject])
            {
                Assert.Equal(Refusals.Untrusted, window.Actions.WhyNot(action, window.Actions.Context()));
            }

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title);
            Ui.WaitFor(() => File.Exists(monitored), "Git's status to run the repository's fsmonitor once the folder is trusted");
            Ui.WaitFor(() => File.Exists(built), () => $"a design-time build to run Directory.Build.targets' Exec once the folder is trusted (the C# server {window.Session.CSharp?.State ?? "never started"})",
                TimeSpan.FromMinutes(3));
            Assert.Null(window.TrustSaid);
            Assert.True(window.Session.Models.Evaluations > 0, "The projects were not evaluated once the folder was trusted");
            Assert.Null(window.Actions.WhyNot(AppActions.Build, window.Actions.Context()));
            Assert.Null(window.Actions.WhyNot(AppActions.Fetch, window.Actions.Context()));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void The_trust_question_is_asked_once_and_kept_and_a_parent_folder_trusted_covers_the_folders_in_it()
    {
        string parent = PathNames.Canonical(TestFolders.Create("trusted-parent-"));
        string first = Directory.CreateDirectory(Path.Combine(parent, "First")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(parent, "Second")).FullName;
        string elsewhere = PathNames.Canonical(TestFolders.Create("declined-"));
        foreach (string folder in (string[])[first, second, elsewhere])
        {
            File.WriteAllText(Path.Combine(folder, "README.md"), "# A folder\n");
        }
        string state = TestFolders.Create("trust-state-");
        using IDisposable askedParent = WorkspaceTrust.Ask(parent);
        using IDisposable askedElsewhere = WorkspaceTrust.Ask(elsewhere);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = state };
        window.Show();
        window.KeepLayout();
        try
        {
            Open(window, first);
            DialogButtonsTests.Check(Asked(window));
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Ui.Settle();
            Assert.False(window.Session.Trusted);
            Assert.Equal(Refusals.UntrustedSaid, window.TrustSaid);

            Open(window, elsewhere);
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Open(window, first);
            Assert.Null(window.AskingTrust);
            Assert.Equal(Refusals.UntrustedSaid, window.TrustSaid);

            Open(window, second);
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.TrustParent);
            Ui.Settle();
            Assert.True(window.Session.Trusted, "The folder whose parent was trusted stayed untrusted");
            Assert.Null(window.TrustSaid);

            Open(window, first);
            Assert.Null(window.AskingTrust);
            Assert.True(window.Session.Trusted, "A folder in the parent trusted opened untrusted, though it was declined before its parent was trusted");
            Assert.Null(window.TrustSaid);

            Open(window, elsewhere);
            Assert.Null(window.AskingTrust);
            Assert.False(window.Session.Trusted);
        }
        finally
        {
            window.Close();
        }
        using System.Text.Json.JsonDocument kept = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "trust.json")));
        Assert.Equal([parent], kept.RootElement.GetProperty("trusted").EnumerateArray().Select(f => f.GetString()));
        Assert.Equal([elsewhere], kept.RootElement.GetProperty("untrusted").EnumerateArray().Select(f => f.GetString()));
    }

    [AvaloniaFact]
    public void An_untrusted_folder_is_served_by_the_carried_TypeScript_until_trusted_then_by_its_own()
    {
        WebLanguagesTests.SkipWithoutNode(typescript: true);
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-typescript-"));
        string ran = Path.Combine(folder, "tsserver.marker");
        string own = Path.Combine(folder, "node_modules", "typescript");
        WebLanguagesTests.CopyFolder(Path.Combine(Passenger.App.Web.WebLanguages.Modules, "typescript"), own);
        string server = Path.Combine(own, "lib", "tsserver.js");
        File.WriteAllText(server, $"require('fs').writeFileSync({System.Text.Json.JsonSerializer.Serialize(ran)}, 'ran');\n" + File.ReadAllText(server));
        File.WriteAllText(Path.Combine(folder, "tsconfig.json"), "{ \"compilerOptions\": { \"strict\": true } }\n");
        File.WriteAllText(Path.Combine(folder, "main.ts"), "const n: number = 1;\n");
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false, autosaves: false);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            window.OpenFile(Path.Combine(folder, "main.ts"));
            Ui.WaitFor(() => window.LanguageShown?.Contains("(Passenger's)", StringComparison.Ordinal) == true,
                () => $"the status bar to name Passenger's TypeScript in the folder not trusted ({window.LanguageShown})");
            Assert.False(File.Exists(ran), "The folder's own tsserver ran before the folder was trusted");

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title);
            Ui.WaitFor(() => File.Exists(ran), () => $"the folder's own tsserver to run once it is trusted ({window.LanguageShown})");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Trust_taken_back_in_Settings_ends_the_language_server_says_what_runs_on_and_forgotten_asks_again_at_the_next_open()
    {
        string folder = PathNames.Canonical(TestFolders.Create("taken-back-"));
        string elsewhere = PathNames.Canonical(TestFolders.Create("taken-back-elsewhere-"));
        File.WriteAllText(Path.Combine(elsewhere, "README.md"), "# Another folder\n");
        File.WriteAllText(Path.Combine(folder, "Shop.csproj"), TestProjects.Console());
        File.WriteAllText(Path.Combine(folder, "Program.cs"), "System.Console.WriteLine(\"waiting\");\nSystem.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);\n");
        string state = TestFolders.Create("trust-state-");
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        using IDisposable askedElsewhere = WorkspaceTrust.Ask(elsewhere);
        MainWindow window = new() { Width = 1200, Height = 800, StateFolder = state };
        window.Show();
        window.KeepLayout();
        try
        {
            Open(window, folder);
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Trust);
            Ui.Settle();
            Assert.True(window.Session.Trusted, "The folder trusted at its question stayed untrusted");
            Assert.NotNull(window.Session.CSharp);
            Assert.True(window.Perform(AppActions.RunProgram), $"Run was refused in the folder trusted: {window.Status}");
            Ui.WaitFor(() => window.Runner.IsRunning && window.BottomPanel.Run.Document.Snapshot.ToString().Contains("waiting", StringComparison.Ordinal),
                () => $"the program to run: {window.Status}", TimeSpan.FromMinutes(3));

            SettingsDialog settings = window.ShowSettings();
            Ui.Settle();
            settings.Page = SettingsDialog.TrustPage;
            Ui.Settle();
            TrustedFoldersTable.Row row = settings.TrustedFolders.Of(folder)
                ?? throw new InvalidOperationException($"Trusted Folders lists no {folder}: [{string.Join(", ", settings.TrustedFolders.Rows.Select(r => r.Folder))}]");
            Assert.True(row.Trusted.IsChecked == true, "The folder trusted was listed as not trusted");
            Ui.Click(row.Trusted);
            Ui.ClickButton(settings, "Apply");
            Ui.Settle();

            Assert.False(window.Session.Trusted, "The folder's window stayed trusted once its trust was taken back");
            Assert.Null(window.Session.CSharp);
            Assert.Equal(Refusals.Untrusted, window.Actions.WhyNot(AppActions.Build, window.Actions.Context()));
            Assert.True(window.Runner.IsRunning, "Taking the trust back ended the program the person started");
            Assert.Equal(Refusals.UntrustedRunsOn(["the program"]), window.TrustSaid);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the program to stop");
            Ui.WaitFor(() => window.TrustSaid == Refusals.UntrustedSaid, () => $"the banner to say only that the folder is not trusted once the program ended ({window.TrustSaid})");

            Ui.Click(row.Forget);
            Assert.Null(settings.TrustedFolders.Of(folder));
            Ui.ClickButton(settings, "OK");
            Ui.Settle();
            Open(window, elsewhere);
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Open(window, folder);
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Ui.Settle();
            Assert.False(window.Session.Trusted);
        }
        finally
        {
            window.Close();
        }
        using System.Text.Json.JsonDocument kept = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "trust.json")));
        Assert.Empty(kept.RootElement.GetProperty("trusted").EnumerateArray());
        Assert.Equal([elsewhere, folder], kept.RootElement.GetProperty("untrusted").EnumerateArray().Select(f => f.GetString()));
    }

    [AvaloniaFact]
    public void A_project_added_to_a_solution_not_trusted_is_made_without_its_restore_which_would_run_the_folders_MSBuild_files()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-new-project-"));
        string marks = PathNames.Canonical(TestFolders.Create("untrusted-new-project-marks-"));
        string restored = Path.Combine(marks, "restored.marker");
        TestFolders.Write(folder, "Shop.slnx", "<Solution />\n");
        TestFolders.Write(folder, "Directory.Build.targets",
            $"<Project>\n  <Target Name=\"Mark\" BeforeTargets=\"Restore;_GenerateRestoreGraph\">\n    <Exec Command=\"echo ran &gt; &quot;{restored}&quot;\" />\n  </Target>\n</Project>\n");
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Ui.Settle();
            Ui.Run(window, AppActions.NewProject);
            Passenger.App.Templates.NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            NewServiceSetupTests.Choose(dialog, "classlib");
            dialog.ProjectName = "Lib";
            dialog.Framework = TestProjects.Framework;
            NewServiceSetupTests.Created(dialog, "the class library");

            Assert.Equal($"Lib did not restore: {Refusals.Untrusted}", NewServiceSetupTests.SetUp(window));
            Assert.True(File.Exists(Path.Combine(folder, "Lib", "Lib.csproj")), $"No Lib.csproj was made: {window.Status}");
            Assert.Contains("Lib/Lib.csproj", File.ReadAllText(Path.Combine(folder, "Shop.slnx")), StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(folder, "Lib", "obj", "project.assets.json")), "The project made in the folder not trusted was restored");
            Assert.False(File.Exists(restored), "A restore ran Directory.Build.targets' Exec in the folder not trusted");
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A mobile project the folder's cache kept from a trusted open is shown as kept when the folder opens again
    /// without the person trusting it, and asks MSBuild nothing: the workload's question the window asks of a mobile
    /// project (a stand-in target marking that it ran) never runs, and Choose Where It Runs, whose listing restores the
    /// project, is refused saying why; trusted, the question runs.</summary>
    [AvaloniaFact]
    public void A_mobile_project_kept_from_before_is_shown_in_a_folder_not_trusted_and_none_of_its_targets_run_until_trusted()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-kept-"));
        string marks = PathNames.Canonical(TestFolders.Create("untrusted-kept-marks-"));
        string state = TestFolders.Create("untrusted-kept-state-");
        string asked = Path.Combine(marks, "asked.marker");
        // A solution's projects are the window's as it opens, before any walk of the folder.
        TestFolders.Write(folder, "Droid.slnx", "<Solution>\n  <Project Path=\"Droid/Droid.csproj\" />\n</Solution>\n");
        TestFolders.Write(folder, "Droid/Droid.csproj", $"""
            <Project>
              <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
              <PropertyGroup>
                <TargetFramework>net{Passenger.App.Mobile.MobilePlatforms.FirstOnCoreClr}.0-android</TargetFramework>
                <OutputType>Exe</OutputType>
              </PropertyGroup>
              <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
              <Target Name="_GetRequiredWorkloads">
                <Touch Files="{asked}" AlwaysCreate="true" />
              </Target>
            </Project>
            """);
        MainWindow trusted = Keeping(state, folder);
        try
        {
            Ui.WaitFor(() => trusted.Projects.Any(p => p.IsEvaluated), () => $"the project to evaluate: {Ui.Said(trusted)}", TimeSpan.FromMinutes(2));
            Ui.Await(trusted.MobileAsked(), "what the project needs to be asked", TimeSpan.FromMinutes(2));
            Assert.True(File.Exists(asked), "The workload's question of the mobile project did not run in the folder trusted, so the test sees nothing");
        }
        finally
        {
            trusted.Close();
        }
        File.Delete(asked);

        using IDisposable held = WorkspaceTrust.Ask(folder);
        MainWindow window = Keeping(state, folder);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Ui.Settle();
            Ui.Await(window.MobileAsked(), "what the project needs to be asked");
            Assert.True(window.Projects.Any(p => p.Name == "Droid" && p.IsEvaluated), "The project the folder's cache kept was not shown as kept");
            Assert.NotNull(window.Session.Configurations.Chosen);
            Assert.Equal(0, window.Session.Models.Evaluations);
            Assert.False(File.Exists(asked), "MSBuild ran the mobile project's target in the folder not trusted, from what its cache kept");
            Assert.Equal(Refusals.Untrusted, window.Actions.WhyNot(AppActions.ChooseRunTarget, window.Actions.Context()));

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title);
            Ui.WaitFor(() => File.Exists(asked), () => $"the workload's question to run once the folder is trusted: {Ui.Said(window)}", TimeSpan.FromMinutes(2));
        }
        finally
        {
            window.Close();
            TestFolders.Delete(state);
        }
    }

    /// <summary>A window that keeps its state in a folder of its own, on the folder given.</summary>
    private static MainWindow Keeping(string state, string folder)
    {
        MainWindow window = new() { Width = 1000, Height = 800, StartsLanguageServer = false, StateFolder = state };
        window.Show();
        window.KeepLayout();
        window.OpenFolder(folder);
        Ui.Settle();
        return window;
    }

    /// <summary>A folder not trusted whose global.json names an SDK it carries (its sdk.paths, a stand-in marking that it ran,
    /// on the runtime of the .NET the run takes) never has it run: New Project reads the templates and makes a class library
    /// there with the SDK Passenger takes everywhere else, the folder's dotnet run outside it; trusted, New Project's read of
    /// the templates takes the SDK its global.json names, which runs.</summary>
    [AvaloniaFact]
    public void A_folder_not_trusted_never_runs_the_sdk_its_global_json_names_until_trusted()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-sdk-"));
        string marks = PathNames.Canonical(TestFolders.Create("untrusted-sdk-marks-"));
        string ran = Path.Combine(marks, "ran.marker");
        string version = Ui.Await(Dotnet.SdkAsync(marks), "the SDK the run takes");
        string carried = Path.Combine(folder, ".sdk");
        TestFolders.Write(marks, "Stand/Stand.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                <AssemblyName>dotnet</AssemblyName>
                <UseAppHost>false</UseAppHost>
              </PropertyGroup>
            </Project>
            """);
        TestFolders.Write(marks, "Stand/Program.cs", $"System.IO.File.AppendAllText({System.Text.Json.JsonSerializer.Serialize(ran)}, string.Join(' ', args) + \"\\n\");\nSystem.Console.WriteLine(\"the folder's own SDK\");\n");
        ChildResult built = Ui.Await(ChildProcesses.RunAsync(ChildProcesses.DotnetCommand(["build", Path.Combine(marks, "Stand", "Stand.csproj"), "-o",
            Path.Combine(carried, "sdk", version), "-nologo", "-v:q"]), limit: Restore.Limit.Scaled()), "the carried SDK's build", Restore.Limit + TestFolders.Spare);
        Assert.True(built.Succeeded, $"The carried SDK did not build: {built.Said}");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(carried, "shared"), Path.Combine(Dotnet.Root(), "shared"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Assert.Skip($"The carried SDK takes its runtime through a link this account may not make: {error.Message}");
        }
        TestFolders.Write(folder, "global.json", $$"""{ "sdk": { "version": "{{version}}", "rollForward": "disable", "paths": [ ".sdk" ] } }""" + "\n");
        TestFolders.Write(folder, "Shop.slnx", "<Solution />\n");
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Ui.Settle();
            Ui.Run(window, AppActions.NewProject);
            Passenger.App.Templates.NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            NewServiceSetupTests.Choose(dialog, "classlib");
            dialog.ProjectName = "Lib";
            dialog.Framework = TestProjects.Framework;
            NewServiceSetupTests.Created(dialog, "the class library");

            Assert.True(File.Exists(Path.Combine(folder, "Lib", "Lib.csproj")), $"No Lib.csproj was made: {window.Status}");
            Assert.False(File.Exists(ran), $"The SDK the folder's global.json names ran in the folder not trusted, asked {(File.Exists(ran) ? File.ReadAllText(ran) : null)}");

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title);
            Ui.Settle();
            Ui.Run(window, AppActions.NewProject);
            Ui.WaitFor(() => File.Exists(ran), () => $"New Project's read of the templates to run the SDK the folder's global.json names once it is trusted: {Ui.Said(window)}",
                TimeSpan.FromMinutes(2));
            Assert.Single(window.OwnedWindows.OfType<Passenger.App.Templates.NewProjectDialog>(), d => d.IsVisible).Close();
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A folder opened in the window, once its opening has landed.</summary>
    private static void Open(MainWindow window, string folder)
    {
        window.OpenFolder(folder);
        Ui.Settle();
    }

    /// <summary>The trust question the window asks now, failing where it asks none.</summary>
    private static WorkspaceTrustDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        return window.AskingTrust!;
    }
}
