using Avalonia.Headless.XUnit;
using Passenger.App;
using Passenger.Core;
using Passenger.Vcs;

namespace Passenger.Tests;

/// <summary>A folder the person has not trusted, as their own window opens it: the question asked once and kept, a parent
/// trusted covering the folders in it, none of the folder's code run (its targets, its Git configuration's programs, the C#
/// server, its own TypeScript) until Trust Folder, and then each.</summary>
public sealed class WorkspaceTrustTests
{
    [AvaloniaFact]
    public void An_untrusted_folder_runs_none_of_its_code_until_trusted_then_its_targets_git_monitor_and_language_server_run()
    {
        string folder = PathNames.Canonical(TestFolders.Create("untrusted-"));
        string built = Path.Combine(folder, "built.marker");
        string monitored = Path.Combine(folder, "monitored.marker");
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
            foreach (string action in (string[])[AppActions.Build, AppActions.RunProgram, AppActions.Debug, AppActions.RunAllTests, AppActions.Profile])
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
