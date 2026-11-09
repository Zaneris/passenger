using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Passenger.App;
using Passenger.App.Packages;
using Passenger.App.ToolWindows;
using Passenger.Workspace;

namespace Passenger.Tests;

/// <summary>The NuGet window over two projects and a stand-in feed whose VulnerabilityInfo the audit reads, so nothing
/// reaches nuget.org: a package is shown, compared and audited at each of a project's frameworks' versions, a range at the
/// version its restore resolved, else its least, and a change of a pinned version keeps it pinned; a change runs one at a
/// time, the projects' actions disabled meanwhile, one refused says why, and one that failed says so until the person
/// next asks for a list.</summary>
public sealed class NuGetTests : IDisposable
{
    private const string Id = "Passenger.Vulnerable";

    private const string Advisory = "https://github.com/advisories/GHSA-pass-nuget-test";

    private readonly string folder = TestFolders.Create("nuget-");
    private readonly StandInFeed feed = new();
    private readonly string lib;

    public NuGetTests()
    {
        feed.Add(Id, "1.0.0");
        feed.Add(Id, "1.0.1");
        feed.Add(Id, "1.1.0");
        feed.Advise(Id, 1, Advisory, "[1.0.0, 1.0.1)");
        // Only the stand-in, for packages and for the audit, and restores kept out of the person's package cache.
        File.WriteAllText(Path.Combine(folder, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" protocolVersion="3" allowInsecureConnections="true" />
              </packageSources>
              <auditSources>
                <clear />
                <add key="stand-in" value="{feed.Index}" allowInsecureConnections="true" />
              </auditSources>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
            </configuration>
            """);
        lib = Project("Lib", TestProjects.Framework, string.Empty);
        Project("App", TestProjects.Framework, string.Empty);
    }

    public void Dispose()
    {
        feed.Dispose();
        TestFolders.Delete(folder);
    }

    [AvaloniaFact]
    public void Updates_checks_on_entry_filters_locally_and_selects_the_latest_version()
    {
        const string Other = "Passenger.Current";
        feed.Add(Other, "2.0.0");
        feed.Add(Id, "2.0.0-preview.1");
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /><PackageReference Include=\"{Other}\" Version=\"2.0.0\" /></ItemGroup>");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Assert.DoesNotContain(feed.Requests, r => r.Path == "/flat/passenger.vulnerable/index.json");
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().SingleOrDefault(b => Equals(b.Content, "Updates"))
            ?? throw new Xunit.Sdk.XunitException("NuGet has no Updates tab"));
        Assert.True(nuget.Rows is [{ Id: Id }] && nuget.ChosenVersion == "1.1.0",
            $"Updates lists [{string.Join(", ", nuget.Rows.Select(r => r.Id))}] and selects {nuget.ChosenVersion}, rather than the latest stable update");
        int asked = feed.Requests.Count;
        nuget.Query = "no-match";
        Ui.Settle();
        Assert.Empty(nuget.Rows);
        Assert.Equal(asked, feed.Requests.Count);
        nuget.Query = string.Empty;
        Ui.Settle();
        Assert.Single(nuget.Rows);
        CheckBox preview = nuget.GetLogicalDescendants().OfType<CheckBox>().Single(b => Equals(b.Content, "Prerelease"));
        Ui.Click(preview);
        Assert.Equal("2.0.0-preview.1", nuget.ChosenVersion);
        window.Close();
    }

    [AvaloniaFact]
    public void Update_all_updates_every_project_even_when_search_hides_results_and_one_undo_restores_them()
    {
        const string Other = "Passenger.Other";
        feed.Add(Other, "1.0.0");
        feed.Add(Other, "1.2.0");
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"[1.0.0]\" /></ItemGroup>");
        string app = Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Other}\" Version=\"1.0.0\" /></ItemGroup>");
        string beforeLib = File.ReadAllText(lib);
        string beforeApp = File.ReadAllText(app);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().SingleOrDefault(b => Equals(b.Content, "Updates"))
            ?? throw new Xunit.Sdk.XunitException("NuGet has no Updates tab"));
        Assert.Equal(2, nuget.Rows.Count);
        nuget.Query = "vulnerable";
        Ui.Settle();
        Assert.Single(nuget.Rows);
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().SingleOrDefault(b => Equals(b.Content, "Update All"))
            ?? throw new Xunit.Sdk.XunitException("Updates has no Update All button"));
        Ui.Landed(() => !nuget.IsBusy && nuget.Rows.Count == 0, $"all packages updated: {nuget.Status}\n{nuget.Log}");
        Assert.True(File.ReadAllText(lib).Contains("[1.1.0]", StringComparison.Ordinal), $"Lib was not updated: {nuget.Status}\n{nuget.Log}");
        Assert.Contains("1.2.0", File.ReadAllText(app), StringComparison.Ordinal);
        MigrationsTests.UndoFromExplorer(window, lib);
        Assert.Equal(beforeLib, File.ReadAllText(lib));
        Assert.Equal(beforeApp, File.ReadAllText(app));
        window.Close();
    }

    [AvaloniaFact]
    public void Updates_reports_partial_feed_failure_instead_of_claiming_every_package_is_current()
    {
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.1.0\" /></ItemGroup>");
        string config = Path.Combine(folder, "nuget.config");
        File.WriteAllText(config, File.ReadAllText(config).Replace("</packageSources>",
            "<add key=\"unavailable\" value=\"http://127.0.0.1:1/v3/index.json\" allowInsecureConnections=\"true\" /></packageSources>", StringComparison.Ordinal));
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().SingleOrDefault(b => Equals(b.Content, "Updates"))
            ?? throw new Xunit.Sdk.XunitException("NuGet has no Updates tab"));
        Assert.Empty(nuget.Rows);
        Assert.Contains("unavailable", nuget.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No updates found", nuget.Status, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public void Update_all_rechecks_shared_central_versions_and_preserves_one_undo()
    {
        File.WriteAllText(Path.Combine(folder, "Directory.Packages.props"), $"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"{Id}\" Version=\"[1.0.0]\" /></ItemGroup></Project>");
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" /></ItemGroup>");
        Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" /></ItemGroup>");
        string props = Path.Combine(folder, "Directory.Packages.props");
        string original = File.ReadAllText(props);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All")));
        Assert.True(File.ReadAllText(props).Contains("[1.1.0]", StringComparison.Ordinal), $"Central version was not updated: {nuget.Status}\n{nuget.Log}\n{File.ReadAllText(props)}");
        Assert.Empty(nuget.Rows);
        MigrationsTests.UndoFromExplorer(window, lib);
        Assert.Equal(original, File.ReadAllText(props));
        window.Close();
    }

    [AvaloniaFact]
    public void Update_all_stops_on_a_refused_project_and_undo_restores_the_earlier_success()
    {
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        string app = Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        string originalLib = File.ReadAllText(lib);
        string originalApp = File.ReadAllText(app);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        int saves = 0;
        nuget.Packages.SaveFirst = (_, _) => Task.FromResult<string?>(++saves == 3 ? "Lib could not be saved" : null);
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All")));
        Assert.True(File.ReadAllText(app).Contains("1.1.0", StringComparison.Ordinal), $"App was not updated: {nuget.Status}\n{nuget.Log}");
        Assert.Equal(originalLib, File.ReadAllText(lib));
        Assert.Contains("Lib could not be saved", nuget.Status, StringComparison.Ordinal);
        MigrationsTests.UndoFromExplorer(window, app);
        Assert.Equal(originalApp, File.ReadAllText(app));
        window.Close();
    }

    [AvaloniaFact]
    public void Stopping_update_all_prevents_the_held_package_commands_from_starting()
    {
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        string original = File.ReadAllText(lib);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reached = nuget.Packages.HoldNextChange(release.Task);
        Button all = nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All"));
        Ui.Click(all, settle: false);
        try
        {
            Ui.PumpUntil(() => reached.IsCompleted, "Update All waiting before its first command");
            Assert.False(all.IsEnabled);
            window.Perform(AppActions.Stop);
        }
        finally
        {
            release.TrySetResult();
        }
        Ui.Settle();
        Assert.Equal(original, File.ReadAllText(lib));
        Assert.Contains("Stopped", nuget.Status, StringComparison.OrdinalIgnoreCase);
        window.Close();
    }

    [AvaloniaFact]
    public void Update_all_stops_when_a_provider_check_handles_an_update_that_fails()
    {
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        string original = File.ReadAllText(lib);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        nuget.BeforeInstall = async (project, id, version, recording, _) =>
        {
            await nuget.Packages.InstallSetAsync(project, [(id, version), ("Passenger.Missing", "1.0.0")], recording);
            return false;
        };
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All")));
        Assert.Equal(original, File.ReadAllText(lib));
        Assert.Contains("failed", nuget.Status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lib…", nuget.Log, StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("9.0.0")]
    [InlineData("[9.0.0,10.0.0)")]
    public void Update_all_does_not_downgrade_a_reference_changed_by_the_final_save(string changedVersion)
    {
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        Restore.Run(lib);
        Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup>");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        int saves = 0;
        nuget.Packages.SaveFirst = (_, _) =>
        {
            if (++saves == 3)
            {
                File.WriteAllText(lib, File.ReadAllText(lib).Replace("1.0.0", changedVersion, StringComparison.Ordinal));
            }
            return Task.FromResult<string?>(null);
        };
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All")));
        Assert.True(File.ReadAllText(lib).Contains(changedVersion, StringComparison.Ordinal), $"The final save's newer version was overwritten: {nuget.Status}\n{nuget.Log}");
        window.Close();
    }

    [AvaloniaFact]
    public void Update_all_stops_before_absorbing_a_user_edit_to_an_open_central_file()
    {
        const string Other = "Passenger.Other";
        feed.Add(Other, "1.0.0");
        feed.Add(Other, "1.2.0");
        string props = Path.Combine(folder, "Directory.Packages.props");
        File.WriteAllText(props, $"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><Description>Before</Description></PropertyGroup><ItemGroup><PackageVersion Include=\"{Id}\" Version=\"1.0.0\" /><PackageVersion Include=\"{Other}\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" /></ItemGroup>");
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Other}\" /></ItemGroup>");
        MainWindow window = Open();
        Assert.Null(window.OpenFile(props));
        Ui.Settle();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        int saves = 0;
        nuget.Packages.SaveFirst = async (working, cancel) =>
        {
            if (++saves == 3)
            {
                Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
                editor.Focus();
                editor.Select(new Passenger.Text.TextRange(editor.Document.Snapshot.ToString().IndexOf("Before", StringComparison.Ordinal), "Before".Length));
                Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, "Kept");
            }
            return await window.EditorArea.SaveAllLandedAsync(working, cancel);
        };
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All")));
        Assert.Contains("<Description>Kept</Description>", File.ReadAllText(props), StringComparison.Ordinal);
        Assert.Contains("changed during Update All", nuget.Status, StringComparison.Ordinal);
        Assert.Contains($"Include=\"{Other}\" Version=\"1.0.0\"", File.ReadAllText(props), StringComparison.Ordinal);
        window.Close();
    }

    [AvaloniaFact]
    public void Update_all_undo_does_not_absorb_a_later_user_save_to_a_shared_central_file()
    {
        string props = Path.Combine(folder, "Directory.Packages.props");
        File.WriteAllText(props, $"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"{Id}\" Version=\"1.0.0\" /></ItemGroup></Project>");
        Project("Lib", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" /></ItemGroup>");
        Project("App", TestProjects.Framework, $"<ItemGroup><PackageReference Include=\"{Id}\" /></ItemGroup>");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Click(nuget.Tabs.GetLogicalDescendants().OfType<ToggleButton>().Single(b => Equals(b.Content, "Updates")));
        int saves = 0;
        nuget.Packages.SaveFirst = (_, _) =>
        {
            if (++saves == 3)
            {
                File.WriteAllText(props, File.ReadAllText(props).Replace("1.1.0", "9.0.0", StringComparison.Ordinal));
            }
            return Task.FromResult<string?>(null);
        };
        Ui.Click(nuget.GetLogicalDescendants().OfType<Button>().Single(b => Equals(b.Content, "Update All")));
        Assert.Contains("9.0.0", File.ReadAllText(props), StringComparison.Ordinal);
        Passenger.App.Editors.CommandHistory.Command command = window.EditorArea.Commands.ToUndo(null)
            ?? throw new Xunit.Sdk.XunitException("Update All recorded no Undo command");
        string? blocked = Ui.Landed(window.EditorArea.Commands.BlockedAsync(command, redo: false), "whether Undo preserves the later user save");
        Assert.NotNull(blocked);
        window.Close();
    }

    /// <summary>A project pinning the vulnerable 1.0.0 ("[1.0.0]") has it shown as 1.0.0 and marked, is offered the fix as
    /// an Update and nothing for 1.0.0 itself, and the Update pins 1.0.1, never turning the pin into a minimum.</summary>
    [AvaloniaFact]
    public void A_pinned_version_is_shown_audited_and_compared_as_that_version_and_an_update_keeps_it_pinned()
    {
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"[1.0.0]\" />\n  </ItemGroup>\n");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} listed as installed, and its versions: {nuget.Status}");
        Assert.True(nuget.Selected!.IsVulnerable && ShownVersion(nuget, "Lib") == "1.0.0",
            $"Lib's pinned [1.0.0] is shown as {ShownVersion(nuget, "Lib")}, {(nuget.Selected.IsVulnerable ? "marked" : "unmarked")}, not as the vulnerable 1.0.0");
        Assert.True(nuget.ChosenVersion == "1.0.1" && Labels(nuget, "Lib").SequenceEqual(["Update", "Remove"]),
            $"The window offers {nuget.ChosenVersion} as [{string.Join(", ", Labels(nuget, "Lib"))}], not 1.0.1, which fixes 1.0.0, as an Update");

        Choose(nuget, "1.0.0");
        Assert.True(Labels(nuget, "Lib").SequenceEqual(["Remove"]), $"The version Lib pins is offered as [{string.Join(", ", Labels(nuget, "Lib"))}]");
        Choose(nuget, "1.0.1");
        Ui.Click(Action(nuget, "Lib", "Update")!, settle: false);
        Ui.Landed(() => !nuget.IsBusy && ShownVersion(nuget, "Lib") == "1.0.1", $"Lib updated to 1.0.1:\n{nuget.Log}");
        Assert.True(File.ReadAllText(lib).Contains($"Include=\"{Id}\" Version=\"[1.0.1]\"", StringComparison.Ordinal),
            $"The update of a pinned version did not pin the new one:\n{File.ReadAllText(lib)}");
        Assert.False(nuget.Selected?.IsVulnerable, "The package pinned to 1.0.1, which fixes 1.0.0, is still marked vulnerable");
        window.Close();
    }

    /// <summary>A range whose least no feed holds ("[0.9.0, 2.0.0)") is shown and audited as the 1.0.0 its restore
    /// resolved, read from the project's project.assets.json, and offered the fix as an Update.</summary>
    [AvaloniaFact]
    public void A_range_is_shown_and_audited_as_the_version_its_restore_resolved()
    {
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"[0.9.0, 2.0.0)\" />\n  </ItemGroup>\n");
        Restore.Run(lib);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} listed as installed, and its versions: {nuget.Status}");
        Assert.True(ShownVersion(nuget, "Lib") == "1.0.0" && nuget.Selected!.IsVulnerable,
            $"Lib's [0.9.0, 2.0.0), restored as 1.0.0, is shown as {ShownVersion(nuget, "Lib")}, {(nuget.Selected!.IsVulnerable ? "marked" : "unmarked")}");
        Assert.True(nuget.ChosenVersion == "1.0.1" && Action(nuget, "Lib", "Update") is not null,
            $"The window offers {nuget.ChosenVersion} as [{string.Join(", ", Labels(nuget, "Lib"))}], not 1.0.1 as an Update");
        window.Close();
    }

    /// <summary>A project built for two frameworks, the package at 1.1.0 for its first and at a vulnerable 1.0.0 for its
    /// second, shows both versions on its line, marked, the package's row and the status counting it vulnerable, and
    /// offers the version that fixes it as what it is for every framework, whose references move together: a downgrade
    /// naming net11.0, which it takes down from 1.1.0, never a plain Update.</summary>
    [AvaloniaFact]
    public void A_package_whose_version_differs_by_framework_shows_each_and_is_marked_vulnerable_when_any_is()
    {
        Project("Lib", $"net11.0;{TestProjects.Framework}",
            $"  <ItemGroup Condition=\"'$(TargetFramework)' == 'net11.0'\">\n    <PackageReference Include=\"{Id}\" Version=\"1.1.0\" />\n  </ItemGroup>\n"
            + $"  <ItemGroup Condition=\"'$(TargetFramework)' == '{TestProjects.Framework}'\">\n    <PackageReference Include=\"{Id}\" Version=\"1.0.0\" />\n  </ItemGroup>\n");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} listed as installed, and its versions: {nuget.Status}");
        Assert.True(nuget.Selected!.IsVulnerable && nuget.Status == "1 package installed, 1 vulnerable",
            $"With {TestProjects.Framework}'s 1.0.0 vulnerable, the package's row is {(nuget.Selected.IsVulnerable ? "marked" : "unmarked")} and the status says \"{nuget.Status}\"");
        Assert.True(ShownVersion(nuget, "Lib") == "1.1.0, 1.0.0", $"Lib's line shows {ShownVersion(nuget, "Lib")}, not each framework's version");
        Assert.True(nuget.ChosenVersion == "1.0.1" && Labels(nuget, "Lib").SequenceEqual(["Downgrade net11.0", "Remove"]),
            $"The window offers {nuget.ChosenVersion} as [{string.Join(", ", Labels(nuget, "Lib"))}], not 1.0.1, which fixes {TestProjects.Framework}'s 1.0.0 "
            + "and takes net11.0's 1.1.0 down, as a downgrade naming net11.0");

        // The second framework's version alone changed on disk, as a person edits the project.
        File.WriteAllText(lib, File.ReadAllText(lib).Replace("Version=\"1.0.0\"", "Version=\"1.0.1\"", StringComparison.Ordinal));
        Ui.WaitFor(() => ShownVersion(nuget, "Lib") == "1.1.0, 1.0.1",
            () => $"Lib's line to show {TestProjects.Framework}'s 1.0.1 written on disk once the project is evaluated again (it shows {ShownVersion(nuget, "Lib")})");
        Assert.False(nuget.Selected?.IsVulnerable, $"With {TestProjects.Framework} at 1.0.1, which fixes 1.0.0, the package is still marked vulnerable");
        window.Close();
    }

    /// <summary>While another asker's change of Lib (an agent's, Migrations') runs, the NuGet window's Install on App is
    /// disabled, and a change the window asks for meanwhile is refused saying what is under way, not dropped unsaid; once
    /// the first change ends, App's Install is enabled again.</summary>
    [AvaloniaFact]
    public void While_a_package_change_runs_the_projects_actions_are_disabled_and_one_asked_meanwhile_is_refused_saying_why()
    {
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Search(nuget);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reached = nuget.Packages.HoldNextChange(release.Task);
        Task<PackageOutcome> first = nuget.Packages.InstallAsync(Named(window, "Lib"), Id, "1.1.0");
        try
        {
            Ui.PumpUntil(() => reached.IsCompleted, "Lib's install to be under way");
            Button install = Action(nuget, "App", "Install") ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Install on App's line");
            Assert.True(!install.IsEnabled && nuget.Status == "Working…",
                $"While Lib's install runs, App's Install is {(install.IsEnabled ? "enabled" : "disabled")} and the status says \"{nuget.Status}\"");
            Task<bool> second = nuget.InstallAsync(Named(window, "App"));
            Assert.False(Ui.AwaitPumped(second, "the window's install asked meanwhile"), "The window's install of App ran while Lib's was under way");
            Assert.True(nuget.Status == $"Already under way: Installing {Id} 1.1.0 in Lib…",
                $"The window's install refused while Lib's ran says \"{nuget.Status}\", not what is under way");
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True(Ui.Landed(first, "Lib's install").Done, nuget.Log);
        Ui.Landed(() => Action(nuget, "App", "Install") is { IsEnabled: true }, "App's Install enabled again once Lib's install ended");
        Assert.Contains(Id, File.ReadAllText(lib), StringComparison.Ordinal);
        window.Close();
    }

    /// <summary>A change that fails puts back only what it wrote: an edit to Lib.csproj typed and saved while the change
    /// waited is kept, never written over by the bytes read before the change, and the change's words say it was left as
    /// saved.</summary>
    [AvaloniaFact]
    public void A_failed_change_keeps_an_edit_saved_to_the_project_while_it_ran()
    {
        Project("Lib", TestProjects.Framework, "  <PropertyGroup>\n    <Description>Before</Description>\n  </PropertyGroup>\n");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task reached = nuget.Packages.HoldNextChange(release.Task);
        Task<PackageOutcome> change = nuget.Packages.InstallAsync(Named(window, "Lib"), "Passenger.Missing", "1.0.0");
        try
        {
            // Pumped, not settled: the held change keeps the window's work from landing.
            Ui.PumpUntil(() => reached.IsCompleted, "Lib's install to be under way");
            Assert.Null(window.OpenFile(lib));
            Ui.PumpUntil(() => window.EditorArea.Active?.Path == Passenger.Core.PathNames.Canonical(lib), "Lib.csproj open in the editor");
            Passenger.Editor.CodeEditor editor = window.EditorArea.CodeEditor;
            editor.Focus();
            editor.Select(new Passenger.Text.TextRange(editor.Document.Snapshot.ToString().IndexOf("Before", StringComparison.Ordinal), "Before".Length));
            Avalonia.Headless.HeadlessWindowExtensions.KeyTextInput(window, "Kept");
            Ui.Run(window, AppActions.SaveAll, settle: false);
            Ui.PumpUntil(() => File.ReadAllText(lib).Contains("<Description>Kept</Description>", StringComparison.Ordinal), "the edit to Lib.csproj saved");
        }
        finally
        {
            release.TrySetResult();
        }
        PackageOutcome outcome = Ui.Landed(change, "Lib's install of a package no feed holds");
        Assert.True(!outcome.Done && File.ReadAllText(lib).Contains("<Description>Kept</Description>", StringComparison.Ordinal),
            $"The install of a package no feed holds {(outcome.Done ? "was done" : "failed")}, leaving Lib.csproj as:\n{File.ReadAllText(lib)}\n{nuget.Log}");
        Assert.True(outcome.Failure("failed")?.EndsWith("Lib.csproj saved meanwhile, left as saved", StringComparison.Ordinal) == true,
            $"The failed install said \"{outcome.Failure("failed")}\", not that the project saved meanwhile was left as saved");
        Assert.True(window.EditorArea.Active?.Text.Snapshot.ToString().Contains("<Description>Kept</Description>", StringComparison.Ordinal) == true,
            "The open project no longer shows the edit saved while the change ran");
        window.Close();
    }

    /// <summary>Another folder opened in the window while the NuGet window is shown has that folder's packages listed in
    /// place of the last folder's, whose rows never stay to be acted on against the new folder's projects.</summary>
    [AvaloniaFact]
    public void Another_folder_opened_with_the_nuget_window_shown_lists_that_folders_packages()
    {
        const string Other = "Passenger.Other";
        feed.Add(Other, "2.0.0");
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"1.0.1\" />\n  </ItemGroup>\n");
        string second = TestFolders.Create("nuget-other-");
        try
        {
            File.Copy(Path.Combine(folder, "nuget.config"), Path.Combine(second, "nuget.config"));
            Directory.CreateDirectory(Path.Combine(second, "Tool"));
            File.WriteAllText(Path.Combine(second, "Tool", "Tool.csproj"), $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>{TestProjects.Framework}</TargetFramework>\n"
                + $"  </PropertyGroup>\n  <ItemGroup>\n    <PackageReference Include=\"{Other}\" Version=\"2.0.0\" />\n  </ItemGroup>\n</Project>\n");
            MainWindow window = Open();
            NuGetView nuget = window.NuGet;
            Ui.Landed(() => nuget.Rows is [{ Id: Id }], $"{Id}, the first folder's package, listed");

            window.OpenFolder(second);
            Ui.Settle();
            Assert.True(window.NuGet == nuget && nuget.Rows is [{ Id: Other }] && nuget.Status == "1 package installed",
                $"With the other folder open the NuGet window lists [{string.Join(", ", nuget.Rows.Select(r => r.Id))}] saying \"{nuget.Status}\", not {Other}, its own package");
            Assert.True(ShownVersion(nuget, "Tool") == "2.0.0" && Line(nuget, "Lib") is null,
                $"{Other}'s projects are listed as Tool at {ShownVersion(nuget, "Tool") ?? "nothing"}{(Line(nuget, "Lib") is null ? string.Empty : " and the last folder's Lib")}");
            window.Close();
        }
        finally
        {
            TestFolders.Delete(second);
        }
    }

    /// <summary>A change refused because an open file would not save (Windows refuses a write to a file another program
    /// holds, stood in for here) says why in the window's status, never "Working…" left unexplained, and leaves the
    /// projects' actions enabled and the project as it was.</summary>
    [AvaloniaFact]
    public void A_change_refused_as_an_open_file_would_not_save_says_why_and_leaves_the_actions_enabled()
    {
        const string Unsaved = "Program.cs could not be saved: The process cannot access the file because it is being used by another process.";
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Search(nuget);
        string before = File.ReadAllText(lib);
        Func<Passenger.App.Working?, CancellationToken, Task<string?>>? saves = nuget.Packages.SaveFirst;
        nuget.Packages.SaveFirst = (_, _) => Task.FromResult<string?>(Unsaved);

        Ui.Click(Action(nuget, "Lib", "Install") ?? throw new Xunit.Sdk.XunitException("The NuGet window offers no Install on Lib's line"), settle: false);
        Ui.Landed(() => !nuget.IsBusy, "the refused install's end");
        Assert.True(nuget.Status == Unsaved, $"The install refused as a file would not save says \"{nuget.Status}\", not why");
        Assert.True(Action(nuget, "Lib", "Install") is { IsEnabled: true }, "Lib's Install is left disabled after the refused install");
        Assert.Equal(before, File.ReadAllText(lib));
        nuget.Packages.SaveFirst = saves;
        window.Close();
    }

    /// <summary>A change that fails part way (moves together, as Migrations' alignment makes them, one to a package no feed
    /// holds, so their restore fails) puts Lib back, and the window's status keeps saying so once its refresh has listed the
    /// packages again, until the person asks for a list (Refresh); the status bar says it too, a click opening the window.</summary>
    [AvaloniaFact]
    public void A_failed_changes_words_stay_in_the_status_after_the_refresh_until_the_person_asks_for_a_list()
    {
        const string Failed = "The change failed, Lib.csproj put back as before; the log says why";
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"1.0.0\" />\n  </ItemGroup>\n");
        byte[] before = File.ReadAllBytes(lib);
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        Ui.Landed(() => nuget.Selected?.Id == Id && nuget.Status == "1 package installed, 1 vulnerable", $"{Id} listed as installed and vulnerable: {nuget.Status}");
        IReadOnlyList<PackageRow> listed = nuget.Rows;

        PackageOutcome outcome = Ui.Landed(nuget.Packages.InstallSetAsync(Named(window, "Lib"), [(Id, "1.0.1"), ("Passenger.Missing", "1.0.0")]), "the moves whose restore fails");
        Assert.True(!outcome.Done && File.ReadAllBytes(lib).AsSpan().SequenceEqual(before),
            $"The moves to a package no feed holds {(outcome.Done ? "were done" : "failed")}, leaving Lib.csproj as:\n{File.ReadAllText(lib)}\n{nuget.Log}");
        Ui.Landed(() => !ReferenceEquals(nuget.Rows, listed) && nuget.Rows is [{ Id: Id }], $"the window's refresh after the failed change to list {Id} again");
        Assert.True(nuget.Status == Failed, $"Once the window's refresh had landed its status said \"{nuget.Status}\", not that the change failed and Lib was put back");
        Assert.True(window.Status == $"Move {Id} to 1.0.1, Passenger.Missing to 1.0.0 in Lib failed, Lib.csproj put back as before; the NuGet window's log says why"
            && window.StatusPlace?.Name == "open the NuGet window", $"The status bar says \"{window.Status}\", a click to {window.StatusPlace?.Name ?? "nowhere"}");

        window.Perform(AppActions.NuGetRefresh);
        Ui.Landed(() => nuget.Status == "1 package installed, 1 vulnerable", $"the list's own words once the person asked for a list (it says \"{nuget.Status}\")");
        window.Close();
    }

    /// <summary>A feed whose VulnerabilityInfo lists a page of advisories it answers 404 for has its audit read as failed,
    /// never as no advisories: the window's status says the vulnerabilities are unknown, naming the feed, rather than count
    /// the vulnerable 1.0.0 as safe.</summary>
    [AvaloniaFact]
    public void A_vulnerability_page_the_feed_lists_but_answers_404_for_is_said_as_an_audit_not_read()
    {
        feed.AdvisoriesGone = true;
        Project("Lib", TestProjects.Framework, $"  <ItemGroup>\n    <PackageReference Include=\"{Id}\" Version=\"1.0.0\" />\n  </ItemGroup>\n");
        MainWindow window = Open();
        NuGetView nuget = window.NuGet;
        const string Said = "1 package installed; vulnerabilities unknown: stand-in did not answer";
        Ui.Landed(() => nuget.Selected?.Id == Id, $"{Id} listed as installed");
        Assert.True(nuget.Status == Said, $"With the feed's page of advisories not there, the status says \"{nuget.Status}\", not that the vulnerabilities are unknown");
        Assert.True(feed.Requests.Any(r => r.Path.EndsWith("/vulnerabilities/base.json", StringComparison.Ordinal)), "The audit never asked for the page the feed lists");
        Assert.False(nuget.Selected!.IsVulnerable, "A package was marked from advisories that were never read");
        window.Close();
    }

    /// <summary>A project of the folder's own for its frameworks, with the item groups given.</summary>
    private string Project(string name, string frameworks, string items)
    {
        Directory.CreateDirectory(Path.Combine(folder, name));
        string path = Path.Combine(folder, name, $"{name}.csproj");
        string property = frameworks.Contains(';', StringComparison.Ordinal) ? "TargetFrameworks" : "TargetFramework";
        File.WriteAllText(path, $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <{property}>{frameworks}</{property}>\n  </PropertyGroup>\n{items}</Project>\n");
        return path;
    }

    /// <summary>A window on the folder with the NuGet window shown; without the C# language server, which nothing here asks
    /// of, so none starts and restores the projects.</summary>
    private MainWindow Open()
    {
        MainWindow window = Ui.OpenFolder(folder, languageServer: false);
        window.Perform(AppActions.ShowNuGet);
        Ui.Settle();
        return window;
    }

    /// <summary>The stand-in's package found and chosen, its versions read.</summary>
    private static void Search(NuGetView nuget)
    {
        nuget.Query = "vulnerable";
        Task searched = nuget.RefreshAsync();
        Ui.Landed(() => searched.IsCompleted && nuget.Selected?.Id == Id && nuget.Versions.Count == 3, $"{Id} and its versions: {nuget.Status}");
    }

    /// <summary>A version chosen in the package's version picker, as a person picks it.</summary>
    private static void Choose(NuGetView nuget, string version)
    {
        ComboBox picker = nuget.GetLogicalDescendants().OfType<ComboBox>().Single(c => c.Name == "Versions");
        picker.SelectedItem = version;
        Ui.Settle();
    }

    private static ProjectModel Named(MainWindow window, string name) => window.Projects.Single(p => p.Name == name);

    /// <summary>A project's line among the chosen package's projects.</summary>
    private static Panel? Line(NuGetView nuget, string project) => nuget.GetLogicalDescendants().OfType<Panel>()
        .FirstOrDefault(line => line.Children.FirstOrDefault() is TextBlock { Text: { } text } && text == project);

    /// <summary>The button of a label on a project's line.</summary>
    private static Button? Action(NuGetView nuget, string project, string label) =>
        Line(nuget, project)?.Children.OfType<Button>().FirstOrDefault(button => (string?)button.Content == label);

    /// <summary>The words on a project's line's buttons.</summary>
    private static IEnumerable<string> Labels(NuGetView nuget, string project) =>
        Line(nuget, project)?.Children.OfType<Button>().Select(b => b.Content).OfType<string>() ?? [];

    /// <summary>The version a project's line shows for the chosen package: the one in force there, or nothing.</summary>
    private static string? ShownVersion(NuGetView nuget, string project) => Line(nuget, project)?.Children.OfType<TextBlock>().Skip(1).FirstOrDefault()?.Text;
}
