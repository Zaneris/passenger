using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Passenger.App;
using Passenger.App.ToolWindows;
using Passenger.App.Vcs;
using Passenger.App.Settings;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>A folder the person has not trusted, as their own window opens it: the question asked once and kept, a parent
/// trusted covering the folders in it, a folder in a Git repository trusted with the repository, none of the folder's code
/// run (its targets, its Git configuration's programs, the C# server, its own TypeScript) and no remote reached until Trust
/// Folder, and then each; its trust taken back in Settings, and its answer forgotten.</summary>
public sealed class WorkspaceTrustTests
{
    /// <summary>A folder not trusted runs none of its code until the person trusts it, and trusting starts what was
    /// held back: its targets, Git's programs, the monitor and the language server, its own TypeScript where the
    /// carried one served meanwhile, a project added to its solution without the restore that would run its MSBuild
    /// files, a mobile project kept from before with none of its targets run, and the SDK its global.json, or one above
    /// a trusted folder, names.</summary>
    [AvaloniaFact]
    public void An_untrusted_folder_runs_none_of_its_code_until_the_person_trusts_it()
    {
        Flow.StepApart<WorkspaceTrustTests>("an untrusted folder runs none of its code until trusted then its targets git monitor and language server run", t => t.An_untrusted_folder_runs_none_of_its_code_until_trusted_then_its_targets_git_monitor_and_language_server_run());
        Flow.StepApart<WorkspaceTrustTests>("an untrusted folder is served by the carried TypeScript until trusted then by its own", t => t.An_untrusted_folder_is_served_by_the_carried_TypeScript_until_trusted_then_by_its_own());
        Flow.StepApart<WorkspaceTrustTests>("a project added to a solution not trusted is made without its restore which would run the folder's MSBuild files", t => t.A_project_added_to_a_solution_not_trusted_is_made_without_its_restore_which_would_run_the_folders_MSBuild_files());
        Flow.StepApart<WorkspaceTrustTests>("a mobile project kept from before is shown in a folder not trusted and none of its targets run until trusted", t => t.A_mobile_project_kept_from_before_is_shown_in_a_folder_not_trusted_and_none_of_its_targets_run_until_trusted());
        Flow.StepApart<WorkspaceTrustTests>("a folder not trusted never runs the SDK its global.json names until trusted", t => t.A_folder_not_trusted_never_runs_the_sdk_its_global_json_names_until_trusted());
        Flow.StepApart<WorkspaceTrustTests>("a trusted folder never runs the SDK a global.json above it names until that folder is trusted too", t => t.A_trusted_folder_never_runs_the_sdk_a_global_json_above_it_names_until_that_folder_is_trusted_too());
    }

    /// <summary>Trust is the person's: asked once and kept, a parent covering its folders and a folder in a Git
    /// repository trusted with the repository, a kept trust below the repository's top asked again naming it, a new
    /// solution made in a repository not trusted opening untrusted, and trust taken back in Settings ending the
    /// language server, saying what runs on and asking again at the next open.</summary>
    [AvaloniaFact]
    public void Trust_is_asked_once_kept_covers_what_it_should_and_is_taken_back_only_in_settings()
    {
        Flow.StepApart<WorkspaceTrustTests>("the trust question is asked once and kept and a parent folder trusted covers the folders in it", t => t.The_trust_question_is_asked_once_and_kept_and_a_parent_folder_trusted_covers_the_folders_in_it());
        Flow.StepApart<WorkspaceTrustTests>("a folder in a Git repository is trusted with the repository whose build files its project then evaluates with", t => t.A_folder_in_a_Git_repository_is_trusted_with_the_repository_whose_build_files_its_project_then_evaluates_with());
        Flow.StepApart<WorkspaceTrustTests>("a trust kept of a folder below its repository's top is asked again naming the repository", t => t.A_trust_kept_of_a_folder_below_its_repositorys_top_is_asked_again_naming_the_repository());
        Flow.StepApart<WorkspaceTrustTests>("a new solution made in a Git repository not trusted is not trusted and opens asking of the repository", t => t.A_new_solution_made_in_a_Git_repository_not_trusted_is_not_trusted_and_opens_asking_of_the_repository());
        Flow.StepApart<WorkspaceTrustTests>("trust taken back in Settings ends the language server says what runs on and forgotten asks again at the next open", t => t.Trust_taken_back_in_Settings_ends_the_language_server_says_what_runs_on_and_forgotten_asks_again_at_the_next_open());
    }

    /// <summary>A file Git stores through a filter runs no filter in a folder not trusted: it is offered for commit,
    /// rolled back, stashed and checked out only once the folder is trusted.</summary>
    [AvaloniaFact]
    public void A_file_git_stores_through_a_filter_is_written_or_committed_only_once_the_folder_is_trusted()
    {
        Flow.StepApart<WorkspaceTrustTests>("a file stored through a git filter is offered for commit only once the folder is trusted", t => t.A_file_stored_through_a_git_filter_is_offered_for_commit_only_once_the_folder_is_trusted());
        Flow.StepApart<WorkspaceTrustTests>("rollback stash and checkout writing out a file stored through a git filter are offered only once the folder is trusted", t => t.Rollback_stash_and_checkout_writing_out_a_file_stored_through_a_git_filter_are_offered_only_once_the_folder_is_trusted());
    }

    private void An_untrusted_folder_runs_none_of_its_code_until_trusted_then_its_targets_git_monitor_and_language_server_run()
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

            Assert.Equal(Refusals.UntrustedSaid.Shown, window.TrustSaid);
            Assert.True(window.Vcs.Branch is { Length: > 0 }, "Git's status was not read in the folder not trusted, where it runs without the repository's programs");
            Assert.False(File.Exists(monitored), "Git ran the repository's fsmonitor in a folder not trusted");
            Assert.Null(window.Session.CSharp);
            Assert.Equal(0, window.Session.Models.Evaluations);
            Assert.Equal(0, window.Session.DesignTimeBuilds.Builds);
            Assert.False(File.Exists(built), "A design-time build ran Directory.Build.targets' Exec in a folder not trusted");
            // Choose Build Configuration too: its projects, never evaluated here, offer none until the folder is trusted.
            foreach (string action in (string[])[AppActions.Build, AppActions.RunProgram, AppActions.Debug, AppActions.RunAllTests, AppActions.Profile,
                AppActions.Push, AppActions.Fetch, AppActions.UpdateProject, AppActions.ChooseBuildConfiguration])
            {
                Assert.Equal(Refusals.Untrusted.Shown, window.Actions.WhyNot(action, window.Actions.Context())?.Shown);
            }

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
            Ui.WaitFor(() => File.Exists(monitored), "Git's status to run the repository's fsmonitor once the folder is trusted");
            Ui.WaitFor(() => File.Exists(built), () => $"a design-time build to run Directory.Build.targets' Exec once the folder is trusted (the C# server {window.Session.CSharp?.State ?? "never started"})",
                TimeSpan.FromMinutes(3));
            Assert.Null(window.TrustSaid);
            Assert.True(window.Session.Models.Evaluations > 0, "The projects were not evaluated once the folder was trusted");
            Assert.Null(window.Actions.WhyNot(AppActions.Build, window.Actions.Context())?.Shown);
            Assert.Null(window.Actions.WhyNot(AppActions.Fetch, window.Actions.Context())?.Shown);
            Ui.WaitFor(() => window.Actions.WhyNot(AppActions.ChooseBuildConfiguration, window.Actions.Context())?.Shown is null,
                () => $"the build configurations offered once the folder is trusted, where Choose Build Configuration says {window.Actions.WhyNot(AppActions.ChooseBuildConfiguration, window.Actions.Context())?.Shown}");
        }
        finally
        {
            window.Close();
        }
    }

    private void The_trust_question_is_asked_once_and_kept_and_a_parent_folder_trusted_covers_the_folders_in_it()
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
            Assert.Equal(Refusals.UntrustedSaid.Shown, window.TrustSaid);

            Open(window, elsewhere);
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            Open(window, first);
            Assert.Null(window.AskingTrust);
            Assert.Equal(Refusals.UntrustedSaid.Shown, window.TrustSaid);

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

    /// <summary>A folder opened below the top of a Git repository the person has not trusted asks of the repository, saying
    /// why, and its banner names it; trusted in Settings, the repository is trusted and listed in the folder's place, a
    /// folder beside it in the repository trusted too, and the folder's project evaluates with the repository's
    /// Directory.Build.props; taken back, no folder in it is trusted, and the banner's Trust Folder trusts the repository
    /// again.</summary>
    private void A_folder_in_a_Git_repository_is_trusted_with_the_repository_whose_build_files_its_project_then_evaluates_with()
    {
        string repository = PathNames.Canonical(TestFolders.Create("trusted-repository-"));
        TestGit.Run(repository, "init", "-q");
        TestFolders.Write(repository, "Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n  </PropertyGroup>\n</Project>\n");
        string app = Path.GetDirectoryName(TestFolders.Write(repository, "src/App/App.csproj", TestProjects.Library()))!;
        TestFolders.Write(app, "Program.cs", "System.Console.WriteLine(\"App\");\n");
        string tools = Path.GetDirectoryName(TestFolders.Write(repository, "tools/README.md", "# Tools\n"))!;
        string state = TestFolders.Create("trust-state-");
        using IDisposable asked = WorkspaceTrust.Ask(repository);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = state };
        window.Show();
        window.KeepLayout();
        try
        {
            Open(window, app);
            WorkspaceTrustDialog question = Asked(window);
            DialogButtonsTests.Check(question);
            Assert.Equal((app, repository), (question.Folder, question.Trusting));
            Assert.Equal($"Trust the code in {Path.GetFileName(repository)}?", question.Title);
            Assert.Contains(WorkspaceTrustDialog.InRepository(app, repository), Avalonia.Automation.AutomationProperties.GetHelpText(question), StringComparison.Ordinal);
            Ui.ClickButton(question, WorkspaceTrustDialog.Untrusted);
            Ui.Settle();
            Assert.Equal(Refusals.UntrustedInRepository(repository).Shown, window.TrustSaid);

            SettingsDialog settings = window.ShowSettings();
            Ui.Settle();
            settings.Page = SettingsDialog.TrustPage;
            Ui.Settle();
            Ui.Click(Listed(settings, app).Trusted);
            Ui.ClickButton(settings, "Apply");
            Ui.Settle();
            Assert.True(window.Session.Trusted, "The folder trusted in Settings stayed untrusted");
            Assert.True(WorkspaceTrust.Trusts(tools), "A folder beside the one trusted, in its Git repository, was not trusted with the repository");
            Assert.True(settings.TrustedFolders.Of(app) is null && Listed(settings, repository).Trusted.IsChecked == true,
                $"Trusted Folders listed [{string.Join(", ", settings.TrustedFolders.Rows.Select(r => $"{r.Folder} {r.Trusted.IsChecked}"))}], not the repository trusted in the folder's place");
            Ui.WaitFor(() => window.Projects.Any(p => p.Name == "App" && p.IsEvaluated), () => $"the project to evaluate once trusted: {Ui.Said(window)}", TimeSpan.FromMinutes(2));
            Assert.Equal("Exe", window.Projects.Single(p => p.Name == "App").OutputType);

            Ui.Click(Listed(settings, repository).Trusted);
            Ui.ClickButton(settings, "OK");
            Ui.Settle();
            Assert.False(window.Session.Trusted, "The folder's window stayed trusted once its repository's trust was taken back");
            Assert.False(WorkspaceTrust.Trusts(app) || WorkspaceTrust.Trusts(tools), "A folder in the repository stayed trusted once the repository's trust was taken back");
            Assert.Equal(Refusals.UntrustedInRepository(repository).Shown, window.TrustSaid);

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
            Ui.Settle();
            Assert.True(window.Session.Trusted && WorkspaceTrust.Trusts(tools), "The banner's Trust Folder did not trust the folder's Git repository");
        }
        finally
        {
            window.Close();
        }
        using System.Text.Json.JsonDocument kept = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "trust.json")));
        Assert.Equal([repository], kept.RootElement.GetProperty("trusted").EnumerateArray().Select(f => f.GetString()));
        Assert.Empty(kept.RootElement.GetProperty("untrusted").EnumerateArray());
    }

    /// <summary>A trust kept from before of a folder below the top of its Git repository, given before trusting it named the
    /// repository, is forgotten as the window takes in what it keeps: the folder opens untrusted and asks again, naming the
    /// repository, whose Trust trusts the repository, kept in the folder's place.</summary>
    private void A_trust_kept_of_a_folder_below_its_repositorys_top_is_asked_again_naming_the_repository()
    {
        string repository = PathNames.Canonical(TestFolders.Create("kept-repository-"));
        TestGit.Run(repository, "init", "-q");
        string app = Path.GetDirectoryName(TestFolders.Write(repository, "src/App/README.md", "# App\n"))!;
        string state = TestFolders.Create("trust-state-");
        TestFolders.Write(state, "trust.json", $$"""{ "version": 1, "trusted": [ {{System.Text.Json.JsonSerializer.Serialize(app)}} ], "untrusted": [] }""" + "\n");
        using IDisposable asked = WorkspaceTrust.Ask(repository);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false, StateFolder = state };
        window.Show();
        window.KeepLayout();
        try
        {
            Open(window, app);
            WorkspaceTrustDialog question = Asked(window);
            Assert.False(window.Session.Trusted, "The folder whose trust was kept from before trusting it named its repository opened trusted");
            Assert.Equal(repository, question.Trusting);
            Ui.ClickButton(question, WorkspaceTrustDialog.Trust);
            Ui.Settle();
            Assert.True(window.Session.Trusted && WorkspaceTrust.Trusts(repository), "The question's Trust did not trust the folder's Git repository");
        }
        finally
        {
            window.Close();
        }
        using System.Text.Json.JsonDocument kept = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(state, "trust.json")));
        Assert.Equal([repository], kept.RootElement.GetProperty("trusted").EnumerateArray().Select(f => f.GetString()));
    }

    /// <summary>A new solution made in a Git repository the person has not trusted is not trusted as it is made, as its
    /// projects would take in the repository's build files: it is made without its restore, and opens asking of the
    /// repository; New Solution makes no second repository in it, saying so.</summary>
    private void A_new_solution_made_in_a_Git_repository_not_trusted_is_not_trusted_and_opens_asking_of_the_repository()
    {
        string repository = PathNames.Canonical(TestFolders.Create("new-in-repository-"));
        TestGit.Run(repository, "init", "-q");
        using IDisposable asked = WorkspaceTrust.Ask(repository);
        MainWindow window = new() { Width = 1200, Height = 800, StartsLanguageServer = false };
        window.Show();
        Ui.Settle();
        try
        {
            Assert.True(window.Perform(AppActions.NewSolution), $"New Solution did not open: {Ui.Said(window)}");
            Passenger.App.Templates.NewProjectDialog dialog = NewServiceSetupTests.Opened(window);
            NewServiceSetupTests.Choose(dialog, "classlib");
            dialog.Folder = Path.Combine(repository, "samples");
            dialog.SolutionName = "Made";
            dialog.ProjectName = "Made";
            dialog.Framework = TestProjects.Framework;
            Ui.WaitFor(() => dialog.RepositoryNote is not null, () => $"New Solution to say it makes no repository in {repository}, which is one");
            Assert.Equal($"The solution goes into the Git repository at {PathNames.Shown(repository, null)}: no second one is made in it", dialog.RepositoryNote);
            Assert.False(dialog.MakesRepository, "New Solution offered a second Git repository inside one");
            NewServiceSetupTests.Created(dialog, "the solution");

            string made = Path.Combine(repository, "samples", "Made");
            Assert.False(Path.Exists(Path.Combine(made, ".git")), "New Solution made a second Git repository inside one");
            Assert.True(File.Exists(Path.Combine(made, "Made", "Made.csproj")), $"No Made.csproj was made: {Ui.Said(window)}");
            Assert.False(WorkspaceTrust.Trusts(made), "The new solution made in a Git repository not trusted was trusted as it was made");
            Assert.False(File.Exists(Path.Combine(made, "Made", "obj", "project.assets.json")), "The new solution made in a Git repository not trusted was restored");
            Assert.Equal(repository, Asked(window).Trusting);
        }
        finally
        {
            window.Close();
        }
    }

    private void An_untrusted_folder_is_served_by_the_carried_TypeScript_until_trusted_then_by_its_own()
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

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
            Ui.WaitFor(() => File.Exists(ran), () => $"the folder's own tsserver to run once it is trusted ({window.LanguageShown})");
        }
        finally
        {
            window.Close();
        }
    }

    private void Trust_taken_back_in_Settings_ends_the_language_server_says_what_runs_on_and_forgotten_asks_again_at_the_next_open()
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
            Assert.Equal(Refusals.Untrusted.Shown, window.Actions.WhyNot(AppActions.Build, window.Actions.Context())?.Shown);
            Assert.True(window.Runner.IsRunning, "Taking the trust back ended the program the person started");
            Assert.Equal(Refusals.UntrustedRunsOn(["the program"]).Shown, window.TrustSaid);
            window.Perform(AppActions.Stop);
            Ui.WaitFor(() => !window.Runner.IsRunning, "the program to stop");
            Ui.WaitFor(() => window.TrustSaid == Refusals.UntrustedSaid.Shown, () => $"the banner to say only that the folder is not trusted once the program ended ({window.TrustSaid})");

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

    private void A_project_added_to_a_solution_not_trusted_is_made_without_its_restore_which_would_run_the_folders_MSBuild_files()
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

            Assert.Equal($"Lib did not restore: {Refusals.Untrusted.Shown}", NewServiceSetupTests.SetUp(window));
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
    private void A_mobile_project_kept_from_before_is_shown_in_a_folder_not_trusted_and_none_of_its_targets_run_until_trusted()
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
            Assert.Equal(Refusals.Untrusted.Shown, window.Actions.WhyNot(AppActions.ChooseRunTarget, window.Actions.Context())?.Shown);

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
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
    private void A_folder_not_trusted_never_runs_the_sdk_its_global_json_names_until_trusted()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-sdk-"));
        string marks = PathNames.Canonical(TestFolders.Create("untrusted-sdk-marks-"));
        string ran = Path.Combine(marks, "ran.marker");
        Carry(folder, marks, ran);
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

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
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

    /// <summary>A trusted folder in one not trusted, whose global.json names an SDK it carries (its sdk.paths, a stand-in
    /// marking that it ran), never has it run: New Project's read of the templates takes the SDK Passenger takes everywhere
    /// else, the folder's dotnet run outside it; once the folder holding the global.json is trusted too, the read takes the
    /// SDK it names, which runs.</summary>
    private void A_trusted_folder_never_runs_the_sdk_a_global_json_above_it_names_until_that_folder_is_trusted_too()
    {
        string repository = PathNames.Canonical(TestFolders.Create("untrusted-above-"));
        string app = PathNames.Canonical(Directory.CreateDirectory(Path.Combine(repository, "src", "App")).FullName);
        string marks = PathNames.Canonical(TestFolders.Create("untrusted-above-marks-"));
        string ran = Path.Combine(marks, "ran.marker");
        Carry(repository, marks, ran);
        TestFolders.Write(app, "App.slnx", "<Solution />\n");
        using IDisposable asked = WorkspaceTrust.Ask(repository);
        WorkspaceTrust.Trust(app);
        MainWindow window = Ui.OpenFolder(app, languageServer: false);
        try
        {
            Assert.True(window.Session.Trusted && window.AskingTrust is null, "The folder trusted was opened as not trusted");
            Ui.Run(window, AppActions.NewProject);
            NewServiceSetupTests.Opened(window).Close();
            Ui.Settle();
            Assert.False(File.Exists(ran), $"The SDK the global.json in the folder above, not trusted, names ran for the folder trusted, asked {(File.Exists(ran) ? File.ReadAllText(ran) : null)}");

            WorkspaceTrust.Trust(repository);
            Ui.Settle();
            Ui.Run(window, AppActions.NewProject);
            Ui.WaitFor(() => File.Exists(ran), () => $"New Project's read of the templates to run the SDK the global.json above names once its folder is trusted: {Ui.Said(window)}",
                TimeSpan.FromMinutes(2));
            Assert.Single(window.OwnedWindows.OfType<Passenger.App.Templates.NewProjectDialog>(), d => d.IsVisible).Close();
        }
        finally
        {
            window.Close();
            WorkspaceTrust.TakeBack(repository);
        }
    }

    /// <summary>A stand-in SDK carried in <paramref name="folder"/>'s .sdk, at the version the run takes, which marks
    /// <paramref name="ran"/> with its arguments as it runs (on the runtime of the .NET the run takes), and the global.json
    /// there naming it by its sdk.paths; skips where this account may make no link to that runtime.</summary>
    private static void Carry(string folder, string marks, string ran)
    {
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
    }

    /// <summary>A folder opened in the window, once its opening has landed.</summary>
    private static void Open(MainWindow window, string folder)
    {
        window.OpenFolder(folder);
        Ui.Settle();
    }

    /// <summary>In a folder not trusted, a file Git stores through a filter (git-lfs's, here the repository's own) would go
    /// into a commit as the disk has it, its filter not running: the Commit window lists it with no box, saying why, and a
    /// commit of it is refused, the file beside it offered as ever; once the folder is trusted it is offered too.</summary>
    private void A_file_stored_through_a_git_filter_is_offered_for_commit_only_once_the_folder_is_trusted()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-filter-"));
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\n");
        File.WriteAllText(Path.Combine(folder, "Picture.bin"), "one\n");
        File.WriteAllText(Path.Combine(folder, ".gitattributes"), "*.bin filter=keep\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "config", "filter.keep.clean", "cat");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "two\n");
        File.WriteAllText(Path.Combine(folder, "Picture.bin"), "two\n");
        const string Why = "The folder is not trusted: Picture.bin is stored through Git's keep filter, which runs only once the folder is trusted";
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = Ui.OpenFolder(folder);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            window.Perform(AppActions.ShowCommit);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.Rows.OfType<CommitRow>().Count() == 2, "Notes.txt and Picture.bin listed as changed");
            CommitRow picture = commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Picture.bin");
            Assert.False(picture.CanChoose, "Picture.bin, which its filter would store, is offered for commit in a folder not trusted, where the filter does not run");
            Assert.Equal(Why, picture.Change.Uncommittable);
            Assert.Equal(["Notes.txt"], commit.Chosen.Select(c => c.Name));
            Assert.Contains(commit.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Why && t.IsEffectivelyVisible);
            CommitResult refused = Ui.Landed(window.Vcs.CommitAsync([picture.Change], "The picture", amend: false, push: false), "the commit's answer");
            Assert.Equal(Why, refused.Commit.Message);

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
            Ui.Landed(() => commit.Rows.OfType<CommitRow>().Any(r => r.Change.Name == "Picture.bin" && r.CanChoose), "Picture.bin offered for commit once the folder is trusted");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>In a folder not trusted, git would write out a file it stores through a filter (git-lfs's, here the
    /// repository's own) as it keeps it, and a stash would keep the file whole: Rollback of it, Stash Changes and Checkout
    /// of a branch changing it are greyed, saying why, and refused, the file's bytes as they were, the file beside it
    /// rolled back as ever; once the folder is trusted each is offered.</summary>
    private void Rollback_stash_and_checkout_writing_out_a_file_stored_through_a_git_filter_are_offered_only_once_the_folder_is_trusted()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-filter-writes-"));
        string picture = Path.Combine(folder, "Picture.bin");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "one\n");
        File.WriteAllText(picture, "one\n");
        File.WriteAllText(Path.Combine(folder, ".gitattributes"), "*.bin filter=keep\n");
        TestGit.Init(folder);
        TestGit.Run(folder, "config", "filter.keep.clean", "cat");
        TestGit.Run(folder, "config", "filter.keep.smudge", "cat");
        TestGit.Run(folder, "switch", "-q", "-c", "pictured");
        File.WriteAllText(picture, "pictured\n");
        TestGit.Run(folder, "commit", "-q", "-a", "-m", "The picture changed");
        TestGit.Run(folder, "switch", "-q", "main");
        File.WriteAllText(Path.Combine(folder, "Notes.txt"), "two\n");
        File.WriteAllText(picture, "two\n");
        const string Why = "The folder is not trusted: Picture.bin is stored through Git's keep filter, which runs only once the folder is trusted";
        using IDisposable asked = WorkspaceTrust.Ask(folder);
        MainWindow window = Ui.OpenFolder(folder, width: 1300, height: 900);
        try
        {
            Ui.ClickButton(Asked(window), WorkspaceTrustDialog.Untrusted);
            window.Perform(AppActions.ShowCommit);
            CommitView commit = window.CommitView;
            Ui.Landed(() => commit.Rows.OfType<CommitRow>().Count() == 2, "Notes.txt and Picture.bin listed as changed");
            Button rollback = commit.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Rollback");
            ListBox rows = commit.GetVisualDescendants().OfType<ListBox>().First();
            rows.SelectedItem = commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Picture.bin");
            Ui.Settle();
            Assert.False(rollback.IsEnabled, "Rollback of Picture.bin is offered in a folder not trusted, where git would write it out as it keeps it");
            Assert.Equal($"Rollback: {Why}", ToolTip.GetTip(rollback));
            Assert.Equal(Why, window.Actions.WhyNot(AppActions.Stash, window.Actions.Context())?.Shown);
            VcsOutcome refused = Ui.Landed(window.Vcs.RollbackAsync([.. commit.Rows.OfType<CommitRow>().Select(r => r.Change)]), "the rollback's answer");
            Assert.Equal((false, Why), (refused.Succeeded, refused.Message));
            Assert.Equal("two\n", File.ReadAllText(picture));
            rows.SelectedItem = commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt");
            Ui.Settle();
            Assert.True(rollback.IsEnabled, "Rollback of Notes.txt, stored through no filter, is greyed in a folder not trusted");
            Assert.True(Ui.Landed(window.Vcs.RollbackAsync([commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Notes.txt").Change]), "Notes.txt rolled back").Succeeded);

            window.Perform(AppActions.ShowGit);
            Ui.Landed(() => window.GitBranches.Rows.Count > 0, "the branches panel");
            BranchInfo pictured = window.GitBranches.Branches.Single(b => b.Name == "pictured");
            MenuItem checkout = window.GitBranches.Menu(pictured).OfType<MenuItem>().Single(i => (string?)i.Header == "Checkout");
            Ui.Landed(() => !checkout.IsEnabled, "Checkout of pictured greyed");
            Assert.Equal(Why, ToolTip.GetTip(checkout));
            VcsOutcome switched = Ui.Landed(window.Vcs.CheckoutRefAsync(pictured, window.GitBranches.Branches), "the checkout's answer");
            Assert.Equal((false, Why), (switched.Succeeded, switched.Message));
            Assert.Equal(("main", "two\n"), (window.Vcs.Branch, File.ReadAllText(picture)));

            Ui.ClickButton(window, window.Actions[AppActions.TrustFolder]!.Title.English);
            Ui.Landed(() => window.Actions.WhyNot(AppActions.Stash, window.Actions.Context())?.Shown is null, "Stash Changes offered once the folder is trusted");
            MenuItem trusted = window.GitBranches.Menu(pictured).OfType<MenuItem>().Single(i => (string?)i.Header == "Checkout");
            Ui.Settle();
            Assert.True(trusted.IsEnabled, "Checkout of pictured is still greyed once the folder is trusted");
            window.Perform(AppActions.ShowCommit);
            rows.SelectedItem = commit.Rows.OfType<CommitRow>().Single(r => r.Change.Name == "Picture.bin");
            Ui.Settle();
            Assert.True(rollback.IsEnabled, "Rollback of Picture.bin is still greyed once the folder is trusted");
        }
        finally
        {
            window.Close();
            TestFolders.Delete(folder);
        }
    }

    /// <summary>A folder's row in Settings' Trusted Folders, failing where it lists none.</summary>
    private static TrustedFoldersTable.Row Listed(SettingsDialog settings, string folder) => settings.TrustedFolders.Of(folder)
        ?? throw new InvalidOperationException($"Trusted Folders lists no {folder}: [{string.Join(", ", settings.TrustedFolders.Rows.Select(r => r.Folder))}]");

    /// <summary>The trust question the window asks now, failing where it asks none.</summary>
    private static WorkspaceTrustDialog Asked(MainWindow window)
    {
        Ui.WaitFor(() => window.AskingTrust is { IsVisible: true }, "the trust question");
        return window.AskingTrust!;
    }
}
